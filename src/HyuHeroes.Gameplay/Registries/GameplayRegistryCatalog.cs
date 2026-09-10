/**
 * GAMEPLAY_REGISTRY_CATALOG
 * Purpose: Collects the controlled schema vocabulary exposed to validation and future dashboard metadata generation.
 * Connections: Owns taxonomy, trigger, condition, formula, effect, duration, and usage-limit registries.
 * Risk: High because registry changes alter which structured gameplay definitions are considered legal.
 */
using System;
using HyuHeroes.Gameplay.Authoring;
using HyuHeroes.Gameplay.Core;
using HyuHeroes.Gameplay.Effects;
using HyuHeroes.Gameplay.Formulas;

namespace HyuHeroes.Gameplay.Registries;

public sealed class DescriptorRegistration : IRegistryEntry
{
    public DescriptorRegistration(PrimitiveDescriptor descriptor)
    {
        Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
    }

    public StableId Id => Descriptor.Id;
    public PrimitiveDescriptor Descriptor { get; }
}

public sealed class GameplayRegistryCatalog
{
    public GameplayRegistryCatalog(
        PrimitiveRegistry<RegistryEntry> alignments,
        PrimitiveRegistry<RegistryEntry> classes,
        PrimitiveRegistry<RegistryEntry> factions,
        PrimitiveRegistry<RegistryEntry> tags,
        PrimitiveRegistry<RegistryEntry> keywords,
        PrimitiveRegistry<RegistryEntry> stats,
        PrimitiveRegistry<RegistryEntry> resources,
        PrimitiveRegistry<RegistryEntry> phases,
        PrimitiveRegistry<RegistryEntry> formulaVariables,
        PrimitiveRegistry<DescriptorRegistration> triggers,
        PrimitiveRegistry<DescriptorRegistration> conditions,
        PrimitiveRegistry<DescriptorRegistration> durations,
        PrimitiveRegistry<DescriptorRegistration> usageLimits,
        FormulaOperatorRegistry formulaOperators,
        EffectRegistry effects)
    {
        Alignments = alignments ?? throw new ArgumentNullException(nameof(alignments));
        Classes = classes ?? throw new ArgumentNullException(nameof(classes));
        Factions = factions ?? throw new ArgumentNullException(nameof(factions));
        Tags = tags ?? throw new ArgumentNullException(nameof(tags));
        Keywords = keywords ?? throw new ArgumentNullException(nameof(keywords));
        Stats = stats ?? throw new ArgumentNullException(nameof(stats));
        Resources = resources ?? throw new ArgumentNullException(nameof(resources));
        Phases = phases ?? throw new ArgumentNullException(nameof(phases));
        FormulaVariables = formulaVariables ?? throw new ArgumentNullException(nameof(formulaVariables));
        Triggers = triggers ?? throw new ArgumentNullException(nameof(triggers));
        Conditions = conditions ?? throw new ArgumentNullException(nameof(conditions));
        Durations = durations ?? throw new ArgumentNullException(nameof(durations));
        UsageLimits = usageLimits ?? throw new ArgumentNullException(nameof(usageLimits));
        FormulaOperators = formulaOperators ?? throw new ArgumentNullException(nameof(formulaOperators));
        Effects = effects ?? throw new ArgumentNullException(nameof(effects));
    }

    public PrimitiveRegistry<RegistryEntry> Alignments { get; }
    public PrimitiveRegistry<RegistryEntry> Classes { get; }
    public PrimitiveRegistry<RegistryEntry> Factions { get; }
    public PrimitiveRegistry<RegistryEntry> Tags { get; }
    public PrimitiveRegistry<RegistryEntry> Keywords { get; }
    public PrimitiveRegistry<RegistryEntry> Stats { get; }
    public PrimitiveRegistry<RegistryEntry> Resources { get; }
    public PrimitiveRegistry<RegistryEntry> Phases { get; }
    public PrimitiveRegistry<RegistryEntry> FormulaVariables { get; }
    public PrimitiveRegistry<DescriptorRegistration> Triggers { get; }
    public PrimitiveRegistry<DescriptorRegistration> Conditions { get; }
    public PrimitiveRegistry<DescriptorRegistration> Durations { get; }
    public PrimitiveRegistry<DescriptorRegistration> UsageLimits { get; }
    public FormulaOperatorRegistry FormulaOperators { get; }
    public EffectRegistry Effects { get; }

