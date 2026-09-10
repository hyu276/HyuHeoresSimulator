# Data-Driven Gameplay Authoring Rules

## Purpose

This document defines how cards, heroes, units, abilities, costs, statistics, targeting, conditions, formulas, and balance data MUST be represented so that future content can be created and adjusted through an admin dashboard without editing gameplay source code for ordinary content changes.

The design target is:

> Programmers create reusable gameplay primitives. Creative and balance teams compose those primitives through structured data.

The admin dashboard is an authoring tool. It MUST NOT become a second rules engine.

---

## 1. Core authoring architecture

Gameplay content SHOULD flow through the following pipeline:

```text
Admin Dashboard
    ↓
Structured Content Schema
    ↓
Validation
    ↓
Versioned Published Content Package
    ↓
Runtime Definition Loader
    ↓
Authoritative Simulation Engine
```

The simulation engine owns gameplay semantics. The dashboard only creates valid definitions that the engine understands.

Ordinary balance/content changes such as changing attack from 3 to 4, changing cost from 2 to 3, changing a target from `ENEMY_UNIT` to `ANY_UNIT`, or adding an existing reusable effect MUST NOT require a code change.

A code change is required only when the requested mechanic cannot be expressed by the existing primitive vocabulary.

---

## 2. Recommended implementation languages and formats

The repository SHOULD use the following technology boundaries unless an ADR deliberately changes them.

### C#

C# is the authoritative gameplay language.

Use it for:

- deterministic simulation;
- commands and validation;
- combat resolution;
- effect execution;
- target resolution;
- modifier calculation;
- trigger processing;
- seeded RNG;
- content validation at runtime/build time;
- replay-compatible game state;
- future authoritative server simulation where practical.

Gameplay truth MUST NOT depend on TypeScript, browser code, or dashboard behavior.

### TypeScript

TypeScript SHOULD be used for the future admin dashboard and content-management backend/application layer.

Use it for:

- forms and drag/drop authoring UI;
- schema-driven editors;
- condition builders;
- effect list editors;
- formula builders;
- previewing generated descriptions;
- validation feedback;
- publishing workflows;
- API DTOs and content-management operations.

TypeScript MUST NOT redefine gameplay semantics independently from the C# engine.

### SQL / PostgreSQL

A relational database such as PostgreSQL SHOULD be used for persisted content-management data when the admin system is introduced.

Use it for:

- card/hero/ability records;
- revisions;
- draft/published status;
- localization references;
- tags and classifications;
- audit history;
- user/editor permissions;
- content package versions.

The live match simulation SHOULD consume a validated, versioned content snapshot rather than querying mutable authoring tables during resolution.

### JSON

JSON SHOULD be the primary transport/published content format between authoring systems and runtime loaders.

JSON is data, not executable code.

### JSON Schema / OpenAPI

A machine-readable schema SHOULD define DTO structure, allowed enum values, required fields, ranges, and validation constraints.

Where possible, C# and TypeScript DTOs SHOULD be generated from or validated against a shared schema contract to reduce schema drift.

### Optional Python

Python MAY later be introduced for offline balance simulation, analytics, Monte Carlo testing, batch validation, or data science. It SHOULD NOT be required by the runtime game engine.

---

## 3. Stable gameplay identity

Every persistent gameplay definition MUST have an immutable stable ID independent of display name.

Examples:

```text
hero.astra_warden
card.ember_fox
ability.burning_entry
keyword.guard
faction.aurora
class.striker
```

Renaming `Ember Fox` to `Solar Fox` MUST NOT change `card.ember_fox` unless it is intentionally a different gameplay definition.

Do not use database row position, list index, localized text, artwork filename, or display name as gameplay identity.

Every published definition SHOULD include at least:

- `id`;
- `schemaVersion`;
- `revision`;
- `status`;
- display/localization key;
- gameplay classification;
- gameplay data;
- optional presentation references.

Recommended authoring statuses:

```text
DRAFT
REVIEW
APPROVED
PUBLISHED
DEPRECATED
```

