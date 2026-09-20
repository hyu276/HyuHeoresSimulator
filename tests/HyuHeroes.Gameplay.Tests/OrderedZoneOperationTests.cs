/**
 * ORDERED_ZONE_OPERATION_TESTS
 * Purpose: Protects authoritative deck/hand/graveyard ordering and executable DRAW, DISCARD, SUMMON, DESTROY, MOVE, and TRANSFORM mechanics.
 * Connections: Exercises MatchStateSnapshot, PlayerZoneState, StateTransitionEngine, CardRuntimeCatalog, EffectSequence/AbilityResolution, validation, and trigger discovery.
 * Risk: High because regressions would desynchronize card location, ordering, residency, replay events, or runtime card definitions.
 */
using HyuHeroes.Gameplay.Abilities;
using HyuHeroes.Gameplay.Authoring;
using HyuHeroes.Gameplay.Core;
using HyuHeroes.Gameplay.Effects;
using HyuHeroes.Gameplay.Events;
using HyuHeroes.Gameplay.Formulas;
using HyuHeroes.Gameplay.Registries;
using HyuHeroes.Gameplay.Runtime;
using HyuHeroes.Gameplay.Schema;
using HyuHeroes.Gameplay.Selectors;
using HyuHeroes.Gameplay.Simulation;
using HyuHeroes.Gameplay.Triggers;
using HyuHeroes.Gameplay.Validation;

namespace HyuHeroes.Gameplay.Tests;

public sealed class OrderedZoneOperationTests
{
    private static readonly StableId OwnerId = StableId.Parse("player.alpha");
    private static readonly StableId OpponentId = StableId.Parse("player.beta");
    private static readonly StableId SourceId = StableId.Parse("entity.source");
    private static readonly StableId EnemyId = StableId.Parse("entity.enemy");
    private static readonly StableId TopCardId = StableId.Parse("card_instance.top");
    private static readonly StableId SecondCardId = StableId.Parse("card_instance.second");
    private static readonly StableId HandCardId = StableId.Parse("card_instance.hand");
    private static readonly StableId UnitDefinitionId = StableId.Parse("card.sprout_guard");
    private static readonly StableId TransformDefinitionId = StableId.Parse("card.iron_bloom");
    private static readonly StableId AttackStatId = StableId.Parse("stat.attack");
    private static readonly StableId DefenseStatId = StableId.Parse("stat.defense");
    private static readonly StableId MaxHealthStatId = StableId.Parse("stat.max_health");
    private static readonly StableId LaneLeftId = StableId.Parse("lane.left");
    private static readonly StableId LaneMiddleId = StableId.Parse("lane.middle");
    private static readonly StableId LaneRightId = StableId.Parse("lane.right");

    [Fact]
    public void Snapshot_PreservesExplicitDeckHandAndGraveyardOrder()
    {
        var state = CreateState();

        var zones = state.GetRequiredPlayerZones(OwnerId);

        Assert.Equal(new[] { TopCardId, SecondCardId }, zones.Deck);
        Assert.Equal(new[] { HandCardId }, zones.Hand);
        Assert.Empty(zones.Graveyard);
    }

    [Fact]
    public void Draw_MovesTopCardsToHandInDeckOrder()
    {
        var state = CreateState();
        var operation = new ResolvedEffectOperation(
            ResolvedEffectOperationKind.Draw,
            EffectIds.Draw,
            SourceId,
            OwnerId,
            2m);

        var result = new StateTransitionEngine(Catalog()).Apply(state, operation);

        var zones = result.State.GetRequiredPlayerZones(OwnerId);
        Assert.Empty(zones.Deck);
        Assert.Equal(new[] { HandCardId, TopCardId, SecondCardId }, zones.Hand);
        Assert.All(new[] { TopCardId, SecondCardId }, cardId =>
            Assert.Equal(TargetZone.Hand, result.State.GetRequiredTarget(cardId).Zone));
        Assert.Collection(
            result.Events,
            item => Assert.IsType<CardDrawnDomainEvent>(item),
            item => Assert.IsType<CardDrawnDomainEvent>(item));
    }

    [Fact]
    public void Discard_AppendsHandCardToGraveyardAndAdvancesResidency()
    {
        var state = CreateState();
        var operation = new ResolvedEffectOperation(
            ResolvedEffectOperationKind.Discard,
            EffectIds.Discard,
            SourceId,
            HandCardId,
            0m);

        var result = new StateTransitionEngine(Catalog()).Apply(state, operation);

        var zones = result.State.GetRequiredPlayerZones(OwnerId);
        Assert.Empty(zones.Hand);
        Assert.Equal(new[] { HandCardId }, zones.Graveyard);
        Assert.Equal(TargetZone.Graveyard, result.State.GetRequiredTarget(HandCardId).Zone);
        Assert.Equal(2, result.State.GetRequiredTarget(HandCardId).ZoneResidencyEpoch);
        Assert.IsType<CardDiscardedDomainEvent>(Assert.Single(result.Events));
    }

