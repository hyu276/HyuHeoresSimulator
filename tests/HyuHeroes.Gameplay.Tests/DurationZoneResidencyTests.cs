/**
 * DURATION_ZONE_RESIDENCY_TESTS
 * Purpose: Protects deterministic modifier expiry checkpoints, zone-residency epochs, and residency-scoped ability limits.
 * Connections: Exercises StateTransitionEngine, LifecycleTransitionEngine, StatModifierDurationState, AbilityUsageRules, and publication validation.
 * Risk: High because regressions would make buffs or usage limits survive or reset at incorrect authoritative timing boundaries.
 */
using HyuHeroes.Gameplay.Abilities;
using HyuHeroes.Gameplay.Authoring;
using HyuHeroes.Gameplay.Core;
using HyuHeroes.Gameplay.Effects;
using HyuHeroes.Gameplay.Formulas;
using HyuHeroes.Gameplay.Registries;
using HyuHeroes.Gameplay.Runtime;
using HyuHeroes.Gameplay.Schema;
using HyuHeroes.Gameplay.Selectors;
using HyuHeroes.Gameplay.Simulation;
using HyuHeroes.Gameplay.Validation;

namespace HyuHeroes.Gameplay.Tests;

public sealed class DurationZoneResidencyTests
{
    private static readonly StableId OwnerId = StableId.Parse("player.alpha");
    private static readonly StableId OpponentId = StableId.Parse("player.beta");
    private static readonly StableId SourceId = StableId.Parse("entity.source");
    private static readonly StableId TargetId = StableId.Parse("entity.target");
    private static readonly StableId AttackerId = StableId.Parse("entity.attacker");
    private static readonly StableId AttackStatId = StableId.Parse("stat.attack");
    private static readonly StableId DefenseStatId = StableId.Parse("stat.defense");
    private static readonly StableId MaxHealthStatId = StableId.Parse("stat.max_health");

    [Fact]
    public void CompleteEndOfTurn_ExpiresUntilEndOfTurnModifier()
    {
        var state = AddModifier(CreateState(), DurationIds.UntilEndOfTurn);
        var endTurn = state.With(phaseId: StableId.Parse("phase.end_turn"));

        var result = new LifecycleTransitionEngine().CompleteEndOfTurn(endTurn);

        Assert.Empty(result.State.StatModifiers);
        Assert.Contains(result.Events, item => item.TypeId == StableId.Parse("event.modifier_expired"));
    }

    [Fact]
    public void StartNextTurn_ExpiresUntilStartAndForNTurnsAtConfiguredTurn()
    {
        var state = AddModifier(CreateState(), DurationIds.UntilStartOfNextTurn);
        state = AddModifier(state, DurationIds.ForNTurns, turns: 2);
        var lifecycle = new LifecycleTransitionEngine();

        var turnFour = lifecycle.StartNextTurn(state.With(phaseId: StableId.Parse("phase.end_turn"))).State;

        Assert.Equal(4, turnFour.TurnNumber);
        Assert.Single(turnFour.StatModifiers);
        Assert.Equal(DurationIds.ForNTurns, turnFour.StatModifiers[0].DurationId);

        var turnFive = lifecycle.StartNextTurn(turnFour.With(phaseId: StableId.Parse("phase.end_turn"))).State;

        Assert.Equal(5, turnFive.TurnNumber);
        Assert.Empty(turnFive.StatModifiers);
    }

    [Fact]
    public void WhileSourceExists_ExpiresWhenSourceLeavesItsResidencyOnDeath()
    {
        var state = AddModifier(CreateState(), DurationIds.WhileSourceExists);
        var lethal = new ResolvedEffectOperation(
            ResolvedEffectOperationKind.DamageRequest,
            EffectIds.Damage,
            AttackerId,
            SourceId,
            99m,
            qualifier: "TRUE");

        var result = new StateTransitionEngine().Apply(state, lethal);

        Assert.Equal(TargetZone.Graveyard, result.State.GetRequiredTarget(SourceId).Zone);
        Assert.Equal(2, result.State.GetRequiredTarget(SourceId).ZoneResidencyEpoch);
        Assert.Empty(result.State.StatModifiers);
        Assert.Contains(result.Events, item => item.TypeId == StableId.Parse("event.modifier_expired"));
    }

    [Fact]
    public void WhileInZone_ExpiresOnLeaveAndReentryCreatesNewEpoch()
    {
        var state = AddModifier(CreateState(), DurationIds.WhileInZone, zone: "BOARD");
        var lifecycle = new LifecycleTransitionEngine();

        var graveyard = lifecycle.ChangeZone(state, TargetId, TargetZone.Graveyard).State;

        Assert.Empty(graveyard.StatModifiers);
        Assert.Equal(2, graveyard.GetRequiredTarget(TargetId).ZoneResidencyEpoch);

        var returned = lifecycle.ChangeZone(graveyard, TargetId, TargetZone.Board, 0).State;

        Assert.Equal(TargetZone.Board, returned.GetRequiredTarget(TargetId).Zone);
        Assert.Equal(3, returned.GetRequiredTarget(TargetId).ZoneResidencyEpoch);
    }

