/**
 * DAMAGE_PIPELINE_TESTS
 * Purpose: Verifies canonical damage ordering, effective defense, prevention, overkill, and integration from declarative DAMAGE effects.
 * Connections: Exercises GameplayEffectExecutor, DamagePipeline, StatModifierPipeline, formula evaluation, and immutable runtime targets.
 * Risk: High because these tests protect the single authoritative damage calculation path used by future combat and abilities.
 */
using HyuHeroes.Gameplay.Authoring;
using HyuHeroes.Gameplay.Combat;
using HyuHeroes.Gameplay.Core;
using HyuHeroes.Gameplay.Effects;
using HyuHeroes.Gameplay.Formulas;
using HyuHeroes.Gameplay.Modifiers;
using HyuHeroes.Gameplay.Runtime;
using HyuHeroes.Gameplay.Schema;
using HyuHeroes.Gameplay.Selectors;

namespace HyuHeroes.Gameplay.Tests;

public sealed class DamagePipelineTests
{
    private static readonly StableId OwnerId = StableId.Parse("player.alpha");
    private static readonly StableId OpponentId = StableId.Parse("player.beta");
    private static readonly StableId SourceId = StableId.Parse("entity.source");
    private static readonly StableId TargetId = StableId.Parse("entity.target");
    private static readonly StableId DefenseStatId = StableId.Parse("stat.defense");
    private static readonly StableId AttackStatId = StableId.Parse("stat.attack");
    private static readonly StableId MaxHealthStatId = StableId.Parse("stat.max_health");
    private static readonly StableId ResourceId = StableId.Parse("resource.primary");

    [Fact]
    public void PhysicalDamage_UsesEffectiveDefenseAndReportsOverkill()
    {
        var statPipeline = new StatModifierPipeline(new[]
        {
            new StatModifier(
                StableId.Parse("modifier.armor_buff"),
                SourceId,
                TargetId,
                DefenseStatId,
                StatModifierOperation.Add,
                2m,
                StatModifierLayer.Temporary)
        });
        var evaluator = new GameplayRuntimeEvaluator(statPipeline: statPipeline);
        var pipeline = new DamagePipeline(evaluator);

        var result = pipeline.Resolve(DamageOperation(10m, "PHYSICAL"), CreateContext());

        Assert.Equal(5m, result.DefensePrevented);
        Assert.Equal(5m, result.FinalDamage);
        Assert.Equal(4m, result.HealthLost);
        Assert.Equal(1m, result.Overkill);
        Assert.Equal(0m, result.ResultingHealth);
        Assert.True(result.WouldDie);
    }

    [Fact]
    public void ArcaneDamage_DoesNotInventAResistanceStat()
    {
        var evaluator = new GameplayRuntimeEvaluator();
        var pipeline = new DamagePipeline(evaluator);

        var result = pipeline.Resolve(DamageOperation(6m, "ARCANE"), CreateContext());

        Assert.Equal(0m, result.DefensePrevented);
        Assert.Equal(6m, result.FinalDamage);
    }

    [Fact]
    public void TrueDamage_BypassesDefenseButStillUsesExplicitPrevention()
    {
        var evaluator = new GameplayRuntimeEvaluator();
        var prevention = new DamagePrevention(StableId.Parse("prevention.shield"), TargetId, 4m);
        var pipeline = new DamagePipeline(evaluator, preventions: new[] { prevention });

        var result = pipeline.Resolve(DamageOperation(10m, "TRUE"), CreateContext());

        Assert.Equal(0m, result.DefensePrevented);
        Assert.Equal(4m, result.PreventionPrevented);
        Assert.Equal(6m, result.FinalDamage);
        Assert.Equal("prevention.shield", Assert.Single(result.AppliedPreventions).PreventionId.Value);
    }

