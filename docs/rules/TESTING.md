# Testing Rules

## Principle

Gameplay behavior is not complete until it is testable and tested independently of Unity scenes.

## Required test layers

### Pure simulation unit tests

Use these for:

- command validation;
- effect resolution;
- target selection;
- resource rules;
- zone movement;
- combat math;
- modifier ordering;
- death/state-based processing;
- win/loss evaluation;
- deterministic RNG behavior.

These tests MUST run without loading Unity scenes.

### Scenario/integration tests

Use deterministic multi-step match scenarios to verify interactions among commands, events, effects, triggers, phases, and state transitions.

Each scenario SHOULD make the setup, command sequence, and expected authoritative outcome readable from the test.

### Presentation tests

Use Unity-specific tests only for presentation/integration concerns such as scene wiring, presenter/view synchronization, animation sequencing, and asset binding. Do not use presentation tests as the only proof that a gameplay rule works.

## Every gameplay change

A gameplay feature or rule change MUST include tests for:

1. normal success path;
2. relevant illegal/invalid command paths;
3. boundary conditions;
4. important interactions with triggers/modifiers where applicable;
5. deterministic behavior if randomness or simultaneous resolution is involved.

A bug fix MUST add a regression test that fails before the fix and passes after it whenever reasonably possible.

## Test naming

Test names SHOULD communicate condition and expected outcome, for example:

`PlayCard_WhenResourceIsInsufficient_IsRejected`

`Damage_WhenHealthReachesZero_QueuesDeathResolution`

`RandomTarget_WithSameSeed_SelectsSameInstance`

## Test fixtures

Build reusable test factories/builders for game states, cards, definitions, and commands. Keep fixture defaults minimal and explicit enough that irrelevant defaults do not accidentally determine test outcomes.

Do not share mutable authoritative state between tests.

## Determinism tests

The suite SHOULD include golden deterministic scenarios that run the same command stream multiple times and compare authoritative state/event results.

When replay/state hashing is implemented, add tests for canonical state equivalence and desynchronization detection.

## Content validation tests

Content pipelines SHOULD automatically detect at least:

- duplicate stable IDs;
- invalid references;
- malformed definitions;
- impossible selector/effect configuration;
- missing required fields.

## No test weakening

Do not delete, skip, broaden tolerances, or change an assertion merely because a new implementation fails. First determine whether the implementation or the documented rule changed.

If behavior intentionally changes, update the test and relevant rule/design documentation together.

## Completion gate

Before a coding task is considered complete:

- relevant tests pass;
- new rules have coverage;
- known failing tests are reported explicitly;
- the agent/contributor states what test scope was run.
