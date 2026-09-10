# Project Constitution

## Core principle

HyuHeoresSimulator is a deterministic lane-based digital card game simulation. The simulation owns game truth. Unity owns presentation, input collection, audiovisual feedback, and platform integration.

> GameState + Commands + Rules + Events + Effects are the game. Unity presents the game but does not define its truth.

## Non-negotiable invariants

1. Core simulation code MUST be engine-agnostic and MUST NOT depend on `UnityEngine`.
2. Authoritative state MUST be mutated only through validated simulation commands and resolution logic.
3. Gameplay randomness MUST be seeded, centralized, reproducible, and testable.
4. Trigger ordering MUST be explicit and deterministic.
5. Card definitions MUST be separated from runtime card instances.
6. Stable identifiers MUST be used for gameplay references, persistence, serialization, replay, and networking.
7. Gameplay behavior SHOULD be composed from reusable effects, targets, conditions, modifiers, and triggers rather than bespoke card classes.
8. Presentation code MUST NOT determine command legality or combat outcomes.
9. Every gameplay rule change MUST have automated tests.
10. Invalid game states MUST fail visibly in development; they MUST NOT be silently ignored.
11. The architecture MUST remain compatible with future replay and server-authoritative multiplayer even if the first playable build is offline.
12. Repository documentation MUST remain synchronized with intentional architecture/rule changes.

## Product boundaries

The project may be inspired by the genre conventions of lane-based card battlers, including concepts seen in PvZ Heroes, but it MUST establish original implementation, terminology, characters, art, audio, UI identity, progression, economy, card content, and mechanic combinations.

## Priority when making trade-offs

When two implementation choices compete, prefer in this order:

1. correctness of game rules;
2. determinism and reproducibility;
3. testability;
4. clear ownership and dependency direction;
5. maintainability and reusable mechanics;
6. performance based on measured evidence;
7. development convenience.

Do not trade rule correctness for visual smoothness, or architecture correctness for a shorter implementation.

## Scope discipline

Features SHOULD be delivered as small vertical slices. Avoid building large generalized systems without a concrete current use case, but do not bypass established abstractions merely to ship one card faster.

A new abstraction is justified when it removes repeated gameplay semantics, clarifies ownership, improves deterministic resolution, or makes a rule testable.

## Architecture exception policy

An exception to a MUST rule requires all of the following:

- explicit user approval;
- a documented reason;
- clearly bounded scope;
- tests proving the behavior;
- a removal or migration plan if the exception is temporary.

An AI coding agent may not invent an exception on its own.
