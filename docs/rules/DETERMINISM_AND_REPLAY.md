# Determinism and Replay

## Determinism goal

Given the same game version/ruleset, initial match configuration, initial decks/content definitions, ordered command sequence, and RNG seed/state, the simulation MUST produce the same authoritative outcomes and event sequence.

## RNG policy

- Gameplay randomness MUST use one simulation-owned RNG abstraction.
- The RNG MUST be seeded explicitly for a match.
- RNG state/consumption order MUST be deterministic.
- Random outcomes that affect gameplay MUST be reproducible in tests and replay.
- Presentation-only randomness such as cosmetic particle variation may use a separate source, but it MUST NOT affect simulation state.

Gameplay code MUST NOT directly use `UnityEngine.Random`, time-based seeds, device entropy, frame count, GUID generation as randomness, or ad-hoc `System.Random` instances.

## Deterministic collection ordering

Never rely on undefined iteration order of hash-based collections when order can affect gameplay. If multiple entities can resolve at once, explicitly sort or otherwise define their order using stable fields such as resolution priority, lane index, controller priority, creation sequence, or stable runtime ID.

Tie-breaking rules MUST be documented and tested.

## Time independence

Simulation outcomes MUST NOT depend on:

- `Time.deltaTime`;
- wall-clock time;
- animation duration;
- frame rate;
- coroutine scheduling;
- asynchronous callback arrival order.

Timed presentation may follow simulation events, but does not control authoritative progression.

## Replay model

A minimal replay SHOULD be reconstructible from:

- ruleset/game version;
- content version/hash when applicable;
- match configuration;
- player/deck setup;
- initial RNG seed/state;
- ordered accepted commands, including explicit player choices.

Do not make replays depend on recording every visual action.

## Command logging

Accepted commands SHOULD receive a deterministic sequence number. Rejected commands MAY be logged for diagnostics but are not part of authoritative replay progression.

Commands stored for replay MUST use stable IDs and serializable values rather than Unity references.

## State hashing

The engine SHOULD support a canonical authoritative-state hash/checksum for debugging replay and future networking desynchronization. The hash must exclude presentation caches and nondeterministic metadata.

## Snapshot policy

Snapshots MAY be added for fast seeking or recovery, but snapshots do not replace the command/seed determinism requirement. Serialized snapshots MUST be versioned.

## Testing determinism

Determinism tests SHOULD include:

- same seed + commands => same final state;
- same seed + commands => same meaningful event order;
- different seeds vary only mechanics that are intended to be random;
- randomized target selection remains stable under identical state/seed;
- simultaneous trigger resolution has stable ordering;
- serialization round-trip preserves authoritative state semantics.

A determinism regression is a gameplay correctness bug, not merely a replay feature bug.
