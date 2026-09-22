(() => {
  const root = document.getElementById("root");
  const title = document.getElementById("editorTitle");
  const send = payload => {
    if (typeof window.__assistSend === "function") {
      window.__assistSend(payload);
      return;
    }
    window.chrome?.webview?.postMessage(payload);
  };

  let simulatorContext = {
    player: null,
    selection: { point: null }
  };
  let questGraph = null;
  let sceneCatalog = [];
  let selectedGraphNodeId = null;
  let pendingOutput = null;
  let graphHistory = { canUndo: false, canRedo: false };
  let graphValidation = [];
  let graphPreviewPositions = new Map();
  let graphViewport = { x: 0, y: 0, width: 1200, height: 720 };
  let graphDocument = { path: "", dirty: false };
  let graphDirtyNodeIds = new Set();
  let graphSpaceDown = false;
  let pendingConnectionPoint = null;
  let runtimeState = { status: "Stopped", currentNodeId: null };
  let executedNodeIds = new Set();
  let pendingGraphFit = false;

  // История последних открытых квестов. Приходит от Host отдельным сообщением
  // recent_files: он же владеет списком и сохраняет его в настройках.
  let recentQuestFiles = [];
  let recentQuestCurrentPath = "";
  let activeRecentMenu = null;

  // Central UI registry of navigable resource references. Planned targets are
  // recorded here so each new editor can use the same open-and-return pattern.
  const GRAPH_REFERENCE_EDITORS = {
    DialogueScene: {
      sceneId: { resourceKind: "Scene", action: "graph_open_scene", label: "Открыть сцену" }
    },
    Interaction: {
      worldPointId: { resourceKind: "WorldPoint", action: null, label: "Открыть точку" }
    },
    WaitForCondition: {
      conditionId: { resourceKind: "Condition", action: null, label: "Открыть условие" }
    },
    Reward: {
      rewardId: { resourceKind: "Reward", action: null, label: "Открыть награду" }
    },
    GiveItem: {
      itemId: { resourceKind: "Item", action: null, label: "Открыть предмет" }
    },
    RemoveItem: {
      itemId: { resourceKind: "Item", action: null, label: "Открыть предмет" }
    }
  };

  const GRAPH_NODE_TYPES = [
    ["Start", "Начало"], ["End", "Завершение"], ["Phase", "Фаза"],
    ["Interaction", "Интеракция"], ["Condition", "Условие"], ["And", "AND"],
    ["Or", "OR"], ["Not", "NOT"], ["Switch", "Переключатель"], ["Random", "Случайная ветка"],
    ["Wait", "Ожидание"], ["WaitForCondition", "Ожидать условие"], ["WaitForEvent", "Ожидать событие"],
    ["Choice", "Выбор"],
    ["SetStatus", "Установить статус"], ["SetStep", "Установить этап"], ["SetFlag", "Установить флаг"],
    ["SetVariable", "Установить переменную"], ["DialogueScene", "Сцена диалога"], ["Reward", "Награда"],
    ["GiveItem", "Выдать предмет"], ["RemoveItem", "Удалить предмет"],
    ["AddReputation", "Добавить репутацию"], ["RemoveReputation", "Уменьшить репутацию"]
  ];

  const configs = {
    graph: { title: "Нодовый редактор квестов", draw: renderGraph },
    scene: { title: "Редактор сцен", draw: renderScene },
    dialogue: {
      title: "Рабочее пространство диалогов",
      draw: (ws, ins) => window.__assistDialogueWorkspace?.render?.(ws, ins)
    },
    world: { title: "Редактор мира и координат", draw: renderWorld },
    channels: { title: "Инспектор Data Channels", draw: renderChannels },
    conditions: { title: "Редактор условий и действий", draw: renderConditions },
    localization: { title: "Редактор локализации", draw: renderLocalization },
    validation: { title: "Проверка проекта", draw: renderValidation },
    registry: { title: "Реестр нод и схем", draw: renderRegistry }
  };

  root.innerHTML =
    "<div class='layoutThree'>" +
      "<section class='panel'><div class='panelTitle'>Редакторы</div><div class='navStack' id='nav'></div></section>" +
      "<section class='panel'><div class='panelTitle' id='workspaceTitle'></div><div class='panelBody' id='workspace'></div></section>" +
      "<section class='panel'><div class='panelTitle'>Свойства</div><div class='panelBody' id='inspector'></div></section>" +
    "</div>";

  const nav = document.getElementById("nav");
  const workspace = document.getElementById("workspace");
  const inspector = document.getElementById("inspector");
  const workspaceTitle = document.getElementById("workspaceTitle");

  Object.entries(configs).forEach(([id, item]) => {
    const button = document.createElement("button");
    button.className = "navButton";
    button.dataset.id = id;
    button.textContent = item.title;
    button.addEventListener("click", () => {
      location.hash = id;
      render();
    });
    nav.appendChild(button);
  });

  function currentId() {
    return location.hash.slice(1).toLowerCase() || "graph";
  }

  function render() {
    const id = currentId();
    const config = configs[id] || configs.graph;
    title.textContent = config.title;
    workspaceTitle.textContent = config.title;
    config.draw(workspace, inspector);

    nav.querySelectorAll(".navButton").forEach(button => {
      button.classList.toggle("active", button.dataset.id === id);
    });
  }

  function renderGraph(ws, ins) {
    if (!questGraph) {
      ws.innerHTML = "<div class='notice'>Ожидание Quest Graph от Host…</div>";
      ins.innerHTML = "";
      return;
    }

    ws.innerHTML =
      "<div class='toolbar' style='margin-bottom:10px;flex-wrap:wrap'>" +
        "<button class='toolButton' id='newGraph'>Новый</button>" +
        "<button class='toolButton' id='openGraph'>Открыть</button>" +
        "<button class='toolButton' id='openRecentGraph' " + (recentQuestFiles.length ? "" : "disabled") +
          " title='Последние открытые квесты'>Последние ▾</button>" +
        "<button class='toolButton' id='openLastGraph' " + (graphDocument.lastPath ? "" : "disabled") + ">Последний JSON</button>" +
        "<button class='toolButton primary' id='saveGraph'>Сохранить</button>" +
        "<button class='toolButton' id='saveGraphAs'>Сохранить как…</button>" +
        "<span class='badge " + (graphDocument.dirty ? "accent" : "blue") + "'>" +
          escapeHtml(graphDocument.dirty ? "Не сохранено" : "Сохранено") +
        "</span>" +
        "<span class='badge'>" + escapeHtml(graphDocument.path ? graphDocument.path : "Новый документ") + "</span>" +
        "<select id='graphNodeType' class='toolButton'>" +
          GRAPH_NODE_TYPES.map(([type, label]) => "<option value='" + escapeHtml(type) + "'>" + escapeHtml(label) + " (" + escapeHtml(type) + ")</option>").join("") +
        "</select>" +
        "<input id='graphNodeTitle' class='toolButton' style='width:190px' placeholder='Название новой ноды'>" +
        "<button class='toolButton primary' id='addGraphNode'>Добавить ноду</button>" +
        "<button class='toolButton' id='layoutGraph' title='Выстроить ноды по слоям без наложений'>Перестроить</button>" +
        "<button class='toolButton' id='fitGraph'>По размеру</button>" +
        "<button class='toolButton' id='undoGraph' " + (graphHistory.canUndo ? "" : "disabled") + ">↶ Отменить</button>" +
        "<button class='toolButton' id='redoGraph' " + (graphHistory.canRedo ? "" : "disabled") + ">↷ Повторить</button>" +
        "<button class='toolButton' id='validateGraph'>Проверить</button>" +
        "<span class='badge accent'>" + questGraph.nodes.length + " нод</span>" +
        "<span class='badge'>" + questGraph.connections.length + " связей</span>" +
        "<span class='badge red'>" + escapeHtml(questGraph.name) + "</span>" +
      "</div>" +
      "<div style='height:calc(100% - 46px);min-height:560px;border:1px solid var(--border);border-radius:8px;overflow:hidden;background:#111419'>" +
        "<svg id='questGraphSvg' viewBox='" + graphViewport.x + " " + graphViewport.y + " " + graphViewport.width + " " + graphViewport.height + "' xmlns='http://www.w3.org/2000/svg' style='width:100%;height:100%'>" +
          "<g id='questGraphEdges'>" + graphEdges() + "</g>" +
          "<g id='questGraphNodes'>" + questGraph.nodes.map(graphNodeMarkup).join("") + "</g>" +
          "<g id='questGraphConnectionPreview'></g>" +
        "</svg>" +
      "</div>";

    ws.querySelector("#newGraph").addEventListener("click", () => send({ action: "graph_new" }));
    ws.querySelector("#openGraph").addEventListener("click", () => send({ action: "graph_open" }));
    ws.querySelector("#openRecentGraph")?.addEventListener("click", event => {
      openRecentFilesMenu(event.currentTarget, recentQuestFiles, recentQuestCurrentPath);
    });
    ws.querySelector("#openLastGraph")?.addEventListener("click", () => send({ action: "graph_open_last" }));
    ws.querySelector("#saveGraph").addEventListener("click", () => send({ action: "graph_save" }));
    ws.querySelector("#saveGraphAs").addEventListener("click", () => send({ action: "graph_save_as" }));

    ws.querySelector("#addGraphNode").addEventListener("click", () => {
      const type = ws.querySelector("#graphNodeType").value;
      const nodeTitle = ws.querySelector("#graphNodeTitle").value.trim() || type;
      send({ action: "graph_add_node", nodeType: type, title: nodeTitle, x: 420, y: 120 });
    });
    ws.querySelector("#fitGraph").addEventListener("click", fitGraph);
    ws.querySelector("#layoutGraph").addEventListener("click", () => {
      if (!questGraph?.nodes?.length) return;

      // Раскладку считает Host на canonical QuestGraph, поэтому UI не дублирует
      // алгоритм. Пока приходит обновлённый граф, сбрасываем локальный preview:
      // иначе отброшенные drag-позиции перекроют новые координаты.
      pendingGraphFit = true;
      graphPreviewPositions.clear();
      send({ action: "graph_layout" });
    });
    ws.querySelector("#undoGraph").addEventListener("click", () => send({ action: "graph_undo" }));
    ws.querySelector("#redoGraph").addEventListener("click", () => send({ action: "graph_redo" }));
    ws.querySelector("#validateGraph").addEventListener("click", () => send({ action: "graph_validate" }));
    bindGraphInteractions(ws.querySelector("#questGraphSvg"));
    updateGraphInspector(ins);
  }

  const GRAPH_NODE_WIDTH = 260;
  const GRAPH_NODE_INPUT_ZONE = 62;
  const GRAPH_NODE_OUTPUT_ZONE = 62;
  const GRAPH_NODE_CENTER_X = GRAPH_NODE_INPUT_ZONE + (GRAPH_NODE_WIDTH - GRAPH_NODE_INPUT_ZONE - GRAPH_NODE_OUTPUT_ZONE) / 2;

  function graphNodeHeight(node) {
    const inputs = node.sockets.filter(socket => socket.direction === "Input").length;
    const outputs = node.sockets.filter(socket => socket.direction === "Output").length;
    return Math.max(110, 78 + Math.max(inputs, outputs) * 22);
  }

  function graphEdges() {
    return questGraph.connections.map(connection => {
      const from = questGraph.nodes.find(node => node.nodeId === connection.fromNodeId);
      const to = questGraph.nodes.find(node => node.nodeId === connection.toNodeId);
      if (!from || !to) return "";

      const fromSocket = from.sockets.find(socket => socket.socketId === connection.fromSocketId);
      const toSocket = to.sockets.find(socket => socket.socketId === connection.toSocketId);
      if (!fromSocket || !toSocket) return "";

      const outputs = from.sockets.filter(socket => socket.direction === "Output");
      const inputs = to.sockets.filter(socket => socket.direction === "Input");
      const fromIndex = outputs.findIndex(socket => socket.socketId === fromSocket.socketId);
      const toIndex = inputs.findIndex(socket => socket.socketId === toSocket.socketId);
      const fromPosition = getGraphNodePosition(from);
      const toPosition = getGraphNodePosition(to);
      const startX = fromPosition.x + GRAPH_NODE_WIDTH;
      const startY = fromPosition.y + 22 + Math.max(0, fromIndex) * 22;
      const endX = toPosition.x;
      const endY = toPosition.y + 22 + Math.max(0, toIndex) * 22;
      const bend = Math.max(70, Math.abs(endX - startX) * 0.45);

      return "<path class='edge " +
        (connection.fromSocketId.endsWith(".false") ? "cut" : "") +
        "' d='M" + startX + " " + startY + " C" +
        (startX + bend) + " " + startY + " " +
        (endX - bend) + " " + endY + " " + endX + " " + endY + "'></path>";
    }).join("");
  }

  function graphNodeMarkup(node) {
    const selected = node.nodeId === selectedGraphNodeId ? " selected" : "";
    const executed = executedNodeIds.has(node.nodeId) ? " executed" : "";
    const active = node.nodeId === runtimeState.currentNodeId &&
      ["Running", "Waiting"].includes(runtimeStatusName(runtimeState.status)) ? " runtime-active" : "";
    const source = pendingOutput?.nodeId === node.nodeId ? " connection-source" : "";
    const dirty = graphDirtyNodeIds.has(node.nodeId) ? " dirty" : "";
    const inputs = node.sockets.filter(socket => socket.direction === "Input");
    const outputs = node.sockets.filter(socket => socket.direction === "Output");

    const position = getGraphNodePosition(node);
    const nodeHeight = graphNodeHeight(node);
    const title = fitNodeText(node.title, 21);
    const nodeType = fitNodeText(node.nodeType, 18);
    const nodeId = fitNodeText(node.nodeId, 21);
    const centerWidth = GRAPH_NODE_WIDTH - GRAPH_NODE_INPUT_ZONE - GRAPH_NODE_OUTPUT_ZONE;

    return "<g class='node" + selected + executed + active + source + dirty + "' data-node-id='" + escapeHtml(node.nodeId) + "' transform='translate(" + position.x + " " + position.y + ")'>" +
      "<rect class='nodeRect' rx='8' width='" + GRAPH_NODE_WIDTH + "' height='" + nodeHeight + "'></rect>" +
      "<rect class='nodeConnectorZone inputZone' x='1' y='1' width='" + (GRAPH_NODE_INPUT_ZONE - 1) + "' height='" + (nodeHeight - 2) + "' rx='7'></rect>" +
      "<rect class='nodeConnectorZone outputZone' x='" + GRAPH_NODE_OUTPUT_ZONE + "' y='1' width='" + (GRAPH_NODE_OUTPUT_ZONE - 1) + "' height='" + (nodeHeight - 2) + "' rx='7'></rect>" +
      "<line class='nodeConnectorSeparator' x1='" + GRAPH_NODE_INPUT_ZONE + "' y1='6' x2='" + GRAPH_NODE_INPUT_ZONE + "' y2='" + (nodeHeight - 6) + "'></line>" +
      "<line class='nodeConnectorSeparator' x1='" + (GRAPH_NODE_WIDTH - GRAPH_NODE_OUTPUT_ZONE) + "' y1='6' x2='" + (GRAPH_NODE_WIDTH - GRAPH_NODE_OUTPUT_ZONE) + "' y2='" + (nodeHeight - 6) + "'></line>" +
      "<text class='nodeDirtyMark' x='" + (GRAPH_NODE_WIDTH / 2) + "' y='15' text-anchor='middle'>★</text>" +
      "<text class='nodeTitle' x='" + (GRAPH_NODE_INPUT_ZONE + centerWidth / 2) + "' y='27' text-anchor='middle'>" + escapeHtml(nodeType) + "</text>" +
      "<text class='nodeExecutedMark' x='" + (GRAPH_NODE_WIDTH - GRAPH_NODE_OUTPUT_ZONE - 10) + "' y='27' text-anchor='middle'>✓</text>" +
      "<text class='nodeLabel' x='" + (GRAPH_NODE_INPUT_ZONE + centerWidth / 2) + "' y='49' text-anchor='middle'>" + escapeHtml(title) + "</text>" +
      "<text class='nodeId' x='" + (GRAPH_NODE_INPUT_ZONE + centerWidth / 2) + "' y='" + (nodeHeight - 13) + "' text-anchor='middle'>" + escapeHtml(nodeId) + "</text>" +
      inputs.map((socket, index) =>
        "<g class='socketGroup' data-socket-direction='Input' data-socket-id='" + escapeHtml(socket.socketId) + "'>" +
          "<rect class='socketHitArea inputHitArea' x='-8' y='" + (10 + index * 22) + "' width='" + (GRAPH_NODE_INPUT_ZONE + 2) + "' height='24' rx='8'></rect>" +
          "<circle class='socket input' cx='0' cy='" + (22 + index * 22) + "' r='6'></circle>" +
          "<text class='socketLabel inputLabel' x='10' y='" + (26 + index * 22) + "'>" + escapeHtml(fitNodeText(socket.name, 9)) + "</text>" +
        "</g>"
      ).join("") +
      outputs.map((socket, index) =>
        "<g class='socketGroup' data-socket-direction='Output' data-socket-id='" + escapeHtml(socket.socketId) + "'>" +
          "<rect class='socketHitArea outputHitArea' x='" + (GRAPH_NODE_WIDTH - GRAPH_NODE_OUTPUT_ZONE - 2) + "' y='" + (10 + index * 22) + "' width='" + (GRAPH_NODE_OUTPUT_ZONE + 10) + "' height='24' rx='8'></rect>" +
          "<circle class='socket output' cx='" + GRAPH_NODE_WIDTH + "' cy='" + (22 + index * 22) + "' r='6'></circle>" +
          "<text class='socketLabel outputLabel' x='" + (GRAPH_NODE_WIDTH - 10) + "' y='" + (26 + index * 22) + "' text-anchor='end'>" + escapeHtml(fitNodeText(socket.name, 9)) + "</text>" +
        "</g>"
      ).join("") +
    "</g>";
  }

  function fitNodeText(value, maxChars) {
    const text = String(value ?? "");
    if (text.length <= maxChars) return text;
    return text.slice(0, Math.max(1, maxChars - 1)) + "…";
  }

  function bindGraphInteractions(svg) {
    if (!svg) return;

    let dragState = null;
    let panState = null;

    /**
     * Поиск сокета под указателем по геометрии, а не по hit-тесту DOM.
     *
     * Причина: круг сокета лежит на границе ноды, и если другая нода перекрывает
     * её край, прямоугольник верхней ноды перехватывает нажатие. Тогда клик по
     * сокету попадает в ноду, связь не создаётся, а курсор мигает между
     * «палец» (сокет) и «рука» (нода).
     *
     * Здесь перебираются все сокеты и выбирается ближайший к указателю в
     * пределах радиуса. Радиус чуть больше видимого круга, поэтому попасть
     * в сокет легко, но соседние сокеты не перепутываются.
     */
    const socketAtPointer = (clientX, clientY, radiusPx = 14) => {
      const nodeElements = svg.querySelectorAll(".node");
      let best = null;
      let bestDistance = Number.POSITIVE_INFINITY;

      nodeElements.forEach(nodeElement => {
        nodeElement.querySelectorAll(".socketGroup").forEach(group => {
          const shape = group.querySelector(".socket");
          if (!shape) return;

          const box = shape.getBoundingClientRect();
          if (box.width === 0 && box.height === 0) return;

          const centerX = box.x + box.width / 2;
          const centerY = box.y + box.height / 2;
          const distance = Math.hypot(clientX - centerX, clientY - centerY);

          if (distance <= radiusPx && distance < bestDistance) {
            bestDistance = distance;
            best = { nodeElement, group, distance };
          }
        });
      });

      if (!best) return null;

      return {
        nodeId: best.nodeElement.dataset.nodeId,
        socketId: best.group.dataset.socketId,
        direction: best.group.dataset.socketDirection
      };
    };

    /**
     * Сокет под курсором в последнем pointermove.
     * Нужен, чтобы завершить соединение с учётом снапа и подсветки.
     */
    let hoveredSocket = null;

    /** Радиус снапа: в этих пределах сокет считается целью. */
    const SOCKET_SNAP_RADIUS = 22;

    /**
     * Подсветка сокета под курсором и подсказка о результате соединения.
     *
     * Правила классов:
     *   hovered      — указатель на сокете (толстая белая обводка);
     *   compatible   — Input, который примет выбранный Output (зелёная обводка);
     *   incompatible — Input, который соединение не примет (красная обводка).
     *
     * Подсветка снимается со всех сокетов каждый раз: иначе при движении
     * оставались бы «залипшие» обводки на ранее наведённых сокетах.
     */
    const updateSocketHover = (clientX, clientY) => {
      const target = socketAtPointer(clientX, clientY);
      hoveredSocket = target;

      svg.querySelectorAll(".socketGroup").forEach(group => {
        const nodeElement = group.closest(".node");
        const nodeId = nodeElement?.dataset.nodeId;
        const socketId = group.dataset.socketId;
        const direction = group.dataset.socketDirection;
        const isHovered = Boolean(target) &&
          target.nodeId === nodeId &&
          target.socketId === socketId;

        group.classList.toggle("hovered", isHovered);
        group.classList.toggle(
          "compatible",
          isHovered && direction === "Input" && isCompatibleInput(nodeId, socketId)
        );
        group.classList.toggle(
          "incompatible",
          isHovered && Boolean(pendingOutput) && direction === "Input" &&
            !isCompatibleInput(nodeId, socketId)
        );
      });
    };

    /** Снять подсветку со всех сокетов. */
    const clearSocketHover = () => {
      hoveredSocket = null;
      svg.querySelectorAll(".socketGroup").forEach(group => {
        group.classList.remove("hovered", "incompatible");
        // Класс compatible также выставляется в updateGraphVisuals по общему
        // правилу совместимости, поэтому здесь его не трогаем.
      });
    };

    const setViewBox = () => {
      svg.setAttribute("viewBox", graphViewport.x + " " + graphViewport.y + " " + graphViewport.width + " " + graphViewport.height);
    };

    svg.addEventListener("contextmenu", event => {
      const path = typeof event.composedPath === "function" ? event.composedPath() : [];
      const socket = path.find(item =>
        item instanceof Element && item.classList?.contains("socketGroup")
      ) || event.target.closest?.(".socketGroup");
      const node = path.find(item =>
        item instanceof Element && item.classList?.contains("node")
      ) || event.target.closest?.(".node");

      // Connector/node menus are bound directly below. Keep the canvas handler
      // reserved for the empty graph background.
      if (socket || node) return;

      event.preventDefault();
      event.stopPropagation();
      const graphPoint = clientToGraph(svg, event.clientX, event.clientY);
      openGraphAddContextMenu(svg, event.clientX, event.clientY, graphPoint);
    });

    svg.addEventListener("pointerdown", event => {
      const wantsPan = event.button === 1 || (event.button === 0 && graphSpaceDown);
      if (wantsPan) {
        panState = { pointerId: event.pointerId, lastX: event.clientX, lastY: event.clientY };
        svg.classList.add("panActive");
        try {
          svg.setPointerCapture?.(event.pointerId);
        } catch {
          // Synthetic or already-released pointers may not be capturable.
        }
        event.preventDefault();
        return;
      }

      if (event.button !== 0) return;

      // Сокет ищется по геометрии, а не через event.target: если край ноды
      // перекрыт другой нодой, прямоугольник верхней ноды перехватывает нажатие,
      // и клик по сокету достаётся ноде. Это же было причиной мигания курсора.
      const hitSocket = socketAtPointer(event.clientX, event.clientY);

      if (pendingOutput && !hitSocket) {
        pendingOutput = null;
        pendingConnectionPoint = null;
        refreshGraphEdges(svg);
        updateGraphVisuals();
      }

      // Нажатие на Output начинает протяжку кабеля.
      //
      // Только `click` недостаточно: если нажать на Output, протянуть мышь и
      // отпустить над Input, браузер присылает click общему предку точки
      // нажатия и отпускания, а не сокетам, поэтому связь не создавалась.
      if (hitSocket && hitSocket.direction === "Output") {
        pendingOutput = { nodeId: hitSocket.nodeId, socketId: hitSocket.socketId };
        pendingConnectionPoint = clientToGraph(svg, event.clientX, event.clientY);
        selectedGraphNodeId = hitSocket.nodeId;

        // Захват указателя: иначе pointerup потеряется, если отпустить за
        // пределами канваса, и кабель останется висеть незавершённым.
        try {
          svg.setPointerCapture?.(event.pointerId);
        } catch {
          // Синтетические указатели могут не захватываться.
        }

        updateGraphVisuals();
        refreshGraphEdges(svg);
        updateGraphInspector(document.getElementById("inspector"));
        event.preventDefault();
        return;
      }

      // Нажатие на Input завершает соединение, если источник уже выбран.
      // Это второй путь (первый — отпускание над Input в finishDrag).
      if (hitSocket && hitSocket.direction === "Input") {
        if (completeConnectionAt(event.clientX, event.clientY, event.pointerId)) {
          event.preventDefault();
        }
        return;
      }

      const node = event.target.closest(".node");
      if (!node) return;

      const nodeId = node.dataset.nodeId;
      const sourceNode = questGraph?.nodes?.find(item => item.nodeId === nodeId);
      if (!sourceNode) return;

      const position = getGraphNodePosition(sourceNode);
      const graphPoint = clientToGraph(svg, event.clientX, event.clientY);

      selectedGraphNodeId = nodeId;
      updateGraphVisuals();
      updateGraphInspector(document.getElementById("inspector"));

      dragState = {
        nodeId,
        startPointerX: event.clientX,
        startPointerY: event.clientY,
        pointerOffsetX: graphPoint.x - position.x,
        pointerOffsetY: graphPoint.y - position.y,
        positionX: position.x,
        positionY: position.y,
        moved: false,
        pointerId: event.pointerId
      };

      svg.setPointerCapture?.(event.pointerId);
      event.preventDefault();
    });

    svg.addEventListener("pointermove", event => {
      if (panState && event.pointerId === panState.pointerId) {
        // Канвас должен «нести» содержимое: графовая точка под указателем
        // обязана остаться той же. Поэтому сдвиг viewBox берётся из реального
        // преобразования экран → граф (clientToGraph опирается на CTM), а не из
        // отношения viewBox.width / rect.width.
        //
        // Последнее верно только при совпадении аспекта канваса и viewBox.
        // Иначе SVG с preserveAspectRatio по умолчанию («xMidYMid meet»)
        // вписывает viewBox с полями, масштаб по осям перестаёт выражаться
        // этими отношениями, и pan «разбегается» по оси с полями.
        const graphPointer = clientToGraph(svg, event.clientX, event.clientY);
        const graphPrevious = clientToGraph(svg, panState.lastX, panState.lastY);
        graphViewport.x -= graphPointer.x - graphPrevious.x;
        graphViewport.y -= graphPointer.y - graphPrevious.y;
        panState.lastX = event.clientX;
        panState.lastY = event.clientY;
        setViewBox();
        refreshGraphEdges(svg);
        event.preventDefault();
        return;
      }

      if (pendingOutput) {
        // Снап: конец кабеля притягивается к сокету под курсором.
        // Так видно, что именно этот сокет станет целью соединения.
        const snapped = socketAtPointer(event.clientX, event.clientY, SOCKET_SNAP_RADIUS);
        if (snapped && snapped.direction === "Input") {
          const point = graphSocketPoint(snapped.nodeId, snapped.socketId);
          if (point) {
            pendingConnectionPoint = point;
          } else {
            pendingConnectionPoint = clientToGraph(svg, event.clientX, event.clientY);
          }
        } else {
          pendingConnectionPoint = clientToGraph(svg, event.clientX, event.clientY);
        }

        refreshGraphEdges(svg);
        updateGraphVisuals();
      }

      // Подсветка сокета под курсором — работает и без выбранного Output,
      // чтобы цель была видна заранее.
      updateSocketHover(event.clientX, event.clientY);

      if (!dragState || event.pointerId !== dragState.pointerId) return;

      const movedDistance = Math.hypot(
        event.clientX - dragState.startPointerX,
        event.clientY - dragState.startPointerY
      );

      const graphPoint = clientToGraph(svg, event.clientX, event.clientY);
      const x = Math.round(graphPoint.x - dragState.pointerOffsetX);
      const y = Math.round(graphPoint.y - dragState.pointerOffsetY);

      if (movedDistance >= 2) {
        dragState.moved = true;
      }

      if (!dragState.moved) return;

      graphPreviewPositions.set(dragState.nodeId, { x, y });

      const nodeElement = svg.querySelector(
        ".node[data-node-id='" + escapeCssAttribute(dragState.nodeId) + "']"
      );
      if (nodeElement) {
        nodeElement.setAttribute("transform", "translate(" + x + " " + y + ")");
      }

      refreshGraphEdges(svg);
      updateGraphInspector(document.getElementById("inspector"));
      event.preventDefault();
    });

    const finishDrag = event => {
      if (panState && event.pointerId === panState.pointerId) {
        panState = null;
        svg.classList.remove("panActive");
        try { svg.releasePointerCapture?.(event.pointerId); } catch {}
        return;
      }

      // Завершение протяжки кабеля проверяется ДО раннего выхода по dragState.
      // При протяжке от сокета нода не перетаскивается, поэтому dragState пуст,
      // и прежний `return` ниже не давал соединению завершиться.
      if (pendingOutput && (!dragState || event.pointerId === dragState.pointerId)) {
        completeConnectionAt(event.clientX, event.clientY, event.pointerId);
        dragState = null;
        return;
      }
      if (!dragState || event.pointerId !== dragState.pointerId) return;

      const finished = dragState;
      dragState = null;

      try {
        svg.releasePointerCapture?.(event.pointerId);
      } catch {
        // Pointer capture may already be released by the browser.
      }

      const preview = graphPreviewPositions.get(finished.nodeId);
      graphPreviewPositions.delete(finished.nodeId);

      if (finished.moved && preview) {
        const node = questGraph?.nodes?.find(item => item.nodeId === finished.nodeId);
        if (node && (preview.x !== node.x || preview.y !== node.y)) {
          queueDirtyNode(node.nodeId);
          send({
            action: "graph_update_node",
            nodeId: node.nodeId,
            title: node.title,
            x: preview.x,
            y: preview.y
          });
        } else {
          refreshGraphEdges(svg);
        }
      }
    };

    /**
     * Завершение соединения в точке отпускания.
     *
     * Сокет ищется с радиусом снапа (`SOCKET_SNAP_RADIUS`, а не `radiusPx` по
     * умолчанию): пользователь ведёт кабель к подсвеченной цели, и небольшой
     * промах на несколько пикселей не должен отменять соединение.
     *
     * Возвращает `true`, если связь была создана.
     */
    const completeConnectionAt = (clientX, clientY, pointerId) => {
      if (!pendingOutput) return false;

      const target = socketAtPointer(clientX, clientY, SOCKET_SNAP_RADIUS);
      if (!target) return false;

      const { nodeId, socketId, direction } = target;

      if (direction !== "Input" || !isCompatibleInput(nodeId, socketId)) return false;

      queueDirtyNodes(pendingOutput.nodeId, nodeId);
      send({
        action: "graph_connect",
        fromNodeId: pendingOutput.nodeId,
        fromSocketId: pendingOutput.socketId,
        toNodeId: nodeId,
        toSocketId: socketId
      });

      pendingOutput = null;
      pendingConnectionPoint = null;

      try {
        svg.releasePointerCapture?.(pointerId);
      } catch {
        // Захват мог быть уже снят браузером.
      }

      clearSocketHover();
      refreshGraphEdges(svg);
      updateGraphVisuals();
      return true;
    };

    // Уход указателя с канваса снимает подсветку: иначе последний наведённый
    // сокет остался бы обведённым.
    svg.addEventListener("pointerleave", () => clearSocketHover());

    svg.addEventListener("pointerup", finishDrag);
    svg.addEventListener("pointercancel", finishDrag);

    svg.addEventListener("wheel", event => {
      if (!questGraph?.nodes?.length) return;

      const rect = svg.getBoundingClientRect();
      if (rect.width <= 0 || rect.height <= 0) return;

      const viewBox = svg.viewBox.baseVal;
      const focus = clientToGraph(svg, event.clientX, event.clientY);
      const zoomFactor = event.deltaY > 0 ? 1.12 : 1 / 1.12;
      const minSize = 180;
      const maxSize = 6000;
      const nextWidth = clamp(viewBox.width * zoomFactor, minSize, maxSize);
      const nextHeight = clamp(viewBox.height * zoomFactor, minSize, maxSize);

      if (nextWidth === viewBox.width && nextHeight === viewBox.height) {
        event.preventDefault();
        return;
      }

      const ratioX = (focus.x - viewBox.x) / viewBox.width;
      const ratioY = (focus.y - viewBox.y) / viewBox.height;

      graphViewport = {
        x: focus.x - ratioX * nextWidth,
        y: focus.y - ratioY * nextHeight,
        width: nextWidth,
        height: nextHeight
      };

      setViewBox();
      refreshGraphEdges(svg);
      event.preventDefault();
    }, { passive: false });

    svg.querySelectorAll(".node").forEach(node => {
      node.addEventListener("contextmenu", event => {
        const path = typeof event.composedPath === "function" ? event.composedPath() : [];
        const socket = path.find(item =>
          item instanceof Element && item.classList?.contains("socketGroup")
        ) || event.target.closest?.(".socketGroup");
        if (socket) return;

        event.preventDefault();
        event.stopPropagation();

        const nodeId = node.dataset.nodeId;
        selectedGraphNodeId = nodeId;
        pendingOutput = null;
        pendingConnectionPoint = null;
        updateGraphVisuals();
        updateGraphInspector(document.getElementById("inspector"));
        openGraphNodeContextMenu(
          svg,
          event.clientX,
          event.clientY,
          nodeId
        );
      });

      node.addEventListener("click", event => {
        if (event.target.closest(".socketGroup")) return;
        selectedGraphNodeId = node.dataset.nodeId;
        updateGraphVisuals();
        updateGraphInspector(document.getElementById("inspector"));
      });
    });

    svg.querySelectorAll(".socketGroup").forEach(socket => {
      const openConnectionMenu = event => {
        event.preventDefault();
        event.stopPropagation();

        const node = socket.closest(".node");
        if (!node) return;

        openGraphConnectionContextMenu(
          svg,
          event.clientX,
          event.clientY,
          node.dataset.nodeId,
          socket.dataset.socketId
        );
      };

      socket.addEventListener("contextmenu", openConnectionMenu);
      socket.querySelectorAll(".socket").forEach(socketShape => {
        socketShape.addEventListener("contextmenu", openConnectionMenu);
      });

      socket.addEventListener("click", event => {
        event.stopPropagation();

        const node = socket.closest(".node");
        const direction = socket.dataset.socketDirection;
        const socketId = socket.dataset.socketId;
        const nodeId = node.dataset.nodeId;

        if (direction === "Output") {
          pendingOutput = { nodeId, socketId };
          pendingConnectionPoint = clientToGraph(svg, event.clientX, event.clientY);
          selectedGraphNodeId = nodeId;
          updateGraphVisuals();
          refreshGraphEdges(svg);
          updateGraphInspector(document.getElementById("inspector"));
          return;
        }

        if (direction === "Input" && pendingOutput) {
          if (isCompatibleInput(nodeId, socketId)) {
            queueDirtyNodes(pendingOutput.nodeId, nodeId);
            send({
              action: "graph_connect",
              fromNodeId: pendingOutput.nodeId,
              fromSocketId: pendingOutput.socketId,
              toNodeId: nodeId,
              toSocketId: socketId
            });
          }
          pendingOutput = null;
          pendingConnectionPoint = null;
          refreshGraphEdges(svg);
          updateGraphVisuals();
        }
      });
    });
  }

  function runtimeStatusName(status) {
    if (typeof status === "string") return status;
    const numeric = Number(status);
    return Number.isInteger(numeric)
      ? ([ "Stopped", "Running", "Waiting", "Completed", "Failed" ][numeric] || String(status))
      : String(status ?? "");
  }

  function isCompatibleInput(nodeId, socketId) {
    if (!pendingOutput || !questGraph || !nodeId || nodeId === pendingOutput.nodeId) return false;
    const node = questGraph.nodes?.find(item => item.nodeId === nodeId);
    return Boolean(node?.sockets?.some(socket =>
      socket.socketId === socketId && socket.direction === "Input"
    ));
  }

  function graphSocketPoint(nodeId, socketId) {
    const node = questGraph?.nodes?.find(item => item.nodeId === nodeId);
    if (!node) return null;
    const socket = node.sockets?.find(item => item.socketId === socketId);
    if (!socket) return null;

    const sockets = node.sockets.filter(item => item.direction === socket.direction);
    const index = Math.max(0, sockets.findIndex(item => item.socketId === socketId));
    const position = getGraphNodePosition(node);
    return {
      x: position.x + (socket.direction === "Output" ? GRAPH_NODE_WIDTH : 0),
      y: position.y + 22 + index * 22
    };
  }

  function connectionPreviewMarkup() {
    if (!pendingOutput || !pendingConnectionPoint) return "";
    const start = graphSocketPoint(pendingOutput.nodeId, pendingOutput.socketId);
    if (!start) return "";
    const end = pendingConnectionPoint;
    const bend = Math.max(70, Math.abs(end.x - start.x) * 0.45);
    return "<path class='edge pendingEdge' d='M" + start.x + " " + start.y + " C" +
      (start.x + bend) + " " + start.y + " " +
      (end.x - bend) + " " + end.y + " " + end.x + " " + end.y + "'></path>";
  }

  function updateGraphVisuals() {
    const svg = document.getElementById("questGraphSvg");
    if (!svg) return;

    svg.querySelectorAll(".node").forEach(node => {
      const id = node.dataset.nodeId;
      node.classList.toggle("selected", id === selectedGraphNodeId);
      node.classList.toggle("executed", executedNodeIds.has(id));
      node.classList.toggle(
        "runtime-active",
        id === runtimeState.currentNodeId &&
        ["Running", "Waiting"].includes(runtimeStatusName(runtimeState.status))
      );
      node.classList.toggle("connection-source", pendingOutput?.nodeId === id);
      node.classList.toggle("dirty", graphDirtyNodeIds.has(id));
    });

    svg.querySelectorAll(".socketGroup").forEach(socket => {
      const nodeId = socket.closest(".node")?.dataset.nodeId;
      const direction = socket.dataset.socketDirection;
      socket.classList.toggle(
        "compatible",
        Boolean(pendingOutput) &&
        direction === "Input" &&
        isCompatibleInput(nodeId, socket.dataset.socketId)
      );
    });

    const preview = svg.querySelector("#questGraphConnectionPreview");
    if (preview) preview.innerHTML = connectionPreviewMarkup();
  }
  const PARAMETER_LABELS = {
    worldPointId: "WorldPoint Id", triggerRadius: "Радиус триггера",
    operator: "Оператор", left: "Левый операнд", comparison: "Сравнение", right: "Правый операнд",
    seconds: "Секунды", conditionId: "Condition Id", eventType: "Тип события",
    status: "Статус", step: "Этап", key: "Ключ", value: "Значение", sceneId: "Scene Id",
    rewardId: "Reward Id", itemId: "Предмет Id", count: "Количество",
    faction: "Фракция", amount: "Количество", outputCount: "Количество выходов"
  };

  function parameterLabel(key) {
    return PARAMETER_LABELS[key] || key;
  }

  function parameterEditorHtml(node) {
    const parameters = node.parameters || {};
    const entries = Object.entries(parameters);
    if (!entries.length) {
      return "<div class='notice'>У этой ноды пока нет параметров.</div>";
    }

    return "<div class='tableLike'>" + entries.map(([key, value]) =>
      "<div class='tableRow' data-param-row>" +
        "<input class='toolButton' data-param-key value='" + escapeHtml(key) + "' title='" + escapeHtml(parameterLabel(key)) + "'>" +
        (node.nodeType === "DialogueScene" && key === "sceneId"
          ? "<div style='display:flex;gap:6px;align-items:center'>" +
              "<select class='toolButton' data-param-value style='flex:1'>" +
                (sceneCatalog.some(scene => scene.id === value)
                  ? ""
                  : "<option value='" + escapeHtml(value) + "' selected>" +
                      escapeHtml(value || "— отсутствует в Scene catalog —") + "</option>") +
                sceneCatalog.map(scene =>
                  "<option value='" + escapeHtml(scene.id) + "'" +
                    (scene.id === value ? " selected" : "") + ">" +
                    escapeHtml(scene.title + " (" + scene.id + ")") +
                  "</option>"
                ).join("") +
              "</select>" +
              (sceneCatalog.some(scene => scene.id === value)
                ? "<button class='toolButton primary' type='button' data-open-reference='" + escapeHtml(value) + "' title='Открыть связанную сцену'>Открыть сцену</button>"
                : "") +
            "</div>"
          : "<input class='toolButton' data-param-value value='" + escapeHtml(value) + "'>") +
        "<button class='toolButton' data-param-remove title='Удалить параметр'>×</button>" +
      "</div>"
    ).join("") + "</div>";
  }

  function addParameterRow(container) {
    const row = document.createElement("div");
    row.className = "tableRow";
    row.dataset.paramRow = "";
    row.innerHTML =
      "<input class='toolButton' data-param-key placeholder='Ключ'>" +
      "<input class='toolButton' data-param-value placeholder='Значение'>" +
      "<button class='toolButton' data-param-remove title='Удалить параметр'>×</button>";
    container.appendChild(row);
    row.querySelector("[data-param-remove]")?.addEventListener("click", () => row.remove());
  }

  function collectParameters(ins) {
    const parameters = {};
    ins.querySelectorAll("[data-param-row]").forEach(row => {
      const key = row.querySelector("[data-param-key]")?.value.trim();
      if (!key) return;
      parameters[key] = row.querySelector("[data-param-value]")?.value ?? "";
    });
    return parameters;
  }

  function updateGraphInspector(ins) {
    if (!ins) return;

    const node = questGraph?.nodes?.find(item => item.nodeId === selectedGraphNodeId);
    if (!node) {
      ins.innerHTML =
        "<div class='badge'>Quest Graph</div>" +
        "<h3 style='margin:10px 0 4px'>Нет выбранной ноды</h3>" +
        "<div class='notice'>Выберите ноду. Для связи нажмите Output, затем нужный Input.</div>";
      return;
    }

    const connections = questGraph.connections.filter(connection =>
      connection.fromNodeId === node.nodeId || connection.toNodeId === node.nodeId);

    ins.innerHTML =
      "<div class='badge accent'>" + escapeHtml(node.nodeType) + "</div>" +
      "<h3 style='margin:10px 0 4px'>" + escapeHtml(node.title) + "</h3>" +
      "<div class='field'><label>NodeId</label><input value='" + escapeHtml(node.nodeId) + "' disabled></div>" +
      "<div class='field' style='margin-top:8px'><label>Название</label><input id='graphEditTitle' value='" + escapeHtml(node.title) + "'></div>" +
      "<div class='fieldGrid' style='margin-top:8px'>" +
        "<div class='field'><label>X</label><input id='graphEditX' type='number' value='" + node.x + "'></div>" +
        "<div class='field'><label>Y</label><input id='graphEditY' type='number' value='" + node.y + "'></div>" +
      "</div>" +
      "<div class='field' style='margin-top:8px'><label>NodeType</label><input value='" + escapeHtml(node.nodeType) + "' disabled></div>" +
      (node.nodeType === "DialogueScene" && node.parameters?.sceneId && sceneCatalog.some(item => item.id === node.parameters.sceneId)
        ? "<div class='card' style='margin-top:12px;padding:10px;border:1px solid var(--accent)'>" +
            "<div class='miniLabel'>Связанная сцена</div>" +
            "<div style='margin-top:4px'>" + escapeHtml(sceneCatalog.find(item => item.id === node.parameters.sceneId)?.title || node.parameters.sceneId) + "</div>" +
            "<button class='toolButton primary' type='button' id='openSceneReference' style='margin-top:8px'>Открыть сцену</button>" +
          "</div>"
        : "") +
      "<div class='miniLabel' style='margin-top:14px'>Параметры</div>" +
      "<div id='graphParameterEditor' style='margin-top:6px'>" + parameterEditorHtml(node) + "</div>" +
      "<button class='toolButton' id='addGraphParameter' style='margin-top:6px'>Добавить параметр</button>" +
      "<button class='toolButton primary' id='saveGraphNode' style='margin-top:10px'>Сохранить свойства</button>" +
      "<button class='toolButton' id='deleteGraphNode' style='margin-top:6px'>Удалить ноду</button>" +
      "<div class='miniLabel' style='margin-top:14px'>Sockets</div>" +
      "<div class='tableLike' style='margin-top:6px'>" +
        node.sockets.map(socket =>
          "<div class='tableRow'><span><strong>" + escapeHtml(socket.name) + "</strong><small>" + escapeHtml(socket.socketId) + "</small></span>" +
          "<span>" + escapeHtml(socket.direction) + "</span><span>" + escapeHtml(socket.flowKind) + "</span></div>"
        ).join("") +
      "</div>" +
      "<div class='miniLabel' style='margin-top:14px'>Связи</div>" +
      "<div class='tableLike' style='margin-top:6px'>" +
        (connections.length
          ? connections.map(connection =>
              "<div class='tableRow'><span><strong>" + escapeHtml(connection.fromNodeId) + " → " +
              escapeHtml(connection.toNodeId) + "</strong><small>" +
              escapeHtml(connection.fromSocketId) + " → " + escapeHtml(connection.toSocketId) +
              "</small></span><button class='toolButton' data-disconnect='" +
              encodeURIComponent(JSON.stringify(connection)) + "'>Удалить</button></div>"
            ).join("")
          : "<div class='notice'>Связей пока нет.</div>") +
      "</div>" +
      "<div class='notice' style='margin-top:12px'>" +
        (pendingOutput
          ? "Выбран Output «" + escapeHtml(pendingOutput.socketId) + "». Нажмите нужный Input."
          : "Связь создаётся кликом Output → Input.") +
      "</div>";

    ins.querySelectorAll("[data-param-remove]").forEach(button => {
      button.addEventListener("click", () => button.closest("[data-param-row]")?.remove());
    });

    ins.querySelectorAll("[data-open-reference]").forEach(button => {
      button.addEventListener("click", () => {
        const row = button.closest("[data-param-row]");
        const key = row?.querySelector("[data-param-key]")?.value;
        const spec = GRAPH_REFERENCE_EDITORS[node.nodeType]?.[key];
        if (!spec?.action) return;
        send({
          action: spec.action,
          nodeId: node.nodeId,
          sceneId: button.dataset.openReference
        });
      });
    });

    ins.querySelector("#openSceneReference")?.addEventListener("click", () => {
      const sceneId = node.parameters?.sceneId;
      if (!sceneId || !sceneCatalog.some(item => item.id === sceneId)) return;
      send({ action: "graph_open_scene", nodeId: node.nodeId, sceneId });
    });

    ins.querySelector("#addGraphParameter").addEventListener("click", () => {
      addParameterRow(ins.querySelector("#graphParameterEditor"));
    });

    ins.querySelector("#saveGraphNode").addEventListener("click", () => {
      queueDirtyNode(node.nodeId);
      updateGraphVisuals();
      send({
        action: "graph_update_node",
        nodeId: node.nodeId,
        title: ins.querySelector("#graphEditTitle").value,
        x: Number(ins.querySelector("#graphEditX").value),
        y: Number(ins.querySelector("#graphEditY").value),
        parameters: collectParameters(ins)
      });
    });

    ins.querySelector("#deleteGraphNode").addEventListener("click", () => {
      requestGraphNodeDelete(node.nodeId);
    });

    ins.querySelectorAll("[data-disconnect]").forEach(button => {
      button.addEventListener("click", () => {
        const connection = JSON.parse(decodeURIComponent(button.dataset.disconnect));
        queueDirtyNodes(connection.fromNodeId, connection.toNodeId);
        send({
          action: "graph_disconnect",
          fromNodeId: connection.fromNodeId,
          fromSocketId: connection.fromSocketId,
          toNodeId: connection.toNodeId,
          toSocketId: connection.toSocketId
        });
      });
    });
  }

  function isTextEntryTarget(target) {
    const element = target instanceof Element ? target : null;
    if (!element) return false;
    if (element.matches("input, textarea, select, [contenteditable='true']")) return true;
    return Boolean(element.closest("input, textarea, select, [contenteditable='true']"));
  }

  function fitGraph() {
    if (!questGraph?.nodes?.length) return;
    const minX = Math.min(...questGraph.nodes.map(node => getGraphNodePosition(node).x));
    const maxX = Math.max(...questGraph.nodes.map(node => getGraphNodePosition(node).x + GRAPH_NODE_WIDTH));
    const minY = Math.min(...questGraph.nodes.map(node => getGraphNodePosition(node).y));
    const maxY = Math.max(...questGraph.nodes.map(node => getGraphNodePosition(node).y + graphNodeHeight(node)));
    const svg = document.getElementById("questGraphSvg");
    if (!svg) return;
    graphViewport = {
      x: minX - 60,
      y: minY - 60,
      width: Math.max(220, maxX - minX + 120),
      height: Math.max(180, maxY - minY + 120)
    };
    svg.setAttribute(
      "viewBox",
      graphViewport.x + " " + graphViewport.y + " " +
      graphViewport.width + " " + graphViewport.height
    );
  }

  function renderScene(ws, ins) {
    if (window.__assistSceneEditor?.render) {
      window.__assistSceneEditor.render(ws, ins);
      return;
    }
    ws.innerHTML = "<div class='notice'>Scene Editor не загружен.</div>";
    ins.innerHTML = "";
  }

  function renderWorld(ws, ins) {
    const point = simulatorContext.selection?.point;
    const playerPosition = simulatorContext.player?.position;
    const selectedText = point
      ? escapeHtml(point.name) + " · " + escapeHtml(point.category)
      : "Точка не выбрана";

    ws.innerHTML =
      "<div class='toolbar' style='margin-bottom:10px'>" +
        "<span class='badge accent'>Мир / координаты</span>" +
        "<button class='toolButton primary' id='useSelectedPoint'>Использовать выбранную точку</button>" +
        "<button class='toolButton' id='usePlayerCoordinates'>Взять координаты игрока</button>" +
      "</div>" +
      "<div class='card'>" +
        "<div class='miniLabel'>Контекст симулятора</div>" +
        "<div style='margin-top:6px'>" + selectedText + "</div>" +
        "<div class='kv'><span>Игрок</span><span>" + (playerPosition ? formatPosition(playerPosition) : "—") + "</span></div>" +
      "</div>" +
      "<div class='card' style='margin-top:12px'>" +
        "<div class='miniLabel'>Координаты WorldPoint</div>" +
        "<div class='fieldGrid' style='margin-top:8px'>" +
          "<div class='field'><label>X</label><input id='worldX' value='" + (point?.position.x ?? 0) + "'></div>" +
          "<div class='field'><label>Y</label><input id='worldY' value='" + (point?.position.y ?? 0) + "'></div>" +
          "<div class='field'><label>Z</label><input id='worldZ' value='" + (point?.position.z ?? 0) + "'></div>" +
        "</div>" +
        "<div class='notice' style='margin-top:10px'>Источник может быть СДО-точка из симулятора или текущая позиция игрока. Сами СДО-точки здесь не редактируются.</div>" +
      "</div>";

    ws.querySelector("#useSelectedPoint").addEventListener("click", () => {
      send({ action: "coordinate_request", source: "selected_point" });
    });

    ws.querySelector("#usePlayerCoordinates").addEventListener("click", () => {
      send({ action: "coordinate_request", source: "player" });
    });

    ins.innerHTML =
      "<div class='badge accent'>Simulator Context</div>" +
      "<h3 style='margin:10px 0 4px'>" + selectedText + "</h3>" +
      "<div class='kv'><span>СДО выбрано</span><span>" + (point ? "да" : "нет") + "</span></div>" +
      "<div class='kv'><span>Игрок</span><span>" + (playerPosition ? formatPosition(playerPosition) : "—") + "</span></div>";
  }

  function renderChannels(ws, ins) {
    ws.innerHTML =
      "<div class='card'><div class='miniLabel'>Контракт</div><h3>Data Channel Hub</h3>" +
      "<p>Источник данных и канал обмена остаются независимыми от Web UI.</p></div>" +
      "<div class='tableLike'>" +
        row("player", "Игрок", "PlayerState", "Simulator") +
        row("world", "Мир", "WorldState", "Simulator") +
        row("world-selection", "Выбранная точка", "WorldSelectionState", "Simulator") +
        row("facts", "Факты", "FactState", "Simulator") +
        row("quest-statuses", "Статусы квестов", "QuestStatusesState", "Simulator") +
        row("states", "Состояния", "RuntimeStatesState", "Simulator") +
        row("inventory", "Инвентарь", "InventoryState", "Simulator") +
        row("reputation", "Репутация", "ReputationState", "Simulator") +
        row("telemetry", "Телеметрия", "TelemetryState", "Simulator") +
        row("environment", "Окружение", "EnvironmentState", "Simulator") +
        row("system", "Система", "SystemState", "Simulator") +
      "</div>";

    ins.innerHTML =
      "<div class='badge'>Data Channels</div><h3 style='margin:10px 0 4px'>Каналы I/O</h3>" +
      "<div class='notice'>Позже сюда подключаются реальные адаптеры ETS2 и DayZ.</div>";
  }

  function row(key, label, type, source) {
    return "<div class='tableRow'><span><strong>" + escapeHtml(key) + "</strong><small>" + escapeHtml(label) + "</small></span><span>" +
      escapeHtml(type) + "</span><span class='badge blue'>" + escapeHtml(source) + "</span></div>";
  }

  function renderConditions(ws, ins) {
    ws.innerHTML =
      "<div class='toolbar'><button class='toolButton primary'>Добавить условие</button><span class='badge accent'>Condition Tree</span></div>" +
      "<div class='conditionTree' style='margin-top:12px'>" +
        condition("AND", "Все условия должны быть истинны", [
          condition("QuestStepIs", "step == return_to_ruslan"),
          condition("ItemCountCompare", "special_marinade_meat >= 1"),
          condition("DistanceCompare", "Distance(player, ruslan) <= 35")
        ]) +
      "</div>";

    ins.innerHTML =
      "<div class='badge'>Condition</div><h3 style='margin:10px 0 4px'>Дерево условий</h3>" +
      "<div class='notice'>Операторы регистрируются отдельно и расширяются без переписывания runtime.</div>";
  }

  function condition(type, label, children) {
    return "<div class='conditionNode'><strong>" + escapeHtml(type) + "</strong><span>" + escapeHtml(label) + "</span>" +
      (children ? "<div class='conditionChildren'>" + children.map(renderCondition).join("") + "</div>" : "") +
    "</div>";
  }

  function renderCondition(value) {
    return "<div class='conditionChild'>" + value + "</div>";
  }

  function renderLocalization(ws, ins) {
    ws.innerHTML =
      "<div class='toolbar'><button class='toolButton primary'>Добавить ключ</button><span class='badge'>RU</span><span class='badge'>EN</span></div>" +
      "<div class='tableLike' style='margin-top:12px'>" +
        "<div class='tableRow'><span><strong>quest.ruslan.start</strong><small>Начало разговора</small></span><span>ru</span><span>Есть для тебя особое предложение.</span></div>" +
        "<div class='tableRow'><span><strong>quest.ruslan.offer</strong><small>Предложение работы</small></span><span>ru</span><span>Нужно найти особое мясо.</span></div>" +
      "</div>";
    ins.innerHTML =
      "<div class='badge'>Localization</div><h3 style='margin:10px 0 4px'>Ключи вместо текста</h3>" +
      "<div class='notice'>Строки диалогов и UI не являются частью runtime-логики.</div>";
  }

  function renderValidation(ws, ins) {
    const errors = graphValidation.filter(item => item.severity === "Error");
    const warnings = graphValidation.filter(item => item.severity === "Warning");

    ws.innerHTML =
      (errors.length === 0
        ? "<div class='notice'>Граф не содержит блокирующих ошибок. Предупреждений: " + warnings.length + ".</div>"
        : "<div class='notice'>Найдено ошибок: " + errors.length + ". Предупреждений: " + warnings.length + ".</div>") +
      (graphValidation.length
        ? "<div class='tableLike' style='margin-top:12px'>" +
            graphValidation.map(item =>
              "<div class='tableRow'><span><strong>" + escapeHtml(item.code) + "</strong><small>" +
              escapeHtml(item.message) +
              (item.nodeId ? " · node=" + escapeHtml(item.nodeId) : "") +
              (item.socketId ? " · socket=" + escapeHtml(item.socketId) : "") +
              "</small></span><span class='badge " +
              (item.severity === "Error" ? "red" : item.severity === "Warning" ? "accent" : "blue") +
              "'>" + escapeHtml(item.severity) + "</span></div>"
            ).join("") +
          "</div>"
        : "<div class='notice' style='margin-top:12px'>Диагностика отсутствует: граф корректен.</div>");

    ins.innerHTML =
      "<div class='badge'>Validation</div><h3 style='margin:10px 0 4px'>Проверка Quest Graph</h3>" +
      "<div class='kv'><span>Ошибки</span><span>" + errors.length + "</span></div>" +
      "<div class='kv'><span>Предупреждения</span><span>" + warnings.length + "</span></div>" +
      "<div class='notice' style='margin-top:10px'>Проверка выполняется на canonical QuestGraph в Host/Domain.</div>";
  }

  function validationRow(a, b, c) {
    return "<div class='tableRow'><span><strong>" + escapeHtml(a) + "</strong><small>" + escapeHtml(b) + "</small></span><span class='badge blue'>" + escapeHtml(c) + "</span></div>";
  }

  function renderRegistry(ws, ins) {
    ws.innerHTML =
      "<div class='card'><div class='miniLabel'>Node Registry</div><p>Каждый тип ноды имеет schema, визуальный редактор и runtime handler.</p></div>" +
      "<div class='tagCloud' style='margin-top:12px'>" +
        GRAPH_NODE_TYPES.map(([type]) => "<span class='badge'>" + escapeHtml(type) + "</span>").join("") +
      "</div>";
    ins.innerHTML =
      "<div class='badge accent'>Registry</div><h3 style='margin:10px 0 4px'>Типы нод</h3>" +
      "<div class='notice'>Расширение не требует изменения канонической модели существующих нод.</div>";
  }

  function getGraphNodePosition(node) {
    return graphPreviewPositions.get(node.nodeId) || { x: node.x, y: node.y };
  }

  function clientToGraph(svg, clientX, clientY) {
    const ctm = svg.getScreenCTM?.();
    if (ctm?.inverse) {
      const point = new DOMPoint(clientX, clientY).matrixTransform(ctm.inverse());
      return { x: point.x, y: point.y };
    }

    // Fallback for environments without SVG CTM support.
    // Повторяет preserveAspectRatio="xMidYMid meet": viewBox вписывается с
    // сохранением пропорций и центрируется, поэтому по одной оси возникают поля.
    // Делить на rect.width/rect.height здесь нельзя — это верно только при
    // совпадении аспектов, иначе координата уезжает по оси с полями.
    const rect = svg.getBoundingClientRect();
    const viewBox = svg.viewBox.baseVal;
    const scale = Math.min(rect.width / viewBox.width, rect.height / viewBox.height);
    if (!(scale > 0)) {
      return { x: viewBox.x, y: viewBox.y };
    }

    const offsetX = rect.left + (rect.width - viewBox.width * scale) / 2;
    const offsetY = rect.top + (rect.height - viewBox.height * scale) / 2;
    return {
      x: viewBox.x + (clientX - offsetX) / scale,
      y: viewBox.y + (clientY - offsetY) / scale
    };
  }

  let activeGraphContextMenu = null;

  /**
   * Меню последних открытых файлов.
   *
   * Список отсортирован Host'ом (свежие сверху). Текущий файл помечается
   * галочкой и остаётся кликабельным: повторное открытие уже открытого
   * документа безопасно — Host не перезагружает его.
   */
  function openRecentFilesMenu(anchor, paths, currentPath) {
    closeRecentFilesMenu();

    if (!Array.isArray(paths) || !paths.length) return;

    const menu = document.createElement("div");
    menu.className = "graphContextMenu recentFilesMenu";

    const heading = document.createElement("div");
    heading.className = "graphContextMenuTitle";
    heading.textContent = "Последние файлы";
    menu.appendChild(heading);

    paths.forEach(path => {
      const isCurrent =
        currentPath &&
        path.toLowerCase() === String(currentPath).toLowerCase();

      const button = graphContextMenuButton(
        (isCurrent ? "● " : "") + recentFileLabel(path),
        () => {
          closeRecentFilesMenu();
          if (isCurrent) return;
          send({ action: "graph_open_recent", path });
        }
      );
      button.title = path;
      if (isCurrent) button.dataset.current = "true";
      menu.appendChild(button);
    });

    const rect = anchor.getBoundingClientRect();
    placeGraphContextMenu(menu, rect.left, rect.bottom + 4);
    activeRecentMenu = menu;
  }

  /** Показывает только имя файла: полный путь виден в подсказке. */
  function recentFileLabel(path) {
    const normalized = String(path ?? "").replace(/\\/g, "/");
    const name = normalized.slice(normalized.lastIndexOf("/") + 1);
    return name || String(path ?? "");
  }

  function closeRecentFilesMenu() {
    activeRecentMenu?.remove();
    activeRecentMenu = null;
  }

  function queueDirtyNode(nodeId) {
    if (nodeId) graphDirtyNodeIds.add(nodeId);
  }

  function queueDirtyNodes(...nodeIds) {
    nodeIds.filter(Boolean).forEach(nodeId => graphDirtyNodeIds.add(nodeId));
  }

  function closeGraphContextMenu() {
    activeGraphContextMenu?.remove();
    activeGraphContextMenu = null;
  }

  function placeGraphContextMenu(menu, clientX, clientY) {
    document.body.appendChild(menu);
    activeGraphContextMenu = menu;

    const clampToViewport = () => {
      const viewportWidth = Math.max(1, window.innerWidth);
      const viewportHeight = Math.max(1, window.innerHeight);
      const maxHeight = Math.max(100, viewportHeight - 12);

      menu.style.maxHeight = maxHeight + "px";
      menu.style.overflowY = "auto";
      menu.style.animation = "none";

      // Use an explicit border-box height. max-height alone can still leave
      // the browser's computed box affected by content/padding during layout.
      const contentHeight = menu.scrollHeight;
      const menuHeight = Math.min(Math.max(1, contentHeight), maxHeight);
      menu.style.height = menuHeight + "px";

      const rect = menu.getBoundingClientRect();
      const left = clamp(
        clientX,
        6,
        Math.max(6, viewportWidth - rect.width - 6)
      );
      const top = clamp(
        clientY,
        6,
        Math.max(6, viewportHeight - rect.height - 6)
      );

      menu.style.left = left + "px";
      menu.style.top = top + "px";

      // Clamp once more using the final rendered rectangle.
      const placedRect = menu.getBoundingClientRect();
      menu.style.left = clamp(
        placedRect.left,
        6,
        Math.max(6, viewportWidth - placedRect.width - 6)
      ) + "px";
      menu.style.top = clamp(
        placedRect.top,
        6,
        Math.max(6, viewportHeight - placedRect.height - 6)
      ) + "px";
    };

    clampToViewport();

    menu.addEventListener("wheel", event => {
      if (menu.scrollHeight > menu.clientHeight) {
        menu.scrollTop += event.deltaY;
        event.preventDefault();
      }
      event.stopPropagation();
    }, { passive: false });
  }

  function graphContextMenuButton(text, handler) {
    const button = document.createElement("button");
    button.type = "button";
    button.className = "graphContextMenuItem";
    button.textContent = text;
    button.addEventListener("click", event => {
      event.preventDefault();
      event.stopPropagation();
      handler();
    });
    return button;
  }

  function openGraphAddContextMenu(svg, clientX, clientY, graphPoint) {
    closeGraphContextMenu();

    const menu = document.createElement("div");
    menu.className = "graphContextMenu";
    menu.dataset.menuKind = "add";

    const heading = document.createElement("div");
    heading.className = "graphContextMenuTitle";
    heading.textContent = "Add";
    menu.appendChild(heading);

    if (pendingOutput) {
      const hint = document.createElement("div");
      hint.className = "graphContextMenuHint";
      hint.textContent = "Новая нода будет подключена к выбранному Output.";
      menu.appendChild(hint);
    }

    GRAPH_NODE_TYPES.forEach(([type, label]) => {
      const button = graphContextMenuButton(label + " (" + type + ")", () => {
        const connectionSource = pendingOutput
          ? {
              connectFromNodeId: pendingOutput.nodeId,
              connectFromSocketId: pendingOutput.socketId
            }
          : {};

        if (pendingOutput) {
          queueDirtyNodes(pendingOutput.nodeId);
        }

        send({
          action: "graph_add_node",
          nodeType: type,
          title: type,
          x: Math.round(graphPoint.x),
          y: Math.round(graphPoint.y),
          ...connectionSource
        });

        pendingOutput = null;
        pendingConnectionPoint = null;
        closeGraphContextMenu();
      });
      button.dataset.nodeType = type;
      if (pendingOutput && type === "Start") button.disabled = true;
      menu.appendChild(button);
    });

    placeGraphContextMenu(menu, clientX, clientY);
  }

  function openGraphNodeContextMenu(svg, clientX, clientY, nodeId) {
    closeGraphContextMenu();

    const node = questGraph?.nodes?.find(item => item.nodeId === nodeId);
    if (!node) return;

    const menu = document.createElement("div");
    menu.className = "graphContextMenu";
    menu.dataset.menuKind = "node";

    const heading = document.createElement("div");
    heading.className = "graphContextMenuTitle";
    heading.textContent = "Нода";
    menu.appendChild(heading);

    const name = document.createElement("div");
    name.className = "graphContextMenuHint";
    name.textContent = node.title + " · " + node.nodeType;
    menu.appendChild(name);

    menu.appendChild(graphContextMenuButton("Сохранить", () => {
      send({ action: "graph_save" });
      closeGraphContextMenu();
    }));

    menu.appendChild(graphContextMenuButton("Удалить", () => {
      requestGraphNodeDelete(nodeId);
      closeGraphContextMenu();
    }));

    placeGraphContextMenu(menu, clientX, clientY);
  }

  function openGraphConnectionContextMenu(svg, clientX, clientY, nodeId, socketId) {
    closeGraphContextMenu();

    const node = questGraph?.nodes?.find(item => item.nodeId === nodeId);
    const socket = node?.sockets?.find(item => item.socketId === socketId);
    if (!node || !socket) return;

    const connections = questGraph.connections.filter(connection =>
      (connection.fromNodeId === nodeId && connection.fromSocketId === socketId) ||
      (connection.toNodeId === nodeId && connection.toSocketId === socketId)
    );

    const menu = document.createElement("div");
    menu.className = "graphContextMenu";
    menu.dataset.menuKind = "connection";

    const heading = document.createElement("div");
    heading.className = "graphContextMenuTitle";
    heading.textContent = "Коннектор";
    menu.appendChild(heading);

    const hint = document.createElement("div");
    hint.className = "graphContextMenuHint";
    hint.textContent = socket.name + " · " + socket.direction;
    menu.appendChild(hint);

    if (!connections.length) {
      const empty = document.createElement("div");
      empty.className = "graphContextMenuHint";
      empty.textContent = "Соединений нет.";
      menu.appendChild(empty);
    } else {
      connections.forEach(connection => {
        const source = connection.fromNodeId + ":" + connection.fromSocketId;
        const target = connection.toNodeId + ":" + connection.toSocketId;
        menu.appendChild(graphContextMenuButton(
          "Разорвать соединение: " + source + " → " + target,
          () => {
            queueDirtyNodes(connection.fromNodeId, connection.toNodeId);
            send({
              action: "graph_disconnect",
              fromNodeId: connection.fromNodeId,
              fromSocketId: connection.fromSocketId,
              toNodeId: connection.toNodeId,
              toSocketId: connection.toSocketId
            });
            closeGraphContextMenu();
          }
        ));
      });
    }

    if (pendingOutput) {
      menu.appendChild(graphContextMenuButton("Отменить выбор Output", () => {
        pendingOutput = null;
        pendingConnectionPoint = null;
        const graphSvg = document.getElementById("questGraphSvg");
        if (graphSvg) refreshGraphEdges(graphSvg);
        closeGraphContextMenu();
      }));
    }

    placeGraphContextMenu(menu, clientX, clientY);
  }

  function requestGraphNodeDelete(nodeId) {
    const node = questGraph?.nodes?.find(item => item.nodeId === nodeId);
    if (!node) return false;

    const relatedNodeIds = questGraph.connections
      .filter(connection =>
        connection.fromNodeId === nodeId || connection.toNodeId === nodeId
      )
      .flatMap(connection => [connection.fromNodeId, connection.toNodeId])
      .filter(relatedNodeId => relatedNodeId !== nodeId);

    const confirmed = window.confirm(
      "Удалить ноду «" + node.title + "» (" + node.nodeType + ")?\n\n" +
      "Все связанные с ней соединения также будут удалены."
    );
    if (!confirmed) return false;

    selectedGraphNodeId = null;
    if (pendingOutput?.nodeId === nodeId) {
      pendingOutput = null;
      pendingConnectionPoint = null;
    }

    queueDirtyNodes(...relatedNodeIds);
    send({ action: "graph_remove_node", nodeId });
    const graphSvg = document.getElementById("questGraphSvg");
    if (graphSvg) refreshGraphEdges(graphSvg);
    return true;
  }

  function refreshGraphEdges(svg) {
    const edgeGroup = svg.querySelector("#questGraphEdges");
    if (edgeGroup) {
      edgeGroup.innerHTML = graphEdges();
    }
    updateGraphVisuals();
  }

  function escapeCssAttribute(value) {
    return String(value ?? "").replace(/\\/g, "\\\\").replace(/'/g, "\\'");
  }

  function clamp(value, min, max) {
    return Math.min(max, Math.max(min, value));
  }

  function formatPosition(position) {
    return "X " + Math.round(position.x) + " · Y " + Math.round(position.y) + " · Z " + Math.round(position.z);
  }

  function escapeHtml(value) {
    return String(value ?? "").replace(/[&<>"']/g, char => ({
      "&": "&amp;",
      "<": "&lt;",
      ">": "&gt;",
      "\"": "&quot;",
      "'": "&#39;"
    }[char]));
  }

  const webview = window.chrome?.webview;
  const handleWebviewMessage = event => {
    const data = typeof event.data === "string" ? JSON.parse(event.data) : event.data;

    if (data?.type === "scene_catalog") {
      sceneCatalog = Array.isArray(data.scenes) ? data.scenes : [];
      if (currentId() === "graph") render();
      return;
    }
    if (data?.type === "quest_graph") {
      questGraph = data.graph;
      graphDocument = {
        path: data.documentPath || "",
        lastPath: data.lastDocumentPath || "",
        dirty: Boolean(data.documentDirty)
      };
      graphPreviewPositions.clear();
      graphHistory = {
        canUndo: Boolean(data.canUndo),
        canRedo: Boolean(data.canRedo)
      };
      graphValidation = Array.isArray(data.validation) ? data.validation : [];
      // Host снимает dirty после save/new/open, поэтому маркеры изменённых
      // нод нужно очищать здесь: иначе звёздочки остаются навсегда и Set растёт.
      if (!graphDocument.dirty) {
        graphDirtyNodeIds.clear();
      }
      if (data.selectedNodeId) selectedGraphNodeId = data.selectedNodeId;
      if (selectedGraphNodeId && !questGraph.nodes.some(node => node.nodeId === selectedGraphNodeId)) {
        selectedGraphNodeId = questGraph.nodes[0]?.nodeId || null;
      }
      if (!selectedGraphNodeId && questGraph.nodes.length) selectedGraphNodeId = questGraph.nodes[0].nodeId;
      if (currentId() === "graph") render();

      // После перестроения сразу показываем граф целиком: иначе новая раскладка
      // может оказаться за пределами текущего viewBox.
      if (pendingGraphFit) {
        pendingGraphFit = false;
        fitGraph();
      }
      return;
    }

    if (data?.type === "recent_files") {
      if (data.kind !== "quest") return;
      recentQuestFiles = Array.isArray(data.paths) ? data.paths : [];
      recentQuestCurrentPath = data.currentPath || "";

      // Список мог измениться уже после открытия меню — закрываем, чтобы
      // пользователь не кликнул по устаревшему составу.
      closeRecentFilesMenu();

      // Кнопка становится доступной/недоступной вместе со списком.
      const button = document.getElementById("openRecentGraph");
      if (button) button.disabled = recentQuestFiles.length === 0;
      return;
    }

    if (data?.type === "host_error") {
      window.alert(data.message || "Ошибка редактора.");
      return;
    }

    if (data?.type === "runtime_state") {
      runtimeState = data.runtime || { status: "Stopped", currentNodeId: null };
      executedNodeIds = new Set(Array.isArray(data.executedNodeIds) ? data.executedNodeIds : []);
      if (currentId() === "graph") updateGraphVisuals();
      return;
    }

    if (data?.type === "simulator_context") {
      simulatorContext = data;
      if (currentId() === "world") {
        render();
      }
      return;
    }

    if (data?.type === "coordinate") {
      const position = data.position;
      if (position) {
        const x = document.getElementById("worldX");
        const y = document.getElementById("worldY");
        const z = document.getElementById("worldZ");
        if (x && y && z) {
          x.value = position.x;
          y.value = position.y;
          z.value = position.z;
          inspector.innerHTML =
            "<div class='badge blue'>Координаты получены</div>" +
            "<h3 style='margin:10px 0 4px'>" + escapeHtml(data.source) + "</h3>" +
            "<div class='kv'><span>X</span><span>" + position.x + "</span></div>" +
            "<div class='kv'><span>Y</span><span>" + position.y + "</span></div>" +
            "<div class='kv'><span>Z</span><span>" + position.z + "</span></div>";
        }
      }
      return;
    }

    if (data?.type === "coordinate_error") {
      window.alert(data.message || "Координаты не получены.");
    }
  };

  window.addEventListener("message", handleWebviewMessage);

  if (typeof webview?.addEventListener === "function") {
    webview.addEventListener("message", handleWebviewMessage);
  }

  document.addEventListener("pointerdown", event => {
    if (activeGraphContextMenu && !activeGraphContextMenu.contains(event.target)) {
      closeGraphContextMenu();
    }

    if (activeRecentMenu && !activeRecentMenu.contains(event.target)) {
      closeRecentFilesMenu();
    }
  });

  document.addEventListener("keydown", event => {
    if (event.key === "Escape" && activeGraphContextMenu) {
      closeGraphContextMenu();
      event.preventDefault();
      return;
    }

    if (event.key === "Escape" && pendingOutput) {
      pendingOutput = null;
      pendingConnectionPoint = null;
      const svg = document.getElementById("questGraphSvg");
      if (svg) refreshGraphEdges(svg);
      event.preventDefault();
      return;
    }


    if (event.code === "Space") {
      graphSpaceDown = true;
      if (currentId() === "graph") event.preventDefault();
    }

    if (
      currentId() === "graph" &&
      event.key === "Delete" &&
      !isTextEntryTarget(event.target) &&
      selectedGraphNodeId
    ) {
      if (requestGraphNodeDelete(selectedGraphNodeId)) {
        event.preventDefault();
      }
      return;
    }

    if (currentId() !== "graph" || !(event.ctrlKey || event.metaKey)) return;
    // Undo/redo не должны зависеть от раскладки: `event.key` отдаёт символ
    // текущего языка (в русской ЙЦУКЕН Ctrl+Z даёт «я», Ctrl+Y — «н»),
    // поэтому опираемся на `event.code` физической клавиши.
    const code = event.code;
    const key = event.key.toLowerCase();
    const isUndo = code === "KeyZ" || (!code && key === "z");
    const isRedo = code === "KeyY" || (!code && key === "y");
    if (isUndo && !event.shiftKey) {
      event.preventDefault();
      if (graphHistory.canUndo) send({ action: "graph_undo" });
    } else if (isUndo && event.shiftKey) {
      event.preventDefault();
      if (graphHistory.canRedo) send({ action: "graph_redo" });
    } else if (isRedo) {
      event.preventDefault();
      if (graphHistory.canRedo) send({ action: "graph_redo" });
    }
  });

  document.addEventListener("keyup", event => {
    if (event.code === "Space") graphSpaceDown = false;
  });

  window.addEventListener("blur", () => {
    graphSpaceDown = false;
    closeGraphContextMenu();
  });

  window.addEventListener("hashchange", render);
  render();
  window.__assistQuestGraphRuntime = {
    getState: () => runtimeState,
    getExecuted: () => [...executedNodeIds],
    getPendingOutput: () => pendingOutput ? { ...pendingOutput } : null
  };
})();
