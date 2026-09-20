/**
 * GAMEPLAY_CONTENT_CANONICAL_WRITER
 * Purpose: Emits stable JSON for published gameplay packages and computes replay-safe SHA-256 content identity.
 * Connections: GameplayContentJsonLoader verifies hashes against this writer; admin/publishing tools can use Sign and Serialize.
 * Risk: High because any canonical ordering change changes contentHash and therefore replay/content compatibility.
 */
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HyuHeroes.Gameplay.Authoring;
using HyuHeroes.Gameplay.Conditions;
using HyuHeroes.Gameplay.Effects;
using HyuHeroes.Gameplay.Formulas;
using HyuHeroes.Gameplay.Schema;
using HyuHeroes.Gameplay.Selectors;

namespace HyuHeroes.Gameplay.Content;

public static class GameplayContentCanonicalWriter
{
    private const string HashPrefix = "sha256:";

    public static GameplayContentPackage Sign(GameplayContentPackage package)
    {
        if (package is null) throw new ArgumentNullException(nameof(package));
        return package.WithContentHash(ComputeHash(package));
    }

    public static string ComputeHash(GameplayContentPackage package)
    {
        if (package is null) throw new ArgumentNullException(nameof(package));
        var canonical = WriteUtf8(package, includeHash: false, indented: false);
        using var sha256 = SHA256.Create();
        var digest = sha256.ComputeHash(canonical);
        return HashPrefix + ToLowerHex(digest);
    }

    public static string Serialize(GameplayContentPackage package, bool indented = true)
    {
        if (package is null) throw new ArgumentNullException(nameof(package));
        return Encoding.UTF8.GetString(WriteUtf8(package, includeHash: true, indented: indented));
    }

