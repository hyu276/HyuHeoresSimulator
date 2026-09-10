/**
 * GAMEPLAY_RUNTIME_EVALUATOR
 * Purpose: Composes target, condition, formula, and effective-stat evaluation into one read-only deterministic runtime bridge.
 * Connections: Coordinates TargetResolver, ConditionEvaluator, DefaultConditionPredicateResolver, DefaultFormulaRuntimeContext, and StatModifierPipeline.
 * Risk: High because recursive evaluation and effective stat resolution must share one coherent authoritative context.
 */
using System;
using System.Collections.Generic;
using HyuHeroes.Gameplay.Conditions;
using HyuHeroes.Gameplay.Core;
using HyuHeroes.Gameplay.Formulas;
using HyuHeroes.Gameplay.Modifiers;
using HyuHeroes.Gameplay.Selectors;

namespace HyuHeroes.Gameplay.Runtime;

public sealed class GameplayRuntimeEvaluator
{
    private readonly ConditionEvaluator _conditionEvaluator;
    private readonly FormulaEvaluator _formulaEvaluator;
    private readonly StatModifierPipeline _statPipeline;
    private readonly TargetResolver _targetResolver;

    public GameplayRuntimeEvaluator(
        FormulaOperatorRegistry? formulaOperators = null,
        StatModifierPipeline? statPipeline = null)
    {
        _targetResolver = new TargetResolver();
        _conditionEvaluator = new ConditionEvaluator();
        _formulaEvaluator = new FormulaEvaluator(formulaOperators ?? DefaultFormulaOperators.Create());
        _statPipeline = statPipeline ?? new StatModifierPipeline();
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

    public decimal ResolveStat(RuntimeTarget target, StableId statId) =>
        _statPipeline.Resolve(target, statId).EffectiveValue;

    public StatResolution ResolveStatDetailed(RuntimeTarget target, StableId statId) =>
        _statPipeline.Resolve(target, statId);

    internal IReadOnlyList<RuntimeTarget> ResolveTargetsForEvaluation(
        TargetSelectorSpec selector,
        GameplayRuntimeContext context) =>
        _targetResolver.ResolveForEvaluation(selector, context, EvaluateCondition);
}
