/**
 * ABILITY_RESOLUTION_ENGINE
 * Purpose: Drains deterministic trigger work into validated ability conditions, usage limits, sequential effects, state transitions, player-choice continuations, and newly discovered reactions.
 * Connections: Coordinates AbilityRuntimeCatalog, GameplayRuntimeEvaluator, EffectSequenceEngine, MatchStateSnapshot, TriggerDiscovery, and DeterministicTriggerQueue.
 * Risk: High because this loop is the authoritative chain-resolution boundary and must prevent recursion, stale continuations, partial player-choice commits, and infinite trigger cycles.
 */
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using HyuHeroes.Gameplay.Abilities;
using HyuHeroes.Gameplay.Core;
using HyuHeroes.Gameplay.Events;
using HyuHeroes.Gameplay.Modifiers;
using HyuHeroes.Gameplay.Runtime;
using HyuHeroes.Gameplay.Schema;
using HyuHeroes.Gameplay.Triggers;

namespace HyuHeroes.Gameplay.Simulation;

public sealed class PendingAbilityChoice
{
    public PendingAbilityChoice(
        TriggerQueueItem queueItem,
        AbilityDefinition ability,
        PendingEffectSequenceChoice effectChoice,
        MatchStateSnapshot checkpointState,
        int processedTriggers,
        IEnumerable<EffectChoiceSelection>? resolvedChoices = null)
    {
        QueueItem = queueItem ?? throw new ArgumentNullException(nameof(queueItem));
        Ability = ability ?? throw new ArgumentNullException(nameof(ability));
        EffectChoice = effectChoice ?? throw new ArgumentNullException(nameof(effectChoice));
        if (checkpointState is null) throw new ArgumentNullException(nameof(checkpointState));
        if (processedTriggers < 0) throw new ArgumentOutOfRangeException(nameof(processedTriggers));

        var choices = (resolvedChoices ?? Array.Empty<EffectChoiceSelection>()).ToArray();
        if (choices.Any(choice => choice is null))
        {
            throw new ArgumentException("Resolved choices cannot contain null values.", nameof(resolvedChoices));
        }

        ResolvedChoices = new ReadOnlyCollection<EffectChoiceSelection>(choices);
        ProcessedTriggers = processedTriggers;
        ExpectedTurnNumber = checkpointState.TurnNumber;
        ExpectedPhaseId = checkpointState.PhaseId;
        ExpectedNextEventSequence = checkpointState.NextEventSequence;
        ExpectedRandomState = checkpointState.RandomState;
    }

    public TriggerQueueItem QueueItem { get; }
    public AbilityDefinition Ability { get; }
    public PendingEffectSequenceChoice EffectChoice { get; }
    public IReadOnlyList<EffectChoiceSelection> ResolvedChoices { get; }
    public int ProcessedTriggers { get; }
    public int ExpectedTurnNumber { get; }
    public StableId ExpectedPhaseId { get; }
    public long ExpectedNextEventSequence { get; }
    public ulong ExpectedRandomState { get; }
}

public sealed class AbilityResolutionResult
{
    public AbilityResolutionResult(
        MatchStateSnapshot state,
        IEnumerable<DomainEvent> events,
        int processedTriggers,
        PendingAbilityChoice? pendingChoice = null)
    {
        State = state ?? throw new ArgumentNullException(nameof(state));
        Events = new ReadOnlyCollection<DomainEvent>((events ?? throw new ArgumentNullException(nameof(events))).ToArray());
        ProcessedTriggers = processedTriggers;
        PendingChoice = pendingChoice;
    }

    public MatchStateSnapshot State { get; }
    public IReadOnlyList<DomainEvent> Events { get; }
    public int ProcessedTriggers { get; }
    public PendingAbilityChoice? PendingChoice { get; }
    public bool RequiresPlayerChoice => PendingChoice is not null;
}

public sealed class ResolutionBudgetExceededException : InvalidOperationException
{
    public ResolutionBudgetExceededException(int budget)
        : base($"Ability trigger resolution exceeded the configured step budget of {budget}.")
    {
        Budget = budget;
    }

    public int Budget { get; }
}

public sealed class AbilityResolutionEngine
{
    private const int DefaultTriggerStepBudget = 256;
    private readonly AbilityRuntimeCatalog _catalog;
    private readonly EffectSequenceEngine _effects;
    private readonly int _triggerStepBudget;
    private readonly TriggerDiscovery _triggerDiscovery;

