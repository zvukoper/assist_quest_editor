import fs from "node:fs";
import path from "node:path";
import { chromium } from "playwright";

const root = process.cwd();
const editorSource = fs.readFileSync(path.join(root, "src", "AssistQuestEditor.App", "Web", "editor.js"), "utf8");
const sceneSource = fs.readFileSync(path.join(root, "src", "AssistQuestEditor.App", "Web", "sceneEditor.js"), "utf8");
const dialogueSource = fs.readFileSync(path.join(root, "src", "AssistQuestEditor.App", "Web", "dialogueWorkspace.js"), "utf8");
const theme = fs.readFileSync(path.join(root, "src", "AssistQuestEditor.App", "Web", "theme.css"), "utf8");

const browser = await chromium.launch({ headless: true });
try {
  const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
  const errors = [];
  page.on("pageerror", error => errors.push(String(error)));

  await page.setContent("<!doctype html><html lang='ru'><body><header><strong id='editorTitle'></strong></header><main id='root'></main></body></html>");
  await page.addStyleTag({ content: theme });
  await page.evaluate(() => {
    window.__messages = [];
    window.__assistSend = payload => window.__messages.push(payload);
  });
  await page.addScriptTag({ content: sceneSource });
  await page.addScriptTag({ content: dialogueSource });
  await page.addScriptTag({ content: editorSource });

  const questGraph = {
    id: "tutorial_ruslan_shashlik",
    name: "Шашлычник Руслан",
    nodes: [{
      nodeId: "dialogue-scene",
      nodeType: "DialogueScene",
      title: "Разговор с Русланом",
      x: 240, y: 160,
      parameters: { sceneId: "ruslan_start" },
      sockets: [
        { socketId: "dialogue-scene.in", name: "Вход", direction: "Input", flowKind: "Normal" },
        { socketId: "dialogue-scene.out", name: "Далее", direction: "Output", flowKind: "Normal" }
      ]
    }],
    connections: []
  };

  await page.evaluate(value => {
    location.hash = "#graph";
    window.dispatchEvent(new MessageEvent("message", {
      data: { type: "scene_catalog", scenes: [{ id: "ruslan_start", title: "Разговор с Русланом" }] }
    }));
    window.dispatchEvent(new MessageEvent("message", {
      data: {
        type: "quest_graph",
        graph: value,
        selectedNodeId: "dialogue-scene",
        canUndo: false,
        canRedo: false,
        validation: [],
        documentPath: "tutorial_ruslan_shashlik.aqquest",
        lastDocumentPath: "tutorial_ruslan_shashlik.aqquest",
        documentDirty: false
      }
    }));
  }, questGraph);

  await page.locator("[data-open-reference='ruslan_start']").waitFor();
  await page.locator("#openSceneReference").waitFor();
  if (await page.locator("#openSceneReference").innerText() !== "Открыть сцену")
    throw new Error("Inspector navigation button is not visibly labelled «Открыть сцену».");
  await page.locator("[data-open-reference='ruslan_start']").click();
  const openMessage = await page.evaluate(() => window.__messages.find(item => item.action === "graph_open_scene"));
  if (!openMessage || openMessage.nodeId !== "dialogue-scene" || openMessage.sceneId !== "ruslan_start")
    throw new Error("Quest node -> Scene navigation sent incorrect payload.");

  const sceneDefinition = {
    id: "ruslan_start",
    title: "Разговор с Русланом",
    description: "",
    graph: {
      id: "ruslan_start",
      name: "Разговор с Русланом",
      nodes: [{ nodeId: "choice", nodeType: "Choice", title: "Руслан предлагает работу", x: 640, y: 180,
        parameters: { choiceId: "ruslan.offer" },
        sockets: [
          { socketId: "choice.in", name: "Вход", direction: "Input", flowKind: "Normal" },
          { socketId: "choice.accept", name: "Принять", direction: "Output", flowKind: "Normal" }
        ] }],
      connections: []
    },
    dialogues: [],
    choices: []
  };

  await page.evaluate(value => {
    location.hash = "#scene";
    window.dispatchEvent(new MessageEvent("message", {
      data: {
        type: "scene_definition",
        definition: value,
        selectedNodeId: "choice",
        canUndo: true,
        canRedo: false,
        validation: [],
        documentPath: "data/scenes/ruslan_start.aqscene",
        lastDocumentPath: "data/scenes/ruslan_start.aqscene",
        documentDirty: false,
        navigationBack: {
          nodeId: "dialogue-scene",
          nodeTitle: "Разговор с Русланом",
          questId: "tutorial_ruslan_shashlik",
          questPath: "data/quests/tutorial_ruslan_shashlik.aqquest"
        }
      }
    }));
  }, sceneDefinition);

  await page.getByRole("button", { name: /Вернуться в Quest Graph/ }).waitFor();
  await page.evaluate(() => { location.hash = "#dialogue"; });
  await page.waitForTimeout(20);
  await page.getByRole("button", { name: /Вернуться в Quest Graph/ }).waitFor();
  await page.getByRole("button", { name: /Вернуться в Quest Graph/ }).click();

  const backMessage = await page.evaluate(() => window.__messages.filter(item => item.action === "navigate_to_quest_node").pop());
  if (!backMessage ||
      backMessage.nodeId !== "dialogue-scene" ||
      backMessage.questId !== "tutorial_ruslan_shashlik" ||
      backMessage.questPath !== "data/quests/tutorial_ruslan_shashlik.aqquest")
    throw new Error("Scene/Dialogue -> Quest node navigation lost source Quest document context.");
  if (errors.length) throw new Error("Resource navigation smoke pageerror: " + errors.join(" | "));

  console.log("Resource navigation smoke: OK");
} finally {
  await browser.close();
}