    public static GameplayRegistryCatalog CreateSchemaV1()
    {
        return new GameplayRegistryCatalog(
            DefaultTaxonomy.CreateAlignments(),
            DefaultTaxonomy.CreateClasses(),
            new PrimitiveRegistry<RegistryEntry>(),
            new PrimitiveRegistry<RegistryEntry>(),
            new PrimitiveRegistry<RegistryEntry>(),
            DefaultTaxonomy.CreateStats(),
            DefaultTaxonomy.CreateResources(),
            DefaultTaxonomy.CreatePhases(),
            DefaultTaxonomy.CreateFormulaVariables(),
            DefaultAuthoringDescriptors.CreateTriggers(),
            DefaultAuthoringDescriptors.CreateConditions(),
            DefaultAuthoringDescriptors.CreateDurations(),
            DefaultAuthoringDescriptors.CreateUsageLimits(),
            DefaultFormulaOperators.Create(),
            DefaultEffectRegistry.Create());
    }
}

public static class DefaultTaxonomy
{
    public static PrimitiveRegistry<RegistryEntry> CreateAlignments() =>
        new(new[]
        {
            Entry("alignment.neutral", "alignment.neutral", "Neutral alignment with no project-specific allegiance.")
        });

    public static PrimitiveRegistry<RegistryEntry> CreateClasses() =>
        new(new[]
        {
            Entry("class.striker", "class.striker", "Pressure and direct damage role."),
            Entry("class.tank", "class.tank", "Durability and protection role."),
            Entry("class.support", "class.support", "Ally enhancement and utility role."),
            Entry("class.control", "class.control", "Board manipulation and denial role."),
            Entry("class.ramp", "class.ramp", "Resource acceleration and scaling role."),
            Entry("class.combo", "class.combo", "Synergy and sequence-dependent role.")
        });

    public static PrimitiveRegistry<RegistryEntry> CreateStats() =>
        new(new[]
        {
            Entry("stat.cost", "stat.cost", "Base resource cost to play the definition."),
            Entry("stat.attack", "stat.attack", "Base attack contribution used by combat or formulas."),
            Entry("stat.max_health", "stat.max_health", "Base maximum health for durable entities."),
            Entry("stat.defense", "stat.defense", "Optional defense value used only by the canonical damage pipeline.")
        });

    public static PrimitiveRegistry<RegistryEntry> CreateResources() =>
        new(new[]
        {
            Entry("resource.primary", "resource.primary", "Default configurable match resource.")
        });

    public static PrimitiveRegistry<RegistryEntry> CreatePhases() =>
        new(new[]
        {
            Entry("phase.start_turn", "phase.start_turn", "Start-of-turn timing window."),
            Entry("phase.resource_draw", "phase.resource_draw", "Resource refresh and draw timing window."),
            Entry("phase.main_action", "phase.main_action", "Primary player action timing window."),
            Entry("phase.reaction_response", "phase.reaction_response", "Optional reaction or response timing window."),
            Entry("phase.combat", "phase.combat", "Lane combat resolution timing window."),
            Entry("phase.end_turn", "phase.end_turn", "End-of-turn timing window.")
        });

