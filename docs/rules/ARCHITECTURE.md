# Repository and Runtime Architecture

## Intended repository shape

```text
HyuHeoresSimulator/
├── AGENTS.md
├── README.md
├── docs/
│   ├── rules/
│   └── adr/
├── Assets/
│   └── _Game/
│       ├── Core/
│       ├── Rules/
│       ├── Cards/
│       ├── Effects/
│       ├── Commands/
│       ├── Events/
│       ├── AI/
│       ├── Presentation/
│       ├── UI/
│       ├── Audio/
│       └── Tests/
├── Packages/
└── ProjectSettings/
```

This structure is a target, not a requirement to create empty folders prematurely.

## Layer model

### Domain/Core
Owns value objects, IDs, immutable/read-only definitions, runtime state models, match configuration, domain contracts, and pure utilities required by the simulation.

It MUST NOT depend on Unity presentation code.

### Rules/Simulation
Owns command validation, command execution, phase progression, combat, trigger resolution, effect execution, target resolution, win/loss evaluation, and seeded RNG access.

It depends on Domain/Core. It MUST NOT depend on presentation.

### Data/Definitions
Owns authoring/transport representations for card definitions and other content. Unity `ScriptableObject` may be used as an authoring adapter, but simulation code SHOULD consume engine-neutral definition models.

### Application/Adapters
Owns orchestration between the simulation and external systems such as persistence, replay loading, deck storage, networking, telemetry, and AI controllers.

### Presentation
Owns Unity scenes, prefabs, GameObjects, view models/presenters, input translation, animation, particles, audio, and visual state synchronization.

Presentation observes authoritative simulation state/events and submits commands. It MUST NOT write directly to authoritative state.

## Dependency direction

Allowed high-level dependency direction:

```text
Presentation --> Application/Adapters --> Simulation/Rules --> Domain/Core
Data authoring ------------------------------^       |
Replay / AI / Server adapters ---------------|-------+
```

Domain/Core MUST NOT depend upward.
Simulation MUST NOT depend on Presentation.

## Unity boundary

Unity-specific types such as `GameObject`, `Transform`, `Sprite`, `Animation`, `MonoBehaviour`, `ScriptableObject`, `Time`, and scene references MUST stay outside the pure simulation boundary unless used only in an authoring adapter that converts them into engine-neutral data before gameplay starts.

Do not store a `GameObject` or prefab reference in `GameState`.

## State ownership

There is exactly one authoritative match state for a running match.

Presentation may cache display state but must be able to rebuild from authoritative state plus emitted events/snapshots.

Mutation should be narrow and controlled. Avoid exposing globally mutable collections. Prefer read-only interfaces/views outside the owning subsystem.

## Event flow

Recommended flow:

```text
Input -> Command -> Validation -> Resolution -> State Mutation -> Domain Events -> Trigger Queue -> Further Resolution -> Final State -> Presentation
```

A domain event is evidence that something occurred in the simulation. It is not a Unity event callback and must not rely on listeners to make authoritative gameplay happen.

## Serialization

- Serialize stable IDs and primitive/domain values, not scene-object references.
- Serialized formats MUST be versionable.
- Replay/persistence payloads SHOULD include a schema/version field once persisted outside tests.
- Runtime-only caches SHOULD be reconstructible and excluded from persisted authoritative state where practical.

## Future multiplayer boundary

Design as if a server may later execute the same pure simulation package.

The client may predict or preview legal actions, but the future server is expected to validate commands and own authoritative outcomes. Therefore no core rule may require a Unity scene, client animation, local wall clock, local device state, or hidden client-only random source.

## AI boundary

Bots SHOULD use the same legal-command and simulation APIs as human players. Do not create an AI-only path that bypasses legality checks or mutates state directly.

## ADR policy

Create an Architecture Decision Record under `docs/adr/` for decisions that materially alter module boundaries, persistence formats, deterministic resolution rules, networking authority, or foundational technology choices. The ADR should capture context, decision, alternatives, and consequences.