Published revisions SHOULD be immutable. Changes create a new revision.

---

## 4. Separate mechanics from balance data

Mechanics answer **what the game does**.

Balance data answers **how much, how often, how expensive, and for how long**.

For example, `DamageEffect` is a mechanic. `amount = 3` is balance data.

Do not create code classes such as:

```text
DealThreeDamageEffect
DealFourDamageEffect
CheapFireballEffect
ExpensiveFireballEffect
```

Create one reusable primitive such as:

```text
DAMAGE(targetSelector, valueExpression, damageType)
```

and configure its values through content data.

---

## 5. Card / entity definition model

A normal playable entity/card definition SHOULD be composed from structured sections rather than bespoke source code.

Recommended conceptual model:

```text
Definition
├── Identity
├── Classification
├── BaseStats
├── Tags
├── Keywords
├── Abilities[]
├── Restrictions[]
└── PresentationReferences
```

### Identity

Examples:

- stable ID;
- localization key;
- revision;
- content set/version.

### Classification

Classification SHOULD use controlled enum/reference values.

Examples:

```text
CardType:
- UNIT
- ACTION
- ENVIRONMENT
- HERO_ABILITY

Faction / Alignment:
- controlled project-specific faction IDs

Class / Role:
- STRIKER
- TANK
- SUPPORT
- CONTROL
- RAMP
- COMBO
```

Exact game-specific classes may evolve. They MUST be registered centrally rather than introduced as arbitrary strings in individual content records.

### Tags

Tags describe searchable/groupable traits and may participate in conditions.

Examples:

```text
BEAST
MACHINE
ARCANE
FIRE
FLYING
UNDEAD
SUMMONED
TOKEN
```

Tags MUST use stable IDs from a controlled registry.

A tag does not automatically create gameplay behavior unless a rule explicitly queries that tag.

### Keywords

Keywords represent defined gameplay mechanics such as a future `GUARD`, `PIERCE`, or `LIFESTEAL` mechanic.

Keywords MUST NOT be treated as decorative tags when they carry rules semantics.

---

## 6. Ability composition model

An ability SHOULD be represented as reusable structured composition:

```text
Ability
├── Trigger
├── Conditions
├── Targets / Choices
├── Effects[]
├── Value Expressions
├── Limits
├── Duration
└── Resolution Metadata
```

The creative team should be able to build most abilities by selecting these components in the admin dashboard.

### 6.1 Trigger

A trigger answers **when does this ability become eligible to resolve?**

Examples:

```text
ON_PLAY
ON_SUMMONED
ON_ATTACK
ON_DAMAGE_DEALT
ON_DAMAGED
ON_DEATH
START_OF_TURN
END_OF_TURN
BEFORE_COMBAT
AFTER_COMBAT
ON_CARD_DRAWN
ON_RESOURCE_SPENT
```

New triggers require code only when they represent a genuinely new timing window not expressible through the existing event/filter system.

### 6.2 Conditions

Conditions answer **is the ability allowed to continue?**

The dashboard MUST represent conditions as a structured boolean tree rather than arbitrary code.

Supported logical operators SHOULD include:

```text
ALL       // logical AND
ANY       // logical OR
NOT
```

Leaf predicates may include reusable concepts such as:

```text
HAS_TAG
HAS_KEYWORD
STAT_COMPARE
HEALTH_COMPARE
RESOURCE_COMPARE
LANE_IS_EMPTY
LANE_TYPE_IS
CARD_TYPE_IS
OWNER_IS
TARGET_IS_DAMAGED
COUNT_MATCHING_COMPARE
TURN_COMPARE
PHASE_IS
ZONE_IS
```

Conceptual example:

```text
ALL
├── SOURCE.HAS_TAG(BEAST)
└── ANY
    ├── OWNER.RESOURCE >= 3
    └── TARGET.IS_DAMAGED
```

Do NOT allow creative users to enter free-form C#, JavaScript, SQL, or `eval()` expressions.

