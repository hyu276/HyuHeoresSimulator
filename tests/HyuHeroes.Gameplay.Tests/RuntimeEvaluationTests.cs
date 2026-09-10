/**
 * RUNTIME_EVALUATION_TESTS
 * Purpose: Verifies deterministic target resolution, built-in condition predicates, and formula runtime variables against immutable fixtures.
 * Connections: Exercises GameplayRuntimeEvaluator across Runtime, Selectors, Conditions, Formulas, schema parameter bags, and seeded RNG boundaries.
 * Risk: High because these tests protect the bridge from validated authoring data into authoritative executable gameplay evaluation.
 */
using HyuHeroes.Gameplay.Authoring;
using HyuHeroes.Gameplay.Conditions;
using HyuHeroes.Gameplay.Core;
using HyuHeroes.Gameplay.Formulas;
using HyuHeroes.Gameplay.Registries;
using HyuHeroes.Gameplay.Runtime;
using HyuHeroes.Gameplay.Schema;
using HyuHeroes.Gameplay.Selectors;

namespace HyuHeroes.Gameplay.Tests;

public sealed class RuntimeEvaluationTests
{
    private static readonly StableId OwnerId = StableId.Parse("player.alpha");
    private static readonly StableId OpponentId = StableId.Parse("player.beta");
    private static readonly StableId SourceId = StableId.Parse("entity.source");
    private static readonly StableId EnemyAId = StableId.Parse("entity.enemy_a");
    private static readonly StableId EnemyBId = StableId.Parse("entity.enemy_b");
    private static readonly StableId AttackStatId = StableId.Parse("stat.attack");
    private static readonly StableId MaxHealthStatId = StableId.Parse("stat.max_health");
    private static readonly StableId PrimaryResourceId = StableId.Parse("resource.primary");

    [Fact]
    public void TargetResolver_FriendlyBoardUnits_AreOrderedByLaneThenStableId()
    {
        var evaluator = new GameplayRuntimeEvaluator();
        var selector = new TargetSelectorSpec(TargetScope.Unit, TargetRelation.Friendly, TargetZone.Board);

        var result = evaluator.ResolveTargets(selector, CreateContext());

        Assert.False(result.RequiresPlayerChoice);
        Assert.Equal(new[] { "entity.friend", "entity.source" }, result.SelectedTargets.Select(target => target.RuntimeId.Value));
    }

    [Fact]
    public void TargetResolver_PlayerChoice_ExposesCandidatesWithoutAutoSelecting()
    {
        var evaluator = new GameplayRuntimeEvaluator();
        var selector = new TargetSelectorSpec(
            TargetScope.Unit,
            TargetRelation.Enemy,
            TargetZone.Board,
            selection: TargetSelection.PlayerChoice);

        var result = evaluator.ResolveTargets(selector, CreateContext());

        Assert.True(result.RequiresPlayerChoice);
        Assert.Equal(1, result.RequiredSelectionCount);
        Assert.Empty(result.SelectedTargets);
        Assert.Equal(new[] { "entity.enemy_a", "entity.enemy_b" }, result.Candidates.Select(target => target.RuntimeId.Value));
    }

    [Fact]
    public void TargetResolver_RandomN_UsesInjectedDeterministicRandomSource()
    {
        var evaluator = new GameplayRuntimeEvaluator();
        var selector = new TargetSelectorSpec(
            TargetScope.Unit,
            TargetRelation.Enemy,
            TargetZone.Board,
            selection: TargetSelection.RandomN,
            selectionCount: 2);

        var result = evaluator.ResolveTargets(selector, CreateContext(new SequenceRandomSource(1, 0)));

        Assert.Equal(new[] { "entity.enemy_b", "entity.enemy_a" }, result.SelectedTargets.Select(target => target.RuntimeId.Value));
    }

    [Fact]
    public void Condition_HasTag_UsesSelfAsActiveTargetInsideRuntimeEvaluation()
    {
        var evaluator = new GameplayRuntimeEvaluator();
        var condition = Predicate(
            ConditionIds.HasTag,
            Pair("subject", new SelectorParameterValue(new TargetSelectorSpec(TargetScope.Self))),
            Pair("tagId", new StableIdParameterValue(StableId.Parse("tag.beast"))));

        var result = evaluator.EvaluateCondition(condition, CreateContext().WithActiveTarget(EnemyAId));

        Assert.True(result);
    }

    [Fact]
    public void Condition_CountMatchingCompare_UsesResolvedEnemyCount()
    {
        var evaluator = new GameplayRuntimeEvaluator();
        var condition = Predicate(
            ConditionIds.CountMatchingCompare,
            Pair("subject", new SelectorParameterValue(new TargetSelectorSpec(TargetScope.Unit, TargetRelation.Enemy, TargetZone.Board))),
            Pair("operator", new EnumParameterValue("EQ")),
            Pair("value", new FormulaParameterValue(FormulaExpression.Constant(2))));

        var result = evaluator.EvaluateCondition(condition, CreateContext());

        Assert.True(result);
    }

