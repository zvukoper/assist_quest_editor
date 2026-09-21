(() => {
  const NODE_WIDTH = 280;
  const MIN_VIEW_WIDTH = 260;
  const MAX_VIEW_WIDTH = 4200;

  let catalog = [];
  let documentState = null;
  let selectedNodeId = null;
  let pendingOutput = null;
  let viewport = { x: 0, y: 0, width: 1200, height: 720 };
  let dragging = null;
  let panning = null;
  let spaceDown = false;
  let contextMenu = null;

  const send = payload => {
    if (typeof window.__assistSend === "function") {
      window.__assistSend(payload);
      return;
    }
    window.chrome?.webview?.postMessage(payload);
  };

  const scene = () => documentState?.definition ?? null;
  const graph = () => scene()?.graph ?? null;

  function escapeHtml(value) {
    return String(value ?? "")
      .replaceAll("&", "&amp;")
      .replaceAll("<", "&lt;")
      .replaceAll(">", "&gt;")
      .replaceAll('"', "&quot;")
      .replaceAll("'", "&#39;");
  }

  function nodeHeight(node) {
    const inputCount = node.sockets.filter(socket => socket.direction === "Input").length;
    const outputCount = node.sockets.filter(socket => socket.direction === "Output").length;
    return Math.max(116, 78 + Math.max(inputCount, outputCount) * 24);
  }

  function nodePosition(node) {
    return { x: Number(node.x) || 0, y: Number(node.y) || 0 };
  }

  function currentNode() {
    return graph()?.nodes.find(node => node.nodeId === selectedNodeId) ?? null;
  }

  function postViewport(svg) {
    if (!svg) return;
    svg.setAttribute(
      "viewBox",
      viewport.x + " " + viewport.y + " " + viewport.width + " " + viewport.height
    );
  }

  function clientToGraph(svg, clientX, clientY) {
    const rect = svg.getBoundingClientRect();
    return {
      x: viewport.x + ((clientX - rect.left) / Math.max(1, rect.width)) * viewport.width,
      y: viewport.y + ((clientY - rect.top) / Math.max(1, rect.height)) * viewport.height
    };
  }

  function fitGraph(svg) {
    const g = graph();
    if (!g?.nodes?.length || !svg) return;

    const minX = Math.min(...g.nodes.map(node => nodePosition(node).x));
    const minY = Math.min(...g.nodes.map(node => nodePosition(node).y));
    const maxX = Math.max(...g.nodes.map(node => nodePosition(node).x + NODE_WIDTH));
    const maxY = Math.max(...g.nodes.map(node => nodePosition(node).y + nodeHeight(node)));

    viewport = {
      x: minX - 80,
      y: minY - 80,
      width: Math.max(420, maxX - minX + 160),
      height: Math.max(260, maxY - minY + 160)
    };
    postViewport(svg);
  }

  function socketPosition(node, socket) {
    const position = nodePosition(node);
    const inputs = node.sockets.filter(item => item.direction === "Input");
    const outputs = node.sockets.filter(item => item.direction === "Output");

    if (socket.direction === "Input") {
      const index = Math.max(0, inputs.findIndex(item => item.socketId === socket.socketId));
      return { x: position.x, y: position.y + 24 + index * 24 };
    }

    const index = Math.max(0, outputs.findIndex(item => item.socketId === socket.socketId));
    return { x: position.x + NODE_WIDTH, y: position.y + 24 + index * 24 };
  }

  function edgeMarkup(connection) {
    const g = graph();
    const fromNode = g.nodes.find(node => node.nodeId === connection.fromNodeId);
    const toNode = g.nodes.find(node => node.nodeId === connection.toNodeId);
    if (!fromNode || !toNode) return "";

    const fromSocket = fromNode.sockets.find(socket => socket.socketId === connection.fromSocketId);
    const toSocket = toNode.sockets.find(socket => socket.socketId === connection.toSocketId);
    if (!fromSocket || !toSocket) return "";

    const a = socketPosition(fromNode, fromSocket);
    const b = socketPosition(toNode, toSocket);
    const bend = Math.max(70, Math.abs(b.x - a.x) * 0.45);

    return "<path class='edge' d='M" + a.x + " " + a.y +
      " C" + (a.x + bend) + " " + a.y +
      " " + (b.x - bend) + " " + b.y +
      " " + b.x + " " + b.y + "'></path>";
  }

  function nodeMarkup(node) {
    const selected = node.nodeId === selectedNodeId ? " selected" : "";
    const source = pendingOutput?.nodeId === node.nodeId ? " connection-source" : "";
    const position = nodePosition(node);
    const height = nodeHeight(node);
    const inputs = node.sockets.filter(socket => socket.direction === "Input");
    const outputs = node.sockets.filter(socket => socket.direction === "Output");

    return "<g class='node" + selected + source + "' data-node-id='" +
      escapeHtml(node.nodeId) + "' transform='translate(" + position.x + " " + position.y + ")'>" +
      "<rect class='nodeRect' rx='8' width='" + NODE_WIDTH + "' height='" + height + "'></rect>" +
      "<text class='nodeTitle' x='16' y='24'>" + escapeHtml(node.title) + "</text>" +
      "<text class='nodeType' x='16' y='46'>" + escapeHtml(node.nodeType) + "</text>" +
      "<text class='nodeId' x='16' y='64'>" + escapeHtml(node.nodeId) + "</text>" +
      inputs.map((socket, index) =>
        "<g class='sceneSocket inputSocket' data-socket-direction='Input' data-socket-id='" +
        escapeHtml(socket.socketId) + "' transform='translate(0 " + (24 + index * 24) + ")'>" +
        "<circle class='socket' cx='0' cy='0' r='7'></circle>" +
        "<text x='14' y='5'>" + escapeHtml(socket.name) + "</text></g>"
      ).join("") +
      outputs.map((socket, index) =>
        "<g class='sceneSocket outputSocket' data-socket-direction='Output' data-socket-id='" +
        escapeHtml(socket.socketId) + "' transform='translate(" + NODE_WIDTH + " " +
        (24 + index * 24) + ")'>" +
        "<circle class='socket' cx='0' cy='0' r='7'></circle>" +
        "<text x='-14' y='5' text-anchor='end'>" + escapeHtml(socket.name) + "</text></g>"
      ).join("") +
      "</g>";
  }

  function addParameterRow(container, key = "", value = "") {
    const row = document.createElement("div");
    row.setAttribute("data-param-row", "");
    row.className = "fieldGrid";
    row.style.marginBottom = "6px";
    row.innerHTML =
      "<input data-param-key placeholder='Ключ' value='" + escapeHtml(key) + "'>" +
      "<input data-param-value placeholder='Значение' value='" + escapeHtml(value) + "'>" +
      "<button type='button' class='toolButton' data-remove-param>×</button>";
    row.querySelector("[data-remove-param]").addEventListener("click", () => row.remove());
    container.appendChild(row);
  }

  function parameterEditor(ins, node) {
    const container = ins.querySelector("#sceneParameterEditor");
    if (!container) return;

    container.innerHTML = "";
    const entries = Object.entries(node.parameters || {});
    if (!entries.length) {
      container.innerHTML = "<div class='notice'>Параметров нет.</div>";
      return;
    }

    entries.forEach(([key, value]) => addParameterRow(container, key, value));
  }

  function collectParameters(ins) {
    const result = {};
    ins.querySelectorAll("[data-param-row]").forEach(row => {
      const key = row.querySelector("[data-param-key]")?.value.trim();
      if (key) {
        result[key] = row.querySelector("[data-param-value]")?.value ?? "";
      }
    });
    return result;
  }

  function renderInspector(ins) {
    const node = currentNode();
    if (!node) {
      ins.innerHTML =
        "<div class='badge accent'>Scene Graph</div>" +
        "<h3 style='margin:10px 0 4px'>Нода не выбрана</h3>" +
        "<div class='notice'>Output → Input создаёт связь. DEL удаляет ноду после подтверждения.</div>";
      return;
    }

    const connections = graph().connections.filter(connection =>
      connection.fromNodeId === node.nodeId || connection.toNodeId === node.nodeId);

    const diagnostics = (documentState.validation || []).filter(item => item.nodeId === node.nodeId);
    ins.innerHTML =
      "<div class='badge accent'>" + escapeHtml(node.nodeType) + "</div>" +
      "<h3 style='margin:10px 0 4px'>" + escapeHtml(node.title) + "</h3>" +
      "<div class='field'><label>NodeId</label><input value='" + escapeHtml(node.nodeId) + "' disabled></div>" +
      "<div class='field' style='margin-top:8px'><label>Название</label><input id='sceneEditTitle' value='" + escapeHtml(node.title) + "'></div>" +
      "<div class='fieldGrid' style='margin-top:8px'>" +
        "<div class='field'><label>X</label><input id='sceneEditX' type='number' value='" + node.x + "'></div>" +
        "<div class='field'><label>Y</label><input id='sceneEditY' type='number' value='" + node.y + "'></div>" +
      "</div>" +
      "<div class='field' style='margin-top:8px'><label>NodeType</label><input value='" + escapeHtml(node.nodeType) + "' disabled></div>" +
      "<div class='miniLabel' style='margin-top:14px'>Параметры</div>" +
      "<div id='sceneParameterEditor' style='margin-top:6px'></div>" +
      "<button class='toolButton' id='addSceneParameter' style='margin-top:6px'>Добавить параметр</button>" +
      "<button class='toolButton primary' id='saveSceneNode' style='margin-top:10px'>Сохранить свойства</button>" +
      "<button class='toolButton' id='deleteSceneNode' style='margin-top:6px'>Удалить ноду</button>" +
      "<div class='miniLabel' style='margin-top:14px'>Sockets</div>" +
      "<div class='tableLike' style='margin-top:6px'>" +
        node.sockets.map(socket =>
          "<div class='tableRow'><span><strong>" + escapeHtml(socket.name) + "</strong><small>" +
          escapeHtml(socket.socketId) + "</small></span><span>" + escapeHtml(socket.direction) + "</span></div>"
        ).join("") +
      "</div>" +
      "<div class='miniLabel' style='margin-top:14px'>Связи</div>" +
      "<div class='tableLike' style='margin-top:6px'>" +
        (connections.length
          ? connections.map(connection =>
            "<div class='tableRow'><span><strong>" +
            escapeHtml(connection.fromNodeId) + " → " + escapeHtml(connection.toNodeId) +
            "</strong><small>" + escapeHtml(connection.fromSocketId) + " → " +
            escapeHtml(connection.toSocketId) + "</small></span>" +
            "<button class='toolButton' data-disconnect='" +
            encodeURIComponent(JSON.stringify(connection)) + "'>Удалить</button></div>"
          ).join("")
          : "<div class='notice'>Связей пока нет.</div>") +
      "</div>" +
      (diagnostics.length
        ? "<div class='miniLabel' style='margin-top:14px'>Диагностика</div><div class='tableLike'>" +
          diagnostics.map(item =>
            "<div class='tableRow'><span><strong>" + escapeHtml(item.code) +
            "</strong><small>" + escapeHtml(item.message) + "</small></span><span class='badge red'>" +
            escapeHtml(item.severity) + "</span></div>"
          ).join("") + "</div>"
        : "");

    parameterEditor(ins, node);

    ins.querySelector("#addSceneParameter").addEventListener("click", () => {
      const container = ins.querySelector("#sceneParameterEditor");
      if (container.querySelector(".notice")) container.innerHTML = "";
      addParameterRow(container);
    });

    ins.querySelector("#saveSceneNode").addEventListener("click", () => send({
      action: "scene_update_node",
      nodeId: node.nodeId,
      title: ins.querySelector("#sceneEditTitle").value,
      x: Number(ins.querySelector("#sceneEditX").value),
      y: Number(ins.querySelector("#sceneEditY").value),
      parameters: collectParameters(ins)
    }));

    ins.querySelector("#deleteSceneNode").addEventListener("click", () => requestDelete(node.nodeId));

    ins.querySelectorAll("[data-disconnect]").forEach(button => {
      button.addEventListener("click", () => {
        const connection = JSON.parse(decodeURIComponent(button.dataset.disconnect));
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
    return target instanceof Element &&
      Boolean(target.closest("input,textarea,select,[contenteditable='true']"));
  }

  function requestDelete(nodeId) {
    if (!nodeId || !window.confirm("Удалить выбранную ноду и все связанные соединения?")) return;
    selectedNodeId = null;
    pendingOutput = null;
    send({ action: "scene_remove_node", nodeId });
  }

  function removeSocketConnections(nodeId, socketId) {
    const connections = graph().connections.filter(connection =>
      (connection.fromNodeId === nodeId && connection.fromSocketId === socketId) ||
      (connection.toNodeId === nodeId && connection.toSocketId === socketId));

    if (connections.length === 1) {
      send({
        action: "scene_disconnect",
        fromNodeId: connections[0].fromNodeId,
        fromSocketId: connections[0].fromSocketId,
        toNodeId: connections[0].toNodeId,
        toSocketId: connections[0].toSocketId
      });
      return;
    }

    if (connections.length > 1 && window.confirm("Удалить все связи этого socket?")) {
      connections.forEach(connection => send({
        action: "scene_disconnect",
        fromNodeId: connection.fromNodeId,
        fromSocketId: connection.fromSocketId,
        toNodeId: connection.toNodeId,
        toSocketId: connection.toSocketId
      }));
    }
  }

  function nodeTypes() {
    return [
      ["SceneStart", "Начало сцены"],
      ["Dialogue", "Диалог"],
      ["Choice", "Выбор"],
      ["SceneWait", "Ожидание"],
      ["SceneEvent", "Событие"],
      ["SceneEnd", "Конец сцены"]
    ];
  }

  function closeContextMenu() {
    contextMenu?.remove();
    contextMenu = null;
  }

  function showContextMenu(clientX, clientY, nodeId, socketId, socketDirection) {
    closeContextMenu();
    const menu = document.createElement("div");
    menu.className = "contextMenu";

    if (socketId && nodeId) {
      menu.innerHTML = "<button class='contextMenuItem'>Удалить соединение</button>";
      menu.querySelector("button").addEventListener("click", () => {
        closeContextMenu();
        removeSocketConnections(nodeId, socketId);
      });
    } else if (nodeId) {
      menu.innerHTML = "<button class='contextMenuItem'>Удалить ноду</button>";
      menu.querySelector("button").addEventListener("click", () => {
        closeContextMenu();
        requestDelete(nodeId);
      });
    } else {
      const scroll = document.createElement("div");
      scroll.style.minWidth = "250px";
      scroll.style.maxHeight = Math.max(120, window.innerHeight - 20) + "px";
      scroll.style.overflowY = "auto";

      nodeTypes().forEach(item => {
        const button = document.createElement("button");
        button.className = "contextMenuItem";
        button.textContent = item[1] + " (" + item[0] + ")";
        button.addEventListener("click", () => {
          closeContextMenu();
          send({
            action: "scene_add_node",
            nodeType: item[0],
            title: item[1],
            x: Math.max(80, viewport.x + viewport.width * 0.35),
            y: Math.max(80, viewport.y + viewport.height * 0.35)
          });
        });
        scroll.appendChild(button);
      });

      menu.appendChild(scroll);
      menu.addEventListener("wheel", event => event.stopPropagation(), { passive: true });
    }

    menu.style.left = clientX + "px";
    menu.style.top = clientY + "px";
    document.body.appendChild(menu);
    contextMenu = menu;

    requestAnimationFrame(() => {
      const rect = menu.getBoundingClientRect();
      const left = Math.max(8, Math.min(clientX, window.innerWidth - rect.width - 8));
      const top = Math.max(8, Math.min(clientY, window.innerHeight - rect.height - 8));
      menu.style.left = left + "px";
      menu.style.top = top + "px";
    });
  }

  function bindCanvas(svg) {
    svg.addEventListener("contextmenu", event => {
      event.preventDefault();
      const socket = event.target.closest?.(".sceneSocket");
      const nodeGroup = event.target.closest?.("[data-node-id]");

      if (socket && nodeGroup) {
        showContextMenu(
          event.clientX,
          event.clientY,
          nodeGroup.dataset.nodeId,
          socket.dataset.socketId,
          socket.dataset.socketDirection
        );
        return;
      }

      if (nodeGroup) {
        showContextMenu(event.clientX, event.clientY, nodeGroup.dataset.nodeId, null, null);
        return;
      }

      showContextMenu(event.clientX, event.clientY, null, null, null);
    });

    svg.addEventListener("pointerdown", event => {
      const socket = event.target.closest?.(".sceneSocket");
      const nodeGroup = event.target.closest?.("[data-node-id]");

      if (event.button === 1 || (event.button === 0 && spaceDown)) {
        event.preventDefault();
        panning = {
          clientX: event.clientX,
          clientY: event.clientY,
          x: viewport.x,
          y: viewport.y
        };
        return;
      }

      if (event.button !== 0) return;

      if (socket && nodeGroup) {
        event.preventDefault();
        const nodeId = nodeGroup.dataset.nodeId;
        const socketId = socket.dataset.socketId;

        if (socket.dataset.socketDirection === "Output") {
          pendingOutput = { nodeId, socketId };
          renderViewport(false);
          return;
        }

        if (socket.dataset.socketDirection === "Input" && pendingOutput) {
          send({
            action: "scene_connect",
            fromNodeId: pendingOutput.nodeId,
            fromSocketId: pendingOutput.socketId,
            toNodeId: nodeId,
            toSocketId: socketId
          });
          pendingOutput = null;
          return;
        }

        return;
      }

      if (nodeGroup) {
        event.preventDefault();
        selectedNodeId = nodeGroup.dataset.nodeId;
        const node = currentNode();
        const point = clientToGraph(svg, event.clientX, event.clientY);

        dragging = {
          nodeId: node.nodeId,
          offsetX: point.x - nodePosition(node).x,
          offsetY: point.y - nodePosition(node).y,
          moved: false
        };
        renderViewport(false);
        return;
      }

      selectedNodeId = null;
      pendingOutput = null;
      renderViewport(false);
    });

    svg.addEventListener("pointermove", event => {
      if (panning) {
        const dx = event.clientX - panning.clientX;
        const dy = event.clientY - panning.clientY;
        const rect = svg.getBoundingClientRect();
        viewport.x = panning.x - dx * viewport.width / Math.max(1, rect.width);
        viewport.y = panning.y - dy * viewport.height / Math.max(1, rect.height);
        postViewport(svg);
        return;
      }

      if (!dragging) return;

      const node = graph()?.nodes.find(item => item.nodeId === dragging.nodeId);
      if (!node) {
        dragging = null;
        return;
      }

      const point = clientToGraph(svg, event.clientX, event.clientY);
      node.x = point.x - dragging.offsetX;
      node.y = point.y - dragging.offsetY;
      dragging.moved = true;
      renderViewport(false);
    });

    window.addEventListener("pointerup", () => {
      if (panning) panning = null;

      if (dragging) {
        const node = graph()?.nodes.find(item => item.nodeId === dragging.nodeId);
        const moved = dragging.moved;
        dragging = null;

        if (node && moved) {
          send({
            action: "scene_update_node",
            nodeId: node.nodeId,
            title: node.title,
            x: Number(node.x),
            y: Number(node.y),
            parameters: { ...(node.parameters || {}) }
          });
        }
      }
    });

    svg.addEventListener("wheel", event => {
      event.preventDefault();
      const before = clientToGraph(svg, event.clientX, event.clientY);
      const factor = event.deltaY < 0 ? 0.88 : 1.14;
      const nextWidth = Math.max(MIN_VIEW_WIDTH, Math.min(MAX_VIEW_WIDTH, viewport.width * factor));
      const nextHeight = Math.max(180, Math.min(3200, viewport.height * factor));
      const rect = svg.getBoundingClientRect();
      const rx = (event.clientX - rect.left) / Math.max(1, rect.width);
      const ry = (event.clientY - rect.top) / Math.max(1, rect.height);

      viewport.width = nextWidth;
      viewport.height = nextHeight;
      viewport.x = before.x - rx * nextWidth;
      viewport.y = before.y - ry * nextHeight;
      postViewport(svg);
    });
  }

  function renderViewport(runFit) {
    const svg = document.getElementById("sceneGraphSvg");
    const ins = document.getElementById("inspector");
    if (!svg || !graph()) return;

    document.getElementById("sceneGraphEdges").innerHTML =
      graph().connections.map(edgeMarkup).join("");
    document.getElementById("sceneGraphNodes").innerHTML =
      graph().nodes.map(nodeMarkup).join("");
    renderInspector(ins);

    if (runFit) fitGraph(svg);
  }

  function render(ws, ins) {
    if (!documentState?.definition) {
      ws.innerHTML = "<div class='notice'>Ожидание Scene Definition от Host…</div>";
      ins.innerHTML = "";
      return;
    }

    const model = documentState.definition;
    const errors = (documentState.validation || []).filter(item => item.severity === "Error");
    const warnings = (documentState.validation || []).filter(item => item.severity === "Warning");

    ws.innerHTML =
      "<div class='toolbar' style='margin-bottom:10px;flex-wrap:wrap'>" +
      "<button class='toolButton' id='newScene'>Новая</button>" +
      "<button class='toolButton' id='openScene'>Открыть</button>" +
      "<button class='toolButton' id='openLastScene' " +
        (documentState.lastDocumentPath ? "" : "disabled") + ">Последняя JSON</button>" +
      "<button class='toolButton primary' id='saveScene'>Сохранить</button>" +
      "<button class='toolButton' id='saveSceneAs'>Сохранить как…</button>" +
      "<span class='badge " + (documentState.documentDirty ? "accent" : "blue") + "'>" +
        (documentState.documentDirty ? "Не сохранено" : "Сохранено") + "</span>" +
      "<select id='sceneResource' class='toolButton'>" +
        catalog.map(item =>
          "<option value='" + escapeHtml(item.id) + "'" +
          (item.id === model.id ? " selected" : "") + ">" +
          escapeHtml(item.title) + "</option>"
        ).join("") +
      "</select>" +
      "<select id='sceneNodeType' class='toolButton'>" +
        nodeTypes().map(item =>
          "<option value='" + escapeHtml(item[0]) + "'>" +
          escapeHtml(item[1]) + " (" + escapeHtml(item[0]) + ")</option>"
        ).join("") +
      "</select>" +
      "<input id='sceneNodeTitle' class='toolButton' style='width:180px' placeholder='Название новой ноды'>" +
      "<button class='toolButton primary' id='addSceneNode'>Добавить ноду</button>" +
      "<button class='toolButton' id='layoutScene' title='Каноническая раскладка в Domain'>Перестроить</button>" +
      "<button class='toolButton' id='fitScene'>По размеру</button>" +
      "<button class='toolButton' id='undoScene' " + (documentState.canUndo ? "" : "disabled") + ">↶ Отменить</button>" +
      "<button class='toolButton' id='redoScene' " + (documentState.canRedo ? "" : "disabled") + ">↷ Повторить</button>" +
      "<button class='toolButton' id='validateScene'>Проверить</button>" +
      "<span class='badge accent'>" + model.graph.nodes.length + " нод</span>" +
      "<span class='badge'>" + model.graph.connections.length + " связей</span>" +
      "<span class='badge " + (errors.length ? "red" : "blue") + "'>" +
        errors.length + " ошибок</span>" +
      "<span class='badge " + (warnings.length ? "accent" : "blue") + "'>" +
        warnings.length + " предупреждений</span>" +
      "</div>" +
      "<div id='sceneGraphCanvasHost' style='height:calc(100% - 55px);min-height:560px;border:1px solid var(--border);border-radius:8px;overflow:hidden;background:#111419'>" +
        "<svg id='sceneGraphSvg' viewBox='" + viewport.x + " " + viewport.y + " " +
        viewport.width + " " + viewport.height + "' xmlns='http://www.w3.org/2000/svg' " +
        "style='width:100%;height:100%;touch-action:none'>" +
          "<g id='sceneGraphEdges'>" + model.graph.connections.map(edgeMarkup).join("") + "</g>" +
          "<g id='sceneGraphNodes'>" + model.graph.nodes.map(nodeMarkup).join("") + "</g>" +
        "</svg>" +
      "</div>";

    document.getElementById("newScene").addEventListener("click", () => send({ action: "scene_new" }));
    document.getElementById("openScene").addEventListener("click", () => send({ action: "scene_open" }));
    document.getElementById("openLastScene").addEventListener("click", () => send({ action: "scene_open_last" }));
    document.getElementById("saveScene").addEventListener("click", () => send({ action: "scene_save" }));
    document.getElementById("saveSceneAs").addEventListener("click", () => send({ action: "scene_save_as" }));
    document.getElementById("sceneResource").addEventListener("change", event => {
      send({ action: "scene_open_resource", sceneId: event.target.value });
    });

    document.getElementById("addSceneNode").addEventListener("click", () => {
      const type = document.getElementById("sceneNodeType").value;
      const title = document.getElementById("sceneNodeTitle").value.trim() || type;
      send({ action: "scene_add_node", nodeType: type, title, x: viewport.x + viewport.width * 0.3, y: viewport.y + viewport.height * 0.35 });
    });

    document.getElementById("layoutScene").addEventListener("click", () => send({ action: "scene_layout" }));
    document.getElementById("fitScene").addEventListener("click", () => fitGraph(document.getElementById("sceneGraphSvg")));
    document.getElementById("undoScene").addEventListener("click", () => send({ action: "scene_undo" }));
    document.getElementById("redoScene").addEventListener("click", () => send({ action: "scene_redo" }));
    document.getElementById("validateScene").addEventListener("click", () => send({ action: "scene_validate" }));

    bindCanvas(document.getElementById("sceneGraphSvg"));
    renderInspector(ins);
    fitGraph(document.getElementById("sceneGraphSvg"));
  }

  const handleSceneHostMessage = event => {
    const data = typeof event.data === "string"
      ? JSON.parse(event.data)
      : event.data;
    if (!data) return;

    if (data.type === "scene_catalog") {
      catalog = Array.isArray(data.scenes) ? data.scenes : [];
      if (location.hash.toLowerCase() === "#scene") {
        const ws = document.getElementById("workspace");
        const ins = document.getElementById("inspector");
        if (ws && ins) render(ws, ins);
      }
      return;
    }

    if (data.type === "scene_definition") {
      documentState = data;
      selectedNodeId = data.selectedNodeId || selectedNodeId;
      pendingOutput = null;

      if (location.hash.toLowerCase() === "#scene") {
        const ws = document.getElementById("workspace");
        const ins = document.getElementById("inspector");
        if (ws && ins) render(ws, ins);
      }
    }
  };

  window.addEventListener("message", handleSceneHostMessage);

  const webview = window.chrome?.webview;
  if (typeof webview?.addEventListener === "function") {
    webview.addEventListener("message", handleSceneHostMessage);
  }

  document.addEventListener("keydown", event => {
    if (location.hash.toLowerCase() !== "#scene") return;

    if (event.code === "Space" && !isTextEntryTarget(event.target)) {
      spaceDown = true;
      event.preventDefault();
    }

    if (event.key === "Escape") {
      if (contextMenu) {
        closeContextMenu();
        event.preventDefault();
        return;
      }

      if (pendingOutput) {
        pendingOutput = null;
        render(document.getElementById("workspace"), document.getElementById("inspector"));
        event.preventDefault();
        return;
      }
    }

    if (
      event.key === "Delete" &&
      selectedNodeId &&
      !isTextEntryTarget(event.target)
    ) {
      requestDelete(selectedNodeId);
      event.preventDefault();
      return;
    }

    if ((event.ctrlKey || event.metaKey) && !isTextEntryTarget(event.target)) {
      if (event.code === "KeyZ" && !event.shiftKey) {
        event.preventDefault();
        send({ action: "scene_undo" });
      } else if ((event.code === "KeyZ" && event.shiftKey) || event.code === "KeyY") {
        event.preventDefault();
        send({ action: "scene_redo" });
      }
    }
  });

  document.addEventListener("keyup", event => {
    if (event.code === "Space") spaceDown = false;
  });

  document.addEventListener("pointerdown", event => {
    if (contextMenu && !contextMenu.contains(event.target)) closeContextMenu();
  });

  window.addEventListener("blur", () => {
    spaceDown = false;
    closeContextMenu();
    dragging = null;
    panning = null;
  });

  window.__assistSceneEditor = {
    render,
    getState: () => ({ documentState, catalog, selectedNodeId, viewport: { ...viewport } })
  };
})();