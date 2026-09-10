import { createDefaultSimulation } from "./simulation.mjs";

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
    const canInteract = this.canInteractWithHand();

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

  canInteractWithHand() {
    return this.simulation.state.phase === "player" && !this.inputLocked && !this.simulation.state.winner;
  }

  renderLaneAffordances() {
    const canDeploy = Boolean(this.selectedCardId) && this.canInteractWithHand();
    const playerBoard = this.simulation.state.players.player.board;

    this.elements.laneGrid.querySelectorAll(".lane").forEach((lane) => {
      const laneIndex = Number(lane.dataset.laneIndex);
      lane.classList.toggle("playable", canDeploy && !playerBoard[laneIndex]);
    });
  }

  updateControls() {
    const isPlayerPhase = this.simulation.state.phase === "player";
    this.elements.endPhaseButton.disabled = this.inputLocked || !isPlayerPhase || Boolean(this.simulation.state.winner);
    this.updateHint(isPlayerPhase);
  }

  updateHint(isPlayerPhase) {
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

    await this.executeAndPresent({
      type: "PlayCard",
      playerId: "player",
      instanceId: this.selectedCardId,
      laneIndex: Number(slot.dataset.laneIndex)
    });
  }

  async handleEndAction() {
    if (this.inputLocked) {
      return;
    }

    this.selectedCardId = null;
    this.setInputLocked(true);
    await this.advanceToOpponent();
    await this.runOpponentActions();
    await this.advanceToCombat();
    await this.resolveAndPresentCombat();
  }

  async advanceToOpponent() {
    const result = this.simulation.execute({ type: "EndPlayerAction" });
    await this.presentEvents(result.events);
    await this.showPhaseOverlay("RIVAL", "Action Phase");
  }

  async advanceToCombat() {
    const result = this.simulation.execute({ type: "EndOpponentAction" });
    await this.presentEvents(result.events);
    await this.showPhaseOverlay("SYSTEM", "Combat");
  }

  async resolveAndPresentCombat() {
    const result = this.simulation.execute({ type: "ResolveCombat" });
    await this.presentEvents(result.events);
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
    const presenters = {
      CardPlayed: () => this.presentCardPlayed(event),
      PhaseChanged: () => this.presentPhaseChanged(event),
      LaneCombatStarted: () => this.presentLaneCombatStarted(event),
      UnitAttacked: () => this.presentUnitAttack(event),
      HeroDamaged: () => this.presentHeroDamage(event),
      UnitDied: () => this.presentUnitDeath(event),
      CardDrawn: () => this.presentCardDrawn(event),
      TurnStarted: () => this.presentTurnStarted(event),
      MatchEnded: () => this.presentMatchEnded(event)
    };
    const presenter = presenters[event.type];
    if (presenter) {
      await presenter();
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

  presentPhaseChanged(event) {
    this.renderHeader();
    this.log(`Phase → ${this.getPhaseLabel(event.phase)}.`);
  }

  presentLaneCombatStarted(event) {
    this.log(`Lane ${event.laneIndex + 1} begins combat.`);
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

  presentCardDrawn(event) {
    this.lastDrawnCardId = event.playerId === "player" ? event.instanceId : null;
    this.log(`${this.playerLabel(event.playerId)} draws a card.`);
  }

  presentTurnStarted(event) {
    this.log(`Turn ${event.turn} begins.`);
  }

  presentMatchEnded(event) {
    this.log(`Match complete: ${event.winner}.`);
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
    const presentations = {
      player: ["Victory", "The rival core has been reduced to zero."],
      opponent: ["Defeat", "Your core has been reduced to zero."],
      draw: ["Draw", "Both cores were destroyed in the same resolution window."]
    };
    const presentation = presentations[this.simulation.state.winner];
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

const simulation = createDefaultSimulation();
new SimulatorView(simulation);
