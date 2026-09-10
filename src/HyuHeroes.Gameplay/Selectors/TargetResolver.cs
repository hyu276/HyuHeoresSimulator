/**
 * TARGET_RESOLVER
 * Purpose: Resolves structured selectors against immutable runtime snapshots with deterministic ordering and explicit player-choice results.
 * Connections: Used by runtime condition predicates, formula COUNT evaluation, and future effect execution handlers.
 * Risk: High because target selection order, filtering, RNG usage, and player-choice boundaries affect replay determinism.
 */
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using HyuHeroes.Gameplay.Conditions;
using HyuHeroes.Gameplay.Core;
using HyuHeroes.Gameplay.Runtime;

namespace HyuHeroes.Gameplay.Selectors;

public sealed class TargetResolution
{
    public TargetResolution(
        IEnumerable<RuntimeTarget> candidates,
        IEnumerable<RuntimeTarget> selectedTargets,
        bool requiresPlayerChoice,
        int requiredSelectionCount)
    {
        Candidates = new ReadOnlyCollection<RuntimeTarget>(candidates.ToArray());
        SelectedTargets = new ReadOnlyCollection<RuntimeTarget>(selectedTargets.ToArray());
        RequiresPlayerChoice = requiresPlayerChoice;
        RequiredSelectionCount = requiredSelectionCount;
    }

    public IReadOnlyList<RuntimeTarget> Candidates { get; }
    public IReadOnlyList<RuntimeTarget> SelectedTargets { get; }
    public bool RequiresPlayerChoice { get; }
    public int RequiredSelectionCount { get; }
}

public sealed class TargetResolver
{
    private static readonly StableId AttackStatId = StableId.Parse("stat.attack");
    private static readonly StableId MaxHealthStatId = StableId.Parse("stat.max_health");

    public TargetResolution Resolve(
        TargetSelectorSpec selector,
        GameplayRuntimeContext context,
        Func<ConditionNode, GameplayRuntimeContext, bool>? evaluateCondition = null)
    {
        if (selector is null)
        {
            throw new ArgumentNullException(nameof(selector));
        }

        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        var candidates = ResolveScope(selector.Scope, context)
            .Where(target => MatchesRelation(target, selector.Relation, context))
            .Where(target => MatchesZone(target, selector.Zone))
            .Where(target => MatchesLocation(target, selector.Location, context))
            .OrderBy(target => target.LaneIndex ?? int.MaxValue)
            .ThenBy(target => target.RuntimeId)
            .ToArray();

        candidates = ApplyFilters(candidates, selector.Filters, context, evaluateCondition);
        return ApplySelection(selector, candidates, context);
    }

    public IReadOnlyList<RuntimeTarget> ResolveForEvaluation(
        TargetSelectorSpec selector,
        GameplayRuntimeContext context,
        Func<ConditionNode, GameplayRuntimeContext, bool>? evaluateCondition = null)
    {
        var resolution = Resolve(selector, context, evaluateCondition);
        if (resolution.RequiresPlayerChoice)
        {
            throw new InvalidOperationException("Evaluation cannot consume an unresolved PLAYER_CHOICE selector.");
        }

        return resolution.SelectedTargets;
    }

    private static IEnumerable<RuntimeTarget> ResolveScope(TargetScope scope, GameplayRuntimeContext context)
    {
        return scope switch
        {
            TargetScope.Self => new[] { context.ActiveTargetId is { } targetId ? context.GetRequiredTarget(targetId) : context.Source },
            TargetScope.Source => new[] { context.Source },
            TargetScope.Owner => new[] { context.Owner },
            TargetScope.Opponent => new[] { context.Opponent },
            TargetScope.Unit => context.Targets.Where(target => target.Kind == RuntimeTargetKind.Unit),
            TargetScope.Hero => context.Targets.Where(target => target.Kind == RuntimeTargetKind.Hero),
            TargetScope.Card => context.Targets.Where(IsCardLike),
            TargetScope.Lane => context.Targets.Where(target => target.Kind == RuntimeTargetKind.Lane),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unsupported target scope.")
        };
    }

    private static bool IsCardLike(RuntimeTarget target) =>
        target.Kind == RuntimeTargetKind.Card || target.CardType is not null;

    private static bool MatchesRelation(
        RuntimeTarget target,
        TargetRelation relation,
        GameplayRuntimeContext context)
    {
        return relation switch
        {
            TargetRelation.Any => true,
            TargetRelation.Friendly => target.EffectiveOwnerId == context.OwnerId,
            TargetRelation.Enemy => target.EffectiveOwnerId == context.OpponentId,
            _ => false
        };
    }

    private static bool MatchesZone(RuntimeTarget target, TargetZone zone) =>
        zone == TargetZone.Any || target.Zone == zone;

    private static bool MatchesLocation(
        RuntimeTarget target,
        TargetLocation location,
        GameplayRuntimeContext context)
    {
        if (location == TargetLocation.Any)
        {
            return true;
        }

        if (target.LaneIndex is not { } laneIndex)
        {
            return false;
        }

        return location switch
        {
            TargetLocation.CurrentLane => context.EffectiveCurrentLaneIndex == laneIndex,
            TargetLocation.AdjacentLanes => IsAdjacent(laneIndex, context.EffectiveCurrentLaneIndex),
            TargetLocation.Leftmost => laneIndex == 0,
            TargetLocation.Rightmost => laneIndex == context.LaneCount - 1,
            TargetLocation.AllLanes => true,
            _ => false
        };
    }

