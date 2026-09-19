/**
 * CARD_RUNTIME_CATALOG
 * Purpose: Materializes immutable card definitions into deterministic runtime card/unit instances and applies definition-preserving transforms.
 * Connections: StateTransitionEngine uses this catalog for SUMMON and TRANSFORM while content loading will provide the published CardDefinition set.
 * Risk: High because instance materialization controls runtime stats, tags, health, and definition identity.
 */
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using HyuHeroes.Gameplay.Core;
using HyuHeroes.Gameplay.Schema;
using HyuHeroes.Gameplay.Selectors;

namespace HyuHeroes.Gameplay.Runtime;

public sealed class CardRuntimeCatalog
{
    private static readonly StableId MaxHealthStatId = StableId.Parse("stat.max_health");
    private readonly IReadOnlyDictionary<StableId, CardDefinition> _definitions;

    public CardRuntimeCatalog(IEnumerable<CardDefinition> definitions)
    {
        if (definitions is null) throw new ArgumentNullException(nameof(definitions));
        var items = definitions.ToArray();
        if (items.Any(item => item is null))
        {
            throw new ArgumentException("Card runtime catalog cannot contain null definitions.", nameof(definitions));
        }

        if (items.GroupBy(item => item.Header.Id).Any(group => group.Count() > 1))
        {
            throw new ArgumentException("Card definition IDs must be unique.", nameof(definitions));
        }

        _definitions = new ReadOnlyDictionary<StableId, CardDefinition>(
            items.ToDictionary(item => item.Header.Id));
    }

    public CardDefinition GetRequired(StableId definitionId) =>
        _definitions.TryGetValue(definitionId, out var definition)
            ? definition
            : throw new KeyNotFoundException($"Unknown runtime card definition '{definitionId}'.");

    public RuntimeTarget Materialize(
        StableId runtimeId,
        StableId ownerId,
        StableId definitionId,
        TargetZone zone,
        int? laneIndex,
        long zoneResidencyEpoch = 1)
    {
        if (runtimeId == default || ownerId == default || definitionId == default)
        {
            throw new ArgumentException("Runtime, owner, and definition IDs must be non-default StableIds.");
        }

        var definition = GetRequired(definitionId);
        ValidateZoneCompatibility(definition, zone, laneIndex);
        var currentHealth = ResolveInitialHealth(definition, zone);
        return new RuntimeTarget(
            runtimeId,
            ResolveRuntimeKind(definition, zone),
            ownerId,
            zone,
            laneIndex,
            definition.CardType,
            definition.Classification.Tags,
            definition.Classification.Keywords,
            definition.BaseStats.Values,
            currentHealth: currentHealth,
            zoneResidencyEpoch: zoneResidencyEpoch,
            cardDefinitionId: definitionId);
    }

    public RuntimeTarget Transform(RuntimeTarget target, StableId definitionId)
    {
        if (target is null) throw new ArgumentNullException(nameof(target));
        if (definitionId == default) throw new ArgumentException("Definition ID must be a non-default StableId.", nameof(definitionId));
        if (target.OwnerId is not { } ownerId)
        {
            throw new InvalidOperationException($"Target '{target.RuntimeId}' has no owner and cannot be transformed as a card instance.");
        }

        var definition = GetRequired(definitionId);
        ValidateZoneCompatibility(definition, target.Zone, target.LaneIndex);
        return new RuntimeTarget(
            target.RuntimeId,
            ResolveRuntimeKind(definition, target.Zone),
            ownerId,
            target.Zone,
            target.LaneIndex,
            definition.CardType,
            definition.Classification.Tags,
            definition.Classification.Keywords,
            definition.BaseStats.Values,
            currentHealth: ResolveInitialHealth(definition, target.Zone),
            zoneResidencyEpoch: target.ZoneResidencyEpoch,
            cardDefinitionId: definitionId);
    }

    private static RuntimeTargetKind ResolveRuntimeKind(CardDefinition definition, TargetZone zone) =>
        zone == TargetZone.Board && definition.CardType == CardType.Unit
            ? RuntimeTargetKind.Unit
            : RuntimeTargetKind.Card;

    private static decimal? ResolveInitialHealth(CardDefinition definition, TargetZone zone)
    {
        if (zone != TargetZone.Board || definition.CardType != CardType.Unit)
        {
            return null;
        }

        return definition.BaseStats.Values.TryGetValue(MaxHealthStatId, out var maxHealth)
            ? maxHealth
            : throw new InvalidOperationException($"Board unit definition '{definition.Header.Id}' requires stat.max_health.");
    }

    private static void ValidateZoneCompatibility(CardDefinition definition, TargetZone zone, int? laneIndex)
    {
        if (!Enum.IsDefined(typeof(TargetZone), zone) || zone == TargetZone.Any)
        {
            throw new ArgumentOutOfRangeException(nameof(zone), "Card instances require a concrete runtime zone.");
        }

        if (zone == TargetZone.Board)
        {
            if (definition.CardType != CardType.Unit)
            {
                throw new InvalidOperationException($"Only UNIT definitions may enter the lane board in schema v1; '{definition.Header.Id}' is '{definition.CardType}'.");
            }

            if (laneIndex is null)
            {
                throw new ArgumentException("Board card instances require a lane index.", nameof(laneIndex));
            }

            return;
        }

        if (laneIndex is not null)
        {
            throw new ArgumentException("Non-board card instances cannot carry a lane index.", nameof(laneIndex));
        }
    }
}