### 6.3 Target selector

A target selector answers **what entity/entities can an effect operate on?**

Reusable selectors SHOULD support composable dimensions such as:

```text
Scope:
- SELF
- SOURCE
- OWNER
- OPPONENT
- UNIT
- HERO
- CARD
- LANE

Relation:
- FRIENDLY
- ENEMY
- ANY

Zone:
- BOARD
- HAND
- DECK
- GRAVEYARD

Location:
- CURRENT_LANE
- ADJACENT_LANES
- LEFTMOST
- RIGHTMOST
- ALL_LANES

Filter:
- HAS_TAG
- HAS_KEYWORD
- CARD_TYPE
- STAT_COMPARE
- DAMAGED

Selection:
- ALL
- FIRST
- HIGHEST_ATTACK
- LOWEST_HEALTH
- RANDOM_N
- PLAYER_CHOICE
```

Every selector with ties MUST define deterministic tie-breaking.

`RANDOM_N` MUST use simulation-owned seeded RNG.

`PLAYER_CHOICE` MUST create an explicit pending decision rather than silently choosing a default target.

### 6.4 Effects

Effects answer **what state-changing operation occurs?**

The reusable effect registry SHOULD initially prioritize primitives such as:

```text
DAMAGE
HEAL
DRAW
DISCARD
SUMMON
DESTROY
MOVE
TRANSFORM
MODIFY_STAT
ADD_MODIFIER
REMOVE_MODIFIER
ADD_KEYWORD
REMOVE_KEYWORD
CHANGE_RESOURCE
CREATE_CARD
COPY_CARD
SET_STAT
```

An effect definition SHOULD reference selectors and values rather than hard-code named cards.

### 6.5 Limits

Reusable limits SHOULD include patterns such as:

```text
ONCE_PER_TURN
ONCE_PER_MATCH
MAX_N_TIMES_PER_TURN
MAX_N_TIMES_WHILE_IN_ZONE
COOLDOWN_TURNS
```

Limits belong to authoritative simulation state where required. The dashboard merely configures them.

### 6.6 Duration

Modifiers/effects that persist SHOULD use controlled duration models such as:

```text
PERMANENT
UNTIL_END_OF_TURN
UNTIL_START_OF_NEXT_TURN
WHILE_SOURCE_EXISTS
WHILE_IN_ZONE
FOR_N_TURNS
```

Do not express durations using wall-clock seconds unless the final game explicitly contains real-time mechanics.

---

## 7. Formula system

Costs, attack values, defense values, damage, healing, scaling, and derived values SHOULD use a safe declarative expression system rather than arbitrary executable code.

A formula MUST be represented as an expression tree / AST constructed from registered operators and variables.

Recommended operators:

```text
CONSTANT
ADD
SUBTRACT
MULTIPLY
DIVIDE
MIN
MAX
CLAMP
ABS
FLOOR
CEIL
COUNT
```

Recommended readable variables may include:

```text
SOURCE.ATTACK
SOURCE.HEALTH
SOURCE.MAX_HEALTH
TARGET.ATTACK
TARGET.HEALTH
OWNER.RESOURCE
OPPONENT.RESOURCE
TURN.NUMBER
COUNT(FRIENDLY_UNIT)
COUNT(ENEMY_UNIT)
```

Example conceptual formula:

```text
MAX(
  1,
  SOURCE.ATTACK + COUNT(FRIENDLY_UNIT WITH TAG=BEAST)
)
```

The dashboard SHOULD provide a visual formula builder using dropdowns and nested blocks rather than a free-form programming text box.

Formula evaluation MUST be deterministic and side-effect free.

Formula evaluation MUST NOT consume RNG.

Random selection or random values must be represented as explicit gameplay primitives using the authoritative RNG system.

---

## 8. Stat architecture

Do not mutate shared base definitions during a match.

Recommended stat layers:

```text
Base Value
    ↓
Permanent Match Modifiers
    ↓
Temporary Modifiers
    ↓
Contextual Modifiers
    ↓
Final Clamp / Rules
    ↓
Effective Value
```

