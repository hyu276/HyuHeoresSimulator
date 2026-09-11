/**
 * LIFECYCLE_TRANSITION_ENGINE
 * Purpose: Owns deterministic turn checkpoints, zone-residency changes, and expiration of persistent modifier lifetimes.
 * Connections: Operates on MatchStateSnapshot and StatModifierDurationState, emits lifecycle DomainEvents, and is invoked after ordinary state transitions for continuous-duration reconciliation.
 * Risk: High because duration expiry timing and residency epochs affect effective stats, usage limits, replay order, and chained gameplay outcomes.
 */
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using HyuHeroes.Gameplay.Core;
using HyuHeroes.Gameplay.Events;
using HyuHeroes.Gameplay.Modifiers;
using HyuHeroes.Gameplay.Registries;
using HyuHeroes.Gameplay.Runtime;
using HyuHeroes.Gameplay.Selectors;

namespace HyuHeroes.Gameplay.Simulation;

public sealed class LifecycleTransitionEngine
{
    private const string EndOfTurnReason = "end_of_turn";
    private const string StartOfTurnReason = "start_of_turn";
    private const string SourceResidencyReason = "source_residency_changed";
    private const string TargetResidencyReason = "target_residency_changed";

    public StateTransitionResult CompleteEndOfTurn(MatchStateSnapshot state)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        if (state.PhaseId != StableId.Parse("phase.end_turn"))
        {
            throw new InvalidOperationException("End-of-turn completion requires phase.end_turn.");
        }