    public static PrimitiveRegistry<RegistryEntry> CreateFormulaVariables() =>
        new(new[]
        {
            Entry("variable.source.attack", "variable.source.attack", "Effective attack of the source entity."),
            Entry("variable.source.health", "variable.source.health", "Current health of the source entity."),
            Entry("variable.source.max_health", "variable.source.max_health", "Effective maximum health of the source entity."),
            Entry("variable.target.attack", "variable.target.attack", "Effective attack of the active target entity."),
            Entry("variable.target.health", "variable.target.health", "Current health of the active target entity."),
            Entry("variable.owner.resource", "variable.owner.resource", "Current primary resource of the source owner."),
            Entry("variable.opponent.resource", "variable.opponent.resource", "Current primary resource of the opposing player."),
            Entry("variable.turn.number", "variable.turn.number", "Current authoritative turn number.")
        });

    private static RegistryEntry Entry(string id, string labelKey, string description) =>
        new(StableId.Parse(id), labelKey, description);
}

public static class TriggerIds
{
    public static readonly StableId OnPlay = StableId.Parse("trigger.on_play");
    public static readonly StableId OnSummoned = StableId.Parse("trigger.on_summoned");
    public static readonly StableId OnAttack = StableId.Parse("trigger.on_attack");
    public static readonly StableId OnDamageDealt = StableId.Parse("trigger.on_damage_dealt");
    public static readonly StableId OnDamaged = StableId.Parse("trigger.on_damaged");
    public static readonly StableId OnDeath = StableId.Parse("trigger.on_death");
    public static readonly StableId StartOfTurn = StableId.Parse("trigger.start_of_turn");
    public static readonly StableId EndOfTurn = StableId.Parse("trigger.end_of_turn");
    public static readonly StableId BeforeCombat = StableId.Parse("trigger.before_combat");
    public static readonly StableId AfterCombat = StableId.Parse("trigger.after_combat");
    public static readonly StableId OnCardDrawn = StableId.Parse("trigger.on_card_drawn");
    public static readonly StableId OnResourceSpent = StableId.Parse("trigger.on_resource_spent");
}

public static class ConditionIds
{
    public static readonly StableId HasTag = StableId.Parse("condition.has_tag");
    public static readonly StableId HasKeyword = StableId.Parse("condition.has_keyword");
    public static readonly StableId StatCompare = StableId.Parse("condition.stat_compare");
    public static readonly StableId ResourceCompare = StableId.Parse("condition.resource_compare");
    public static readonly StableId LaneIsEmpty = StableId.Parse("condition.lane_is_empty");
    public static readonly StableId CardTypeIs = StableId.Parse("condition.card_type_is");
    public static readonly StableId OwnerIs = StableId.Parse("condition.owner_is");
    public static readonly StableId TargetIsDamaged = StableId.Parse("condition.target_is_damaged");
    public static readonly StableId CountMatchingCompare = StableId.Parse("condition.count_matching_compare");
    public static readonly StableId TurnCompare = StableId.Parse("condition.turn_compare");
    public static readonly StableId PhaseIs = StableId.Parse("condition.phase_is");
    public static readonly StableId ZoneIs = StableId.Parse("condition.zone_is");
}

public static class DurationIds
{
    public static readonly StableId Permanent = StableId.Parse("duration.permanent");
    public static readonly StableId UntilEndOfTurn = StableId.Parse("duration.until_end_of_turn");
    public static readonly StableId UntilStartOfNextTurn = StableId.Parse("duration.until_start_of_next_turn");
    public static readonly StableId WhileSourceExists = StableId.Parse("duration.while_source_exists");
    public static readonly StableId WhileInZone = StableId.Parse("duration.while_in_zone");
    public static readonly StableId ForNTurns = StableId.Parse("duration.for_n_turns");
}

public static class UsageLimitIds
{
    public static readonly StableId OncePerTurn = StableId.Parse("limit.once_per_turn");
    public static readonly StableId OncePerMatch = StableId.Parse("limit.once_per_match");
    public static readonly StableId MaxPerTurn = StableId.Parse("limit.max_n_times_per_turn");
    public static readonly StableId MaxWhileInZone = StableId.Parse("limit.max_n_times_while_in_zone");
    public static readonly StableId CooldownTurns = StableId.Parse("limit.cooldown_turns");
}

