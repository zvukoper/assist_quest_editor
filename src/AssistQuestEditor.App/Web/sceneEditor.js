(() => {
  const SCENE_NODE_WIDTH = 260;
  const SCENE_NODE_INPUT_ZONE = 62;
  const SCENE_NODE_OUTPUT_ZONE = 62;
  const SCENE_NODE_CENTER_X =
    SCENE_NODE_INPUT_ZONE +
    (SCENE_NODE_WIDTH - SCENE_NODE_INPUT_ZONE - SCENE_NODE_OUTPUT_ZONE) / 2;

  const SCENE_NODE_TYPES = [["SceneStart","Начало сцены"],["Dialogue","Диалог"],["Choice","Выбор"],["SceneWait","Ожидание"],["SceneEvent","Событие"],["SceneEnd","Конец сцены"]];

  const send = payload => {
    if (typeof window.__assistSend === "function") {
      window.__assistSend(payload);
      return;
    }
    window.chrome?.webview?.postMessage(payload);
  };

  let sceneGraph = null;
  let selectedSceneNodeId = null;
  let pendingOutput = null;
  let sceneHistory = { canUndo: false, canRedo: false };
  let sceneValidation = [];
  let scenePreviewPositions = new Map();
  let sceneViewport = { x: 0, y: 0, width: 1200, height: 720 };
  let sceneDocument = { path: "", lastPath: "", dirty: false };
  let sceneNavigationBack = null;
  let sceneDirtyNodeIds = new Set();
  let sceneSpaceDown = false;
  let pendingConnectionPoint = null;
  let runtimeState = { status: "Stopped", currentNodeId: null };
  let executedNodeIds = new Set();
  let pendingSceneFit = false;
  let activeSceneContextMenu = null;

  // История последних открытых сцен. Приходит от Host сообщением recent_files.
  let recentSceneFiles = [];
  let recentSceneCurrentPath = "";
  let activeRecentSceneMenu = null;

  const sceneDefinition = () => window.__assistSceneEditorState?.definition || null;

  function renderScene(ws, ins) {
    if (!sceneGraph) {
      ws.innerHTML = "<div class='notice'>Ожидание Scene Definition от Host…</div>";
      ins.innerHTML = "";
      return;
    }

    const errors = sceneValidation.filter(item => item.severity === "Error");
    const warnings = sceneValidation.filter(item => item.severity === "Warning");

    ws.innerHTML =
      "<div class='toolbar' style='margin-bottom:10px;flex-wrap:wrap'>" +
        "<button class='toolButton' id='newScene'>Новая</button>" +
        "<button class='toolButton' id='openScene'>Открыть</button>" +
        "<button class='toolButton' id='openRecentScene' " + (recentSceneFiles.length ? "" : "disabled") +
          " title='Последние открытые сцены'>Последние ▾</button>" +
        "<button class='toolButton' id='openLastScene' " + (sceneDocument.lastPath ? "" : "disabled") + ">Последняя сцена</button>" +
        (sceneNavigationBack
          ? "<button class='toolButton' id='sceneBackToQuest' title='Вернуться к исходной ноде Quest Graph'>↩ Вернуться в Quest Graph</button>"
          : "") +
        "<button class='toolButton primary' id='saveScene'>Сохранить</button>" +
        "<button class='toolButton' id='saveSceneAs'>Сохранить как…</button>" +
        "<span class='badge " + (sceneDocument.dirty ? "accent" : "blue") + "'>" +
          escapeHtml(sceneDocument.dirty ? "Не сохранено" : "Сохранено") +
        "</span>" +
        "<span class='badge'>" + escapeHtml(sceneDocument.path ? sceneDocument.path : "Новый документ") + "</span>" +
        "<select id='sceneResource' class='toolButton'>" +
          sceneCatalog.map(item =>
            "<option value='" + escapeHtml(item.id) + "'" +
            (item.id === sceneGraph.id ? " selected" : "") + ">" +
            escapeHtml(item.title) + "</option>"
          ).join("") +
        "</select>" +
        "<select id='sceneNodeType' class='toolButton'>" +
          SCENE_NODE_TYPES.map(([type, label]) =>
            "<option value='" + escapeHtml(type) + "'>" +
            escapeHtml(label) + " (" + escapeHtml(type) + ")</option>"
          ).join("") +
        "</select>" +
        "<input id='sceneNodeTitle' class='toolButton' style='width:190px' placeholder='Название новой ноды'>" +
        "<button class='toolButton primary' id='addSceneNode'>Добавить ноду</button>" +
        "<button class='toolButton' id='layoutScene' title='Выстроить ноды по слоям без наложений'>Перестроить</button>" +
        "<button class='toolButton' id='fitScene'>По размеру</button>" +
        "<button class='toolButton' id='undoScene' " + (sceneHistory.canUndo ? "" : "disabled") + ">↶ Отменить</button>" +
        "<button class='toolButton' id='redoScene' " + (sceneHistory.canRedo ? "" : "disabled") + ">↷ Повторить</button>" +
        "<button class='toolButton' id='validateScene'>Проверить</button>" +
        "<span class='badge accent'>" + sceneGraph.nodes.length + " нод</span>" +
        "<span class='badge'>" + sceneGraph.connections.length + " связей</span>" +
        "<span class='badge red'>" + escapeHtml(sceneGraph.name) + "</span>" +
        "<span class='badge " + (errors.length ? "red" : "blue") + "'>" + errors.length + " ошибок</span>" +
        "<span class='badge " + (warnings.length ? "accent" : "blue") + "'>" + warnings.length + " предупреждений</span>" +
      "</div>" +
      (sceneNavigationBack
        ? "<div class='card' style='margin-bottom:10px;padding:9px 12px;border:1px solid var(--accent)'><strong>Контекст перехода:</strong> " +
            escapeHtml(sceneNavigationBack.nodeTitle || sceneNavigationBack.nodeId) +
            " · NodeId " + escapeHtml(sceneNavigationBack.nodeId) +
          "</div>"
        : "") +
      "<div style='height:calc(100% - 46px);min-height:560px;border:1px solid var(--border);border-radius:8px;overflow:hidden;background:#111419'>" +
        "<svg id='sceneGraphSvg' viewBox='" + sceneViewport.x + " " + sceneViewport.y + " " + sceneViewport.width + " " + sceneViewport.height + "' xmlns='http://www.w3.org/2000/svg' style='width:100%;height:100%'>" +
          "<g id='sceneGraphEdges'>" + sceneEdges() + "</g>" +
          "<g id='sceneGraphNodes'>" + sceneGraph.nodes.map(sceneNodeMarkup).join("") + "</g>" +
          "<g id='sceneGraphConnectionPreview'></g>" +
        "</svg>" +
      "</div>";

    ws.querySelector("#newScene").addEventListener("click", () => send({ action: "scene_new" }));
    ws.querySelector("#openScene").addEventListener("click", () => send({ action: "scene_open" }));
    ws.querySelector("#openRecentScene")?.addEventListener("click", event => {
      openRecentSceneFilesMenu(event.currentTarget, recentSceneFiles, recentSceneCurrentPath);
    });
    ws.querySelector("#openLastScene")?.addEventListener("click", () => send({ action: "scene_open_last" }));
    ws.querySelector("#sceneBackToQuest")?.addEventListener("click", () => {
      if (!sceneNavigationBack?.nodeId) return;
      send({
        action: "navigate_to_quest_node",
        nodeId: sceneNavigationBack.nodeId,
        questId: sceneNavigationBack.questId || "",
        questPath: sceneNavigationBack.questPath || ""
      });
    });
    ws.querySelector("#saveScene").addEventListener("click", () => send({ action: "scene_save" }));
    ws.querySelector("#saveSceneAs").addEventListener("click", () => send({ action: "scene_save_as" }));
    ws.querySelector("#sceneResource").addEventListener("change", event => {
      send({ action: "scene_open_resource", sceneId: event.target.value });
    });
    ws.querySelector("#addSceneNode").addEventListener("click", () => {
      const type = ws.querySelector("#sceneNodeType").value;
      const title = ws.querySelector("#sceneNodeTitle").value.trim() || type;
      send({ action: "scene_add_node", nodeType: type, title, x: 420, y: 120 });
    });
    ws.querySelector("#fitScene").addEventListener("click", fitScene);
    ws.querySelector("#layoutScene").addEventListener("click", () => {
      if (!sceneGraph?.nodes?.length) return;
      pendingSceneFit = true;
      scenePreviewPositions.clear();
      send({ action: "scene_layout" });
    });
    ws.querySelector("#undoScene").addEventListener("click", () => send({ action: "scene_undo" }));
    ws.querySelector("#redoScene").addEventListener("click", () => send({ action: "scene_redo" }));
    ws.querySelector("#validateScene").addEventListener("click", () => send({ action: "scene_validate" }));

    bindSceneInteractions(ws.querySelector("#sceneGraphSvg"));
    updateSceneInspector(ins);
  }

  function fitScene() {
    fitSceneGraph();
  }

  function sceneNodeHeight(node) {
    const inputs = node.sockets.filter(socket => socket.direction === "Input").length;
    const outputs = node.sockets.filter(socket => socket.direction === "Output").length;
    return Math.max(110, 78 + Math.max(inputs, outputs) * 22);
  }

  function sceneEdges() {
    return sceneGraph.connections.map(connection => {
      const from = sceneGraph.nodes.find(node => node.nodeId === connection.fromNodeId);
      const to = sceneGraph.nodes.find(node => node.nodeId === connection.toNodeId);
      if (!from || !to) return "";

      const fromSocket = from.sockets.find(socket => socket.socketId === connection.fromSocketId);
      const toSocket = to.sockets.find(socket => socket.socketId === connection.toSocketId);
      if (!fromSocket || !toSocket) return "";

      const outputs = from.sockets.filter(socket => socket.direction === "Output");
      const inputs = to.sockets.filter(socket => socket.direction === "Input");
      const fromIndex = outputs.findIndex(socket => socket.socketId === fromSocket.socketId);
      const toIndex = inputs.findIndex(socket => socket.socketId === toSocket.socketId);
      const fromPosition = getSceneNodePosition(from);
      const toPosition = getSceneNodePosition(to);
      const startX = fromPosition.x + SCENE_NODE_WIDTH;
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

  function sceneNodeMarkup(node) {
    const selected = node.nodeId === selectedSceneNodeId ? " selected" : "";
    const executed = executedNodeIds.has(node.nodeId) ? " executed" : "";
    const active = node.nodeId === runtimeState.currentNodeId &&
      ["Running", "Waiting"].includes(runtimeStatusName(runtimeState.status)) ? " runtime-active" : "";
    const source = pendingOutput?.nodeId === node.nodeId ? " connection-source" : "";
    const dirty = sceneDirtyNodeIds.has(node.nodeId) ? " dirty" : "";
    const inputs = node.sockets.filter(socket => socket.direction === "Input");
    const outputs = node.sockets.filter(socket => socket.direction === "Output");

    const position = getSceneNodePosition(node);
    const nodeHeight = sceneNodeHeight(node);
    const title = fitNodeText(node.title, 21);
    const nodeType = fitNodeText(node.nodeType, 18);
    const nodeId = fitNodeText(node.nodeId, 21);
    const centerWidth = SCENE_NODE_WIDTH - SCENE_NODE_INPUT_ZONE - SCENE_NODE_OUTPUT_ZONE;

    return "<g class='node" + selected + executed + active + source + dirty + "' data-node-id='" + escapeHtml(node.nodeId) + "' transform='translate(" + position.x + " " + position.y + ")'>" +
      "<rect class='nodeRect' rx='8' width='" + SCENE_NODE_WIDTH + "' height='" + nodeHeight + "'></rect>" +
      "<rect class='nodeConnectorZone inputZone' x='1' y='1' width='" + (SCENE_NODE_INPUT_ZONE - 1) + "' height='" + (nodeHeight - 2) + "' rx='7'></rect>" +
      "<rect class='nodeConnectorZone outputZone' x='" + SCENE_NODE_OUTPUT_ZONE + "' y='1' width='" + (SCENE_NODE_OUTPUT_ZONE - 1) + "' height='" + (nodeHeight - 2) + "' rx='7'></rect>" +
      "<line class='nodeConnectorSeparator' x1='" + SCENE_NODE_INPUT_ZONE + "' y1='6' x2='" + SCENE_NODE_INPUT_ZONE + "' y2='" + (nodeHeight - 6) + "'></line>" +
      "<line class='nodeConnectorSeparator' x1='" + (SCENE_NODE_WIDTH - SCENE_NODE_OUTPUT_ZONE) + "' y1='6' x2='" + (SCENE_NODE_WIDTH - SCENE_NODE_OUTPUT_ZONE) + "' y2='" + (nodeHeight - 6) + "'></line>" +
      "<text class='nodeDirtyMark' x='" + (SCENE_NODE_WIDTH / 2) + "' y='15' text-anchor='middle'>★</text>" +
      "<text class='nodeTitle' x='" + (SCENE_NODE_INPUT_ZONE + centerWidth / 2) + "' y='27' text-anchor='middle'>" + escapeHtml(nodeType) + "</text>" +
      "<text class='nodeExecutedMark' x='" + (SCENE_NODE_WIDTH - SCENE_NODE_OUTPUT_ZONE - 10) + "' y='27' text-anchor='middle'>✓</text>" +
      "<text class='nodeLabel' x='" + (SCENE_NODE_INPUT_ZONE + centerWidth / 2) + "' y='49' text-anchor='middle'>" + escapeHtml(title) + "</text>" +
      "<text class='nodeId' x='" + (SCENE_NODE_INPUT_ZONE + centerWidth / 2) + "' y='" + (nodeHeight - 13) + "' text-anchor='middle'>" + escapeHtml(nodeId) + "</text>" +
      inputs.map((socket, index) =>
        "<g class='socketGroup' data-socket-direction='Input' data-socket-id='" + escapeHtml(socket.socketId) + "'>" +
          "<rect class='socketHitArea inputHitArea' x='-8' y='" + (10 + index * 22) + "' width='" + (SCENE_NODE_INPUT_ZONE + 2) + "' height='24' rx='8'></rect>" +
          "<circle class='socket input' cx='0' cy='" + (22 + index * 22) + "' r='6'></circle>" +
          "<text class='socketLabel inputLabel' x='10' y='" + (26 + index * 22) + "'>" + escapeHtml(fitNodeText(socket.name, 9)) + "</text>" +
        "</g>"
      ).join("") +
      outputs.map((socket, index) =>
        "<g class='socketGroup' data-socket-direction='Output' data-socket-id='" + escapeHtml(socket.socketId) + "'>" +
          "<rect class='socketHitArea outputHitArea' x='" + (SCENE_NODE_WIDTH - SCENE_NODE_OUTPUT_ZONE - 2) + "' y='" + (10 + index * 22) + "' width='" + (SCENE_NODE_OUTPUT_ZONE + 10) + "' height='24' rx='8'></rect>" +
          "<circle class='socket output' cx='" + SCENE_NODE_WIDTH + "' cy='" + (22 + index * 22) + "' r='6'></circle>" +
          "<text class='socketLabel outputLabel' x='" + (SCENE_NODE_WIDTH - 10) + "' y='" + (26 + index * 22) + "' text-anchor='end'>" + escapeHtml(fitNodeText(socket.name, 9)) + "</text>" +
        "</g>"
      ).join("") +
    "</g>";
  }

  function fitNodeText(value, maxChars) {
    const text = String(value ?? "");
    if (text.length <= maxChars) return text;
    return text.slice(0, Math.max(1, maxChars - 1)) + "…";
  }

  function bindSceneInteractions(svg) {
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
        // Класс compatible также выставляется в updateSceneVisuals по общему
        // правилу совместимости, поэтому здесь его не трогаем.
      });
    };

    const setViewBox = () => {
      svg.setAttribute("viewBox", sceneViewport.x + " " + sceneViewport.y + " " + sceneViewport.width + " " + sceneViewport.height);
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
      // reserved for the empty sceneGraph background.
      if (socket || node) return;

      event.preventDefault();
      event.stopPropagation();
      const sceneGraphPoint = clientToSceneGraph(svg, event.clientX, event.clientY);
      openSceneAddContextMenu(svg, event.clientX, event.clientY, sceneGraphPoint);
    });

    svg.addEventListener("pointerdown", event => {
      const wantsPan = event.button === 1 || (event.button === 0 && sceneSpaceDown);
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
        refreshSceneEdges(svg);
        updateSceneVisuals();
      }

      // Нажатие на Output начинает протяжку кабеля.
      //
      // Только `click` недостаточно: если нажать на Output, протянуть мышь и
      // отпустить над Input, браузер присылает click общему предку точки
      // нажатия и отпускания, а не сокетам, поэтому связь не создавалась.
      if (hitSocket && hitSocket.direction === "Output") {
        pendingOutput = { nodeId: hitSocket.nodeId, socketId: hitSocket.socketId };
        pendingConnectionPoint = clientToSceneGraph(svg, event.clientX, event.clientY);
        selectedSceneNodeId = hitSocket.nodeId;

        // Захват указателя: иначе pointerup потеряется, если отпустить за
        // пределами канваса, и кабель останется висеть незавершённым.
        try {
          svg.setPointerCapture?.(event.pointerId);
        } catch {
          // Синтетические указатели могут не захватываться.
        }

        updateSceneVisuals();
        refreshSceneEdges(svg);
        updateSceneInspector(document.getElementById("inspector"));
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
      const sourceNode = sceneGraph?.nodes?.find(item => item.nodeId === nodeId);
      if (!sourceNode) return;

      const position = getSceneNodePosition(sourceNode);
      const sceneGraphPoint = clientToSceneGraph(svg, event.clientX, event.clientY);

      selectedSceneNodeId = nodeId;
      updateSceneVisuals();
      updateSceneInspector(document.getElementById("inspector"));

      dragState = {
        nodeId,
        startPointerX: event.clientX,
        startPointerY: event.clientY,
        pointerOffsetX: sceneGraphPoint.x - position.x,
        pointerOffsetY: sceneGraphPoint.y - position.y,
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
        // преобразования экран → граф (clientToSceneGraph опирается на CTM),
        // а не из отношения viewBox.width / rect.width.
        //
        // Последнее верно только при совпадении аспекта канваса и viewBox.
        // Иначе SVG с preserveAspectRatio по умолчанию («xMidYMid meet»)
        // вписывает viewBox с полями, масштаб по осям перестаёт выражаться
        // этими отношениями, и pan «разбегается» по оси с полями.
        const graphPointer = clientToSceneGraph(svg, event.clientX, event.clientY);
        const graphPrevious = clientToSceneGraph(svg, panState.lastX, panState.lastY);
        sceneViewport.x -= graphPointer.x - graphPrevious.x;
        sceneViewport.y -= graphPointer.y - graphPrevious.y;
        panState.lastX = event.clientX;
        panState.lastY = event.clientY;
        setViewBox();
        refreshSceneEdges(svg);
        event.preventDefault();
        return;
      }

      if (pendingOutput) {
        // Снап: конец кабеля притягивается к сокету под курсором.
        // Так видно, что именно этот сокет станет целью соединения.
        const snapped = socketAtPointer(event.clientX, event.clientY, SOCKET_SNAP_RADIUS);
        if (snapped && snapped.direction === "Input") {
          const point = sceneSocketPoint(snapped.nodeId, snapped.socketId);
          if (point) {
            pendingConnectionPoint = point;
          } else {
            pendingConnectionPoint = clientToSceneGraph(svg, event.clientX, event.clientY);
          }
        } else {
          pendingConnectionPoint = clientToSceneGraph(svg, event.clientX, event.clientY);
        }

        refreshSceneEdges(svg);
        updateSceneVisuals();
      }

      // Подсветка сокета под курсором — работает и без выбранного Output,
      // чтобы цель была видна заранее.
      updateSocketHover(event.clientX, event.clientY);

      if (!dragState || event.pointerId !== dragState.pointerId) return;

      const movedDistance = Math.hypot(
        event.clientX - dragState.startPointerX,
        event.clientY - dragState.startPointerY
      );

      const sceneGraphPoint = clientToSceneGraph(svg, event.clientX, event.clientY);
      const x = Math.round(sceneGraphPoint.x - dragState.pointerOffsetX);
      const y = Math.round(sceneGraphPoint.y - dragState.pointerOffsetY);

      if (movedDistance >= 2) {
        dragState.moved = true;
      }

      if (!dragState.moved) return;

      scenePreviewPositions.set(dragState.nodeId, { x, y });

      const nodeElement = svg.querySelector(
        ".node[data-node-id='" + escapeCssAttribute(dragState.nodeId) + "']"
      );
      if (nodeElement) {
        nodeElement.setAttribute("transform", "translate(" + x + " " + y + ")");
      }

      refreshSceneEdges(svg);
      updateSceneInspector(document.getElementById("inspector"));
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

      const preview = scenePreviewPositions.get(finished.nodeId);
      scenePreviewPositions.delete(finished.nodeId);

      if (finished.moved && preview) {
        const node = sceneGraph?.nodes?.find(item => item.nodeId === finished.nodeId);
        if (node && (preview.x !== node.x || preview.y !== node.y)) {
          queueSceneDirtyNode(node.nodeId);
          send({
            action: "scene_update_node",
            nodeId: node.nodeId,
            title: node.title,
            x: preview.x,
            y: preview.y
          });
        } else {
          refreshSceneEdges(svg);
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

      queueSceneDirtyNodes(pendingOutput.nodeId, nodeId);
      send({
        action: "scene_connect",
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
      refreshSceneEdges(svg);
      updateSceneVisuals();
      return true;
    };

    // Уход указателя с канваса снимает подсветку: иначе последний наведённый
    // сокет остался бы обведённым.
    svg.addEventListener("pointerleave", () => clearSocketHover());

    svg.addEventListener("pointerup", finishDrag);
    svg.addEventListener("pointercancel", finishDrag);

    svg.addEventListener("wheel", event => {
      if (!sceneGraph?.nodes?.length) return;

      const rect = svg.getBoundingClientRect();
      if (rect.width <= 0 || rect.height <= 0) return;

      const viewBox = svg.viewBox.baseVal;
      const focus = clientToSceneGraph(svg, event.clientX, event.clientY);
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

      sceneViewport = {
        x: focus.x - ratioX * nextWidth,
        y: focus.y - ratioY * nextHeight,
        width: nextWidth,
        height: nextHeight
      };

      setViewBox();
      refreshSceneEdges(svg);
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
        selectedSceneNodeId = nodeId;
        pendingOutput = null;
        pendingConnectionPoint = null;
        updateSceneVisuals();
        updateSceneInspector(document.getElementById("inspector"));
        openSceneNodeContextMenu(
          svg,
          event.clientX,
          event.clientY,
          nodeId
        );
      });

      node.addEventListener("click", event => {
        if (event.target.closest(".socketGroup")) return;
        selectedSceneNodeId = node.dataset.nodeId;
        updateSceneVisuals();
        updateSceneInspector(document.getElementById("inspector"));
      });
    });

    svg.querySelectorAll(".socketGroup").forEach(socket => {
      const openConnectionMenu = event => {
        event.preventDefault();
        event.stopPropagation();

        const node = socket.closest(".node");
        if (!node) return;

        openSceneConnectionContextMenu(
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
          pendingConnectionPoint = clientToSceneGraph(svg, event.clientX, event.clientY);
          selectedSceneNodeId = nodeId;
          updateSceneVisuals();
          refreshSceneEdges(svg);
          updateSceneInspector(document.getElementById("inspector"));
          return;
        }

        if (direction === "Input" && pendingOutput) {
          if (isCompatibleInput(nodeId, socketId)) {
            queueSceneDirtyNodes(pendingOutput.nodeId, nodeId);
            send({
              action: "scene_connect",
              fromNodeId: pendingOutput.nodeId,
              fromSocketId: pendingOutput.socketId,
              toNodeId: nodeId,
              toSocketId: socketId
            });
          }
          pendingOutput = null;
          pendingConnectionPoint = null;
          refreshSceneEdges(svg);
          updateSceneVisuals();
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
    if (!pendingOutput || !sceneGraph || !nodeId || nodeId === pendingOutput.nodeId) return false;
    const node = sceneGraph.nodes?.find(item => item.nodeId === nodeId);
    return Boolean(node?.sockets?.some(socket =>
      socket.socketId === socketId && socket.direction === "Input"
    ));
  }

  function sceneSocketPoint(nodeId, socketId) {
    const node = sceneGraph?.nodes?.find(item => item.nodeId === nodeId);
    if (!node) return null;
    const socket = node.sockets?.find(item => item.socketId === socketId);
    if (!socket) return null;

    const sockets = node.sockets.filter(item => item.direction === socket.direction);
    const index = Math.max(0, sockets.findIndex(item => item.socketId === socketId));
    const position = getSceneNodePosition(node);
    return {
      x: position.x + (socket.direction === "Output" ? SCENE_NODE_WIDTH : 0),
      y: position.y + 22 + index * 22
    };
  }

  function sceneConnectionPreviewMarkup() {
    if (!pendingOutput || !pendingConnectionPoint) return "";
    const start = sceneSocketPoint(pendingOutput.nodeId, pendingOutput.socketId);
    if (!start) return "";
    const end = pendingConnectionPoint;
    const bend = Math.max(70, Math.abs(end.x - start.x) * 0.45);
    return "<path class='edge pendingEdge' d='M" + start.x + " " + start.y + " C" +
      (start.x + bend) + " " + start.y + " " +
      (end.x - bend) + " " + end.y + " " + end.x + " " + end.y + "'></path>";
  }

  function updateSceneVisuals() {
    const svg = document.getElementById("sceneGraphSvg");
    if (!svg) return;

    svg.querySelectorAll(".node").forEach(node => {
      const id = node.dataset.nodeId;
      node.classList.toggle("selected", id === selectedSceneNodeId);
      node.classList.toggle("executed", executedNodeIds.has(id));
      node.classList.toggle(
        "runtime-active",
        id === runtimeState.currentNodeId &&
        ["Running", "Waiting"].includes(runtimeStatusName(runtimeState.status))
      );
      node.classList.toggle("connection-source", pendingOutput?.nodeId === id);
      node.classList.toggle("dirty", sceneDirtyNodeIds.has(id));
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

    const preview = svg.querySelector("#sceneGraphConnectionPreview");
    if (preview) preview.innerHTML = sceneConnectionPreviewMarkup();
  }
  const PARAMETER_LABELS = {
    worldPointId: "WorldPoint Id", triggerRadius: "Радиус триггера",
    dialogueId: "Dialogue Id", choiceId: "Choice Id",
    outputCount: "Количество выходов", seconds: "Секунды", eventType: "Тип события",
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
        "<input class='toolButton' data-param-value value='" + escapeHtml(value) + "'>" +
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


  function findSceneDialogue(node) {
    const id = node?.parameters?.dialogueId;
    return sceneDefinition()?.dialogues?.find(item =>
      item.id === id) || null;
  }

  function findSceneChoice(node) {
    const id = node?.parameters?.choiceId;
    return sceneDefinition()?.choices?.find(item =>
      item.id === id) || null;
  }

  function sceneResourceOptions(resources, currentId) {
    const items = Array.isArray(resources) ? resources : [];
    const options = [];

    if (currentId && !items.some(item => item.id === currentId)) {
      options.push(
        "<option value='" + escapeHtml(currentId) + "' selected>" +
        escapeHtml(currentId + " — отсутствует") +
        "</option>"
      );
    }

    options.push(...items.map(item =>
      "<option value='" + escapeHtml(item.id) + "'" +
      (item.id === currentId ? " selected" : "") + ">" +
      escapeHtml(item.id + " — " + (item.title || item.speaker || "Без названия")) +
      "</option>"
    ));

    return options.join("");
  }

  function updateSceneNodeParameter(node, key, value) {
    const parameters = { ...(node.parameters || {}) };
    parameters[key] = value;
    queueSceneDirtyNode(node.nodeId);
    updateSceneVisuals();

    send({
      action: "scene_update_node",
      nodeId: node.nodeId,
      title: node.title,
      x: node.x,
      y: node.y,
      parameters
    });
  }

  function specialSceneNodePropertyHtml(node, contentHtml) {
    return (
      "<div class='badge accent'>" + escapeHtml(node.nodeType) + "</div>" +
      "<h3 style='margin:10px 0 4px'>" + escapeHtml(node.title) + "</h3>" +
      "<div class='field'><label>NodeId</label><input value='" + escapeHtml(node.nodeId) + "' disabled></div>" +
      "<div class='field' style='margin-top:8px'><label>Название ноды</label><input id='sceneGraphEditTitle' value='" + escapeHtml(node.title) + "'></div>" +
      "<div class='fieldGrid' style='margin-top:8px'>" +
        "<div class='field'><label>X</label><input id='sceneGraphEditX' type='number' value='" + node.x + "'></div>" +
        "<div class='field'><label>Y</label><input id='sceneGraphEditY' type='number' value='" + node.y + "'></div>" +
      "</div>" +
      contentHtml +
      "<div class='miniLabel' style='margin-top:16px'>Технические параметры</div>" +
      "<div id='sceneGraphParameterEditor' style='margin-top:6px'>" + parameterEditorHtml(node) + "</div>" +
      "<button class='toolButton' id='addSceneGraphParameter' style='margin-top:6px'>Добавить параметр</button>" +
      "<button class='toolButton primary' id='saveSceneGraphNode' style='margin-top:10px'>Сохранить свойства ноды</button>" +
      "<button class='toolButton' id='deleteSceneGraphNode' style='margin-top:6px'>Удалить ноду</button>" +
      "<div class='notice' style='margin-top:12px'>ID ресурсов и Output socket ID не редактируются вручную: они являются стабильными связями canonical Scene.</div>"
    );
  }

  function bindSpecialSceneNodePropertyInspector(ins, node) {
    ins.querySelectorAll("[data-param-remove]").forEach(button => {
      button.addEventListener("click", () => button.closest("[data-param-row]")?.remove());
    });

    ins.querySelector("#addSceneGraphParameter")?.addEventListener("click", () => {
      addParameterRow(ins.querySelector("#sceneGraphParameterEditor"));
    });

    ins.querySelector("#saveSceneGraphNode")?.addEventListener("click", () => {
      queueSceneDirtyNode(node.nodeId);
      updateSceneVisuals();
      send({
        action: "scene_update_node",
        nodeId: node.nodeId,
        title: ins.querySelector("#sceneGraphEditTitle")?.value ?? node.title,
        x: Number(ins.querySelector("#sceneGraphEditX")?.value ?? node.x),
        y: Number(ins.querySelector("#sceneGraphEditY")?.value ?? node.y),
        parameters: collectParameters(ins)
      });
    });

    ins.querySelector("#deleteSceneGraphNode")?.addEventListener("click", () => {
      requestSceneNodeDelete(node.nodeId);
    });
  }

  function renderDialogueInspector(ins, node) {
    const dialogueId = node.parameters?.dialogueId || "";
    const dialogue = findSceneDialogue(node);
    const dialogues = sceneDefinition()?.dialogues || [];

    const content =
      "<div class='miniLabel' style='margin-top:16px'>Dialogue resource</div>" +
      "<select id='sceneDialogueResource' class='toolButton' style='width:100%;margin-top:6px'>" +
        "<option value=''>— не выбран —</option>" +
        sceneResourceOptions(dialogues, dialogueId) +
      "</select>" +
      (!dialogue
        ? "<div class='notice' style='margin-top:8px'>У ноды нет корректного Dialogue resource. Создайте новый ресурс или выберите существующий.</div>" +
          "<button class='toolButton primary' id='createSceneDialogue' style='margin-top:8px'>Создать диалог для ноды</button>"
        : "<div class='badge blue' style='margin-top:8px'>Resource ID: " + escapeHtml(dialogue.id) + "</div>" +
          "<div class='field' style='margin-top:10px'><label>Speaker</label><input id='sceneDialogueSpeaker' value='" + escapeHtml(dialogue.speaker) + "'></div>" +
          "<div class='field' style='margin-top:8px'><label>Текст</label><textarea id='sceneDialogueText' rows='7' style='width:100%;resize:vertical'>" + escapeHtml(dialogue.text) + "</textarea></div>" +
          "<button class='toolButton primary' id='saveSceneDialogue' style='margin-top:8px'>Сохранить диалог</button>"
      );

    ins.innerHTML = specialSceneNodePropertyHtml(node,
      "<div class='miniLabel' style='margin-top:16px'>Содержимое диалога</div>" +
      "<div class='tableLike' style='margin-top:6px'>" + content + "</div>"
    );

    bindSpecialSceneNodePropertyInspector(ins, node);

    ins.querySelector("#sceneDialogueResource")?.addEventListener("change", event => {
      updateSceneNodeParameter(node, "dialogueId", event.target.value);
    });

    ins.querySelector("#createSceneDialogue")?.addEventListener("click", () => {
      queueSceneDirtyNode(node.nodeId);
      send({ action: "scene_create_dialogue_for_node", nodeId: node.nodeId });
    });

    ins.querySelector("#saveSceneDialogue")?.addEventListener("click", () => {
      send({
        action: "scene_update_dialogue",
        dialogueId: dialogue.id,
        speaker: ins.querySelector("#sceneDialogueSpeaker")?.value ?? "",
        text: ins.querySelector("#sceneDialogueText")?.value ?? ""
      });
    });
  }

  function renderChoiceInspector(ins, node) {
    const choiceId = node.parameters?.choiceId || "";
    const choice = findSceneChoice(node);
    const choices = sceneDefinition()?.choices || [];

    let optionsHtml = "";
    if (choice) {
      optionsHtml = choice.options?.length
        ? choice.options.map((option, index) => {
            const socket = node.sockets?.find(item =>
              item.socketId === option.outputSocketId &&
              item.direction === "Output");
            const socketExists = Boolean(socket);
            return (
              "<div class='tableRow' data-choice-option-row='" + escapeHtml(option.id) + "' style='display:grid;grid-template-columns:minmax(110px,1fr) minmax(180px,2fr);gap:8px;align-items:center'>" +
                "<div>" +
                  "<strong>Вариант " + (index + 1) + "</strong>" +
                  "<small>" + escapeHtml(option.id) + "</small>" +
                "</div>" +
                "<div>" +
                  "<input class='toolButton' data-choice-option-text value='" + escapeHtml(option.text) + "' style='width:100%'>" +
                  "<div class='notice' style='margin-top:4px;padding:5px 7px'>" +
                    escapeHtml(socketExists ? socket.name + " · " + option.outputSocketId : "Output socket отсутствует: " + option.outputSocketId) +
                  "</div>" +
                  "<button class='toolButton' data-remove-choice-option='" + escapeHtml(option.id) + "' style='margin-top:5px'>Удалить вариант</button>" +
                "</div>" +
              "</div>"
            );
          }).join("")
        : "<div class='notice'>Вариантов пока нет.</div>";

      optionsHtml +=
        "<button class='toolButton' id='addSceneChoiceOption' style='margin-top:8px'>Добавить вариант</button>";
    } else {
      optionsHtml =
        "<div class='notice'>У ноды нет корректного Choice resource.</div>" +
        "<button class='toolButton primary' id='createSceneChoice' style='margin-top:8px'>Создать выбор для ноды</button>";
    }

    const content =
      "<div class='miniLabel' style='margin-top:16px'>Choice resource</div>" +
      "<select id='sceneChoiceResource' class='toolButton' style='width:100%;margin-top:6px'>" +
        "<option value=''>— не выбран —</option>" +
        sceneResourceOptions(choices, choiceId) +
      "</select>" +
      (choice
        ? "<div class='badge blue' style='margin-top:8px'>Resource ID: " + escapeHtml(choice.id) + "</div>" +
          "<div class='field' style='margin-top:10px'><label>Название выбора</label><input id='sceneChoiceTitle' value='" + escapeHtml(choice.title) + "'></div>" +
          "<div class='field' style='margin-top:8px'><label>Speaker</label><input id='sceneChoiceSpeaker' value='" + escapeHtml(choice.speaker) + "'></div>" +
          "<div class='field' style='margin-top:8px'><label>Текст выбора</label><textarea id='sceneChoiceText' rows='5' style='width:100%;resize:vertical'>" + escapeHtml(choice.text) + "</textarea></div>" +
          "<button class='toolButton primary' id='saveSceneChoice' style='margin-top:8px'>Сохранить выбор</button>" +
          "<div class='miniLabel' style='margin-top:14px'>Варианты и Output sockets</div>" +
          "<div class='tableLike' style='margin-top:6px'>" + optionsHtml + "</div>"
        : optionsHtml);

    ins.innerHTML = specialSceneNodePropertyHtml(node, content);
    bindSpecialSceneNodePropertyInspector(ins, node);

    ins.querySelector("#sceneChoiceResource")?.addEventListener("change", event => {
      updateSceneNodeParameter(node, "choiceId", event.target.value);
    });

    ins.querySelector("#createSceneChoice")?.addEventListener("click", () => {
      queueSceneDirtyNode(node.nodeId);
      send({ action: "scene_create_choice_for_node", nodeId: node.nodeId });
    });

    ins.querySelector("#saveSceneChoice")?.addEventListener("click", () => {
      const options = Array.from(ins.querySelectorAll("[data-choice-option-row]")).map(row => ({
        id: row.dataset.choiceOptionRow,
        text: row.querySelector("[data-choice-option-text]")?.value ?? ""
      }));

      send({
        action: "scene_update_choice",
        choiceId: choice.id,
        title: ins.querySelector("#sceneChoiceTitle")?.value ?? "",
        speaker: ins.querySelector("#sceneChoiceSpeaker")?.value ?? "",
        text: ins.querySelector("#sceneChoiceText")?.value ?? "",
        options
      });
    });

    ins.querySelector("#addSceneChoiceOption")?.addEventListener("click", () => {
      send({
        action: "scene_add_choice_option",
        nodeId: node.nodeId,
        choiceId: choice?.id || choiceId
      });
    });

    ins.querySelectorAll("[data-remove-choice-option]").forEach(button => {
      button.addEventListener("click", () => {
        const optionId = button.dataset.removeChoiceOption;
        if (!optionId || !choice) return;
        if (!window.confirm("Удалить этот вариант выбора?\n\nЕсли его Output socket уже подключён, операция будет отклонена.")) return;

        send({
          action: "scene_remove_choice_option",
          nodeId: node.nodeId,
          choiceId: choice.id,
          optionId
        });
      });
    });
  }

  function selectSceneNodeByContent(kind, resourceId) {
    const nodes = sceneGraph?.nodes || [];
    const key = kind === "dialogue" ? "dialogueId" : "choiceId";
    const node = nodes.find(item =>
      item.parameters?.[key] &&
      item.parameters[key] === resourceId);
    if (!node) return;

    selectedSceneNodeId = node.nodeId;
    updateSceneVisuals();
    updateSceneInspector(document.getElementById("inspector"));
  }

  function renderSceneContentOverview(ins) {
    const definition = sceneDefinition() || { dialogues: [], choices: [] };
    const dialogues = definition.dialogues || [];
    const choices = definition.choices || [];

    const dialogueRows = dialogues.length
      ? dialogues.map(item =>
          "<div class='tableRow'><span><strong>" + escapeHtml(item.id) + "</strong><small>" +
          escapeHtml((item.speaker || "Без Speaker") + ": " + (item.text || "Без текста")) +
          "</small></span><button class='toolButton' data-open-dialogue='" + escapeHtml(item.id) + "'>Открыть</button></div>"
        ).join("")
      : "<div class='notice'>Dialogue resources пока нет.</div>";

    const choiceRows = choices.length
      ? choices.map(item =>
          "<div class='tableRow'><span><strong>" + escapeHtml(item.id) + "</strong><small>" +
          escapeHtml((item.title || "Без названия") + ": " + (item.text || "Без текста")) +
          "</small></span><button class='toolButton' data-open-choice='" + escapeHtml(item.id) + "'>Открыть</button></div>"
        ).join("")
      : "<div class='notice'>Choice resources пока нет.</div>";

    ins.innerHTML =
      "<div class='badge accent'>Контент сцены</div>" +
      "<h3 style='margin:10px 0 4px'>Dialogue / Choice</h3>" +
      "<div class='notice'>Выберите ресурс ниже. Редактор откроет связанную Graph-ноду и сохранит контент в canonical Scene Definition.</div>" +
      "<div class='miniLabel' style='margin-top:14px'>Dialogues (" + dialogues.length + ")</div>" +
      "<div class='tableLike' style='margin-top:6px'>" + dialogueRows + "</div>" +
      "<div class='miniLabel' style='margin-top:14px'>Choices (" + choices.length + ")</div>" +
      "<div class='tableLike' style='margin-top:6px'>" + choiceRows + "</div>";

    ins.querySelectorAll("[data-open-dialogue]").forEach(button => {
      button.addEventListener("click", () =>
        selectSceneNodeByContent("dialogue", button.dataset.openDialogue));
    });
    ins.querySelectorAll("[data-open-choice]").forEach(button => {
      button.addEventListener("click", () =>
        selectSceneNodeByContent("choice", button.dataset.openChoice));
    });
  }

  function updateSceneInspector(ins) {
    if (!ins) return;

    const node = sceneGraph?.nodes?.find(item => item.nodeId === selectedSceneNodeId);
    if (!node) {
      renderSceneContentOverview(ins);
      return;
    }


    if (node.nodeType === "Dialogue") {
      renderDialogueInspector(ins, node);
      return;
    }

    if (node.nodeType === "Choice") {
      renderChoiceInspector(ins, node);
      return;
    }

    const connections = sceneGraph.connections.filter(connection =>
      connection.fromNodeId === node.nodeId || connection.toNodeId === node.nodeId);

    ins.innerHTML =
      "<div class='badge accent'>" + escapeHtml(node.nodeType) + "</div>" +
      "<h3 style='margin:10px 0 4px'>" + escapeHtml(node.title) + "</h3>" +
      "<div class='field'><label>NodeId</label><input value='" + escapeHtml(node.nodeId) + "' disabled></div>" +
      "<div class='field' style='margin-top:8px'><label>Название</label><input id='sceneGraphEditTitle' value='" + escapeHtml(node.title) + "'></div>" +
      "<div class='fieldGrid' style='margin-top:8px'>" +
        "<div class='field'><label>X</label><input id='sceneGraphEditX' type='number' value='" + node.x + "'></div>" +
        "<div class='field'><label>Y</label><input id='sceneGraphEditY' type='number' value='" + node.y + "'></div>" +
      "</div>" +
      "<div class='field' style='margin-top:8px'><label>NodeType</label><input value='" + escapeHtml(node.nodeType) + "' disabled></div>" +
      "<div class='miniLabel' style='margin-top:14px'>Параметры</div>" +
      "<div id='sceneGraphParameterEditor' style='margin-top:6px'>" + parameterEditorHtml(node) + "</div>" +
      "<button class='toolButton' id='addSceneGraphParameter' style='margin-top:6px'>Добавить параметр</button>" +
      "<button class='toolButton primary' id='saveSceneGraphNode' style='margin-top:10px'>Сохранить свойства</button>" +
      "<button class='toolButton' id='deleteSceneGraphNode' style='margin-top:6px'>Удалить ноду</button>" +
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

    ins.querySelector("#addSceneGraphParameter").addEventListener("click", () => {
      addParameterRow(ins.querySelector("#sceneGraphParameterEditor"));
    });

    ins.querySelector("#saveSceneGraphNode").addEventListener("click", () => {
      queueSceneDirtyNode(node.nodeId);
      updateSceneVisuals();
      send({
        action: "scene_update_node",
        nodeId: node.nodeId,
        title: ins.querySelector("#sceneGraphEditTitle").value,
        x: Number(ins.querySelector("#sceneGraphEditX").value),
        y: Number(ins.querySelector("#sceneGraphEditY").value),
        parameters: collectParameters(ins)
      });
    });

    ins.querySelector("#deleteSceneGraphNode").addEventListener("click", () => {
      requestSceneNodeDelete(node.nodeId);
    });

    ins.querySelectorAll("[data-disconnect]").forEach(button => {
      button.addEventListener("click", () => {
        const connection = JSON.parse(decodeURIComponent(button.dataset.disconnect));
        queueSceneDirtyNodes(connection.fromNodeId, connection.toNodeId);
        send({
          action: "scene_disconnect",
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

  function fitSceneGraph() {
    if (!sceneGraph?.nodes?.length) return;
    const minX = Math.min(...sceneGraph.nodes.map(node => getSceneNodePosition(node).x));
    const maxX = Math.max(...sceneGraph.nodes.map(node => getSceneNodePosition(node).x + SCENE_NODE_WIDTH));
    const minY = Math.min(...sceneGraph.nodes.map(node => getSceneNodePosition(node).y));
    const maxY = Math.max(...sceneGraph.nodes.map(node => getSceneNodePosition(node).y + sceneNodeHeight(node)));
    const svg = document.getElementById("sceneGraphSvg");
    if (!svg) return;
    sceneViewport = {
      x: minX - 60,
      y: minY - 60,
      width: Math.max(220, maxX - minX + 120),
      height: Math.max(180, maxY - minY + 120)
    };
    svg.setAttribute(
      "viewBox",
      sceneViewport.x + " " + sceneViewport.y + " " +
      sceneViewport.width + " " + sceneViewport.height
    );
  }

  function getSceneNodePosition(node) {
    return scenePreviewPositions.get(node.nodeId) || { x: node.x, y: node.y };
  }

  function clientToSceneGraph(svg, clientX, clientY) {
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

  function queueSceneDirtyNode(nodeId) {
    if (nodeId) sceneDirtyNodeIds.add(nodeId);
  }

  function queueSceneDirtyNodes(...nodeIds) {
    nodeIds.filter(Boolean).forEach(nodeId => sceneDirtyNodeIds.add(nodeId));
  }

  function closeSceneContextMenu() {
    activeSceneContextMenu?.remove();
    activeSceneContextMenu = null;
  }

  /**
   * Меню последних открытых сцен. Состав и порядок задаёт Host (свежие сверху),
   * текущая сцена помечается и повторно не перезагружается.
   */
  function openRecentSceneFilesMenu(anchor, paths, currentPath) {
    closeRecentSceneFilesMenu();

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

      const button = sceneContextMenuButton(
        (isCurrent ? "● " : "") + recentSceneFileLabel(path),
        () => {
          closeRecentSceneFilesMenu();
          if (isCurrent) return;
          send({ action: "scene_open_recent", path });
        }
      );
      button.title = path;
      if (isCurrent) button.dataset.current = "true";
      menu.appendChild(button);
    });

    const rect = anchor.getBoundingClientRect();
    placeSceneContextMenu(menu, rect.left, rect.bottom + 4);
    activeRecentSceneMenu = menu;
  }

  /** Показывает только имя файла: полный путь виден в подсказке кнопки. */
  function recentSceneFileLabel(path) {
    const normalized = String(path ?? "").replace(/\\/g, "/");
    const name = normalized.slice(normalized.lastIndexOf("/") + 1);
    return name || String(path ?? "");
  }

  function closeRecentSceneFilesMenu() {
    activeRecentSceneMenu?.remove();
    activeRecentSceneMenu = null;
  }

  function placeSceneContextMenu(menu, clientX, clientY) {
    document.body.appendChild(menu);
    activeSceneContextMenu = menu;

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

  function sceneContextMenuButton(text, handler) {
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

  function openSceneAddContextMenu(svg, clientX, clientY, sceneGraphPoint) {
    closeSceneContextMenu();

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

    SCENE_NODE_TYPES.forEach(([type, label]) => {
      const button = sceneContextMenuButton(label + " (" + type + ")", () => {
        const connectionSource = pendingOutput
          ? {
              connectFromNodeId: pendingOutput.nodeId,
              connectFromSocketId: pendingOutput.socketId
            }
          : {};

        if (pendingOutput) {
          queueSceneDirtyNodes(pendingOutput.nodeId);
        }

        send({
          action: "scene_add_node",
          nodeType: type,
          title: type,
          x: Math.round(sceneGraphPoint.x),
          y: Math.round(sceneGraphPoint.y),
          ...connectionSource
        });

        pendingOutput = null;
        pendingConnectionPoint = null;
        closeSceneContextMenu();
      });
      button.dataset.nodeType = type;
      if (pendingOutput && type === "Start") button.disabled = true;
      menu.appendChild(button);
    });

    placeSceneContextMenu(menu, clientX, clientY);
  }

  function openSceneNodeContextMenu(svg, clientX, clientY, nodeId) {
    closeSceneContextMenu();

    const node = sceneGraph?.nodes?.find(item => item.nodeId === nodeId);
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

    menu.appendChild(sceneContextMenuButton("Сохранить", () => {
      send({ action: "scene_save" });
      closeSceneContextMenu();
    }));

    menu.appendChild(sceneContextMenuButton("Удалить", () => {
      requestSceneNodeDelete(nodeId);
      closeSceneContextMenu();
    }));

    placeSceneContextMenu(menu, clientX, clientY);
  }

  function openSceneConnectionContextMenu(svg, clientX, clientY, nodeId, socketId) {
    closeSceneContextMenu();

    const node = sceneGraph?.nodes?.find(item => item.nodeId === nodeId);
    const socket = node?.sockets?.find(item => item.socketId === socketId);
    if (!node || !socket) return;

    const connections = sceneGraph.connections.filter(connection =>
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
        menu.appendChild(sceneContextMenuButton(
          "Разорвать соединение: " + source + " → " + target,
          () => {
            queueSceneDirtyNodes(connection.fromNodeId, connection.toNodeId);
            send({
              action: "scene_disconnect",
              fromNodeId: connection.fromNodeId,
              fromSocketId: connection.fromSocketId,
              toNodeId: connection.toNodeId,
              toSocketId: connection.toSocketId
            });
            closeSceneContextMenu();
          }
        ));
      });
    }

    if (pendingOutput) {
      menu.appendChild(sceneContextMenuButton("Отменить выбор Output", () => {
        pendingOutput = null;
        pendingConnectionPoint = null;
        const sceneGraphSvg = document.getElementById("sceneGraphSvg");
        if (sceneGraphSvg) refreshSceneEdges(sceneGraphSvg);
        closeSceneContextMenu();
      }));
    }

    placeSceneContextMenu(menu, clientX, clientY);
  }

  function requestSceneNodeDelete(nodeId) {
    const node = sceneGraph?.nodes?.find(item => item.nodeId === nodeId);
    if (!node) return false;

    const relatedNodeIds = sceneGraph.connections
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

    selectedSceneNodeId = null;
    if (pendingOutput?.nodeId === nodeId) {
      pendingOutput = null;
      pendingConnectionPoint = null;
    }

    queueSceneDirtyNodes(...relatedNodeIds);
    send({ action: "scene_remove_node", nodeId });
    const sceneGraphSvg = document.getElementById("sceneGraphSvg");
    if (sceneGraphSvg) refreshSceneEdges(sceneGraphSvg);
    return true;
  }

  function refreshSceneEdges(svg) {
    const edgeGroup = svg.querySelector("#sceneGraphEdges");
    if (edgeGroup) {
      edgeGroup.innerHTML = sceneEdges();
    }
    updateSceneVisuals();
  }

  function escapeCssAttribute(value) {
    return String(value ?? "").replace(/\\/g, "\\\\").replace(/'/g, "\\'");
  }

  function clamp(value, min, max) {
    return Math.min(max, Math.max(min, value));
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

  let sceneCatalog = [];

  function updateSceneMessage(data) {
    if (!data) return;

    if (data.type === "scene_catalog") {
      sceneCatalog = Array.isArray(data.scenes) ? data.scenes : [];
      if (location.hash.toLowerCase() === "#scene") {
        renderScene(document.getElementById("workspace"), document.getElementById("inspector"));
      } else if (location.hash.toLowerCase() === "#dialogue") {
        window.__assistDialogueWorkspace?.refresh?.(
          document.getElementById("workspace"),
          document.getElementById("inspector")
        );
      }
      return;
    }

    if (data.type === "recent_files") {
      if (data.kind !== "scene") return;
      recentSceneFiles = Array.isArray(data.paths) ? data.paths : [];
      recentSceneCurrentPath = data.currentPath || "";
      closeRecentSceneFilesMenu();

      const button = document.getElementById("openRecentScene");
      if (button) button.disabled = recentSceneFiles.length === 0;
      return;
    }

    if (data.type === "scene_content_selected") {
      window.__assistDialogueWorkspace?.selectResource?.(data.kind, data.id);
      return;
    }

    if (data.type === "scene_definition") {
      sceneGraph = data.definition?.graph || null;
      sceneDocument = {
        path: data.documentPath || "",
        lastPath: data.lastDocumentPath || "",
        dirty: Boolean(data.documentDirty)
      };
      sceneNavigationBack = data.navigationBack || null;
      sceneHistory = {
        canUndo: Boolean(data.canUndo),
        canRedo: Boolean(data.canRedo)
      };
      sceneValidation = Array.isArray(data.validation) ? data.validation : [];

      scenePreviewPositions.clear();

      if (!sceneDocument.dirty) {
        sceneDirtyNodeIds.clear();
      }

      if (data.selectedNodeId) {
        selectedSceneNodeId = data.selectedNodeId;
      }

      if (selectedSceneNodeId && !sceneGraph.nodes.some(node => node.nodeId === selectedSceneNodeId)) {
        selectedSceneNodeId = sceneGraph.nodes[0]?.nodeId || null;
      }

      if (!selectedSceneNodeId && sceneGraph.nodes.length) {
        selectedSceneNodeId = sceneGraph.nodes[0].nodeId;
      }

      window.__assistSceneEditorState = {
        definition: data.definition,
        payload: data
      };

      if (location.hash.toLowerCase() === "#scene") {
        renderScene(document.getElementById("workspace"), document.getElementById("inspector"));
      } else if (location.hash.toLowerCase() === "#dialogue") {
        window.__assistDialogueWorkspace?.refresh?.(
          document.getElementById("workspace"),
          document.getElementById("inspector")
        );
      }

      if (pendingSceneFit) {
        pendingSceneFit = false;
        fitSceneGraph();
      }
    }
  }

  window.__assistSceneEditor = {
    render: renderScene,
    selectNode(nodeId) {
      if (!nodeId || !sceneGraph?.nodes?.some(node => node.nodeId === nodeId)) return;
      selectedSceneNodeId = nodeId;
      updateSceneVisuals();
      updateSceneInspector(document.getElementById("inspector"));
    },
    getState: () => ({
      definition: window.__assistSceneEditorState?.definition || null,
      graph: sceneGraph,
      catalog: sceneCatalog,
      selectedNodeId: selectedSceneNodeId,
      viewport: { ...sceneViewport }
    })
  };

  window.addEventListener("message", event => {
    try {
      updateSceneMessage(typeof event.data === "string" ? JSON.parse(event.data) : event.data);
    } catch (error) {
      console.error("Scene Editor message parse error", error);
    }
  });

  const webview = window.chrome?.webview;
  if (typeof webview?.addEventListener === "function") {
    webview.addEventListener("message", event => {
      try {
        updateSceneMessage(typeof event.data === "string" ? JSON.parse(event.data) : event.data);
      } catch (error) {
        console.error("Scene Editor WebView2 message parse error", error);
      }
    });
  }

  document.addEventListener("keydown", event => {
    if (location.hash.toLowerCase() !== "#scene") return;

    if (event.code === "Space") {
      sceneSpaceDown = true;
      if (!isTextEntryTarget(event.target)) event.preventDefault();
    }

    if (event.key === "Escape" && activeSceneContextMenu) {
      closeSceneContextMenu();
      event.preventDefault();
      return;
    }

    if (event.key === "Escape" && pendingOutput) {
      pendingOutput = null;
      pendingConnectionPoint = null;
      const svg = document.getElementById("sceneGraphSvg");
      if (svg) refreshSceneEdges(svg);
      event.preventDefault();
      return;
    }

    if (
      event.key === "Delete" &&
      selectedSceneNodeId &&
      !isTextEntryTarget(event.target)
    ) {
      if (requestSceneNodeDelete(selectedSceneNodeId)) event.preventDefault();
      return;
    }

    if ((event.ctrlKey || event.metaKey) && !isTextEntryTarget(event.target)) {
      if (event.code === "KeyZ" && !event.shiftKey) {
        event.preventDefault();
        if (sceneHistory.canUndo) send({ action: "scene_undo" });
      } else if ((event.code === "KeyZ" && event.shiftKey) || event.code === "KeyY") {
        event.preventDefault();
        if (sceneHistory.canRedo) send({ action: "scene_redo" });
      }
    }
  });

  document.addEventListener("keyup", event => {
    if (event.code === "Space") sceneSpaceDown = false;
  });

  document.addEventListener("pointerdown", event => {
    if (activeSceneContextMenu && !activeSceneContextMenu.contains(event.target)) {
      closeSceneContextMenu();
    }

    if (activeRecentSceneMenu && !activeRecentSceneMenu.contains(event.target)) {
      closeRecentSceneFilesMenu();
    }
  });

  window.addEventListener("blur", () => {
    sceneSpaceDown = false;
    closeSceneContextMenu();
    closeRecentSceneFilesMenu();
  });
})();
