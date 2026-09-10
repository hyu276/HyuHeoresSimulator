/**
 * GAMEPLAY_SCHEMA_VALIDATOR
 * Purpose: Validates top-level schema-v1 cards and abilities before publication or authoritative runtime loading.
 * Connections: Coordinates GameplayRegistryCatalog with PrimitiveGraphValidator and immutable gameplay definitions.
 * Risk: High because this is the publication gate for structured gameplay content.
 */
using System;
using System.Collections.Generic;
using HyuHeroes.Gameplay.Conditions;
using HyuHeroes.Gameplay.Core;
using HyuHeroes.Gameplay.Effects;
using HyuHeroes.Gameplay.Formulas;
using HyuHeroes.Gameplay.Registries;
using HyuHeroes.Gameplay.Schema;

namespace HyuHeroes.Gameplay.Validation;

public sealed class GameplaySchemaValidator
{
    public const int SupportedSchemaVersion = 1;
    public const int MaxConditionDepth = 12;
    public const int MaxFormulaDepth = 16;
    public const int MaxEffectDepth = 8;
    public const int MaxEffectsPerAbility = 32;

    private readonly GameplayRegistryCatalog _catalog;
    private readonly PrimitiveGraphValidator _primitives;

    public GameplaySchemaValidator(GameplayRegistryCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _primitives = new PrimitiveGraphValidator(catalog);
    }

    public ValidationResult Validate(CardDefinition card)
    {
        if (card is null)
        {
            throw new ArgumentNullException(nameof(card));
        }

        var errors = new ValidationCollector();
        ValidateHeader(card.Header, "card.header", errors);
        ValidateClassification(card.Classification, "card.classification", errors);
        ValidateStats(card.BaseStats, "card.baseStats", errors);
        ValidateBindings(card.Abilities, errors);
        return errors.ToResult();
    }

    public ValidationResult Validate(AbilityDefinition ability)
    {
        if (ability is null)
        {
            throw new ArgumentNullException(nameof(ability));
        }

        var errors = new ValidationCollector();
        ValidateAbility(ability, "ability", errors);
        return errors.ToResult();
    }

    public ValidationResult Validate(EffectDefinition effect)
    {
        if (effect is null)
        {
            throw new ArgumentNullException(nameof(effect));
        }

        var errors = new ValidationCollector();
        _primitives.ValidateEffect(effect, "effect", 1, errors);
        return errors.ToResult();
    }

    public ValidationResult Validate(FormulaExpression formula)
    {
        if (formula is null)
        {
            throw new ArgumentNullException(nameof(formula));
        }

        var errors = new ValidationCollector();
        _primitives.ValidateFormula(formula, "formula", 1, errors);
        return errors.ToResult();
    }

    public ValidationResult Validate(ConditionNode condition)
    {
        if (condition is null)
        {
            throw new ArgumentNullException(nameof(condition));
        }

        var errors = new ValidationCollector();
        _primitives.ValidateCondition(condition, "condition", 1, errors);
        return errors.ToResult();
    }

    private static void ValidateHeader(GameplayDefinitionHeader header, string path, ValidationCollector errors)
    {
        if (header.SchemaVersion != SupportedSchemaVersion)
        {
            errors.Add("schema.unsupported_version", path, $"Expected schema version {SupportedSchemaVersion}, got {header.SchemaVersion}.");
        }
    }

    private void ValidateClassification(Classification classification, string path, ValidationCollector errors)
    {
        RequireRegistered(_catalog.Alignments, classification.AlignmentId, $"{path}.alignmentId", "alignment", errors);
        RequireRegistered(_catalog.Classes, classification.ClassId, $"{path}.classId", "class", errors);

        if (classification.FactionId is { } factionId)
        {
            RequireRegistered(_catalog.Factions, factionId, $"{path}.factionId", "faction", errors);
        }

        ValidateIds(_catalog.Tags, classification.Tags, $"{path}.tags", "tag", errors);
        ValidateIds(_catalog.Keywords, classification.Keywords, $"{path}.keywords", "keyword", errors);
    }

    private void ValidateStats(StatBlock stats, string path, ValidationCollector errors)
    {
        foreach (var pair in stats.Values)
        {
            RequireRegistered(_catalog.Stats, pair.Key, $"{path}.{pair.Key}", "stat", errors);
        }
    }

    private void ValidateBindings(IReadOnlyList<AbilityBinding> abilities, ValidationCollector errors)
    {
        for (var index = 0; index < abilities.Count; index += 1)
        {
            var binding = abilities[index];
            if (binding.InlineAbility is not null)
            {
                ValidateAbility(binding.InlineAbility, $"card.abilities[{index}].inline", errors);
                continue;
            }

            if (binding.ReferencedAbilityId is null)
            {
                errors.Add("ability.invalid_binding", $"card.abilities[{index}]", "Ability binding needs a reference or inline definition.");
            }
        }
    }

    private void ValidateAbility(AbilityDefinition ability, string path, ValidationCollector errors)
    {
        ValidateHeader(ability.Header, $"{path}.header", errors);
        _primitives.ValidateDescriptor(_catalog.Triggers, ability.Trigger.TypeId, ability.Trigger.Parameters, $"{path}.trigger", errors);

        if (ability.Condition is not null)
        {
            _primitives.ValidateCondition(ability.Condition, $"{path}.condition", 1, errors);
        }

        if (ability.Effects.Count > MaxEffectsPerAbility)
        {
            errors.Add("ability.too_many_effects", $"{path}.effects", $"Maximum is {MaxEffectsPerAbility} effects.");
        }

        for (var index = 0; index < ability.Effects.Count; index += 1)
        {
            _primitives.ValidateEffect(ability.Effects[index], $"{path}.effects[{index}]", 1, errors);
        }

        ValidateOptionalAbilitySpecs(ability, path, errors);
    }

    private void ValidateOptionalAbilitySpecs(AbilityDefinition ability, string path, ValidationCollector errors)
    {
        if (ability.UsageLimit is not null)
        {
            _primitives.ValidateDescriptor(_catalog.UsageLimits, ability.UsageLimit.TypeId, ability.UsageLimit.Parameters, $"{path}.usageLimit", errors);
        }

        if (ability.Duration is not null)
        {
            _primitives.ValidateDescriptor(_catalog.Durations, ability.Duration.TypeId, ability.Duration.Parameters, $"{path}.duration", errors);
        }
    }

    private static void ValidateIds<TEntry>(
        PrimitiveRegistry<TEntry> registry,
        IEnumerable<StableId> ids,
        string path,
        string kind,
        ValidationCollector errors)
        where TEntry : IRegistryEntry
    {
        var index = 0;
        foreach (var id in ids)
        {
            RequireRegistered(registry, id, $"{path}[{index}]", kind, errors);
            index += 1;
        }
    }

    private static void RequireRegistered<TEntry>(
        PrimitiveRegistry<TEntry> registry,
        StableId id,
        string path,
        string kind,
        ValidationCollector errors)
        where TEntry : IRegistryEntry
    {
        if (!registry.Contains(id))
        {
            errors.Add("registry.unknown_id", path, $"Unknown {kind} ID '{id}'.");
        }
    }
}
