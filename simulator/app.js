"use strict";

const MATCH_CONFIG = Object.freeze({
  laneCount: 5,
  startingHealth: 20,
  startingEnergy: 1,
  energyCap: 10,
  openingHandSize: 4,
  cardsDrawnPerTurn: 1,
  seed: 2762026
});

const CARD_DEFINITIONS = Object.freeze([
  { id: "unit_ember_fox", name: "Ember Fox", role: "Striker", cost: 1, attack: 2, health: 1, glyph: "✦", accent: "#ff9d68", description: "Fast pressure unit." },
  { id: "unit_aegis_drone", name: "Aegis Drone", role: "Guard", cost: 1, attack: 1, health: 3, glyph: "⬡", accent: "#74f2e6", description: "Stable early defender." },
  { id: "unit_arc_runner", name: "Arc Runner", role: "Raider", cost: 2, attack: 3, health: 2, glyph: "⌁", accent: "#75b9ff", description: "High-tempo lane attacker." },
  { id: "unit_moss_golem", name: "Moss Golem", role: "Bulwark", cost: 2, attack: 2, health: 4, glyph: "◆", accent: "#76e39f", description: "Durable board presence." },
  { id: "unit_nova_adept", name: "Nova Adept", role: "Caster", cost: 3, attack: 4, health: 3, glyph: "✧", accent: "#d2a4ff", description: "Expensive, efficient threat." },
  { id: "unit_iron_hound", name: "Iron Hound", role: "Bruiser", cost: 3, attack: 3, health: 5, glyph: "⬢", accent: "#ffd166", description: "Heavy midgame combatant." },
  { id: "unit_void_moth", name: "Void Moth", role: "Skirmisher", cost: 1, attack: 1, health: 2, glyph: "◇", accent: "#a493ff", description: "Cheap rival tempo unit." },
  { id: "unit_rift_knight", name: "Rift Knight", role: "Vanguard", cost: 2, attack: 2, health: 3, glyph: "◈", accent: "#c18cff", description: "Balanced rival unit." }
]);

const PLAYER_DECK = Object.freeze([
  "unit_ember_fox",
  "unit_aegis_drone",
  "unit_arc_runner",
  "unit_moss_golem",
  "unit_nova_adept",
  "unit_iron_hound",
  "unit_arc_runner",
  "unit_aegis_drone",
  "unit_ember_fox",
  "unit_moss_golem",
  "unit_nova_adept",
  "unit_iron_hound"
]);

const OPPONENT_DECK = Object.freeze([
  "unit_void_moth",
  "unit_rift_knight",
  "unit_arc_runner",
  "unit_moss_golem",
  "unit_nova_adept",
  "unit_iron_hound",
  "unit_void_moth",
  "unit_rift_knight",
  "unit_arc_runner",
  "unit_moss_golem",
  "unit_nova_adept",
  "unit_iron_hound"
]);

class SeededRng {
  constructor(seed) {
    this.state = seed >>> 0 || 1;
  }

  nextUint() {
    let value = this.state;
    value ^= value << 13;
    value ^= value >>> 17;
    value ^= value << 5;
    this.state = value >>> 0;
    return this.state;
  }

  nextInt(maxExclusive) {
    if (!Number.isInteger(maxExclusive) || maxExclusive <= 0) {
      throw new RangeError("maxExclusive must be a positive integer.");
    }
    return this.nextUint() % maxExclusive;
  }
}

class BattleSimulation {
  constructor(config, definitions, seed) {
    this.config = config;
    this.definitionMap = new Map(definitions.map((definition) => [definition.id, definition]));
    this.seed = seed;
    this.reset();
  }

  reset() {
    this.rng = new SeededRng(this.seed);
    this.instanceSequence = 0;
    this.commandSequence = 0;
    this.commandLog = [];
    this.state = {
      turn: 1,
      phase: "player",
      winner: null,
      players: {
        player: this.createPlayerState("player", PLAYER_DECK),
        opponent: this.createPlayerState("opponent", OPPONENT_DECK)
      }
    };

    const events = [{ type: "MatchStarted", turn: 1 }];
    this.drawOpeningHands(events);
    return events;
  }

