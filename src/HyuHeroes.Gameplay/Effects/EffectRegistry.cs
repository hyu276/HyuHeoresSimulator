/**
 * EFFECT_REGISTRY
 * Purpose: Defines structured effect instances and the controlled metadata catalog for authorable state changes.
 * Connections: Ability schemas reference effects while validators and future dashboards consume registry metadata.
 * Risk: High because effect IDs form the stable vocabulary that future execution handlers must implement.
 */
using System;
using System.Collections.Generic;
using HyuHeroes.Gameplay.Authoring;
using HyuHeroes.Gameplay.Core;
using HyuHeroes.Gameplay.Registries;

namespace HyuHeroes.Gameplay.Effects;

public sealed class EffectDefinition
{
    public EffectDefinition(StableId typeId, ParameterBag? parameters = null)
    {
        TypeId = typeId;
        Parameters = parameters ?? new ParameterBag();
    }

    public StableId TypeId { get; }
    public ParameterBag Parameters { get; }
}

public sealed class EffectRegistration : IRegistryEntry
{
    public EffectRegistration(PrimitiveDescriptor descriptor)
    {
        Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
    }

    public StableId Id => Descriptor.Id;
    public PrimitiveDescriptor Descriptor { get; }
}

public sealed class EffectRegistry : PrimitiveRegistry<EffectRegistration>
{
    public EffectRegistry(IEnumerable<EffectRegistration>? registrations = null) : base(registrations)
    {
    }
}

public static class EffectIds
{
    public static readonly StableId Damage = StableId.Parse("effect.damage");
    public static readonly StableId Heal = StableId.Parse("effect.heal");
    public static readonly StableId Draw = StableId.Parse("effect.draw");
    public static readonly StableId Discard = StableId.Parse("effect.discard");
    public static readonly StableId Summon = StableId.Parse("effect.summon");
    public static readonly StableId Destroy = StableId.Parse("effect.destroy");
    public static readonly StableId Move = StableId.Parse("effect.move");
    public static readonly StableId Transform = StableId.Parse("effect.transform");
    public static readonly StableId ModifyStat = StableId.Parse("effect.modify_stat");
    public static readonly StableId AddModifier = StableId.Parse("effect.add_modifier");
    public static readonly StableId RemoveModifier = StableId.Parse("effect.remove_modifier");
    public static readonly StableId AddKeyword = StableId.Parse("effect.add_keyword");
    public static readonly StableId RemoveKeyword = StableId.Parse("effect.remove_keyword");
    public static readonly StableId ChangeResource = StableId.Parse("effect.change_resource");
    public static readonly StableId CreateCard = StableId.Parse("effect.create_card");
    public static readonly StableId CopyCard = StableId.Parse("effect.copy_card");
    public static readonly StableId SetStat = StableId.Parse("effect.set_stat");
    public static readonly StableId Conditional = StableId.Parse("effect.conditional");
}

public static class DefaultEffectRegistry
{
    public static EffectRegistry Create()
    {
        return new EffectRegistry(new[]
        {
            Register(EffectIds.Damage, "effect.damage", "Deal calculated damage to selected targets.",
                Selector("target"), Formula("amount"), Enum("damageType", "PHYSICAL", "ARCANE", "TRUE")),
            Register(EffectIds.Heal, "effect.heal", "Restore calculated health to selected targets.",
                Selector("target"), Formula("amount")),
            Register(EffectIds.Draw, "effect.draw", "Draw cards for a selected player.",
                Selector("target"), Formula("amount")),
            Register(EffectIds.Discard, "effect.discard", "Move selected cards from hand to discard or graveyard.",
                Selector("target")),
            Register(EffectIds.Summon, "effect.summon", "Summon a card definition into a legal board location.",
                StableId("cardDefinitionId", RegistryReferenceKind.CardDefinition), Selector("destination")),
            Register(EffectIds.Destroy, "effect.destroy", "Destroy selected units through authoritative death processing.",
                Selector("target")),
            Register(EffectIds.Move, "effect.move", "Move selected entities between legal locations or zones.",
                Selector("target"), Selector("destination")),
            Register(EffectIds.Transform, "effect.transform", "Transform selected entities into another card definition.",
                Selector("target"), StableId("cardDefinitionId", RegistryReferenceKind.CardDefinition)),
            Register(EffectIds.ModifyStat, "effect.modify_stat", "Apply a duration-bound stat modification.",
                Selector("target"), StableId("statId", RegistryReferenceKind.Stat), Enum("operation", "ADD", "MULTIPLY", "SET", "MINIMUM", "MAXIMUM"), Formula("value"), StableId("durationId", RegistryReferenceKind.Duration)),
            Register(EffectIds.AddModifier, "effect.add_modifier", "Attach a reusable modifier definition to selected targets.",
                Selector("target"), StableId("modifierId", RegistryReferenceKind.Modifier)),
            Register(EffectIds.RemoveModifier, "effect.remove_modifier", "Remove matching modifiers from selected targets.",
                Selector("target"), StableId("modifierId", RegistryReferenceKind.Modifier)),
            Register(EffectIds.AddKeyword, "effect.add_keyword", "Grant a registered gameplay keyword.",
                Selector("target"), StableId("keywordId", RegistryReferenceKind.Keyword), StableId("durationId", RegistryReferenceKind.Duration)),
            Register(EffectIds.RemoveKeyword, "effect.remove_keyword", "Remove a registered gameplay keyword.",
                Selector("target"), StableId("keywordId", RegistryReferenceKind.Keyword)),
            Register(EffectIds.ChangeResource, "effect.change_resource", "Modify an authoritative resource value.",
                Selector("target"), StableId("resourceId", RegistryReferenceKind.Resource), Formula("amount")),
            Register(EffectIds.CreateCard, "effect.create_card", "Create a card instance in a selected zone.",
                Selector("target"), StableId("cardDefinitionId", RegistryReferenceKind.CardDefinition)),
            Register(EffectIds.CopyCard, "effect.copy_card", "Create a copy of a selected card instance.",
                Selector("target"), Selector("destination")),
            Register(EffectIds.SetStat, "effect.set_stat", "Set a target stat to a calculated value.",
                Selector("target"), StableId("statId", RegistryReferenceKind.Stat), Formula("value")),
            Register(EffectIds.Conditional, "effect.conditional", "Resolve one effect branch when a structured condition succeeds and an optional branch otherwise.",
                Condition("if"), EffectList("thenEffects"), EffectList("elseEffects", required: false))
        });
    }

    private static EffectRegistration Register(
        StableId id,
        string labelKey,
        string description,
        params ParameterSchema[] parameters) =>
        new(new PrimitiveDescriptor(id, labelKey, description, parameters));

    private static ParameterSchema Selector(string name) =>
        new(name, ParameterKind.Selector, editorHint: "target-selector-builder");

    private static ParameterSchema Formula(string name) =>
        new(name, ParameterKind.Formula, editorHint: "formula-builder");

    private static ParameterSchema StableId(string name, RegistryReferenceKind referenceKind = RegistryReferenceKind.None) =>
        new(name, ParameterKind.StableId, editorHint: "registry-select", referenceKind: referenceKind);

    private static ParameterSchema Condition(string name) =>
        new(name, ParameterKind.Condition, editorHint: "condition-builder");

    private static ParameterSchema EffectList(string name, bool required = true) =>
        new(name, ParameterKind.EffectList, required: required, editorHint: "effect-list-builder");

    private static ParameterSchema Enum(string name, params string[] allowedValues) =>
        new(name, ParameterKind.Enum, allowedValues: allowedValues, editorHint: "select");
}
