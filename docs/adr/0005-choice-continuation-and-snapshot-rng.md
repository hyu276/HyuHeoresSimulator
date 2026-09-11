# ADR 0005: Choice continuation and snapshot-owned deterministic RNG

## Status

Accepted for the current prototype baseline.

## Context

ADR 0004 established transactional pending player choices: when an ability reaches a `PLAYER_CHOICE` selector, speculative prefix state and events are discarded and the authoritative snapshot remains unchanged. The remaining requirements are a deterministic way to continue that choice and an authoritative random source for `RANDOM_N` selectors.

A continuation cannot safely resume from partially mutated in-memory objects. It must be replayable from authoritative state, and any random decisions made before the pending choice must reproduce exactly during replay.

## Decision

### Snapshot-owned RNG

`MatchStateSnapshot` now owns one `UInt64` random state. Runtime random selection uses `DeterministicRandomStream`, a fixed integer-only SplitMix64-style stream. No framework RNG, wall clock, process seed, Unity RNG, or hidden singleton participates in authoritative gameplay.

An effect sequence creates its random stream from `MatchStateSnapshot.RandomState`. The stream is shared across every selector/formula evaluation in that ability execution. The updated random state is written back to the resulting snapshot only when the effect sequence completes successfully.

If execution pauses for `PLAYER_CHOICE`, random consumption is speculative and is not committed. Replaying from the unchanged snapshot reproduces the same random prefix before applying the player's choice.

Changing the random algorithm or the order in which gameplay primitives consume random values is replay-breaking and requires an explicit compatibility/versioning decision.

### Choice continuation

A pending choice records:

- the originating trigger queue item and ability definition;
- the pending leaf effect and structural effect path;
- previously resolved choices for the same ability, if any;
- processed trigger count so the trigger budget cannot reset across a pause;
- a continuation checkpoint consisting of turn, phase, next event sequence, and random state.

The continuation command supplies only selected runtime target IDs. The engine then:

```text
Validate snapshot checkpoint
    -> rebuild the ability from the original authoritative snapshot
    -> replay prior resolved choices at their exact effect paths
    -> replay deterministic random consumption
    -> reach the pending PLAYER_CHOICE effect
    -> recompute legal candidates
    -> require exact selection count + unique IDs + candidate membership
    -> inject the validated selection
    -> continue remaining effects
    -> if another choice is reached, return a new pending boundary
    -> otherwise commit state + RNG + usage + events
    -> discover new triggers
    -> continue draining the existing trigger queue
```

No partial prefix state is persisted between choice requests.

### Multiple choices in one ability

Previously resolved choices are retained as `(effectPath, parameterName, selectedTargetIds)` selections. If a later `PLAYER_CHOICE` is reached, the next continuation replays all earlier choices before satisfying the new one. A supplied path that is no longer reached because deterministic control flow changed is rejected rather than silently ignored.

### Stale continuation protection

Continuation requires the authoritative snapshot checkpoint to match the pending request. A turn/phase transition, emitted-event advancement, or RNG advancement invalidates the pending continuation.

This checkpoint is intentionally narrow for the current immutable engine. Future serialized multiplayer commands should replace it with a canonical snapshot/content hash once the content-package hashing layer exists.

## Consequences

- `RANDOM_N` can now execute inside authoritative ability resolution and remain replayable.
- Pending choices can be completed without committing speculative state.
- Random effects before a pending choice replay identically on continuation.
- Invalid, duplicate, wrong-count, stale, or no-longer-reachable selections fail explicitly.
- Trigger-chain budgets survive choice pauses rather than resetting.
- Future work can build duration expiry, zone-residency epochs, ordered deck/hand state, and canonical snapshot hashes on top of this continuation boundary.