  createPlayerState(id, deckDefinitionIds) {
    const deck = [...deckDefinitionIds];
    this.shuffle(deck);
    return {
      id,
      health: this.config.startingHealth,
      maxEnergy: this.config.startingEnergy,
      energy: this.config.startingEnergy,
      deck,
      hand: [],
      board: Array(this.config.laneCount).fill(null),
      graveyard: []
    };
  }

  shuffle(items) {
    for (let index = items.length - 1; index > 0; index -= 1) {
      const swapIndex = this.rng.nextInt(index + 1);
      [items[index], items[swapIndex]] = [items[swapIndex], items[index]];
    }
  }

  drawOpeningHands(events) {
    for (let count = 0; count < this.config.openingHandSize; count += 1) {
      this.drawCard("player", events);
      this.drawCard("opponent", events);
    }
  }

  createCardInstance(definitionId, ownerId) {
    const definition = this.getDefinition(definitionId);
    this.instanceSequence += 1;
    return {
      instanceId: `card_${this.instanceSequence}`,
      definitionId,
      ownerId,
      attack: definition.attack,
      health: definition.health
    };
  }

  getDefinition(definitionId) {
    const definition = this.definitionMap.get(definitionId);
    if (!definition) {
      throw new Error(`Unknown card definition: ${definitionId}`);
    }
    return definition;
  }

  drawCard(playerId, events) {
    const player = this.state.players[playerId];
    if (player.deck.length === 0) {
      events.push({ type: "DeckEmpty", playerId });
      return;
    }

    const definitionId = player.deck.shift();
    const instance = this.createCardInstance(definitionId, playerId);
    player.hand.push(instance);
    events.push({ type: "CardDrawn", playerId, instanceId: instance.instanceId, definitionId });
  }

  validate(command) {
    if (this.state.winner) {
      return { ok: false, reason: "The match is already complete." };
    }

    if (command.type === "PlayCard") {
      return this.validatePlayCard(command);
    }

    if (command.type === "EndPlayerAction") {
      return this.validatePhaseCommand("player");
    }

    if (command.type === "EndOpponentAction") {
      return this.validatePhaseCommand("opponent");
    }

    if (command.type === "ResolveCombat") {
      return this.validatePhaseCommand("combat");
    }

    return { ok: false, reason: `Unknown command: ${command.type}` };
  }

  validatePhaseCommand(requiredPhase) {
    if (this.state.phase !== requiredPhase) {
      return { ok: false, reason: `Command requires ${requiredPhase} phase.` };
    }
    return { ok: true };
  }

  validatePlayCard(command) {
    const player = this.state.players[command.playerId];
    if (!player) {
      return { ok: false, reason: "Unknown player." };
    }

    if (this.state.phase !== command.playerId) {
      return { ok: false, reason: "It is not that player's action phase." };
    }

    if (!Number.isInteger(command.laneIndex) || command.laneIndex < 0 || command.laneIndex >= this.config.laneCount) {
      return { ok: false, reason: "Invalid lane." };
    }

    if (player.board[command.laneIndex]) {
      return { ok: false, reason: "That lane is occupied." };
    }

    const card = player.hand.find((item) => item.instanceId === command.instanceId);
    if (!card) {
      return { ok: false, reason: "Card is not in hand." };
    }

    const definition = this.getDefinition(card.definitionId);
    if (player.energy < definition.cost) {
      return { ok: false, reason: "Not enough energy." };
    }

    return { ok: true };
  }

  execute(command) {
    const validation = this.validate(command);
    if (!validation.ok) {
      return { accepted: false, reason: validation.reason, events: [] };
    }

    this.commandSequence += 1;
    this.commandLog.push({ sequence: this.commandSequence, ...command });

    if (command.type === "PlayCard") {
      return this.executePlayCard(command);
    }

    if (command.type === "EndPlayerAction") {
      return this.changePhase("opponent");
    }

    if (command.type === "EndOpponentAction") {
      return this.changePhase("combat");
    }

    return this.resolveCombat();
  }

