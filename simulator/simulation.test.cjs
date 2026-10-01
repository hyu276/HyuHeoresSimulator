/**
 * SIMULATOR_CONTENT_PACKAGE_TESTS
 * Purpose: Verifies the HTML simulator consumes the signed prototype package instead of maintaining duplicate card-definition fixtures.
 * Connections: Protects content/packages/prototype-v1.json, web-simulator.html, and the repository auto-merge simulator test gate.
 * Risk: Medium because presentation drift can silently desynchronize the browser prototype from authoritative published content.
 */
const assert = require("node:assert/strict");
const crypto = require("node:crypto");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");
const vm = require("node:vm");

const root = path.resolve(__dirname, "..");
const packagePath = path.join(root, "content", "packages", "prototype-v1.json");
const simulatorPath = path.join(root, "web-simulator.html");

function readPackage() {
  return JSON.parse(fs.readFileSync(packagePath, "utf8"));
}

function canonicalHash(packageData) {
  const unsigned = { ...packageData };
  delete unsigned.contentHash;
  const bytes = Buffer.from(JSON.stringify(unsigned), "utf8");
  return "sha256:" + crypto.createHash("sha256").update(bytes).digest("hex");
}

test("prototype package has a valid canonical content hash", () => {
  const packageData = readPackage();

  assert.equal(packageData.schemaVersion, 1);
  assert.match(packageData.contentHash, /^sha256:[0-9a-f]{64}$/);
  assert.equal(canonicalHash(packageData), packageData.contentHash);
});

test("simulator loads package definitions instead of a duplicate CARD_DEFINITIONS object", () => {
  const html = fs.readFileSync(simulatorPath, "utf8");

  assert.match(html, /CONTENT_PACKAGE_URL = "\.\/content\/packages\/prototype-v1\.json"/);
  assert.match(html, /buildCardDefinitions\(contentPackage\)/);
  assert.doesNotMatch(html, /const CARD_DEFINITIONS\s*=/);
});

test("all simulator fixture definition IDs exist in the prototype package", () => {
  const packageData = readPackage();
  const packageIds = new Set(packageData.cards.map(card => card.header.id));
  const html = fs.readFileSync(simulatorPath, "utf8");
  const fixtureIds = [...html.matchAll(/definitionId: "([^"]+)"/g)].map(match => match[1]);

  assert.ok(fixtureIds.length > 0);
  fixtureIds.forEach(id => assert.ok(packageIds.has(id), `Missing fixture definition '${id}' in package.`));
});

test("embedded simulator JavaScript remains syntactically valid", () => {
  const html = fs.readFileSync(simulatorPath, "utf8");
  const scripts = [...html.matchAll(/<script>([\s\S]*?)<\/script>/g)];

  assert.equal(scripts.length, 1);
  assert.doesNotThrow(() => new vm.Script(scripts[0][1], { filename: "web-simulator-inline.js" }));
});
