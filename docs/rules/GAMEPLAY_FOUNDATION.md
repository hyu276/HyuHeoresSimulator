# Gameplay Foundation

This document defines the baseline simulation model. Concrete balance values may change; structural rules should not be changed accidentally.

## Match model

A match is represented by one authoritative `GameState` containing both players, board lanes, zones, turn/phase data, match configuration, pending resolutions, and RNG state.

The initial implementation SHOULD support a small MVP ruleset, but its state model MUST avoid assumptions that make future cards impossible to express cleanly.

## Turn and phase structure

Turn progression MUST be represented explicitly by enums/value objects rather than inferred from UI state.

A recommended baseline is:

1. Start Turn
2. Resource/Draw
3. Main Action Phase
4. Reaction/Response Phase if the design uses one
5. Combat
6. End Turn

The exact final phase list may evolve, but:

- each legal command MUST declare/check the phases in which it is legal;
- phase transition MUST be performed by simulation rules;
- presentation animation MUST NOT delay or advance game truth;
- simultaneous effects MUST be reduced to an explicit deterministic order.

## Board and lanes

- Lane count MUST be part of match configuration, not spread as a magic number throughout code.
- Lanes MUST have stable indexes/IDs.
- Occupancy rules MUST be validated by the simulation.
- Special lane/environment properties SHOULD be represented as data/status rather than a separate code path per visual lane.
- Adjacent, leftmost, rightmost, random-lane, and similar selectors MUST use reusable targeting primitives.

## Players and zones

At minimum, model these zones explicitly where applicable:

- deck;
- hand;
- board;
- graveyard/discard;
- exile/removed zone only if the design requires it.

Moving a card between zones MUST occur through simulation actions/effects so that events, triggers, replay, and validation can observe the move.

## Card categories

Card categories MUST be represented as definitions/data. The exact taxonomy may evolve; likely categories include Unit, Action/Spell, Environment, and Hero Ability.

Do not encode a category only through prefab type or UI layout.

## Resources

- Resource values are authoritative simulation state.
- Resource gain, spend, caps, temporary resource, and alternate resource types MUST be expressed through rule/effect primitives.
- Costs MUST be validated at command execution time, not only by UI button availability.
- UI may predict affordability but is not authoritative.

## Commands

Player intent enters the rules engine using explicit command objects/value types, for example:

- `PlayCardCommand`
- `ChooseTargetCommand`
- `ActivateAbilityCommand`
- `EndPhaseCommand`
- `ConcedeCommand`

A command MUST include all player choices necessary for deterministic resolution, or the engine must enter an explicit pending-choice state and request a follow-up command.

Commands MUST NOT contain presentation-only references such as GameObjects.

## Validation

Command legality checks SHOULD return structured validation results/reasons usable by tests, UI, logs, and networking.

Validation includes, where relevant:

- correct active player/priority;
- correct phase;
- card exists in expected zone;
- sufficient resources;
- destination is legal;
- targets are legal;
- ability limits/cooldowns are respected;
- match is not already complete.

The same authoritative validator SHOULD be reusable by offline play, AI, and future server code.

## Combat

Combat MUST resolve from `GameState`, not scene position or animation timing.

Damage, healing, stat change, destruction/death, movement, summon, transform, and similar mechanics SHOULD be represented as explicit simulation actions/effects and resulting domain events.

If combat resolves by lane, lane resolution order MUST be explicitly defined and tested.

## Targeting

Targets MUST use stable IDs/selectors. Reusable targeting concepts SHOULD include:

- self/source;
- friendly/enemy player;
- friendly/enemy unit;
- current lane;
- adjacent lane;
- all matching entities;
- N random matching entities;
- weakest/strongest matching entity with deterministic tie-breaking.

Every tie-break rule MUST be deterministic.

## Win/loss

Win and loss conditions MUST be evaluated by the simulation after the resolution points where they can become true.

A match-complete state MUST be terminal except for explicitly allowed post-match metadata/logging. No normal gameplay command may execute after match completion.

## Pending decisions

Rules that require a user choice MUST NOT secretly pick a default target. The engine should represent a pending decision containing the legal options/context, then accept a deterministic follow-up command.

## Future-safe baseline

The engine should be able to support, without architectural replacement:

- AI opponents;
- replays;
- spectator playback;
- deterministic simulations for tests/balance tools;
- authoritative multiplayer;
- hundreds of card definitions built mostly from reusable primitives.