  executePlayCard(command) {
    const player = this.state.players[command.playerId];
    const handIndex = player.hand.findIndex((item) => item.instanceId === command.instanceId);
    const [card] = player.hand.splice(handIndex, 1);
    const definition = this.getDefinition(card.definitionId);

    player.energy -= definition.cost;
    player.board[command.laneIndex] = card;

    return {
      accepted: true,
      events: [{
        type: "CardPlayed",
        playerId: command.playerId,
        laneIndex: command.laneIndex,
        instanceId: card.instanceId,
        definitionId: card.definitionId,
        cost: definition.cost
      }]
    };
  }

  changePhase(phase) {
    this.state.phase = phase;
    return { accepted: true, events: [{ type: "PhaseChanged", phase, turn: this.state.turn }] };
  }

  getLegalPlayCommands(playerId) {
    const player = this.state.players[playerId];
    if (!player || this.state.phase !== playerId) {
      return [];
    }

    const emptyLanes = player.board
      .map((card, laneIndex) => (card ? null : laneIndex))
      .filter((laneIndex) => laneIndex !== null);

    const commands = [];
    for (const card of player.hand) {
      const definition = this.getDefinition(card.definitionId);
      if (definition.cost > player.energy) {
        continue;
      }
      for (const laneIndex of emptyLanes) {
        commands.push({ type: "PlayCard", playerId, instanceId: card.instanceId, laneIndex });
      }
    }
    return commands;
  }

  chooseOpponentPlayCommand() {
    const legalCommands = this.getLegalPlayCommands("opponent");
    if (legalCommands.length === 0) {
      return null;
    }

    const scored = legalCommands.map((command) => ({
      command,
      score: this.scoreOpponentCommand(command)
    }));
    const bestScore = Math.max(...scored.map((item) => item.score));
    const bestCommands = scored.filter((item) => item.score === bestScore);
    return bestCommands[this.rng.nextInt(bestCommands.length)].command;
  }

  scoreOpponentCommand(command) {
    const opponent = this.state.players.opponent;
    const player = this.state.players.player;
    const card = opponent.hand.find((item) => item.instanceId === command.instanceId);
    const definition = this.getDefinition(card.definitionId);
    const opposingUnit = player.board[command.laneIndex];
    const lanePressure = opposingUnit ? opposingUnit.attack + opposingUnit.health : 2;
    return definition.attack * 3 + definition.health + lanePressure - definition.cost;
  }

  resolveCombat() {
    const events = [{ type: "CombatStarted", turn: this.state.turn }];

    for (let laneIndex = 0; laneIndex < this.config.laneCount; laneIndex += 1) {
      if (this.state.winner) {
        break;
      }
      this.resolveLane(laneIndex, events);
      this.removeDeadUnits(events);
      this.evaluateWinner(events);
    }

    if (!this.state.winner) {
      this.beginNextTurn(events);
    }

    return { accepted: true, events };
  }

  resolveLane(laneIndex, events) {
    const playerUnit = this.state.players.player.board[laneIndex];
    const opponentUnit = this.state.players.opponent.board[laneIndex];

    if (!playerUnit && !opponentUnit) {
      return;
    }

    events.push({ type: "LaneCombatStarted", laneIndex });

    if (playerUnit && opponentUnit) {
      this.resolveUnitClash(playerUnit, opponentUnit, laneIndex, events);
      return;
    }

    if (playerUnit) {
      this.applyHeroDamage("opponent", playerUnit.attack, playerUnit.instanceId, laneIndex, events);
      return;
    }

    this.applyHeroDamage("player", opponentUnit.attack, opponentUnit.instanceId, laneIndex, events);
  }