    [Fact]
    public void Destroy_MovesBoardUnitToOrderedGraveyardAndEmitsDeath()
    {
        var state = CreateState();
        var operation = new ResolvedEffectOperation(
            ResolvedEffectOperationKind.Destroy,
            EffectIds.Destroy,
            SourceId,
            EnemyId,
            0m);

        var result = new StateTransitionEngine(Catalog()).Apply(state, operation);

        var enemy = result.State.GetRequiredTarget(EnemyId);
        var zones = result.State.GetRequiredPlayerZones(OpponentId);
        Assert.Equal(TargetZone.Graveyard, enemy.Zone);
        Assert.Equal(0m, enemy.CurrentHealth);
        Assert.Equal(2, enemy.ZoneResidencyEpoch);
        Assert.Equal(new[] { EnemyId }, zones.Graveyard);
        Assert.IsType<EntityDiedDomainEvent>(Assert.Single(result.Events));
    }

    [Fact]
    public void Move_HandToBoardThenAcrossBoardPreservesSameZoneEpoch()
    {
        var engine = new StateTransitionEngine(Catalog());
        var first = new ResolvedEffectOperation(
            ResolvedEffectOperationKind.Move,
            EffectIds.Move,
            SourceId,
            HandCardId,
            0m,
            qualifier: "BOTTOM",
            destinationId: LaneMiddleId,
            destinationZone: TargetZone.Board);

        var onBoard = engine.Apply(CreateState(), first).State;
        var summoned = onBoard.GetRequiredTarget(HandCardId);

        Assert.Equal(TargetZone.Board, summoned.Zone);
        Assert.Equal(1, summoned.LaneIndex);
        Assert.Equal(RuntimeTargetKind.Unit, summoned.Kind);
        Assert.Equal(2, summoned.ZoneResidencyEpoch);
        Assert.Empty(onBoard.GetRequiredPlayerZones(OwnerId).Hand);

        var second = new ResolvedEffectOperation(
            ResolvedEffectOperationKind.Move,
            EffectIds.Move,
            SourceId,
            HandCardId,
            0m,
            qualifier: "BOTTOM",
            destinationId: LaneRightId,
            destinationZone: TargetZone.Board);
        var moved = engine.Apply(onBoard, second).State.GetRequiredTarget(HandCardId);

        Assert.Equal(2, moved.LaneIndex);
        Assert.Equal(2, moved.ZoneResidencyEpoch);
    }

    [Fact]
    public void Move_ToDeckTopPreservesExplicitTopOrdering()
    {
        var state = CreateState();
        var operation = new ResolvedEffectOperation(
            ResolvedEffectOperationKind.Move,
            EffectIds.Move,
            SourceId,
            HandCardId,
            0m,
            qualifier: "TOP",
            destinationZone: TargetZone.Deck);

        var result = new StateTransitionEngine(Catalog()).Apply(state, operation);

        Assert.Equal(
            new[] { HandCardId, TopCardId, SecondCardId },
            result.State.GetRequiredPlayerZones(OwnerId).Deck);
    }

    [Fact]
    public void Summon_MaterializesDefinitionWithDeterministicRuntimeIdentity()
    {
        var state = CreateState();
        var operation = new ResolvedEffectOperation(
            ResolvedEffectOperationKind.Summon,
            EffectIds.Summon,
            SourceId,
            LaneMiddleId,
            0m,
            primaryReferenceId: UnitDefinitionId);

        var result = new StateTransitionEngine(Catalog()).Apply(state, operation);

        var entity = result.State.GetRequiredTarget(StableId.Parse("entity.runtime_00000001"));
        Assert.Equal(OwnerId, entity.OwnerId);
        Assert.Equal(UnitDefinitionId, entity.CardDefinitionId);
        Assert.Equal(TargetZone.Board, entity.Zone);
        Assert.Equal(1, entity.LaneIndex);
        Assert.Equal(4m, entity.GetRequiredStat(AttackStatId));
        Assert.Equal(9m, entity.CurrentHealth);
        Assert.Equal(2, result.State.NextEntitySequence);
        Assert.IsType<EntitySummonedDomainEvent>(Assert.Single(result.Events));
    }

