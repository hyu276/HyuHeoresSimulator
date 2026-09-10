/**
 * STABLE_ID
 * Purpose: Provides validated immutable identifiers for gameplay content and primitive registry keys.
 * Connections: Used by schemas, registries, formulas, conditions, selectors, and effects.
 * Risk: High because identifier drift would break persistence, replay, and content references.
 */
using System;
using System.Text.RegularExpressions;

namespace HyuHeroes.Gameplay.Core;

public readonly struct StableId : IEquatable<StableId>, IComparable<StableId>
{
    private static readonly Regex Pattern = new(
        "^[a-z][a-z0-9_]*(\\.[a-z][a-z0-9_]*)+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public StableId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Stable ID cannot be empty.", nameof(value));
        }

        if (!Pattern.IsMatch(value))
        {
            throw new ArgumentException(
                "Stable ID must be namespaced lowercase text such as 'card.ember_fox'.",
                nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public static StableId Parse(string value) => new(value);

    public static bool TryParse(string? value, out StableId stableId)
    {
        if (!string.IsNullOrWhiteSpace(value) && Pattern.IsMatch(value))
        {
            stableId = new StableId(value);
            return true;
        }

        stableId = default;
        return false;
    }

    public bool Equals(StableId other) => StringComparer.Ordinal.Equals(Value, other.Value);

    public override bool Equals(object? obj) => obj is StableId other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value ?? string.Empty);

    public int CompareTo(StableId other) => StringComparer.Ordinal.Compare(Value, other.Value);

    public override string ToString() => Value ?? string.Empty;

    public static bool operator ==(StableId left, StableId right) => left.Equals(right);

    public static bool operator !=(StableId left, StableId right) => !left.Equals(right);
}