  resolveUnitClash(playerUnit, opponentUnit, laneIndex, events) {
    const playerDamage = playerUnit.attack;
    const opponentDamage = opponentUnit.attack;

    opponentUnit.health -= playerDamage;
    playerUnit.health -= opponentDamage;

    events.push({
      type: "UnitAttacked",
      attackerId: playerUnit.instanceId,
      targetId: opponentUnit.instanceId,
      damage: playerDamage,
      laneIndex
    });
    events.push({
      type: "UnitAttacked",
      attackerId: opponentUnit.instanceId,
      targetId: playerUnit.instanceId,
      damage: opponentDamage,
      laneIndex
    });
  }

  applyHeroDamage(targetPlayerId, amount, sourceInstanceId, laneIndex, events) {
    const target = this.state.players[targetPlayerId];
    target.health = Math.max(0, target.health - amount);
    events.push({ type: "HeroDamaged", targetPlayerId, sourceInstanceId, damage: amount, laneIndex, health: target.health });
  }

  removeDeadUnits(events) {
    this.removeDeadUnitsForPlayer("player", events);
    this.removeDeadUnitsForPlayer("opponent", events);
  }

  removeDeadUnitsForPlayer(playerId, events) {
    const player = this.state.players[playerId];
    for (let laneIndex = 0; laneIndex < player.board.length; laneIndex += 1) {
      const card = player.board[laneIndex];
      if (!card || card.health > 0) {
        continue;
      }
      player.board[laneIndex] = null;
      player.graveyard.push(card);
      events.push({ type: "UnitDied", playerId, laneIndex, instanceId: card.instanceId, definitionId: card.definitionId });
    }
  }

  evaluateWinner(events) {
    const playerDead = this.state.players.player.health <= 0;
    const opponentDead = this.state.players.opponent.health <= 0;

    if (!playerDead && !opponentDead) {
      return;
    }

    this.state.winner = this.resolveWinner(playerDead, opponentDead);
    this.state.phase = "complete";
    events.push({ type: "MatchEnded", winner: this.state.winner });
  }

  resolveWinner(playerDead, opponentDead) {
    if (playerDead && opponentDead) {
      return "draw";
    }
    return opponentDead ? "player" : "opponent";
  }

  beginNextTurn(events) {
    this.state.turn += 1;
    this.state.phase = "player";
    this.refillEnergy("player");
    this.refillEnergy("opponent");

    for (let drawIndex = 0; drawIndex < this.config.cardsDrawnPerTurn; drawIndex += 1) {
      this.drawCard("player", events);
      this.drawCard("opponent", events);
    }

    events.push({ type: "TurnStarted", turn: this.state.turn });
    events.push({ type: "PhaseChanged", phase: "player", turn: this.state.turn });
  }

  refillEnergy(playerId) {
    const player = this.state.players[playerId];
    player.maxEnergy = Math.min(this.config.energyCap, player.maxEnergy + 1);
    player.energy = player.maxEnergy;
  }
}

class SimulatorView {
  constructor(simulation) {
    this.simulation = simulation;
    this.selectedCardId = null;
    this.inputLocked = false;
    this.lastDrawnCardId = null;
    this.cacheElements();
    this.buildLanes();
    this.bindEvents();
    this.renderAll();
    this.log("Simulation initialized with deterministic seed 2762026.");
  }

