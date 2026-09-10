"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const {
  BattleSimulation,
  MATCH_CONFIG
} = require("../simulation.js");

function createSimulation() {
  return new BattleSimulation();
}

function deployFixture(simulation, playerId, definitionId, laneIndex) {
  const card = simulation.createCardInstance(definitionId, playerId);
  simulation.state.players[playerId].board[laneIndex] = card;
  return card;
}

test("reset with the same seed reproduces the authoritative opening state", () => {
  const first = createSimulation();
  const second = createSimulation();

  assert.deepEqual(first.createAuthoritativeSnapshot(), second.createAuthoritativeSnapshot());
});

test("rejected commands do not mutate state or enter the command log", () => {
  const simulation = createSimulation();
  const before = simulation.createAuthoritativeSnapshot();
  const card = simulation.state.players.player.hand[0];

  simulation.state.players.player.energy = 0;
  const result = simulation.execute({
    type: "PlayCard",
    playerId: "player",
    instanceId: card.instanceId,
    laneIndex: 0
  });

  assert.equal(result.accepted, false);
  assert.equal(simulation.commandLog.length, 0);
  assert.equal(simulation.state.players.player.board[0], null);
  assert.equal(simulation.state.players.player.hand.length, before.players.player.hand.length);
});

test("legal PlayCard spends energy and moves the runtime instance to the selected lane", () => {
  const simulation = createSimulation();
  const player = simulation.state.players.player;
  const card = player.hand[0];
  const definition = simulation.getDefinition(card.definitionId);
  player.energy = 10;
  const startingHandSize = player.hand.length;

  const result = simulation.execute({
    type: "PlayCard",
    playerId: "player",
    instanceId: card.instanceId,
    laneIndex: 2
  });

  assert.equal(result.accepted, true);
  assert.equal(player.board[2].instanceId, card.instanceId);
  assert.equal(player.hand.length, startingHandSize - 1);
  assert.equal(player.energy, 10 - definition.cost);
  assert.equal(simulation.commandLog.length, 1);
});

test("an unopposed unit damages the opposing core during deterministic lane combat", () => {
  const simulation = createSimulation();
  const attacker = deployFixture(simulation, "player", "unit_ember_fox", 0);
  simulation.state.phase = "combat";

  const result = simulation.execute({ type: "ResolveCombat" });
  const damageEvent = result.events.find((event) => event.type === "HeroDamaged");

  assert.equal(damageEvent.sourceInstanceId, attacker.instanceId);
  assert.equal(damageEvent.damage, 2);
  assert.equal(simulation.state.players.opponent.health, MATCH_CONFIG.startingHealth - 2);
  assert.equal(simulation.state.turn, 2);
  assert.equal(simulation.state.phase, "player");
});

test("opposing units deal damage simultaneously and dead units move to graveyards", () => {
  const simulation = createSimulation();
  deployFixture(simulation, "player", "unit_ember_fox", 1);
  deployFixture(simulation, "opponent", "unit_void_moth", 1);
  simulation.state.phase = "combat";

  const result = simulation.execute({ type: "ResolveCombat" });
  const attackEvents = result.events.filter((event) => event.type === "UnitAttacked");
  const deathEvents = result.events.filter((event) => event.type === "UnitDied");

  assert.equal(attackEvents.length, 2);
  assert.equal(deathEvents.length, 2);
  assert.equal(simulation.state.players.player.board[1], null);
  assert.equal(simulation.state.players.opponent.board[1], null);
  assert.equal(simulation.state.players.player.graveyard.length, 1);
  assert.equal(simulation.state.players.opponent.graveyard.length, 1);
});

test("same state and RNG seed choose the same rival legal command", () => {
  const first = createSimulation();
  const second = createSimulation();
  first.execute({ type: "EndPlayerAction" });
  second.execute({ type: "EndPlayerAction" });

  assert.deepEqual(first.chooseOpponentPlayCommand(), second.chooseOpponentPlayCommand());
});