Common numeric stat IDs MAY include:

```text
COST
ATTACK
MAX_HEALTH
ARMOR / DEFENSE     // only if the final game design uses it
SPEED / PRIORITY    // only if required by turn rules
```

Do not add a stat merely because another game has it. Every stat must have defined gameplay semantics.

Modifier definitions SHOULD include:

- affected stat ID;
- operation;
- value/formula;
- source;
- duration;
- stacking rule;
- priority/layer.

Recommended modifier operations:

```text
ADD
MULTIPLY
SET
MINIMUM
MAXIMUM
```

Their ordering MUST be globally defined and tested.

---

## 9. Damage calculation pipeline

Damage MUST use one canonical pipeline rather than different formulas inside individual cards.

A future baseline may resemble:

```text
Base Damage Expression
    ↓
Source Damage Modifiers
    ↓
Damage-Type Modifiers
    ↓
Target Vulnerability / Resistance
    ↓
Armor / Shield / Prevention Rules
    ↓
Final Damage Clamp
    ↓
Health Loss
    ↓
Damage Event
    ↓
State-Based Death Check
```

The exact defense/armor formula is a game-design decision and MUST be documented before implementation.

Abilities MAY bypass a stage only through explicit flags/primitives such as a future `IGNORE_ARMOR`; they MUST NOT implement private alternative damage math.

If attack combat and ability damage use different semantics, the distinction MUST be explicit in the damage context rather than duplicated code.

---

## 10. Controlled registries

The engine SHOULD expose central registries/catalogs for authorable primitive types.

Examples:

```text
TriggerRegistry
EffectRegistry
ConditionRegistry
TargetSelectorRegistry
KeywordRegistry
TagRegistry
StatRegistry
FormulaOperatorRegistry
DurationRegistry
```

Each registry entry SHOULD expose machine-readable metadata sufficient for the dashboard to build an editor:

- stable type ID;
- display label/localization key;
- description;
- parameter schema;
- allowed value ranges;
- allowed target/entity categories;
- editor control hint;
- compatibility constraints where useful.

Example conceptual metadata:

```json
{
  "type": "DAMAGE",
  "parameters": {
    "target": { "kind": "TargetSelector", "required": true },
    "amount": { "kind": "ValueExpression", "required": true },
    "damageType": {
      "kind": "Enum",
      "options": ["PHYSICAL", "ARCANE", "TRUE"]
    }
  }
}
```

The dashboard SHOULD derive its dropdowns and editors from this registry/schema metadata instead of maintaining hand-written duplicate lists.

---

## 11. Creative-team authoring UX contract

A content creator SHOULD be able to build an ordinary ability without source-code knowledge.

The future admin dashboard SHOULD provide controls such as:

- dropdown for card type/class/faction;
- searchable multi-select for tags;
- keyword picker;
- numeric inputs with min/max constraints;
- trigger dropdown;
- nested ALL/ANY/NOT condition builder;
- target selector builder;
- drag-sortable effect sequence;
- formula/value builder;
- duration dropdown;
- usage-limit dropdown;
- generated human-readable preview;
- validation panel;
- simulation/test button;
- revision diff before publish.

Example authoring intent:

```text
WHEN: On Play
IF: Target is an enemy unit AND target is damaged
TARGET: Chosen enemy unit
DO:
  1. Deal 3 damage
  2. If target dies, draw 1 card
```

The stored representation MUST be structured data, not the natural-language sentence.

Natural-language card text is presentation/localization and MUST NOT be parsed to determine gameplay behavior.

---

## 12. Ability nesting and branching

Conditional branches inside an ability SHOULD use explicit structured nodes.

Example:

```text
Sequence
├── Damage(target, 3)
└── Conditional
    ├── If: TARGET.IS_DEAD
    ├── Then: Draw(owner, 1)
    └── Else: ModifyStat(target, ATTACK, -1, END_OF_TURN)
```

