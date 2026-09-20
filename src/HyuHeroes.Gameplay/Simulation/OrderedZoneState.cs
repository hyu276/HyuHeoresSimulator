/**
 * ORDERED_ZONE_STATE
 * Purpose: Stores deterministic per-player ordering for deck, hand, and graveyard while RuntimeTarget remains authoritative for entity identity and zone.
 * Connections: MatchStateSnapshot owns these states; StateTransitionEngine and LifecycleTransitionEngine update them when cards move between zones.
 * Risk: High because ordering determines top-deck draws, replay-visible hand order, and graveyard sequence.
 */
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using HyuHeroes.Gameplay.Core;
using HyuHeroes.Gameplay.Runtime;
using HyuHeroes.Gameplay.Selectors;

namespace HyuHeroes.Gameplay.Simulation;

public enum OrderedZoneInsertPosition
{
    Top,
    Bottom
}

public sealed class PlayerZoneState
{
    public PlayerZoneState(
        StableId playerId,
        IEnumerable<StableId>? deck = null,
        IEnumerable<StableId>? hand = null,
        IEnumerable<StableId>? graveyard = null)
    {
        if (playerId == default)
        {
            throw new ArgumentException("Player zone owner must be a non-default StableId.", nameof(playerId));
        }

        PlayerId = playerId;
        Deck = CopyDistinct(deck, nameof(deck));
        Hand = CopyDistinct(hand, nameof(hand));
        Graveyard = CopyDistinct(graveyard, nameof(graveyard));

        var all = Deck.Concat(Hand).Concat(Graveyard).ToArray();
        if (all.Distinct().Count() != all.Length)
        {
            throw new ArgumentException("A card instance cannot appear in multiple ordered zones for the same player.");
        }
    }

    public StableId PlayerId { get; }
    public IReadOnlyList<StableId> Deck { get; }
    public IReadOnlyList<StableId> Hand { get; }
    public IReadOnlyList<StableId> Graveyard { get; }

    public IReadOnlyList<StableId> GetOrderedZone(TargetZone zone) =>
        zone switch
        {
            TargetZone.Deck => Deck,
            TargetZone.Hand => Hand,
            TargetZone.Graveyard => Graveyard,
            _ => throw new InvalidOperationException($"Zone '{zone}' is not an ordered player zone.")
        };

    public PlayerZoneState WithOrderedZone(TargetZone zone, IEnumerable<StableId> values) =>
        zone switch
        {
            TargetZone.Deck => new PlayerZoneState(PlayerId, values, Hand, Graveyard),
            TargetZone.Hand => new PlayerZoneState(PlayerId, Deck, values, Graveyard),
            TargetZone.Graveyard => new PlayerZoneState(PlayerId, Deck, Hand, values),
            _ => throw new InvalidOperationException($"Zone '{zone}' is not an ordered player zone.")
        };

    private static IReadOnlyList<StableId> CopyDistinct(IEnumerable<StableId>? values, string parameterName)
    {
        var array = (values ?? Array.Empty<StableId>()).ToArray();
        if (array.Any(value => value == default))
        {
            throw new ArgumentException("Ordered zone IDs cannot contain default StableIds.", parameterName);
        }

        if (array.Distinct().Count() != array.Length)
        {
            throw new ArgumentException("Ordered zone IDs must be unique.", parameterName);
        }

        return new ReadOnlyCollection<StableId>(array);
    }
}

internal static class OrderedZoneStateRules
{
    public static IReadOnlyList<PlayerZoneState> CreateOrValidate(
        IReadOnlyList<RuntimeTarget> targets,
        IEnumerable<PlayerZoneState>? supplied)
    {
        if (targets is null) throw new ArgumentNullException(nameof(targets));

        var playerIds = targets
            .Where(target => target.Kind == RuntimeTargetKind.Player)
            .Select(target => target.RuntimeId)
            .OrderBy(id => id)
            .ToArray();

        if (supplied is null)
        {
            return Derive(targets, playerIds);
        }

        var states = supplied.ToArray();
        if (states.Any(state => state is null))
        {
            throw new ArgumentException("Player zone states cannot contain null values.", nameof(supplied));
        }

        if (states.GroupBy(state => state.PlayerId).Any(group => group.Count() > 1))
        {
            throw new ArgumentException("Player zone states must be unique per player.", nameof(supplied));
        }

        if (!states.Select(state => state.PlayerId).OrderBy(id => id).SequenceEqual(playerIds))
        {
            throw new ArgumentException("Player zone states must contain exactly one entry for every runtime player.", nameof(supplied));
        }

        ValidateMembership(targets, states);
        return new ReadOnlyCollection<PlayerZoneState>(states.OrderBy(state => state.PlayerId).ToArray());
    }