    [Fact]
    public void MaxWhileInZone_ResetsWhenSourceResidencyEpochChanges()
    {
        var limit = new UsageLimitSpec(
            UsageLimitIds.MaxWhileInZone,
            Bag(Pair("count", new IntegerParameterValue(2))));
        IReadOnlyList<AbilityUsageRecord> records = Array.Empty<AbilityUsageRecord>();
        var abilityId = StableId.Parse("ability.residency_limit");
        records = AbilityUsageRules.RecordResolution(records, SourceId, abilityId, 3, 1);
        records = AbilityUsageRules.RecordResolution(records, SourceId, abilityId, 3, 1);
        var record = Assert.Single(records);

        Assert.False(AbilityUsageRules.CanResolve(limit, record, 3, 1));
        Assert.True(AbilityUsageRules.CanResolve(limit, record, 3, 2));

        records = AbilityUsageRules.RecordResolution(records, SourceId, abilityId, 3, 2);
        record = Assert.Single(records);
        Assert.Equal(1, record.ResolutionsThisResidency);
        Assert.Equal(2, record.ZoneResidencyEpoch);
    }

    [Fact]
    public void Validator_RequiresDependentDurationParameters()
    {
        var validator = new GameplaySchemaValidator(GameplayRegistryCatalog.CreateSchemaV1());
        var missingTurns = ModifyStatEffect(DurationIds.ForNTurns);
        var invalidZone = ModifyStatEffect(DurationIds.UntilEndOfTurn, zone: "BOARD");

        var missingTurnsResult = validator.Validate(missingTurns);
        var invalidZoneResult = validator.Validate(invalidZone);

        Assert.Contains(missingTurnsResult.Errors, error => error.Code == "effect.duration_parameter_required");
        Assert.Contains(invalidZoneResult.Errors, error => error.Code == "effect.duration_parameter_invalid");
    }

    private static MatchStateSnapshot AddModifier(
        MatchStateSnapshot state,
        StableId durationId,
        int? turns = null,
        string? zone = null)
    {
        var durationParameters = new List<KeyValuePair<string, ParameterValue>>();
        if (turns is { } configuredTurns)
        {
            durationParameters.Add(Pair("turns", new IntegerParameterValue(configuredTurns)));
        }

        if (zone is not null)
        {
            durationParameters.Add(Pair("zone", new EnumParameterValue(zone)));
        }

        var operation = new ResolvedEffectOperation(
            ResolvedEffectOperationKind.AddStatModifier,
            EffectIds.ModifyStat,
            SourceId,
            TargetId,
            2m,
            AttackStatId,
            durationId,
            "ADD",
            new DurationSpec(durationId, new ParameterBag(durationParameters)));
        return new StateTransitionEngine().Apply(state, operation).State;
    }

    private static EffectDefinition ModifyStatEffect(
        StableId durationId,
        int? turns = null,
        string? zone = null)
    {
        var values = new List<KeyValuePair<string, ParameterValue>>
        {
            Pair("target", new SelectorParameterValue(new TargetSelectorSpec(TargetScope.Source))),
            Pair("statId", new StableIdParameterValue(AttackStatId)),
            Pair("operation", new EnumParameterValue("ADD")),
            Pair("value", new FormulaParameterValue(FormulaExpression.Constant(1m))),
            Pair("durationId", new StableIdParameterValue(durationId))
        };
        if (turns is { } configuredTurns)
        {
            values.Add(Pair("durationTurns", new IntegerParameterValue(configuredTurns)));
        }

        if (zone is not null)
        {
            values.Add(Pair("durationZone", new EnumParameterValue(zone)));
        }

        return new EffectDefinition(EffectIds.ModifyStat, new ParameterBag(values));
    }

    private static MatchStateSnapshot CreateState()
    {
        var targets = new[]
        {
            new RuntimeTarget(OwnerId, RuntimeTargetKind.Player),
            new RuntimeTarget(OpponentId, RuntimeTargetKind.Player),
            Unit(SourceId, OwnerId, 3m, 20m, 0m, 0, 20m),
            Unit(TargetId, OwnerId, 2m, 20m, 0m, 0, 20m),
            Unit(AttackerId, OpponentId, 4m, 20m, 0m, 0, 20m),
            new RuntimeTarget(StableId.Parse("lane.left"), RuntimeTargetKind.Lane, laneIndex: 0)
        };
        return new MatchStateSnapshot(targets, 3, StableId.Parse("phase.main_action"), 1);
    }

    private static RuntimeTarget Unit(
        StableId id,
        StableId ownerId,
        decimal attack,
        decimal maxHealth,
        decimal defense,
        int laneIndex,
        decimal currentHealth) =>
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
            currentHealth: currentHealth);

    private static ParameterBag Bag(params KeyValuePair<string, ParameterValue>[] values) => new(values);

    private static KeyValuePair<string, ParameterValue> Pair(string key, ParameterValue value) => new(key, value);
}
