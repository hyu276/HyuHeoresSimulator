/**
 * ABILITY_RUNTIME
 * Purpose: Defines immutable runtime ability lookup and usage-ledger rules used by deterministic trigger resolution.
 * Connections: AbilityResolutionEngine consumes the catalog and usage policy while MatchStateSnapshot persists usage records.
 * Risk: High because ability identity and usage-limit semantics decide whether triggered effects are eligible to execute.
 */
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using HyuHeroes.Gameplay.Authoring;
using HyuHeroes.Gameplay.Core;
using HyuHeroes.Gameplay.Registries;
using HyuHeroes.Gameplay.Schema;

namespace HyuHeroes.Gameplay.Abilities;

public sealed class AbilityRuntimeCatalog
{
    private readonly IReadOnlyDictionary<StableId, AbilityDefinition> _definitions;

    public AbilityRuntimeCatalog(IEnumerable<AbilityDefinition> definitions)
    {
        if (definitions is null)
        {
            throw new ArgumentNullException(nameof(definitions));
        }

        var items = definitions.ToArray();
        if (items.Any(item => item is null))
        {
            throw new ArgumentException("Ability catalog cannot contain null definitions.", nameof(definitions));
        }

        if (items.GroupBy(item => item.Header.Id).Any(group => group.Count() > 1))
        {
            throw new ArgumentException("Ability definition IDs must be unique.", nameof(definitions));
        }

        _definitions = new ReadOnlyDictionary<StableId, AbilityDefinition>(
            items.ToDictionary(item => item.Header.Id));
    }

    public AbilityDefinition GetRequired(StableId abilityId) =>
        _definitions.TryGetValue(abilityId, out var definition)
            ? definition
            : throw new KeyNotFoundException($"Unknown runtime ability '{abilityId}'.");
}

public sealed class AbilityUsageRecord
{
    public AbilityUsageRecord(
        StableId sourceId,
        StableId abilityId,
        int totalResolutions,
        int turnNumber,
        int resolutionsThisTurn,
        int? lastResolvedTurn)
    {
        if (sourceId == default || abilityId == default)
        {
            throw new ArgumentException("Ability usage source and ability IDs must be non-default StableIds.");
        }

        if (totalResolutions < 0 || resolutionsThisTurn < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalResolutions), "Ability usage counters cannot be negative.");
        }

        if (turnNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(turnNumber), "Ability usage turn number must be positive.");
        }

        if (lastResolvedTurn is <= 0 || lastResolvedTurn > turnNumber)
        {
            throw new ArgumentOutOfRangeException(nameof(lastResolvedTurn), "Last resolved turn must be null or within the recorded turn range.");
        }

        SourceId = sourceId;
        AbilityId = abilityId;
        TotalResolutions = totalResolutions;
        TurnNumber = turnNumber;
        ResolutionsThisTurn = resolutionsThisTurn;
        LastResolvedTurn = lastResolvedTurn;
    }

    public StableId SourceId { get; }
    public StableId AbilityId { get; }
    public int TotalResolutions { get; }
    public int TurnNumber { get; }
    public int ResolutionsThisTurn { get; }
    public int? LastResolvedTurn { get; }
}

public static class AbilityUsageRules
{
    public static AbilityUsageRecord? Find(
        IEnumerable<AbilityUsageRecord> records,
        StableId sourceId,
        StableId abilityId) =>
        records.FirstOrDefault(record => record.SourceId == sourceId && record.AbilityId == abilityId);

    public static bool CanResolve(UsageLimitSpec? limit, AbilityUsageRecord? record, int currentTurn)
    {
        if (limit is null)
        {
            return true;
        }

        var currentTurnCount = record?.TurnNumber == currentTurn ? record.ResolutionsThisTurn : 0;
        if (limit.TypeId == UsageLimitIds.OncePerTurn) return currentTurnCount < 1;
        if (limit.TypeId == UsageLimitIds.OncePerMatch) return (record?.TotalResolutions ?? 0) < 1;
        if (limit.TypeId == UsageLimitIds.MaxPerTurn) return currentTurnCount < GetRequiredCount(limit, "count");
        if (limit.TypeId == UsageLimitIds.CooldownTurns) return IsCooldownReady(limit, record, currentTurn);
        if (limit.TypeId == UsageLimitIds.MaxWhileInZone)
        {
            throw new NotSupportedException(
                "MAX_N_TIMES_WHILE_IN_ZONE requires a persisted zone-residency epoch and is not approximated by the current runtime.");
        }

        throw new InvalidOperationException($"Unsupported usage-limit primitive '{limit.TypeId}'.");
    }

    public static IReadOnlyList<AbilityUsageRecord> RecordResolution(
        IEnumerable<AbilityUsageRecord> records,
        StableId sourceId,
        StableId abilityId,
        int currentTurn)
    {
        var items = records.ToList();
        var existing = Find(items, sourceId, abilityId);
        var updated = new AbilityUsageRecord(
            sourceId,
            abilityId,
            (existing?.TotalResolutions ?? 0) + 1,
            currentTurn,
            existing?.TurnNumber == currentTurn ? existing.ResolutionsThisTurn + 1 : 1,
            currentTurn);
        items.RemoveAll(record => record.SourceId == sourceId && record.AbilityId == abilityId);
        items.Add(updated);
        return new ReadOnlyCollection<AbilityUsageRecord>(
            items.OrderBy(record => record.SourceId).ThenBy(record => record.AbilityId).ToArray());
    }

    private static bool IsCooldownReady(UsageLimitSpec limit, AbilityUsageRecord? record, int currentTurn)
    {
        if (record?.LastResolvedTurn is not { } lastResolvedTurn)
        {
            return true;
        }

        var cooldownTurns = GetRequiredCount(limit, "turns");
        return currentTurn > lastResolvedTurn + cooldownTurns;
    }

    private static int GetRequiredCount(UsageLimitSpec limit, string parameterName) =>
        limit.Parameters.GetRequired<IntegerParameterValue>(parameterName).Value;
}
