/**
 * GAMEPLAY_RUNTIME_MODELS
 * Purpose: Defines immutable runtime targets and evaluation context consumed by deterministic gameplay resolvers.
 * Connections: Shared by TargetResolver, condition predicate handlers, formula runtime context, and future effect handlers.
 * Risk: High because runtime identity, ownership, lane placement, and stat visibility drive authoritative evaluations.
 */
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using HyuHeroes.Gameplay.Core;
using HyuHeroes.Gameplay.Schema;
using HyuHeroes.Gameplay.Selectors;

namespace HyuHeroes.Gameplay.Runtime;

public enum RuntimeTargetKind
{
    Player,
    Unit,
    Hero,
    Card,
    Lane
}

public interface IDeterministicRandomSource
{
    int NextInt(int exclusiveMaximum);
}

public sealed class RuntimeTarget
{
    private readonly IReadOnlyDictionary<StableId, decimal> _resources;
    private readonly IReadOnlyDictionary<StableId, decimal> _stats;

    public RuntimeTarget(
        StableId runtimeId,
        RuntimeTargetKind kind,
        StableId? ownerId = null,
        TargetZone zone = TargetZone.Any,
        int? laneIndex = null,
        CardType? cardType = null,
        IEnumerable<StableId>? tags = null,
        IEnumerable<StableId>? keywords = null,
        IEnumerable<KeyValuePair<StableId, decimal>>? stats = null,
        IEnumerable<KeyValuePair<StableId, decimal>>? resources = null,
        decimal? currentHealth = null)
    {
        if (runtimeId == default)
        {
            throw new ArgumentException("Runtime target ID must be a non-default StableId.", nameof(runtimeId));
        }

        if (!Enum.IsDefined(typeof(RuntimeTargetKind), kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Runtime target kind is not defined.");
        }

        if (!Enum.IsDefined(typeof(TargetZone), zone))
        {
            throw new ArgumentOutOfRangeException(nameof(zone), zone, "Runtime target zone is not defined.");
        }

        if (ownerId is { } concreteOwnerId && concreteOwnerId == default)
        {
            throw new ArgumentException("Owner ID must be null or a non-default StableId.", nameof(ownerId));
        }

        if (laneIndex is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(laneIndex), "Lane index cannot be negative.");
        }

        if (kind == RuntimeTargetKind.Lane && laneIndex is null)
        {
            throw new ArgumentException("Lane targets require a lane index.", nameof(laneIndex));
        }

        RuntimeId = runtimeId;
        Kind = kind;
        OwnerId = ownerId;
        Zone = zone;
        LaneIndex = laneIndex;
        CardType = cardType;
        Tags = CopyIds(tags, nameof(tags));
        Keywords = CopyIds(keywords, nameof(keywords));
        _stats = CopyValues(stats, nameof(stats));
        _resources = CopyValues(resources, nameof(resources));
        CurrentHealth = currentHealth;
    }

    public StableId RuntimeId { get; }
    public RuntimeTargetKind Kind { get; }
    public StableId? OwnerId { get; }
    public TargetZone Zone { get; }
    public int? LaneIndex { get; }
    public CardType? CardType { get; }
    public IReadOnlyList<StableId> Tags { get; }
    public IReadOnlyList<StableId> Keywords { get; }
    public decimal? CurrentHealth { get; }
    public IReadOnlyDictionary<StableId, decimal> Stats => _stats;
    public IReadOnlyDictionary<StableId, decimal> Resources => _resources;

    public StableId? EffectiveOwnerId => Kind == RuntimeTargetKind.Player ? RuntimeId : OwnerId;

    public bool TryGetStat(StableId statId, out decimal value) => _stats.TryGetValue(statId, out value);

    public decimal GetRequiredStat(StableId statId) =>
        _stats.TryGetValue(statId, out var value)
            ? value
            : throw new KeyNotFoundException($"Runtime target '{RuntimeId}' has no stat '{statId}'.");

    public bool TryGetResource(StableId resourceId, out decimal value) => _resources.TryGetValue(resourceId, out value);

    public decimal GetRequiredResource(StableId resourceId) =>
        _resources.TryGetValue(resourceId, out var value)
            ? value
            : throw new KeyNotFoundException($"Runtime target '{RuntimeId}' has no resource '{resourceId}'.");

    public bool TryGetCurrentHealth(StableId maxHealthStatId, out decimal value)
    {
        if (CurrentHealth is { } currentHealth)
        {
            value = currentHealth;
            return true;
        }

        return _stats.TryGetValue(maxHealthStatId, out value);
    }

    private static IReadOnlyList<StableId> CopyIds(IEnumerable<StableId>? values, string parameterName)
    {
        var result = (values ?? Array.Empty<StableId>()).ToArray();
        if (result.Any(value => value == default))
        {
            throw new ArgumentException("Stable ID collections cannot contain default values.", parameterName);
        }

        return new ReadOnlyCollection<StableId>(result.Distinct().OrderBy(value => value).ToArray());
    }