        return ExpireMatching(
            state,
            Array.Empty<DomainEvent>(),
            modifier => modifier.DurationState?.TypeId == DurationIds.UntilEndOfTurn,
            _ => EndOfTurnReason);
    }

    public StateTransitionResult StartNextTurn(MatchStateSnapshot state)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        if (state.PhaseId != StableId.Parse("phase.end_turn"))
        {
            throw new InvalidOperationException("Starting the next turn requires phase.end_turn.");
        }

        var nextTurn = checked(state.TurnNumber + 1);
        var nextPhase = StableId.Parse("phase.start_turn");
        var timingEvent = new TimingAdvancedDomainEvent(
            state.NextEventSequence,
            state.TurnNumber,
            state.PhaseId,
            nextTurn,
            nextPhase);
        var advanced = state.With(
            turnNumber: nextTurn,
            phaseId: nextPhase,
            nextEventSequence: state.NextEventSequence + 1);
        return ExpireMatching(
            advanced,
            new DomainEvent[] { timingEvent },
            modifier => ExpiresAtStartOfTurn(modifier, nextTurn) || ShouldExpireContinuous(modifier, advanced),
            modifier => ExpiryReason(modifier, advanced, StartOfTurnReason));
    }

    public StateTransitionResult ChangeZone(
        MatchStateSnapshot state,
        StableId targetId,
        TargetZone destinationZone,
        int? destinationLaneIndex = null)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        if (targetId == default) throw new ArgumentException("Target ID must be a non-default StableId.", nameof(targetId));
        if (!Enum.IsDefined(typeof(TargetZone), destinationZone))
        {
            throw new ArgumentOutOfRangeException(nameof(destinationZone), destinationZone, "Destination zone is not defined.");
        }

        var target = state.GetRequiredTarget(targetId);
        if (target.Zone == destinationZone)
        {
            throw new InvalidOperationException("ChangeZone requires a different destination zone; same-zone lane movement is a separate gameplay operation.");
        }

        ValidateDestination(state, target, destinationZone, destinationLaneIndex);
        var replacement = CloneInZone(target, destinationZone, destinationLaneIndex);
        var zoneEvent = new ZoneChangedDomainEvent(
            state.NextEventSequence,
            target.RuntimeId,
            target.Zone,
            destinationZone,
            replacement.ZoneResidencyEpoch);
        var updatedState = state.With(
            targets: ReplaceTarget(state.Targets, replacement),
            nextEventSequence: state.NextEventSequence + 1);
        return ReconcileContinuousDurations(updatedState, new DomainEvent[] { zoneEvent });
    }

    public StateTransitionResult ReconcileContinuousDurations(
        MatchStateSnapshot state,
        IEnumerable<DomainEvent>? existingEvents = null)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        return ExpireMatching(
            state,
            existingEvents ?? Array.Empty<DomainEvent>(),
            modifier => ShouldExpireContinuous(modifier, state),
            modifier => ExpiryReason(modifier, state, TargetResidencyReason));
    }

    private static StateTransitionResult ExpireMatching(
        MatchStateSnapshot state,
        IEnumerable<DomainEvent> existingEvents,
        Func<StatModifier, bool> shouldExpire,
        Func<StatModifier, string> reason)
    {
        var events = existingEvents.ToList();
        var expired = state.StatModifiers
            .Where(shouldExpire)
            .OrderBy(modifier => modifier.InstanceId)
            .ToArray();
        var current = state;
        foreach (var modifier in expired)
        {
            current = current.With(
                statModifiers: current.StatModifiers.Where(item => item.InstanceId != modifier.InstanceId),
                nextEventSequence: current.NextEventSequence + 1);
            events.Add(new ModifierExpiredDomainEvent(
                current.NextEventSequence - 1,
                modifier.SourceId,
                modifier.TargetId,
                modifier.InstanceId,
                modifier.DurationId ?? DurationIds.Permanent,
                reason(modifier)));
        }

        return new StateTransitionResult(current, events);
    }

    private static bool ExpiresAtStartOfTurn(StatModifier modifier, int currentTurn)
    {
        var duration = modifier.DurationState;
        if (duration is null)
        {
            return false;
        }

        if (duration.TypeId == DurationIds.UntilEndOfTurn)
        {
            return duration.CreatedTurn < currentTurn;
        }

        if (duration.TypeId == DurationIds.UntilStartOfNextTurn || duration.TypeId == DurationIds.ForNTurns)
        {
            return duration.ExpiresAtTurn is { } expiryTurn && currentTurn >= expiryTurn;
        }

        return false;
    }

    private static bool ShouldExpireContinuous(StatModifier modifier, MatchStateSnapshot state)
    {
        var duration = modifier.DurationState;
        if (duration is null)
        {
            return false;
        }

        if (duration.TypeId == DurationIds.WhileSourceExists)
        {
            var source = FindTarget(state, modifier.SourceId);
            return source is null || source.ZoneResidencyEpoch != duration.SourceResidencyEpoch;
        }

        if (duration.TypeId == DurationIds.WhileInZone)
        {
            var target = FindTarget(state, modifier.TargetId);
            return target is null
                || target.Zone != duration.RequiredTargetZone
                || target.ZoneResidencyEpoch != duration.TargetResidencyEpoch;
        }

        return false;
    }

    private static string ExpiryReason(
        StatModifier modifier,
        MatchStateSnapshot state,
        string fallback)
    {
        var duration = modifier.DurationState;
        if (duration?.TypeId == DurationIds.WhileSourceExists)
        {
            return SourceResidencyReason;
        }

        if (duration?.TypeId == DurationIds.WhileInZone)
        {
            return TargetResidencyReason;
        }

        return fallback;
    }

    private static RuntimeTarget? FindTarget(MatchStateSnapshot state, StableId targetId) =>
        state.Targets.FirstOrDefault(target => target.RuntimeId == targetId);

    private static void ValidateDestination(
        MatchStateSnapshot state,
        RuntimeTarget target,
        TargetZone destinationZone,
        int? destinationLaneIndex)
    {
        if (destinationZone != TargetZone.Board && destinationLaneIndex is not null)
        {
            throw new ArgumentException("Non-board zones cannot carry a lane index.", nameof(destinationLaneIndex));
        }

        if (destinationZone == TargetZone.Board &&
            (target.Kind == RuntimeTargetKind.Unit || target.Kind == RuntimeTargetKind.Hero) &&
            destinationLaneIndex is null)
        {
            throw new ArgumentException("Units and heroes entering the board require a lane index.", nameof(destinationLaneIndex));
        }

        if (destinationLaneIndex is < 0 || destinationLaneIndex >= state.LaneCount)
        {
            throw new ArgumentOutOfRangeException(nameof(destinationLaneIndex), "Destination lane is outside the board.");
        }
    }

    private static RuntimeTarget CloneInZone(
        RuntimeTarget target,
        TargetZone zone,
        int? laneIndex) =>
        new(
            target.RuntimeId,
            target.Kind,
            target.OwnerId,
            zone,
            laneIndex,
            target.CardType,
            target.Tags,
            target.Keywords,
            target.Stats,
            target.Resources,
            target.CurrentHealth,
            checked(target.ZoneResidencyEpoch + 1));

    private static IReadOnlyList<RuntimeTarget> ReplaceTarget(
        IReadOnlyList<RuntimeTarget> targets,
        RuntimeTarget replacement) =>
        new ReadOnlyCollection<RuntimeTarget>(
            targets.Select(target => target.RuntimeId == replacement.RuntimeId ? replacement : target).ToArray());
}
