# ADR 0003: Domain event and trigger ordering baseline

## Status

Accepted for the current prototype baseline.

## Context

The runtime can now resolve declarative effects and calculate canonical damage, but those layers are intentionally read-only. The project needs a narrow authoritative mutation boundary, immutable domain events, state-based death processing, and an explicit trigger queue before chained abilities can be executed safely.

Replay, AI, presentation, and a future server must observe the same ordering independent of collection iteration order or UI timing.

## Decision

The baseline authoritative flow is:

```text
Resolved Effect Operation
    -> StateTransitionEngine
    -> State Mutation
    -> Domain Event(s)
    -> State-Based Death Check
    -> additional death event(s)
    -> TriggerDiscovery
    -> DeterministicTriggerQueue
    -> future Ability Resolution
```

### State ownership

`MatchStateSnapshot` is immutable outside the transition engine. Each applied operation returns a new snapshot and an ordered event list. The previous snapshot remains valid for replay/debug comparison.

The first reducer handlers cover:

- damage requests;
- healing;
- resource changes;
- base stat SET operations;
- stat modifier attachment.

`DRAW` is deliberately rejected until ordered deck/hand state exists. The engine must not fake a draw event without an authoritative deck mutation.

### Event sequence

Every emitted domain event receives a positive monotonically increasing sequence from match state.

A causative event is emitted before state-based events it creates. For lethal damage, the order is:

```text
DamageApplied(sequence N)
EntityDied(sequence N+1)
```

State-based death candidates are processed by lane index and then runtime StableId so simultaneous lethal entities remain deterministic.

The current state-based check treats a board unit/hero with `CurrentHealth <= 0` as dead, moves it to `GRAVEYARD`, clears its lane position, and emits `EntityDied`. More advanced replacement/prevention-of-death mechanics require an explicit future rule rather than UI interception.

### Trigger discovery

A `DamageApplied` event opens two ordered windows:

1. `ON_DAMAGE_DEALT` for the damage source;
2. `ON_DAMAGED` for the damaged target.

An `EntityDied` event opens `ON_DEATH` for the dead entity.

Because the death event has a later event sequence than its causative damage event, damage reactions are queued before death reactions.

### Trigger queue ordering

Queue items are ordered by:

1. domain event sequence ascending;
2. trigger window order ascending;
3. binding priority descending;
4. source StableId ascending;
5. ability StableId ascending;
6. binding StableId ascending.

The binding ID also prevents the same binding from being queued twice for the same event.

New events created by a future queued ability receive later event sequences and therefore cannot retroactively reorder an earlier trigger window.

## Consequences

- Presentation can rebuild from snapshots plus domain events without owning gameplay truth.
- Replay and a future server can reproduce reaction order exactly.
- Trigger discovery is separate from trigger execution, preventing uncontrolled recursive callbacks.
- Ability execution, usage limits, pending player choices, and loop safeguards remain the next simulation-layer responsibilities.
- Any future change to event or trigger ordering requires explicit regression tests and an ADR/rules update.
