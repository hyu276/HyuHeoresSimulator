# HTML Battle Simulator

This directory contains a zero-dependency browser sandbox for testing the feel and baseline rules of Hyu Heroes before equivalent systems are implemented in Unity.

## Run

Open `index.html` directly in a modern browser. No package install, bundler, local server, or external asset is required.

## Current interaction loop

1. Select an affordable card from the player hand.
2. Select an empty player lane to deploy it.
3. Deploy additional cards while energy remains.
4. Press **End Action**.
5. The rival chooses legal commands through the same simulation legality API.
6. Combat resolves from lane 1 through lane 5.
7. Surviving units remain on board, dead units move to graveyards, both players refill energy for the next turn, and each draws a card.
8. Reduce the opposing core from 20 health to zero to end the simulation.

## Architecture

`simulation.js` is the authoritative, DOM-independent sandbox simulation. It owns state, commands, validation, deterministic seeded RNG, legal command generation, energy, board state, combat, deaths, phase transitions, and command logging.

`app.js` is presentation only. It translates clicks into commands, observes simulation results, renders the current state, and plays CSS animations from emitted events. Animation timing does not determine game truth.

`styles.css` contains responsive visual presentation and motion. A `prefers-reduced-motion` fallback is included.

This structure intentionally mirrors the repository rule that simulation truth remains separate from presentation so prototype code does not establish the wrong dependency direction before Unity development begins.

## Determinism

The sandbox uses the fixed seed `2762026`. Resetting the match reproduces the same initial shuffle. Accepted player/rival commands receive deterministic sequence numbers. Rival tie-breaking also consumes the simulation-owned seeded RNG.

The current simulator is a prototype rules slice, not the final card/effect engine. It intentionally does not yet implement triggers, spells, environments, hero abilities, targeting choices, modifiers, replay serialization, or the production effect queue.

## Tests

With Node.js installed, run from the repository root:

```bash
node --test simulator/tests/simulation.test.js
```

The current tests cover opening-state determinism, rejected-command immutability, legal card deployment, unopposed hero damage, simultaneous unit combat/death cleanup, and deterministic rival command selection.

## Prototype boundary

Do not copy simulator DOM logic into future Unity simulation code. The portable concepts are the command/state/rules model and deterministic behaviors. Unity presentation should consume the production C# simulation rather than reproduce rules in scene scripts.