    public static IReadOnlyList<PlayerZoneState> Move(
        IReadOnlyList<PlayerZoneState> states,
        RuntimeTarget target,
        TargetZone destinationZone,
        OrderedZoneInsertPosition insertPosition)
    {
        if (states is null) throw new ArgumentNullException(nameof(states));
        if (target is null) throw new ArgumentNullException(nameof(target));
        if (target.OwnerId is not { } ownerId)
        {
            throw new InvalidOperationException($"Target '{target.RuntimeId}' has no owner and cannot participate in player ordered zones.");
        }

        var updated = states.ToArray();
        var stateIndex = Array.FindIndex(updated, state => state.PlayerId == ownerId);
        if (stateIndex < 0)
        {
            throw new InvalidOperationException($"Player zone state '{ownerId}' does not exist.");
        }

        var ownerState = updated[stateIndex];
        if (IsOrdered(target.Zone))
        {
            ownerState = ownerState.WithOrderedZone(
                target.Zone,
                ownerState.GetOrderedZone(target.Zone).Where(id => id != target.RuntimeId));
        }

        if (IsOrdered(destinationZone))
        {
            var destination = ownerState.GetOrderedZone(destinationZone).ToList();
            if (insertPosition == OrderedZoneInsertPosition.Top)
            {
                destination.Insert(0, target.RuntimeId);
            }
            else
            {
                destination.Add(target.RuntimeId);
            }

            ownerState = ownerState.WithOrderedZone(destinationZone, destination);
        }

        updated[stateIndex] = ownerState;
        return new ReadOnlyCollection<PlayerZoneState>(updated);
    }

    public static bool IsOrdered(TargetZone zone) =>
        zone == TargetZone.Deck || zone == TargetZone.Hand || zone == TargetZone.Graveyard;

    private static IReadOnlyList<PlayerZoneState> Derive(
        IReadOnlyList<RuntimeTarget> targets,
        IReadOnlyList<StableId> playerIds)
    {
        var states = playerIds
            .Select(playerId => new PlayerZoneState(
                playerId,
                targets.Where(target => target.OwnerId == playerId && target.Zone == TargetZone.Deck).Select(target => target.RuntimeId),
                targets.Where(target => target.OwnerId == playerId && target.Zone == TargetZone.Hand).Select(target => target.RuntimeId),
                targets.Where(target => target.OwnerId == playerId && target.Zone == TargetZone.Graveyard).Select(target => target.RuntimeId)))
            .ToArray();

        ValidateMembership(targets, states);
        return new ReadOnlyCollection<PlayerZoneState>(states);
    }

    private static void ValidateMembership(
        IReadOnlyList<RuntimeTarget> targets,
        IReadOnlyList<PlayerZoneState> states)
    {
        var targetById = targets.ToDictionary(target => target.RuntimeId);
        var listed = new HashSet<StableId>();

        foreach (var state in states)
        {
            ValidateZone(state, TargetZone.Deck, state.Deck, targetById, listed);
            ValidateZone(state, TargetZone.Hand, state.Hand, targetById, listed);
            ValidateZone(state, TargetZone.Graveyard, state.Graveyard, targetById, listed);
        }

        var expected = targets
            .Where(target => OrderedZoneStateRules.IsOrdered(target.Zone))
            .Select(target => target.RuntimeId)
            .ToHashSet();

        if (!expected.SetEquals(listed))
        {
            throw new ArgumentException("Ordered player zones must reference every runtime target in deck, hand, or graveyard exactly once.");
        }
    }

    private static void ValidateZone(
        PlayerZoneState state,
        TargetZone zone,
        IReadOnlyList<StableId> ids,
        IReadOnlyDictionary<StableId, RuntimeTarget> targetById,
        ISet<StableId> listed)
    {
        foreach (var id in ids)
        {
            if (!targetById.TryGetValue(id, out var target))
            {
                throw new ArgumentException($"Ordered zone references unknown target '{id}'.");
            }

            if (target.OwnerId != state.PlayerId || target.Zone != zone)
            {
                throw new ArgumentException($"Ordered zone target '{id}' does not match player '{state.PlayerId}' and zone '{zone}'.");
            }

            if (!listed.Add(id))
            {
                throw new ArgumentException($"Ordered zone target '{id}' is listed more than once.");
            }
        }
    }
}