public static class DefaultAuthoringDescriptors
{
    private static readonly string[] CompareOperators = { "EQ", "NE", "LT", "LTE", "GT", "GTE" };

    public static PrimitiveRegistry<DescriptorRegistration> CreateTriggers() =>
        new(new[]
        {
            Descriptor(TriggerIds.OnPlay, "trigger.on_play", "Eligible after the source card is legally played."),
            Descriptor(TriggerIds.OnSummoned, "trigger.on_summoned", "Eligible after the source unit enters the board."),
            Descriptor(TriggerIds.OnAttack, "trigger.on_attack", "Eligible when the source begins an authoritative attack."),
            Descriptor(TriggerIds.OnDamageDealt, "trigger.on_damage_dealt", "Eligible after the source deals damage."),
            Descriptor(TriggerIds.OnDamaged, "trigger.on_damaged", "Eligible after the source receives damage."),
            Descriptor(TriggerIds.OnDeath, "trigger.on_death", "Eligible when authoritative death processing records source death."),
            Descriptor(TriggerIds.StartOfTurn, "trigger.start_of_turn", "Eligible at a defined start-of-turn timing window."),
            Descriptor(TriggerIds.EndOfTurn, "trigger.end_of_turn", "Eligible at a defined end-of-turn timing window."),
            Descriptor(TriggerIds.BeforeCombat, "trigger.before_combat", "Eligible immediately before combat resolution."),
            Descriptor(TriggerIds.AfterCombat, "trigger.after_combat", "Eligible immediately after combat resolution."),
            Descriptor(TriggerIds.OnCardDrawn, "trigger.on_card_drawn", "Eligible after the configured owner draws a card."),
            Descriptor(TriggerIds.OnResourceSpent, "trigger.on_resource_spent", "Eligible after the configured owner spends resources.")
        });

    public static PrimitiveRegistry<DescriptorRegistration> CreateConditions() =>
        new(new[]
        {
            Descriptor(ConditionIds.HasTag, "condition.has_tag", "Selected subject has a registered tag.", Selector("subject"), StableIdParameter("tagId", RegistryReferenceKind.Tag)),
            Descriptor(ConditionIds.HasKeyword, "condition.has_keyword", "Selected subject has a registered gameplay keyword.", Selector("subject"), StableIdParameter("keywordId", RegistryReferenceKind.Keyword)),
            Descriptor(ConditionIds.StatCompare, "condition.stat_compare", "Compare a selected subject stat against a formula.", Selector("subject"), StableIdParameter("statId", RegistryReferenceKind.Stat), Compare("operator"), Formula("value")),
            Descriptor(ConditionIds.ResourceCompare, "condition.resource_compare", "Compare a selected player's resource against a formula.", Selector("subject"), StableIdParameter("resourceId", RegistryReferenceKind.Resource), Compare("operator"), Formula("value")),
            Descriptor(ConditionIds.LaneIsEmpty, "condition.lane_is_empty", "Selected lane has no occupying unit.", Selector("subject")),
            Descriptor(ConditionIds.CardTypeIs, "condition.card_type_is", "Selected card has the configured fundamental card type.", Selector("subject"), Enum("cardType", "UNIT", "ACTION", "ENVIRONMENT", "HERO_ABILITY")),
            Descriptor(ConditionIds.OwnerIs, "condition.owner_is", "Selected entity is controlled by the configured relation.", Selector("subject"), Enum("relation", "FRIENDLY", "ENEMY")),
            Descriptor(ConditionIds.TargetIsDamaged, "condition.target_is_damaged", "Selected durable entity is below effective maximum health.", Selector("subject")),
            Descriptor(ConditionIds.CountMatchingCompare, "condition.count_matching_compare", "Compare a selector result count against a formula.", Selector("subject"), Compare("operator"), Formula("value")),
            Descriptor(ConditionIds.TurnCompare, "condition.turn_compare", "Compare the current turn number against a formula.", Compare("operator"), Formula("value")),
            Descriptor(ConditionIds.PhaseIs, "condition.phase_is", "Current authoritative phase matches a registered phase ID.", StableIdParameter("phaseId", RegistryReferenceKind.Phase)),
            Descriptor(ConditionIds.ZoneIs, "condition.zone_is", "Selected card currently belongs to a configured zone.", Selector("subject"), Enum("zone", "BOARD", "HAND", "DECK", "GRAVEYARD"))
        });