    private static IReadOnlyDictionary<StableId, decimal> CopyValues(
        IEnumerable<KeyValuePair<StableId, decimal>>? values,
        string parameterName)
    {
        var dictionary = new Dictionary<StableId, decimal>();
        foreach (var pair in values ?? Array.Empty<KeyValuePair<StableId, decimal>>())
        {
            if (pair.Key == default)
            {
                throw new ArgumentException("Runtime value keys cannot be default StableIds.", parameterName);
            }

            dictionary.Add(pair.Key, pair.Value);
        }

        return new ReadOnlyDictionary<StableId, decimal>(dictionary);
    }
}

public sealed class GameplayRuntimeContext
{
    private readonly IReadOnlyDictionary<StableId, RuntimeTarget> _targetById;

    public GameplayRuntimeContext(
        IEnumerable<RuntimeTarget> targets,
        StableId sourceId,
        StableId ownerId,
        StableId opponentId,
        int turnNumber,
        StableId phaseId,
        int laneCount,
        int? currentLaneIndex = null,
        StableId? activeTargetId = null,
        IDeterministicRandomSource? randomSource = null)
        : this(
            CopyTargets(targets),
            sourceId,
            ownerId,
            opponentId,
            turnNumber,
            phaseId,
            laneCount,
            currentLaneIndex,
            activeTargetId,
            randomSource)
    {
    }

    private GameplayRuntimeContext(
        IReadOnlyList<RuntimeTarget> targets,
        StableId sourceId,
        StableId ownerId,
        StableId opponentId,
        int turnNumber,
        StableId phaseId,
        int laneCount,
        int? currentLaneIndex,
        StableId? activeTargetId,
        IDeterministicRandomSource? randomSource)
    {
        if (sourceId == default || ownerId == default || opponentId == default || phaseId == default)
        {
            throw new ArgumentException("Runtime context IDs must be non-default StableIds.");
        }

        if (ownerId == opponentId)
        {
            throw new ArgumentException("Owner and opponent IDs must be different.");
        }

        if (turnNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(turnNumber), "Turn number must be positive.");
        }

        if (laneCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(laneCount), "Lane count must be positive.");
        }

        if (currentLaneIndex is < 0 || currentLaneIndex >= laneCount)
        {
            throw new ArgumentOutOfRangeException(nameof(currentLaneIndex), "Current lane index is outside the board.");
        }

        Targets = targets;
        _targetById = new ReadOnlyDictionary<StableId, RuntimeTarget>(targets.ToDictionary(target => target.RuntimeId));
        RequireExisting(sourceId, nameof(sourceId));
        RequireExisting(ownerId, nameof(ownerId));
        RequireExisting(opponentId, nameof(opponentId));
        if (activeTargetId is { } targetId)
        {
            RequireExisting(targetId, nameof(activeTargetId));
        }

        SourceId = sourceId;
        OwnerId = ownerId;
        OpponentId = opponentId;
        TurnNumber = turnNumber;
        PhaseId = phaseId;
        LaneCount = laneCount;
        CurrentLaneIndex = currentLaneIndex;
        ActiveTargetId = activeTargetId;
        RandomSource = randomSource;
    }

    public IReadOnlyList<RuntimeTarget> Targets { get; }
    public StableId SourceId { get; }
    public StableId OwnerId { get; }
    public StableId OpponentId { get; }
    public int TurnNumber { get; }
    public StableId PhaseId { get; }
    public int LaneCount { get; }
    public int? CurrentLaneIndex { get; }
    public StableId? ActiveTargetId { get; }
    public IDeterministicRandomSource? RandomSource { get; }

    public RuntimeTarget GetRequiredTarget(StableId runtimeId) =>
        _targetById.TryGetValue(runtimeId, out var target)
            ? target
            : throw new KeyNotFoundException($"Unknown runtime target '{runtimeId}'.");

    public RuntimeTarget Source => GetRequiredTarget(SourceId);
    public RuntimeTarget Owner => GetRequiredTarget(OwnerId);
    public RuntimeTarget Opponent => GetRequiredTarget(OpponentId);

    public RuntimeTarget ActiveTarget => ActiveTargetId is { } targetId
        ? GetRequiredTarget(targetId)
        : throw new InvalidOperationException("This evaluation requires an active target.");

    public int? EffectiveCurrentLaneIndex => CurrentLaneIndex ?? Source.LaneIndex;

    public GameplayRuntimeContext WithActiveTarget(StableId runtimeId) =>
        new(
            Targets,
            SourceId,
            OwnerId,
            OpponentId,
            TurnNumber,
            PhaseId,
            LaneCount,
            CurrentLaneIndex,
            runtimeId,
            RandomSource);

    private void RequireExisting(StableId runtimeId, string parameterName)
    {
        if (!_targetById.ContainsKey(runtimeId))
        {
            throw new ArgumentException($"Runtime target '{runtimeId}' is missing from the context.", parameterName);
        }
    }

    private static IReadOnlyList<RuntimeTarget> CopyTargets(IEnumerable<RuntimeTarget> targets)
    {
        if (targets is null)
        {
            throw new ArgumentNullException(nameof(targets));
        }

        var result = targets.ToArray();
        if (result.Any(target => target is null))
        {
            throw new ArgumentException("Runtime target collection cannot contain null values.", nameof(targets));
        }

        if (result.GroupBy(target => target.RuntimeId).Any(group => group.Count() > 1))
        {
            throw new ArgumentException("Runtime target IDs must be unique.", nameof(targets));
        }

        return new ReadOnlyCollection<RuntimeTarget>(result);
    }
}
