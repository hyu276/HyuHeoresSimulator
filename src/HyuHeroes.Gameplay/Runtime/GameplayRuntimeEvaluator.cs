/**
 * GAMEPLAY_RUNTIME_EVALUATOR
 * Purpose: Composes target, condition, and formula evaluation into one read-only deterministic runtime bridge for validated schema content.
 * Connections: Coordinates TargetResolver, ConditionEvaluator, DefaultConditionPredicateResolver, and DefaultFormulaRuntimeContext.
 * Risk: High because recursive selector filters and predicate/formula evaluation must share one coherent authoritative context.
 */
using System;
using System.Collections.Generic;
using HyuHeroes.Gameplay.Conditions;
using HyuHeroes.Gameplay.Formulas;
using HyuHeroes.Gameplay.Selectors;

namespace HyuHeroes.Gameplay.Runtime;

public sealed class GameplayRuntimeEvaluator
{
    private readonly ConditionEvaluator _conditionEvaluator;
    private readonly FormulaEvaluator _formulaEvaluator;
    private readonly TargetResolver _targetResolver;

    public GameplayRuntimeEvaluator(FormulaOperatorRegistry? formulaOperators = null)
    {
        _targetResolver = new TargetResolver();
        _conditionEvaluator = new ConditionEvaluator();
        _formulaEvaluator = new FormulaEvaluator(formulaOperators ?? DefaultFormulaOperators.Create());
    }

    public TargetResolution ResolveTargets(TargetSelectorSpec selector, GameplayRuntimeContext context) =>
        _targetResolver.Resolve(selector, context, EvaluateCondition);

    public bool EvaluateCondition(ConditionNode condition, GameplayRuntimeContext context)
    {
        if (condition is null)
        {
            throw new ArgumentNullException(nameof(condition));
        }

        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        var predicateResolver = new DefaultConditionPredicateResolver(this, context);
        return _conditionEvaluator.Evaluate(condition, predicateResolver);
    }

    public decimal EvaluateFormula(FormulaExpression formula, GameplayRuntimeContext context)
    {
        if (formula is null)
        {
            throw new ArgumentNullException(nameof(formula));
        }

        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        var runtimeContext = new DefaultFormulaRuntimeContext(this, context);
        return _formulaEvaluator.Evaluate(formula, new FormulaEvaluationContext(runtimeContext, runtimeContext));
    }

    internal IReadOnlyList<RuntimeTarget> ResolveTargetsForEvaluation(
        TargetSelectorSpec selector,
        GameplayRuntimeContext context) =>
        _targetResolver.ResolveForEvaluation(selector, context, EvaluateCondition);
}
