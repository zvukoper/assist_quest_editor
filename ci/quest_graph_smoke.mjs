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
          window.chrome = {
            webview: {
              listeners: new Map(),
              addEventListener(type, handler) { this.listeners.set(type, handler); },
              postMessage(payload) { window.__messages.push(payload); }
            }
          };
          window.__messages = [];
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
        sockets: [{ socketId: "start.out", name: "Далее", direction: "Output", flowKind: "Normal" }]
      },
      {
        nodeId: "end",
        nodeType: "End",
        title: "Завершение",
        x: 500, y: 250,
        sockets: [{ socketId: "end.in", name: "Вход", direction: "Input", flowKind: "Normal" }]
      }
    ],
    connections: [
      { fromNodeId: "start", fromSocketId: "start.out", toNodeId: "end", toSocketId: "end.in" }
    ]
  };


  await page.evaluate(graphValue => {
    window.chrome.webview.listeners.get("message")({
      data: JSON.stringify({ type: "quest_graph", graph: graphValue, canUndo: true, canRedo: true })
    });
  }, graph);

  await page.locator("#questGraphSvg .nodeTitle", { hasText: "Start" }).waitFor();
  await page.locator("#inspector input#graphEditTitle").waitFor();

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
