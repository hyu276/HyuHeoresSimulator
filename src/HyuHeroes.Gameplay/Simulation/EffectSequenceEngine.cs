/**
 * EFFECT_SEQUENCE_ENGINE
 * Purpose: Executes an ability effect list against successive immutable snapshots so later effects observe state produced by earlier effects.
 * Connections: Uses GameplayEffectExecutor for leaf effects, StateTransitionEngine for mutation, snapshot-owned RNG, and replayed choice selections.
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

public sealed class EffectChoiceSelection
{
    public EffectChoiceSelection(
        IEnumerable<int> effectPath,
        string parameterName,
        IEnumerable<StableId> selectedTargetIds)
    {
        if (effectPath is null) throw new ArgumentNullException(nameof(effectPath));
        var path = effectPath.ToArray();
        if (path.Length == 0 || path.Any(index => index < 0))
        {
            throw new ArgumentException("Effect choice path must contain only non-negative indexes.", nameof(effectPath));
        }

        EffectPath = new ReadOnlyCollection<int>(path);
        Override = new EffectChoiceOverride(parameterName, selectedTargetIds);
    }

    public IReadOnlyList<int> EffectPath { get; }
    public EffectChoiceOverride Override { get; }
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
        StableId? activeTargetId = null,
        IEnumerable<EffectChoiceSelection>? resolvedChoices = null)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        if (sourceId == default) throw new ArgumentException("Source ID must be a non-default StableId.", nameof(sourceId));
        if (effects is null) throw new ArgumentNullException(nameof(effects));

        var choices = CopyChoices(resolvedChoices);
        var consumedChoicePaths = new HashSet<string>(StringComparer.Ordinal);
        var random = new DeterministicRandomStream(state.RandomState);
        var steps = 0;
        var result = ExecuteList(
            state,
            sourceId,
            effects.ToArray(),
            activeTargetId,
            Array.Empty<int>(),
            choices,
            consumedChoicePaths,
            random,
            ref steps);
        EnsureAllChoicesConsumed(choices, consumedChoicePaths);
        return result.PendingChoice is null
            ? new EffectSequenceResult(result.State.With(randomState: random.State), result.Events)
            : result;
    }

    private EffectSequenceResult ExecuteList(
        MatchStateSnapshot state,
        StableId sourceId,
        IReadOnlyList<EffectDefinition> effects,
        StableId? activeTargetId,
        IReadOnlyList<int> pathPrefix,
        IReadOnlyList<EffectChoiceSelection> resolvedChoices,
        ISet<string> consumedChoicePaths,
        DeterministicRandomStream random,
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
                ? ExecuteConditional(currentState, sourceId, effect, activeTargetId, path, resolvedChoices, consumedChoicePaths, random, ref steps)
                : ExecuteLeaf(currentState, sourceId, effect, activeTargetId, path, resolvedChoices, consumedChoicePaths, random);
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
        IReadOnlyList<EffectChoiceSelection> resolvedChoices,
        ISet<string> consumedChoicePaths,
        DeterministicRandomStream random,
        ref int steps)
    {
        var evaluator = CreateEvaluator(state);
        var runtime = CreateRuntimeContext(state, sourceId, activeTargetId, random);
        var condition = effect.Parameters.GetRequired<ConditionParameterValue>("if").Value;
        var branchName = evaluator.EvaluateCondition(condition, runtime) ? "thenEffects" : "elseEffects";
        if (!effect.Parameters.TryGet(branchName, out var branchValue) || branchValue is not EffectListParameterValue branch)
        {
            return new EffectSequenceResult(state, Array.Empty<DomainEvent>());
        }

        return ExecuteList(
            state,
            sourceId,
            branch.Value,
            activeTargetId,
            effectPath,
            resolvedChoices,
            consumedChoicePaths,
            random,
            ref steps);
    }

    private EffectSequenceResult ExecuteLeaf(
        MatchStateSnapshot state,
        StableId sourceId,
        EffectDefinition effect,
        StableId? activeTargetId,
        IReadOnlyList<int> effectPath,
        IReadOnlyList<EffectChoiceSelection> resolvedChoices,
        ISet<string> consumedChoicePaths,
        DeterministicRandomStream random)
    {
        var evaluator = CreateEvaluator(state);
        var runtime = CreateRuntimeContext(state, sourceId, activeTargetId, random);
        var executor = new GameplayEffectExecutor(evaluator);
        var choice = FindChoice(resolvedChoices, effectPath);
        var planned = choice is null
            ? executor.Resolve(effect, runtime)
            : executor.ResolveWithChoice(effect, runtime, choice.Override);
        if (choice is not null)
        {
            consumedChoicePaths.Add(PathKey(effectPath));
        }

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

    private static IReadOnlyList<EffectChoiceSelection> CopyChoices(IEnumerable<EffectChoiceSelection>? resolvedChoices)
    {
        var choices = (resolvedChoices ?? Array.Empty<EffectChoiceSelection>()).ToArray();
        if (choices.Any(choice => choice is null))
        {
            throw new ArgumentException("Resolved choices cannot contain null values.", nameof(resolvedChoices));
        }

        if (choices.GroupBy(choice => PathKey(choice.EffectPath)).Any(group => group.Count() > 1))
        {
            throw new ArgumentException("Only one resolved choice may target each effect path.", nameof(resolvedChoices));
        }

        return new ReadOnlyCollection<EffectChoiceSelection>(choices);
    }

    private static EffectChoiceSelection? FindChoice(
        IReadOnlyList<EffectChoiceSelection> resolvedChoices,
        IReadOnlyList<int> effectPath) =>
        resolvedChoices.FirstOrDefault(choice => choice.EffectPath.SequenceEqual(effectPath));

    private static void EnsureAllChoicesConsumed(
        IReadOnlyList<EffectChoiceSelection> choices,
        ISet<string> consumedChoicePaths)
    {
        var unconsumed = choices.FirstOrDefault(choice => !consumedChoicePaths.Contains(PathKey(choice.EffectPath)));
        if (unconsumed is not null)
        {
            throw new InvalidOperationException(
                $"Resolved choice path '{PathKey(unconsumed.EffectPath)}' was not reached during deterministic replay.");
        }
    }

    private static string PathKey(IEnumerable<int> effectPath) => string.Join(".", effectPath);

    private static GameplayRuntimeEvaluator CreateEvaluator(MatchStateSnapshot state) =>
        new(statPipeline: new StatModifierPipeline(state.StatModifiers));

    private static GameplayRuntimeContext CreateRuntimeContext(
        MatchStateSnapshot state,
        StableId sourceId,
        StableId? activeTargetId,
        IDeterministicRandomSource random)
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
            activeTargetId ?? sourceId,
            random);
    }
}