    private static byte[] WriteUtf8(
        GameplayContentPackage package,
        bool includeHash,
        bool indented)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
            stream,
            new JsonWriterOptions { Indented = indented, SkipValidation = false }))
        {
            WritePackage(writer, package, includeHash);
        }

        return stream.ToArray();
    }

    private static void WritePackage(
        Utf8JsonWriter writer,
        GameplayContentPackage package,
        bool includeHash)
    {
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", package.SchemaVersion);
        writer.WriteString("contentVersion", package.ContentVersion);
        writer.WriteString("publishedAt", package.PublishedAt.ToString("O", CultureInfo.InvariantCulture));
        WriteRegistries(writer, package.Registries);
        WriteAbilities(writer, package.Abilities);
        WriteCards(writer, package.Cards);
        if (includeHash && package.ContentHash is { } hash)
        {
            writer.WriteString("contentHash", hash);
        }

        writer.WriteEndObject();
        writer.Flush();
    }

    private static void WriteRegistries(
        Utf8JsonWriter writer,
        GameplayRegistrySnapshot snapshot)
    {
        writer.WritePropertyName("registries");
        writer.WriteStartObject();
        foreach (var group in snapshot.Groups.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            writer.WritePropertyName(group.Key);
            writer.WriteStartArray();
            foreach (var id in group.Value)
            {
                writer.WriteStringValue(id.Value);
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    private static void WriteAbilities(
        Utf8JsonWriter writer,
        IReadOnlyList<AbilityDefinition> abilities)
    {
        writer.WritePropertyName("abilities");
        writer.WriteStartArray();
        foreach (var ability in abilities.OrderBy(item => item.Header.Id))
        {
            WriteAbility(writer, ability);
        }

        writer.WriteEndArray();
    }

    private static void WriteCards(
        Utf8JsonWriter writer,
        IReadOnlyList<CardDefinition> cards)
    {
        writer.WritePropertyName("cards");
        writer.WriteStartArray();
        foreach (var card in cards.OrderBy(item => item.Header.Id))
        {
            WriteCard(writer, card);
        }

        writer.WriteEndArray();
    }

    private static void WriteAbility(Utf8JsonWriter writer, AbilityDefinition ability)
    {
        writer.WriteStartObject();
        WriteHeader(writer, ability.Header);
        WriteDescriptor(writer, "trigger", ability.Trigger.TypeId.Value, ability.Trigger.Parameters);
        if (ability.Condition is not null)
        {
            writer.WritePropertyName("condition");
            WriteCondition(writer, ability.Condition);
        }

        writer.WritePropertyName("effects");
        writer.WriteStartArray();
        foreach (var effect in ability.Effects)
        {
            WriteEffect(writer, effect);
        }

        writer.WriteEndArray();
        if (ability.UsageLimit is not null)
        {
            WriteDescriptor(writer, "usageLimit", ability.UsageLimit.TypeId.Value, ability.UsageLimit.Parameters);
        }

        if (ability.Duration is not null)
        {
            WriteDescriptor(writer, "duration", ability.Duration.TypeId.Value, ability.Duration.Parameters);
        }

        writer.WriteEndObject();
    }

    private static void WriteCard(Utf8JsonWriter writer, CardDefinition card)
    {
        writer.WriteStartObject();
        WriteHeader(writer, card.Header);
        writer.WriteString("cardType", card.CardType.ToString());
        WriteClassification(writer, card.Classification);
        WriteStats(writer, card.BaseStats);

        writer.WritePropertyName("abilities");
        writer.WriteStartArray();
        foreach (var binding in card.Abilities)
        {
            writer.WriteStartObject();
            if (binding.ReferencedAbilityId is { } referenceId)
            {
                writer.WriteString("referenceId", referenceId.Value);
            }
            else if (binding.InlineAbility is { } inline)
            {
                writer.WritePropertyName("inline");
                WriteAbility(writer, inline);
            }
            else
            {
                throw new InvalidOperationException("Ability binding has neither reference nor inline definition.");
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        if (card.ArtworkReferenceId is not null)
        {
            writer.WriteString("artworkReferenceId", card.ArtworkReferenceId);
        }

        writer.WriteEndObject();
    }

    private static void WriteHeader(Utf8JsonWriter writer, GameplayDefinitionHeader header)
    {
        writer.WritePropertyName("header");
        writer.WriteStartObject();
        writer.WriteString("id", header.Id.Value);
        writer.WriteNumber("schemaVersion", header.SchemaVersion);
        writer.WriteNumber("revision", header.Revision);
        writer.WriteString("status", header.Status.ToString());
        writer.WriteString("localizationKey", header.LocalizationKey);
        writer.WriteEndObject();
    }

    private static void WriteClassification(Utf8JsonWriter writer, Classification classification)
    {
        writer.WritePropertyName("classification");
        writer.WriteStartObject();
        writer.WriteString("alignmentId", classification.AlignmentId.Value);
        writer.WriteString("classId", classification.ClassId.Value);
        if (classification.FactionId is { } factionId)
        {
            writer.WriteString("factionId", factionId.Value);
        }

        WriteIds(writer, "tags", classification.Tags);
        WriteIds(writer, "keywords", classification.Keywords);
        writer.WriteEndObject();
    }

    private static void WriteStats(Utf8JsonWriter writer, StatBlock stats)
    {
        writer.WritePropertyName("baseStats");
        writer.WriteStartObject();
        foreach (var pair in stats.Values.OrderBy(pair => pair.Key))
        {
            writer.WriteNumber(pair.Key.Value, pair.Value);
        }

        writer.WriteEndObject();
    }

    private static void WriteIds(
        Utf8JsonWriter writer,
        string propertyName,
        IEnumerable<HyuHeroes.Gameplay.Core.StableId> ids)
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartArray();
        foreach (var id in ids.OrderBy(value => value))
        {
            writer.WriteStringValue(id.Value);
        }

        writer.WriteEndArray();
    }

    private static void WriteDescriptor(
        Utf8JsonWriter writer,
        string propertyName,
        string typeId,
        ParameterBag parameters)
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartObject();
        writer.WriteString("typeId", typeId);
        WriteParameters(writer, parameters);
        writer.WriteEndObject();
    }

    private static void WriteEffect(Utf8JsonWriter writer, EffectDefinition effect)
    {
        writer.WriteStartObject();
        writer.WriteString("typeId", effect.TypeId.Value);
        WriteParameters(writer, effect.Parameters);
        writer.WriteEndObject();
    }

    private static void WriteParameters(Utf8JsonWriter writer, ParameterBag parameters)
    {
        writer.WritePropertyName("parameters");
        writer.WriteStartObject();
        foreach (var pair in parameters.Values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            writer.WritePropertyName(pair.Key);
            WriteParameterValue(writer, pair.Value);
        }

        writer.WriteEndObject();
    }

    private static void WriteParameterValue(Utf8JsonWriter writer, ParameterValue value)
    {
        writer.WriteStartObject();
        writer.WriteString("kind", value.Kind.ToString());
        writer.WritePropertyName("value");

        switch (value)
        {
            case IntegerParameterValue integer:
                writer.WriteNumberValue(integer.Value);
                break;
            case DecimalParameterValue decimalValue:
                writer.WriteNumberValue(decimalValue.Value);
                break;
            case BooleanParameterValue boolean:
                writer.WriteBooleanValue(boolean.Value);
                break;
            case StringParameterValue text:
                writer.WriteStringValue(text.Value);
                break;
            case StableIdParameterValue stableId:
                writer.WriteStringValue(stableId.Value.Value);
                break;
            case EnumParameterValue enumeration:
                writer.WriteStringValue(enumeration.Value);
                break;
            case FormulaParameterValue formula:
                WriteFormula(writer, formula.Value);
                break;
            case SelectorParameterValue selector:
                WriteSelector(writer, selector.Value);
                break;
            case ConditionParameterValue condition:
                WriteCondition(writer, condition.Value);
                break;
            case EffectListParameterValue effects:
                writer.WriteStartArray();
                foreach (var effect in effects.Value)
                {
                    WriteEffect(writer, effect);
                }

                writer.WriteEndArray();
                break;
            default:
                throw new InvalidOperationException($"Unsupported parameter value type '{value.GetType().Name}'.");
        }

        writer.WriteEndObject();
    }

    private static void WriteFormula(Utf8JsonWriter writer, FormulaExpression formula)
    {
        writer.WriteStartObject();
        writer.WriteString("operatorId", formula.OperatorId.Value);
        if (formula.ConstantValue is { } constant)
        {
            writer.WriteNumber("constant", constant);
        }

        if (formula.VariableId is { } variableId)
        {
            writer.WriteString("variableId", variableId.Value);
        }

        if (formula.Selector is not null)
        {
            writer.WritePropertyName("selector");
            WriteSelector(writer, formula.Selector);
        }

        writer.WritePropertyName("arguments");
        writer.WriteStartArray();
        foreach (var argument in formula.Arguments)
        {
            WriteFormula(writer, argument);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteSelector(Utf8JsonWriter writer, TargetSelectorSpec selector)
    {
        writer.WriteStartObject();
        writer.WriteString("scope", selector.Scope.ToString());
        writer.WriteString("relation", selector.Relation.ToString());
        writer.WriteString("zone", selector.Zone.ToString());
        writer.WriteString("location", selector.Location.ToString());
        writer.WriteString("selection", selector.Selection.ToString());
        writer.WriteNumber("selectionCount", selector.SelectionCount);
        writer.WritePropertyName("filters");
        writer.WriteStartArray();
        foreach (var filter in selector.Filters)
        {
            WriteCondition(writer, filter);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteCondition(Utf8JsonWriter writer, ConditionNode condition)
    {
        writer.WriteStartObject();
        writer.WriteString("kind", condition.Kind.ToString());
        switch (condition)
        {
            case AllConditionNode all:
                WriteConditionChildren(writer, all.Children);
                break;
            case AnyConditionNode any:
                WriteConditionChildren(writer, any.Children);
                break;
            case NotConditionNode not:
                writer.WritePropertyName("child");
                WriteCondition(writer, not.Child);
                break;
            case PredicateConditionNode predicate:
                writer.WriteString("typeId", predicate.Predicate.TypeId.Value);
                WriteParameters(writer, predicate.Predicate.Parameters);
                break;
            default:
                throw new InvalidOperationException($"Unsupported condition node type '{condition.GetType().Name}'.");
        }

        writer.WriteEndObject();
    }

    private static void WriteConditionChildren(
        Utf8JsonWriter writer,
        IEnumerable<ConditionNode> children)
    {
        writer.WritePropertyName("children");
        writer.WriteStartArray();
        foreach (var child in children)
        {
            WriteCondition(writer, child);
        }

        writer.WriteEndArray();
    }

    private static string ToLowerHex(byte[] bytes)
    {
        var builder = new StringBuilder(bytes.Length * 2);
        foreach (var value in bytes)
        {
            builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }
}