  cacheElements() {
    this.elements = {
      turnLabel: document.querySelector("#turnLabel"),
      phasePill: document.querySelector("#phasePill"),
      playerHealth: document.querySelector("#playerHealth"),
      opponentHealth: document.querySelector("#opponentHealth"),
      playerEnergy: document.querySelector("#playerEnergy"),
      opponentEnergy: document.querySelector("#opponentEnergy"),
      playerDeckCount: document.querySelector("#playerDeckCount"),
      opponentDeckCount: document.querySelector("#opponentDeckCount"),
      laneGrid: document.querySelector("#laneGrid"),
      playerHand: document.querySelector("#playerHand"),
      hintLine: document.querySelector("#hintLine"),
      endPhaseButton: document.querySelector("#endPhaseButton"),
      resetButton: document.querySelector("#resetButton"),
      logToggle: document.querySelector("#logToggle"),
      closeLog: document.querySelector("#closeLog"),
      combatLog: document.querySelector("#combatLog"),
      combatLogList: document.querySelector("#combatLogList"),
      phaseOverlay: document.querySelector("#phaseOverlay"),
      overlayKicker: document.querySelector("#overlayKicker"),
      overlayTitle: document.querySelector("#overlayTitle"),
      resultOverlay: document.querySelector("#resultOverlay"),
      resultTitle: document.querySelector("#resultTitle"),
      resultCopy: document.querySelector("#resultCopy"),
      playAgainButton: document.querySelector("#playAgainButton"),
      laneTemplate: document.querySelector("#laneTemplate"),
      unitTemplate: document.querySelector("#unitTemplate"),
      handCardTemplate: document.querySelector("#handCardTemplate")
    };
  }

  buildLanes() {
    this.elements.laneGrid.replaceChildren();
    for (let laneIndex = 0; laneIndex < this.simulation.config.laneCount; laneIndex += 1) {
      const lane = this.elements.laneTemplate.content.firstElementChild.cloneNode(true);
      lane.dataset.laneIndex = String(laneIndex);
      lane.querySelector(".lane-number").textContent = `L${laneIndex + 1}`;
      lane.querySelector(".player-slot").dataset.laneIndex = String(laneIndex);
      this.elements.laneGrid.append(lane);
    }
  }

  bindEvents() {
    this.elements.playerHand.addEventListener("click", (event) => this.handleHandClick(event));
    this.elements.laneGrid.addEventListener("click", (event) => this.handleLaneClick(event));
    this.elements.endPhaseButton.addEventListener("click", () => this.handleEndAction());
    this.elements.resetButton.addEventListener("click", () => this.resetSimulation());
    this.elements.playAgainButton.addEventListener("click", () => this.resetSimulation());
    this.elements.logToggle.addEventListener("click", () => this.setLogOpen(true));
    this.elements.closeLog.addEventListener("click", () => this.setLogOpen(false));
  }

  renderAll() {
    this.renderHeader();
    this.renderPlayerPanels();
    this.renderBoard();
    this.renderHand();
    this.renderLaneAffordances();
    this.updateControls();
  }

  renderHeader() {
    const { turn, phase } = this.simulation.state;
    this.elements.turnLabel.textContent = `TURN ${turn}`;
    this.elements.phasePill.textContent = this.getPhaseLabel(phase);
    this.elements.phasePill.dataset.phase = phase;
  }

  getPhaseLabel(phase) {
    const labels = {
      player: "PLAYER ACTION",
      opponent: "RIVAL ACTION",
      combat: "COMBAT",
      complete: "COMPLETE"
    };
    return labels[phase] || phase.toUpperCase();
  }

  renderPlayerPanels() {
    const { player, opponent } = this.simulation.state.players;
    this.elements.playerHealth.textContent = String(player.health);
    this.elements.opponentHealth.textContent = String(opponent.health);
    this.elements.playerEnergy.textContent = `${player.energy} / ${player.maxEnergy}`;
    this.elements.opponentEnergy.textContent = `${opponent.energy} / ${opponent.maxEnergy}`;
    this.elements.playerDeckCount.textContent = String(player.deck.length);
    this.elements.opponentDeckCount.textContent = String(opponent.deck.length);
  }

  renderBoard() {
    for (let laneIndex = 0; laneIndex < this.simulation.config.laneCount; laneIndex += 1) {
      const lane = this.elements.laneGrid.querySelector(`[data-lane-index="${laneIndex}"]`);
      this.renderBoardSlot(lane.querySelector(".player-slot"), this.simulation.state.players.player.board[laneIndex]);
      this.renderBoardSlot(lane.querySelector(".opponent-slot"), this.simulation.state.players.opponent.board[laneIndex]);
    }
  }

