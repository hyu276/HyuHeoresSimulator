/**
 * STATE_TRANSITION_ENGINE
 * Purpose: Applies resolved effect operations to immutable match snapshots and emits ordered domain events plus state-based death transitions.
 * Connections: Consumes EffectHandlerRegistry operations and DamagePipeline results, then feeds TriggerDiscovery and future replay/presentation adapters.
 * Risk: High because this is the first authoritative mutation boundary and therefore owns narrow, deterministic state changes.
 */
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using HyuHeroes.Gameplay.Combat;
using HyuHeroes.Gameplay.Core;
using HyuHeroes.Gameplay.Effects;
using HyuHeroes.Gameplay.Events;
using HyuHeroes.Gameplay.Modifiers;
using HyuHeroes.Gameplay.Runtime;
using HyuHeroes.Gameplay.Selectors;

namespace HyuHeroes.Gameplay.Simulation;

public sealed class MatchStateSnapshot
{
    public MatchStateSnapshot(
        IEnumerable<RuntimeTarget> targets,
        int turnNumber,
        StableId phaseId,
        int laneCount,
        IEnumerable<StatModifier>? statModifiers = null,
        IEnumerable<DamageAdjustment>? damageAdjustments = null,
        IEnumerable<DamagePrevention>? damagePreventions = null,
        long nextEventSequence = 1)
    {
        if (turnNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(turnNumber), "Turn number must be positive.");
        }

        if (phaseId == default)
        {
            throw new ArgumentException("Phase ID must be a non-default StableId.", nameof(phaseId));
        }

        if (laneCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(laneCount), "Lane count must be positive.");
        }

        if (nextEventSequence <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(nextEventSequence), "Next event sequence must be positive.");
        }

        Targets = CopyUnique(targets, target => target.RuntimeId, nameof(targets));
        StatModifiers = CopyUnique(statModifiers, modifier => modifier.InstanceId, nameof(statModifiers));
        DamageAdjustments = CopyUnique(damageAdjustments, adjustment => adjustment.InstanceId, nameof(damageAdjustments));
        DamagePreventions = CopyUnique(damagePreventions, prevention => prevention.InstanceId, nameof(damagePreventions));
        TurnNumber = turnNumber;
        PhaseId = phaseId;
        LaneCount = laneCount;
        NextEventSequence = nextEventSequence;
    }

    public IReadOnlyList<RuntimeTarget> Targets { get; }
    public IReadOnlyList<StatModifier> StatModifiers { get; }
    public IReadOnlyList<DamageAdjustment> DamageAdjustments { get; }
    public IReadOnlyList<DamagePrevention> DamagePreventions { get; }
    public int TurnNumber { get; }
    public StableId PhaseId { get; }
    public int LaneCount { get; }
    public long NextEventSequence { get; }

    public RuntimeTarget GetRequiredTarget(StableId runtimeId) =>
        Targets.FirstOrDefault(target => target.RuntimeId == runtimeId)
        ?? throw new KeyNotFoundException($"Unknown match-state target '{runtimeId}'.");

    public MatchStateSnapshot With(
        IEnumerable<RuntimeTarget>? targets = null,
        IEnumerable<StatModifier>? statModifiers = null,
        IEnumerable<DamagePrevention>? damagePreventions = null,
        long? nextEventSequence = null) =>
        new(
            targets ?? Targets,
            TurnNumber,
            PhaseId,
            LaneCount,
            statModifiers ?? StatModifiers,
            DamageAdjustments,
            damagePreventions ?? DamagePreventions,
            nextEventSequence ?? NextEventSequence);

    private static IReadOnlyList<TItem> CopyUnique<TItem>(
        IEnumerable<TItem>? values,
        Func<TItem, StableId> idSelector,
        string parameterName)
        where TItem : class
    {
        var items = (values ?? Array.Empty<TItem>()).ToArray();
        if (items.Any(item => item is null))
        {
            throw new ArgumentException("Match-state collections cannot contain null values.", parameterName);
        }

        if (items.GroupBy(idSelector).Any(group => group.Count() > 1))
        {
            throw new ArgumentException("Match-state runtime IDs must be unique within each collection.", parameterName);
        }

        return new ReadOnlyCollection<TItem>(items);
    }
}

