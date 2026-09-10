/**
 * TARGET_SELECTOR_INVARIANT_TESTS
 * Purpose: Ensures selector construction rejects undefined enum values before they reach deterministic targeting resolution.
 * Connections: Exercises TargetSelectorSpec input invariants used by effects, conditions, and formulas.
 * Risk: High because undefined targeting semantics would make authoritative resolution ambiguous.
 */
using HyuHeroes.Gameplay.Selectors;

namespace HyuHeroes.Gameplay.Tests;

public sealed class TargetSelectorInvariantTests
{
    [Fact]
    public void Selector_WithUndefinedEnums_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TargetSelectorSpec((TargetScope)999));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TargetSelectorSpec(TargetScope.Unit, relation: (TargetRelation)999));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TargetSelectorSpec(TargetScope.Unit, zone: (TargetZone)999));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TargetSelectorSpec(TargetScope.Unit, location: (TargetLocation)999));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TargetSelectorSpec(TargetScope.Unit, selection: (TargetSelection)999));
    }
}
