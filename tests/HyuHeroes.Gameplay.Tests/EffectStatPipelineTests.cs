/**
 * EFFECT_STAT_PIPELINE_TESTS
 * Purpose: Protects deterministic modifier ordering and declarative effect planning across formulas, conditions, and target selection.
 * Connections: Exercises StatModifierPipeline, GameplayRuntimeEvaluator, EffectHandlerRegistry, schema parameters, and immutable runtime fixtures.
 * Risk: High because regressions here would desynchronize authoring semantics from executable numeric gameplay behavior.
 */
using HyuHeroes.Gameplay.Authoring;
using HyuHeroes.Gameplay.Conditions;
using HyuHeroes.Gameplay.Core;
using HyuHeroes.Gameplay.Effects;
using HyuHeroes.Gameplay.Formulas;
using HyuHeroes.Gameplay.Modifiers;
using HyuHeroes.Gameplay.Registries;
using HyuHeroes.Gameplay.Runtime;
using HyuHeroes.Gameplay.Schema;
using HyuHeroes.Gameplay.Selectors;

namespace HyuHeroes.Gameplay.Tests;

public sealed class EffectStatPipelineTests
{
    private static readonly StableId OwnerId = StableId.Parse("player.alpha");
    private static readonly StableId OpponentId = StableId.Parse("player.beta");
    private static readonly StableId SourceId = StableId.Parse("entity.source");
    private static readonly StableId EnemyId = StableId.Parse("entity.enemy");
    private static readonly StableId AttackStatId = StableId.Parse("stat.attack");
    private static readonly StableId DefenseStatId = StableId.Parse("stat.defense");
    private static readonly StableId MaxHealthStatId = StableId.Parse("stat.max_health");
    private static readonly StableId ResourceId = StableId.Parse("resource.primary");

    [Fact]
    public void StatPipeline_ResolvesLayersInCanonicalDeterministicOrder()
    {
        var pipeline = new StatModifierPipeline(new[]
        {
            Modifier("modifier.context", StatModifierOperation.Maximum, 20m, StatModifierLayer.Contextual),
            Modifier("modifier.temporary", StatModifierOperation.Multiply, 2m, StatModifierLayer.Temporary),
            Modifier("modifier.permanent", StatModifierOperation.Add, 2m, StatModifierLayer.PermanentMatch)
        });

        var result = pipeline.Resolve(Unit(SourceId, OwnerId, 10m, 5m), AttackStatId);

        Assert.Equal(10m, result.BaseValue);
        Assert.Equal(20m, result.EffectiveValue);
        Assert.Equal(
            new[] { "modifier.permanent", "modifier.temporary", "modifier.context" },
            result.AppliedModifiers.Select(modifier => modifier.InstanceId.Value));
    }

    [Fact]
    public void StatPipeline_ClampsDefenseAtZeroAfterModifiers()
    {
        var pipeline = new StatModifierPipeline(new[]
        {
            new StatModifier(
                StableId.Parse("modifier.break_armor"),
                SourceId,
                EnemyId,
                DefenseStatId,
                StatModifierOperation.Add,
                -8m,
                StatModifierLayer.Temporary)
        });
        var target = Unit(EnemyId, OpponentId, 2m, 4m, defense: 3m);

        Assert.Equal(0m, pipeline.Resolve(target, DefenseStatId).EffectiveValue);
    }

    [Fact]
    public void FormulaAndCondition_ReadTheSameEffectiveStat()
    {
        var pipeline = new StatModifierPipeline(new[]
        {
            Modifier("modifier.buff", StatModifierOperation.Add, 2m, StatModifierLayer.Temporary)
        });
        var evaluator = new GameplayRuntimeEvaluator(statPipeline: pipeline);
        var context = CreateContext();
        var formula = FormulaExpression.Variable(StableId.Parse("variable.source.attack"));
        var condition = new PredicateConditionNode(new ConditionPredicateSpec(
            ConditionIds.StatCompare,
            new ParameterBag(new[]
            {
                Pair("subject", new SelectorParameterValue(new TargetSelectorSpec(TargetScope.Source))),
                Pair("statId", new StableIdParameterValue(AttackStatId)),
                Pair("operator", new EnumParameterValue("EQ")),
                Pair("value", new FormulaParameterValue(FormulaExpression.Constant(5m)))
            })));

        Assert.Equal(5m, evaluator.EvaluateFormula(formula, context));
        Assert.True(evaluator.EvaluateCondition(condition, context));
    }

    [Fact]
    public void EffectExecutor_DamageRequestEvaluatesAmountPerResolvedTarget()
    {
        var evaluator = new GameplayRuntimeEvaluator();
        var executor = new GameplayEffectExecutor(evaluator);
        var effect = new EffectDefinition(
            EffectIds.Damage,
            new ParameterBag(new[]
            {
                Pair("target", new SelectorParameterValue(new TargetSelectorSpec(TargetScope.Unit, TargetRelation.Enemy, TargetZone.Board))),
                Pair("amount", new FormulaParameterValue(FormulaExpression.Variable(StableId.Parse("variable.target.attack")))),
                Pair("damageType", new EnumParameterValue("PHYSICAL"))
            }));

        var result = executor.Resolve(effect, CreateContext());

        Assert.False(result.RequiresPlayerChoice);
        var operation = Assert.Single(result.Operations);
        Assert.Equal(ResolvedEffectOperationKind.DamageRequest, operation.Kind);
        Assert.Equal(EnemyId, operation.TargetId);
        Assert.Equal(2m, operation.Amount);
        Assert.Equal("PHYSICAL", operation.Qualifier);
    }