public sealed class StateTransitionResult
{
    public StateTransitionResult(MatchStateSnapshot state, IEnumerable<DomainEvent> events)
    {
        State = state ?? throw new ArgumentNullException(nameof(state));
        Events = new ReadOnlyCollection<DomainEvent>((events ?? throw new ArgumentNullException(nameof(events))).ToArray());
    }

    public MatchStateSnapshot State { get; }
    public IReadOnlyList<DomainEvent> Events { get; }
}

public sealed class StateTransitionEngine
{
    private static readonly StableId MaxHealthStatId = StableId.Parse("stat.max_health");
    private static readonly StableId PermanentDurationId = StableId.Parse("duration.permanent");
    private readonly IReadOnlyDictionary<ResolvedEffectOperationKind, Func<MatchStateSnapshot, ResolvedEffectOperation, StateTransitionResult>> _handlers;

    public StateTransitionEngine()
    {
        _handlers = new Dictionary<ResolvedEffectOperationKind, Func<MatchStateSnapshot, ResolvedEffectOperation, StateTransitionResult>>
        {
            [ResolvedEffectOperationKind.DamageRequest] = ApplyDamage,
            [ResolvedEffectOperationKind.Heal] = ApplyHeal,
            [ResolvedEffectOperationKind.AddStatModifier] = ApplyStatModifier,
            [ResolvedEffectOperationKind.SetStat] = ApplySetStat,
            [ResolvedEffectOperationKind.ChangeResource] = ApplyResourceChange,
            [ResolvedEffectOperationKind.Draw] = RejectUnsupportedDraw
        };
    }

    public StateTransitionResult Apply(MatchStateSnapshot state, ResolvedEffectOperation operation)
    {
        if (state is null)
        {
            throw new ArgumentNullException(nameof(state));
        }

        if (operation is null)
        {
            throw new ArgumentNullException(nameof(operation));
        }

        if (!_handlers.TryGetValue(operation.Kind, out var handler))
        {
            throw new NotSupportedException($"No state transition handler exists for operation '{operation.Kind}'.");
        }

        return ApplyStateBasedDeaths(handler(state, operation));
    }

    private static StateTransitionResult RejectUnsupportedDraw(MatchStateSnapshot state, ResolvedEffectOperation operation) =>
        throw new NotSupportedException("DRAW requires an ordered deck/hand state model and is intentionally not mutated by the current reducer.");

    private StateTransitionResult ApplyDamage(MatchStateSnapshot state, ResolvedEffectOperation operation)
    {
        var evaluator = CreateEvaluator(state);
        var runtime = CreateRuntimeContext(state, operation);
        var resolution = new DamagePipeline(evaluator, state.DamageAdjustments, state.DamagePreventions).Resolve(operation, runtime);
        var target = state.GetRequiredTarget(operation.TargetId);
        var updatedTarget = CloneWithHealth(target, resolution.ResultingHealth);
        var updatedState = state.With(
            targets: ReplaceTarget(state.Targets, updatedTarget),
            damagePreventions: ConsumePreventions(state.DamagePreventions, resolution),
            nextEventSequence: state.NextEventSequence + 1);
        var domainEvent = new DamageAppliedDomainEvent(state.NextEventSequence, operation.SourceId, operation.TargetId, resolution);
        return new StateTransitionResult(updatedState, new DomainEvent[] { domainEvent });
    }

    private StateTransitionResult ApplyHeal(MatchStateSnapshot state, ResolvedEffectOperation operation)
    {
        var evaluator = CreateEvaluator(state);
        var target = state.GetRequiredTarget(operation.TargetId);
        if (!target.TryGetStat(MaxHealthStatId, out _))
        {
            throw new InvalidOperationException($"Heal target '{target.RuntimeId}' does not expose maximum health.");
        }

        var maximumHealth = evaluator.ResolveStat(target, MaxHealthStatId);
        var previousHealth = Math.Min(target.CurrentHealth ?? maximumHealth, maximumHealth);
        var requested = Math.Max(0m, operation.Amount);
        var resultingHealth = Math.Min(maximumHealth, previousHealth + requested);
        var eventRecord = new HealedDomainEvent(
            state.NextEventSequence,
            operation.SourceId,
            operation.TargetId,
            operation.Amount,
            resultingHealth - previousHealth,
            resultingHealth);
        var updatedState = state.With(
            targets: ReplaceTarget(state.Targets, CloneWithHealth(target, resultingHealth)),
            nextEventSequence: state.NextEventSequence + 1);
        return new StateTransitionResult(updatedState, new DomainEvent[] { eventRecord });
    }

