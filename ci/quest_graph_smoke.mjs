import fs from "node:fs";
import path from "node:path";
import { chromium } from "playwright";

const root = process.cwd();
const source = fs.readFileSync(
  path.join(root, "src", "AssistQuestEditor.App", "Web", "editor.js"),
  "utf8"
);

const browser = await chromium.launch({ headless: true });
try {
  const page = await browser.newPage();
  const messages = [];
  const errors = [];

  page.on("pageerror", error => errors.push(String(error)));

  await page.setContent(`
    <!doctype html>
    <html lang="ru">
      <body>
        <header><strong id="editorTitle"></strong></header>
        <main id="root"></main>
        <script>
          window.__messages = [];
          window.__messageHandler = null;
          window.__assistWebview = {
            addEventListener(type, handler) {
              if (type === "message") window.__messageHandler = handler;
            },
            postMessage(payload) { window.__messages.push(payload); }
          };
        </script>
        <script>
          ${source.replaceAll("</script", "<\\/script")}
        </script>
      </body>
    </html>
  `);

  const graph = {
    id: "special_marinated_shashlik",
    name: "Спецмаринад для Руслана",
    nodes: [
      {
        nodeId: "start",
        nodeType: "Start",
        title: "Начало квеста",
        x: 80, y: 250,
        parameters: {},
        sockets: [{ socketId: "start.out", name: "Далее", direction: "Output", flowKind: "Normal" }]
      },
      {
        nodeId: "choice",
        nodeType: "Choice",
        title: "Выбор",
        x: 300, y: 220,
        parameters: { outputCount: "3" },
        sockets: [
          { socketId: "choice.in", name: "Вход", direction: "Input", flowKind: "Normal" },
          { socketId: "choice.choice1", name: "Выбор 1", direction: "Output", flowKind: "Normal" },
          { socketId: "choice.choice2", name: "Выбор 2", direction: "Output", flowKind: "Normal" },
          { socketId: "choice.choice3", name: "Выбор 3", direction: "Output", flowKind: "Normal" }
        ]
      },
      {
        nodeId: "end",
        nodeType: "End",
        title: "Завершение",
        x: 600, y: 220,
        parameters: {},
        sockets: [{ socketId: "end.in", name: "Вход", direction: "Input", flowKind: "Normal" }]
      }
    ],
    connections: [
      { fromNodeId: "start", fromSocketId: "start.out", toNodeId: "choice", toSocketId: "choice.in" },
      { fromNodeId: "choice", fromSocketId: "choice.choice1", toNodeId: "end", toSocketId: "end.in" }
    ]
  };


  await page.evaluate(graphValue => {
    window.__messageHandler({
      data: JSON.stringify({ type: "quest_graph", graph: graphValue, canUndo: true, canRedo: true })
    });
  }, graph);

  await page.locator("#questGraphSvg .nodeTitle", { hasText: "Start" }).waitFor();
  await page.locator("#inspector input#graphEditTitle").waitFor();

  await page.locator("[data-node-id='choice']").click();
  const parameterKeys = page.locator("[data-param-key]");
  await parameterKeys.first().waitFor();
  const outputCountKey = await parameterKeys.evaluateAll(nodes =>
    nodes.map(node => node.value).find(value => value === "outputCount")
  );
  if (outputCountKey !== "outputCount") {
    throw new Error("Параметр outputCount не отображается в инспекторе.");
  }
  const parameterValues = page.locator("[data-param-value]");
  await parameterValues.first().fill("4");
  await page.getByRole("button", { name: "Сохранить свойства" }).click();
  await page.waitForTimeout(20);

  const parameterUpdate = await page.evaluate(() =>
    window.__messages.slice().reverse().find(message =>
      message.action === "graph_update_node" &&
      message.nodeId === "choice"
    )
  );
  if (!parameterUpdate || parameterUpdate.parameters?.outputCount !== "4") {
    throw new Error("Редактор ноды не отправил параметры outputCount.");
  }

  await page.locator("#graphNodeType").selectOption("Phase");
  await page.locator("#graphNodeTitle").fill("Новая фаза");
  await page.getByRole("button", { name: "Добавить ноду" }).click();

  await page.waitForTimeout(20);

  const added = await page.evaluate(() =>
    window.__messages.find(message => message.action === "graph_add_node")
  );

  if (!added || added.nodeType !== "Phase" || added.title !== "Новая фаза") {
    throw new Error("UI не отправил корректную команду graph_add_node.");
  }

  await page.locator("[data-node-id='start']").click();
  await page.locator("#graphEditTitle").fill("Старт обновлён");
  await page.getByRole("button", { name: "Сохранить свойства" }).click();

  await page.waitForTimeout(20);
  await page.getByRole("button", { name: "Проверить" }).click();

  await page.waitForTimeout(20);

  const validation = await page.evaluate(() =>
    window.__messages.find(message => message.action === "graph_validate")
  );

  if (!validation) {
    throw new Error("UI не отправил корректную команду graph_validate.");
  }


  const update = await page.evaluate(() =>
    window.__messages.find(message => message.action === "graph_update_node")
  );

  if (!update || update.nodeId !== "start" || update.title !== "Старт обновлён") {
    throw new Error("UI не отправил корректную команду graph_update_node.");
  }

  const beforeDragMessages = await page.evaluate(() => window.__messages.length);
  const startBox = await page.locator("[data-node-id='start']").boundingBox();

  if (!startBox) {
    throw new Error("Не удалось получить границы ноды start для drag-теста.");
  }

  await page.mouse.move(startBox.x + startBox.width / 2, startBox.y + startBox.height / 2);
  await page.mouse.down();
  await page.mouse.move(startBox.x + startBox.width / 2 + 100, startBox.y + startBox.height / 2 + 60);
  await page.mouse.up();
  await page.waitForTimeout(20);

  const dragUpdate = await page.evaluate(startIndex =>
    window.__messages.slice(startIndex).find(message =>
      message.action === "graph_update_node" &&
      message.nodeId === "start" &&
      message.x !== 80 &&
      message.y !== 250
    ),
    beforeDragMessages
  );

  if (!dragUpdate) {
    throw new Error("Drag ноды не отправил изменённые координаты в graph_update_node.");
  }

  const svgBox = await page.locator("#questGraphSvg").boundingBox();
  if (!svgBox) {
    throw new Error("Не удалось получить границы Quest Graph canvas.");
  }

  const viewBoxBeforePan = await page.locator("#questGraphSvg").getAttribute("viewBox");
  await page.mouse.move(svgBox.x + svgBox.width / 2, svgBox.y + svgBox.height / 2);
  await page.mouse.down({ button: "middle" });
  await page.mouse.move(svgBox.x + svgBox.width / 2 + 120, svgBox.y + svgBox.height / 2 + 80);
  await page.mouse.up({ button: "middle" });
  await page.waitForTimeout(20);
  const viewBoxAfterPan = await page.locator("#questGraphSvg").getAttribute("viewBox");
  const panBefore = viewBoxBeforePan.split(/\s+/).map(Number);
  const panAfter = viewBoxAfterPan.split(/\s+/).map(Number);
  if (panBefore.length !== 4 || panAfter.length !== 4 ||
      (panAfter[0] === panBefore[0] && panAfter[1] === panBefore[1])) {
    throw new Error("Pan средней кнопкой не изменил viewport.");
  }

  await page.getByRole("button", { name: "Новый" }).click();
  if (!await page.evaluate(() => window.__messages.some(message => message.action === "graph_new"))) {
    throw new Error("UI не отправил graph_new.");
  }

  await page.mouse.move(svgBox.x + svgBox.width / 2, svgBox.y + svgBox.height / 2);
  const viewBoxBeforeZoom = await page.locator("#questGraphSvg").getAttribute("viewBox");
  await page.mouse.wheel(0, -500);
  await page.waitForTimeout(20);
  const viewBoxAfterZoom = await page.locator("#questGraphSvg").getAttribute("viewBox");

  const beforeParts = viewBoxBeforeZoom.split(/\s+/).map(Number);
  const afterParts = viewBoxAfterZoom.split(/\s+/).map(Number);

  if (
    beforeParts.length !== 4 ||
    afterParts.length !== 4 ||
    !(afterParts[2] < beforeParts[2]) ||
    !(afterParts[3] < beforeParts[3])
  ) {
    throw new Error("Колесо мыши не изменило масштаб Quest Graph.");
  }

  await page.getByRole("button", { name: "Отменить" }).click();
  await page.getByRole("button", { name: "Повторить" }).click();

  await page.waitForTimeout(20);

  const undo = await page.evaluate(() =>
    window.__messages.find(message => message.action === "graph_undo")
  );
  const redo = await page.evaluate(() =>
    window.__messages.find(message => message.action === "graph_redo")
  );

  if (!undo || !redo) {
    throw new Error("UI не отправил корректные команды graph_undo/graph_redo.");
  }

  if (errors.length) {
    throw new Error("Quest Graph UI вызвал pageerror: " + errors.join(" | "));
  }

  console.log("Quest Graph Playwright smoke: OK");
} finally {
  await browser.close();
}