The project MAY support nested conditions, but nesting depth SHOULD be bounded and validated to keep content understandable and prevent pathological execution graphs.

Loops SHOULD NOT be directly authorable by the dashboard.

Repeated gameplay should be expressed through controlled primitives such as `REPEAT_N` only if a concrete mechanic requires it, with strict deterministic execution limits.

---

## 13. No arbitrary scripting from content data

Published content MUST NOT contain executable C#, JavaScript, Lua, SQL, shell commands, reflection type names, or arbitrary scripts supplied from the dashboard.

Do NOT use:

```text
eval(...)
new Function(...)
C# dynamic compilation
arbitrary reflection-based method invocation from content strings
```

The content system is a constrained declarative DSL represented as validated structured data.

If designers require a new mechanic, programmers add a new reviewed primitive to the authoritative engine and expose that primitive through schema/registry metadata.

---

## 14. Validation requirements

Content MUST be validated before publication and again when loaded into an authoritative runtime environment.

Validation SHOULD detect at minimum:

- duplicate stable IDs;
- unknown references;
- invalid enum values;
- out-of-range costs/stats;
- malformed formulas;
- division-by-zero paths where statically detectable;
- impossible targets;
- incompatible selector/effect combinations;
- invalid trigger context;
- illegal duration configuration;
- missing required parameters;
- invalid condition trees;
- excessive nesting;
- dependency/reference cycles where cycles are forbidden;
- nondeterministic operations outside approved RNG primitives;
- deprecated primitive use;
- unpublished dependencies;
- missing content/schema version.

Invalid published gameplay data MUST fail visibly. It MUST NOT silently fall back to another mechanic.

---

## 15. Content dependency model

Definitions MAY reference other definitions by stable ID.

Examples:

```text
Card -> Ability
Ability -> Effect configuration
SummonEffect -> CardDefinition
TransformEffect -> CardDefinition
Card -> Keyword
Condition -> Tag
```

References MUST be validated before publish.

The content compiler/publisher SHOULD be able to calculate dependency graphs and reject missing or invalid dependencies.

---

## 16. Reusable abilities versus inline abilities

The system SHOULD support both:

### Reusable ability definition

Use when the same behavior is shared by multiple cards/entities.

```text
ability.guardian_entry
```

### Inline ability composition

Use when the behavior is unique content assembled entirely from standard primitives.

Do not create a globally named reusable ability merely for every one-off card. Conversely, do not copy/paste the same multi-step ability across many card records.

The admin dashboard SHOULD make reusable templates easy to reference and clone deliberately.

---

## 17. Content versioning and publication

A running match MUST use one resolved content version/snapshot.

Publishing balance changes MUST NOT mutate the definitions of an already-running authoritative match.

Recommended publication concepts:

```text
schemaVersion
contentVersion
entityRevision
publishedAt
publishedBy
contentHash
```

Future multiplayer/replay data SHOULD record the content version/hash required to reproduce the match.

The admin dashboard SHOULD support draft editing without affecting production/published content.

---

## 18. Auditability

Every administrative content change SHOULD eventually record:

- actor/editor;
- timestamp;
- old revision;
- new revision;
- changed fields;
- reason/comment where required;
- approval/publish action.

Balance changes must be diffable.

Do not overwrite production values without retaining revision history.

---

## 19. Runtime content packaging

The simulation SHOULD load an immutable validated content package at match/bootstrap time.

The package MAY contain normalized JSON or a later compiled binary format, but its semantics must correspond to the same schema.

Runtime simulation MUST NOT depend on the admin dashboard being online.

A match SHOULD NOT query mutable CMS/database rows during individual effect resolution.

---

## 20. Admin API boundary

The future admin backend MAY create/update drafts, validate definitions, request simulation previews, and publish content packages.

It MUST NOT permit a client browser to directly modify authoritative production content storage without server-side validation, authorization, and revision checks.

Publishing SHOULD be an explicit privileged action separate from editing a draft.

---

## 21. What requires programming versus what should not

