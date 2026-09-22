// Regression contract for independent Quest resources.
import fs from "node:fs";
import assert from "node:assert/strict";

const load = file => JSON.parse(fs.readFileSync("data/quests/" + file, "utf8")).definition;
const main = load("tutorial_ruslan_shashlik.aqquest");
const gosha = load("gosha_homemade_sausage.aqquest");
const delivery = load("ruslan_shashlik_delivery.aqquest");

assert.equal(main.id, "tutorial_ruslan_shashlik");
assert.equal(gosha.id, "gosha_homemade_sausage");
assert.equal(delivery.id, "ruslan_shashlik_delivery");
assert.equal(gosha.activation.requiredReputation, 350);
assert.equal(gosha.activation.repeatable, true);
assert.equal(delivery.activation.requiredReputation, 400);
assert.equal(delivery.activation.repeatable, false);

const mainIds = new Set(main.graph.nodes.map(n => n.nodeId));
assert.ok(mainIds.has("end-ruslan-meat"));
assert.ok(!mainIds.has("step-shop-gosha"));
assert.ok(!mainIds.has("step-delivery"));
assert.ok(!mainIds.has("scene-gosha-shop"));
assert.ok(!mainIds.has("scene-ruslan-delivery"));

const connectedToMain = main.graph.connections.some(c => c.fromNodeId.startsWith("shop-") || c.toNodeId.startsWith("shop-") || c.fromNodeId.startsWith("delivery-") || c.toNodeId.startsWith("delivery-"));
assert.equal(connectedToMain, false);

let goshaRep = 350;
let money = 1500;
for (let i = 0; i < 2; i++) { money -= 450; goshaRep += 25; }
assert.equal(money, 600);
assert.equal(goshaRep, 400);
assert.ok(400 >= delivery.activation.requiredReputation);
assert.ok(399 < delivery.activation.requiredReputation);

console.log("Quest 1 ends after the meat handoff.");
console.log("Quest 2 is an independent repeatable Gosha quest at 350+ reputation.");
console.log("Quest 3 is an independent Ruslan delivery quest at Gosha reputation 400+.");
console.log("OK");
