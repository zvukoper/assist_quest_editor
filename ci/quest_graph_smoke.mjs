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
      </body>
    </html>
  `);

  await page.evaluate(() => {
    window.__messages = [];
    window.__assistSend = payload => window.__messages.push(payload);
  });

  await page.addScriptTag({ content: source });

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
    window.dispatchEvent(new MessageEvent("message", {
      data: JSON.stringify({ type: "quest_graph", graph: graphValue, canUndo: true, canRedo: true })
    }));
  }, graph);

  if (errors.length) {
    throw new Error("Ошибка выполнения editor.js до инициализации graph: " + errors.join(" | "));
  }

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
  if (!await page.locator("[data-node-id='start'].dirty").count()) {
    throw new Error("Изменённая нода не помечена оранжевой звёздочкой.");
  }

  await page.getByRole("button", { name: "Проверить" }).click();

  await page.waitForTimeout(20);

  const validation = await page.evaluate(() =>
    window.__messages.find(message => message.action === "graph_validate")
  );

  if (!validation) {
    throw new Error("UI не отправил корректную команду graph_validate.");
  }


  const update = await page.evaluate(() =>
    window.__messages.find(message =>
      message.action === "graph_update_node" &&
      message.nodeId === "start"
    )
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

  // Right-click on a connector must open the connection menu, not Add.
  const choiceInput = page.locator("[data-node-id='choice'] .socketGroup[data-socket-direction='Input']");
  const choiceInputBox = await choiceInput.boundingBox();
  if (!choiceInputBox) throw new Error("Не удалось получить границы Choice Input socket.");
  await page.mouse.click(choiceInputBox.x + 3, choiceInputBox.y + 3, { button: "right" });
  await page.getByText("Коннектор", { exact: true }).waitFor();
  if (await page.locator(".graphContextMenuItem[data-node-type]").count()) {
    throw new Error("ПКМ по коннектору открыл меню создания ноды.");
  }
  await page.getByText("Разорвать соединение:", { exact: false }).first().waitFor();
  await page.keyboard.press("Escape");

  // Right-click on a node opens node actions.
  const choiceBox = await page.locator("[data-node-id='choice']").boundingBox();
  if (!choiceBox) throw new Error("Не удалось получить границы Choice node.");
  await page.mouse.click(choiceBox.x + 20, choiceBox.y + 20, { button: "right" });
  await page.getByText("Нода", { exact: true }).waitFor();
  await page.getByRole("button", { name: "Сохранить", exact: true }).waitFor();
  await page.getByRole("button", { name: "Удалить", exact: true }).waitFor();
  await page.keyboard.press("Escape");

  // Delete key must require confirmation and issue graph_remove_node after confirmation.
  await page.locator("[data-node-id='choice']").click();
  await page.evaluate(() => { window.confirm = () => false; });
  await page.keyboard.press("Delete");
  if (await page.evaluate(() => window.__messages.some(message => message.action === "graph_remove_node"))) {
    throw new Error("Delete отправил удаление без подтверждения.");
  }
  await page.evaluate(() => { window.confirm = () => true; });
  await page.keyboard.press("Delete");
  await page.waitForTimeout(20);
  if (!await page.evaluate(() => window.__messages.some(message =>
    message.action === "graph_remove_node" && message.nodeId === "choice"
  ))) {
    throw new Error("Delete после подтверждения не отправил graph_remove_node.");
  }

  // Right-click Add menu and Choice availability.
  const contextX = svgBox.x + 240;
  const contextY = svgBox.y + 140;
  await page.mouse.click(contextX, contextY, { button: "right" });
  await page.getByText("Add", { exact: true }).waitFor();
  const choiceMenuItem = page.locator(".graphContextMenuItem[data-node-type=\"Choice\"]");
  await choiceMenuItem.waitFor();
  await choiceMenuItem.scrollIntoViewIfNeeded();
  await choiceMenuItem.click();
  await page.waitForTimeout(20);

  const contextAdded = await page.evaluate(() =>
    window.__messages.slice().reverse().find(message =>
      message.action === "graph_add_node" &&
      message.nodeType === "Choice"
    )
  );
  if (!contextAdded || !Number.isFinite(contextAdded.x) || !Number.isFinite(contextAdded.y)) {
    throw new Error("Контекстное меню Add не отправило корректный graph_add_node для Choice.");
  }

  const startOutput = page.locator("[data-node-id='start'] .socketGroup[data-socket-direction='Output']");
  const startOutputBox = await startOutput.boundingBox();
  if (!startOutputBox) {
    throw new Error("Не удалось получить границы Start Output socket.");
  }

  await page.evaluate(() => {
    const socket = document.querySelector(
      "[data-node-id='start'] .socketGroup[data-socket-direction='Output']"
    );
    if (!socket) {
      throw new Error("Start Output socket не найден в DOM.");
    }
    socket.dispatchEvent(new MouseEvent("click", {
      bubbles: true,
      cancelable: true,
      clientX: 0,
      clientY: 0,
      button: 0
    }));
  });

  const beforeConnectMessages = await page.evaluate(() => window.__messages.length);
  const pendingOutput = await page.evaluate(() =>
    window.__assistQuestGraphRuntime?.getPendingOutput?.()
  );
  if (!pendingOutput ||
      pendingOutput.nodeId !== "start" ||
      pendingOutput.socketId !== "start.out") {
    throw new Error(
      "После выбора Start Output не установлен pendingOutput: " +
      JSON.stringify(pendingOutput)
    );
  }

  const connectContextX = svgBox.x + svgBox.width * 0.72;
  const connectContextY = svgBox.y + svgBox.height * 0.72;
  if (connectContextX <= svgBox.x || connectContextX >= svgBox.x + svgBox.width ||
      connectContextY <= svgBox.y || connectContextY >= svgBox.y + svgBox.height) {
    throw new Error("Точка контекстного меню должна находиться внутри Quest Graph canvas.");
  }

  await page.evaluate(({ x, y }) => {
    const svg = document.getElementById("questGraphSvg");
    svg.dispatchEvent(new MouseEvent("contextmenu", {
      bubbles: true,
      cancelable: true,
      clientX: x,
      clientY: y,
      button: 2,
      buttons: 0
    }));
  }, { x: connectContextX, y: connectContextY });

  const phaseMenuItem = page.locator(".graphContextMenuItem[data-node-type=\"Phase\"]");
  await phaseMenuItem.waitFor({ state: "attached" });
  if (await phaseMenuItem.count() !== 1) {
    const menuItems = await page.locator(".graphContextMenuItem").evaluateAll(nodes =>
      nodes.map(node => node.getAttribute("data-node-type"))
    );
    throw new Error("Контекстное меню должно содержать ровно один пункт Phase. Найдены: " + menuItems.join(", "));
  }
  await phaseMenuItem.click({ force: true });
  await page.waitForTimeout(20);

  const addAndConnect = await page.evaluate(index =>
    window.__messages.slice(index).find(message =>
      message.action === "graph_add_node" &&
      message.nodeType === "Phase" &&
      message.connectFromNodeId === "start" &&
      message.connectFromSocketId === "start.out"
    ),
    beforeConnectMessages
  );
  if (!addAndConnect) {
    throw new Error("Add из режима соединения не сохранил источник Output.");
  }

  // Regression: the preview endpoint must be the exact SVG-space position
  // corresponding to the screen cursor.
  const endX = svgBox.x + svgBox.width * 0.82;
  const endY = svgBox.y + svgBox.height * 0.27;
  await page.evaluate(({ x, y }) => {
    const socket = document.querySelector(
      "[data-node-id='start'] .socketGroup[data-socket-direction='Output']"
    );
    if (!socket) {
      throw new Error("Start Output socket не найден для проверки preview.");
    }
    socket.dispatchEvent(new MouseEvent("click", {
      bubbles: true,
      cancelable: true,
      clientX: x,
      clientY: y,
      button: 0
    }));
  }, {
    x: startOutputBox.x + startOutputBox.width / 2,
    y: startOutputBox.y + startOutputBox.height / 2
  });

  await page.evaluate(({ x, y }) => {
    const svg = document.getElementById("questGraphSvg");
    if (!svg) {
      throw new Error("Quest Graph SVG не найден для проверки preview."); 
    }
    svg.dispatchEvent(new PointerEvent("pointermove", {
      bubbles: true,
      cancelable: true,
      pointerId: 1,
      pointerType: "mouse",
      isPrimary: true,
      clientX: x,
      clientY: y,
      buttons: 0
    }));
  }, { x: endX, y: endY });

  const pendingPath = await page.locator("#questGraphConnectionPreview .pendingEdge").getAttribute("d");
  if (!pendingPath) {
    throw new Error("Не удалось получить preview кабеля.");
  }

  const expectedPoint = await page.evaluate(({x, y}) => {
    const svg = document.getElementById("questGraphSvg");
    return new DOMPoint(x, y).matrixTransform(svg.getScreenCTM().inverse());
  }, { x: endX, y: endY });

  const match = pendingPath.match(/\s([-\d.]+)\s([-\d.]+)$/);
  if (!match) {
    throw new Error("Не удалось разобрать конечную точку preview кабеля: " + pendingPath);
  }

  const actualX = Number(match[1]);
  const actualY = Number(match[2]);
  if (Math.abs(actualX - expectedPoint.x) > 0.01 || Math.abs(actualY - expectedPoint.y) > 0.01) {
    throw new Error(
      "Preview кабеля не совпадает с курсором: actual=(" +
      actualX + "," + actualY + "), expected=(" +
      expectedPoint.x + "," + expectedPoint.y + ")"
    );
  }

  const viewBoxBeforePan = await page.locator("#questGraphSvg").getAttribute("viewBox");
  const panStartX = svgBox.x + svgBox.width / 2;
  const panStartY = svgBox.y + svgBox.height / 2;
  const panEndX = panStartX + 120;
  const panEndY = panStartY + 80;

  await page.evaluate(({ x, y }) => {
    const svg = document.getElementById("questGraphSvg");
    if (!svg) throw new Error("Quest Graph SVG не найден для проверки pan.");

    svg.dispatchEvent(new PointerEvent("pointerdown", {
      bubbles: true,
      cancelable: true,
      pointerId: 7,
      pointerType: "mouse",
      isPrimary: true,
      button: 1,
      buttons: 4,
      clientX: x,
      clientY: y
    }));
  }, { x: panStartX, y: panStartY });

  await page.evaluate(({ x, y }) => {
    const svg = document.getElementById("questGraphSvg");
    svg.dispatchEvent(new PointerEvent("pointermove", {
      bubbles: true,
      cancelable: true,
      pointerId: 7,
      pointerType: "mouse",
      isPrimary: true,
      button: -1,
      buttons: 4,
      clientX: x,
      clientY: y
    }));
  }, { x: panEndX, y: panEndY });

  await page.evaluate(({ x, y }) => {
    const svg = document.getElementById("questGraphSvg");
    svg.dispatchEvent(new PointerEvent("pointerup", {
      bubbles: true,
      cancelable: true,
      pointerId: 7,
      pointerType: "mouse",
      isPrimary: true,
      button: 1,
      buttons: 0,
      clientX: x,
      clientY: y
    }));
  }, { x: panEndX, y: panEndY });

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