### SHOULD NOT require programming

- card name/localization;
- card cost;
- base attack/health;
- faction/class/tag assignment;
- artwork reference;
- using an existing trigger;
- using an existing target selector;
- changing numeric effect values;
- composing existing effects;
- changing conditions built from existing predicates;
- changing formula parameters/operators already supported;
- changing duration/limits already supported;
- ordinary balance patches.

### DOES require programming

- introducing a new timing window;
- introducing a new fundamental state-changing operation;
- introducing a new selector semantic that cannot be composed from existing selectors;
- changing global combat mathematics;
- adding a new modifier layer/stacking semantic;
- adding a new deterministic RNG operation;
- changing authoritative resolution ordering;
- changing schema semantics;
- introducing a new game-state domain concept.

When programming creates a new authorable primitive, the same change MUST add:

1. authoritative runtime implementation;
2. stable primitive ID;
3. parameter/schema definition;
4. validation rules;
5. automated tests;
6. dashboard/editor metadata or sufficient schema metadata for future UI generation;
7. documentation if timing or semantics are non-obvious.

---

## 22. Suggested implementation order

Before producing large amounts of card content, build these foundations in roughly this order:

1. Stable ID/value-object system.
2. Definition schema and version fields.
3. Stat registry and base-stat model.
4. Tags, classes, factions, keywords as controlled registries.
5. Command/state model.
6. Domain event vocabulary.
7. Target selector primitives.
8. Condition tree system.
9. Safe value/formula expression system.
10. Core effect primitives.
11. Modifier/stat calculation pipeline.
12. Trigger/timing system.
13. Explicit effect/trigger resolution queue.
14. Definition validator.
15. JSON content loader/exporter.
16. Golden simulation tests.
17. Content package/version/hash pipeline.
18. Admin-facing schema metadata/API.
19. Admin dashboard editors.
20. Publish/revision/audit workflow.

Do not build the full admin dashboard before the gameplay schema and primitive vocabulary have survived real card prototypes. The dashboard should expose a proven schema, not define one accidentally through UI forms.

---

## 23. Prototype requirement before dashboard construction

Before investing heavily in the admin dashboard, the team SHOULD express at least 30–50 deliberately varied prototype cards/abilities using only structured definitions.

The prototype set should intentionally stress:

- multiple triggers;
- nested conditions;
- player-choice targeting;
- random targeting;
- adjacent-lane targeting;
- stat scaling;
- temporary/permanent modifiers;
- death triggers;
- summoning;
- transformation;
- resource modification;
- multi-effect sequences;
- reusable ability templates.

If ordinary desired cards repeatedly require custom source code, the primitive vocabulary is not mature enough for dashboard implementation.

---

## 24. AI coding requirements

AI coding agents modifying gameplay/content infrastructure MUST preserve these rules.

Before implementing a new card-specific mechanic, the agent MUST ask internally:

> Can this behavior be expressed by Trigger + Conditions + Target Selector + Effects + Value Expression + Duration/Limits using existing primitives?

If yes, implement it as data/composition rather than card-specific branching.

If no, the agent SHOULD add the smallest general-purpose primitive that accurately represents the missing mechanic and provide tests plus schema metadata.

AI agents MUST NOT create growing `if cardId == ...` or `switch(cardId)` gameplay branches as a shortcut.

AI agents MUST NOT solve dashboard authoring requirements by permitting arbitrary scripting.

---

## 25. Governing principle

The long-term content workflow should converge on this separation:

```text
PROGRAMMERS
create and test the vocabulary of legal game mechanics

DESIGNERS / CREATIVE / BALANCE TEAM
compose that vocabulary into cards, heroes, abilities, values, conditions, and content revisions

ADMIN DASHBOARD
provides a safe visual editor for that vocabulary

SIMULATION ENGINE
is the only authority that interprets and executes that vocabulary
```

If a design decision makes ordinary content easier to author but creates a second interpretation of gameplay rules outside the authoritative simulation, reject that design.
