/**
 * GAMEPLAY_CONTENT_JSON_LOADER
 * Purpose: Parses strict schema-v1 JSON packages, verifies deterministic contentHash, and re-runs publication validation before runtime use.
 * Connections: Consumes GameplayContentCanonicalWriter identity rules plus GameplaySchemaValidator and GameplayRegistryCatalog.
 * Risk: High because this is the trust boundary between external published JSON and authoritative gameplay objects.
 */
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using HyuHeroes.Gameplay.Authoring;
using HyuHeroes.Gameplay.Conditions;
using HyuHeroes.Gameplay.Core;
using HyuHeroes.Gameplay.Effects;
using HyuHeroes.Gameplay.Formulas;
using HyuHeroes.Gameplay.Registries;
using HyuHeroes.Gameplay.Schema;
using HyuHeroes.Gameplay.Selectors;

namespace HyuHeroes.Gameplay.Content;

public static class GameplayContentJsonLoader
{
    public static GameplayContentPackage Load(
        string json,
        GameplayRegistryCatalog? catalog = null)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ArgumentException("Content package JSON cannot be empty.", nameof(json));
        }

        using var document = JsonDocument.Parse(json);
        var root = RequireObject(document.RootElement, "package");
        RequireOnly(root, "package",
            "schemaVersion", "contentVersion", "publishedAt", "registries", "abilities", "cards", "contentHash");

        var contentHash = ReadRequiredString(root, "contentHash", "package");
        var package = new GameplayContentPackage(
            ReadRequiredInt(root, "schemaVersion", "package"),
            ReadRequiredString(root, "contentVersion", "package"),
            ParsePublishedAt(ReadRequiredString(root, "publishedAt", "package")),
            ParseRegistries(ReadRequired(root, "registries", "package")),
            ParseAbilities(ReadRequired(root, "abilities", "package")),
            ParseCards(ReadRequired(root, "cards", "package")),
            contentHash);

        var expectedHash = GameplayContentCanonicalWriter.ComputeHash(package);
        if (!string.Equals(contentHash, expectedHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Content hash mismatch. Expected '{expectedHash}', received '{contentHash}'.");
        }

        GameplayContentPackageValidator.Validate(
            package,
            catalog ?? GameplayRegistryCatalog.CreateSchemaV1());
        return package;
    }

    private static GameplayRegistrySnapshot ParseRegistries(JsonElement element)
    {
        element = RequireObject(element, "registries");
        var groups = new List<KeyValuePair<string, IEnumerable<StableId>>>();
        foreach (var property in element.EnumerateObject())
        {
            var values = RequireArray(property.Value, $"registries.{property.Name}")
                .EnumerateArray()
                .Select((item, index) => ParseStableId(
                    RequireString(item, $"registries.{property.Name}[{index}]"),
                    $"registries.{property.Name}[{index}]"))
                .ToArray();
            groups.Add(new KeyValuePair<string, IEnumerable<StableId>>(property.Name, values));
        }

        return new GameplayRegistrySnapshot(groups);
    }

    private static IReadOnlyList<AbilityDefinition> ParseAbilities(JsonElement element)
    {
        var array = RequireArray(element, "abilities");
        return array.EnumerateArray()
            .Select((item, index) => ParseAbility(item, $"abilities[{index}]"))
            .ToArray();
    }

    private static IReadOnlyList<CardDefinition> ParseCards(JsonElement element)
    {
        var array = RequireArray(element, "cards");
        return array.EnumerateArray()
            .Select((item, index) => ParseCard(item, $"cards[{index}]"))
            .ToArray();
    }

    private static AbilityDefinition ParseAbility(JsonElement element, string path)
    {
        element = RequireObject(element, path);
        RequireOnly(element, path, "header", "trigger", "condition", "effects", "usageLimit", "duration");
        var condition = TryGet(element, "condition", out var conditionElement)
            ? ParseCondition(conditionElement, $"{path}.condition")
            : null;
        var usageLimit = TryGet(element, "usageLimit", out var usageElement)
            ? ParseUsageLimit(usageElement, $"{path}.usageLimit")
            : null;
        var duration = TryGet(element, "duration", out var durationElement)
            ? ParseDuration(durationElement, $"{path}.duration")
            : null;

        return new AbilityDefinition(
            ParseHeader(ReadRequired(element, "header", path), $"{path}.header"),
            ParseTrigger(ReadRequired(element, "trigger", path), $"{path}.trigger"),
            ParseEffects(ReadRequired(element, "effects", path), $"{path}.effects"),
            condition,
            usageLimit,
            duration);
    }

    private static CardDefinition ParseCard(JsonElement element, string path)
    {
        element = RequireObject(element, path);
        RequireOnly(
            element,
            path,
            "header", "cardType", "classification", "baseStats", "abilities", "artworkReferenceId");

        var artworkReferenceId = TryGet(element, "artworkReferenceId", out var artwork)
            ? RequireString(artwork, $"{path}.artworkReferenceId")
            : null;

        return new CardDefinition(
            ParseHeader(ReadRequired(element, "header", path), $"{path}.header"),
            ParseEnum<CardType>(ReadRequiredString(element, "cardType", path), $"{path}.cardType"),
            ParseClassification(ReadRequired(element, "classification", path), $"{path}.classification"),
            ParseStats(ReadRequired(element, "baseStats", path), $"{path}.baseStats"),
            ParseBindings(ReadRequired(element, "abilities", path), $"{path}.abilities"),
            artworkReferenceId);
    }

    private static GameplayDefinitionHeader ParseHeader(JsonElement element, string path)
    {
        element = RequireObject(element, path);
        RequireOnly(element, path, "id", "schemaVersion", "revision", "status", "localizationKey");
        return new GameplayDefinitionHeader(
            ParseStableId(ReadRequiredString(element, "id", path), $"{path}.id"),
            ReadRequiredInt(element, "schemaVersion", path),
            ReadRequiredInt(element, "revision", path),
            ParseEnum<ContentStatus>(ReadRequiredString(element, "status", path), $"{path}.status"),
            ReadRequiredString(element, "localizationKey", path));
    }

    private static Classification ParseClassification(JsonElement element, string path)
    {
        element = RequireObject(element, path);
        RequireOnly(element, path, "alignmentId", "classId", "factionId", "tags", "keywords");
        var factionId = TryGet(element, "factionId", out var faction)
            ? ParseStableId(RequireString(faction, $"{path}.factionId"), $"{path}.factionId")
            : (StableId?)null;
        return new Classification(
            ParseStableId(ReadRequiredString(element, "alignmentId", path), $"{path}.alignmentId"),
            ParseStableId(ReadRequiredString(element, "classId", path), $"{path}.classId"),
            factionId,
            ParseIdArray(ReadRequired(element, "tags", path), $"{path}.tags"),
            ParseIdArray(ReadRequired(element, "keywords", path), $"{path}.keywords"));
    }

    private static StatBlock ParseStats(JsonElement element, string path)
    {
        element = RequireObject(element, path);
        var values = element.EnumerateObject()
            .Select(property => new KeyValuePair<StableId, decimal>(
                ParseStableId(property.Name, $"{path}.{property.Name}"),
                RequireDecimal(property.Value, $"{path}.{property.Name}")))
            .ToArray();
        return new StatBlock(values);
    }

    private static IReadOnlyList<AbilityBinding> ParseBindings(JsonElement element, string path)
    {
        var array = RequireArray(element, path);
        return array.EnumerateArray()
            .Select((item, index) => ParseBinding(item, $"{path}[{index}]"))
            .ToArray();
    }

    private static AbilityBinding ParseBinding(JsonElement element, string path)
    {
        element = RequireObject(element, path);
        RequireOnly(element, path, "referenceId", "inline");
        var hasReference = TryGet(element, "referenceId", out var reference);
        var hasInline = TryGet(element, "inline", out var inline);
        if (hasReference == hasInline)
        {
            throw new InvalidDataException($"{path} must contain exactly one of referenceId or inline.");
        }

        return hasReference
            ? AbilityBinding.Reference(ParseStableId(RequireString(reference, $"{path}.referenceId"), $"{path}.referenceId"))
            : AbilityBinding.Inline(ParseAbility(inline, $"{path}.inline"));
    }

    private static TriggerSpec ParseTrigger(JsonElement element, string path)
    {
        var descriptor = ParseDescriptor(element, path);
        return new TriggerSpec(descriptor.TypeId, descriptor.Parameters);
    }

    private static UsageLimitSpec ParseUsageLimit(JsonElement element, string path)
    {
        var descriptor = ParseDescriptor(element, path);
        return new UsageLimitSpec(descriptor.TypeId, descriptor.Parameters);
    }

    private static DurationSpec ParseDuration(JsonElement element, string path)
    {
        var descriptor = ParseDescriptor(element, path);
        return new DurationSpec(descriptor.TypeId, descriptor.Parameters);
    }

    private static (StableId TypeId, ParameterBag Parameters) ParseDescriptor(
        JsonElement element,
        string path)
    {
        element = RequireObject(element, path);
        RequireOnly(element, path, "typeId", "parameters");
        return (
            ParseStableId(ReadRequiredString(element, "typeId", path), $"{path}.typeId"),
            ParseParameters(ReadRequired(element, "parameters", path), $"{path}.parameters"));
    }

    private static IReadOnlyList<EffectDefinition> ParseEffects(JsonElement element, string path)
    {
        var array = RequireArray(element, path);
        return array.EnumerateArray()
            .Select((item, index) => ParseEffect(item, $"{path}[{index}]"))
            .ToArray();
    }

    private static EffectDefinition ParseEffect(JsonElement element, string path)
    {
        element = RequireObject(element, path);
        RequireOnly(element, path, "typeId", "parameters");
        return new EffectDefinition(
            ParseStableId(ReadRequiredString(element, "typeId", path), $"{path}.typeId"),
            ParseParameters(ReadRequired(element, "parameters", path), $"{path}.parameters"));
    }

    private static ParameterBag ParseParameters(JsonElement element, string path)
    {
        element = RequireObject(element, path);
        var values = element.EnumerateObject()
            .Select(property => new KeyValuePair<string, ParameterValue>(
                property.Name,
                ParseParameterValue(property.Value, $"{path}.{property.Name}")))
            .ToArray();
        return new ParameterBag(values);
    }

    private static ParameterValue ParseParameterValue(JsonElement element, string path)
    {
        element = RequireObject(element, path);
        RequireOnly(element, path, "kind", "value");
        var kind = ParseEnum<ParameterKind>(
            ReadRequiredString(element, "kind", path),
            $"{path}.kind");
        var value = ReadRequired(element, "value", path);

        return kind switch
        {
            ParameterKind.Integer => new IntegerParameterValue(RequireInt(value, $"{path}.value")),
            ParameterKind.Decimal => new DecimalParameterValue(RequireDecimal(value, $"{path}.value")),
            ParameterKind.Boolean => new BooleanParameterValue(RequireBoolean(value, $"{path}.value")),
            ParameterKind.String => new StringParameterValue(RequireString(value, $"{path}.value")),
            ParameterKind.StableId => new StableIdParameterValue(ParseStableId(RequireString(value, $"{path}.value"), $"{path}.value")),
            ParameterKind.Enum => new EnumParameterValue(RequireString(value, $"{path}.value")),
            ParameterKind.Formula => new FormulaParameterValue(ParseFormula(value, $"{path}.value")),
            ParameterKind.Selector => new SelectorParameterValue(ParseSelector(value, $"{path}.value")),
            ParameterKind.Condition => new ConditionParameterValue(ParseCondition(value, $"{path}.value")),
            ParameterKind.EffectList => new EffectListParameterValue(ParseEffects(value, $"{path}.value")),
            _ => throw new InvalidDataException($"Unsupported parameter kind '{kind}' at {path}.")
        };
    }

    private static FormulaExpression ParseFormula(JsonElement element, string path)
    {
        element = RequireObject(element, path);
        RequireOnly(element, path, "operatorId", "constant", "variableId", "selector", "arguments");
        var arguments = RequireArray(ReadRequired(element, "arguments", path), $"{path}.arguments")
            .EnumerateArray()
            .Select((item, index) => ParseFormula(item, $"{path}.arguments[{index}]"))
            .ToArray();
        var constant = TryGet(element, "constant", out var constantElement)
            ? RequireDecimal(constantElement, $"{path}.constant")
            : (decimal?)null;
        var variableId = TryGet(element, "variableId", out var variableElement)
            ? ParseStableId(RequireString(variableElement, $"{path}.variableId"), $"{path}.variableId")
            : (StableId?)null;
        var selector = TryGet(element, "selector", out var selectorElement)
            ? ParseSelector(selectorElement, $"{path}.selector")
            : null;

        return new FormulaExpression(
            ParseStableId(ReadRequiredString(element, "operatorId", path), $"{path}.operatorId"),
            arguments,
            constant,
            variableId,
            selector);
    }

    private static TargetSelectorSpec ParseSelector(JsonElement element, string path)
    {
        element = RequireObject(element, path);
        RequireOnly(element, path, "scope", "relation", "zone", "location", "selection", "selectionCount", "filters");
        var filters = RequireArray(ReadRequired(element, "filters", path), $"{path}.filters")
            .EnumerateArray()
            .Select((item, index) => ParseCondition(item, $"{path}.filters[{index}]"))
            .ToArray();

        return new TargetSelectorSpec(
            ParseEnum<TargetScope>(ReadRequiredString(element, "scope", path), $"{path}.scope"),
            ParseEnum<TargetRelation>(ReadRequiredString(element, "relation", path), $"{path}.relation"),
            ParseEnum<TargetZone>(ReadRequiredString(element, "zone", path), $"{path}.zone"),
            ParseEnum<TargetLocation>(ReadRequiredString(element, "location", path), $"{path}.location"),
            ParseEnum<TargetSelection>(ReadRequiredString(element, "selection", path), $"{path}.selection"),
            ReadRequiredInt(element, "selectionCount", path),
            filters);
    }

    private static ConditionNode ParseCondition(JsonElement element, string path)
    {
        element = RequireObject(element, path);
        var kind = ParseEnum<ConditionNodeKind>(
            ReadRequiredString(element, "kind", path),
            $"{path}.kind");

        return kind switch
        {
            ConditionNodeKind.All => ParseAllCondition(element, path),
            ConditionNodeKind.Any => ParseAnyCondition(element, path),
            ConditionNodeKind.Not => ParseNotCondition(element, path),
            ConditionNodeKind.Predicate => ParsePredicateCondition(element, path),
            _ => throw new InvalidDataException($"Unsupported condition kind '{kind}' at {path}.")
        };
    }

    private static ConditionNode ParseAllCondition(JsonElement element, string path)
    {
        RequireOnly(element, path, "kind", "children");
        return new AllConditionNode(ParseConditionChildren(element, path));
    }

    private static ConditionNode ParseAnyCondition(JsonElement element, string path)
    {
        RequireOnly(element, path, "kind", "children");
        return new AnyConditionNode(ParseConditionChildren(element, path));
    }

    private static ConditionNode ParseNotCondition(JsonElement element, string path)
    {
        RequireOnly(element, path, "kind", "child");
        return new NotConditionNode(ParseCondition(
            ReadRequired(element, "child", path),
            $"{path}.child"));
    }

    private static ConditionNode ParsePredicateCondition(JsonElement element, string path)
    {
        RequireOnly(element, path, "kind", "typeId", "parameters");
        return new PredicateConditionNode(new ConditionPredicateSpec(
            ParseStableId(ReadRequiredString(element, "typeId", path), $"{path}.typeId"),
            ParseParameters(ReadRequired(element, "parameters", path), $"{path}.parameters")));
    }

    private static IReadOnlyList<ConditionNode> ParseConditionChildren(
        JsonElement element,
        string path) =>
        RequireArray(ReadRequired(element, "children", path), $"{path}.children")
            .EnumerateArray()
            .Select((item, index) => ParseCondition(item, $"{path}.children[{index}]"))
            .ToArray();

    private static IReadOnlyList<StableId> ParseIdArray(JsonElement element, string path) =>
        RequireArray(element, path)
            .EnumerateArray()
            .Select((item, index) => ParseStableId(
                RequireString(item, $"{path}[{index}]"),
                $"{path}[{index}]"))
            .ToArray();

    private static DateTimeOffset ParsePublishedAt(string value)
    {
        if (!DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var publishedAt))
        {
            throw new InvalidDataException($"Invalid publishedAt timestamp '{value}'.");
        }

        return publishedAt;
    }

    private static StableId ParseStableId(string value, string path)
    {
        if (!StableId.TryParse(value, out var id))
        {
            throw new InvalidDataException($"Invalid StableId '{value}' at {path}.");
        }

        return id;
    }

    private static TEnum ParseEnum<TEnum>(string value, string path)
        where TEnum : struct, Enum
    {
        if (!Enum.TryParse<TEnum>(value, ignoreCase: false, out var parsed) ||
            !Enum.IsDefined(typeof(TEnum), parsed))
        {
            throw new InvalidDataException($"Invalid {typeof(TEnum).Name} value '{value}' at {path}.");
        }

        return parsed;
    }

    private static JsonElement ReadRequired(JsonElement element, string propertyName, string path)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            throw new InvalidDataException($"Missing required property '{path}.{propertyName}'.");
        }

        return value;
    }

    private static string ReadRequiredString(JsonElement element, string propertyName, string path) =>
        RequireString(ReadRequired(element, propertyName, path), $"{path}.{propertyName}");

    private static int ReadRequiredInt(JsonElement element, string propertyName, string path) =>
        RequireInt(ReadRequired(element, propertyName, path), $"{path}.{propertyName}");

    private static bool TryGet(JsonElement element, string propertyName, out JsonElement value) =>
        element.TryGetProperty(propertyName, out value);

    private static JsonElement RequireObject(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{path} must be a JSON object.");
        }

        return element;
    }

    private static JsonElement RequireArray(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"{path} must be a JSON array.");
        }

        return element;
    }

    private static string RequireString(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"{path} must be a JSON string.");
        }

        return element.GetString()
            ?? throw new InvalidDataException($"{path} cannot be null.");
    }

    private static int RequireInt(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value))
        {
            throw new InvalidDataException($"{path} must be an Int32 JSON number.");
        }

        return value;
    }

    private static decimal RequireDecimal(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetDecimal(out var value))
        {
            throw new InvalidDataException($"{path} must be a decimal JSON number.");
        }

        return value;
    }

    private static bool RequireBoolean(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.True && element.ValueKind != JsonValueKind.False)
        {
            throw new InvalidDataException($"{path} must be a JSON boolean.");
        }

        return element.GetBoolean();
    }

    private static void RequireOnly(
        JsonElement element,
        string path,
        params string[] propertyNames)
    {
        var allowed = propertyNames.ToHashSet(StringComparer.Ordinal);
        var unknown = element.EnumerateObject()
            .FirstOrDefault(property => !allowed.Contains(property.Name));
        if (unknown.Name is not null)
        {
            throw new InvalidDataException($"Unknown property '{path}.{unknown.Name}'.");
        }
    }
}
