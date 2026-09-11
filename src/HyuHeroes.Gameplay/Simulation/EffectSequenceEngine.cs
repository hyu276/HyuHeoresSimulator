/**
 * EFFECT_SEQUENCE_ENGINE
 * Purpose: Executes an ability effect list against successive immutable snapshots so later effects observe state produced by earlier effects.
 * Connections: Uses GameplayEffectExecutor for leaf effects, StateTransitionEngine for mutation, and GameplayRuntimeEvaluator for conditional branches.
 * Risk: High because effect sequencing determines authoritative dependency order inside a single ability resolution.
 */
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using HyuHeroes.Gameplay.Authoring;
using HyuHeroes.Gameplay.Core;
using HyuHeroes.Gameplay.Effects;
using HyuHeroes.Gameplay.Events;
using HyuHeroes.Gameplay.Modifiers;
using HyuHeroes.Gameplay.Runtime;

namespace HyuHeroes.Gameplay.Simulation;

public sealed class PendingEffectSequenceChoice
{
    public PendingEffectSequenceChoice(
        EffectDefinition effect,
        IEnumerable<int> effectPath,
        PendingEffectChoice choice)
    {
        Effect = effect ?? throw new ArgumentNullException(nameof(effect));
        EffectPath = new ReadOnlyCollection<int>((effectPath ?? throw new ArgumentNullException(nameof(effectPath))).ToArray());
        Choice = choice ?? throw new ArgumentNullException(nameof(choice));
    }

    public EffectDefinition Effect { get; }
    public IReadOnlyList<int> EffectPath { get; }
    public PendingEffectChoice Choice { get; }
}

public sealed class EffectSequenceResult
{
    public EffectSequenceResult(
        MatchStateSnapshot state,
        IEnumerable<DomainEvent> events,
        PendingEffectSequenceChoice? pendingChoice = null)
    {
        State = state ?? throw new ArgumentNullException(nameof(state));
        Events = new ReadOnlyCollection<DomainEvent>((events ?? throw new ArgumentNullException(nameof(events))).ToArray());
        PendingChoice = pendingChoice;
    }

    public MatchStateSnapshot State { get; }
    public IReadOnlyList<DomainEvent> Events { get; }
    public PendingEffectSequenceChoice? PendingChoice { get; }
    public bool RequiresPlayerChoice => PendingChoice is not null;
}

public sealed class EffectSequenceEngine
{
    private const int DefaultEffectStepBudget = 128;
    private readonly int _effectStepBudget;
    private readonly StateTransitionEngine _transitions;

    public EffectSequenceEngine(StateTransitionEngine? transitions = null, int effectStepBudget = DefaultEffectStepBudget)
    {
        if (effectStepBudget <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(effectStepBudget), "Effect step budget must be positive.");
        }

        _transitions = transitions ?? new StateTransitionEngine();
        _effectStepBudget = effectStepBudget;
    }

    public EffectSequenceResult Execute(
        MatchStateSnapshot state,
        StableId sourceId,
        IEnumerable<EffectDefinition> effects,
        StableId? activeTargetId = null)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        if (sourceId == default) throw new ArgumentException("Source ID must be a non-default StableId.", nameof(sourceId));
        if (effects is null) throw new ArgumentNullException(nameof(effects));

        var steps = 0;
        return ExecuteList(state, sourceId, effects.ToArray(), activeTargetId, Array.Empty<int>(), ref steps);
    }

    private EffectSequenceResult ExecuteList(
        MatchStateSnapshot state,
        StableId sourceId,
        IReadOnlyList<EffectDefinition> effects,
        StableId? activeTargetId,
        IReadOnlyList<int> pathPrefix,
        ref int steps)
    {
        var currentState = state;
        var events = new List<DomainEvent>();
        for (var index = 0; index < effects.Count; index++)
        {
            RequireStepBudget(++steps);
            var effect = effects[index] ?? throw new InvalidOperationException("Validated effect sequence cannot contain null effects.");
            var path = pathPrefix.Concat(new[] { index }).ToArray();
            var result = effect.TypeId == EffectIds.Conditional
                ? ExecuteConditional(currentState, sourceId, effect, activeTargetId, path, ref steps)
                : ExecuteLeaf(currentState, sourceId, effect, activeTargetId, path);
            currentState = result.State;
            events.AddRange(result.Events);
            if (result.PendingChoice is not null)
            {
                return new EffectSequenceResult(currentState, events, result.PendingChoice);
            }
        }

        return new EffectSequenceResult(currentState, events);
    }

    private EffectSequenceResult ExecuteConditional(
        MatchStateSnapshot state,
        StableId sourceId,
        EffectDefinition effect,
        StableId? activeTargetId,
        IReadOnlyList<int> effectPath,
        ref int steps)
    {
        var evaluator = CreateEvaluator(state);
        var runtime = CreateRuntimeContext(state, sourceId, activeTargetId);
        var condition = effect.Parameters.GetRequired<ConditionParameterValue>("if").Value;
        var branchName = evaluator.EvaluateCondition(condition, runtime) ? "thenEffects" : "elseEffects";
        if (!effect.Parameters.TryGet(branchName, out var branchValue) || branchValue is not EffectListParameterValue branch)
        {
            return new EffectSequenceResult(state, Array.Empty<DomainEvent>());
        }

        return ExecuteList(state, sourceId, branch.Value, activeTargetId, effectPath, ref steps);
    }

    private EffectSequenceResult ExecuteLeaf(
        MatchStateSnapshot state,
        StableId sourceId,
        EffectDefinition effect,
        StableId? activeTargetId,
        IReadOnlyList<int> effectPath)
    {
        var evaluator = CreateEvaluator(state);
        var runtime = CreateRuntimeContext(state, sourceId, activeTargetId);
        var planned = new GameplayEffectExecutor(evaluator).Resolve(effect, runtime);
        if (planned.PendingChoice is not null)
        {
            var pending = new PendingEffectSequenceChoice(effect, effectPath, planned.PendingChoice);
            return new EffectSequenceResult(state, Array.Empty<DomainEvent>(), pending);
        }

        var currentState = state;
        var events = new List<DomainEvent>();
        foreach (var operation in planned.Operations)
        {
            var transition = _transitions.Apply(currentState, operation);
            currentState = transition.State;
            events.AddRange(transition.Events);
        }

        return new EffectSequenceResult(currentState, events);
    }

    private void RequireStepBudget(int steps)
    {
        if (steps > _effectStepBudget)
        {
            throw new InvalidOperationException($"Effect sequence exceeded the configured step budget of {_effectStepBudget}.");
        }
    }

    private static GameplayRuntimeEvaluator CreateEvaluator(MatchStateSnapshot state) =>
        new(statPipeline: new StatModifierPipeline(state.StatModifiers));

    private static GameplayRuntimeContext CreateRuntimeContext(
        MatchStateSnapshot state,
        StableId sourceId,
        StableId? activeTargetId)
    {
        var source = state.GetRequiredTarget(sourceId);
        var ownerId = source.EffectiveOwnerId
            ?? throw new InvalidOperationException($"Ability source '{source.RuntimeId}' has no effective owner.");
        var opponentId = state.Targets
            .Where(target => target.Kind == RuntimeTargetKind.Player && target.RuntimeId != ownerId)
            .Select(target => target.RuntimeId)
            .Single();
        return new GameplayRuntimeContext(
            state.Targets,
            sourceId,
            ownerId,
            opponentId,
            state.TurnNumber,
            state.PhaseId,
            state.LaneCount,
            source.LaneIndex,
            activeTargetId ?? sourceId);
    }
}
