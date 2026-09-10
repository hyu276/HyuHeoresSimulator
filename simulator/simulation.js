"use strict";

(function exposeSimulation(globalScope) {
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
    "unit_ember_fox", "unit_aegis_drone", "unit_arc_runner", "unit_moss_golem",
    "unit_nova_adept", "unit_iron_hound", "unit_arc_runner", "unit_aegis_drone",
    "unit_ember_fox", "unit_moss_golem", "unit_nova_adept", "unit_iron_hound"
  ]);

  const OPPONENT_DECK = Object.freeze([
    "unit_void_moth", "unit_rift_knight", "unit_arc_runner", "unit_moss_golem",
    "unit_nova_adept", "unit_iron_hound", "unit_void_moth", "unit_rift_knight",
    "unit_arc_runner", "unit_moss_golem", "unit_nova_adept", "unit_iron_hound"
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
    constructor({ config = MATCH_CONFIG, definitions = CARD_DEFINITIONS, seed = config.seed } = {}) {
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
      const validator = this.getValidator(command.type);
      return validator ? validator(command) : { ok: false, reason: `Unknown command: ${command.type}` };
    }

    getValidator(commandType) {
      const validators = {
        PlayCard: (command) => this.validatePlayCard(command),
        EndPlayerAction: () => this.validatePhaseCommand("player"),
        EndOpponentAction: () => this.validatePhaseCommand("opponent"),
        ResolveCombat: () => this.validatePhaseCommand("combat")
      };
      return validators[commandType];
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
      return this.validateKnownPlayerCardCommand(command, player);
    }

    validateKnownPlayerCardCommand(command, player) {
      const phaseError = this.validatePlayPhase(command.playerId);
      if (phaseError) {
        return phaseError;
      }
      const laneError = this.validateLane(player, command.laneIndex);
      return laneError || this.validateCardAndCost(player, command.instanceId);
    }

    validatePlayPhase(playerId) {
      if (this.state.phase !== playerId) {
        return { ok: false, reason: "It is not that player's action phase." };
      }
      return null;
    }

    validateLane(player, laneIndex) {
      if (!Number.isInteger(laneIndex) || laneIndex < 0 || laneIndex >= this.config.laneCount) {
        return { ok: false, reason: "Invalid lane." };
      }
      if (player.board[laneIndex]) {
        return { ok: false, reason: "That lane is occupied." };
      }
      return null;
    }

    validateCardAndCost(player, instanceId) {
      const card = player.hand.find((item) => item.instanceId === instanceId);
      if (!card) {
        return { ok: false, reason: "Card is not in hand." };
      }
      const definition = this.getDefinition(card.definitionId);
      return player.energy < definition.cost
        ? { ok: false, reason: "Not enough energy." }
        : { ok: true };
    }

    execute(command) {
      const validation = this.validate(command);
      if (!validation.ok) {
        return { accepted: false, reason: validation.reason, events: [] };
      }
      this.recordCommand(command);
      return this.executeValidated(command);
    }

    recordCommand(command) {
      this.commandSequence += 1;
      this.commandLog.push({ sequence: this.commandSequence, ...command });
    }

    executeValidated(command) {
      const executors = {
        PlayCard: () => this.executePlayCard(command),
        EndPlayerAction: () => this.changePhase("opponent"),
        EndOpponentAction: () => this.changePhase("combat"),
        ResolveCombat: () => this.resolveCombat()
      };
      return executors[command.type]();
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
      const emptyLanes = this.getEmptyLanes(player);
      return this.buildAffordablePlayCommands(player, playerId, emptyLanes);
    }

    getEmptyLanes(player) {
      return player.board
        .map((card, laneIndex) => (card ? null : laneIndex))
        .filter((laneIndex) => laneIndex !== null);
    }

    buildAffordablePlayCommands(player, playerId, emptyLanes) {
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
      const scored = legalCommands.map((command) => ({ command, score: this.scoreOpponentCommand(command) }));
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
      this.resolveOccupiedLane(playerUnit, opponentUnit, laneIndex, events);
    }

    resolveOccupiedLane(playerUnit, opponentUnit, laneIndex, events) {
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
      events.push(this.createUnitAttackEvent(playerUnit, opponentUnit, playerDamage, laneIndex));
      events.push(this.createUnitAttackEvent(opponentUnit, playerUnit, opponentDamage, laneIndex));
    }

    createUnitAttackEvent(attacker, target, damage, laneIndex) {
      return { type: "UnitAttacked", attackerId: attacker.instanceId, targetId: target.instanceId, damage, laneIndex };
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
        if (card && card.health <= 0) {
          this.moveDeadUnitToGraveyard(player, card, laneIndex, events);
        }
      }
    }

    moveDeadUnitToGraveyard(player, card, laneIndex, events) {
      player.board[laneIndex] = null;
      player.graveyard.push(card);
      events.push({ type: "UnitDied", playerId: player.id, laneIndex, instanceId: card.instanceId, definitionId: card.definitionId });
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
      this.drawTurnCards(events);
      events.push({ type: "TurnStarted", turn: this.state.turn });
      events.push({ type: "PhaseChanged", phase: "player", turn: this.state.turn });
    }

    drawTurnCards(events) {
      for (let drawIndex = 0; drawIndex < this.config.cardsDrawnPerTurn; drawIndex += 1) {
        this.drawCard("player", events);
        this.drawCard("opponent", events);
      }
    }

    refillEnergy(playerId) {
      const player = this.state.players[playerId];
      player.maxEnergy = Math.min(this.config.energyCap, player.maxEnergy + 1);
      player.energy = player.maxEnergy;
    }

    createAuthoritativeSnapshot() {
      return JSON.parse(JSON.stringify({
        turn: this.state.turn,
        phase: this.state.phase,
        winner: this.state.winner,
        players: this.state.players,
        rngState: this.rng.state,
        instanceSequence: this.instanceSequence,
        commandSequence: this.commandSequence
      }));
    }
  }

  function createDefaultSimulation() {
    return new BattleSimulation();
  }

  const publicApi = Object.freeze({
    MATCH_CONFIG,
    CARD_DEFINITIONS,
    PLAYER_DECK,
    OPPONENT_DECK,
    SeededRng,
    BattleSimulation,
    createDefaultSimulation
  });

  if (typeof module !== "undefined" && module.exports) {
    module.exports = publicApi;
  } else {
    globalScope.HyuSimulation = publicApi;
  }
}(typeof globalThis !== "undefined" ? globalThis : this));
