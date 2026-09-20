# ADR 0007: Ordered player zones and executable card-zone operations

## Status

Accepted for the current prototype baseline.

## Context

The schema and effect registry already exposed DRAW, DISCARD, SUMMON, DESTROY, MOVE, and TRANSFORM, but the authoritative simulation only modeled card location as a flat `RuntimeTarget.Zone`. That is sufficient for selectors, but not for mechanics where order is gameplay state:

- DRAW needs a deterministic top of deck.
- Hand order must be replayable and presentation-independent.
- Graveyard order must reflect authoritative event order.
- Moving between zones must update zone residency exactly once.
- SUMMON and TRANSFORM need published card definitions to materialize runtime stats and identity.
- Dynamic runtime instance IDs must be deterministic and snapshot-owned.

Without ordered zone state these effects would either remain metadata-only or depend on collection iteration/UI state.

## Decision

### Dual representation with one invariant

`RuntimeTarget` remains the authoritative entity record for identity, owner, current zone, lane, residency epoch, stats, and optional `CardDefinitionId`.

`MatchStateSnapshot.PlayerZones` adds one `PlayerZoneState` per player containing ordered runtime IDs for:

```text
Deck       index 0 = top of deck
Hand       append in authoritative arrival order
Graveyard  append in authoritative arrival/death order
```

Board remains lane-addressed and is not represented as an ordered list.

Every snapshot validates that every entity whose `RuntimeTarget.Zone` is Deck, Hand, or Graveyard appears exactly once in the matching player's ordered zone and nowhere else. A mismatch fails immediately.

### Runtime card definition identity

Card-backed `RuntimeTarget` instances may carry `CardDefinitionId`.

`CardRuntimeCatalog` materializes runtime instances from immutable `CardDefinition` data for SUMMON and applies replacement definition data for TRANSFORM. This keeps card-specific data out of the reducer and avoids card-ID branching.

### Deterministic instance identity

`MatchStateSnapshot.NextEntitySequence` owns dynamic runtime identity generation.

SUMMON creates IDs in the form:

```text
entity.runtime_00000001
entity.runtime_00000002
...
```

The counter advances only when a runtime entity is successfully materialized.

Pending choice checkpoints include this sequence so a stale continuation cannot replay against a different dynamic-entity allocation state.

### Zone operation semantics

#### DRAW

- target must be a player;
- amount must be a non-negative whole number;
- cards are removed from Deck index 0;
- each card appends to Hand;
- drawing stops when the deck is empty;
- no fatigue/empty-deck damage is introduced in schema v1;
- each successful draw emits `event.card_drawn`.

#### DISCARD

- target must currently be an owned card in Hand;
- the card is removed from Hand and appended to Graveyard;
- zone residency epoch increments;
- emits `event.card_discarded`;
- discard is not death.

#### SUMMON

- destination resolves to lane targets;
- the source's effective owner becomes the summoned card owner;
- only UNIT card definitions may occupy lane Board in schema v1;
- the owner's destination lane must be empty;
- the new instance is materialized from `CardRuntimeCatalog`;
- emits `event.entity_summoned`.

#### DESTROY

- target must be a Unit/Hero on Board;
- target moves to the owner's Graveyard and health becomes zero;
- zone residency epoch increments;
- emits canonical `event.entity_died`;
- DESTROY does not route through damage and therefore does not emit damage events.

#### MOVE

MOVE uses explicit structured parameters:

```text
target
destinationZone = BOARD | HAND | DECK | GRAVEYARD
destination?             // required only for BOARD; resolves one lane
destinationPlacement?    // TOP | BOTTOM; only meaningful for DECK
```

Cross-zone movement increments residency epoch and updates ordered zone membership.

Moving between Board lanes is a same-zone move and therefore does not increment residency epoch.

Schema v1 does not support reordering a card inside the same Deck/Hand/Graveyard.

One MOVE effect may not require PLAYER_CHOICE for both target and destination. That requires a future multi-choice-per-effect continuation contract.

#### TRANSFORM

- target must be card-backed;
- runtime ID, owner, current zone, lane, and residency epoch are preserved;
- card definition, classification, base stats, and board health are replaced from `CardRuntimeCatalog`;
- existing instance-scoped modifier records remain attached to the same runtime entity;
- emits `event.entity_transformed`.

### Trigger windows

`event.card_drawn` opens `trigger.on_card_drawn` for the drawn runtime card.

`event.entity_summoned` opens `trigger.on_summoned` for the summoned runtime entity.

Dynamic binding materialization from card definitions is a separate content/runtime-binding concern and is not added implicitly in this ADR.

## Consequences

- Deck, Hand, and Graveyard order are now replay-visible authoritative state.
- DRAW/DISCARD/SUMMON/DESTROY/MOVE/TRANSFORM can execute through the same effect -> reducer -> domain-event pipeline as damage/stat mechanics.
- Zone-residency durations and MAX_N_TIMES_WHILE_IN_ZONE remain coherent across all card movement.
- UI/Unity do not choose deck order, instance IDs, summon stats, or movement legality.
- The next architecture step can add published JSON content packages and runtime binding materialization without redesigning card-zone state.
