/**
 * DEFAULT_STABLE_ID_VALIDATION_TESTS
 * Purpose: Ensures persistent gameplay definitions cannot pass schema validation with default StableId values.
 * Connections: Exercises GameplayDefinitionHeader and GameplaySchemaValidator publication invariants.
 * Risk: High because empty persistent identity would break content references, replay, and package versioning.
 */
using HyuHeroes.Gameplay.Core;
using HyuHeroes.Gameplay.Registries;
using HyuHeroes.Gameplay.Schema;
using HyuHeroes.Gameplay.Validation;

namespace HyuHeroes.Gameplay.Tests;

public sealed class DefaultStableIdValidationTests
{
    [Fact]
    public void Card_WithDefaultHeaderId_IsRejected()
    {
        var card = new CardDefinition(
            new GameplayDefinitionHeader(
                default,
                GameplaySchemaValidator.SupportedSchemaVersion,
                1,
                ContentStatus.Draft,
                "loc.card.invalid"),
            CardType.Unit,
            new Classification(
                StableId.Parse("alignment.neutral"),
                StableId.Parse("class.striker")),
            new StatBlock());
        var validator = new GameplaySchemaValidator(GameplayRegistryCatalog.CreateSchemaV1());

        var result = validator.Validate(card);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error => error.Code == "schema.invalid_id" && error.Path == "card.header.id");
    }
}