    [Fact]
    public void DamageAdjustments_FollowSourceTypeTargetThenDefenseThenPrevention()
    {
        var evaluator = new GameplayRuntimeEvaluator();
        var adjustments = new[]
        {
            new DamageAdjustment(StableId.Parse("damage.source_bonus"), DamageAdjustmentStage.Source, DamageAdjustmentOperation.Add, 2m, sourceId: SourceId),
            new DamageAdjustment(StableId.Parse("damage.physical_amp"), DamageAdjustmentStage.DamageType, DamageAdjustmentOperation.Multiply, 2m, damageType: DamageType.Physical),
            new DamageAdjustment(StableId.Parse("damage.target_cap"), DamageAdjustmentStage.Target, DamageAdjustmentOperation.Maximum, 20m, targetId: TargetId)
        };
        var preventions = new[]
        {
            new DamagePrevention(StableId.Parse("prevention.first"), TargetId, 3m),
            new DamagePrevention(StableId.Parse("prevention.second"), TargetId, 4m)
        };
        var pipeline = new DamagePipeline(evaluator, adjustments, preventions);

        var result = pipeline.Resolve(DamageOperation(10m, "PHYSICAL"), CreateContext());

        Assert.Equal(12m, result.AfterSourceAdjustments);
        Assert.Equal(24m, result.AfterTypeAdjustments);
        Assert.Equal(20m, result.AfterTargetAdjustments);
        Assert.Equal(3m, result.DefensePrevented);
        Assert.Equal(7m, result.PreventionPrevented);
        Assert.Equal(10m, result.FinalDamage);
        Assert.Equal(
            new[] { "damage.source_bonus", "damage.physical_amp", "damage.target_cap" },
            result.AppliedAdjustments.Select(adjustment => adjustment.InstanceId.Value));
        Assert.Equal(
            new[] { "prevention.first", "prevention.second" },
            result.AppliedPreventions.Select(prevention => prevention.PreventionId.Value));
    }

    [Fact]
    public void DeclarativeDamageEffect_FlowsThroughEffectExecutorIntoDamagePipeline()
    {
        var evaluator = new GameplayRuntimeEvaluator();
        var executor = new GameplayEffectExecutor(evaluator);
        var effect = new EffectDefinition(
            EffectIds.Damage,
            new ParameterBag(new[]
            {
                Pair("target", new SelectorParameterValue(new TargetSelectorSpec(TargetScope.Unit, TargetRelation.Enemy, TargetZone.Board))),
                Pair("amount", new FormulaParameterValue(FormulaExpression.Constant(8m))),
                Pair("damageType", new EnumParameterValue("PHYSICAL"))
            }));
        var context = CreateContext();

        var planned = executor.Resolve(effect, context);
        var resolved = new DamagePipeline(evaluator).Resolve(Assert.Single(planned.Operations), context);

        Assert.Equal(5m, resolved.FinalDamage);
        Assert.Equal(DamageType.Physical, resolved.DamageType);
    }

    [Fact]
    public void DamagePipeline_RejectsUnknownDamageType()
    {
        var evaluator = new GameplayRuntimeEvaluator();
        var pipeline = new DamagePipeline(evaluator);

        Assert.Throws<InvalidOperationException>(() => pipeline.Resolve(DamageOperation(3m, "CHAOS"), CreateContext()));
    }

    private static ResolvedEffectOperation DamageOperation(decimal amount, string damageType) =>
        new(
            ResolvedEffectOperationKind.DamageRequest,
            EffectIds.Damage,
            SourceId,
            TargetId,
            amount,
            qualifier: damageType);

    private static KeyValuePair<string, ParameterValue> Pair(string key, ParameterValue value) => new(key, value);

    private static GameplayRuntimeContext CreateContext()
    {
        var targets = new[]
        {
            new RuntimeTarget(OwnerId, RuntimeTargetKind.Player, resources: new[] { new KeyValuePair<StableId, decimal>(ResourceId, 5m) }),
            new RuntimeTarget(OpponentId, RuntimeTargetKind.Player, resources: new[] { new KeyValuePair<StableId, decimal>(ResourceId, 5m) }),
            new RuntimeTarget(StableId.Parse("lane.left"), RuntimeTargetKind.Lane, laneIndex: 0),
            new RuntimeTarget(SourceId, RuntimeTargetKind.Unit, OwnerId, TargetZone.Board, 0, CardType.Unit,
                stats: new[]
                {
                    new KeyValuePair<StableId, decimal>(AttackStatId, 3m),
                    new KeyValuePair<StableId, decimal>(MaxHealthStatId, 5m),
                    new KeyValuePair<StableId, decimal>(DefenseStatId, 0m)
                }, currentHealth: 5m),
            new RuntimeTarget(TargetId, RuntimeTargetKind.Unit, OpponentId, TargetZone.Board, 0, CardType.Unit,
                stats: new[]
                {
                    new KeyValuePair<StableId, decimal>(AttackStatId, 2m),
                    new KeyValuePair<StableId, decimal>(MaxHealthStatId, 4m),
                    new KeyValuePair<StableId, decimal>(DefenseStatId, 3m)
                }, currentHealth: 4m)
        };
        return new GameplayRuntimeContext(
            targets,
            SourceId,
            OwnerId,
            OpponentId,
            turnNumber: 3,
            phaseId: StableId.Parse("phase.main_action"),
            laneCount: 1,
            currentLaneIndex: 0);
    }
}
