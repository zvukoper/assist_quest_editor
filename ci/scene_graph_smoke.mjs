import fs from "node:fs";
import path from "node:path";
import { chromium } from "playwright";

const root = process.cwd();
const source = fs.readFileSync(
  path.join(root, "src", "AssistQuestEditor.App", "Web", "sceneEditor.js"),
  "utf8"
);
const theme = fs.readFileSync(
  path.join(root, "src", "AssistQuestEditor.App", "Web", "theme.css"),
  "utf8"
);

const browser = await chromium.launch({ headless: true });
try {
  const page = await browser.newPage({ viewport: { width: 1280, height: 900 } });
  const errors = [];

  page.on("pageerror", error => errors.push(String(error)));

  await page.setContent(
    "<!doctype html><html lang='ru'><body><main>" +
    "<div id='workspace'></div><div id='inspector'></div>" +
    "</main></body></html>"
  );

  await page.addStyleTag({ content: theme });
  await page.evaluate(() => {
    window.__messages = [];
    window.__assistSend = payload => window.__messages.push(payload);
  });
  await page.addScriptTag({ content: source });

  const definition = {
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
          title: "Начало",
          x: 80, y: 250,
          parameters: {},
          sockets: [
            { socketId: "start.out", name: "Далее", direction: "Output", flowKind: "Normal" }
          ]
        },
        {
          nodeId: "dialogue",
          nodeType: "Dialogue",
          title: "Диалог",
          x: 320, y: 80,
          parameters: { dialogueId: "fixture.dialogue" },
          sockets: [
            { socketId: "dialogue.in", name: "Вход", direction: "Input", flowKind: "Normal" },
            { socketId: "dialogue.out", name: "Далее", direction: "Output", flowKind: "Normal" }
          ]
        },
        {
          nodeId: "dialogue-empty",
          nodeType: "Dialogue",
          title: "Пустой диалог",
          x: 80, y: 500,
          parameters: { dialogueId: "" },
          sockets: [
            { socketId: "dialogue-empty.in", name: "Вход", direction: "Input", flowKind: "Normal" },
            { socketId: "dialogue-empty.out", name: "Далее", direction: "Output", flowKind: "Normal" }
          ]
        },
        {
          nodeId: "choice",
          nodeType: "Choice",
          title: "Выбор",
          x: 360, y: 200,
          parameters: { choiceId: "fixture.choice", outputCount: "3" },
          sockets: [
            { socketId: "choice.in", name: "Вход", direction: "Input", flowKind: "Normal" },
            { socketId: "choice.option1", name: "Вариант 1", direction: "Output", flowKind: "Normal" },
            { socketId: "choice.option2", name: "Вариант 2", direction: "Output", flowKind: "Normal" },
            { socketId: "choice.option3", name: "Вариант 3", direction: "Output", flowKind: "Normal" }
          ]
        },
        {
          nodeId: "end",
          nodeType: "SceneEnd",
          title: "Конец",
          x: 720, y: 250,
          parameters: {},
          sockets: [
            { socketId: "end.in", name: "Вход", direction: "Input", flowKind: "Normal" }
          ]
        }
      ],
      connections: [
        { fromNodeId: "start", fromSocketId: "start.out", toNodeId: "choice", toSocketId: "choice.in" }
      ]
    },
    dialogues: [
      { id: "fixture.dialogue", speaker: "A", text: "Исходный диалог." }
    ],
    choices: [
      {
        id: "fixture.choice",
        title: "Выбор",
        speaker: "A",
        text: "B",
        options: [
          { id: "one", text: "Один", outputSocketId: "choice.option1" },
          { id: "two", text: "Два", outputSocketId: "choice.option2" },
          { id: "three", text: "Три", outputSocketId: "choice.option3" }
        ]
      }
    ]
  };

  await page.evaluate(value => {
    history.replaceState({}, "", "#scene");
    window.dispatchEvent(new MessageEvent("message", {
      data: JSON.stringify({
        type: "scene_catalog",
        scenes: [{ id: "fixture", title: "Fixture Scene" }]
      })
    }));
    window.dispatchEvent(new MessageEvent("message", {
      data: JSON.stringify({
        type: "scene_definition",
        definition: value,
        canUndo: true,
        canRedo: true,
        validation: [],
        documentPath: "",
        lastDocumentPath: "",
        documentDirty: true
      })
    }));
  }, definition);

  await page.locator("#sceneGraphSvg .nodeTitle", { hasText: "SceneStart" }).waitFor();
  // Инспектор Scene Editor переименован в sceneGraphEditTitle при синхронизации с
  // механикой Quest Graph. Проверка ждала старое имя и падала по таймауту ещё до
  // начала сценария, поэтому не ловила ни одного реального дефекта.
  await page.locator("#sceneGraphEditTitle").waitFor();


  // Dialogue authoring: linked resource is shown with speaker/text and can be saved.
  await page.locator("[data-node-id='dialogue']").click();
  await page.locator("#sceneDialogueResource").waitFor();
  if (await page.locator("#sceneDialogueSpeaker").inputValue() !== "A")
    throw new Error("Dialogue Speaker не загружен из Scene resource.");
  if (await page.locator("#sceneDialogueText").inputValue() !== "Исходный диалог.")
    throw new Error("Dialogue Text не загружен из Scene resource.");

  await page.locator("#sceneDialogueSpeaker").fill("Новый A");
  await page.locator("#sceneDialogueText").fill("Новый текст диалога.");
  await page.getByRole("button", { name: "Сохранить диалог" }).click();
  const dialogueUpdate = await page.evaluate(() =>
    window.__messages.slice().reverse().find(message =>
      message.action === "scene_update_dialogue" &&
      message.dialogueId === "fixture.dialogue"
    )
  );
  if (!dialogueUpdate ||
      dialogueUpdate.speaker !== "Новый A" ||
      dialogueUpdate.text !== "Новый текст диалога.") {
    throw new Error("Редактор Dialogue не отправил корректный scene_update_dialogue.");
  }

  // Empty Dialogue node can create a canonical content resource and link it to the node.
  await page.locator("[data-node-id='dialogue-empty']").click();
  await page.locator("#createSceneDialogue").waitFor();
  await page.getByRole("button", { name: "Создать диалог для ноды" }).click();
  if (!await page.evaluate(() =>
    window.__messages.some(message =>
      message.action === "scene_create_dialogue_for_node" &&
      message.nodeId === "dialogue-empty"
    ))) {
    throw new Error("Создание Dialogue resource для ноды не отправило корректное действие.");
  }

  if (errors.length) throw new Error("Scene Editor pageerror при инициализации: " + errors.join(" | "));

  // Output -> Input протяжкой. Это главный regression для canvas interaction.
  const startOutput = page.locator(
    "[data-node-id='start'] .socketGroup[data-socket-direction='Output'] .socket"
  );
  const endInput = page.locator(
    "[data-node-id='end'] .socketGroup[data-socket-direction='Input'] .socket"
  );

  const fromBox = await startOutput.boundingBox();
  const toBox = await endInput.boundingBox();
  if (!fromBox || !toBox) throw new Error("Не удалось измерить Scene sockets.");

  const fromX = fromBox.x + fromBox.width / 2;
  const fromY = fromBox.y + fromBox.height / 2;
  const toX = toBox.x + toBox.width / 2;
  const toY = toBox.y + toBox.height / 2;

  const beforeConnect = await page.evaluate(() => window.__messages.length);
  await page.mouse.move(fromX, fromY);
  await page.mouse.down();
  await page.mouse.move((fromX + toX) / 2, (fromY + toY) / 2);
  await page.mouse.move(toX, toY);
  await page.mouse.up();

  const connect = await page.evaluate(index =>
    window.__messages.slice(index).find(message => message.action === "scene_connect"),
    beforeConnect
  );
  if (!connect || connect.fromNodeId !== "start" || connect.fromSocketId !== "start.out")
    throw new Error("Протяжка Output → Input не создала корректный scene_connect.");

  if (await page.evaluate(() => window.__assistSceneEditor.getState().selectedNodeId) !== "start")
    throw new Error("После выбора Output start не стал выбранной нодой.");

  // Hover/cursor/snap — те же гарантии, что в Quest Graph.
  await page.mouse.move(fromX, fromY);
  await page.waitForTimeout(30);
  const hover = await page.evaluate(() => {
    const group = document.querySelector(
      "[data-node-id='start'] .socketGroup[data-socket-direction='Output']"
    );
    return {
      hovered: group?.classList.contains("hovered") ?? false,
      cursor: group ? getComputedStyle(group).cursor : null
    };
  });
  if (!hover.hovered || hover.cursor !== "crosshair")
    throw new Error("Output hover/cursor отличается от эталона Quest Graph: " + JSON.stringify(hover));

  await page.mouse.down();
  await page.mouse.up();
  await page.mouse.move(toX, toY);
  await page.waitForTimeout(40);
  const compatible = await page.evaluate(() => {
    const group = document.querySelector(
      "[data-node-id='end'] .socketGroup[data-socket-direction='Input']"
    );
    return {
      hovered: group?.classList.contains("hovered") ?? false,
      compatible: group?.classList.contains("compatible") ?? false,
      cursor: group ? getComputedStyle(group).cursor : null
    };
  });
  if (!compatible.hovered || !compatible.compatible || compatible.cursor !== "copy")
    throw new Error("Input hover/cursor/sovmestimost не совпадает с эталоном: " + JSON.stringify(compatible));

  await page.keyboard.press("Escape");
  await page.mouse.move(5, 5);
  if (await page.locator("#sceneGraphSvg .socketGroup.hovered").count())
    throw new Error("Hover socket не очищается при уходе указателя.");


  // Choice authoring: text fields and option texts are content, not generic node parameters.
  await page.locator("[data-node-id='choice']").click();
  await page.locator("#sceneChoiceResource").waitFor();
  if (await page.locator("#sceneChoiceSpeaker").inputValue() !== "A")
    throw new Error("Choice Speaker не загружен.");
  if (await page.locator("#sceneChoiceText").inputValue() !== "B")
    throw new Error("Choice Text не загружен.");

  const optionRows = page.locator("[data-choice-option-row]");
  if (await optionRows.count() !== 3)
    throw new Error("Количество Choice options в редакторе не совпадает с ресурсом.");

  const firstOptionText = optionRows.nth(0).locator("[data-choice-option-text]");
  await firstOptionText.fill("Первый обновлённый");
  await page.locator("#sceneChoiceTitle").fill("Обновлённый выбор");
  await page.locator("#sceneChoiceSpeaker").fill("Новый A");
  await page.locator("#sceneChoiceText").fill("Новый вопрос");
  await page.getByRole("button", { name: "Сохранить выбор" }).click();

  const choiceUpdate = await page.evaluate(() =>
    window.__messages.slice().reverse().find(message =>
      message.action === "scene_update_choice" &&
      message.choiceId === "fixture.choice"
    )
  );
  if (!choiceUpdate ||
      choiceUpdate.title !== "Обновлённый выбор" ||
      choiceUpdate.speaker !== "Новый A" ||
      choiceUpdate.text !== "Новый вопрос" ||
      choiceUpdate.options?.[0]?.id !== "one" ||
      choiceUpdate.options?.[0]?.text !== "Первый обновлённый") {
    throw new Error("Редактор Choice не отправил корректный scene_update_choice.");
  }

  await page.getByRole("button", { name: "Добавить вариант" }).click();
  if (!await page.evaluate(() =>
    window.__messages.some(message =>
      message.action === "scene_add_choice_option" &&
      message.nodeId === "choice" &&
      message.choiceId === "fixture.choice"
    ))) {
    throw new Error("Добавление Choice option не отправило корректное действие.");
  }

  // Inspector + dirty.
  await page.locator("[data-node-id='choice']").click();
  await page.locator("#sceneGraphEditTitle").fill("Выбор обновлён");
  await page.getByRole("button", { name: "Сохранить свойства" }).click();
  await page.waitForTimeout(20);
  if (!await page.locator("[data-node-id='choice'].dirty").count())
    throw new Error("Изменённая Scene node не получила dirty-звезду.");

  const update = await page.evaluate(() =>
    window.__messages.slice().reverse().find(message =>
      message.action === "scene_update_node" && message.nodeId === "choice"
    )
  );
  if (!update || update.title !== "Выбор обновлён")
    throw new Error("Inspector не отправил scene_update_node.");

  // Dynamic Choice outputs remain editable.
  const outputCount = page.locator("[data-param-key]").filter({ hasText: "outputCount" });
  if (!await page.locator("[data-param-key]").evaluateAll(nodes =>
    nodes.some(node => node.value === "outputCount")))
    throw new Error("Choice outputCount не отображается в inspector.");

  // Add node from toolbar.
  await page.locator("#sceneNodeType").selectOption("Dialogue");
  await page.locator("#sceneNodeTitle").fill("Новый диалог");
  await page.getByRole("button", { name: "Добавить ноду" }).click();
  if (!await page.evaluate(() =>
    window.__messages.some(message =>
      message.action === "scene_add_node" &&
      message.nodeType === "Dialogue" &&
      message.title === "Новый диалог"
    )))
    throw new Error("Кнопка добавления не отправила scene_add_node.");

  // Context menu on connector/node/background.
  await page.locator("[data-node-id='choice'] .socketGroup[data-socket-direction='Input'] .socket")
    .click({ button: "right" });
  const connectionMenu = page.locator(".graphContextMenu[data-menu-kind='connection']");
  await connectionMenu.waitFor();
  if (await page.locator(".graphContextMenu[data-menu-kind='add']").count())
    throw new Error("ПКМ по socket открыл Add вместо Connection menu.");
  await connectionMenu.getByText("Коннектор", { exact: true }).waitFor();
  await page.keyboard.press("Escape");

  await page.locator("[data-node-id='choice']").click({ button: "right" });
  const nodeMenu = page.locator(".graphContextMenu[data-menu-kind='node']");
  await nodeMenu.waitFor();
  await nodeMenu.getByText("Нода", { exact: true }).waitFor();
  await page.keyboard.press("Escape");

  // Delete confirmation.
  await page.locator("[data-node-id='choice']").click();
  await page.evaluate(() => { window.confirm = () => false; });
  await page.keyboard.press("Delete");
  if (await page.evaluate(() => window.__messages.some(message => message.action === "scene_remove_node")))
    throw new Error("DEL удаляет Scene node без подтверждения.");

  await page.evaluate(() => { window.confirm = () => true; });
  await page.keyboard.press("Delete");
  if (!await page.evaluate(() =>
    window.__messages.some(message => message.action === "scene_remove_node" && message.nodeId === "choice")
  ))
    throw new Error("Подтверждённый DEL не отправил scene_remove_node.");

  // Add menu must remain inside viewport and scroll.
  await page.setViewportSize({ width: 1280, height: 420 });
  await page.locator("#sceneGraphSvg").click({ button: "right", position: { x: 220, y: 120 } });
  const addMenu = page.locator(".graphContextMenu[data-menu-kind='add']");
  await addMenu.waitFor();
  const geometry = await addMenu.evaluate(menu => ({
    top: menu.getBoundingClientRect().top,
    bottom: menu.getBoundingClientRect().bottom,
    scrollHeight: menu.scrollHeight,
    clientHeight: menu.clientHeight,
    scrollTop: menu.scrollTop
  }));
  const height = await page.evaluate(() => window.innerHeight);
  if (geometry.top < 0 || geometry.bottom > height)
    throw new Error("Add menu выходит за пределы viewport.");
  if (geometry.scrollHeight <= geometry.clientHeight)
    throw new Error("Add menu не имеет вертикальной прокрутки.");
  await addMenu.hover();
  await page.mouse.wheel(0, 600);
  const scrollTop = await addMenu.evaluate(menu => menu.scrollTop);
  if (scrollTop <= geometry.scrollTop)
    throw new Error("Колесо не прокручивает Add menu.");

  await page.keyboard.press("Escape");

  // Drag node sends one final update with changed coordinates.
  const dragNode = page.locator("[data-node-id='start']");
  const dragBox = await dragNode.boundingBox();
  if (!dragBox) throw new Error("Не удалось получить границы start.");
  const dragBefore = await page.evaluate(() => window.__messages.length);
  await page.mouse.move(dragBox.x + 120, dragBox.y + 40);
  await page.mouse.down();
  await page.mouse.move(dragBox.x + 220, dragBox.y + 100);
  await page.mouse.up();
  await page.waitForTimeout(20);

  const dragUpdate = await page.evaluate(index =>
    window.__messages.slice(index).find(message =>
      message.action === "scene_update_node" &&
      message.nodeId === "start" &&
      Number.isFinite(message.x) &&
      Number.isFinite(message.y)
    ), dragBefore);
  if (!dragUpdate) throw new Error("Drag Scene node не отправил координаты.");

  // Pan with middle mouse.
  const svgBox = await page.locator("#sceneGraphSvg").boundingBox();
  if (!svgBox) throw new Error("Не удалось измерить Scene Graph canvas.");
  const beforePan = await page.locator("#sceneGraphSvg").getAttribute("viewBox");
  const panX = svgBox.x + svgBox.width / 2;
  const panY = Math.min(svgBox.y + svgBox.height / 2, height - 8);

  await page.evaluate(({ x, y }) => {
    const svg = document.getElementById("sceneGraphSvg");
    svg.dispatchEvent(new PointerEvent("pointerdown", {
      bubbles: true, cancelable: true, pointerId: 71, pointerType: "mouse",
      button: 1, buttons: 4, clientX: x, clientY: y
    }));
  }, { x: panX, y: panY });
  await page.evaluate(({ x, y }) => {
    const svg = document.getElementById("sceneGraphSvg");
    svg.dispatchEvent(new PointerEvent("pointermove", {
      bubbles: true, cancelable: true, pointerId: 71, pointerType: "mouse",
      button: -1, buttons: 4, clientX: x, clientY: y
    }));
  }, { x: panX + 100, y: panY + 60 });
  await page.evaluate(({ x, y }) => {
    const svg = document.getElementById("sceneGraphSvg");
    svg.dispatchEvent(new PointerEvent("pointerup", {
      bubbles: true, cancelable: true, pointerId: 71, pointerType: "mouse",
      button: 1, buttons: 0, clientX: x, clientY: y
    }));
  }, { x: panX + 100, y: panY + 60 });

  const afterPan = await page.locator("#sceneGraphSvg").getAttribute("viewBox");
  if (beforePan === afterPan) throw new Error("Pan не изменил viewport.");

  // Pan обязан следовать за курсором: графовая точка под указателем не должна
  // смещаться за время протяжки. Проверка только «viewBox изменился» слишком
  // слабая: при несовпадении аспекта канваса и viewBox pan разбегается, но
  // viewBox всё равно меняется. Полный набор геометрий — в ci/pan_smoke.mjs.
  const panDrift = await page.evaluate(() => {
    const svg = document.getElementById("sceneGraphSvg");
    const toGraph = (clientX, clientY) => {
      const point = new DOMPoint(clientX, clientY)
        .matrixTransform(svg.getScreenCTM().inverse());
      return { x: point.x, y: point.y };
    };

    const rect = svg.getBoundingClientRect();
    const startX = rect.left + rect.width / 2;
    const startY = rect.top + rect.height / 2;
    const before = toGraph(startX, startY);

    const fire = (type, clientX, clientY, buttons) => svg.dispatchEvent(
      new PointerEvent(type, {
        bubbles: true, cancelable: true, pointerId: 72, pointerType: "mouse",
        isPrimary: true, button: type === "pointermove" ? -1 : 1,
        buttons, clientX, clientY
      })
    );

    fire("pointerdown", startX, startY, 4);
    fire("pointermove", startX + 80, startY + 60, 4);
    fire("pointerup", startX + 80, startY + 60, 0);

    const after = toGraph(startX + 80, startY + 60);
    return {
      driftX: Math.abs(after.x - before.x),
      driftY: Math.abs(after.y - before.y)
    };
  });
  if (panDrift.driftX > 0.5 || panDrift.driftY > 0.5) {
    throw new Error(
      "Pan не следует за курсором: дрейф (" +
      panDrift.driftX.toFixed(2) + ", " + panDrift.driftY.toFixed(2) + ")."
    );
  }

  // Wheel zoom.
  const beforeZoom = (await page.locator("#sceneGraphSvg").getAttribute("viewBox")).split(/\s+/).map(Number);
  await page.mouse.move(panX, panY);
  await page.mouse.wheel(0, -500);
  const afterZoom = (await page.locator("#sceneGraphSvg").getAttribute("viewBox")).split(/\s+/).map(Number);
  if (!(afterZoom[2] < beforeZoom[2]) || !(afterZoom[3] < beforeZoom[3]))
    throw new Error("Wheel zoom не изменил масштаб.");

  // Host save clears dirty markers.
  await page.evaluate(value => {
    window.dispatchEvent(new MessageEvent("message", {
      data: JSON.stringify({
        type: "scene_definition",
        definition: value,
        canUndo: true,
        canRedo: true,
        validation: [],
        documentPath: "saved.json",
        lastDocumentPath: "saved.json",
        documentDirty: false
      })
    }));
  }, definition);
  await page.waitForTimeout(20);
  if (await page.locator("#sceneGraphSvg .node.dirty").count())
    throw new Error("После сохранения dirty-звёздочки Scene node не сброшены.");

  if (errors.length) throw new Error("Scene Editor pageerror: " + errors.join(" | "));

  console.log("Scene Graph Playwright smoke: OK");
} finally {
  await browser.close();
}
