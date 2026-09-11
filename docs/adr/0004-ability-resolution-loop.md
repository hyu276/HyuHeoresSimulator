# ADR 0004: Triggered ability resolution loop

## Status

Accepted for the current prototype baseline.

## Context

The simulation now has validated abilities, deterministic target/condition/formula evaluation, effect planning, stat modifiers, canonical damage, immutable state transitions, domain events, and an explicit trigger queue. The missing bridge is a single authoritative loop that turns queued trigger work into ability execution without recursive callbacks or stale-state effect planning.

## Decision

Triggered abilities resolve through this loop:

```text
Dequeue TriggerQueueItem
    -> load AbilityDefinition by stable ID
    -> verify binding trigger matches definition
    -> build runtime context from current MatchStateSnapshot + originating DomainEvent
    -> evaluate ability condition
    -> evaluate usage limit
    -> execute effect sequence transactionally
         effect N plans from state immediately before effect N
         operations of effect N apply
         state/events advance
         then effect N+1 evaluates
    -> record successful ability usage
    -> discover triggers from committed events
    -> enqueue them
    -> continue until queue empty or a pending decision is reached
```

### Effect sequencing

The runtime MUST NOT pre-plan a whole ability against one old snapshot. Later effects may depend on stat/resource/health changes caused by earlier effects.

One individual multi-target effect plans all of its target operations from the same pre-effect snapshot. Those resolved operations are then applied in deterministic target order. The next effect sees the resulting snapshot.

`CONDITIONAL` is a structural effect. Its condition is evaluated when that effect is reached, and only the selected branch is executed recursively.

### Player choice transaction

If execution reaches a `PLAYER_CHOICE` selector, the effect sequence may have speculatively evaluated earlier effects against immutable local snapshots, but none of that ability's state/events are committed externally. The loop returns the original pre-ability state plus a `PendingAbilityChoice` containing the queued trigger, ability, effect path, and candidate resolution.

Usage is not consumed and triggers from speculative events are not enqueued. A future choice-command continuation can replay the deterministic prefix and apply the selected legal target as one authoritative transaction.

### Usage limits

Usage records are authoritative match state keyed by `(sourceId, abilityId)` and track total resolutions, current-turn count, and last resolved turn.

Supported now:

- `ONCE_PER_TURN`;
- `ONCE_PER_MATCH`;
- `MAX_N_TIMES_PER_TURN`;
- `COOLDOWN_TURNS`.

For cooldown `N`, N complete turns must occur between resolutions. A resolution on turn 3 with cooldown 1 becomes eligible on turn 5.

`MAX_N_TIMES_WHILE_IN_ZONE` is deliberately rejected until the match state owns a zone-residency epoch. Comparing only the current zone would fail if a source leaves and later re-enters the same zone, so the engine will not approximate this rule.

### Event context

The originating domain event is carried by the queue item. Its target becomes the default active target for runtime formula/condition context; if the event has no target, the ability source is used. The engine never reconstructs this context from UI state.

### Loop safeguard

The queue is drained iteratively, not recursively. A configurable trigger-step budget defaults to 256 dequeued trigger items. Exceeding the budget throws `ResolutionBudgetExceededException`, making an infinite or pathological chain explicit rather than hanging the match.

Random target resolution is not enabled in this authoritative loop until deterministic RNG state is owned by `MatchStateSnapshot`; no local or hidden RNG is introduced as a shortcut.

## Consequences

- Triggered abilities can now cause real deterministic state transitions and further reactions.
- Conditions, formulas, modifiers, damage, events, and triggers form one coherent execution path.
- Player-choice abilities have an explicit pending boundary without partial authoritative commits.
- Usage limits are replayable state rather than UI counters.
- Ordered deck/hand/card-zone state, choice continuation, zone-residency epochs, duration expiry, and snapshot-owned RNG remain explicit next steps.
