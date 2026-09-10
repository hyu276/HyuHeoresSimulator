# Code Quality Rules

## General standard

Code should optimize for correctness, clarity, deterministic behavior, and long-term maintainability. Cleverness is not a goal.

## Complexity

- Cyclomatic complexity SHOULD remain below 8 per method.
- A method approaching that limit should be decomposed by responsibility, not merely split mechanically.
- Avoid deep nesting. Prefer guard clauses and explicit domain helpers.
- Avoid god classes, especially classes named generically such as `GameManager`, `CardManager`, `BattleManager`, or `Utility` that accumulate unrelated responsibilities.

## Naming

Use C# conventions consistently:

- types, methods, properties, enums: `PascalCase`;
- local variables and parameters: `camelCase`;
- private fields: `_camelCase`;
- interfaces: `IName` only when an interface genuinely represents a contract boundary;
- constants: `PascalCase` unless a project formatter later standardizes otherwise.

Names MUST describe domain meaning. Prefer `ResolveTriggeredEffects()` over `ProcessStuff()`.

## Method and class responsibilities

A class SHOULD have one clear ownership reason. A method SHOULD do one conceptual job.

Simulation methods that mutate authoritative state should make that mutation obvious in naming and placement. Pure query/evaluation methods SHOULD NOT mutate state.

## Magic values

Do not embed unexplained gameplay numbers or strings in logic. Balance values belong in definitions/configuration. Structural constants belong in named constants or match configuration.

Stable gameplay identifiers MUST NOT be derived from display text.

## Nullability and invalid state

- Prefer non-nullable domain models where absence is not semantically valid.
- Represent optional domain values explicitly.
- Validate inputs at boundaries.
- Invalid authoritative state should fail fast in development/testing.
- Do not catch exceptions only to suppress them.

## Collections

Expose read-only views where callers should not mutate owned data.

Do not depend on hash collection iteration order for gameplay resolution.

Avoid unnecessary allocations in hot simulation loops, but do not sacrifice clarity without evidence from profiling.

## Immutability and mutation scope

Definitions and configuration SHOULD be immutable after match initialization.

Runtime mutation SHOULD be centralized within the subsystem that owns the state. Avoid public setters on authoritative state unless they are required by a narrow serialization boundary.

## Dependency discipline

- Domain/Core does not reference Unity.
- Simulation does not reference UI or presentation.
- Presentation may reference application/simulation APIs, not internal mutable state implementation details.
- Avoid circular dependencies between modules/assemblies.
- Introduce an abstraction at a real boundary, not for every class by default.

## Logging

Use structured, meaningful diagnostic logging where needed. Do not leave noisy debug logging in production paths.

Logs MUST NOT be required for gameplay correctness.

Never log secrets, access tokens, authentication headers, or sensitive user data.

## Comments

Comments should explain why, invariants, timing semantics, or non-obvious constraints. Do not narrate self-evident code.

Public APIs with non-obvious gameplay semantics SHOULD document preconditions, ordering, and side effects.

## Duplicated gameplay semantics

If two cards require the same mechanic, create or reuse a shared effect/condition/selector rather than copying logic.

Duplication is especially unacceptable for:

- damage/healing semantics;
- death processing;
- target legality;
- zone movement;
- resource spending;
- trigger ordering;
- modifier application;
- seeded random selection.

## Performance policy

Correctness first. Profile before optimizing.

When performance work is necessary:

1. measure the actual bottleneck;
2. preserve deterministic results;
3. add regression tests where behavior could change;
4. document any non-obvious optimization invariant.

## Refactoring policy

Do not combine a broad unrelated refactor with a gameplay feature unless necessary for correctness. Prefer small, reviewable changes.

A refactor MUST preserve externally observable gameplay behavior unless the task explicitly changes that behavior.