    [Fact]
    public void Transform_PreservesRuntimeIdentityAndResidencyButReplacesDefinitionStats()
    {
        var state = CreateState();
        var operation = new ResolvedEffectOperation(
            ResolvedEffectOperationKind.Transform,
            EffectIds.Transform,
            SourceId,
            EnemyId,
            0m,
            primaryReferenceId: TransformDefinitionId);
        var before = state.GetRequiredTarget(EnemyId);

        var result = new StateTransitionEngine(Catalog()).Apply(state, operation);
        var after = result.State.GetRequiredTarget(EnemyId);

        Assert.Equal(before.RuntimeId, after.RuntimeId);
        Assert.Equal(before.ZoneResidencyEpoch, after.ZoneResidencyEpoch);
        Assert.Equal(TransformDefinitionId, after.CardDefinitionId);
        Assert.Equal(7m, after.GetRequiredStat(AttackStatId));
        Assert.Equal(14m, after.CurrentHealth);
        Assert.IsType<EntityTransformedDomainEvent>(Assert.Single(result.Events));
    }

    [Fact]
    public void AbilityLoop_ExecutesDrawThenSummonWithConfiguredCardCatalog()
    {
        var abilityId = StableId.Parse("ability.zone_combo");
        var draw = new EffectDefinition(
            EffectIds.Draw,
            Bag(
                Pair("target", Selector(TargetScope.Owner)),
                Pair("amount", FormulaExpression.Constant(1m))));
        var summon = new EffectDefinition(
            EffectIds.Summon,
            Bag(
                Pair("cardDefinitionId", new StableIdParameterValue(UnitDefinitionId)),
                Pair("destination", Selector(TargetScope.Lane, location: TargetLocation.CurrentLane))));
        var ability = new AbilityDefinition(
            Header(abilityId),
            new TriggerSpec(TriggerIds.OnDeath),
            new[] { draw, summon },
            usageLimit: new UsageLimitSpec(UsageLimitIds.OncePerMatch));
        var binding = new TriggerBinding(
            StableId.Parse("binding.zone_combo"),
            SourceId,
            abilityId,
            TriggerIds.OnDeath);
        var queue = new DeterministicTriggerQueue();
        queue.Enqueue(new TriggerQueueItem(new EntityDiedDomainEvent(1, SourceId), 0, binding));
        var engine = new AbilityResolutionEngine(
            new AbilityRuntimeCatalog(new[] { ability }),
            new TriggerDiscovery(new[] { binding }),
            cardCatalog: Catalog());

        var result = engine.Resolve(CreateState(sourceLaneIndex: 1), queue);

        Assert.Equal(new[] { SecondCardId }, result.State.GetRequiredPlayerZones(OwnerId).Deck);
        Assert.Equal(new[] { HandCardId, TopCardId }, result.State.GetRequiredPlayerZones(OwnerId).Hand);
        Assert.Contains(result.State.Targets, target =>
            target.RuntimeId == StableId.Parse("entity.runtime_00000001") &&
            target.Zone == TargetZone.Board &&
            target.LaneIndex == 1);
        Assert.Contains(result.Events, item => item is CardDrawnDomainEvent);
        Assert.Contains(result.Events, item => item is EntitySummonedDomainEvent);
    }

    [Fact]
    public void TriggerDiscovery_QueuesCardDrawnAndSummonedWindows()
    {
        var drawnBinding = new TriggerBinding(
            StableId.Parse("binding.drawn"),
            TopCardId,
            StableId.Parse("ability.drawn"),
            TriggerIds.OnCardDrawn);
        var summonedId = StableId.Parse("entity.runtime_00000001");
        var summonBinding = new TriggerBinding(
            StableId.Parse("binding.summoned"),
            summonedId,
            StableId.Parse("ability.summoned"),
            TriggerIds.OnSummoned);
        var discovery = new TriggerDiscovery(new[] { drawnBinding, summonBinding });

        var drawn = discovery.Discover(new CardDrawnDomainEvent(1, OwnerId, TopCardId, 1, 2));
        var summoned = discovery.Discover(new EntitySummonedDomainEvent(2, SourceId, summonedId, UnitDefinitionId, 1));

        Assert.Equal(TriggerIds.OnCardDrawn, Assert.Single(drawn).Binding.TriggerId);
        Assert.Equal(TriggerIds.OnSummoned, Assert.Single(summoned).Binding.TriggerId);
    }

