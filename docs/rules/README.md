# Engineering Rules Index

This directory is the canonical engineering rule set for HyuHeoresSimulator.

Every human or AI contributor should read `PROJECT_CONSTITUTION.md` first, then the documents relevant to the task. `AGENTS.md` at repository root is the mandatory AI entry point and references this directory.

## Documents

- `PROJECT_CONSTITUTION.md` — non-negotiable engineering invariants and project boundaries.
- `GAMEPLAY_FOUNDATION.md` — lane card-game model, phases, state, commands, targeting, resources, win conditions, and timing policy.
- `ARCHITECTURE.md` — repository/module structure, dependency direction, Unity boundary, data flow, serialization, and future networking boundary.
- `CARD_AND_EFFECT_SYSTEM.md` — card definitions, runtime instances, reusable effects, conditions, targets, modifiers, triggers, and resolution rules.
- `DATA_DRIVEN_GAMEPLAY_AUTHORING.md` — canonical rules for schema-driven cards/abilities/stats, controlled tags/classes, visual condition/formula builders, admin-dashboard authoring, publication/versioning, and the boundary between creative content and programming.
- `DETERMINISM_AND_REPLAY.md` — seeded RNG, deterministic ordering, command logs, replay requirements, and nondeterminism bans.
- `CODE_QUALITY.md` — C# quality standards, naming, complexity, error handling, dependencies, and maintainability constraints.
- `TESTING.md` — required test layers and acceptance criteria for gameplay changes.
- `GIT_WORKFLOW.md` — commits, branches, pull requests, generated files, Unity metadata, and change discipline.
- `SECURITY.md` — secrets, authoritative state, client trust boundaries, serialization/input validation, and future multiplayer requirements.

## Rule keywords

The words **MUST**, **MUST NOT**, **SHOULD**, **SHOULD NOT**, and **MAY** are intentional:

- **MUST / MUST NOT**: mandatory repository invariant.
- **SHOULD / SHOULD NOT**: default rule; deviation requires a concrete reason in the change description.
- **MAY**: explicitly optional.

## Change policy

Do not silently alter a rule to make implementation easier. If a feature genuinely requires a rule change, update the relevant document deliberately and explain the architectural consequence in the same change.

When two specialized rules appear to conflict, `PROJECT_CONSTITUTION.md` wins. If ambiguity remains, preserve determinism, testability, and separation between simulation and presentation until an explicit decision is made.