  renderBoardSlot(slot, card) {
    slot.replaceChildren();
    if (!card) {
      return;
    }

    const definition = this.simulation.getDefinition(card.definitionId);
    const unit = this.elements.unitTemplate.content.firstElementChild.cloneNode(true);
    unit.dataset.instanceId = card.instanceId;
    unit.style.setProperty("--unit-accent", definition.accent);
    unit.querySelector(".unit-glyph").textContent = definition.glyph;
    unit.querySelector(".unit-name").textContent = definition.name;
    unit.querySelector(".attack-stat").textContent = String(card.attack);
    unit.querySelector(".health-stat").textContent = String(card.health);
    slot.append(unit);
  }

  renderHand() {
    const player = this.simulation.state.players.player;
    this.elements.playerHand.replaceChildren();

    for (const card of player.hand) {
      this.elements.playerHand.append(this.createHandCard(card, player.energy));
    }
  }

  createHandCard(card, energy) {
    const definition = this.simulation.getDefinition(card.definitionId);
    const element = this.elements.handCardTemplate.content.firstElementChild.cloneNode(true);
    const unaffordable = definition.cost > energy;
    const canInteract = this.simulation.state.phase === "player" && !this.inputLocked && !this.simulation.state.winner;

    element.dataset.instanceId = card.instanceId;
    element.style.setProperty("--card-accent", definition.accent);
    element.classList.toggle("selected", card.instanceId === this.selectedCardId);
    element.classList.toggle("unaffordable", unaffordable);
    element.classList.toggle("card-draw-in", card.instanceId === this.lastDrawnCardId);
    element.disabled = !canInteract || unaffordable;
    element.querySelector(".card-cost").textContent = String(definition.cost);
    element.querySelector(".card-glyph").textContent = definition.glyph;
    element.querySelector(".card-name").textContent = definition.name;
    element.querySelector(".card-role").textContent = definition.role;
    element.querySelector(".card-description").textContent = definition.description;
    element.querySelector(".card-attack").textContent = String(definition.attack);
    element.querySelector(".card-health").textContent = String(definition.health);
    return element;
  }

  renderLaneAffordances() {
    const canDeploy = Boolean(this.selectedCardId) && this.simulation.state.phase === "player" && !this.inputLocked;
    const playerBoard = this.simulation.state.players.player.board;

    this.elements.laneGrid.querySelectorAll(".lane").forEach((lane) => {
      const laneIndex = Number(lane.dataset.laneIndex);
      lane.classList.toggle("playable", canDeploy && !playerBoard[laneIndex]);
    });
  }

  updateControls() {
    const isPlayerPhase = this.simulation.state.phase === "player";
    this.elements.endPhaseButton.disabled = this.inputLocked || !isPlayerPhase || Boolean(this.simulation.state.winner);

    if (this.simulation.state.winner) {
      this.setHint("Simulation complete.");
      return;
    }

    if (this.inputLocked) {
      this.setHint("Resolving simulation events…");
      return;
    }

    if (this.selectedCardId) {
      this.setHint("Choose an empty lane to deploy the selected unit.", true);
      return;
    }

    this.setHint(isPlayerPhase ? "Select a card, then choose an empty lane." : "Rival is resolving its action.");
  }

  handleHandClick(event) {
    const cardElement = event.target.closest(".hand-card");
    if (!cardElement || cardElement.disabled || this.inputLocked) {
      return;
    }

    const instanceId = cardElement.dataset.instanceId;
    this.selectedCardId = this.selectedCardId === instanceId ? null : instanceId;
    this.renderHand();
    this.renderLaneAffordances();
    this.updateControls();
  }

  async handleLaneClick(event) {
    const slot = event.target.closest(".player-slot");
    if (!slot || !this.selectedCardId || this.inputLocked) {
      return;
    }

    const command = {
      type: "PlayCard",
      playerId: "player",
      instanceId: this.selectedCardId,
      laneIndex: Number(slot.dataset.laneIndex)
    };
    await this.executeAndPresent(command);
  }

