/**
 * SIMULATOR_CONTENT_PACKAGE_TESTS
 * Purpose: Verifies the HTML simulator resolves version-pinned localized content through the published manifest without duplicate card-definition fixtures.
 * Connections: Protects content/manifest.json, prototype-v2.json, web-simulator.html, and the repository auto-merge simulator test gate.
 * Risk: Medium because presentation drift can silently desynchronize the browser prototype from authoritative published content.
 */
const assert = require("node:assert/strict");
const crypto = require("node:crypto");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");
const vm = require("node:vm");

const root = path.resolve(__dirname, "..");
const manifestPath = path.join(root, "content", "manifest.json");
const simulatorPath = path.join(root, "web-simulator.html");

function readJson(filePath) {
  return JSON.parse(fs.readFileSync(filePath, "utf8"));
}

function readManifestAndDefaultPackage() {
  const manifest = readJson(manifestPath);
  const entry = manifest.packages.find(item => item.contentVersion === manifest.defaultContentVersion);
  assert.ok(entry, "Default manifest entry must exist.");
  return {
    manifest,
    entry,
    packageData: readJson(path.join(root, entry.packagePath))
  };
}

function canonicalHash(packageData) {
  const unsigned = { ...packageData };
  delete unsigned.contentHash;
  const bytes = Buffer.from(JSON.stringify(unsigned), "utf8");
  return "sha256:" + crypto.createHash("sha256").update(bytes).digest("hex");
}

test("manifest pins the default package by exact version and hash", () => {
  const { manifest, entry, packageData } = readManifestAndDefaultPackage();

  assert.equal(manifest.manifestVersion, 1);
  assert.equal(entry.contentVersion, "prototype.2");
  assert.equal(packageData.contentVersion, entry.contentVersion);
  assert.equal(packageData.contentHash, entry.contentHash);
  assert.equal(packageData.schemaVersion, entry.schemaVersion);
});

test("prototype v2 package has a valid canonical content hash", () => {
  const { packageData } = readManifestAndDefaultPackage();

  assert.match(packageData.contentHash, /^sha256:[0-9a-f]{64}$/);
  assert.equal(canonicalHash(packageData), packageData.contentHash);
});

test("default locale covers every published card and ability localization key", () => {
  const { entry, packageData } = readManifestAndDefaultPackage();
  const presentation = packageData.presentation;
  const bundle = presentation.localizations[presentation.defaultLocale];
  const definitions = [...packageData.cards, ...packageData.abilities];

  assert.ok(entry.locales.includes(presentation.defaultLocale));
  definitions.forEach(definition => {
    assert.ok(bundle[definition.header.localizationKey], `Missing localization '${definition.header.localizationKey}'.`);
    assert.ok(bundle[definition.header.localizationKey].name);
  });
});

test("simulator resolves manifest pins instead of hard-coding a package URL", () => {
  const html = fs.readFileSync(simulatorPath, "utf8");

  assert.match(html, /CONTENT_MANIFEST_URL = "\.\/content\/manifest\.json"/);
  assert.match(html, /resolveManifestEntry\(contentManifest\)/);
  assert.match(html, /contentVersion/);
  assert.match(html, /contentHash/);
  assert.doesNotMatch(html, /CONTENT_PACKAGE_URL\s*=/);
  assert.doesNotMatch(html, /const CARD_DEFINITIONS\s*=/);
});

test("all simulator fixture definition IDs exist in the manifest default package", () => {
  const { packageData } = readManifestAndDefaultPackage();
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