    private StateTransitionResult ApplyResourceChange(MatchStateSnapshot state, ResolvedEffectOperation operation)
    {
        var resourceId = RequireReference(operation.PrimaryReferenceId, "resource");
        var target = state.GetRequiredTarget(operation.TargetId);
        var previousValue = target.GetRequiredResource(resourceId);
        var resultingValue = Math.Max(0m, previousValue + operation.Amount);
        var eventRecord = new ResourceChangedDomainEvent(
            state.NextEventSequence,
            operation.SourceId,
            operation.TargetId,
            resourceId,
            operation.Amount,
            resultingValue);
        var updatedState = state.With(
            targets: ReplaceTarget(state.Targets, CloneWithResource(target, resourceId, resultingValue)),
            nextEventSequence: state.NextEventSequence + 1);
        return new StateTransitionResult(updatedState, new DomainEvent[] { eventRecord });
    }

    private StateTransitionResult ApplySetStat(MatchStateSnapshot state, ResolvedEffectOperation operation)
    {
        var statId = RequireReference(operation.PrimaryReferenceId, "stat");
        var target = state.GetRequiredTarget(operation.TargetId);
        var previousValue = target.GetRequiredStat(statId);
        var updatedTarget = CloneWithStat(target, statId, operation.Amount);
        var eventRecord = new StatSetDomainEvent(
            state.NextEventSequence,
            operation.SourceId,
            operation.TargetId,
            statId,
            previousValue,
            operation.Amount);
        var updatedState = state.With(
            targets: ReplaceTarget(state.Targets, updatedTarget),
            nextEventSequence: state.NextEventSequence + 1);
        return new StateTransitionResult(updatedState, new DomainEvent[] { eventRecord });
    }

    private StateTransitionResult ApplyStatModifier(MatchStateSnapshot state, ResolvedEffectOperation operation)
    {
        var statId = RequireReference(operation.PrimaryReferenceId, "stat");
        var durationId = RequireReference(operation.SecondaryReferenceId, "duration");
        var target = state.GetRequiredTarget(operation.TargetId);
        target.GetRequiredStat(statId);
        var modifierId = StableId.Parse(
            "modifier.runtime_" + state.NextEventSequence.ToString("D8", CultureInfo.InvariantCulture));
        var modifier = new StatModifier(
            modifierId,
            operation.SourceId,
            operation.TargetId,
            statId,
            ParseModifierOperation(operation.Qualifier),
            operation.Amount,
            durationId == PermanentDurationId ? StatModifierLayer.PermanentMatch : StatModifierLayer.Temporary,
            durationId: durationId);
        var modifiers = state.StatModifiers.Concat(new[] { modifier });
        var eventRecord = new ModifierAddedDomainEvent(
            state.NextEventSequence,
            operation.SourceId,
            operation.TargetId,
            modifierId,
            statId,
            operation.Amount);
        var updatedState = state.With(statModifiers: modifiers, nextEventSequence: state.NextEventSequence + 1);
        return new StateTransitionResult(updatedState, new DomainEvent[] { eventRecord });
    }

    private StateTransitionResult ApplyStateBasedDeaths(StateTransitionResult transition)
    {
        var state = transition.State;
        var events = transition.Events.ToList();
        var lethalTargets = state.Targets
            .Where(IsLethalBoardEntity)
            .OrderBy(target => target.LaneIndex ?? int.MaxValue)
            .ThenBy(target => target.RuntimeId)
            .ToArray();
        foreach (var target in lethalTargets)
        {
            var deadTarget = CloneAsDead(target);
            state = state.With(
                targets: ReplaceTarget(state.Targets, deadTarget),
                nextEventSequence: state.NextEventSequence + 1);
            events.Add(new EntityDiedDomainEvent(state.NextEventSequence - 1, target.RuntimeId));
        }

        return new StateTransitionResult(state, events);
    }

    private static bool IsLethalBoardEntity(RuntimeTarget target) =>
        target.Zone == TargetZone.Board &&
        (target.Kind == RuntimeTargetKind.Unit || target.Kind == RuntimeTargetKind.Hero) &&
        target.CurrentHealth is <= 0m;

    private static GameplayRuntimeEvaluator CreateEvaluator(MatchStateSnapshot state) =>
        new(statPipeline: new StatModifierPipeline(state.StatModifiers));

