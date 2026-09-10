/**
 * STABLE_ID_AND_REGISTRY_TESTS
 * Purpose: Verifies stable identifier validation and immutable registry lookup behavior for schema primitives.
 * Connections: Exercises Core StableId and generic PrimitiveRegistry used by all gameplay catalogs.
 * Risk: Medium because identity and duplicate handling are foundational but structurally isolated.
 */
using HyuHeroes.Gameplay.Core;
using HyuHeroes.Gameplay.Registries;

namespace HyuHeroes.Gameplay.Tests;

public sealed class StableIdAndRegistryTests
{
    [Fact]
    public void StableId_WithNamespacedLowercaseValue_IsAccepted()
    {
        var id = StableId.Parse("card.ember_fox");
        Assert.Equal("card.ember_fox", id.Value);
    }

    [Theory]
    [InlineData("Card.EmberFox")]
    [InlineData("card")]
    [InlineData("card.ember-fox")]
    [InlineData("")]
    public void StableId_WithInvalidFormat_IsRejected(string value)
    {
        Assert.Throws<ArgumentException>(() => StableId.Parse(value));
    }

    [Fact]
    public void PrimitiveRegistry_WithDuplicateId_IsRejected()
    {
        var id = StableId.Parse("tag.beast");
        var entries = new[]
        {
            new RegistryEntry(id, "tag.beast", "First"),
            new RegistryEntry(id, "tag.beast", "Second")
        };

        Assert.Throws<ArgumentException>(() => new PrimitiveRegistry<RegistryEntry>(entries));
    }
}
