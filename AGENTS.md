# HyuHeoresSimulator — AI Engineering Contract

This file is the mandatory entry point for every AI coding agent, pair-programming assistant, or automated code generator that modifies this repository.

## 1. Mandatory preflight

Before creating, editing, deleting, moving, or refactoring source code, the agent MUST read and comply with all documents under `docs/rules/`, starting with `docs/rules/README.md` and `docs/rules/PROJECT_CONSTITUTION.md`.

The agent MUST NOT treat these documents as optional guidance. They are repository-level engineering constraints. If a requested implementation conflicts with them, the agent must identify the conflict and propose the smallest compliant alternative. A rule may be intentionally changed only when the user explicitly asks to change that rule or approves an architecture decision that requires it.

## 2. Authority order inside this repository

For repository-specific decisions, use this order:

1. The user's explicit task and approved design decisions.
2. `AGENTS.md`.
3. `docs/rules/PROJECT_CONSTITUTION.md`.
4. The specialized documents under `docs/rules/`.
5. Existing code and implementation conventions.

Existing code does not justify violating a higher-level rule. If legacy code conflicts with the rules, do not copy the violation into new code.

## 3. Non-negotiable architecture constraints

- The game simulation is a deterministic rules engine. Unity is a presentation/input client around that engine.
- Core simulation code MUST NOT depend on `UnityEngine`, `MonoBehaviour`, scenes, prefabs, animations, frame timing, or UI objects.
- UI and animation MUST NOT mutate authoritative `GameState` directly.
- Player intent enters the simulation as Commands.
- Commands are validated before execution.
- State changes occur only through the rules/resolution layer.
- Meaningful state changes produce domain Events that presentation, replay, logging, and trigger systems can consume.
- Card behavior is primarily data plus reusable effects/conditions/targets. Do not create one bespoke gameplay class per card unless the mechanic is genuinely irreducible and the exception is documented.
- Trigger chains MUST resolve through an explicit ordered queue/stack mechanism. Never implement hidden recursive trigger side effects through UI callbacks.
- All gameplay randomness MUST use the simulation's seeded RNG source. Never use ad-hoc random APIs inside gameplay logic.
- A match must be reproducible from initial state/configuration, player commands, and RNG seed.
- `CardDefinition` and runtime `CardInstance` are separate concepts and MUST remain separate.
- Stable IDs, not display names or asset names, are used for gameplay references and serialization.

## 4. Required workflow for every coding task

Before implementation:

1. Identify which module owns the requested behavior.
2. Identify the affected rules documents.
3. State any assumption that changes gameplay semantics.
4. Check whether the change affects determinism, serialization, replay, trigger ordering, targeting, or multiplayer authority.
5. Prefer extending an existing reusable primitive over adding card-specific branching.

During implementation:

1. Keep changes scoped to the requested behavior.
2. Preserve dependency direction defined in `docs/rules/ARCHITECTURE.md`.
3. Add or update tests for every gameplay rule change and every fixed regression.
4. Use explicit domain names; avoid generic managers and god objects.
5. Keep cyclomatic complexity below 8 per method. Split logic when necessary.
6. Do not introduce magic gameplay numbers; use named configuration/data fields.
7. Do not add silent fallback behavior that hides invalid states.
8. Do not add secrets, credentials, tokens, or environment-specific values to tracked files.

Before declaring completion:

1. Confirm the project compiles.
2. Run relevant automated tests.
3. Confirm no direct UI-to-state mutation was introduced.
4. Confirm no unseeded gameplay randomness was introduced.
5. Confirm trigger/timing ordering is explicit and tested where relevant.
6. Confirm new gameplay data has stable IDs and validation.
7. Summarize files changed, behavior changed, tests run, and any remaining risk.

## 5. Forbidden shortcuts

Unless explicitly approved as a temporary prototype with a documented removal plan, do NOT:

- put authoritative gameplay rules in `MonoBehaviour` scripts;
- make animation completion callbacks determine game truth;
- call `Destroy()` or manipulate scene objects as the source of a unit's death state;
- use `UnityEngine.Random`, `System.Random`, current time, frame count, or nondeterministic collection ordering directly in simulation decisions;
- hard-code card names in central `switch`/`if` chains;
- duplicate an existing effect because creating a reusable effect is slightly more work;
- serialize object references that should be represented by stable IDs;
- let presentation code decide whether a command is legal;
- catch-and-ignore invalid gameplay states;
- weaken tests merely to make a failing change pass.

## 6. Documentation synchronization

A code change that changes a documented invariant MUST update the corresponding rule document in the same change. Architecture changes should also be recorded as an ADR under `docs/adr/` when that directory is introduced.

## 7. Scope of inspiration

The project may study lane-based digital card games such as PvZ Heroes for high-level mechanics, but it must develop its own names, characters, artwork, audio, UI identity, rules wording, content, progression, economy, and original mechanic combinations. Do not copy protected game content into the repository.

## 8. Final compliance statement

Every AI-generated code change should be evaluated against this sentence:

> GameState + Commands + Rules + Events + Effects are the game; Unity presents and controls access to that game, but does not define its truth.
