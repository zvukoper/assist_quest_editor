// Сквозной прогон игровых правил репутации по настоящему JSON-контенту квеста.
//
// Домен защищён юнит-тестами (ReputationRuntimeTests), но они собирают сцену
// кодом. Здесь проверяется сам файл data/quests/tutorial_ruslan_shashlik.aqquest:
// что покупка колбасы повторяется, а доставка открывается при репутации 400.
import fs from "node:fs";
import assert from "node:assert/strict";

const graph = JSON.parse(
  fs.readFileSync("data/quests/tutorial_ruslan_shashlik.aqquest", "utf8")).definition.graph;

const nodeById = new Map(graph.nodes.map(node => [node.nodeId, node]));
const nextByOutput = new Map(graph.connections.map(connection =>
  [`${connection.fromNodeId}\u001f${connection.fromSocketId}`, connection.toNodeId]));

// Выбор по умолчанию для сцен. Для магазина это «Уйти»: иначе повторная
// покупка замкнула бы проход сам на себя и проверка зависла бы на цикле.
const DEFAULT_CHOICE = {
  ruslan_start: "ruslan.offer.accept",
  gosha_meat: "gosha.meat.accept",
  ruslan_finish: "ruslan.finish.complete",
  gosha_shop: "gosha.shop.leave",
  ruslan_delivery: "ruslan.delivery.accept",
  gosha_delivery: "gosha.delivery.give"
};

const state = { reputation: { ruslan: 0, gosha: 0 }, money: 1500, items: {}, visited: [] };

function socketFor(node, name) {
  const socket = `${node.nodeId}.${name}`;
  assert.ok(node.sockets.some(s => s.socketId === socket), `нет socket ${socket}`);
  return socket;
}

/**
 * Проход графа от nodeId. `queues` — очередь выборов сцен: каждый вход в сцену
 * забирает следующий выбор, а когда очередь пуста, берётся DEFAULT_CHOICE.
 * Так один вызов может пройти и покупку, и выход из магазина.
 */
function run(nodeId, queues = {}) {
  const choices = {};
  for (const [sceneId, list] of Object.entries(queues)) choices[sceneId] = [...list];

  let current = nodeId;
  for (let guard = 0; guard < 500; guard++) {
    const node = nodeById.get(current);
    assert.ok(node, `нет ноды ${current}`);
    state.visited.push(current);

    switch (node.nodeType) {
      case "Start":
      case "Phase":
      case "SetStatus":
      case "SetStep":
        if (node.nodeType === "SetStep") state.step = node.parameters.step;
        current = nextByOutput.get(`${current}\u001f${socketFor(node, "out")}`);
        break;

      case "Interaction":
        // Игрок считается стоящим у нужной точки: проверяется контент, а не
        // ожидание близости.
        current = nextByOutput.get(`${current}\u001f${socketFor(node, "out")}`);
        break;

      case "RemoveMoney":
        state.money -= Number(node.parameters.amount);
        current = nextByOutput.get(`${current}\u001f${socketFor(node, "out")}`);
        break;

      case "GiveItem":
        state.items[node.parameters.itemId] =
          (state.items[node.parameters.itemId] || 0) + Number(node.parameters.count);
        current = nextByOutput.get(`${current}\u001f${socketFor(node, "out")}`);
        break;

      case "AddReputation":
        state.reputation[node.parameters.npcId] += Number(node.parameters.amount);
        current = nextByOutput.get(`${current}\u001f${socketFor(node, "out")}`);
        break;

      case "DialogueScene": {
        const sceneId = node.parameters.sceneId;
        const queue = choices[sceneId];
        state.lastChoice = queue && queue.length ? queue.shift() : DEFAULT_CHOICE[sceneId];
        current = nextByOutput.get(`${current}\u001f${socketFor(node, "out")}`);
        break;
      }

      case "Condition":
      case "ReputationCompare": {
        const branch = evaluate(node) ? "true" : "false";
        current = nextByOutput.get(`${current}\u001f${socketFor(node, branch)}`);
        break;
      }

      case "End":
        return state.visited.slice();

      default:
        throw new Error(`необработанный NodeType ${node.nodeType} в ${current}`);
    }

    assert.ok(current, `тупик после ${node.nodeId}`);
  }

  throw new Error("слишком длинный проход: возможен цикл");
}

