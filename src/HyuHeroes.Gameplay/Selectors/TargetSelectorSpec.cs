/**
 * TARGET_SELECTOR_SPEC
 * Purpose: Represents deterministic structured target selection without embedding presentation or executable scripts.
 * Connections: Referenced by effects, formulas, conditions, and future authoritative target-resolution logic.
 * Risk: High because target semantics and tie-breaking directly affect deterministic gameplay outcomes.
 */
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using HyuHeroes.Gameplay.Conditions;

namespace HyuHeroes.Gameplay.Selectors;

public enum TargetScope
{
    Self,
    Source,
    Owner,
    Opponent,
    Unit,
    Hero,
    Card,
    Lane
}

public enum TargetRelation
{
    Any,
    Friendly,
    Enemy
}

public enum TargetZone
{
    Any,
    Board,
    Hand,
    Deck,
    Graveyard
}

public enum TargetLocation
{
    Any,
    CurrentLane,
    AdjacentLanes,
    Leftmost,
    Rightmost,
    AllLanes
}

public enum TargetSelection
{
    All,
    First,
    HighestAttack,
    LowestHealth,
    RandomN,
    PlayerChoice
}

public sealed class TargetSelectorSpec
{
    public TargetSelectorSpec(
        TargetScope scope,
        TargetRelation relation = TargetRelation.Any,
        TargetZone zone = TargetZone.Any,
        TargetLocation location = TargetLocation.Any,
        TargetSelection selection = TargetSelection.All,
        int selectionCount = 1,
        IEnumerable<ConditionNode>? filters = null)
    {
        if (selectionCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(selectionCount), "Selection count must be positive.");
        }

        Scope = scope;
        Relation = relation;
        Zone = zone;
        Location = location;
        Selection = selection;
        SelectionCount = selectionCount;
        Filters = new ReadOnlyCollection<ConditionNode>((filters ?? Array.Empty<ConditionNode>()).ToArray());
    }

    public TargetScope Scope { get; }
    public TargetRelation Relation { get; }
    public TargetZone Zone { get; }
    public TargetLocation Location { get; }
    public TargetSelection Selection { get; }
    public int SelectionCount { get; }
    public IReadOnlyList<ConditionNode> Filters { get; }
}