    [Fact]
    public void Condition_LaneIsEmpty_RecognizesUnoccupiedLane()
    {
        var evaluator = new GameplayRuntimeEvaluator();
        var selector = new TargetSelectorSpec(
            TargetScope.Lane,
            location: TargetLocation.Rightmost,
            selection: TargetSelection.First);
        var condition = Predicate(
            ConditionIds.LaneIsEmpty,
            Pair("subject", new SelectorParameterValue(selector)));

        var result = evaluator.EvaluateCondition(condition, CreateContext());

        Assert.True(result);
    }

    [Fact]
    public void FormulaRuntimeContext_ResolvesSourceTargetResourceAndTurnVariables()
    {
        var evaluator = new GameplayRuntimeEvaluator();
        var formula = FormulaExpression.Call(
            FormulaOperatorIds.Add,
            FormulaExpression.Variable(StableId.Parse("variable.source.attack")),
            FormulaExpression.Variable(StableId.Parse("variable.owner.resource")),
            FormulaExpression.Variable(StableId.Parse("variable.target.health")),
            FormulaExpression.Variable(StableId.Parse("variable.turn.number")));

        var result = evaluator.EvaluateFormula(formula, CreateContext().WithActiveTarget(EnemyAId));

        Assert.Equal(13m, result);
    }

    [Fact]
    public void FormulaCount_DelegatesToTargetResolverWithoutConsumingRandomness()
    {
        var evaluator = new GameplayRuntimeEvaluator();
        var formula = FormulaExpression.Count(new TargetSelectorSpec(TargetScope.Unit, TargetRelation.Enemy, TargetZone.Board));

        var result = evaluator.EvaluateFormula(formula, CreateContext());

        Assert.Equal(2m, result);
    }

    [Fact]
    public void FormulaCount_WithNonAllSelection_IsRejectedAtRuntimeBoundary()
    {
        var evaluator = new GameplayRuntimeEvaluator();
        var formula = FormulaExpression.Count(new TargetSelectorSpec(
            TargetScope.Unit,
            TargetRelation.Enemy,
            TargetZone.Board,
            selection: TargetSelection.PlayerChoice));

        Assert.Throws<InvalidOperationException>(() => evaluator.EvaluateFormula(formula, CreateContext()));
    }

    private static PredicateConditionNode Predicate(StableId typeId, params KeyValuePair<string, ParameterValue>[] values) =>
        new(new ConditionPredicateSpec(typeId, new ParameterBag(values)));

    private static KeyValuePair<string, ParameterValue> Pair(string key, ParameterValue value) => new(key, value);

    private static GameplayRuntimeContext CreateContext(IDeterministicRandomSource? randomSource = null)
    {
        var targets = new[]
        {
            Player(OwnerId, 5),
            Player(OpponentId, 4),
            Lane("lane.left", 0),
            Lane("lane.center", 1),
            Lane("lane.right", 2),
            Lane("lane.far", 3),
            Unit("entity.friend", OwnerId, 0, 2, 4, 4),
            Unit(SourceId.Value, OwnerId, 1, 3, 5, 5),
            Unit(EnemyAId.Value, OpponentId, 1, 4, 4, 2, StableId.Parse("tag.beast")),
            Unit(EnemyBId.Value, OpponentId, 2, 1, 3, 3)
        };

        return new GameplayRuntimeContext(
            targets,
            SourceId,
            OwnerId,
            OpponentId,
            turnNumber: 3,
            phaseId: StableId.Parse("phase.main_action"),
            laneCount: 4,
            currentLaneIndex: 1,
            randomSource: randomSource);
    }

    private static RuntimeTarget Player(StableId id, decimal resource) =>
        new(
            id,
            RuntimeTargetKind.Player,
            resources: new[] { new KeyValuePair<StableId, decimal>(PrimaryResourceId, resource) });

    private static RuntimeTarget Lane(string id, int laneIndex) =>
        new(StableId.Parse(id), RuntimeTargetKind.Lane, laneIndex: laneIndex);

    private static RuntimeTarget Unit(
        string id,
        StableId ownerId,
        int laneIndex,
        decimal attack,
        decimal maxHealth,
        decimal currentHealth,
        params StableId[] tags) =>
        new(
            StableId.Parse(id),
            RuntimeTargetKind.Unit,
            ownerId,
            TargetZone.Board,
            laneIndex,
            CardType.Unit,
            tags,
            stats: new[]
            {
                new KeyValuePair<StableId, decimal>(AttackStatId, attack),
                new KeyValuePair<StableId, decimal>(MaxHealthStatId, maxHealth)
            },
            currentHealth: currentHealth);

    private sealed class SequenceRandomSource : IDeterministicRandomSource
    {
        private readonly Queue<int> _values;

        public SequenceRandomSource(params int[] values) => _values = new Queue<int>(values);

        public int NextInt(int exclusiveMaximum)
        {
            if (_values.Count == 0)
            {
                throw new InvalidOperationException("No deterministic test RNG value is available.");
            }

            var value = _values.Dequeue();
            if (value < 0 || value >= exclusiveMaximum)
            {
                throw new InvalidOperationException("Deterministic test RNG value is outside the requested range.");
            }

            return value;
        }
    }
}
