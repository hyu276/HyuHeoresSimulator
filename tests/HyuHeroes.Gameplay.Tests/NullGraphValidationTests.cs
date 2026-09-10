/**
 * NULL_GRAPH_VALIDATION_TESTS
 * Purpose: Ensures malformed authoring graphs containing null nested nodes are rejected as validation errors instead of throwing.
 * Connections: Exercises FormulaExpression, TargetSelectorSpec, EffectListParameterValue, and GameplaySchemaValidator.
 * Risk: High because untrusted authoring payloads must not crash recursive validation.
 */
using HyuHeroes.Gameplay.Authoring;
using HyuHeroes.Gameplay.Conditions;
using HyuHeroes.Gameplay.Core;
using HyuHeroes.Gameplay.Effects;
using HyuHeroes.Gameplay.Formulas;
using HyuHeroes.Gameplay.Registries;
using HyuHeroes.Gameplay.Selectors;
using HyuHeroes.Gameplay.Validation;

namespace HyuHeroes.Gameplay.Tests;

public sealed class NullGraphValidationTests
{
    [Fact]
    public void Formula_WithNullNestedArgument_ReturnsValidationError()
    {
        var validator = CreateValidator();
        var formula = new FormulaExpression(
            FormulaOperatorIds.Add,
            new FormulaExpression[] { FormulaExpression.Constant(1), null! });

        var result = validator.Validate(formula);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == "formula.null_node");
    }

    [Fact]
    public void Selector_WithNullFilter_ReturnsValidationError()
    {
        var validator = CreateValidator();
        var selector = new TargetSelectorSpec(
            TargetScope.Unit,
            TargetRelation.Enemy,
            TargetZone.Board,
            filters: new ConditionNode[] { null! });
        var effect = DamageEffect(selector);

        var result = validator.Validate(effect);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == "condition.null_node");
    }

    [Fact]
    public void ConditionalEffect_WithNullNestedEffect_ReturnsValidationError()
    {
        var validator = CreateValidator();
        var predicate = new PredicateConditionNode(
            new ConditionPredicateSpec(
                ConditionIds.TargetIsDamaged,
                Bag(Pair(
                    "subject",
                    new SelectorParameterValue(new TargetSelectorSpec(TargetScope.Unit, TargetRelation.Enemy, TargetZone.Board))))));
        var effect = new EffectDefinition(
            EffectIds.Conditional,
            Bag(
                Pair("if", new ConditionParameterValue(predicate)),
                Pair("thenEffects", new EffectListParameterValue(new EffectDefinition[] { null! }))));

        var result = validator.Validate(effect);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == "effect.null_node");
    }

    private static GameplaySchemaValidator CreateValidator() =>
        new(GameplayRegistryCatalog.CreateSchemaV1());

    private static EffectDefinition DamageEffect(TargetSelectorSpec selector) =>
        new(
            EffectIds.Damage,
            Bag(
                Pair("target", new SelectorParameterValue(selector)),
                Pair("amount", new FormulaParameterValue(FormulaExpression.Constant(1))),
                Pair("damageType", new EnumParameterValue("PHYSICAL"))));

    private static ParameterBag Bag(params KeyValuePair<string, ParameterValue>[] pairs) => new(pairs);

    private static KeyValuePair<string, ParameterValue> Pair(string key, ParameterValue value) => new(key, value);
}
