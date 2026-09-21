import fs from "node:fs";
import path from "node:path";
import { chromium } from "playwright";

// Проверка кнопки «Перестроить» в нодовом редакторе.
//
// Кнопка не считает раскладку сама: она отправляет Host-действие `graph_layout`,
// а координаты приходят обратно в обновлённом `quest_graph`. Здесь проверяется
// именно эта проводка, потому что сам алгоритм раскладки покрыт .NET-тестами
// (AssertNoOverlaps в QuestGraphStoreTests).
//
// Граф намеренно шире стартового viewBox (1200), иначе проверка «вид подогнан
// под результат» проходила бы даже без вызова fitGraph.

const root = process.cwd();
const source = fs.readFileSync(
  path.join(root, "src", "AssistQuestEditor.App", "Web", "editor.js"),
  "utf8"
);

const theme = fs.readFileSync(
  path.join(root, "src", "AssistQuestEditor.App", "Web", "theme.css"),
  "utf8"
);

const failures = [];
const check = (condition, message) => {
  if (!condition) failures.push(message);
};

const browser = await chromium.launch({ headless: true });
try {
  const page = await browser.newPage();
  const pageErrors = [];
  page.on("pageerror", error => pageErrors.push(String(error)));

  await page.setContent(`
    <!doctype html>
    <html lang="ru">
      <body>
        <header><strong id="editorTitle"></strong></header>
        <main id="root"></main>
      </body>
    </html>
  `);

  await page.evaluate(() => {
    window.__messages = [];
    window.__assistSend = payload => window.__messages.push(payload);
  });

  await page.addStyleTag({ content: theme });
  await page.addScriptTag({ content: source });

  const node = (nodeId, nodeType, x, y) => ({
    nodeId,
    nodeType,
    title: nodeId,
    x,
    y,
    parameters: {},
    sockets: [
      { socketId: `${nodeId}.in`, name: "Вход", direction: "Input", flowKind: "Normal" },
      { socketId: `${nodeId}.out`, name: "Далее", direction: "Output", flowKind: "Normal" }
    ]
  });

  const NODE_COUNT = 8;
  const STEP = 400;
  const ids = Array.from({ length: NODE_COUNT }, (_, index) =>
    index === 0 ? "start" : index === NODE_COUNT - 1 ? "end" : "n" + index);

  const chain = ids.slice(0, -1).map((id, index) => ({
    fromNodeId: id,
    fromSocketId: `${id}.out`,
    toNodeId: ids[index + 1],
    toSocketId: `${ids[index + 1]}.in`
  }));

  // Все ноды в одной точке: именно так выглядят графы, созданные «вслепую».
  const overlapped = {
    id: "blind",
    name: "Граф без координат",
    nodes: ids.map(id =>
      node(id, id === "start" ? "Start" : id === "end" ? "End" : "Phase", 0, 0)),
    connections: chain
  };

  // Ожидаемый результат раскладки: цепочка слоёв с ровным шагом.
  const laidOut = {
    ...overlapped,
    nodes: ids.map((id, index) =>
      node(id, id === "start" ? "Start" : id === "end" ? "End" : "Phase", index * STEP, 0))
  };

  const dispatchGraph = graphValue => page.evaluate(value => {
    window.dispatchEvent(new MessageEvent("message", {
      data: JSON.stringify({ type: "quest_graph", graph: value, documentDirty: true })
    }));
  }, graphValue);

  await dispatchGraph(overlapped);
  await page.locator("#questGraphSvg .nodeTitle", { hasText: "start" }).waitFor();

  // Кнопка существует и видима.
  const button = page.locator("#layoutGraph");
  check(await button.count() === 1, "Кнопка «Перестроить» не найдена в панели редактора нод.");
  check(await button.isVisible(), "Кнопка «Перестроить» не видима.");

  await page.evaluate(() => { window.__messages.length = 0; });
  await button.click();
  await page.waitForTimeout(50);

  const messages = await page.evaluate(() => window.__messages);
  check(
    messages.some(message => message.action === "graph_layout"),
    "Нажатие «Перестроить» должно отправлять действие graph_layout: " +
      JSON.stringify(messages)
  );

  // Host отвечает перестроенным графом.
  await dispatchGraph(laidOut);
  await page.waitForTimeout(50);

  const transforms = await page.evaluate(() => {
    const result = {};
    document.querySelectorAll("#questGraphSvg .node").forEach(element => {
      result[element.dataset.nodeId] = element.getAttribute("transform");
    });
    return result;
  });

  // Каждая нода должна встать на координаты от Host.
  ids.forEach((id, index) => {
    const expected = "translate(" + (index * STEP) + " 0)";
    check(
      transforms[id] === expected,
      `Нода «${id}» ожидалась на ${expected}, получено ${transforms[id]}.`
    );
  });

  // Ноды не должны пересекаться в DOM после перестроения.
  const overlaps = await page.evaluate(() => {
    const rects = [];
    document.querySelectorAll("#questGraphSvg .node").forEach(element => {
      const rect = element.querySelector(".nodeRect");
      if (!rect) return;
      const match = element.getAttribute("transform").match(/translate\(([-\d.]+)\s([-\d.]+)\)/);
      rects.push({
        id: element.dataset.nodeId,
        x: Number(match[1]),
        y: Number(match[2]),
        w: Number(rect.getAttribute("width")),
        h: Number(rect.getAttribute("height"))
      });
    });

    const hits = [];
    for (let a = 0; a < rects.length; a++) {
      for (let b = a + 1; b < rects.length; b++) {
        const left = rects[a], right = rects[b];
        const separated =
          left.x + left.w <= right.x + 0.001 ||
          right.x + right.w <= left.x + 0.001 ||
          left.y + left.h <= right.y + 0.001 ||
          right.y + right.h <= left.y + 0.001;
        if (!separated) hits.push(left.id + "/" + right.id);
      }
    }
    return hits;
  });

  check(
    overlaps.length === 0,
    "Ноды пересекаются после перестроения: " + overlaps.join(", ")
  );

  // ViewBox должен вмещать весь перестроенный граф. Граф шире стартового
  // viewBox (1200), поэтому проверка ловит отсутствие fitGraph.
  const viewBox = await page.evaluate(() => {
    const box = document.getElementById("questGraphSvg").viewBox.baseVal;
    return { x: box.x, y: box.y, width: box.width, height: box.height };
  });

  const contentWidth = (NODE_COUNT - 1) * STEP + 260;
  check(
    viewBox.width >= contentWidth,
    `ViewBox (${viewBox.width}) не вмещает перестроенный граф (${contentWidth}).`
  );
  check(
    viewBox.x <= 0 && viewBox.y <= 0,
    "ViewBox не должен отсекать начало графа: " + JSON.stringify(viewBox)
  );

  check(pageErrors.length === 0, "pageerror при перестроении: " + pageErrors.join(" | "));
} finally {
  await browser.close();
}

if (failures.length) {
  console.error("Graph layout smoke: FAIL");
  for (const failure of failures) console.error("- " + failure);
  process.exit(1);
}

console.log("Graph layout smoke: OK");
