import fs from "node:fs";
import path from "node:path";
import { openBrowser, closeBrowser } from "./lib/browser.mjs";

// Проверка концепции левого сайдбара: он выбирает содержимое рабочей области
// ТОГО ЖЕ окна, а не открывает второе окно (как File Editor в WolvenKit).

const root = process.cwd();
const read = rel => fs.readFileSync(path.join(root, ...rel.split("/")), "utf8");
const editorJs = read("src/AssistQuestEditor.App/Web/editor.js");
const sceneJs = read("src/AssistQuestEditor.App/Web/sceneEditor.js");
const dialogueJs = read("src/AssistQuestEditor.App/Web/dialogueWorkspace.js");
const locationJs = read("src/AssistQuestEditor.App/Web/locationEditor.js");
const theme = read("src/AssistQuestEditor.App/Web/theme.css");

const { browser } = await openBrowser();
try {
  const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
  const errors = [];
  page.on("pageerror", e => errors.push(String(e)));

  await page.setContent("<!doctype html><html lang='ru'><body><header><strong id='editorTitle'></strong></header><main id='root'></main></body></html>");
  await page.addStyleTag({ content: theme });
  await page.evaluate(() => {
    window.__messages = [];
    window.__assistSend = payload => window.__messages.push(payload);
  });
  await page.addScriptTag({ content: sceneJs });
  await page.addScriptTag({ content: dialogueJs });
  await page.addScriptTag({ content: locationJs });
  await page.addScriptTag({ content: editorJs });

  // Клик по «Редактор сцен» в сайдбаре должен дать activate_pane, а НЕ open_editor.
  await page.evaluate(() => { location.hash = "#graph"; });
  await page.evaluate(() => {
    window.dispatchEvent(new MessageEvent("message", {
      data: { type: "quest_graph", graph: { id: "q", name: "Q", nodes: [], connections: [] },
        selectedNodeId: null, canUndo: false, canRedo: false, validation: [],
        documentPath: "", lastDocumentPath: "", documentDirty: false }
    }));
  });

  await page.locator(".navButton[data-id='scene']").click();
  const sent = await page.evaluate(() => window.__messages);
  const paneMsg = sent.find(m => m.action === "activate_pane");
  if (!paneMsg || paneMsg.pane !== "scene")
    throw new Error("Сайдбар не отправил activate_pane(scene): " + JSON.stringify(sent));
  if (sent.some(m => m.action === "open_editor"))
    throw new Error("Сайдбар открыл второе окно через open_editor вместо переключения панели.");

  // Host сообщает активную панель — рабочая область обязана перерисоваться.
  await page.evaluate(() => {
    window.dispatchEvent(new MessageEvent("message", { data: { type: "active_pane", pane: "scene" } }));
  });
  if (await page.evaluate(() => location.hash) !== "#scene")
    throw new Error("active_pane не переключил hash.");

  // Информационная панель (каналы) не требует Host-payload: переключается локально.
  await page.evaluate(() => { window.__messages.length = 0; });
  await page.locator(".navButton[data-id='channels']").click();
  const afterChannels = await page.evaluate(() => ({ hash: location.hash, msgs: window.__messages }));
  if (afterChannels.hash !== "#channels")
    throw new Error("Информационная панель не переключилась локально.");
  if (afterChannels.msgs.length)
    throw new Error("Информационная панель не должна слать Host-сообщений: " + JSON.stringify(afterChannels.msgs));

  if (errors.length) throw new Error("pageerror: " + errors.join(" | "));
  console.log("Sidebar pane switch smoke: OK");
} finally {
  await closeBrowser(browser);
}