    public static PrimitiveRegistry<DescriptorRegistration> CreateDurations() =>
        new(new[]
        {
            Descriptor(DurationIds.Permanent, "duration.permanent", "Persists until explicitly removed or the match ends."),
            Descriptor(DurationIds.UntilEndOfTurn, "duration.until_end_of_turn", "Expires at the current turn end checkpoint."),
            Descriptor(DurationIds.UntilStartOfNextTurn, "duration.until_start_of_next_turn", "Expires at the next start-of-turn checkpoint."),
            Descriptor(DurationIds.WhileSourceExists, "duration.while_source_exists", "Persists while the originating source remains valid."),
            Descriptor(DurationIds.WhileInZone, "duration.while_in_zone", "Persists while the affected entity remains in a configured zone.", Enum("zone", "BOARD", "HAND", "DECK", "GRAVEYARD")),
            Descriptor(DurationIds.ForNTurns, "duration.for_n_turns", "Persists for an exact number of authoritative turns.", Integer("turns", 1, 100))
        });

    public static PrimitiveRegistry<DescriptorRegistration> CreateUsageLimits() =>
        new(new[]
        {
            Descriptor(UsageLimitIds.OncePerTurn, "limit.once_per_turn", "Ability may resolve at most once per turn."),
            Descriptor(UsageLimitIds.OncePerMatch, "limit.once_per_match", "Ability may resolve at most once per match."),
            Descriptor(UsageLimitIds.MaxPerTurn, "limit.max_n_times_per_turn", "Ability may resolve a configured maximum each turn.", Integer("count", 1, 100)),
            Descriptor(UsageLimitIds.MaxWhileInZone, "limit.max_n_times_while_in_zone", "Ability may resolve a configured maximum while its source stays in a zone.", Integer("count", 1, 100)),
            Descriptor(UsageLimitIds.CooldownTurns, "limit.cooldown_turns", "Ability must wait configured turns between resolutions.", Integer("turns", 1, 100))
        });

    private static DescriptorRegistration Descriptor(
        StableId id,
        string labelKey,
        string description,
        params ParameterSchema[] parameters) =>
        new(new PrimitiveDescriptor(id, labelKey, description, parameters));

    private static ParameterSchema Selector(string name) =>
        new(name, ParameterKind.Selector, editorHint: "target-selector-builder");

    private static ParameterSchema Formula(string name) =>
        new(name, ParameterKind.Formula, editorHint: "formula-builder");

    private static ParameterSchema StableIdParameter(string name, RegistryReferenceKind referenceKind = RegistryReferenceKind.None) =>
        new(name, ParameterKind.StableId, editorHint: "registry-select", referenceKind: referenceKind);

    private static ParameterSchema Compare(string name) =>
        new(name, ParameterKind.Enum, allowedValues: CompareOperators, editorHint: "select");

    private static ParameterSchema Enum(string name, params string[] values) =>
        new(name, ParameterKind.Enum, allowedValues: values, editorHint: "select");

    private static ParameterSchema Integer(string name, int minimum, int maximum) =>
        new(name, ParameterKind.Integer, minimum: minimum, maximum: maximum, editorHint: "number");
}
