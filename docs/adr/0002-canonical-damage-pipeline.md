# ADR 0002: Canonical damage pipeline baseline

## Status

Accepted for the current prototype baseline.

## Context

Gameplay Schema v1 can author `DAMAGE(target, amount, damageType)`, and the runtime effect executor now resolves that declaration into a deterministic `DamageRequest`. The project needs one combat-math path before health mutation, events, death checks, and trigger queues are introduced.

The authoring rules explicitly require the defense formula to be documented before implementation. This ADR fixes the initial formula while keeping future extensions explicit and data-driven.

## Decision

Every damage request resolves through this order:

```text
Requested Amount
    -> non-negative Base Damage
    -> Source Damage Adjustments
    -> Damage-Type Adjustments
    -> Target Vulnerability / Resistance Adjustments
    -> Defense Mitigation
    -> Explicit Prevention / Shield Capacity
    -> non-negative Final Damage
    -> Health Lost / Overkill Calculation
    -> future Damage Event
    -> future State-Based Death Check
```

Within each adjustment stage, ordering is deterministic by `priority`, then stable modifier instance ID.

### Physical

`PHYSICAL` damage uses flat reduction from the target's effective `stat.defense` after source/type/target damage adjustments:

```text
postDefense = max(0, incoming - effectiveDefense)
```

Because `effectiveDefense` is obtained through `StatModifierPipeline`, buffs and debuffs use the same stat semantics as formulas and conditions.

### Arcane

`ARCANE` currently has no resistance stat. It therefore bypasses `stat.defense` and proceeds to explicit prevention. The engine MUST NOT invent an undocumented magic-resistance stat merely because another game has one.

A future arcane resistance mechanic requires a deliberate schema/rules addition and migration rather than private card math.

### True

`TRUE` bypasses `stat.defense`, but it does **not** automatically bypass explicit prevention or shield effects. A future `IGNORE_PREVENTION`, `UNPREVENTABLE`, or similar behavior must be represented by an explicit reviewed mechanic rather than inferred from the damage type.

### Prevention

Prevention objects are deterministic capacities associated with a target and optionally a damage type. They resolve by priority then stable ID and report how much capacity was used. This calculation is read-only in the current layer; the future authoritative reducer will consume/decrement persistent shield state.

### Health and overkill

The pipeline reports:

- final damage after all mitigation/prevention;
- actual health lost, capped at current health;
- resulting health, clamped to zero;
- overkill, defined as final damage minus actual health lost;
- whether the target would enter a death-eligible state.

The pipeline does not visually remove units and does not itself dispatch death triggers.

## Consequences

- Ability damage and future combat attacks can share one resolver.
- No card may implement private armor or shield math.
- Damage calculation remains deterministic and testable without Unity.
- Health mutation, domain events, state-based death processing, and trigger discovery remain the responsibility of the next simulation layer.
- Changing the defense formula is a balance/architecture change requiring an explicit rules/ADR update and regression tests.