function evaluate(node) {
  const p = node.parameters;
  if (node.nodeType === "ReputationCompare") {
    return compare(state.reputation[p.npcId] ?? 0, Number(p.right), p.comparison);
  }
  if (p.operator === "VariableEquals") {
    assert.ok(p.left.startsWith("scene."), `переменная ${p.left} не поддержана`);
    return String(state.lastChoice) === p.right;
  }
  throw new Error(`неизвестный оператор ${p.operator}`);
}

function compare(left, right, op) {
  if (op === ">=") return left >= right;
  if (op === ">") return left > right;
  if (op === "==") return left === right;
  throw new Error(`неизвестное сравнение ${op}`);
}

// --- Награда за квест Руслана: +500 Руслану и +350 Гоше.
state.reputation.ruslan += 500;
state.reputation.gosha += 350;

// --- Сценарий 1: две покупки подряд, затем выход из магазина.
//
// После выхода граф ведёт дальше по цепочке (шаг доставки), поэтому доставку
// здесь явно отклоняем: сценарий проверяет только покупку, а не награду.
run("step-shop-gosha", {
  gosha_shop: ["gosha.shop.buy", "gosha.shop.buy", "gosha.shop.leave"],
  ruslan_delivery: ["ruslan.delivery.decline"]
});

assert.equal(state.items["gosha.homemade_sausage"], 2, "покупка должна повторяться");
assert.equal(state.money, 1500 - 450 * 2, "каждая покупка стоит 450 ₽");
assert.equal(state.reputation.gosha, 350 + 25 * 2, "+25 репутации за каждую покупку");
assert.ok(state.visited.includes("shop-pay"), "оплата должна выполняться");
assert.ok(state.visited.includes("step-completed"), "выход из магазина ведёт к завершению");

// --- Сценарий 2: при репутации Гоши 400 у Руслана появляется доставка.
assert.equal(state.reputation.gosha, 400, "две покупки поднимают репутацию ровно до порога 400");

state.visited.length = 0;
run("step-delivery");
assert.equal(state.reputation.ruslan, 500 + 100, "+100 репутации Руслану за доставку");
assert.equal(state.reputation.gosha, 400 + 150, "+150 репутации Гоше за доставку");
assert.ok(state.visited.includes("condition-delivery-available"));
assert.ok(state.visited.includes("delivery-reputation-ruslan"));
assert.ok(state.visited.includes("delivery-reputation-gosha"));

// --- Сценарий 3: доставка недоступна при репутации Гоши ниже 400.
state.reputation.gosha = 399;
state.visited.length = 0;
run("step-delivery");
assert.ok(!state.visited.includes("delivery-reputation-ruslan"),
  "доставка не должна открываться ниже порога 400");
assert.ok(state.visited.includes("step-completed"));;

// --- Требование стоит именно на покупке, а выход всегда доступен.
const shop = JSON.parse(fs.readFileSync("data/scenes/gosha_shop.aqscene", "utf8")).definition;
const options = shop.choices.find(c => c.id === "gosha.shop").options;
assert.deepEqual(options.find(o => o.id === "gosha.shop.buy").requirement, { npcId: "gosha", minValue: 350 });
assert.equal(options.find(o => o.id === "gosha.shop.leave").requirement, undefined);

console.log("Покупка повторяется, 450 ₽ за штуку, +25 репутации, порог покупки 350.");
console.log("Доставка при репутации Гоши 400: +100 Руслану и +150 Гоше; ниже 400 — недоступна.");
console.log("OK");