  async handleEndAction() {
    if (this.inputLocked) {
      return;
    }

    this.selectedCardId = null;
    this.setInputLocked(true);

    const phaseResult = this.simulation.execute({ type: "EndPlayerAction" });
    await this.presentEvents(phaseResult.events);
    await this.showPhaseOverlay("RIVAL", "Action Phase");
    await this.runOpponentActions();

    const combatPhase = this.simulation.execute({ type: "EndOpponentAction" });
    await this.presentEvents(combatPhase.events);
    await this.showPhaseOverlay("SYSTEM", "Combat");

    const combat = this.simulation.execute({ type: "ResolveCombat" });
    await this.presentEvents(combat.events);
    this.renderAll();

    if (this.simulation.state.winner) {
      this.showResult();
      return;
    }

    this.setInputLocked(false);
  }

  async runOpponentActions() {
    const maxActions = this.simulation.config.laneCount;
    for (let actionIndex = 0; actionIndex < maxActions; actionIndex += 1) {
      const command = this.simulation.chooseOpponentPlayCommand();
      if (!command) {
        return;
      }
      const result = this.simulation.execute(command);
      await this.presentEvents(result.events);
      await this.delay(180);
    }
  }

  async executeAndPresent(command) {
    const result = this.simulation.execute(command);
    if (!result.accepted) {
      this.setHint(result.reason, true);
      return;
    }

    this.selectedCardId = null;
    this.setInputLocked(true);
    await this.presentEvents(result.events);
    this.renderAll();
    this.setInputLocked(false);
  }

  async presentEvents(events) {
    for (const event of events) {
      await this.presentEvent(event);
    }
  }

  async presentEvent(event) {
    if (event.type === "CardPlayed") {
      await this.presentCardPlayed(event);
      return;
    }

    if (event.type === "PhaseChanged") {
      this.renderHeader();
      this.log(`Phase → ${this.getPhaseLabel(event.phase)}.`);
      return;
    }

    if (event.type === "LaneCombatStarted") {
      this.log(`Lane ${event.laneIndex + 1} begins combat.`);
      return;
    }

    if (event.type === "UnitAttacked") {
      await this.presentUnitAttack(event);
      return;
    }

    if (event.type === "HeroDamaged") {
      await this.presentHeroDamage(event);
      return;
    }

    if (event.type === "UnitDied") {
      await this.presentUnitDeath(event);
      return;
    }

    if (event.type === "CardDrawn") {
      this.lastDrawnCardId = event.playerId === "player" ? event.instanceId : null;
      this.log(`${this.playerLabel(event.playerId)} draws a card.`);
      return;
    }

    if (event.type === "TurnStarted") {
      this.log(`Turn ${event.turn} begins.`);
      return;
    }

    if (event.type === "MatchEnded") {
      this.log(`Match complete: ${event.winner}.`);
    }
  }

  async presentCardPlayed(event) {
    this.renderPlayerPanels();
    this.renderBoard();
    this.renderHand();
    const unit = this.findUnitElement(event.instanceId);
    unit?.classList.add("summon-in");
    const definition = this.simulation.getDefinition(event.definitionId);
    this.log(`${this.playerLabel(event.playerId)} deploys ${definition.name} to lane ${event.laneIndex + 1}.`);
    await this.delay(420);
  }

  async presentUnitAttack(event) {
    const attacker = this.findUnitElement(event.attackerId);
    const target = this.findUnitElement(event.targetId);
    attacker?.classList.add("attack-forward");
    await this.delay(250);
    target?.classList.add("take-hit");
    this.showFloatingDamage(target, event.damage);
    this.log(`Lane ${event.laneIndex + 1}: unit deals ${event.damage} damage.`);
    await this.delay(350);
    attacker?.classList.remove("attack-forward");
    target?.classList.remove("take-hit");
  }

