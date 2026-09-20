(() => {
  const root = document.getElementById("root");
  const title = document.getElementById("editorTitle");
  const send = payload => window.chrome?.webview?.postMessage(payload);

  let simulatorContext = {
    player: null,
    selection: { point: null }
  };
  let questGraph = null;
  let selectedGraphNodeId = null;
  let pendingOutput = null;
  let graphHistory = { canUndo: false, canRedo: false };
  let graphValidation = [];
  let graphPreviewPositions = new Map();
  let graphViewport = { x: 0, y: 0, width: 1200, height: 720 };
  let graphDocument = { path: "", dirty: false };
  let graphSpaceDown = false;

  const configs = {
    graph: { title: "Нодовый редактор квестов", draw: renderGraph },
    scene: { title: "Редактор сцен и диалогов", draw: renderScene },
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

    const nodeTypes = [
      ["Start", "Начало"], ["End", "Завершение"], ["Phase", "Фаза"],
      ["Interaction", "Интеракция"], ["Condition", "Условие"], ["And", "AND"],
      ["Or", "OR"], ["Not", "NOT"], ["Switch", "Переключатель"], ["Random", "Случайная ветка"],
      ["Wait", "Ожидание"], ["WaitForCondition", "Ожидать условие"], ["WaitForEvent", "Ожидать событие"],
      ["SetStatus", "Установить статус"], ["SetStep", "Установить этап"], ["SetFlag", "Установить флаг"],
      ["SetVariable", "Установить переменную"], ["DialogueScene", "Сцена диалога"], ["Reward", "Награда"],
      ["GiveItem", "Выдать предмет"], ["RemoveItem", "Удалить предмет"],
      ["AddReputation", "Добавить репутацию"], ["RemoveReputation", "Уменьшить репутацию"]
    ];

    ws.innerHTML =
      "<div class='toolbar' style='margin-bottom:10px;flex-wrap:wrap'>" +
        "<button class='toolButton' id='newGraph'>Новый</button>" +
        "<button class='toolButton' id='openGraph'>Открыть</button>" +
        "<button class='toolButton primary' id='saveGraph'>Сохранить</button>" +
        "<button class='toolButton' id='saveGraphAs'>Сохранить как…</button>" +
        "<span class='badge " + (graphDocument.dirty ? "accent" : "blue") + "'>" +
          escapeHtml(graphDocument.dirty ? "Не сохранено" : "Сохранено") +
        "</span>" +
        "<span class='badge'>" + escapeHtml(graphDocument.path ? graphDocument.path : "Новый документ") + "</span>" +
        "<select id='graphNodeType' class='toolButton'> +
          nodeTypes.map(([type, label]) => "<option value='" + escapeHtml(type) + "'>" + escapeHtml(label) + " (" + escapeHtml(type) + ")</option>").join("") +
        "</select>" +
        "<input id='graphNodeTitle' class='toolButton' style='width:190px' placeholder='Название новой ноды'>" +
        "<button class='toolButton primary' id='addGraphNode'>Добавить ноду</button>" +
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
        "</svg>" +
      "</div>";

    ws.querySelector("#newGraph").addEventListener("click", () => send({ action: "graph_new" }));
    ws.querySelector("#openGraph").addEventListener("click", () => send({ action: "graph_open" }));
    ws.querySelector("#saveGraph").addEventListener("click", () => send({ action: "graph_save" }));
    ws.querySelector("#saveGraphAs").addEventListener("click", () => send({ action: "graph_save_as" }));

    ws.querySelector("#addGraphNode").addEventListener("click", () => {
      const type = ws.querySelector("#graphNodeType").value;
      const nodeTitle = ws.querySelector("#graphNodeTitle").value.trim() || type;
      send({ action: "graph_add_node", nodeType: type, title: nodeTitle, x: 420, y: 120 });
    });
    ws.querySelector("#fitGraph").addEventListener("click", fitGraph);
    ws.querySelector("#undoGraph").addEventListener("click", () => send({ action: "graph_undo" }));
    ws.querySelector("#redoGraph").addEventListener("click", () => send({ action: "graph_redo" }));
    ws.querySelector("#validateGraph").addEventListener("click", () => send({ action: "graph_validate" }));
    bindGraphInteractions(ws.querySelector("#questGraphSvg"));
    updateGraphInspector(ins);
  }

  const GRAPH_NODE_WIDTH = 210;

  function graphNodeHeight(node) {
    const inputs = node.sockets.filter(socket => socket.direction === "Input").length;
    const outputs = node.sockets.filter(socket => socket.direction === "Output").length;
    return Math.max(110, 72 + Math.max(inputs, outputs) * 22);
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
    const inputs = node.sockets.filter(socket => socket.direction === "Input");
    const outputs = node.sockets.filter(socket => socket.direction === "Output");

    const position = getGraphNodePosition(node);
    const nodeHeight = graphNodeHeight(node);
    return "<g class='node" + selected + "' data-node-id='" + escapeHtml(node.nodeId) + "' transform='translate(" + position.x + " " + position.y + ")'>" +
      "<rect class='nodeRect' rx='8' width='" + GRAPH_NODE_WIDTH + "' height='" + nodeHeight + "'></rect>" +
      "<text class='nodeTitle' x='14' y='26'>" + escapeHtml(node.nodeType) + "</text>" +
      "<text x='14' y='49' fill='#a6a6a6' font-size='11'>" + escapeHtml(node.title) + "</text>" +
      "<text x='14' y='91' fill='#737f8b' font-size='9'>" + escapeHtml(node.nodeId) + "</text>" +
      inputs.map((socket, index) =>
        "<g class='socketGroup' data-socket-direction='Input' data-socket-id='" + escapeHtml(socket.socketId) + "'>" +
          "<circle class='socket input' cx='0' cy='" + (22 + index * 22) + "' r='6'></circle>" +
          "<text x='10' y='" + (26 + index * 22) + "' fill='#8f9baa' font-size='9'>" + escapeHtml(socket.name) + "</text>" +
        "</g>"
      ).join("") +
      outputs.map((socket, index) =>
        "<g class='socketGroup' data-socket-direction='Output' data-socket-id='" + escapeHtml(socket.socketId) + "'>" +
          "<circle class='socket output' cx='" + GRAPH_NODE_WIDTH + "' cy='" + (22 + index * 22) + "' r='6'></circle>" +
          "<text x='" + (GRAPH_NODE_WIDTH - 10) + "' y='" + (26 + index * 22) + "' text-anchor='end' fill='#8f9baa' font-size='9'>" + escapeHtml(socket.name) + "</text>" +
        "</g>"
      ).join("") +
    "</g>";
  }

  function bindGraphInteractions(svg) {
    if (!svg) return;

    let dragState = null;
    let panState = null;

    const setViewBox = () => {
      svg.setAttribute("viewBox", graphViewport.x + " " + graphViewport.y + " " + graphViewport.width + " " + graphViewport.height);
    };

    svg.addEventListener("pointerdown", event => {
      const wantsPan = event.button === 1 || (event.button === 0 && graphSpaceDown);
      if (wantsPan) {
        panState = { pointerId: event.pointerId, lastX: event.clientX, lastY: event.clientY };
        svg.classList.add("panActive");
        svg.setPointerCapture?.(event.pointerId);
        event.preventDefault();
        return;
      }

      if (event.button !== 0) return;

      const socket = event.target.closest(".socketGroup");
      if (socket) return;

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
        const rect = svg.getBoundingClientRect();
        if (rect.width > 0 && rect.height > 0) {
          const scaleX = svg.viewBox.baseVal.width / rect.width;
          const scaleY = svg.viewBox.baseVal.height / rect.height;
          graphViewport.x -= (event.clientX - panState.lastX) * scaleX;
          graphViewport.y -= (event.clientY - panState.lastY) * scaleY;
          panState.lastX = event.clientX;
          panState.lastY = event.clientY;
          setViewBox();
          refreshGraphEdges(svg);
        }
        event.preventDefault();
        return;
      }

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
      node.addEventListener("click", event => {
        if (event.target.closest(".socketGroup")) return;
        selectedGraphNodeId = node.dataset.nodeId;
        updateGraphVisuals();
        updateGraphInspector(document.getElementById("inspector"));
      });
    });

    svg.querySelectorAll(".socketGroup").forEach(socket => {
      socket.addEventListener("click", event => {
        event.stopPropagation();

        const node = socket.closest(".node");
        const direction = socket.dataset.socketDirection;
        const socketId = socket.dataset.socketId;
        const nodeId = node.dataset.nodeId;

        if (direction === "Output") {
          pendingOutput = { nodeId, socketId };
          selectedGraphNodeId = nodeId;
          updateGraphVisuals();
          updateGraphInspector(document.getElementById("inspector"));
          return;
        }

        if (direction === "Input" && pendingOutput) {
          send({
            action: "graph_connect",
            fromNodeId: pendingOutput.nodeId,
            fromSocketId: pendingOutput.socketId,
            toNodeId: nodeId,
            toSocketId: socketId
          });
          pendingOutput = null;
        }
      });
    });
  }

  function updateGraphVisuals() {
    const svg = document.getElementById("questGraphSvg");
    if (!svg) return;
    svg.querySelectorAll(".node").forEach(node =>
      node.classList.toggle("selected", node.dataset.nodeId === selectedGraphNodeId));
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

    ins.querySelector("#addGraphParameter").addEventListener("click", () => {
      addParameterRow(ins.querySelector("#graphParameterEditor"));
    });

    ins.querySelector("#saveGraphNode").addEventListener("click", () => {
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
      selectedGraphNodeId = null;
      pendingOutput = null;
      send({ action: "graph_remove_node", nodeId: node.nodeId });
    });

    ins.querySelectorAll("[data-disconnect]").forEach(button => {
      button.addEventListener("click", () => {
        const connection = JSON.parse(decodeURIComponent(button.dataset.disconnect));
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
    ws.innerHTML =
      "<div class='layoutTwo'>" +
        "<section class='panel'><div class='panelTitle'>Scene Graph</div><div class='panelBody'>" +
          sceneItem("ruslan_start", "Начало разговора с Русланом", true) +
          sceneItem("ruslan_job_offer", "Предложение поручения", false) +
          sceneItem("gosha_meat", "Получение мяса", false) +
          sceneItem("ruslan_completed", "Завершение", false) +
        "</div></section>" +
        "<section class='panel'><div class='panelTitle'>Предпросмотр</div><div class='panelBody'>" +
          "<div class='notice'>Scene Graph отвечает за подачу сцены. QuestState и Data Channels здесь только читаются.</div>" +
          "<div class='card zoomIn' style='margin-top:12px'><div class='miniLabel'>Руслан</div><h3>Есть для тебя особое предложение.</h3><p>Предпросмотр диалогового блока. Позже сюда добавятся варианты, таймлайн, актёры, звук, камера и VFX.</p></div>" +
        "</div></section>" +
      "</div>";

    ins.innerHTML =
      "<div class='badge'>Scene</div>" +
      "<h3 style='margin:10px 0 4px'>Диалоговая сцена</h3>" +
      "<div class='notice'>Сцена не владеет runtime-состоянием квеста.</div>";
  }

  function sceneItem(id, label, active) {
    return "<button class='listButton " + (active ? "active" : "") + "' data-scene='" + id + "'>" + escapeHtml(label) + "</button>";
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
    const types = [
      "Start", "End", "Phase", "Interaction", "Condition", "And", "Or", "Not",
      "Switch", "Random", "Wait", "WaitForCondition", "WaitForEvent", "SetStatus",
      "SetStep", "SetFlag", "SetVariable", "DialogueScene", "Reward", "GiveItem",
      "RemoveItem", "AddReputation", "RemoveReputation"
    ];
    ws.innerHTML =
      "<div class='card'><div class='miniLabel'>Node Registry</div><p>Каждый тип ноды имеет schema, визуальный редактор и runtime handler.</p></div>" +
      "<div class='tagCloud' style='margin-top:12px'>" +
        types.map(type => "<span class='badge'>" + escapeHtml(type) + "</span>").join("") +
      "</div>";
    ins.innerHTML =
      "<div class='badge accent'>Registry</div><h3 style='margin:10px 0 4px'>Типы нод</h3>" +
      "<div class='notice'>Расширение не требует изменения канонической модели существующих нод.</div>";
  }

  function getGraphNodePosition(node) {
    return graphPreviewPositions.get(node.nodeId) || { x: node.x, y: node.y };
  }

  function clientToGraph(svg, clientX, clientY) {
    const rect = svg.getBoundingClientRect();
    const viewBox = svg.viewBox.baseVal;
    return {
      x: viewBox.x + ((clientX - rect.left) / rect.width) * viewBox.width,
      y: viewBox.y + ((clientY - rect.top) / rect.height) * viewBox.height
    };
  }

  function refreshGraphEdges(svg) {
    const edgeGroup = svg.querySelector("#questGraphEdges");
    if (edgeGroup) {
      edgeGroup.innerHTML = graphEdges();
    }
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
  webview?.addEventListener("message", event => {
    const data = typeof event.data === "string" ? JSON.parse(event.data) : event.data;

    if (data?.type === "quest_graph") {
      questGraph = data.graph;
      graphDocument = {
        path: data.documentPath || "",
        dirty: Boolean(data.documentDirty)
      };
      graphPreviewPositions.clear();
      graphHistory = {
        canUndo: Boolean(data.canUndo),
        canRedo: Boolean(data.canRedo)
      };
      graphValidation = Array.isArray(data.validation) ? data.validation : [];
      if (data.selectedNodeId) selectedGraphNodeId = data.selectedNodeId;
      if (selectedGraphNodeId && !questGraph.nodes.some(node => node.nodeId === selectedGraphNodeId)) {
        selectedGraphNodeId = questGraph.nodes[0]?.nodeId || null;
      }
      if (!selectedGraphNodeId && questGraph.nodes.length) selectedGraphNodeId = questGraph.nodes[0].nodeId;
      if (currentId() === "graph") render();
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
  });

  document.addEventListener("keydown", event => {
    if (event.code === "Space") {
      graphSpaceDown = true;
      if (currentId() === "graph") event.preventDefault();
    }
    if (currentId() !== "graph" || !(event.ctrlKey || event.metaKey)) return;
    const key = event.key.toLowerCase();
    if (key === "z" && !event.shiftKey) {
      event.preventDefault();
      if (graphHistory.canUndo) send({ action: "graph_undo" });
    } else if (key === "z" && event.shiftKey) {
      event.preventDefault();
      if (graphHistory.canRedo) send({ action: "graph_redo" });
    } else if (key === "y") {
      event.preventDefault();
      if (graphHistory.canRedo) send({ action: "graph_redo" });
    }
  });

  document.addEventListener("keyup", event => {
    if (event.code === "Space") graphSpaceDown = false;
  });

  window.addEventListener("blur", () => {
    graphSpaceDown = false;
  });

  window.addEventListener("hashchange", render);
  render();
})();
