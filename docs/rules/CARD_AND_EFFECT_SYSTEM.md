# Card and Effect System

## Definition versus instance

`CardDefinition` is immutable content data shared by every copy of a card. `CardInstance` is runtime match state for one concrete card copy.

A definition SHOULD contain stable definition ID, display/localization key, card category, base cost/stats, tags/tribes/keywords, artwork reference ID, and declarative behavior/effect references.

An instance MUST contain its own stable runtime instance ID and only the mutable state required during a match: owner/controller, current zone, current stats, counters, modifiers, statuses, chosen modes, and similar runtime state.

Never store mutable match state back into a shared definition asset.

## Behavior composition

Card behavior SHOULD be composed from reusable primitives:

- triggers;
- conditions;
- target selectors;
- effects/actions;
- modifiers;
- durations;
- amounts/formulas.

Examples of reusable effects include damage, heal, draw, discard, summon, destroy, move, transform, add/remove modifier, modify resource, copy, discover/generate, and apply status.

A new bespoke gameplay class is acceptable only when an existing composition cannot express the mechanic cleanly. Before adding one, verify whether the needed primitive should instead become reusable.

## No central card-name branching

Do not create logic such as:

```text
if card == "CardA" ...
else if card == "CardB" ...
```

or a growing central switch over card IDs. Card identity should select data/behavior definitions; the engine should resolve generic primitives.

## Trigger model

Triggers MUST be explicit domain concepts such as:

- OnPlay;
- OnSummoned;
- OnDamaged;
- OnDeath;
- OnDestroyed;
- OnMove;
- StartOfTurn;
- EndOfTurn;
- BeforeCombat;
- AfterCombat;
- Before/After an applicable effect.

Exact trigger vocabulary should grow conservatively. Prefer generic event filters over adding a new trigger keyword for every card.

## Trigger resolution

Trigger chains MUST use an explicit deterministic resolution structure. A trigger may enqueue further effects/events, but it MUST NOT hide authoritative side effects inside presentation callbacks or uncontrolled recursion.

For every trigger system, define and test:

1. which domain event opens the trigger window;
2. which entities are eligible to react;
3. ordering between multiple eligible triggers;
4. deterministic tie-breaking;
5. whether newly created triggers can join the current window or a later one;
6. when death/state-based checks occur;
7. when win/loss checks occur;
8. safeguards against accidental infinite loops.

## Effects and events

An Effect describes an intended simulation operation. A Domain Event records what actually happened.

Example:

```text
DamageEffect(target, 3)
    -> resolution applies legal damage
    -> DamageAppliedEvent(source, target, 3)
    -> possible health/death events
    -> trigger discovery
```

Do not use domain events as mutable commands and do not rely on UI event listeners to complete core resolution.

## Conditions

Conditions SHOULD be reusable predicates over simulation state/context. Examples:

- target is damaged;
- lane is empty;
- player has at least N resource;
- source has tag X;
- another allied unit exists;
- this is the first action of the turn.

Condition evaluation MUST be pure with respect to authoritative state: evaluating a condition may not mutate game state or consume RNG unless the mechanic explicitly performs a random selection as a separate effect.

## Target selectors

Target selection MUST distinguish between:

- deterministic selectors resolved entirely by the engine;
- player-choice selectors requiring an explicit pending choice/command;
- random selectors using seeded RNG.

Selectors MUST define deterministic tie-breaking where several equally ranked targets exist.

## Modifiers

Temporary and persistent modifiers SHOULD be represented explicitly rather than by permanently rewriting base definition values.

Each modifier should have, where relevant:

- source ID;
- affected entity ID;
- stat/rule affected;
- operation/value;
- duration/expiry rule;
- stacking policy;
- deterministic ordering/priority.

Computed values must have a documented ordering model if multiple modifier layers interact.

## Death and state-based processing

Do not equate visual removal with simulation death.

A recommended model is:

1. effect changes state;
2. domain event emitted;
3. state-based checks identify entities that should die/be removed;
4. death/removal operation occurs;
5. death-related events are emitted;
6. relevant triggers enter the resolution structure;
7. repeat state-based checks until stable;
8. evaluate terminal match conditions at defined checkpoints.

The exact ordering may be revised deliberately, but MUST be globally consistent and tested.

## Content validation

Card/content definitions SHOULD be validated before a match starts. Reject or surface errors for:

- duplicate stable IDs;
- unknown referenced effects/tags/definitions;
- impossible target configuration;
- invalid negative/base values where forbidden;
- malformed modifier durations;
- missing localization/art references when required by build policy.

Invalid content MUST NOT silently degrade into a different gameplay behavior.
