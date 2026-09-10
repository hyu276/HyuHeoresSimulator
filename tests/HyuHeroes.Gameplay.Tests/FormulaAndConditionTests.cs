/**
 * FORMULA_AND_CONDITION_TESTS
 * Purpose: Verifies deterministic formula evaluation and nested boolean condition tree semantics without game-engine dependencies.
 * Connections: Exercises Formula AST, target counting, variable resolution, and condition predicate resolver boundaries.
 * Risk: High because arithmetic and conditional behavior directly determine authoritative gameplay results.
 */
using HyuHeroes.Gameplay.Conditions;
using HyuHeroes.Gameplay.Core;
using HyuHeroes.Gameplay.Formulas;
using HyuHeroes.Gameplay.Selectors;

namespace HyuHeroes.Gameplay.Tests;

public sealed class FormulaAndConditionTests
{
    [Fact]
    public void Formula_MaxOfOneAndAttackPlusCount_EvaluatesDeterministically()
    {
        var operators = DefaultFormulaOperators.Create();
        var evaluator = new FormulaEvaluator(operators);
        var selector = new TargetSelectorSpec(TargetScope.Unit, TargetRelation.Friendly, TargetZone.Board);
        var expression = FormulaExpression.Call(
            FormulaOperatorIds.Max,
            FormulaExpression.Constant(1),
            FormulaExpression.Call(
                FormulaOperatorIds.Add,
                FormulaExpression.Variable(StableId.Parse("variable.source.attack")),
                FormulaExpression.Count(selector)));
        var context = new FormulaEvaluationContext(
            new DictionaryVariableResolver(new Dictionary<StableId, decimal>
            {
                [StableId.Parse("variable.source.attack")] = 3
            }),
            new FixedCountResolver(2));

        var result = evaluator.Evaluate(expression, context);

        Assert.Equal(5m, result);
    }

    [Fact]
    public void Formula_DivideByZero_IsRejected()
    {
        var evaluator = new FormulaEvaluator(DefaultFormulaOperators.Create());
        var expression = FormulaExpression.Call(
            FormulaOperatorIds.Divide,
            FormulaExpression.Constant(4),
            FormulaExpression.Constant(0));
        var context = new FormulaEvaluationContext(new DictionaryVariableResolver(), new FixedCountResolver(0));

        Assert.Throws<DivideByZeroException>(() => evaluator.Evaluate(expression, context));
    }

    [Fact]
    public void ConditionTree_AllAnyNot_UsesRegisteredPredicateResolver()
    {
        var enabled = new PredicateConditionNode(new ConditionPredicateSpec(StableId.Parse("condition.test_enabled")));
        var blocked = new PredicateConditionNode(new ConditionPredicateSpec(StableId.Parse("condition.test_blocked")));
        var tree = new AllConditionNode(new ConditionNode[]
        {
            enabled,
            new NotConditionNode(blocked)
        });
        var resolver = new DictionaryPredicateResolver(new Dictionary<StableId, bool>
        {
            [StableId.Parse("condition.test_enabled")] = true,
            [StableId.Parse("condition.test_blocked")] = false
        });

        var result = new ConditionEvaluator().Evaluate(tree, resolver);

        Assert.True(result);
    }

    private sealed class DictionaryVariableResolver : IFormulaVariableResolver
    {
        private readonly IReadOnlyDictionary<StableId, decimal> _values;

        public DictionaryVariableResolver(IReadOnlyDictionary<StableId, decimal>? values = null)
        {
            _values = values ?? new Dictionary<StableId, decimal>();
        }

        public decimal Resolve(StableId variableId) =>
            _values.TryGetValue(variableId, out var value)
                ? value
                : throw new KeyNotFoundException(variableId.ToString());
    }

    private sealed class FixedCountResolver : ITargetCountResolver
    {
        private readonly int _count;

        public FixedCountResolver(int count) => _count = count;

        public int Count(TargetSelectorSpec selector) => _count;
    }

    private sealed class DictionaryPredicateResolver : IConditionPredicateResolver
    {
        private readonly IReadOnlyDictionary<StableId, bool> _values;

        public DictionaryPredicateResolver(IReadOnlyDictionary<StableId, bool> values) => _values = values;

        public bool Evaluate(ConditionPredicateSpec predicate) =>
            _values.TryGetValue(predicate.TypeId, out var result) && result;
    }
}
