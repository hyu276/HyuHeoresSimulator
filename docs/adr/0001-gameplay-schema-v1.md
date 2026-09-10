# ADR 0001: Pure C# Gameplay Schema v1

## Status

Accepted for initial implementation.

## Context

HyuHeoresSimulator needs gameplay content that can later be authored through an admin dashboard without embedding card-specific source code or allowing arbitrary scripts. The authoritative simulation must remain deterministic, engine-agnostic, replay-safe, and compatible with a future Unity client and server-authoritative runtime.

## Decision

Gameplay Schema v1 is implemented as a standalone C# library targeting .NET Standard 2.1 under `src/HyuHeroes.Gameplay`.

The schema uses:

- validated namespaced `StableId` values for persistent identity;
- immutable card and ability definition models;
- controlled registries for taxonomy and authorable primitive metadata;
- typed parameter bags rather than arbitrary dictionaries of raw objects;
- a deterministic Formula AST whose operators are registered C# handlers;
- a structured Condition Tree supporting `ALL`, `ANY`, `NOT`, and registered predicates;
- structured target selectors;
- an Effect Registry whose entries expose parameter metadata suitable for future schema-driven dashboard controls;
- publication/runtime validation with bounded formula, condition, and nested-effect depth;
- no reflection-based gameplay dispatch and no arbitrary C#, JavaScript, Lua, or expression evaluation from content.

The initial library defines schema and authoring vocabulary only. Combat resolution, trigger queues, modifier calculation, effect execution handlers, JSON DTO serialization, and content package hashing are subsequent layers built on this schema.

## Consequences

Ordinary cards and abilities can be represented as composition instead of one class per card. The future admin dashboard can derive dropdowns and builders from registry metadata instead of maintaining a second list of gameplay semantics.

Adding a genuinely new primitive still requires reviewed C# implementation, metadata, validation, and tests. This is intentional: the content layer remains constrained data rather than executable scripting.

Targeting and formula interfaces are engine-neutral, so Unity presentation code does not become a dependency of the gameplay kernel.