    public AbilityResolutionEngine(
        AbilityRuntimeCatalog catalog,
        TriggerDiscovery triggerDiscovery,
        EffectSequenceEngine? effects = null,
        int triggerStepBudget = DefaultTriggerStepBudget)
    {
        if (triggerStepBudget <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(triggerStepBudget), "Trigger step budget must be positive.");
        }

        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _triggerDiscovery = triggerDiscovery ?? throw new ArgumentNullException(nameof(triggerDiscovery));
        _effects = effects ?? new EffectSequenceEngine();
        _triggerStepBudget = triggerStepBudget;
    }

    public AbilityResolutionResult Resolve(MatchStateSnapshot state, DeterministicTriggerQueue queue)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        if (queue is null) throw new ArgumentNullException(nameof(queue));
        return DrainQueue(state, queue, Array.Empty<DomainEvent>(), 0);
    }

    public AbilityResolutionResult ResolveChoice(
        MatchStateSnapshot state,
        DeterministicTriggerQueue queue,
        PendingAbilityChoice pendingChoice,
        IEnumerable<StableId> selectedTargetIds)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        if (queue is null) throw new ArgumentNullException(nameof(queue));
        if (pendingChoice is null) throw new ArgumentNullException(nameof(pendingChoice));
        if (selectedTargetIds is null) throw new ArgumentNullException(nameof(selectedTargetIds));

        ValidateCheckpoint(state, pendingChoice);
        ValidateBinding(pendingChoice.QueueItem, pendingChoice.Ability);
        if (!CanResolveAbility(state, pendingChoice.QueueItem, pendingChoice.Ability))
        {
            throw new InvalidOperationException("Pending ability is no longer eligible at its continuation checkpoint.");
        }

        var resolvedChoice = new EffectChoiceSelection(
            pendingChoice.EffectChoice.EffectPath,
            pendingChoice.EffectChoice.Choice.ParameterName,
            selectedTargetIds);
        var choices = pendingChoice.ResolvedChoices.Concat(new[] { resolvedChoice }).ToArray();
        var speculative = ExecuteAbility(state, pendingChoice.QueueItem, pendingChoice.Ability, choices);
        if (speculative.PendingChoice is not null)
        {
            var pending = CreatePending(
                pendingChoice.QueueItem,
                pendingChoice.Ability,
                speculative.PendingChoice,
                state,
                pendingChoice.ProcessedTriggers,
                choices);
            return new AbilityResolutionResult(state, Array.Empty<DomainEvent>(), pendingChoice.ProcessedTriggers, pending);
        }

        var committedState = RecordSuccessfulResolution(speculative.State, pendingChoice.QueueItem, pendingChoice.Ability);
        _triggerDiscovery.DiscoverInto(speculative.Events, queue);
        return DrainQueue(committedState, queue, speculative.Events, pendingChoice.ProcessedTriggers);
    }

    private AbilityResolutionResult DrainQueue(
        MatchStateSnapshot state,
        DeterministicTriggerQueue queue,
        IEnumerable<DomainEvent> initialEvents,
        int processedTriggers)
    {
        var currentState = state;
        var emittedEvents = new List<DomainEvent>(initialEvents);
        while (queue.Count > 0)
        {
            RequireTriggerBudget(processedTriggers);
            var item = queue.Dequeue();
            processedTriggers++;
            var ability = _catalog.GetRequired(item.Binding.AbilityId);
            ValidateBinding(item, ability);
            if (!CanResolveAbility(currentState, item, ability))
            {
                continue;
            }

            var speculative = ExecuteAbility(currentState, item, ability, Array.Empty<EffectChoiceSelection>());
            if (speculative.PendingChoice is not null)
            {
                var pending = CreatePending(item, ability, speculative.PendingChoice, currentState, processedTriggers);
                return new AbilityResolutionResult(currentState, emittedEvents, processedTriggers, pending);
            }

            currentState = RecordSuccessfulResolution(speculative.State, item, ability);
            emittedEvents.AddRange(speculative.Events);
            _triggerDiscovery.DiscoverInto(speculative.Events, queue);
        }

        return new AbilityResolutionResult(currentState, emittedEvents, processedTriggers);
    }

    private EffectSequenceResult ExecuteAbility(
        MatchStateSnapshot state,
        TriggerQueueItem item,
        AbilityDefinition ability,
        IEnumerable<EffectChoiceSelection> resolvedChoices)
    {
        var activeTargetId = item.OriginatingEvent.TargetId ?? item.Binding.SourceId;
        return _effects.Execute(
            state,
            item.Binding.SourceId,
            ability.Effects,
            activeTargetId,
            resolvedChoices);
    }

    private static MatchStateSnapshot RecordSuccessfulResolution(
        MatchStateSnapshot state,
        TriggerQueueItem item,
        AbilityDefinition ability)
    {
        var source = state.GetRequiredTarget(item.Binding.SourceId);
        return state.With(
            abilityUsage: AbilityUsageRules.RecordResolution(
                state.AbilityUsage,
                item.Binding.SourceId,
                ability.Header.Id,
                state.TurnNumber,
                source.ZoneResidencyEpoch));
    }

    private static PendingAbilityChoice CreatePending(
        TriggerQueueItem item,
        AbilityDefinition ability,
        PendingEffectSequenceChoice effectChoice,
        MatchStateSnapshot checkpointState,
        int processedTriggers,
        IEnumerable<EffectChoiceSelection>? resolvedChoices = null) =>
        new(item, ability, effectChoice, checkpointState, processedTriggers, resolvedChoices);

    private void RequireTriggerBudget(int processedTriggers)
    {
        if (processedTriggers >= _triggerStepBudget)
        {
            throw new ResolutionBudgetExceededException(_triggerStepBudget);
        }
    }

    private static void ValidateCheckpoint(MatchStateSnapshot state, PendingAbilityChoice pendingChoice)
    {
        var matches = state.TurnNumber == pendingChoice.ExpectedTurnNumber
            && state.PhaseId == pendingChoice.ExpectedPhaseId
            && state.NextEventSequence == pendingChoice.ExpectedNextEventSequence
            && state.RandomState == pendingChoice.ExpectedRandomState;
        if (!matches)
        {
            throw new InvalidOperationException("Pending choice continuation checkpoint does not match the authoritative match snapshot.");
        }
    }

    private static void ValidateBinding(TriggerQueueItem item, AbilityDefinition ability)
    {
        if (ability.Header.Id != item.Binding.AbilityId)
        {
            throw new InvalidOperationException("Trigger binding ability ID does not match the runtime ability definition.");
        }

        if (ability.Trigger.TypeId != item.Binding.TriggerId)
        {
            throw new InvalidOperationException(
                $"Ability '{ability.Header.Id}' is bound to '{item.Binding.TriggerId}' but declares trigger '{ability.Trigger.TypeId}'.");
        }
    }

    private static bool CanResolveAbility(
        MatchStateSnapshot state,
        TriggerQueueItem item,
        AbilityDefinition ability)
    {
        var evaluator = new GameplayRuntimeEvaluator(statPipeline: new StatModifierPipeline(state.StatModifiers));
        var runtime = CreateRuntimeContext(state, item);
        if (ability.Condition is not null && !evaluator.EvaluateCondition(ability.Condition, runtime))
        {
            return false;
        }

        var source = state.GetRequiredTarget(item.Binding.SourceId);
        var usage = AbilityUsageRules.Find(state.AbilityUsage, item.Binding.SourceId, ability.Header.Id);
        return AbilityUsageRules.CanResolve(
            ability.UsageLimit,
            usage,
            state.TurnNumber,
            source.ZoneResidencyEpoch);
    }

    private static GameplayRuntimeContext CreateRuntimeContext(MatchStateSnapshot state, TriggerQueueItem item)
    {
        var source = state.GetRequiredTarget(item.Binding.SourceId);
        var ownerId = source.EffectiveOwnerId
            ?? throw new InvalidOperationException($"Ability source '{source.RuntimeId}' has no effective owner.");
        var opponentId = state.Targets
            .Where(target => target.Kind == RuntimeTargetKind.Player && target.RuntimeId != ownerId)
            .Select(target => target.RuntimeId)
            .Single();
        return new GameplayRuntimeContext(
            state.Targets,
            source.RuntimeId,
            ownerId,
            opponentId,
            state.TurnNumber,
            state.PhaseId,
            state.LaneCount,
            source.LaneIndex,
            item.OriginatingEvent.TargetId ?? source.RuntimeId);
    }
}