    [Fact]
    public void Validator_RejectsAmbiguousOrMismatchedMoveDestinations()
    {
        var validator = new GameplaySchemaValidator(GameplayRegistryCatalog.CreateSchemaV1());
        var boardWithoutLane = MoveEffect("BOARD");
        var graveyardWithLane = MoveEffect("GRAVEYARD", Selector(TargetScope.Lane));
        var handWithPlacement = MoveEffect("HAND", placement: "TOP");
        var doubleChoice = MoveEffect(
            "BOARD",
            Selector(TargetScope.Lane, selection: TargetSelection.PlayerChoice),
            target: Selector(TargetScope.Card, selection: TargetSelection.PlayerChoice));

        Assert.Contains(
            validator.Validate(boardWithoutLane).Errors,
            error => error.Code == "effect.move_destination_required");
        Assert.Contains(
            validator.Validate(graveyardWithLane).Errors,
            error => error.Code == "effect.move_destination_invalid");
        Assert.Contains(
            validator.Validate(handWithPlacement).Errors,
            error => error.Code == "effect.move_placement_invalid");
        Assert.Contains(
            validator.Validate(doubleChoice).Errors,
            error => error.Code == "effect.move_ambiguous_choice");
    }

    private static EffectDefinition MoveEffect(
        string destinationZone,
        SelectorParameterValue? destination = null,
        string? placement = null,
        SelectorParameterValue? target = null)
    {
        var values = new List<KeyValuePair<string, ParameterValue>>
        {
            Pair("target", target ?? Selector(TargetScope.Card, selection: TargetSelection.First)),
            Pair("destinationZone", new EnumParameterValue(destinationZone))
        };
        if (destination is not null)
        {
            values.Add(Pair("destination", destination));
        }

        if (placement is not null)
        {
            values.Add(Pair("destinationPlacement", new EnumParameterValue(placement)));
        }

        return new EffectDefinition(EffectIds.Move, new ParameterBag(values));
    }

    private static MatchStateSnapshot CreateState(int sourceLaneIndex = 0)
    {
        var catalog = Catalog();
        var targets = new List<RuntimeTarget>
        {
            new(OwnerId, RuntimeTargetKind.Player),
            new(OpponentId, RuntimeTargetKind.Player),
            new(LaneLeftId, RuntimeTargetKind.Lane, laneIndex: 0),
            new(LaneMiddleId, RuntimeTargetKind.Lane, laneIndex: 1),
            new(LaneRightId, RuntimeTargetKind.Lane, laneIndex: 2),
            catalog.Materialize(SourceId, OwnerId, UnitDefinitionId, TargetZone.Board, sourceLaneIndex),
            catalog.Materialize(EnemyId, OpponentId, UnitDefinitionId, TargetZone.Board, 0),
            catalog.Materialize(TopCardId, OwnerId, UnitDefinitionId, TargetZone.Deck, null),
            catalog.Materialize(SecondCardId, OwnerId, UnitDefinitionId, TargetZone.Deck, null),
            catalog.Materialize(HandCardId, OwnerId, UnitDefinitionId, TargetZone.Hand, null)
        };
        var zones = new[]
        {
            new PlayerZoneState(OwnerId, new[] { TopCardId, SecondCardId }, new[] { HandCardId }),
            new PlayerZoneState(OpponentId)
        };
        return new MatchStateSnapshot(
            targets,
            3,
            StableId.Parse("phase.main_action"),
            3,
            playerZones: zones);
    }

    private static CardRuntimeCatalog Catalog() =>
        new(new[] { UnitDefinition(), TransformDefinition() });

    private static CardDefinition UnitDefinition() =>
        Card(UnitDefinitionId, 4m, 9m);

    private static CardDefinition TransformDefinition() =>
        Card(TransformDefinitionId, 7m, 14m);

    private static CardDefinition Card(StableId id, decimal attack, decimal health) =>
        new(
            Header(id),
            CardType.Unit,
            new Classification(
                StableId.Parse("alignment.neutral"),
                StableId.Parse("class.striker")),
            new StatBlock(new[]
            {
                new KeyValuePair<StableId, decimal>(AttackStatId, attack),
                new KeyValuePair<StableId, decimal>(DefenseStatId, 0m),
                new KeyValuePair<StableId, decimal>(MaxHealthStatId, health)
            }));

    private static GameplayDefinitionHeader Header(StableId id) =>
        new(id, 1, 1, ContentStatus.Published, "loc." + id.Value.Replace('.', '_'));

    private static ParameterBag Bag(params KeyValuePair<string, ParameterValue>[] values) => new(values);

    private static KeyValuePair<string, ParameterValue> Pair(string key, ParameterValue value) => new(key, value);

    private static KeyValuePair<string, ParameterValue> Pair(string key, FormulaExpression value) =>
        Pair(key, new FormulaParameterValue(value));

    private static SelectorParameterValue Selector(
        TargetScope scope,
        TargetRelation relation = TargetRelation.Any,
        TargetZone zone = TargetZone.Any,
        TargetLocation location = TargetLocation.Any,
        TargetSelection selection = TargetSelection.All) =>
        new(new TargetSelectorSpec(scope, relation, zone, location, selection));
}
