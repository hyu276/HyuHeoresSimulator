/**
 * GAMEPLAY_CONTENT_PACKAGE_TESTS
 * Purpose: Protects canonical JSON round-tripping, deterministic semantic hashing, tamper detection, and publication validation.
 * Connections: Exercises GameplayContentPackage, canonical writer, strict JSON loader, registry snapshot, schema validation, and ability references.
 * Risk: High because package/hash regressions would break replay identity or allow mismatched client/server content.
 */
using System.Globalization;
using HyuHeroes.Gameplay.Authoring;
using HyuHeroes.Gameplay.Content;
using HyuHeroes.Gameplay.Core;
using HyuHeroes.Gameplay.Effects;
using HyuHeroes.Gameplay.Formulas;
using HyuHeroes.Gameplay.Registries;
using HyuHeroes.Gameplay.Schema;
using HyuHeroes.Gameplay.Selectors;
using HyuHeroes.Gameplay.Validation;

namespace HyuHeroes.Gameplay.Tests;

public sealed class GameplayContentPackageTests
{
    private static readonly DateTimeOffset PublishedAt =
        DateTimeOffset.Parse("2026-09-20T03:00:00Z", CultureInfo.InvariantCulture);

    [Fact]
    public void SignedPackage_RoundTripsAndPreservesHash()
    {
        var package = GameplayContentCanonicalWriter.Sign(CreatePackage());

        var json = GameplayContentCanonicalWriter.Serialize(package);
        var loaded = GameplayContentJsonLoader.Load(json);

        Assert.Equal(package.ContentHash, loaded.ContentHash);
        Assert.Equal(package.ContentVersion, loaded.ContentVersion);
        Assert.Equal("en-US", loaded.Presentation?.DefaultLocale);
        Assert.Equal(
            "Prototype Mage",
            loaded.Presentation?.GetDefaultBundle().GetRequired("loc.card_prototype_mage").Name);
        Assert.Equal(2, loaded.Abilities.Count);
        Assert.Equal(2, loaded.Cards.Count);
        var mage = Assert.Single(
            loaded.Cards,
            card => card.Header.Id == StableId.Parse("card.prototype_mage"));
        Assert.Equal(
            StableId.Parse("ability.prototype_burst"),
            mage.Abilities[0].ReferencedAbilityId);
    }

    [Fact]
    public void Hash_IsStableAcrossDefinitionAndParameterInsertionOrder()
    {
        var first = CreatePackage(reverseInsertionOrder: false);
        var second = CreatePackage(reverseInsertionOrder: true);

        var firstHash = GameplayContentCanonicalWriter.ComputeHash(first);
        var secondHash = GameplayContentCanonicalWriter.ComputeHash(second);

        Assert.Equal(firstHash, secondHash);
    }

    [Fact]
    public void Publisher_EmitsSignedPackageAndPinnedManifest()
    {
        var artifact = ContentPublisher.Publish(
            CreatePackage(),
            "content/packages/prototype-1.json");

        Assert.NotNull(artifact.Package.ContentHash);
        Assert.Contains(artifact.Package.ContentHash!, artifact.PackageJson, StringComparison.Ordinal);
        Assert.Contains("\"defaultContentVersion\": \"prototype.1\"", artifact.ManifestJson, StringComparison.Ordinal);

        var pinned = artifact.Manifest.ResolvePinned(
            artifact.Package.ContentVersion,
            artifact.Package.ContentHash!);
        Assert.Equal("content/packages/prototype-1.json", pinned.PackagePath);
        Assert.Equal("en-US", pinned.DefaultLocale);
        Assert.Equal(new[] { "en-US", "vi-VN" }, pinned.Locales);
    }

