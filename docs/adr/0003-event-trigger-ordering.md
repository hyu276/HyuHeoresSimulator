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

The first reducer handlers cover damage requests, healing, resource changes, base stat SET operations, and stat modifier attachment. `DRAW` is deliberately rejected until ordered deck/hand state exists; the engine must not fake a draw event without an authoritative deck mutation.

### Event sequence

Every emitted domain event receives a positive monotonically increasing sequence from match state. A causative event is emitted before state-based events it creates. For lethal damage:

```text
DamageApplied(sequence N)
EntityDied(sequence N+1)
```

State-based death candidates are processed by lane index and then runtime StableId so simultaneous lethal entities remain deterministic.

### Trigger discovery

A `DamageApplied` event opens damage-reaction windows only when canonical resolution reports `HealthLost > 0`. A fully mitigated or prevented request remains observable as a resolution event but does not count as dealing or receiving damage.

For positive health loss, `ON_DAMAGE_DEALT` opens before `ON_DAMAGED`. `EntityDied` opens `ON_DEATH`. Because the death event has a later sequence than its causative damage event, damage reactions queue before death reactions.

### Trigger event context

Every `TriggerQueueItem` keeps the immutable originating `DomainEvent`, not merely its sequence number. The event sequence remains the primary ordering key, while the preserved payload gives the ability-resolution layer authoritative context such as the damaged target, damage resolution, or dead entity. Trigger execution MUST NOT reconstruct this context from current board position or presentation state.

### Trigger queue ordering

Queue items are ordered by:

1. domain event sequence ascending;
2. trigger window order ascending;
3. binding priority descending;
4. source StableId ascending;
5. ability StableId ascending;
6. binding StableId ascending.

The binding ID prevents the same binding from being queued twice for the same event. New events created by a future queued ability receive later event sequences and cannot retroactively reorder an earlier window.

## Consequences

- Presentation can rebuild from snapshots plus domain events without owning gameplay truth.
- Replay and a future server can reproduce reaction order and trigger context exactly.
- Trigger discovery is separate from trigger execution, preventing uncontrolled recursive callbacks.
- Ability execution, usage limits, pending player choices, and loop safeguards remain the next simulation-layer responsibilities.
- Any future change to event or trigger ordering requires explicit regression tests and an ADR/rules update.
