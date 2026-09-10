/**
 * SCHEMA_VALIDATION_TESTS
 * Purpose: Verifies schema-v1 cards, abilities, formulas, effects, and controlled taxonomy validation behavior.
 * Connections: Exercises GameplayRegistryCatalog, EffectRegistry, GameplaySchema, and GameplaySchemaValidator together.
 * Risk: High because publication validation is the primary defense against malformed authoring data.
 */
using HyuHeroes.Gameplay.Authoring;
using HyuHeroes.Gameplay.Core;
using HyuHeroes.Gameplay.Effects;
using HyuHeroes.Gameplay.Formulas;
using HyuHeroes.Gameplay.Registries;
using HyuHeroes.Gameplay.Schema;
using HyuHeroes.Gameplay.Selectors;
using HyuHeroes.Gameplay.Validation;

namespace HyuHeroes.Gameplay.Tests;

public sealed class SchemaValidationTests
{
    [Fact]
    public void Card_WithInlineDamageAbilityAndRegisteredTaxonomy_IsValid()
    {
        var catalog = GameplayRegistryCatalog.CreateSchemaV1();
        var validator = new GameplaySchemaValidator(catalog);
        var target = new TargetSelectorSpec(
            TargetScope.Unit,
            TargetRelation.Enemy,
            TargetZone.Board,
            selection: TargetSelection.PlayerChoice);
        var damage = new EffectDefinition(
            EffectIds.Damage,
            Bag(
                Pair("target", new SelectorParameterValue(target)),
                Pair("amount", new FormulaParameterValue(FormulaExpression.Constant(3))),
                Pair("damageType", new EnumParameterValue("ARCANE"))));
        var ability = new AbilityDefinition(
            Header("ability.prototype_burst"),
            new TriggerSpec(TriggerIds.OnPlay),
            new[] { damage });
        var card = new CardDefinition(
            Header("card.prototype_mage"),
            CardType.Unit,
            new Classification(
                StableId.Parse("alignment.neutral"),
                StableId.Parse("class.control")),
            new StatBlock(new[]
            {
                new KeyValuePair<StableId, decimal>(StableId.Parse("stat.cost"), 2),
                new KeyValuePair<StableId, decimal>(StableId.Parse("stat.attack"), 2),
                new KeyValuePair<StableId, decimal>(StableId.Parse("stat.max_health"), 3)
            }),
            new[] { AbilityBinding.Inline(ability) });

        var result = validator.Validate(card);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
    }

    [Fact]
    public void Effect_MissingRequiredAmount_IsRejected()
    {
        var validator = new GameplaySchemaValidator(GameplayRegistryCatalog.CreateSchemaV1());
        var target = new TargetSelectorSpec(TargetScope.Unit, TargetRelation.Enemy, TargetZone.Board);
        var effect = new EffectDefinition(
            EffectIds.Damage,
            Bag(
                Pair("target", new SelectorParameterValue(target)),
                Pair("damageType", new EnumParameterValue("PHYSICAL"))));

        var result = validator.Validate(effect);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == "parameter.required" && error.Path.EndsWith("amount", StringComparison.Ordinal));
    }

    [Fact]
    public void ConditionalEffect_WithNestedDamageBranch_IsValid()
    {
        var validator = new GameplaySchemaValidator(GameplayRegistryCatalog.CreateSchemaV1());
        var target = new TargetSelectorSpec(TargetScope.Unit, TargetRelation.Enemy, TargetZone.Board);
        var predicate = new HyuHeroes.Gameplay.Conditions.PredicateConditionNode(
            new HyuHeroes.Gameplay.Conditions.ConditionPredicateSpec(
                ConditionIds.TargetIsDamaged,
                Bag(Pair("subject", new SelectorParameterValue(target)))));
        var damage = new EffectDefinition(
            EffectIds.Damage,
            Bag(
                Pair("target", new SelectorParameterValue(target)),
                Pair("amount", new FormulaParameterValue(FormulaExpression.Constant(2))),
                Pair("damageType", new EnumParameterValue("PHYSICAL"))));
        var conditional = new EffectDefinition(
            EffectIds.Conditional,
            Bag(
                Pair("if", new ConditionParameterValue(predicate)),
                Pair("thenEffects", new EffectListParameterValue(new[] { damage }))));

        var result = validator.Validate(conditional);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
    }

    [Fact]
    public void Formula_WithUnknownVariable_IsRejected()
    {
        var validator = new GameplaySchemaValidator(GameplayRegistryCatalog.CreateSchemaV1());
        var formula = FormulaExpression.Variable(StableId.Parse("variable.unknown.value"));

        var result = validator.Validate(formula);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == "registry.unknown_id");
    }

    private static GameplayDefinitionHeader Header(string id) =>
        new(StableId.Parse(id), GameplaySchemaValidator.SupportedSchemaVersion, 1, ContentStatus.Draft, $"loc.{id.Replace('.', '_')}");

    private static ParameterBag Bag(params KeyValuePair<string, ParameterValue>[] pairs) => new(pairs);

    private static KeyValuePair<string, ParameterValue> Pair(string key, ParameterValue value) => new(key, value);
}