    [Fact]
    public void Publisher_RejectsReusingVersionForDifferentArtifact()
    {
        var first = ContentPublisher.Publish(
            CreatePackage(),
            "content/packages/prototype-1.json");
        var changed = CreatePackage(descriptionSuffix: " changed");

        var error = Assert.Throws<InvalidOperationException>(() =>
            ContentPublisher.Publish(
                changed,
                "content/packages/prototype-1.json",
                first.Manifest));

        Assert.Contains("immutable", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Manifest_RejectsWrongPinnedHash()
    {
        var artifact = ContentPublisher.Publish(
            CreatePackage(),
            "content/packages/prototype-1.json");

        Assert.Throws<InvalidDataException>(() =>
            artifact.Manifest.ResolvePinned(
                "prototype.1",
                "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
    }

    [Fact]
    public void Loader_RejectsSemanticTamperingAfterSigning()
    {
        var package = GameplayContentCanonicalWriter.Sign(CreatePackage());
        var json = GameplayContentCanonicalWriter.Serialize(package);
        var tampered = json.Replace(
            "\"contentVersion\": \"prototype.1\"",
            "\"contentVersion\": \"prototype.2\"",
            StringComparison.Ordinal);

        var error = Assert.Throws<InvalidDataException>(() =>
            GameplayContentJsonLoader.Load(tampered));

        Assert.Contains("Content hash mismatch", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Loader_RejectsUnknownFieldsBeforeRuntimeUse()
    {
        var package = GameplayContentCanonicalWriter.Sign(CreatePackage());
        var json = GameplayContentCanonicalWriter.Serialize(package);
        var objectStart = json.IndexOf('{');
        var tampered = json.Insert(objectStart + 1, "\"unexpected\":true,");

        var error = Assert.Throws<InvalidDataException>(() =>
            GameplayContentJsonLoader.Load(tampered));

        Assert.Contains("Unknown property", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Loader_RejectsSignedPackageWithMissingAbilityReference()
    {
        var catalog = GameplayRegistryCatalog.CreateSchemaV1();
        var missingReferenceCard = Card(
            "card.orphan",
            "ability.missing",
            1m,
            1m,
            2m);
        var package = GameplayContentCanonicalWriter.Sign(
            new GameplayContentPackage(
                GameplaySchemaValidator.SupportedSchemaVersion,
                "prototype.missing-reference",
                PublishedAt,
                GameplayRegistrySnapshot.FromCatalog(catalog),
                Array.Empty<AbilityDefinition>(),
                new[] { missingReferenceCard }));

        var json = GameplayContentCanonicalWriter.Serialize(package);

        var error = Assert.Throws<InvalidDataException>(() =>
            GameplayContentJsonLoader.Load(json));

        Assert.Contains("references missing package ability", error.Message, StringComparison.Ordinal);
    }

    private static GameplayContentPackage CreatePackage(
        bool reverseInsertionOrder = false,
        string descriptionSuffix = "")
    {
        var catalog = GameplayRegistryCatalog.CreateSchemaV1();
        var burst = Ability("ability.prototype_burst", EffectIds.Damage, 3m);
        var mend = Ability("ability.prototype_mend", EffectIds.Heal, 2m);
        var mage = Card("card.prototype_mage", burst.Header.Id.Value, 2m, 2m, 3m);
        var guardian = Card("card.prototype_guardian", mend.Header.Id.Value, 3m, 1m, 6m);

        var abilities = reverseInsertionOrder
            ? new[] { mend, burst }
            : new[] { burst, mend };
        var cards = reverseInsertionOrder
            ? new[] { guardian, mage }
            : new[] { mage, guardian };

        var presentation = PresentationFor(abilities, cards, descriptionSuffix);
        return new GameplayContentPackage(
            GameplaySchemaValidator.SupportedSchemaVersion,
            "prototype.1",
            PublishedAt,
            GameplayRegistrySnapshot.FromCatalog(catalog),
            abilities,
            cards,
            presentation: presentation);
    }

    private static GameplayPresentationSnapshot PresentationFor(
        IEnumerable<AbilityDefinition> abilities,
        IEnumerable<CardDefinition> cards,
        string descriptionSuffix)
    {
        var definitions = abilities.Select(ability => ability.Header)
            .Concat(cards.Select(card => card.Header))
            .ToArray();

        LocalizationBundle Bundle(string locale, string prefix) =>
            new(
                locale,
                definitions.Select(header =>
                    new LocalizedDefinitionText(
                        header.LocalizationKey,
                        prefix + DisplayName(header.Id.Value),
                        $"Description for {header.Id.Value}{descriptionSuffix}")));

        return new GameplayPresentationSnapshot(
            "en-US",
            new[]
            {
                Bundle("en-US", ""),
                Bundle("vi-VN", "VN ")
            },
            cards.Select(card =>
                new DefinitionPresentationMetadata(
                    card.Header.Id,
                    artworkReferenceId: $"art/{card.Header.Id.Value}.png",
                    frameStyle: "prototype")));
    }

    private static string DisplayName(string stableId)
    {
        var name = stableId.Split('.').Last().Replace('_', ' ');
        return string.Join(
            " ",
            name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => char.ToUpperInvariant(part[0]) + part.Substring(1)));
    }

    private static AbilityDefinition Ability(
        string id,
        StableId effectId,
        decimal amount)
    {
        var parameters = new[]
        {
            Pair(
                "amount",
                new FormulaParameterValue(FormulaExpression.Constant(amount))),
            Pair(
                "target",
                new SelectorParameterValue(new TargetSelectorSpec(
                    TargetScope.Unit,
                    effectId == EffectIds.Damage ? TargetRelation.Enemy : TargetRelation.Friendly,
                    TargetZone.Board,
                    selection: TargetSelection.PlayerChoice)))
        };

        if (effectId == EffectIds.Damage)
        {
            parameters = parameters
                .Concat(new[] { Pair("damageType", new EnumParameterValue("ARCANE")) })
                .ToArray();
        }

        return new AbilityDefinition(
            Header(id),
            new TriggerSpec(TriggerIds.OnPlay),
            new[] { new EffectDefinition(effectId, new ParameterBag(parameters)) },
            usageLimit: new UsageLimitSpec(UsageLimitIds.OncePerTurn));
    }

    private static CardDefinition Card(
        string id,
        string abilityId,
        decimal cost,
        decimal attack,
        decimal health)
    {
        var stats = new[]
        {
            new KeyValuePair<StableId, decimal>(StableId.Parse("stat.max_health"), health),
            new KeyValuePair<StableId, decimal>(StableId.Parse("stat.cost"), cost),
            new KeyValuePair<StableId, decimal>(StableId.Parse("stat.attack"), attack)
        };

        return new CardDefinition(
            Header(id),
            CardType.Unit,
            new Classification(
                StableId.Parse("alignment.neutral"),
                StableId.Parse("class.control")),
            new StatBlock(stats),
            new[] { AbilityBinding.Reference(StableId.Parse(abilityId)) });
    }

    private static GameplayDefinitionHeader Header(string id) =>
        new(
            StableId.Parse(id),
            GameplaySchemaValidator.SupportedSchemaVersion,
            1,
            ContentStatus.Published,
            $"loc.{id.Replace('.', '_')}");

    private static KeyValuePair<string, ParameterValue> Pair(
        string name,
        ParameterValue value) => new(name, value);
}
