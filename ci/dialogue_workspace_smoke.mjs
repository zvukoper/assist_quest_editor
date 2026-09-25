import fs from "node:fs";
import path from "node:path";
import { openBrowser, closeBrowser } from "./lib/browser.mjs";

const root = process.cwd();
const editorSource = fs.readFileSync(
  path.join(root, "src", "AssistQuestEditor.App", "Web", "editor.js"),
  "utf8"
);
const sceneSource = fs.readFileSync(
  path.join(root, "src", "AssistQuestEditor.App", "Web", "sceneEditor.js"),
  "utf8"
);
const dialogueSource = fs.readFileSync(
  path.join(root, "src", "AssistQuestEditor.App", "Web", "dialogueWorkspace.js"),
  "utf8"
);
const theme = fs.readFileSync(
  path.join(root, "src", "AssistQuestEditor.App", "Web", "theme.css"),
  "utf8"
);

const { browser } = await openBrowser();
try {
  const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
  const errors = [];
  page.on("pageerror", error => errors.push(String(error)));

  // editor.js на старте требует #root и #editorTitle: он строит layoutThree
  // (nav + #workspace + #inspector) внутрь #root. Без них скрипт падал на
  // root.innerHTML, поэтому #workspace не создавался и таб #dialogue
  // оставался пустым.
  await page.setContent(
    "<!doctype html><html lang='ru'><head><style>" +
    "html,body{height:100%;margin:0;overflow:hidden}" +
    "#root{height:100%;min-height:0}" +
    "</style></head><body>" +
    "<header><strong id='editorTitle'></strong></header>" +
    "<main id='root'></main>" +
    "</body></html>"
  );
  await page.addStyleTag({ content: theme });

  await page.evaluate(() => {
    window.__messages = [];
    window.__assistSend = payload => window.__messages.push(payload);
  });

  await page.addScriptTag({ content: sceneSource });
  await page.addScriptTag({ content: dialogueSource });
  await page.addScriptTag({ content: editorSource });

  const definition = {
    id: "dialogue_fixture",
    title: "Dialogue Fixture",
    description: "",
    graph: {
      id: "dialogue_fixture",
      name: "Dialogue Fixture",
      nodes: [
        {
          nodeId: "start",
          nodeType: "SceneStart",
          title: "Начало",
          x: 60, y: 220,
          parameters: {},
          sockets: [
            { socketId: "start.out", name: "Далее", direction: "Output", flowKind: "Normal" }
          ]
        },
        {
          nodeId: "dialogue",
          nodeType: "Dialogue",
          title: "Первая реплика",
          x: 360, y: 220,
          parameters: { dialogueId: "fixture.dialogue" },
          sockets: [
            { socketId: "dialogue.in", name: "Вход", direction: "Input", flowKind: "Normal" },
            { socketId: "dialogue.out", name: "Далее", direction: "Output", flowKind: "Normal" }
          ]
        },
        {
          nodeId: "choice",
          nodeType: "Choice",
          title: "Вопрос",
          x: 660, y: 220,
          parameters: { choiceId: "fixture.choice", outputCount: "2" },
          sockets: [
            { socketId: "choice.in", name: "Вход", direction: "Input", flowKind: "Normal" },
            { socketId: "choice.accept", name: "Вариант 1", direction: "Output", flowKind: "Normal" },
            { socketId: "choice.decline", name: "Вариант 2", direction: "Output", flowKind: "Normal" }
          ]
        },
        {
          nodeId: "end1",
          nodeType: "SceneEnd",
          title: "Конец 1",
          x: 980, y: 160,
          parameters: {},
          sockets: [
            { socketId: "end1.in", name: "Вход", direction: "Input", flowKind: "Normal" }
          ]
        },
        {
          nodeId: "end2",
          nodeType: "SceneEnd",
          title: "Конец 2",
          x: 980, y: 300,
          parameters: {},
          sockets: [
            { socketId: "end2.in", name: "Вход", direction: "Input", flowKind: "Normal" }
          ]
        }
      ],
      connections: [
        { fromNodeId: "start", fromSocketId: "start.out", toNodeId: "dialogue", toSocketId: "dialogue.in" },
        { fromNodeId: "dialogue", fromSocketId: "dialogue.out", toNodeId: "choice", toSocketId: "choice.in" },
        { fromNodeId: "choice", fromSocketId: "choice.accept", toNodeId: "end1", toSocketId: "end1.in" },
        { fromNodeId: "choice", fromSocketId: "choice.decline", toNodeId: "end2", toSocketId: "end2.in" }
      ]
    },
    dialogues: [
      { id: "fixture.dialogue", speaker: "Руслан", text: "Исходный текст реплики." },
      { id: "other.dialogue", speaker: "Гоша", text: "Вторая реплика." }
    ],
    choices: [
      {
        id: "fixture.choice",
        title: "Предложение",
        speaker: "Руслан",
        text: "Готов взять мясо?",
        options: [
          { id: "accept", text: "Да, беру.", outputSocketId: "choice.accept" },
          { id: "decline", text: "Нет, не беру.", outputSocketId: "choice.decline" }
        ]
      }
    ]
  };

  await page.evaluate(value => {
    location.hash = "#dialogue";
    window.dispatchEvent(new MessageEvent("message", {
      data: {
        type: "scene_catalog",
        scenes: [
          { id: "dialogue_fixture", title: "Dialogue Fixture" },
          { id: "another_scene", title: "Another Scene" }
        ]
      }
    }));
    window.dispatchEvent(new MessageEvent("message", {
      data: {
        type: "scene_definition",
        definition: value,
        selectedNodeId: "start",
        canUndo: true,
        canRedo: false,
        validation: [],
        documentPath: "fixture.aqscene",
        lastDocumentPath: "fixture.aqscene",
        documentDirty: false
      }
    }));
  }, definition);

  await page.locator("#dialogueSearch").waitFor();
  if (errors.length) throw new Error("Dialogue Workspace init pageerror: " + errors.join(" | "));

  if (await page.locator(".dialogueResourceItem").count() !== 2)
    throw new Error("Dialogue Workspace не показал оба Dialogue resource.");

  await page.locator("[data-resource-kind='dialogue'][data-resource-id='fixture.dialogue']").click();
  await page.locator("#dialogueEditSpeaker").waitFor();

  if (await page.locator("#dialogueEditSpeaker").inputValue() !== "Руслан")
    throw new Error("Speaker Dialogue resource загружен неверно.");
  if (await page.locator("#dialogueEditText").inputValue() !== "Исходный текст реплики.")
    throw new Error("Text Dialogue resource загружен неверно.");

  await page.locator("#dialogueEditSpeaker").fill("Новый Руслан");
  await page.locator("#dialogueEditText").fill("Новый текст реплики.");
  await page.getByRole("button", { name: "Сохранить реплику" }).click();

  const dialogueUpdate = await page.evaluate(() =>
    window.__messages.find(message =>
      message.action === "scene_update_dialogue" &&
      message.dialogueId === "fixture.dialogue"
    )
  );
  if (!dialogueUpdate ||
      dialogueUpdate.speaker !== "Новый Руслан" ||
      dialogueUpdate.text !== "Новый текст реплики.") {
    throw new Error("Dialogue Workspace не отправил корректный scene_update_dialogue.");
  }

  // Search filters by content but keeps stable resource IDs.
  await page.locator("#dialogueSearch").fill("Вторая");
  if (await page.locator(".dialogueResourceItem").count() !== 1)
    throw new Error("Поиск Dialogue Workspace не отфильтровал список.");

  // Строка ресурса содержит ещё и бейдж связи (linked/unlinked), который
  // отрисовывается в верхнем регистре через text-transform, поэтому innerText
  // всей строки не совпадёт с текстом ресурса. Проверяются компоненты.
  const searchResult = page.locator(".dialogueResourceItem");
  if (await searchResult.locator("strong").innerText() !== "other.dialogue")
    throw new Error("Поиск Dialogue Workspace потерял стабильный resource ID.");
  if (await searchResult.locator(".dialogueResourceSpeaker").innerText() !== "Гоша")
    throw new Error("Результат поиска Dialogue Workspace имеет неверный Speaker.");
  if (await searchResult.locator(".dialogueResourcePreview").innerText() !== "Вторая реплика.")
    throw new Error("Результат поиска Dialogue Workspace имеет неверный текст.");
  await page.locator("#dialogueSearch").fill("");

  // Switch to Choice.
  await page.getByRole("button", { name: /Выборы \(1\)/ }).click();
  await page.locator("#choiceEditTitle").waitFor();
  if (await page.locator("#choiceEditSpeaker").inputValue() !== "Руслан")
    throw new Error("Choice Speaker загружен неверно.");
  if (await page.locator("[data-choice-option-row]").count() !== 2)
    throw new Error("Choice options не загружены.");

  await page.locator("#choiceEditTitle").fill("Обновлённое предложение");
  await page.locator("#choiceEditText").fill("Обновлённый вопрос.");
  await page.locator("[data-choice-option-row='accept'] [data-choice-option-text]").fill("Беру.");
  await page.getByRole("button", { name: "Сохранить выбор" }).click();

  const choiceUpdate = await page.evaluate(() =>
    window.__messages.find(message =>
      message.action === "scene_update_choice" &&
      message.choiceId === "fixture.choice"
    )
  );
  if (!choiceUpdate ||
      choiceUpdate.title !== "Обновлённое предложение" ||
      choiceUpdate.text !== "Обновлённый вопрос." ||
      choiceUpdate.options?.[0]?.id !== "accept" ||
      choiceUpdate.options?.[0]?.text !== "Беру.") {
    throw new Error("Choice Workspace не отправил корректный scene_update_choice.");
  }

  await page.getByRole("button", { name: "+ Добавить вариант" }).click();
  const addOption = await page.evaluate(() =>
    window.__messages.find(message =>
      message.action === "scene_add_choice_option" &&
      message.nodeId === "choice" &&
      message.choiceId === "fixture.choice"
    )
  );
  if (!addOption)
    throw new Error("Choice Workspace не отправил scene_add_choice_option.");

  // Open in Scene Graph and retain the selected canonical node.
  await page.getByRole("button", { name: "Открыть в Graph" }).click();
  await page.waitForTimeout(20);
  if (await page.evaluate(() => location.hash).then(hash => hash.toLowerCase()) !== "#scene")
    throw new Error("Переход из Dialogue Workspace в Scene Graph не сработал.");
  if (await page.evaluate(() => window.__assistSceneEditor.getState().selectedNodeId) !== "choice")
    throw new Error("Переход в Scene Graph не выбрал связанную Choice node.");
  if (!await page.locator("#sceneGraphSvg").count())
    throw new Error("После перехода в Scene Graph канвас не отрисован.");

  // Return to Dialogue Workspace and verify resource list remains available.
  await page.evaluate(() => { location.hash = "#dialogue"; });
  await page.waitForTimeout(20);
  await page.locator("#dialogueSearch").waitFor();
  if (await page.locator("[data-resource-kind='choice'][data-resource-id='fixture.choice']").count() !== 1)
    throw new Error("После возврата Choice resource потерян из workspace.");

  // New Dialogue action and safe delete action.
  await page.getByRole("button", { name: "+ Новый диалог" }).click();
  if (!await page.evaluate(() =>
    window.__messages.some(message => message.action === "scene_add_dialogue")
  )) {
    throw new Error("Кнопка '+ Новый диалог' не отправила scene_add_dialogue.");
  }

  // Workspace помнит выбранную вкладку (сейчас «Выборы»), поэтому до выбора
  // Dialogue resource нужно вернуться на вкладку «Диалоги».
  await page.getByRole("button", { name: /Диалоги \(/ }).click();
  await page.locator("[data-resource-kind='dialogue'][data-resource-id='fixture.dialogue']").click();
  // Удаление подтверждается через window.confirm: без обработчика диалога
  // click зависает до таймаута.
  page.once("dialog", dialog => dialog.accept());
  await page.getByRole("button", { name: "Удалить" }).click();
  const removeDialogue = await page.evaluate(() =>
    window.__messages.find(message =>
      message.action === "scene_remove_dialogue" &&
      message.dialogueId === "fixture.dialogue"
    )
  );
  if (!removeDialogue)
    throw new Error("Удаление Dialogue resource не отправило scene_remove_dialogue.");

  if (errors.length) throw new Error("Dialogue Workspace pageerror: " + errors.join(" | "));
  console.log("Dialogue Workspace Playwright smoke: OK");
} finally {
  await closeBrowser(browser);
}
