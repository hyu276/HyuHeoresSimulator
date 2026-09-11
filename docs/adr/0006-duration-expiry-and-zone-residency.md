# ADR 0006: Duration expiry and zone-residency epochs

## Status

Accepted for the current prototype baseline.

## Context

The simulation already supports persistent stat modifiers and declares controlled duration primitives, but runtime modifiers previously stored only a duration ID. That was insufficient to determine deterministic expiry for turn-bound or zone-bound effects. `MAX_N_TIMES_WHILE_IN_ZONE` was also intentionally disabled because the engine had no authoritative way to distinguish a continuous stay in a zone from leaving and later re-entering the same zone.

## Decision

### Zone residency belongs to the runtime entity

Every `RuntimeTarget` owns a positive `ZoneResidencyEpoch`, initially `1`. The epoch increments only when the entity changes zones. Same-zone lane movement does not represent a new residency and must not increment it.

This keeps residency identity attached to the card/unit instance and prevents a second snapshot-side table from drifting out of sync with `RuntimeTarget.Zone`.

Examples:

```text
Board epoch 1
  -> Graveyard epoch 2
  -> Board epoch 3
```

### Runtime modifier duration state

Persistent stat modifiers materialize a `StatModifierDurationState` at the moment the modifier is created. The state records only deterministic gameplay coordinates needed for expiry:

- duration primitive ID;
- creation turn;
- calculated expiry turn where applicable;
- configured target zone where applicable;
- source residency epoch for `WHILE_SOURCE_EXISTS`;
- target residency epoch for `WHILE_IN_ZONE`.

No wall-clock timestamp is stored.

### Duration semantics

The current authoritative meanings are:

- `PERMANENT`: never removed by lifecycle expiry.
- `UNTIL_END_OF_TURN`: removed by the current turn's end-of-turn completion checkpoint. `StartNextTurn` also removes any leftover instance defensively.
- `UNTIL_START_OF_NEXT_TURN`: removed when the next turn begins.
- `FOR_N_TURNS`: if created on turn `T`, expires when start-of-turn `T + N` is entered. `N=1` therefore expires at the next turn start.
- `WHILE_SOURCE_EXISTS`: captures the source's current zone-residency epoch and expires when that source leaves the residency. Death counts as leaving because authoritative death processing moves the entity from Board to Graveyard and increments the epoch.
- `WHILE_IN_ZONE`: requires a configured zone, captures the target's current residency epoch, and expires when the target leaves that zone/residency. Re-entering the same zone creates a new epoch and never resurrects the old modifier.

### Lifecycle transition boundary

`LifecycleTransitionEngine` is the single kernel boundary for turn checkpoints and zone changes relevant to duration state:

```text
CompleteEndOfTurn
    -> expire UNTIL_END_OF_TURN

StartNextTurn
    -> advance turn + phase.start_turn
    -> expire leftover UNTIL_END_OF_TURN
    -> expire UNTIL_START_OF_NEXT_TURN
    -> expire due FOR_N_TURNS
    -> reconcile continuous durations

ChangeZone
    -> increment RuntimeTarget.ZoneResidencyEpoch
    -> emit zone event
    -> reconcile WHILE_SOURCE_EXISTS / WHILE_IN_ZONE
```

Ordinary `StateTransitionEngine` mutation also reconciles continuous durations after state-based deaths so source death immediately removes dependent modifiers.

### Residency-scoped usage limits

`AbilityUsageRecord` persists the source residency epoch and `ResolutionsThisResidency`. `MAX_N_TIMES_WHILE_IN_ZONE` compares the record's epoch with the source's current epoch:

- same epoch: continue counting against the configured maximum;
- new epoch: effective residency count is zero;
- first successful resolution in the new epoch writes count `1` for that epoch.

Lifetime total and per-turn counters remain independent.

### Authoring contract

`MODIFY_STAT` keeps the stable `durationId` field and adds dependent parameters:

- `durationTurns` for `duration.for_n_turns`;
- `durationZone` for `duration.while_in_zone`.

The publication validator rejects missing dependent parameters and rejects those parameters when paired with another duration primitive. The effect executor converts the authoring fields into a structured `DurationSpec` before producing a resolved operation.

### Domain events

Lifecycle mutations emit ordered events for:

- `event.modifier_expired`;
- `event.zone_changed`;
- `event.timing_advanced`.

Consumers must use these events rather than infer lifecycle changes from presentation timing.

## Consequences

- All declared stat-modifier duration primitives now have authoritative deterministic expiry semantics.
- `MAX_N_TIMES_WHILE_IN_ZONE` is executable rather than intentionally unsupported.
- Death and zone movement can invalidate continuous modifiers without special card-specific code.
- Admin-authored `FOR_N_TURNS` and `WHILE_IN_ZONE` parameters fail publication early when malformed.
- Future ordered Deck/Hand/Graveyard operations should call `LifecycleTransitionEngine.ChangeZone` (or a reducer built on the same epoch contract) so residency semantics remain centralized.