    private static GameplayRuntimeContext CreateRuntimeContext(MatchStateSnapshot state, ResolvedEffectOperation operation)
    {
        var source = state.GetRequiredTarget(operation.SourceId);
        var ownerId = source.EffectiveOwnerId
            ?? throw new InvalidOperationException($"Operation source '{source.RuntimeId}' has no effective owner.");
        var opponentId = state.Targets
            .Where(target => target.Kind == RuntimeTargetKind.Player && target.RuntimeId != ownerId)
            .Select(target => target.RuntimeId)
            .Single();
        return new GameplayRuntimeContext(
            state.Targets,
            operation.SourceId,
            ownerId,
            opponentId,
            state.TurnNumber,
            state.PhaseId,
            state.LaneCount,
            source.LaneIndex,
            operation.TargetId);
    }

    private static IReadOnlyList<RuntimeTarget> ReplaceTarget(
        IReadOnlyList<RuntimeTarget> targets,
        RuntimeTarget replacement) =>
        new ReadOnlyCollection<RuntimeTarget>(
            targets.Select(target => target.RuntimeId == replacement.RuntimeId ? replacement : target).ToArray());

    private static IReadOnlyList<DamagePrevention> ConsumePreventions(
        IReadOnlyList<DamagePrevention> preventions,
        DamageResolution resolution)
    {
        var usedById = resolution.AppliedPreventions.ToDictionary(item => item.PreventionId, item => item.PreventedAmount);
        var remaining = preventions.Select(prevention =>
        {
            var used = usedById.TryGetValue(prevention.InstanceId, out var amount) ? amount : 0m;
            return new DamagePrevention(
                prevention.InstanceId,
                prevention.TargetId,
                Math.Max(0m, prevention.Capacity - used),
                prevention.Priority,
                prevention.DamageType);
        }).Where(prevention => prevention.Capacity > 0m).ToArray();
        return new ReadOnlyCollection<DamagePrevention>(remaining);
    }

    private static RuntimeTarget CloneWithHealth(RuntimeTarget target, decimal currentHealth) =>
        Clone(target, target.Zone, target.LaneIndex, target.Stats, target.Resources, currentHealth);

    private static RuntimeTarget CloneWithStat(RuntimeTarget target, StableId statId, decimal value)
    {
        var stats = target.Stats.ToDictionary(pair => pair.Key, pair => pair.Value);
        stats[statId] = value;
        return Clone(target, target.Zone, target.LaneIndex, stats, target.Resources, target.CurrentHealth);
    }

    private static RuntimeTarget CloneWithResource(RuntimeTarget target, StableId resourceId, decimal value)
    {
        var resources = target.Resources.ToDictionary(pair => pair.Key, pair => pair.Value);
        resources[resourceId] = value;
        return Clone(target, target.Zone, target.LaneIndex, target.Stats, resources, target.CurrentHealth);
    }

    private static RuntimeTarget CloneAsDead(RuntimeTarget target) =>
        Clone(target, TargetZone.Graveyard, null, target.Stats, target.Resources, target.CurrentHealth);

    private static RuntimeTarget Clone(
        RuntimeTarget target,
        TargetZone zone,
        int? laneIndex,
        IEnumerable<KeyValuePair<StableId, decimal>> stats,
        IEnumerable<KeyValuePair<StableId, decimal>> resources,
        decimal? currentHealth) =>
        new(
            target.RuntimeId,
            target.Kind,
            target.OwnerId,
            zone,
            laneIndex,
            target.CardType,
            target.Tags,
            target.Keywords,
            stats,
            resources,
            currentHealth);

    private static StableId RequireReference(StableId? referenceId, string kind) =>
        referenceId is { } concreteId
            ? concreteId
            : throw new InvalidOperationException($"Resolved operation is missing its {kind} reference ID.");

    private static StatModifierOperation ParseModifierOperation(string? operation) =>
        operation switch
        {
            "ADD" => StatModifierOperation.Add,
            "MULTIPLY" => StatModifierOperation.Multiply,
            "SET" => StatModifierOperation.Set,
            "MINIMUM" => StatModifierOperation.Minimum,
            "MAXIMUM" => StatModifierOperation.Maximum,
            _ => throw new InvalidOperationException($"Unsupported stat modifier operation '{operation ?? "<null>"}'.")
        };
}
