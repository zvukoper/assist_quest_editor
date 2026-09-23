import fs from "node:fs";
import path from "node:path";
import { chromium } from "playwright";

const root = process.cwd();
const source = fs.readFileSync(
  path.join(root, "src", "AssistQuestEditor.App", "Web", "editor.js"),
  "utf8"
);

// Harness must load the real stylesheet: the context menu relies on
// position:fixed, max-height and box-sizing from theme.css. Without it the
// menu is an unstyled static block and any viewport-geometry assertion is
// meaningless.
const theme = fs.readFileSync(
  path.join(root, "src", "AssistQuestEditor.App", "Web", "theme.css"),
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

  await page.addStyleTag({ content: theme });
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

  // Regression: соединение протяжкой от Output к Input.
  //
  // Раньше соединение обрабатывалось только в обработчике `click`. Если нажать
  // на Output, протянуть мышь и отпустить над Input, браузер присылает click
  // общему предку точек нажатия и отпускания, а не сокетам — ни источник не
  // выбирался, ни связь не создавалась. Проверяем реальными событиями мыши.
  //
  // Шаг выполняется до перемещений нод: иначе DOM-позиции уходят от исходных,
  // и сокет может оказаться за границей компактного окна.
  {
    const outCircle = page.locator("[data-node-id='start'] .socketGroup[data-socket-direction='Output'] .socket");
    const inCircle = page.locator("[data-node-id='end'] .socketGroup[data-socket-direction='Input'] .socket");

    const fromBox = await outCircle.boundingBox();
    const toBox = await inCircle.boundingBox();
    if (!fromBox || !toBox) {
      throw new Error("Не удалось получить границы сокетов для проверки протяжки.");
    }

    const fromX = fromBox.x + fromBox.width / 2;
    const fromY = fromBox.y + fromBox.height / 2;
    const toX = toBox.x + toBox.width / 2;
    const toY = toBox.y + toBox.height / 2;

    const beforeIndex = await page.evaluate(() => window.__messages.length);

    await page.mouse.move(fromX, fromY);
    await page.mouse.down();
    await page.mouse.move((fromX + toX) / 2, (fromY + toY) / 2);
    await page.mouse.move(toX, toY);
    await page.mouse.up();
    await page.waitForTimeout(30);

    const dragConnect = await page.evaluate(index =>
      window.__messages.slice(index).find(message => message.action === "graph_connect"),
      beforeIndex
    );

    if (!dragConnect) {
      throw new Error("Протяжка кабеля от Output к Input не создала соединение.");
    }

    if (dragConnect.fromNodeId !== "start" || dragConnect.fromSocketId !== "start.out") {
      throw new Error("Протяжка началась не с start.out: " + JSON.stringify(dragConnect));
    }

    // Протяжка кабеля не должна двигать ноду: для неё не создаётся dragState.
    const dragMoves = await page.evaluate(index =>
      window.__messages.slice(index).filter(message => message.action === "graph_update_node").length,
      beforeIndex
    );
    if (dragMoves !== 0) {
      throw new Error("Протяжка кабеля сдвинула ноду (graph_update_node: " + dragMoves + ").");
    }

    if (await page.evaluate(() => window.__assistQuestGraphRuntime?.getPendingOutput?.())) {
      throw new Error("После успешного соединения источник не сброшен.");
    }
  }

  // Regression: подсветка сокета под курсором и снап кабеля.
  //
  // Сокет лежит на границе ноды, поэтому без подсветки непонятно, попал ли
  // указатель в сокет. Проверяем обводку (класс hovered), подтверждение
  // совместимости (compatible) и притягивание конца кабеля к центру сокета.
  {
    const outCircle = page.locator("[data-node-id='start'] .socketGroup[data-socket-direction='Output'] .socket");
    const inCircle = page.locator("[data-node-id='end'] .socketGroup[data-socket-direction='Input'] .socket");

    const fromBox = await outCircle.boundingBox();
    const toBox = await inCircle.boundingBox();
    if (!fromBox || !toBox) {
      throw new Error("Не удалось получить границы сокетов для проверки подсветки.");
    }

    const fromX = fromBox.x + fromBox.width / 2;
    const fromY = fromBox.y + fromBox.height / 2;
    const toX = toBox.x + toBox.width / 2;
    const toY = toBox.y + toBox.height / 2;

    // 1. Наведение на Output: белая обводка и курсор crosshair.
    await page.mouse.move(fromX, fromY);
    await page.waitForTimeout(40);

    const outputHover = await page.evaluate(() => {
      const group = document.querySelector(
        "[data-node-id='start'] .socketGroup[data-socket-direction='Output']"
      );
      return {
        hovered: group?.classList.contains("hovered") ?? false,
        cursor: group ? getComputedStyle(group).cursor : null
      };
    });

    if (!outputHover.hovered) {
      throw new Error("Сокет Output не подсвечен при наведении.");
    }
    if (outputHover.cursor !== "crosshair") {
      throw new Error("Курсор над сокетом не crosshair: " + outputHover.cursor);
    }

    // 2. Выбираем Output и наводим на совместимый Input.
    await page.mouse.down();
    await page.mouse.up();
    await page.waitForTimeout(30);
    await page.mouse.move(toX, toY);
    await page.waitForTimeout(50);

    const inputHover = await page.evaluate(() => {
      const group = document.querySelector(
        "[data-node-id='end'] .socketGroup[data-socket-direction='Input']"
      );
      return {
        hovered: group?.classList.contains("hovered") ?? false,
        compatible: group?.classList.contains("compatible") ?? false,
        cursor: group ? getComputedStyle(group).cursor : null
      };
    });

    if (!inputHover.hovered || !inputHover.compatible) {
      throw new Error(
        "Совместимый Input не подсвечен как hovered+compatible: " + JSON.stringify(inputHover)
      );
    }
    if (inputHover.cursor !== "copy") {
      throw new Error("Курсор над совместимым Input не copy: " + inputHover.cursor);
    }

    // 3. Снап: конец кабеля совпадает с центром целевого сокета.
    const snap = await page.evaluate(({ x, y }) => {
      const path = document.querySelector("#questGraphConnectionPreview .pendingEdge");
      if (!path) return null;
      const match = path.getAttribute("d").match(/\s([-\d.]+)\s([-\d.]+)$/);
      if (!match) return null;
      const svg = document.getElementById("questGraphSvg");
      const expected = new DOMPoint(x, y).matrixTransform(svg.getScreenCTM().inverse());
      return { actualX: Number(match[1]), actualY: Number(match[2]),
               expectedX: expected.x, expectedY: expected.y };
    }, { x: toX, y: toY });

    if (!snap) {
      throw new Error("Не удалось получить конец preview-кабеля для проверки снапа.");
    }
    if (Math.abs(snap.actualX - snap.expectedX) > 0.5 ||
        Math.abs(snap.actualY - snap.expectedY) > 0.5) {
      throw new Error(
        "Кабель не притянут к центру сокета: actual=(" +
        snap.actualX + "," + snap.actualY + "), expected=(" +
        snap.expectedX + "," + snap.expectedY + ")"
      );
    }

    // 4. Уход указателя снимает подсветку.
    await page.mouse.move(5, 5);
    await page.waitForTimeout(40);
    if (await page.locator("#questGraphSvg .socketGroup.hovered").count()) {
      throw new Error("При уходе указателя подсветка сокета не снята.");
    }

    // Сбрасываем выбранный Output, чтобы не влиять на следующие шаги.
    await page.keyboard.press("Escape");
  }

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

  // Use a compact viewport so the Add menu is guaranteed to exercise its
  // viewport clamp and wheel scrolling behavior.
  await page.setViewportSize({ width: 1280, height: 420 });

  // The canvas box changes with the viewport, so re-measure it before any
  // pointer-based interaction that relies on it (pan and wheel zoom).
  const resizedSvgBox = await page.locator("#questGraphSvg").boundingBox();
  if (!resizedSvgBox) {
    throw new Error("Не удалось получить границы Quest Graph canvas после смены viewport.");
  }

  // Right-click on a connector must open the connection menu, not Add.
  const choiceInput = page.locator("[data-node-id='choice'] .socketGroup[data-socket-direction='Input'] .socket");
  await choiceInput.waitFor();
  await choiceInput.click({ button: "right" });

  const connectionMenu = page.locator(".graphContextMenu[data-menu-kind='connection']");
  await connectionMenu.waitFor();
  if (await page.locator(".graphContextMenu[data-menu-kind='add']").count()) {
    throw new Error("ПКМ по коннектору открыл меню создания ноды.");
  }
  await connectionMenu.getByText("Коннектор", { exact: true }).waitFor();
  await connectionMenu.getByText("Разорвать соединение:", { exact: false }).first().waitFor();
  await page.keyboard.press("Escape");

  // Right-click on a node opens node actions.
  const choiceNode = page.locator("[data-node-id='choice']");
  await choiceNode.waitFor();
  await choiceNode.click({ button: "right" });

  const nodeMenu = page.locator(".graphContextMenu[data-menu-kind='node']");
  await nodeMenu.waitFor();
  await nodeMenu.getByText("Нода", { exact: true }).waitFor();
  await nodeMenu.getByRole("button", { name: "Сохранить", exact: true }).click();
  if (!await page.evaluate(() => window.__messages.some(message => message.action === "graph_save"))) {
    throw new Error("Команда «Сохранить» из меню ноды не отправила graph_save.");
  }

  // Regression: Host clears documentDirty after a successful save. The UI must
  // drop its per-node dirty markers, otherwise the star stays forever.
  if (!await page.locator("[data-node-id='start'].dirty").count()) {
    throw new Error("Перед проверкой сброса dirty нода start должна быть помечена.");
  }
  await page.evaluate(graphValue => {
    window.dispatchEvent(new MessageEvent("message", {
      data: JSON.stringify({
        type: "quest_graph",
        graph: graphValue,
        canUndo: true,
        canRedo: true,
        documentDirty: false
      })
    }));
  }, graph);
  await page.waitForTimeout(20);
  if (await page.locator("#questGraphSvg .node.dirty").count()) {
    throw new Error("После сохранения метки изменённых нод не сброшены.");
  }

  await choiceNode.click({ button: "right" });
  const reopenedNodeMenu = page.locator(".graphContextMenu[data-menu-kind='node']");
  await reopenedNodeMenu.waitFor();
  await reopenedNodeMenu.getByText("Нода", { exact: true }).waitFor();
  await reopenedNodeMenu.getByRole("button", { name: "Удалить", exact: true }).waitFor();
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

  // Right-click Add menu: it must stay inside the viewport and scroll by mouse wheel.
  await page.locator("#questGraphSvg").click({
    button: "right",
    position: { x: 240, y: 140 }
  });
  await page.getByText("Add", { exact: true }).waitFor();

  const addMenu = page.locator(".graphContextMenu");
  const addMenuGeometry = await addMenu.evaluate(menu => ({
    top: menu.getBoundingClientRect().top,
    bottom: menu.getBoundingClientRect().bottom,
    scrollHeight: menu.scrollHeight,
    clientHeight: menu.clientHeight,
    scrollTop: menu.scrollTop
  }));
  if (addMenuGeometry.top < 0 || addMenuGeometry.bottom > (await page.evaluate(() => window.innerHeight))) {
    throw new Error("Меню Add выходит за пределы окна.");
  }
  if (addMenuGeometry.scrollHeight <= addMenuGeometry.clientHeight) {
    throw new Error("Меню Add не стало прокручиваемым.");
  }

  await addMenu.hover();
  await page.mouse.wheel(0, 700);
  const addMenuScrolled = await addMenu.evaluate(menu => menu.scrollTop);
  if (addMenuScrolled <= addMenuGeometry.scrollTop) {
    throw new Error("Колесо мыши не прокрутило меню Add.");
  }

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
  const panStartY = Math.min(
    resizedSvgBox.y + resizedSvgBox.height / 2,
    (await page.evaluate(() => window.innerHeight)) - 8
  );
  const panStartX = resizedSvgBox.x + resizedSvgBox.width / 2;
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

  // Pan обязан следовать за курсором: графовая точка под указателем не должна
  // смещаться за время протяжки. Проверка только «viewBox изменился» слишком
  // слабая: при несовпадении аспекта канваса и viewBox pan разбегается, но
  // viewBox всё равно меняется. Полный набор геометрий — в ci/pan_smoke.mjs.
  const panDrift = await page.evaluate(() => {
    const svg = document.getElementById("questGraphSvg");
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
        bubbles: true, cancelable: true, pointerId: 7, pointerType: "mouse",
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
      "Pan средней кнопкой не следует за курсором: дрейф (" +
      panDrift.driftX.toFixed(2) + ", " + panDrift.driftY.toFixed(2) + ")."
    );
  }

  await page.getByRole("button", { name: "Новый" }).click();
  if (!await page.evaluate(() => window.__messages.some(message => message.action === "graph_new"))) {
    throw new Error("UI не отправил graph_new.");
  }

  // The canvas is taller than the compact viewport, so its center can fall
  // below the window. Aim at a point that is guaranteed to be visible.
  const zoomX = resizedSvgBox.x + resizedSvgBox.width / 2;
  const zoomY = Math.min(
    resizedSvgBox.y + resizedSvgBox.height / 2,
    (await page.evaluate(() => window.innerHeight)) - 8
  );
  const zoomHitsCanvas = await page.evaluate(({ x, y }) => {
    const target = document.elementFromPoint(x, y);
    return Boolean(target && target.closest("#questGraphSvg"));
  }, { x: zoomX, y: zoomY });
  if (!zoomHitsCanvas) {
    throw new Error("Точка для проверки зума не попадает в Quest Graph canvas.");
  }

  await page.mouse.move(zoomX, zoomY);
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
  // Reference Picker: human-readable catalog value is shown in the editor,
  // while save must send the stable ID. Unknown references must remain intact.
  await page.evaluate(() => {
    window.dispatchEvent(new MessageEvent("message", {
      data: JSON.stringify({
        type: "quest_graph",
        graph: {
          id: "reference-test",
          name: "Reference test",
          nodes: [{
            nodeId: "interaction",
            nodeType: "Interaction",
            title: "Интеракция",
            x: 100, y: 100,
            parameters: { worldPointId: "city:chelyabinsk", itemId: "item.test" },
            sockets: []
          }],
          connections: []
        },
        selectedNodeId: "interaction",
        sceneCatalog: [{ id: "scene.test", title: "Тестовая сцена" }],
        itemCatalog: [{ id: "item.test", name: "Тестовый предмет", category: "Тест" }],
        npcCatalog: [{ id: "npc.test", name: "Тестовый НПЦ" }],
        worldPointCatalog: [{ id: "city:chelyabinsk", name: "Челябинск", category: "Город" }]
      })
    }));
  });

  const referenceInput = page.locator("[data-reference-input][data-reference-key='worldPointId']");
  await referenceInput.waitFor();
  const referenceValue = await referenceInput.inputValue();
  if (referenceValue !== "Челябинск") {
    throw new Error("Reference Picker должен показывать имя WorldPoint, а не технический ID: " + referenceValue);
  }

  await page.locator("#saveGraphNode").click();
  const referenceSave = await page.evaluate(() =>
    window.__messages.find(message => message.action === "graph_update_node")
  );
  if (!referenceSave || referenceSave.parameters.worldPointId !== "city:chelyabinsk") {
    throw new Error("Reference Picker должен сохранять стабильный WorldPoint ID.");
  }

  await page.evaluate(() => {
    const input = document.querySelector("[data-reference-input][data-reference-key='worldPointId']");
    input.value = "future.point.42";
  });
  await page.locator("#saveGraphNode").click();
  const unknownSave = await page.evaluate(() =>
    [...window.__messages].reverse().find(message => message.action === "graph_update_node")
  );
  if (!unknownSave || unknownSave.parameters.worldPointId !== "future.point.42") {
    throw new Error("Неизвестный Reference ID нельзя очищать при сохранении.");
  }

} finally {
  await browser.close();
}
