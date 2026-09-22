// Проверка Campaign Definition и согласованности Quest versions.
import fs from "node:fs";
import path from "node:path";

const root = "data/campaigns";
const files = findFiles(root, file => path.basename(file).toLowerCase() === "campaign.aqcampaign");
if (!files.length) throw new Error("В data/campaigns нет campaign.aqcampaign.");

let ok = true;

for (const file of files) {
  const doc = JSON.parse(fs.readFileSync(file, "utf8"));
  const definition = doc.definition;
  const problems = [];

  if (doc.schemaVersion !== 1 || doc.format !== "aqcampaign")
    problems.push("unsupported campaign document format");
  if (!definition?.id || !definition?.name)
    problems.push("missing campaign id/name");
  if (!(Number(definition?.version) >= 1))
    problems.push("invalid campaign version");

  const campaignRoot = path.dirname(file);
  const questIds = new Set();
  const orders = new Map();

  for (const entry of definition?.quests || []) {
    if (questIds.has(entry.questId))
      problems.push("duplicate questId " + entry.questId);
    questIds.add(entry.questId);

    const questPath = path.join(campaignRoot, entry.relativePath);
    if (!fs.existsSync(questPath)) {
      problems.push("missing quest " + entry.relativePath);
      continue;
    }

    const quest = JSON.parse(fs.readFileSync(questPath, "utf8"));
    const actualVersion = Number(quest.definition?.version || 1);
    if (actualVersion !== Number(entry.version))
      problems.push("version mismatch " + entry.questId + ": campaign=" + entry.version + ", file=" + actualVersion);

    if (!["Enabled", "Disabled"].includes(entry.status))
      problems.push("invalid quest status " + entry.questId);

    // Порядковый номер задаёт сортировку квестов в Simulator и в окне
    // кампаний, поэтому он должен быть задан явно и не повторяться внутри
    // кампании: при совпадении сортировка уходила бы в алфавит по файлу.
    if (!Number.isInteger(entry.order) || entry.order < 1)
      problems.push("invalid quest order " + entry.questId + ": " + entry.order);
    else if (orders.has(entry.order))
      problems.push("duplicate quest order " + entry.order +
        " (" + orders.get(entry.order) + " и " + entry.questId + ")");
    else
      orders.set(entry.order, entry.questId);

    const nodes = quest.definition?.graph?.nodes;
    if (!Array.isArray(nodes))
      problems.push("missing graph nodes " + entry.questId);
    else {
      for (const node of nodes) {
        if (!node?.nodeId || !node?.nodeType)
          problems.push("non-canonical node identity " + entry.questId +
            ": expected nodeId/nodeType");
      }
    }
  }

  for (const relative of definition?.files || []) {
    if (relative === "campaign.aqcampaign") continue;
    if (!fs.existsSync(path.join(campaignRoot, relative)))
      problems.push("missing declared file " + relative);
  }

  // Свойства мира: гео-координата нужна астрономии, и её порча (0,0) не видна ни
  // сборке, ни приложению — мир просто теряет своё место на Земле. Случай уже был:
  // пустое поле в интерфейсе сохранялось как 0. Ноль допустим географически, но
  // для кампании это признак потерянных данных, поэтому отвергается явно.
  const geo = definition?.geo;
  if (geo === undefined || geo === null) {
    problems.push("missing world geo coordinate");
  } else {
    const { latitude, longitude } = geo;

    // Вычисляемое свойство не должно попадать в файл.
    if (Object.prototype.hasOwnProperty.call(geo, "isValid"))
      problems.push("computed isValid leaked into campaign geo");

    for (const [name, value, limit] of [["latitude", latitude, 90], ["longitude", longitude, 180]]) {
      if (typeof value !== "number" || !Number.isFinite(value))
        problems.push("invalid geo " + name + ": " + value);
      else if (Math.abs(value) > limit)
        problems.push("geo " + name + " out of range: " + value);
      else if (value === 0)
        problems.push("geo " + name + " is zero: world lost its position (was it an empty input field?)");
    }
  }

  if (definition?.startDate !== undefined && definition?.startDate !== null &&
      Number.isNaN(Date.parse(definition.startDate))) {
    problems.push("invalid world startDate: " + definition.startDate);
  }

  console.log(path.basename(path.dirname(file)) + ": " + (problems.length ? "FAIL" : "OK") +
    " quests=" + (definition?.quests?.length || 0) +
    " version=" + definition?.version);

  if (problems.length) {
    ok = false;
    for (const problem of problems) console.log(" - " + problem);
  }
}

if (!ok) process.exit(1);
console.log("All Campaign resources OK");

function findFiles(dir, predicate) {
  if (!fs.existsSync(dir)) return [];
  const result = [];
  const walk = current => {
    for (const entry of fs.readdirSync(current, { withFileTypes: true })) {
      const full = path.join(current, entry.name);
      if (entry.isDirectory()) walk(full);
      else if (predicate(full)) result.push(full);
    }
  };
  walk(dir);
  return result.sort();
}
