import fs from "node:fs";
import path from "node:path";
import { chromium } from "playwright";

const root = process.cwd();
const sceneJs = fs.readFileSync(path.join(root, "src/AssistQuestEditor.App/Web/sceneEditor.js"), "utf8");
const themeCss = fs.readFileSync(path.join(root, "src/AssistQuestEditor.App/Web/theme.css"), "utf8");

const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
const actions = [];

await page.exposeFunction("assistSend", payload => actions.push(payload));
await page.setContent(
  "<!doctype html><html><head><style>" + themeCss + "</style></head><body>" +
  "<div id='workspace'></div><div id='inspector'></div>" +
  "<script>" + sceneJs + "</script></body></html>",
  { waitUntil: "load" }
);

await page.evaluate(() => {
  window.__assistSend = payload => window.assistSend(payload);
  history.replaceState({}, "", "#scene");

  window.postMessage({ type: "scene_catalog", scenes: [{ id: "fixture", title: "Fixture Scene" }] }, "*");
  window.postMessage({
    type: "scene_definition",
    definition: {
      id: "fixture",
      title: "Fixture Scene",
      description: "",
      graph: {
        id: "fixture",
        name: "Fixture Scene",
        nodes: [
          {
            nodeId: "start",
            nodeType: "SceneStart",
            title: "Start",
            x: 0, y: 0,
            sockets: [{ socketId: "start.out", name: "Далее", direction: "Output", flowKind: "Normal" }],
            parameters: {}
          },
          {
            nodeId: "dialogue",
            nodeType: "Dialogue",
            title: "Dialogue",
            x: 420, y: 0,
            sockets: [
              { socketId: "dialogue.in", name: "Вход", direction: "Input", flowKind: "Normal" },
              { socketId: "dialogue.out", name: "Далее", direction: "Output", flowKind: "Normal" }
            ],
            parameters: { dialogueId: "fixture.dialogue" }
          },
          {
            nodeId: "end",
            nodeType: "SceneEnd",
            title: "End",
            x: 840, y: 0,
            sockets: [{ socketId: "end.in", name: "Вход", direction: "Input", flowKind: "Normal" }],
            parameters: {}
          }
        ],
        connections: [
          { fromNodeId: "start", fromSocketId: "start.out", toNodeId: "dialogue", toSocketId: "dialogue.in" }
        ]
      },
      dialogues: [{ id: "fixture.dialogue", speaker: "A", text: "B" }],
      choices: []
    },
    canUndo: false,
    canRedo: false,
    validation: [],
    documentPath: "",
    lastDocumentPath: "",
    documentDirty: false
  }, "*");
});

await page.waitForTimeout(50);
if (!await page.locator("#sceneGraphSvg").count()) throw new Error("Scene Graph canvas не создан");

await page.locator("#layoutScene").click();
if (!actions.some(action => action.action === "scene_layout")) throw new Error("Перестроить не отправил scene_layout");

await page.locator(".outputSocket").first().click();
await page.locator(".inputSocket").last().click();
if (!actions.some(action => action.action === "scene_connect")) throw new Error("Output → Input не отправил scene_connect");

await page.locator(".node[data-node-id='dialogue']").click();
await page.locator("#sceneEditTitle").fill("Dialogue 2");
await page.locator("#saveSceneNode").click();
if (!actions.some(action => action.action === "scene_update_node" && action.title === "Dialogue 2"))
  throw new Error("Изменение title не отправило scene_update_node");

const before = await page.evaluate(() => window.__assistSceneEditor.getState().viewport.width);
await page.locator("#sceneGraphSvg").hover();
await page.mouse.wheel(0, -500);
const after = await page.evaluate(() => window.__assistSceneEditor.getState().viewport.width);
if (!(after < before)) throw new Error("Wheel zoom не изменил viewport");

const beforePan = await page.evaluate(() => window.__assistSceneEditor.getState().viewport.x);
await page.locator("#sceneGraphSvg").hover();
await page.mouse.move(500, 400);
await page.mouse.down({ button: "middle" });
await page.mouse.move(560, 440);
await page.mouse.up({ button: "middle" });
const afterPan = await page.evaluate(() => window.__assistSceneEditor.getState().viewport.x);
if (afterPan === beforePan) throw new Error("Pan не изменил viewport");

await page.evaluate(() => { window.confirm = () => false; });
await page.locator(".node[data-node-id='dialogue']").click();
await page.keyboard.press("Delete");
if (actions.some(action => action.action === "scene_remove_node"))
  throw new Error("Отмена DEL всё равно удаляет ноду");

await page.evaluate(() => { window.confirm = () => true; });
await page.keyboard.press("Delete");
if (!actions.some(action => action.action === "scene_remove_node" && action.nodeId === "dialogue"))
  throw new Error("Подтверждённый DEL не отправил scene_remove_node");

await browser.close();
console.log("Scene Graph smoke: OK");