    private static bool IsAdjacent(int laneIndex, int? currentLaneIndex) =>
        currentLaneIndex is { } current && Math.Abs(laneIndex - current) == 1;

    private static RuntimeTarget[] ApplyFilters(
        IReadOnlyList<RuntimeTarget> candidates,
        IReadOnlyList<ConditionNode> filters,
        GameplayRuntimeContext context,
        Func<ConditionNode, GameplayRuntimeContext, bool>? evaluateCondition)
    {
        if (filters.Count == 0)
        {
            return candidates.ToArray();
        }

        if (evaluateCondition is null)
        {
            throw new InvalidOperationException("Selector filters require a runtime condition evaluator.");
        }

        return candidates
            .Where(candidate => FiltersMatch(candidate, filters, context, evaluateCondition))
            .ToArray();
    }

    private static bool FiltersMatch(
        RuntimeTarget candidate,
        IReadOnlyList<ConditionNode> filters,
        GameplayRuntimeContext context,
        Func<ConditionNode, GameplayRuntimeContext, bool> evaluateCondition)
    {
        var candidateContext = context.WithActiveTarget(candidate.RuntimeId);
        return filters.All(filter => evaluateCondition(filter, candidateContext));
    }

    private static TargetResolution ApplySelection(
        TargetSelectorSpec selector,
        IReadOnlyList<RuntimeTarget> candidates,
        GameplayRuntimeContext context)
    {
        return selector.Selection switch
        {
            TargetSelection.All => Completed(candidates, candidates),
            TargetSelection.First => Completed(candidates, candidates.Take(1)),
            TargetSelection.HighestAttack => Completed(candidates, SelectHighestAttack(candidates)),
            TargetSelection.LowestHealth => Completed(candidates, SelectLowestHealth(candidates)),
            TargetSelection.RandomN => Completed(candidates, SelectRandom(candidates, selector.SelectionCount, context)),
            TargetSelection.PlayerChoice => PendingChoice(candidates, selector.SelectionCount),
            _ => throw new ArgumentOutOfRangeException(nameof(selector), selector.Selection, "Unsupported target selection.")
        };
    }

    private static TargetResolution Completed(
        IReadOnlyList<RuntimeTarget> candidates,
        IEnumerable<RuntimeTarget> selectedTargets) =>
        new(candidates, selectedTargets, requiresPlayerChoice: false, requiredSelectionCount: 0);

    private static TargetResolution PendingChoice(IReadOnlyList<RuntimeTarget> candidates, int selectionCount) =>
        new(
            candidates,
            Array.Empty<RuntimeTarget>(),
            requiresPlayerChoice: true,
            requiredSelectionCount: Math.Min(selectionCount, candidates.Count));

    private static IEnumerable<RuntimeTarget> SelectHighestAttack(IReadOnlyList<RuntimeTarget> candidates)
    {
        var scored = candidates
            .Select(target => target.TryGetStat(AttackStatId, out var attack) ? (Target: target, Value: attack, HasValue: true) : (Target: target, Value: 0m, HasValue: false))
            .Where(item => item.HasValue)
            .ToArray();

        return scored.Length == 0
            ? Array.Empty<RuntimeTarget>()
            : new[] { scored.OrderByDescending(item => item.Value).ThenBy(item => item.Target.LaneIndex ?? int.MaxValue).ThenBy(item => item.Target.RuntimeId).First().Target };
    }

    private static IEnumerable<RuntimeTarget> SelectLowestHealth(IReadOnlyList<RuntimeTarget> candidates)
    {
        var scored = candidates
            .Select(target => target.TryGetCurrentHealth(MaxHealthStatId, out var health) ? (Target: target, Value: health, HasValue: true) : (Target: target, Value: 0m, HasValue: false))
            .Where(item => item.HasValue)
            .ToArray();

        return scored.Length == 0
            ? Array.Empty<RuntimeTarget>()
            : new[] { scored.OrderBy(item => item.Value).ThenBy(item => item.Target.LaneIndex ?? int.MaxValue).ThenBy(item => item.Target.RuntimeId).First().Target };
    }

    private static IEnumerable<RuntimeTarget> SelectRandom(
        IReadOnlyList<RuntimeTarget> candidates,
        int selectionCount,
        GameplayRuntimeContext context)
    {
        var random = context.RandomSource
            ?? throw new InvalidOperationException("RANDOM_N requires the authoritative deterministic random source.");
        var pool = candidates.ToList();
        var selected = new List<RuntimeTarget>();
        var required = Math.Min(selectionCount, pool.Count);

        for (var index = 0; index < required; index += 1)
        {
            var randomIndex = random.NextInt(pool.Count);
            if (randomIndex < 0 || randomIndex >= pool.Count)
            {
                throw new InvalidOperationException("Deterministic random source returned an out-of-range index.");
            }

            selected.Add(pool[randomIndex]);
            pool.RemoveAt(randomIndex);
        }

        return selected;
    }
}