    [Fact]
    public void EffectExecutor_PlayerChoiceStopsBeforeCreatingOperations()
    {
        var evaluator = new GameplayRuntimeEvaluator();
        var executor = new GameplayEffectExecutor(evaluator);
        var effect = new EffectDefinition(
            EffectIds.Heal,
            new ParameterBag(new[]
            {
                Pair("target", new SelectorParameterValue(new TargetSelectorSpec(
                    TargetScope.Unit,
                    TargetRelation.Friendly,
                    TargetZone.Board,
                    selection: TargetSelection.PlayerChoice))),
                Pair("amount", new FormulaParameterValue(FormulaExpression.Constant(3m)))
            }));

        var result = executor.Resolve(effect, CreateContext());

        Assert.True(result.RequiresPlayerChoice);
        Assert.Empty(result.Operations);
        Assert.NotNull(result.PendingChoice);
        Assert.Equal("target", result.PendingChoice!.ParameterName);
    }

    [Fact]
    public void EffectExecutor_ConditionalResolvesOnlySelectedBranch()
    {
        var evaluator = new GameplayRuntimeEvaluator();
        var executor = new GameplayEffectExecutor(evaluator);
        var condition = new PredicateConditionNode(new ConditionPredicateSpec(
            ConditionIds.TurnCompare,
            new ParameterBag(new[]
            {
                Pair("operator", new EnumParameterValue("GTE")),
                Pair("value", new FormulaParameterValue(FormulaExpression.Constant(3m)))
            })));
        var setStat = new EffectDefinition(
            EffectIds.SetStat,
            new ParameterBag(new[]
            {
                Pair("target", new SelectorParameterValue(new TargetSelectorSpec(TargetScope.Source))),
                Pair("statId", new StableIdParameterValue(AttackStatId)),
                Pair("value", new FormulaParameterValue(FormulaExpression.Constant(9m)))
            }));
        var effect = new EffectDefinition(
            EffectIds.Conditional,
            new ParameterBag(new[]
            {
                Pair("if", new ConditionParameterValue(condition)),
                Pair("thenEffects", new EffectListParameterValue(new[] { setStat }))
            }));

        var result = executor.Resolve(effect, CreateContext());

        var operation = Assert.Single(result.Operations);
        Assert.Equal(ResolvedEffectOperationKind.SetStat, operation.Kind);
        Assert.Equal(9m, operation.Amount);
        Assert.Equal(AttackStatId, operation.PrimaryReferenceId);
    }

    private static StatModifier Modifier(
        string id,
        StatModifierOperation operation,
        decimal value,
        StatModifierLayer layer) =>
        new(StableId.Parse(id), SourceId, SourceId, AttackStatId, operation, value, layer);

    private static KeyValuePair<string, ParameterValue> Pair(string key, ParameterValue value) => new(key, value);

    private static GameplayRuntimeContext CreateContext()
    {
        var targets = new[]
        {
            new RuntimeTarget(OwnerId, RuntimeTargetKind.Player, resources: new[] { new KeyValuePair<StableId, decimal>(ResourceId, 5m) }),
            new RuntimeTarget(OpponentId, RuntimeTargetKind.Player, resources: new[] { new KeyValuePair<StableId, decimal>(ResourceId, 4m) }),
            new RuntimeTarget(StableId.Parse("lane.left"), RuntimeTargetKind.Lane, laneIndex: 0),
            new RuntimeTarget(StableId.Parse("lane.right"), RuntimeTargetKind.Lane, laneIndex: 1),
            Unit(SourceId, OwnerId, 3m, 5m, laneIndex: 0),
            Unit(EnemyId, OpponentId, 2m, 4m, laneIndex: 1)
        };
        return new GameplayRuntimeContext(
            targets,
            SourceId,
            OwnerId,
            OpponentId,
            turnNumber: 3,
            phaseId: StableId.Parse("phase.main_action"),
            laneCount: 2,
            currentLaneIndex: 0);
    }

    private static RuntimeTarget Unit(
        StableId id,
        StableId ownerId,
        decimal attack,
        decimal maxHealth,
        decimal defense = 0m,
        int laneIndex = 0) =>
        new(
            id,
            RuntimeTargetKind.Unit,
            ownerId,
            TargetZone.Board,
            laneIndex,
            CardType.Unit,
            stats: new[]
            {
                new KeyValuePair<StableId, decimal>(AttackStatId, attack),
                new KeyValuePair<StableId, decimal>(MaxHealthStatId, maxHealth),
                new KeyValuePair<StableId, decimal>(DefenseStatId, defense)
            },
            currentHealth: maxHealth);
}