  async presentHeroDamage(event) {
    const source = this.findUnitElement(event.sourceInstanceId);
    const healthElement = event.targetPlayerId === "player" ? this.elements.playerHealth : this.elements.opponentHealth;
    source?.classList.add("attack-forward");
    await this.delay(250);
    healthElement.textContent = String(event.health);
    healthElement.classList.add("damage-pop");
    this.showFloatingDamage(healthElement, event.damage);
    this.log(`Lane ${event.laneIndex + 1}: ${this.playerLabel(event.targetPlayerId)} core takes ${event.damage}.`);
    await this.delay(360);
    source?.classList.remove("attack-forward");
    healthElement.classList.remove("damage-pop");
  }

  async presentUnitDeath(event) {
    const unit = this.findUnitElement(event.instanceId);
    const definition = this.simulation.getDefinition(event.definitionId);
    unit?.classList.add("unit-die");
    this.log(`${definition.name} is removed from lane ${event.laneIndex + 1}.`);
    await this.delay(420);
  }

  findUnitElement(instanceId) {
    return this.elements.laneGrid.querySelector(`[data-instance-id="${instanceId}"]`);
  }

  showFloatingDamage(target, damage) {
    if (!target) {
      return;
    }
    const rect = target.getBoundingClientRect();
    const marker = document.createElement("span");
    marker.className = "floating-damage";
    marker.textContent = `−${damage}`;
    marker.style.left = `${rect.left + rect.width / 2}px`;
    marker.style.top = `${rect.top + rect.height / 2}px`;
    document.body.append(marker);
    window.setTimeout(() => marker.remove(), 800);
  }

  showPhaseOverlay(kicker, title) {
    this.elements.overlayKicker.textContent = kicker;
    this.elements.overlayTitle.textContent = title;
    this.elements.phaseOverlay.classList.remove("show");
    void this.elements.phaseOverlay.offsetWidth;
    this.elements.phaseOverlay.classList.add("show");
    return this.delay(780);
  }

  showResult() {
    const winner = this.simulation.state.winner;
    const presentation = {
      player: ["Victory", "The rival core has been reduced to zero."],
      opponent: ["Defeat", "Your core has been reduced to zero."],
      draw: ["Draw", "Both cores were destroyed in the same resolution window."]
    }[winner];

    this.elements.resultTitle.textContent = presentation[0];
    this.elements.resultCopy.textContent = presentation[1];
    this.elements.resultOverlay.classList.add("show");
    this.elements.resultOverlay.setAttribute("aria-hidden", "false");
  }

  resetSimulation() {
    const events = this.simulation.reset();
    this.selectedCardId = null;
    this.inputLocked = false;
    this.lastDrawnCardId = null;
    this.elements.resultOverlay.classList.remove("show");
    this.elements.resultOverlay.setAttribute("aria-hidden", "true");
    this.elements.combatLogList.replaceChildren();
    this.renderAll();
    this.log("Simulation reset. Seed and opening state reproduced.");
    this.log(`${events.filter((event) => event.type === "CardDrawn").length} opening draws resolved.`);
  }

  setInputLocked(locked) {
    this.inputLocked = locked;
    this.renderHand();
    this.renderLaneAffordances();
    this.updateControls();
  }

  setHint(message, attention = false) {
    this.elements.hintLine.textContent = message;
    this.elements.hintLine.classList.toggle("attention", attention);
  }

  setLogOpen(open) {
    this.elements.combatLog.classList.toggle("open", open);
    this.elements.combatLog.setAttribute("aria-hidden", String(!open));
    this.elements.logToggle.setAttribute("aria-expanded", String(open));
  }

  playerLabel(playerId) {
    return playerId === "player" ? "Player" : "Rival";
  }

  log(message) {
    const item = document.createElement("li");
    item.textContent = message;
    this.elements.combatLogList.append(item);
    this.elements.combatLogList.scrollTop = this.elements.combatLogList.scrollHeight;
  }

  delay(milliseconds) {
    return new Promise((resolve) => window.setTimeout(resolve, milliseconds));
  }
}

const simulation = new BattleSimulation(MATCH_CONFIG, CARD_DEFINITIONS, MATCH_CONFIG.seed);
new SimulatorView(simulation);
