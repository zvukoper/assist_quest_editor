(() => {
  const map = document.getElementById("mapCanvas");
  const side = document.getElementById("side");
  const hud = document.getElementById("hud");
  const runtimeSide = document.getElementById("runtimeSide");
  const playerOverlay = document.getElementById("playerOverlay");
  const backpackButton = document.getElementById("backpackButton");
  const backpackNewDot = document.getElementById("backpackNewDot");
  const inventoryPanel = document.getElementById("inventoryPanel");
  const characterPanel = document.getElementById("characterPanel");
  const characterTabs = document.getElementById("characterTabs");
  const characterTabBody = document.getElementById("characterTabBody");
  const inventoryNotifications = document.getElementById("inventoryNotifications");
  const send = payload => window.chrome?.webview?.postMessage(payload);

  let snapshot = null;
  let runtime = null;
  let draggingPlayer = false;
  let panning = false;
  let panStart = null;
  let selectedPointId = null;
  let hoveredPointId = null;
  let questGraph = null;
  let campaigns = [];
  let questActivations = [];
  let simulationRunning = false;
  let camera = { cx: 0, cz: 0, mpp: 50 };
  let eventHistory = [];
  let runtimeTargetKey = "";
  let dragPlayerPosition = null;
  let journalDetached = false;
  let lastQuestVisualLogKey = "";
  let lastQuestTargetResolutionKey = "";
  let gameplayInventoryOpen = false;
  const locallySeenInventoryItems = new Set();
  let uiRenderScheduled = false;
  const DISTANCE_RINGS = [25, 50, 100, 250, 500, 1000, 1500, 2000];

  function formatPosition(position) {
    if (!position) return "—";
    return "X " + Math.round(position.x) + " · Y " + Math.round(position.y) + " · Z " + Math.round(position.z);
  }

  const escapeHtml = value => String(value ?? "").replace(/[&<>"']/g, char => ({
    "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;", "'": "&#39;"
  }[char]));

  function runtimeStatusName(status) {
    if (typeof status === "string") return status;
    const numeric = Number(status);
    return Number.isInteger(numeric)
      ? ([ "Stopped", "Running", "Waiting", "Completed", "Failed" ][numeric] || String(status))
      : String(status ?? "");
  }

  function logQuestTargetResolution(stage) {
    const info = runtimeTargetInfo();
    const key = stage + "|" + runtimeStatusName(runtime?.status) + "|" +
      String(runtime?.currentNodeId || "") + "|" + String(info.point?.id || "") + "|" + info.kind;
    if (key === lastQuestTargetResolutionKey) return info;
    lastQuestTargetResolutionKey = key;
    window.assistQuestLog?.("INFO", "Quest UI: целевая точка вычислена.", {
      stage,
      runtimeStatus: runtimeStatusName(runtime?.status),
      rawRuntimeStatus: runtime?.status,
      currentNodeId: runtime?.currentNodeId || null,
      targetKind: info.kind,
      targetPointId: info.point?.id || null,
      targetName: info.point?.name || null,
      targetCategory: info.point?.category || null,
      targetPosition: info.point?.position || null,
      radius: info.radius,
      found: !!info.point
    });
    return info;
  }

  const sections = [
    ["player", "Игрок и мир"],
    ["facts", "Факты"],
    ["statuses", "Статусы"],
    ["states", "Состояния"],
    ["vitals", "Потребности"],
    ["progress", "Деньги / опыт"],
    ["character", "Персонаж"],
    ["inventory", "Инвентарь"],
    ["reputation", "Репутация"],
    ["telemetry", "Телеметрия"],
    ["environment", "Окружение"],
    ["events", "События"],
    ["system", "Система"]
  ];

  function visibleSize() {
    return { width: map.clientWidth || 1, height: map.clientHeight || 1 };
  }

  function worldToScreen(x, z) {
    const s = visibleSize();
    return {
      x: s.width / 2 + (x - camera.cx) / camera.mpp,
      y: s.height / 2 + (z - camera.cz) / camera.mpp
    };
  }

  function screenToWorld(x, y) {
    const s = visibleSize();
    return {
      x: camera.cx + (x - s.width / 2) * camera.mpp,
      z: camera.cz + (y - s.height / 2) * camera.mpp
    };
  }

  function pointerPosition(event) {
    const rect = map.getBoundingClientRect();
    return { x: event.clientX - rect.left, y: event.clientY - rect.top };
  }

  function fitWorld() {
    const points = snapshot?.world?.points || [];
    const player = snapshot?.player?.position;
    const coords = [];
    for (const p of points) coords.push([p.position.x, p.position.z]);
    if (player) coords.push([player.x, player.z]);
    if (!coords.length) {
      camera = { cx: 0, cz: 0, mpp: 50 };
      return;
    }

    const xs = coords.map(v => v[0]);
    const zs = coords.map(v => v[1]);
    const minX = Math.min(...xs), maxX = Math.max(...xs);
    const minZ = Math.min(...zs), maxZ = Math.max(...zs);
    const s = visibleSize();
    camera.cx = (minX + maxX) / 2;
    camera.cz = (minZ + maxZ) / 2;
    camera.mpp = Math.max(
      1,
      Math.max(maxX - minX, 100) / Math.max(1, s.width - 80),
      Math.max(maxZ - minZ, 100) / Math.max(1, s.height - 80)
    );
  }

  function drawMap() {
    if (!snapshot) return;
    const dpr = window.devicePixelRatio || 1;
    const width = map.clientWidth || 1;
    const height = map.clientHeight || 1;
    const targetWidth = Math.max(1, Math.round(width * dpr));
    const targetHeight = Math.max(1, Math.round(height * dpr));
    if (map.width !== targetWidth || map.height !== targetHeight) {
      map.width = targetWidth;
      map.height = targetHeight;
    }

    const ctx = map.getContext("2d");
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, width, height);
    ctx.fillStyle = "#0d1014";
    ctx.fillRect(0, 0, width, height);
    drawGrid(ctx, width, height);
    drawQuestActivations(ctx, width, height);

    const points = snapshot.world?.points || [];
    const showAllLabels = camera.mpp < 24;
    const labelLimit = camera.mpp < 70 ? 140 : (camera.mpp < 180 ? 55 : 24);
    const occupied = new Set();

    for (const point of points) {
      const q = worldToScreen(point.position.x, point.position.z);
      if (q.x < -18 || q.y < -18 || q.x > width + 18 || q.y > height + 18) continue;

      const selected = point.id === selectedPointId;
      const hovered = point.id === hoveredPointId;
      const isCity = point.isCity === true;
      const radius = isCity
        ? (hovered ? 5.2 : 3.4)
        : (selected ? 5.5 : (hovered ? 5.2 : (camera.mpp > 250 ? 3.8 : 4.2)));

      ctx.save();
      ctx.beginPath();
      ctx.arc(q.x, q.y, radius, 0, Math.PI * 2);
      ctx.fillStyle = point.color || "#78c8f0";
      ctx.shadowColor = selected
        ? "rgba(250,176,3,.95)"
        : hovered
          ? "rgba(255,255,255,.9)"
          : "rgba(0,0,0,.78)";
      ctx.shadowBlur = selected ? 14 : (hovered ? 10 : 5);
      ctx.shadowOffsetX = 0;
      ctx.shadowOffsetY = 1.5;
      ctx.fill();

      if (selected) {
        ctx.beginPath();
        ctx.arc(q.x, q.y, radius + 5, 0, Math.PI * 2);
        ctx.lineWidth = 4.5;
        ctx.strokeStyle = "#fab003";
        ctx.shadowColor = "rgba(250,176,3,.9)";
        ctx.shadowBlur = 16;
        ctx.stroke();
      } else if (hovered) {
        ctx.beginPath();
        ctx.arc(q.x, q.y, radius + 3.5, 0, Math.PI * 2);
        ctx.lineWidth = isCity ? 2 : 2.5;
        ctx.strokeStyle = "#ffffff";
        ctx.shadowColor = "rgba(255,255,255,.75)";
        ctx.shadowBlur = 9;
        ctx.stroke();
      }
      ctx.restore();

      if (selected || hovered || (isCity && camera.mpp < 220) ||
          shouldLabel(q, showAllLabels, labelLimit, occupied, point)) {
        drawPointLabel(ctx, point, q, selected, isCity);
      }
    }

    const playerForDraw = currentPlayerForDraw();
    drawDistanceRings(ctx, width, height, playerForDraw?.position);
    drawRuntimeTarget(ctx, playerForDraw?.position);
    drawPlayer(ctx, playerForDraw);

    const visualInfo = runtimeTargetInfo();
    const targetScreen = visualInfo.point
      ? worldToScreen(visualInfo.point.position.x, visualInfo.point.position.z)
      : null;
    const playerScreen = playerForDraw?.position
      ? worldToScreen(playerForDraw.position.x, playerForDraw.position.z)
      : null;
    const targetVisible = !!targetScreen &&
      targetScreen.x >= -24 && targetScreen.y >= -24 &&
      targetScreen.x <= width + 24 && targetScreen.y <= height + 24;
    const visualKey = [
      runtimeStatusName(runtime?.status),
      runtime?.currentNodeId || "",
      visualInfo.point?.id || "",
      Math.round(playerScreen?.x || 0),
      Math.round(playerScreen?.y || 0),
      Math.round(targetScreen?.x || 0),
      Math.round(targetScreen?.y || 0),
      Math.round(camera.mpp * 100) / 100
    ].join("|");
    if (visualKey !== lastQuestVisualLogKey) {
      lastQuestVisualLogKey = visualKey;
      window.assistQuestLog?.("INFO", "Quest UI: карта перерисована.", {
        runtimeStatus: runtimeStatusName(runtime?.status),
        rawRuntimeStatus: runtime?.status,
        currentNodeId: runtime?.currentNodeId || null,
        targetPointId: visualInfo.point?.id || null,
        targetHighlighted: !!visualInfo.point,
        targetRendered: !!targetScreen,
        targetVisible,
        targetScreen,
        playerScreen,
        targetRadiusPx: Number.isFinite(visualInfo.radius) ? visualInfo.radius / camera.mpp : null,
        measurementRingsRendered: DISTANCE_RINGS.filter(value => value / camera.mpp >= 3)
      });
    }

    drawCategoryLegend(ctx, width, height, points);
    drawScaleBar(ctx, width, height);
    drawHud();
  }

  function drawGrid(ctx, width, height) {
    const step = niceGridStep(camera.mpp);
    if (!Number.isFinite(step) || step / camera.mpp < 18) return;

    const left = camera.cx - width * camera.mpp / 2;
    const right = camera.cx + width * camera.mpp / 2;
    const top = camera.cz - height * camera.mpp / 2;
    const bottom = camera.cz + height * camera.mpp / 2;

    ctx.save();
    ctx.font = "9px Open Sans, Arial, sans-serif";
    ctx.textBaseline = "top";
    ctx.lineWidth = 1;

    for (let x = Math.floor(left / step) * step; x <= right; x += step) {
      const sx = worldToScreen(x, camera.cz).x;
      const major = Math.abs(x) < 0.001 || Math.abs(x / step) % 5 < 0.001;
      ctx.strokeStyle = major ? "rgba(255,255,255,.105)" : "rgba(255,255,255,.055)";
      ctx.beginPath();
      ctx.moveTo(sx, 0);
      ctx.lineTo(sx, height);
      ctx.stroke();

      if (sx >= 0 && sx <= width && major) {
        ctx.fillStyle = "rgba(210,220,232,.68)";
        ctx.fillText("X " + Math.round(x) + " м", Math.min(width - 60, sx + 4), 5);
      }
    }

    for (let z = Math.floor(top / step) * step; z <= bottom; z += step) {
      const sy = worldToScreen(camera.cx, z).y;
      const major = Math.abs(z) < 0.001 || Math.abs(z / step) % 5 < 0.001;
      ctx.strokeStyle = major ? "rgba(255,255,255,.105)" : "rgba(255,255,255,.055)";
      ctx.beginPath();
      ctx.moveTo(0, sy);
      ctx.lineTo(width, sy);
      ctx.stroke();

      if (sy >= 0 && sy <= height - 14 && major) {
        ctx.fillStyle = "rgba(210,220,232,.68)";
        ctx.fillText("Z " + Math.round(z) + " м", 5, sy + 3);
      }
    }

    ctx.restore();
  }

  function drawDistanceRings(ctx, width, height, playerPosition = null) {
    const p = playerPosition || snapshot?.player?.position;
    if (!p) return;

    const center = worldToScreen(p.x, p.z);
    ctx.save();
    ctx.textBaseline = "middle";
    ctx.font = "600 10px Open Sans, Arial, sans-serif";

    for (const distanceMeters of DISTANCE_RINGS) {
      const radiusPx = distanceMeters / camera.mpp;
      if (radiusPx < 3) continue;

      ctx.beginPath();
      ctx.arc(center.x, center.y, radiusPx, 0, Math.PI * 2);
      ctx.lineWidth = distanceMeters <= 100 ? 1.5 : 1;
      ctx.setLineDash(distanceMeters <= 100 ? [] : [5, 5]);
      ctx.strokeStyle = distanceMeters <= 100
        ? "rgba(255,255,255,.36)"
        : "rgba(157,181,205,.24)";
      ctx.stroke();

      const angle = -Math.PI / 4;
      const labelX = center.x + Math.cos(angle) * radiusPx;
      const labelY = center.y + Math.sin(angle) * radiusPx;
      const label = distanceMeters.toLocaleString("ru-RU") + " м";
      const widthLabel = Math.max(30, ctx.measureText(label).width + 8);
      const x = Math.max(4, Math.min(width - widthLabel - 4, labelX + 4));
      const y = Math.max(12, Math.min(height - 12, labelY));

      ctx.fillStyle = "rgba(10,12,16,.86)";
      ctx.fillRect(x - 2, y - 8, widthLabel, 16);
      ctx.fillStyle = "#d9e2ec";
      ctx.fillText(label, x + 2, y);
    }

    ctx.setLineDash([]);
    ctx.restore();
  }

  function niceDistance(value) {
    const raw = Math.max(1, value);
    const power = Math.pow(10, Math.floor(Math.log10(raw)));
    for (const mult of [1, 2, 2.5, 5, 10]) {
      if (mult * power >= raw) return mult * power;
    }
    return 10 * power;
  }

  function drawScaleBar(ctx, width, height) {
    const distance = niceDistance(camera.mpp * Math.min(180, width * 0.2));
    const pixelWidth = distance / camera.mpp;
    const x = 16;
    const y = height - 24;

    ctx.save();
    ctx.lineWidth = 3;
    ctx.strokeStyle = "rgba(255,255,255,.85)";
    ctx.beginPath();
    ctx.moveTo(x, y);
    ctx.lineTo(x + pixelWidth, y);
    ctx.stroke();

    ctx.fillStyle = "rgba(10,12,16,.86)";
    ctx.fillRect(x - 3, y - 21, pixelWidth + 6, 17);
    ctx.fillStyle = "#d9e2ec";
    ctx.font = "600 10px Open Sans, Arial, sans-serif";
    ctx.textBaseline = "middle";
    ctx.fillText(distance.toLocaleString("ru-RU") + " м", x + 4, y - 12);
    ctx.restore();
  }

  function niceGridStep(mpp) {
    const raw = Math.max(1, mpp * 70);
    const power = Math.pow(10, Math.floor(Math.log10(raw)));
    for (const mult of [1, 2, 5, 10]) {
      if (mult * power >= raw) return mult * power;
    }
    return 10 * power;
  }

  function shouldLabel(q, showAll, limit, occupied, point) {
    if (showAll) return true;
    const cell = Math.max(22, Math.round(22 + camera.mpp / 2));
    const key = Math.floor(q.x / cell) + ":" + Math.floor(q.y / cell);
    if (occupied.has(key) || occupied.size >= limit) return false;
    occupied.add(key);
    return true;
  }

  function drawPointLabel(ctx, point, q, selected, isCity = point.isCity === true) {
    const text = point.name || point.category || "СДО";
    const offset = isCity ? 9 : 10;
    ctx.save();
    ctx.font = isCity
      ? "600 11px Open Sans, Arial, sans-serif"
      : (selected ? "700 12px Open Sans, Arial, sans-serif" : "600 10px Open Sans, Arial, sans-serif");
    ctx.textAlign = "left";
    ctx.textBaseline = "middle";
    ctx.lineWidth = selected ? 4 : (isCity ? 3.5 : 3);
    ctx.strokeStyle = "rgba(0,0,0,.98)";
    ctx.strokeText(text, q.x + 10, q.y - offset);
    ctx.fillStyle = isCity ? "#e2c85f" : (point.color || "#78c8f0");
    ctx.fillText(text, q.x + 10, q.y - offset);
    ctx.restore();
  }

  function drawPlayer(ctx, player) {
    const p = player?.position;
    if (!p) return;
    const q = worldToScreen(p.x, p.z);
    const canvasSize = visibleSize();
    if (q.x < -50 || q.y < -50 || q.x > canvasSize.width + 50 || q.y > canvasSize.height + 50) return;

    const radius = 7.5;

    // Оранжевый «перекрест» через всю карту: вертикальная и горизонтальная
    // линии всегда проходят через игрока, поэтому его позицию видно сразу,
    // даже когда маркер теряется среди СДО. Рисуется под маркером и почти
    // прозрачный, чтобы не мешать чтению карты.
    ctx.save();
    ctx.beginPath();
    ctx.moveTo(q.x, 0);
    ctx.lineTo(q.x, canvasSize.height);
    ctx.moveTo(0, q.y);
    ctx.lineTo(canvasSize.width, q.y);
    ctx.lineWidth = 1;
    ctx.strokeStyle = "rgba(245,158,11,.24)";
    ctx.stroke();
    ctx.restore();

    ctx.save();

    // Тень под маркером: мягкое радиальное затемнение со смещением вниз.
    // Затемнение выходит за чёрную обводку, поэтому маркер «приподнят» над
    // картой и читается поверх светлых элементов (сетка, кольца, подписи).
    const shadowOffsetY = 4;
    const shadowRadius = radius * 3;
    const shadowGradient = ctx.createRadialGradient(
      q.x, q.y + shadowOffsetY, radius * 0.3,
      q.x, q.y + shadowOffsetY, shadowRadius
    );
    shadowGradient.addColorStop(0, "rgba(0,0,0,.96)");
    shadowGradient.addColorStop(0.42, "rgba(0,0,0,.62)");
    shadowGradient.addColorStop(1, "rgba(0,0,0,0)");
    ctx.beginPath();
    ctx.arc(q.x, q.y + shadowOffsetY, shadowRadius, 0, Math.PI * 2);
    ctx.fillStyle = shadowGradient;
    ctx.fill();

    // Заливка маркера.
    ctx.beginPath();
    ctx.arc(q.x, q.y, radius, 0, Math.PI * 2);
    ctx.fillStyle = "#f59e0b";
    ctx.fill();

    // Красная обводка.
    ctx.lineWidth = 3;
    ctx.strokeStyle = "#d83a3a";
    ctx.stroke();

    // Чёрная обводка снаружи красной: отделяет маркер от светлых элементов
    // карты и от линий перекреста, сохраняя ту же толщину, что и красная.
    ctx.beginPath();
    ctx.arc(q.x, q.y, radius + 3, 0, Math.PI * 2);
    ctx.lineWidth = 3;
    ctx.strokeStyle = "rgba(0,0,0,.95)";
    ctx.stroke();

    ctx.restore();
  }

  function currentPlayerForDraw() {
    if (!snapshot?.player) return null;
    if (!dragPlayerPosition) return snapshot.player;

    return {
      ...snapshot.player,
      position: {
        ...snapshot.player.position,
        x: dragPlayerPosition.x,
        z: dragPlayerPosition.z
      }
    };
  }

  function hitPlayer(px, py) {
    const p = snapshot?.player?.position;
    if (!p) return false;
    const q = worldToScreen(p.x, p.z);
    return Math.hypot(q.x - px, q.y - py) <= 22;
  }

  function hitPoint(px, py) {
    const points = snapshot?.world?.points || [];
    let best = null;
    let bestDistance = Math.min(18, Math.max(8, 10 / Math.sqrt(camera.mpp)));
    for (const point of points) {
      const q = worldToScreen(point.position.x, point.position.z);
      if (q.x < -20 || q.y < -20 || q.x > visibleSize().width + 20 || q.y > visibleSize().height + 20) continue;
      const distance = Math.hypot(q.x - px, q.y - py);
      if (distance <= bestDistance) {
        best = point;
        bestDistance = distance;
      }
    }
    return best;
  }


  function drawCategoryLegend(ctx, width, height, points) {
    const counts = new Map();
    for (const point of points) {
      if (point.isCity) continue;
      const q = worldToScreen(point.position.x, point.position.z);
      if (q.x < 0 || q.y < 0 || q.x > width || q.y > height) continue;
      const key = point.category || point.name || "СДО";
      const existing = counts.get(key);
      if (existing) {
        existing.count += 1;
      } else {
        counts.set(key, {
          count: 1,
          color: point.color || "#78c8f0",
          name: key
        });
      }
    }

    const categories = [...counts.values()]
      .sort((a, b) => b.count - a.count || a.name.localeCompare(b.name, "ru"))
      .slice(0, 10);

    if (!categories.length) return;

    const lineH = 18;
    const pad = 9;
    const boxW = 230;
    const boxH = pad * 2 + lineH * (categories.length + 1);

    ctx.save();
    ctx.fillStyle = "rgba(10,12,16,.88)";
    ctx.strokeStyle = "rgba(255,255,255,.12)";
    ctx.lineWidth = 1;
    ctx.beginPath();
    ctx.roundRect(width - boxW - 12, 12, boxW, boxH, 8);
    ctx.fill();
    ctx.stroke();

    ctx.font = "600 11px Open Sans, Arial, sans-serif";
    ctx.textAlign = "left";
    ctx.textBaseline = "middle";
    ctx.fillStyle = "#e7edf4";
    ctx.fillText("Категории СДО", width - boxW + 1, 12 + pad + lineH / 2);

    categories.forEach((entry, index) => {
      const y = 12 + pad + lineH * (index + 1) + lineH / 2;

      ctx.beginPath();
      ctx.arc(width - boxW + 7, y, 4, 0, Math.PI * 2);
      ctx.fillStyle = entry.color;
      ctx.fill();

      ctx.textAlign = "left";
      ctx.fillStyle = "#d9e2ec";
      const label = entry.name.length > 25 ? entry.name.slice(0, 24) + "…" : entry.name;
      ctx.fillText(label, width - boxW + 18, y);

      ctx.textAlign = "right";
      ctx.fillStyle = "#8f9baa";
      ctx.fillText(String(entry.count), width - 20, y);
    });

    ctx.restore();
  }

  function runtimeStatusLabel(status) {
    return ({
      Running: "Выполняется",
      Waiting: "Ожидает",
      Completed: "Завершён",
      Failed: "Ошибка",
      Stopped: "Остановлен"
    })[status] || status || "Остановлен";
  }

  function getRuntimeNode() {
    const nodeId = runtime?.currentNodeId;
    return nodeId && questGraph?.nodes
      ? questGraph.nodes.find(node => node.nodeId === nodeId) || null
      : null;
  }

  function nodeParameter(node, key, fallback = "") {
    if (!node?.parameters) return fallback;
    const entry = Object.entries(node.parameters)
      .find(([name]) => name.toLowerCase() === key.toLowerCase());
    return entry ? entry[1] : fallback;
  }

  function findPoint(pointId) {
    if (!pointId || !snapshot?.world?.points) return null;
    return snapshot.world.points.find(point =>
      point.id?.toLowerCase() === String(pointId).toLowerCase());
  }

  function distanceMeters(a, b) {
    if (!a || !b) return null;
    const dx = Number(a.x) - Number(b.x);
    const dy = Number(a.y) - Number(b.y);
    const dz = Number(a.z) - Number(b.z);
    return Math.sqrt(dx * dx + dy * dy + dz * dz);
  }

  function formatMeters(value) {
    if (!Number.isFinite(value)) return "—";
    if (value >= 1000) return (value / 1000).toLocaleString("ru-RU", { maximumFractionDigits: 2 }) + " км";
    return Math.round(value).toLocaleString("ru-RU") + " м";
  }

  function runtimeTargetInfo() {
    const node = getRuntimeNode();
    if (!node) return { kind: "none", html: "<div class='notice'>Текущая нода ещё не определена.</div>", point: null, radius: null };

    const type = String(node.nodeType || "").toLowerCase();
    let pointId = "";
    let radius = null;

    if (type === "interaction") {
      pointId = nodeParameter(node, "worldPointId");
      radius = Number(nodeParameter(node, "triggerRadius", "35"));
    } else if (type === "waitforcondition" || type === "condition") {
      const operator = nodeParameter(node, "operator", "");
      if (operator.toLowerCase() === "distancecompare") {
        pointId = nodeParameter(node, "worldPointId", nodeParameter(node, "right"));
        radius = Number(nodeParameter(node, "triggerRadius", "35"));
      }
    }

    if (pointId) {
      const point = findPoint(pointId);
      if (!point) {
        return {
          kind: "missing",
          point: null,
          radius,
          html:
            "<div class='targetMissing'><strong>Целевая СДО не найдена</strong>" +
            "<div>WorldPoint Id: <code>" + escapeHtml(pointId) + "</code></div></div>"
        };
      }

      const player = snapshot?.player?.position;
      const distance = distanceMeters(player, point.position);
      const radiusText = Number.isFinite(radius) ? formatMeters(radius) : "—";
      const stateText = Number.isFinite(distance) && Number.isFinite(radius)
        ? (distance <= radius ? "Игрок уже в радиусе" : "Нужно приблизиться ещё на " + formatMeters(Math.max(0, distance - radius)))
        : "Расстояние не определено";

      return {
        kind: "point",
        point,
        radius,
        html:
          "<div class='targetTitle'>Приблизить игрока к:</div>" +
          "<strong class='targetName'>" + escapeHtml(point.name || point.category || point.id) + "</strong>" +
          "<div class='targetCategory'>" + escapeHtml(point.category || "СДО") + "</div>" +
          "<div class='kv'><span>Координаты</span><span>" + formatPosition(point.position) + "</span></div>" +
          "<div class='kv'><span>Радиус</span><span>" + radiusText + "</span></div>" +
          "<div class='kv'><span>Сейчас до точки</span><span>" + formatMeters(distance) + "</span></div>" +
          "<div class='targetStatus'>" + escapeHtml(stateText) + "</div>",
        point
      };
    }

    if (type === "waitforevent") {
      const eventType = nodeParameter(node, "eventType", "—");
      return {
        kind: "event",
        point: null,
        radius: null,
        html:
          "<div class='targetTitle'>Ожидается событие:</div>" +
          "<strong class='eventTarget'>" + escapeHtml(eventType) + "</strong>" +
          "<div class='notice' style='margin-top:8px'>Для теста можно отправить это событие через раздел «События» справа.</div>"
      };
    }

    if (type === "choice") {
      const outputs = (node.sockets || []).filter(socket => socket.direction === "Output").length;
      return {
        kind: "choice",
        point: null,
        radius: null,
        html:
          "<div class='targetTitle'>Ожидается выбор игрока</div>" +
          "<div class='kv'><span>Вариантов</span><span>" + outputs + "</span></div>" +
          "<div class='notice' style='margin-top:8px'>Событие ChoiceSelected приходит из тестового интерфейса.</div>"
      };
    }

    if (type === "wait") {
      const seconds = Number(nodeParameter(node, "seconds", "1"));
      return {
        kind: "wait",
        point: null,
        radius: null,
        html:
          "<div class='targetTitle'>Квест стоит на паузе</div>" +
          "<div class='kv'><span>Длительность</span><span>" + (Number.isFinite(seconds) ? seconds.toLocaleString("ru-RU") + " с" : "—") + "</span></div>"
      };
    }

    const operator = nodeParameter(node, "operator", "");
    if (type === "waitforcondition" || type === "condition") {
      return {
        kind: "condition",
        point: null,
        radius: null,
        html:
          "<div class='targetTitle'>Ожидается условие</div>" +
          (operator ? "<div class='kv'><span>Оператор</span><span>" + escapeHtml(operator) + "</span></div>" : "") +
          "<div class='notice' style='margin-top:8px'>Проверь параметры текущей ноды в Нодовом редакторе.</div>"
      };
    }

    return { kind: "none", point: null, radius: null, html: "<div class='notice'>Для этой ноды специальная цель ожидания не требуется.</div>" };
  }

  function runtimeExpectedEvent() {
    const node = getRuntimeNode();
    if (!node || runtimeStatusName(runtime?.status) !== "Waiting") return null;

    const type = String(node.nodeType || "").toLowerCase();
    if (type === "waitforevent") {
      const eventType = nodeParameter(node, "eventType", "");
      if (!eventType) return null;
      return { eventType, source: "Simulator", payload: {} };
    }

    if (type === "choice") {
      return { eventType: "ChoiceSelected", source: "Simulator", payload: { index: "1" } };
    }

    return null;
  }

  function focusRuntimeTarget() {
    if (runtimeStatusName(runtime?.status) !== "Waiting") {
      runtimeTargetKey = "";
      return;
    }

    const info = runtimeTargetInfo();
    if (info.kind !== "point" || !info.point) {
      runtimeTargetKey = "";
      return;
    }

    const key = String(runtime.currentNodeId || "") + "|" + String(info.point.id || "");
    if (key === runtimeTargetKey) return;

    runtimeTargetKey = key;
    camera.cx = Number(info.point.position.x);
    camera.cz = Number(info.point.position.z);

    const radius = Number.isFinite(info.radius) && info.radius > 0 ? info.radius : 100;
    camera.mpp = Math.max(0.25, Math.min(25, Math.max(radius / 90, 0.5)));
  }

  function formatEventTimestamp(timestamp) {
    if (timestamp === null || timestamp === undefined || timestamp === "") {
      return "время неизвестно";
    }

    const parsed = timestamp instanceof Date
      ? timestamp
      : new Date(typeof timestamp === "number" && timestamp < 100000000000
        ? timestamp * 1000
        : timestamp);

    if (!Number.isFinite(parsed.getTime())) {
      return "время неизвестно";
    }

    return parsed.toLocaleTimeString("ru-RU");
  }

  function drawQuestActivations(ctx, width, height) {
    if (!questActivations?.length) return;

    for (const marker of questActivations) {
      if (!marker?.position) continue;

      const center = worldToScreen(marker.position.x, marker.position.z);
      const radius = Number(marker.radius);
      const radiusPx = Number.isFinite(radius) ? radius / camera.mpp : 0;
      const available = marker.available !== false;

      if (radiusPx >= 2) {
        ctx.save();
        ctx.beginPath();
        ctx.arc(center.x, center.y, radiusPx, 0, Math.PI * 2);
        ctx.setLineDash([7, 5]);
        ctx.lineWidth = available ? 2 : 1.5;
        ctx.strokeStyle = available
          ? "rgba(250,176,3,.72)"
          : "rgba(140,150,165,.42)";
        ctx.fillStyle = available
          ? "rgba(250,176,3,.045)"
          : "rgba(140,150,165,.018)";
        ctx.fill();
        ctx.stroke();
        ctx.restore();
      }

      const pulse = 8 + Math.sin(Date.now() / 320) * 1.5;
      ctx.save();
      ctx.beginPath();
      ctx.moveTo(center.x, center.y - pulse);
      ctx.lineTo(center.x + pulse, center.y);
      ctx.lineTo(center.x, center.y + pulse);
      ctx.lineTo(center.x - pulse, center.y);
      ctx.closePath();
      ctx.fillStyle = available ? "#fab003" : "#707b89";
      ctx.fill();
      ctx.lineWidth = 2.5;
      ctx.strokeStyle = "rgba(0,0,0,.95)";
      ctx.stroke();

      ctx.font = "900 9px Open Sans, Arial, sans-serif";
      ctx.textAlign = "center";
      ctx.textBaseline = "middle";
      ctx.fillStyle = "#101319";
      ctx.fillText("Q", center.x, center.y + 0.5);

      const label = (marker.questTitle || marker.questId || "Квест") +
        (available ? "" : " · условие не выполнено");
      const maxLabelWidth = 240;
      const measured = Math.min(maxLabelWidth, ctx.measureText(label).width + 12);
      const x = Math.max(6, Math.min(width - measured - 6, center.x - measured / 2));
      const y = Math.max(6, Math.min(height - 24, center.y + pulse + 8));

      ctx.fillStyle = "rgba(10,12,16,.9)";
      ctx.fillRect(x, y, measured, 18);
      ctx.strokeStyle = available ? "rgba(250,176,3,.45)" : "rgba(140,150,165,.28)";
      ctx.lineWidth = 1;
      ctx.strokeRect(x, y, measured, 18);
      ctx.fillStyle = available ? "#f4c04d" : "#aab2bd";
      ctx.textAlign = "center";
      ctx.textBaseline = "middle";
      ctx.font = "700 9px Open Sans, Arial, sans-serif";
      ctx.fillText(
        label.length > 38 ? label.slice(0, 35) + "…" : label,
        x + measured / 2,
        y + 9);
      ctx.restore();
    }
  }

  function drawRuntimeTarget(ctx, playerPosition = null) {
    if (!snapshot || runtimeStatusName(runtime?.status) !== "Waiting" || !questGraph) return;
    const info = runtimeTargetInfo();
    if (!info.point) return;

    const player = playerPosition || snapshot.player?.position;
    const target = worldToScreen(info.point.position.x, info.point.position.z);
    const playerScreen = player ? worldToScreen(player.x, player.z) : null;

    if (Number.isFinite(info.radius)) {
      const radiusPx = info.radius / camera.mpp;
      if (radiusPx >= 2) {
        ctx.save();
        ctx.beginPath();
        ctx.arc(target.x, target.y, radiusPx, 0, Math.PI * 2);
        ctx.setLineDash([8, 5]);
        ctx.lineWidth = 2;
        ctx.strokeStyle = "rgba(250,176,3,.78)";
        ctx.stroke();
        ctx.restore();
      }
    }

    ctx.save();
    const pulse = 12 + Math.sin(Date.now() / 180) * 2;
    ctx.beginPath();
    ctx.arc(target.x, target.y, pulse + 5, 0, Math.PI * 2);
    ctx.lineWidth = 2;
    ctx.strokeStyle = "rgba(250,176,3,.48)";
    ctx.stroke();

    ctx.beginPath();
    ctx.arc(target.x, target.y, 10, 0, Math.PI * 2);
    ctx.lineWidth = 3;
    ctx.strokeStyle = "#ffffff";
    ctx.stroke();
    ctx.beginPath();
    ctx.arc(target.x, target.y, 5, 0, Math.PI * 2);
    ctx.fillStyle = "#fab003";
    ctx.fill();
    ctx.font = "800 11px Open Sans, Arial, sans-serif";
    ctx.textBaseline = "bottom";
    ctx.fillStyle = "#fab003";
    ctx.strokeStyle = "rgba(0,0,0,.95)";
    ctx.lineWidth = 4;
    ctx.strokeText("ЦЕЛЬ", target.x + 12, target.y - 12);
    ctx.fillText("ЦЕЛЬ", target.x + 12, target.y - 12);
    if (playerScreen) {
      ctx.beginPath();
      ctx.moveTo(playerScreen.x, playerScreen.y);
      ctx.lineTo(target.x, target.y);
      ctx.setLineDash([6, 5]);
      ctx.lineWidth = 1.5;
      ctx.strokeStyle = "rgba(250,176,3,.55)";
      ctx.stroke();
    }
    ctx.restore();
  }


  const CHARACTER_STAT_LABELS = {
    strength: "Сила",
    perception: "Восприятие",
    endurance: "Выносливость",
    charisma: "Харизма",
    intelligence: "Интеллект",
    agility: "Ловкость",
    luck: "Удача"
  };

  function itemCatalogDefinition(itemId) {
    const key = String(itemId || "").toLowerCase();
    const found = (window.__assistItemCatalog || []).find(item =>
      String(item.id || "").toLowerCase() === key);
    return found || {
      id: itemId,
      name: itemId,
      description: "Предмет без зарегистрированного описания.",
      category: "Неизвестный предмет",
      color: "#59636d"
    };
  }

  function inventoryNewItemIds() {
    return new Set((snapshot?.inventory?.newItemIds || []).map(value =>
      String(value).toLowerCase()));
  }

  function isInventoryItemNew(itemId) {
    return inventoryNewItemIds().has(String(itemId).toLowerCase()) &&
      !locallySeenInventoryItems.has(String(itemId).toLowerCase());
  }

  function percent(value, max) {
    const maximum = Number(max);
    return Math.max(0, Math.min(100,
      maximum > 0 ? Number(value || 0) / maximum * 100 : 0));
  }

  function renderVitalsPanel() {
    const v = snapshot?.playerVitals || {};
    const n = value => Math.round(Number(value || 0));
    return [
      "<div class='gamePanelHeader'><div><div class='gamePanelTitle'>Инвентарь</div><div class='gamePanelSub'>Состояние и содержимое</div></div><div class='gamePanelSub'>I — открыть / закрыть</div></div>",
      "<div class='gameVitals'>",
        "<div class='vitalRow' data-game-tooltip='Здоровье: текущее значение от 0 до максимума.'><span class='vitalLabel'>Здоровье</span><div class='vitalTrack'><div class='vitalFill health' style='width:" + percent(v.health, v.maxHealth) + "%'></div></div><span class='vitalValue'>" + n(v.health) + "%</span></div>",
        "<div class='vitalDual'>",
          "<div class='vitalDualCell' data-game-tooltip='Энергия: запас сил игрока.'><span class='vitalLabel'>Энергия</span><div class='vitalTrack'><div class='vitalFill energy' style='width:" + percent(v.energy, v.maxEnergy) + "%'></div></div></div>",
          "<div class='vitalDualCell' data-game-tooltip='Жидкость: уровень гидратации игрока.'><span class='vitalLabel'>Жидкость</span><div class='vitalTrack'><div class='vitalFill hydration' style='width:" + percent(v.hydration, v.maxHydration) + "%'></div></div></div>",
        "</div>",
        "<div class='vitalRow' data-game-tooltip='Усталость: 0 — полностью отдохнул, 100 — максимальная усталость.'><span class='vitalLabel'>Усталость</span><div class='vitalTrack'><div class='vitalFill fatigue' style='width:" + percent(v.fatigue, v.maxFatigue) + "%'></div></div><span class='vitalValue'>" + n(v.fatigue) + "%</span></div>",
      "</div>"
    ].join("");
  }

  function renderInventoryGrid() {
    const items = Object.entries(snapshot?.inventory?.items || {})
      .filter(([, quantity]) => Number(quantity) > 0)
      .sort(([a], [b]) => a.localeCompare(b));

    const slots = [];
    for (let index = 0; index < 24; index++) {
      const pair = items[index];
      if (!pair) {
        slots.push("<div class='inventorySlot empty' data-game-tooltip='Свободная ячейка инвентаря.'></div>");
        continue;
      }

      const [itemId, quantity] = pair;
      const item = itemCatalogDefinition(itemId);
      const isNew = isInventoryItemNew(itemId);
      slots.push(
        "<div class='inventorySlot' data-game-tooltip='" + escapeHtml(item.description || item.name) + "'>" +
          "<button class='inventoryItemButton' type='button' data-inventory-item='" + escapeHtml(itemId) + "' data-game-tooltip='" + escapeHtml((item.name || itemId) + ". " + (item.description || "")) + "'>" +
            "<span class='inventoryItemSquare' style='background:" + escapeHtml(item.color || "#59636d") + "'>" + (String(itemId).toLowerCase() === "ruslan.raw_meat" ? "М" : "") + "</span>" +
            "<span class='inventoryItemName'>" + escapeHtml(item.name || itemId) + (isNew ? " <span class='inventoryNewDot' aria-label='Новый предмет'></span>" : "") + "</span>" +
            "<span class='inventoryItemQty'>×" + Number(quantity) + "</span>" +

          "</button>" +
        "</div>"
      );
    }
    return slots.join("");
  }

  function renderInventoryPanel() {
    if (!inventoryPanel || !snapshot) return;
    const progress = snapshot.playerProgress || {};
    inventoryPanel.innerHTML =
      renderVitalsPanel() +
      "<div class='inventoryGrid'>" + renderInventoryGrid() + "</div>" +
      "<div class='inventoryFooter'>" +
        "<div class='inventoryFooterCell' data-game-tooltip='Деньги игрока.'>₽ <strong>" + Number(progress.money || 0).toLocaleString("ru-RU") + "</strong></div>" +
        "<div class='inventoryFooterCell' data-game-tooltip='Опыт игрока. Увеличивается через AddExperience.'>XP <strong>" + Number(progress.experience || 0).toLocaleString("ru-RU") + "</strong></div>" +
        "<div class='inventoryFooterCell' data-game-tooltip='Резервный ресурс для будущих механик.'>Резерв <strong>" + Number(progress.reserve || 0).toLocaleString("ru-RU") + "</strong></div>" +
      "</div>";

    inventoryPanel.querySelectorAll("[data-inventory-item]").forEach(button => {
      button.addEventListener("mouseenter", () => {
        const id = String(button.dataset.inventoryItem || "").toLowerCase();
        if (!id) return;
        locallySeenInventoryItems.add(id);
        send({ action: "mark_inventory_seen", itemId: id });
        renderGameplayPanels();
      });
    });
  }

  // Активный таб правой панели: "character" или "reputation".
  let characterTab = "character";

  /**
   * Строки списка репутации.
   *
   * Показываются только НПЦ, с которыми контакт состоялся: иначе список
   * заполнялся бы незнакомцами с нулевой репутацией. Порядок — по убыванию
   * репутации, при равенстве по имени, чтобы важные НПЦ были сверху.
   */
  function reputationRows() {
    const entries = Object.entries(snapshot?.reputation?.entries || {});
    const catalog = snapshot?.npcCatalog || [];

    return entries
      .map(([npcId, entry]) => {
        const npc = catalog.find(item => item.id === npcId) || {};
        return {
          npcId,
          name: npc.name || npcId,
          avatar: npc.avatar || "",
          value: Number(entry?.value || 0),
          contacted: Boolean(entry?.contacted)
        };
      })
      .filter(entry => entry.contacted)
      .sort((a, b) => b.value - a.value || a.name.localeCompare(b.name, "ru"));
  }

  function renderReputationPanel() {
    const rows = reputationRows();

    if (!rows.length) {
      return (
        "<div class='reputationEmpty'>" +
          "<div class='gamePanelSub'>Пока нет НПЦ, с которыми состоялся контакт.</div>" +
          "<div class='notice' style='margin-top:8px'>Репутация появляется после первой реплики или выбора в диалоге.</div>" +
        "</div>"
      );
    }

    return (
      "<div class='reputationList'>" +
        rows.map(row => {
          const view = ReputationScaleView(row.npcId, row.value);
          return (
            "<article class='reputationRow' data-reputation-npc='" + escapeHtml(row.npcId) + "'>" +
              "<img class='reputationAvatar' src='" + escapeHtml(resolveAssetUrl(row.avatar)) + "'" +
                " alt='' loading='lazy' onerror=\"this.removeAttribute('src');this.className+=' missing'\">" +
              "<div class='reputationBody'>" +
                "<div class='reputationTop'>" +
                  "<span class='reputationName'>" + escapeHtml(row.name) + "</span>" +
                  "<span class='reputationValue'>" + escapeHtml(view.valueLabel) + "</span>" +
                "</div>" +
                "<div class='reputationRange' style='color:" + escapeHtml(view.rangeColor) + "'>" +
                  escapeHtml(view.rangeName) +
                "</div>" +
                "<div class='reputationTrack' data-game-tooltip='" + escapeHtml(view.tooltip || row.name) + "'>" +
                  "<div class='reputationFill' style=\"width:" + view.progressPercent + "%;background:" + escapeHtml(view.fillColor) + "\"></div>" +
                "</div>" +
              "</div>" +
            "</article>"
          );
        }).join("") +
      "</div>"
    );
  }

  /**
   * Преобразует путь ресурса игры в URL, понятный странице.
   *
   * Страницы лежат в подкаталоге Web, а data/ — рядом с executable, поэтому
   * относительный путь "data/images/..." со страницы Web/ уходил бы в
   * несуществующий Web/data/... и давал битое изображение. Ведущий "../"
   * возвращает путь к корню приложения — эта форма проверена и для file://,
   * и для виртуального хоста WebView2, в отличие от пути с ведущим слешем,
   * который под file:// уходит в корень диска.
   *
   * Внешние URL (http:, data:, blob:) не трогаем — их отдаёт не приложение.
   */
  function resolveAssetUrl(path) {
    if (!path) return "";
    if (/^(?:[a-z][a-z0-9+.-]*:|\/\/)/i.test(path)) return path;

    var trimmed = path.replace(/^\.?\//, "");
    return "../" + trimmed;
  }

  /**
   * Представление репутации для UI.
   *
   * Пороги, цвета и проценты считает домен и присылает готовым в
   * `reputationViews`, поэтому здесь нет таблицы диапазонов. Если данных нет
   * (например, в тестовой фикстуре), используется нейтральное представление,
   * а не падение.
   */
  function ReputationScaleView(npcId, value) {
    const view = snapshot?.reputationViews?.[npcId];
    if (view) return view;

    return {
      value,
      valueLabel: value > 0 ? "+" + value : String(value),
      rangeName: "Нейтральный",
      rangeColor: "#c8ccd2",
      fillColor: "#8f9baa",
      progressPercent: Math.min(100, Math.abs(value) / 100),
      tooltip: "Репутация: " + value
    };
  }

  function renderCharacterTabs() {
    if (!characterTabs) return;

    const tabs = [
      ["character", "Персонаж"],
      ["reputation", "Репутация"]
    ];

    characterTabs.innerHTML = tabs.map(([id, label]) =>
      "<button class='gamePanelTab" + (characterTab === id ? " active" : "") + "' " +
        "type='button' role='tab' data-character-tab='" + id + "' " +
        "aria-selected='" + (characterTab === id ? "true" : "false") + "'>" +
        escapeHtml(label) +
      "</button>"
    ).join("");

    characterTabs.querySelectorAll("[data-character-tab]").forEach(button => {
      button.addEventListener("click", () => {
        const next = button.dataset.characterTab;
        if (!next || next === characterTab) return;
        characterTab = next;
        renderCharacterPanel();
      });
    });
  }

  function renderCharacterPanel() {
    if (!characterPanel || !snapshot) return;

    renderCharacterTabs();

    if (characterTab === "reputation") {
      if (characterTabBody) characterTabBody.innerHTML = renderReputationPanel();
      return;
    }

    const character = snapshot.character || {};
    const stats = Object.entries(character.stats || {});
    const skills = character.skills || [];

    if (characterTabBody) {
      characterTabBody.innerHTML =
        "<div class='characterBody'>" +
          "<div class='characterSectionTitle'>Статы</div>" +
          "<div class='characterStats'>" +
            stats.map(([key, value]) =>
              "<div class='characterStat' data-game-tooltip='" + escapeHtml((CHARACTER_STAT_LABELS[key] || key) + ": " + Number(value) + " из 10") + "'>" +
                "<span class='characterStatName'>" + escapeHtml(CHARACTER_STAT_LABELS[key] || key) + "</span>" +
                "<span class='characterStatValue'>" + Number(value) + "</span>" +
              "</div>"
            ).join("") +
          "</div>" +
          "<div class='characterSectionTitle' style='margin-top:12px'>Скиллы</div>" +
          "<div class='characterSkills'>" +
            skills.map(skill =>
              "<div class='characterSkill' data-game-tooltip='" + escapeHtml(skill.description || skill.name) + "'>" +
                "<span class='characterSkillName'>" + escapeHtml(skill.name) + "</span>" +
                "<span class='characterSkillLevel'>" + escapeHtml(skill.levelLabel || (skill.unlocked ? "Получен" : "Не изучен")) + "</span>" +
                "<div class='characterSkillDesc'>" + escapeHtml(skill.description || "") + "</div>" +
              "</div>"
            ).join("") +
          "</div>" +
        "</div>";
    }
  }

  function renderGameplayPanels() {
    if (!playerOverlay || !backpackButton || !snapshot) return;

    playerOverlay.classList.toggle("visible", gameplayInventoryOpen);
    playerOverlay.setAttribute("aria-hidden", gameplayInventoryOpen ? "false" : "true");
    backpackButton.setAttribute("aria-expanded", gameplayInventoryOpen ? "true" : "false");

    const hasNew = [...inventoryNewItemIds()].some(id =>
      !locallySeenInventoryItems.has(id));
    backpackButton.classList.toggle("hasNew", hasNew && !gameplayInventoryOpen);

    renderInventoryPanel();
    renderCharacterPanel();
  }

  function toggleGameplayInventory(force = null) {
    gameplayInventoryOpen = force === null ? !gameplayInventoryOpen : !!force;
    renderGameplayPanels();
  }

  function showInventoryNotification(itemId, delta) {
    if (!inventoryNotifications) return;

    const amount = Math.abs(Number(delta) || 0);
    const item = itemCatalogDefinition(itemId);
    const type = Number(delta) > 0 ? "add" : "remove";
    const toast = document.createElement("div");
    toast.className = "inventoryNotification " + type + " in";
    toast.innerHTML =
      "<div class='inventoryNotificationTitle'>" + (type === "add" ? "Получено" : "Изъято") + "</div>" +
      "<div class='inventoryNotificationName'>" + escapeHtml(item.name || itemId) + " ×" + amount + "</div>";
    inventoryNotifications.appendChild(toast);

    window.setTimeout(() => {
      toast.classList.add("closing");
      window.setTimeout(() => toast.remove(), 100);
    }, 5900);
  }

  function handleInventoryEvent(event) {
    if (!event || event.source !== "QuestRuntime") return;
    const payload = event.payload || {};
    const itemId = payload.itemId;
    const delta = Number(payload.delta || 0);
    if (!itemId || !delta) return;

    if (delta > 0) {
      locallySeenInventoryItems.delete(String(itemId).toLowerCase());
    }

    showInventoryNotification(itemId, delta);
    renderGameplayPanels();
  }

  function scheduleUiRender() {
    if (uiRenderScheduled) return;
    uiRenderScheduled = true;
    window.requestAnimationFrame(() => {
      uiRenderScheduled = false;
      drawMap();
      renderGameplayPanels();
    });
  }

  function renderQuestCatalog() {
    if (!campaigns.length) {
      return "<div class='notice'>Установленных кампаний нет.</div>";
    }

    const statusEntries = new Map(
      (snapshot?.questStatuses?.quests || []).map(item => [item.questId, item]));

    return campaigns.map(campaign => {
      const campaignChecked = !!campaign.active;
      const questRows = (campaign.quests || []).map(quest => {
        const enabled = String(quest.status || "").toLowerCase() === "enabled";
        const runtimeEntry = statusEntries.get(quest.questId);
        const runtimeStatus = runtimeEntry?.status || "Available";
        const runtimeLabel = runtimeStatusName(runtimeStatus);

        return "<div class='campaignQuestRow'>" +
          "<label class='campaignQuestMain'>" +
            "<input type='checkbox' data-quest-enabled='1' " +
              "data-campaign-id='" + escapeHtml(campaign.id) + "' " +
              "data-quest-id='" + escapeHtml(quest.questId) + "' " +
              (enabled ? "checked" : "") + ">" +
            "<span class='campaignQuestText'>" +
              "<strong>" + escapeHtml(quest.title || quest.questId) + "</strong>" +
              "<small>v" + Number(quest.version || 1) + " · " +
                escapeHtml(runtimeLabel) + "</small>" +
            "</span>" +
          "</label>" +
          "<span class='campaignQuestStatus " + (enabled ? "enabled" : "disabled") + "'>" +
            (enabled ? "Активен" : "Отключён") +
          "</span>" +
          "<button class='microButton' type='button' " +
            "data-open-quest='" + escapeHtml(quest.fullPath || "") + "'>Ред.</button>" +
        "</div>";
      }).join("");

      return "<section class='campaignBlock'>" +
        "<div class='campaignHeader'>" +
          "<label class='campaignMainLabel'>" +
            "<input type='checkbox' data-campaign-active='1' " +
              "data-campaign-id='" + escapeHtml(campaign.id) + "' " +
              (campaignChecked ? "checked" : "") + ">" +
            "<span>" +
              "<strong>" + escapeHtml(campaign.name || campaign.id) + "</strong>" +
              "<small>v" + Number(campaign.version || 1) + "</small>" +
            "</span>" +
          "</label>" +
          "<button class='microButton' type='button' " +
            "data-open-campaign='" + escapeHtml(campaign.id) + "' " +
            "title='Открыть папку кампании'>↗</button>" +
        "</div>" +
        "<div class='campaignQuestList'>" + questRows + "</div>" +
      "</section>";
    }).join("");
  }

  function renderRuntimeSidebar() {
    if (!runtimeSide || !snapshot) return;

    const status = runtimeStatusName(runtime?.status || "Stopped");
    const node = getRuntimeNode();
    const info = runtimeTargetInfo();
    const expectedEvent = runtimeExpectedEvent();
    const pointCount = snapshot.world?.points?.length || 0;

    runtimeSide.innerHTML =
      "<div class='runtimeSideHeader'>" +
        "<div>" +
          "<div class='panelTitle'>Квесты</div>" +
          "<div class='runtimeQuestName'>" +
            (simulationRunning ? "Симуляция запущена" : "Симуляция остановлена") +
          "</div>" +
        "</div>" +
      "</div>" +
      "<section class='questCatalog'>" +
        "<div class='questCatalogHeader'>" +
          "<div class='miniLabel'>Кампании и доступные квесты</div>" +
          "<div class='miniLabel'>" + campaigns.reduce((sum, x) => sum + (x.quests?.length || 0), 0) +
            "</div>" +
        "</div>" +
        "<div class='campaignList'>" + renderQuestCatalog() + "</div>" +
      "</section>" +
      "<div class='runtimeControls'>" +
        "<button class='smallButton' id='fitWorldSide'>Все СДО</button>" +
      "</div>" +
      "<div class='miniLabel' style='margin-top:8px'>СДО на карте: " +
        pointCount.toLocaleString("ru-RU") + "</div>" +
      "<div class='runtimeStatusCard' data-status='" + escapeHtml(String(status).toLowerCase()) + "'>" +
        "<span class='statusDot'></span><strong>" +
          escapeHtml(runtimeStatusLabel(status)) + "</strong>" +
      "</div>" +
      "<section class='runtimeBlock'>" +
        "<div class='miniLabel'>Текущая нода</div>" +
        (node
          ? "<div class='runtimeNodeName'>" + escapeHtml(node.title || node.nodeType) + "</div>" +
            "<div class='runtimeNodeType'>" + escapeHtml(node.nodeType) + " · " + escapeHtml(node.nodeId) + "</div>"
          : "<div class='notice' style='margin-top:6px'>" +
              (simulationRunning
                ? "Сейчас ни один квест не выполняется."
                : "Запусти симуляцию, чтобы Runtime начал реагировать.") +
            "</div>") +
      "</section>" +
      "<section class='runtimeBlock'>" +
        "<div class='miniLabel'>Что сейчас происходит</div>" +
        "<div class='runtimeTransition'>" +
          escapeHtml(runtime?.lastTransition || snapshot?.system?.lastTransition || "—") +
        "</div>" +
      "</section>" +
      "<section class='runtimeBlock runtimeTargetBlock'>" +
        "<div class='miniLabel'>Цель ожидания</div>" +
        info.html +
      "</section>" +
      "<section class='runtimeBlock'>" +
        "<div class='miniLabel'>Последнее событие</div>" +
        "<div class='kv'><span>Runtime</span><span>" +
          escapeHtml(runtime?.lastEvent || snapshot?.system?.lastEvent || "—") + "</span></div>" +
        "<div class='kv'><span>Ожидание</span><span>" +
          escapeHtml(runtime?.waitingFor || "—") + "</span></div>" +
      "</section>" +
      "<section class='runtimeBlock'>" +
        "<div class='runtimeBlockHeader'><div class='miniLabel'>События теста</div>" +
          (!journalDetached ? "<button class='microButton' id='detachJournal'>Отделить</button>" : "") +
        "</div>" +
        (expectedEvent
          ? "<div class='notice'><strong>Runtime ждёт:</strong> " + escapeHtml(expectedEvent.eventType) + "</div>" +
            "<button class='smallButton primary eventTool' id='emitExpectedEventSide' style='margin-top:6px'>Отправить ожидаемое</button>"
          : "<div class='notice'>Runtime сейчас не ждёт события.</div>") +
        "<button class='smallButton eventTool' id='hornEventSide' style='margin-top:6px'>HornPressed</button>" +
        (journalDetached
          ? "<div class='notice journalDetachedNotice' style='margin-top:6px'>Журнал вынесен в отдельное окно.</div>" +
            "<button class='smallButton' id='openJournal' style='margin-top:5px'>Открыть журнал</button>"
          : "<div class='eventList runtimeEventList'>" + eventHistory.map(renderEvent).join("") + "</div>") +
      "</section>";

    runtimeSide.querySelector("#detachJournal")?.addEventListener("click", () => send({ action: "detach_journal" }));
    runtimeSide.querySelector("#openJournal")?.addEventListener("click", () => send({ action: "open_journal" }));
    runtimeSide.querySelector("#fitWorldSide")?.addEventListener("click", () => {
      fitWorld();
      drawMap();
    });
    runtimeSide.querySelector("#emitExpectedEventSide")?.addEventListener("click", () => {
      const expected = runtimeExpectedEvent();
      if (expected) send({ action: "emit_event", ...expected });
    });
    runtimeSide.querySelector("#hornEventSide")?.addEventListener("click", () => {
      send({ action: "emit_event", eventType: "HornPressed", source: "Simulator", payload: {} });
    });

    runtimeSide.querySelectorAll("[data-quest-enabled]").forEach(input => {
      input.addEventListener("change", () => {
        send({
          action: "set_quest_enabled",
          campaignId: input.dataset.campaignId,
          questId: input.dataset.questId,
          enabled: input.checked
        });
      });
    });

    runtimeSide.querySelectorAll("[data-campaign-active]").forEach(input => {
      input.addEventListener("change", () => {
        send({
          action: "set_campaign_active",
          campaignId: input.dataset.campaignId,
          active: input.checked
        });
      });
    });

    runtimeSide.querySelectorAll("[data-open-quest]").forEach(button => {
      button.addEventListener("click", () => {
        const path = button.dataset.openQuest;
        if (path) send({ action: "open_quest_editor", path });
      });
    });

    runtimeSide.querySelectorAll("[data-open-campaign]").forEach(button => {
      button.addEventListener("click", () => {
        const campaignId = button.dataset.openCampaign;
        if (campaignId) send({ action: "open_campaign_folder", campaignId });
      });
    });
  }

  function drawHud() {
    const p = snapshot.player.position;
    const selected = snapshot.selection?.point;
    const points = snapshot.world?.points || [];
    const sdoCount = points.filter(point => !point.isCity).length;
    const cityCount = points.filter(point => point.isCity).length;
    const simulationButton = document.getElementById("simulationToggle");
    if (simulationButton) {
      simulationButton.textContent = simulationRunning ? "Остановить симуляцию" : "Запустить симуляцию";
      simulationButton.classList.toggle("primary", !simulationRunning);
    }

    hud.innerHTML = [
      "<span class='badge " + (simulationRunning ? "accent" : "blue") + "'>" +
        (simulationRunning ? "Симуляция: ВКЛ" : "Симуляция: ВЫКЛ") + "</span>",
      "<span class='badge blue'>СДО " + sdoCount.toLocaleString("ru-RU") + "</span>",
      "<span class='badge blue'>Города " + cityCount.toLocaleString("ru-RU") + "</span>",
      "<span class='badge blue'>X " + Math.round(p.x) + "</span>",
      "<span class='badge blue'>Y " + Math.round(p.y) + "</span>",
      "<span class='badge blue'>Z " + Math.round(p.z) + "</span>",
      "<span class='badge accent'>" + (selected ? "Выбрана: " + escapeHtml(selected.name || selected.category) : "Точка не выбрана") + "</span>"
    ].join("");
    renderRuntimeSidebar();
  }

    function renderSide() {
    const previouslyOpen = new Set(
      [...side.querySelectorAll(".acc.open")].map(section => section.dataset.section));
    side.innerHTML = sections.map((entry, index) => {
      const id = entry[0];
      const label = entry[1];
      const open = previouslyOpen.size === 0 ? index === 0 : previouslyOpen.has(id);
      return "<section class='acc " + (open ? "open" : "") + "' data-section='" + id + "'>" +
        "<div class='accHead'><strong>" + label + "</strong><span>⌄</span></div>" +
        "<div class='accBody'>" + sectionBody(id) + "</div>" +
      "</section>";
    }).join("");

    side.querySelectorAll(".accHead").forEach(head => {
      head.addEventListener("click", () => head.parentElement.classList.toggle("open"));
    });

    bindInputs();
    const eventList = side.querySelector("#eventList");
    if (eventList) eventList.innerHTML = eventHistory.map(event => renderEvent(event)).join("");
  }

  function sectionBody(id) {
    if (id === "player") {
      const p = snapshot.player.position;
      const selected = snapshot.selection?.point;
      return [
        "<div class='fieldGrid'>",
          "<div class='field'><label>X</label><input data-player='x' value='" + p.x + "'></div>",
          "<div class='field'><label>Y</label><input data-player='y' value='" + p.y + "'></div>",
          "<div class='field'><label>Z</label><input data-player='z' value='" + p.z + "'></div>",
          "<div class='field'><label>Скорость</label><input value='" + snapshot.player.speedKmh + "' disabled></div>",
        "</div>",
        "<div class='card' style='margin-top:8px;padding:8px'>",
          "<div class='miniLabel'>Выбранная СДО</div>",
          "<div style='margin-top:5px'>" + (selected ? escapeHtml(selected.name || selected.category) : "Нет выбранной точки") + "</div>",
          selected ? "<div class='kv'><span>Категория</span><span>" + escapeHtml(selected.category) + "</span></div>" : "",
          selected ? "<div class='kv'><span>Координаты</span><span>" + formatPosition(selected.position) + "</span></div>" : "",
        "</div>",
        "<div class='kv'><span>Направление</span><span>" + Math.round(snapshot.player.heading) + "°</span></div>",
        "<div class='kv'><span>Пауза</span><span>" + (snapshot.player.paused ? "да" : "нет") + "</span></div>",
        "<div class='kv'><span>В кабине</span><span>" + (snapshot.player.inCab ? "да" : "нет") + "</span></div>"
      ].join("");
    }

    if (id === "facts") {
      return Object.entries(snapshot.facts.values).map(([key, value]) =>
        "<div class='field' style='margin-top:6px'><label>" + escapeHtml(key) + "</label><input data-fact='" + escapeHtml(key) + "' value='" + escapeHtml(value) + "'></div>"
      ).join("") +
      "<div class='inline' style='margin-top:8px'><input id='newFactKey' placeholder='новый.факт'><input id='newFactValue' placeholder='значение'><button class='smallButton primary' id='addFact'>+</button></div>";
    }

    if (id === "statuses") {
      const q = snapshot.questStatuses.quests[0] || {};
      return [
        "<div class='field'><label>Квест</label><input id='questId' value='" + escapeHtml(q.questId || "special_marinated_shashlik") + "'></div>",
        "<div class='field' style='margin-top:6px'><label>Статус</label><select id='questStatus'>" +
          ["Available","Active","Completed","Cancelled","Failed","Archived"].map(status => "<option " + (status === (q.status || "Available") ? "selected" : "") + ">" + status + "</option>").join("") +
        "</select></div>",
        "<div class='field' style='margin-top:6px'><label>Этап</label><input id='questStep' value='" + escapeHtml(q.step || "available") + "'></div>",
        "<button class='smallButton primary' id='applyStatus' style='margin-top:8px'>Применить статус</button>",
        "<div class='notice' style='margin-top:8px'>Status и Step хранятся отдельно: статус определяет жизненный цикл квеста, Step — текущую фазу.</div>"
      ].join("");
    }

    if (id === "states") {
      const flags = Object.entries(snapshot.states.flags).map(([key, value]) =>
        "<div class='inline' style='margin-top:6px'><label style='flex:1;font-size:10px;color:var(--muted)'>" + escapeHtml(key) + "</label><input type='checkbox' data-flag='" + escapeHtml(key) + "' " + (value ? "checked" : "") + " style='flex:0 0 auto;width:auto'></div>"
      ).join("");
      const variables = Object.entries(snapshot.states.variables).map(([key, value]) =>
        "<div class='field' style='margin-top:6px'><label>" + escapeHtml(key) + "</label><input data-var='" + escapeHtml(key) + "' value='" + escapeHtml(value) + "'></div>"
      ).join("");
      return "<div class='miniLabel'>Флаги</div>" + flags +
        "<div class='miniLabel' style='margin-top:12px'>Переменные</div>" + variables +
        "<div class='inline' style='margin-top:8px'><input id='newVarKey' placeholder='ключ'><input id='newVarValue' placeholder='значение'><button class='smallButton primary' id='addVar'>+</button></div>";
    }

    if (id === "vitals") {
      const v = snapshot.playerVitals || {};
      return [
        "<div class='fieldGrid'>",
          field("health", "Здоровье", v.health),
          field("energy", "Энергия", v.energy),
          field("hydration", "Жидкость", v.hydration),
          field("fatigue", "Усталость", v.fatigue),
        "</div>",
        "<button class='smallButton primary' id='applyVitals' style='margin-top:8px'>Применить</button>"
      ].join("");
    }

    if (id === "progress") {
      const p = snapshot.playerProgress || {};
      return [
        "<div class='fieldGrid'>",
          field("money", "Деньги", p.money),
          field("experience", "Опыт", p.experience),
          field("reserve", "Резерв", p.reserve),
        "</div>",
        "<button class='smallButton primary' id='applyProgress' style='margin-top:8px'>Применить</button>"
      ].join("");
    }

    if (id === "character") {
      return Object.entries(snapshot.character?.stats || {}).map(([key, value]) =>
        "<div class='field' style='margin-top:6px'><label>" + escapeHtml(CHARACTER_STAT_LABELS[key] || key) + "</label><input type='number' data-stat='" + escapeHtml(key) + "' value='" + Number(value) + "'></div>"
      ).join("") +
      "<button class='smallButton primary' id='applyCharacter' style='margin-top:8px'>Применить</button>";
    }

    if (id === "inventory") {
      return Object.entries(snapshot.inventory.items).map(([key, value]) =>
        "<div class='field' style='margin-top:6px'><label>" + escapeHtml(key) + "</label><input type='number' data-item='" + escapeHtml(key) + "' value='" + value + "'></div>"
      ).join("") +
      "<div class='inline' style='margin-top:8px'><input id='newItemKey' placeholder='item_id'><input id='newItemValue' value='1'><button class='smallButton primary' id='addItem'>+</button></div>";
    }

    if (id === "reputation") {
      const entries = Object.entries(snapshot.reputation?.entries || {});
      const rows = entries
        .map(([key, entry]) => "<div class='field' style='margin-top:6px'><label>" + escapeHtml(key) + "</label><input type='number' data-rep='" + escapeHtml(key) + "' value='" + Number(entry?.value || 0) + "'></div>")
        .join("");
      const list = rows || "<div class='miniLabel'>Нет НПЦ в канале репутации</div>";
      return list +
      "<div class='inline' style='margin-top:8px'><input id='newRepKey' placeholder='npc_id'><input id='newRepValue' value='0'><button class='smallButton primary' id='addRep'>+</button></div>";
    }

    if (id === "telemetry") {
      const t = snapshot.telemetry;
      return [
        "<div class='fieldGrid'>",
          field("speed", "Скорость км/ч", t.speedKmh), field("rpm", "Обороты RPM", t.engineRpm),
          field("throttle", "Газ %", t.throttle), field("brake", "Тормоз %", t.brake),
          field("steering", "Руль %", t.steering), field("fuel", "Топливо %", t.fuelPercent),
          field("engineTemperature", "Темп. двигателя", t.engineTemperature), field("cabinTemperature", "Темп. кабины", t.cabinTemperature),
          field("damageCab", "Урон кабины %", t.damageCabPercent), field("damageEngine", "Урон двигателя %", t.damageEnginePercent),
          field("damageTransmission", "Урон КПП %", t.damageTransmissionPercent), field("damageWheel", "Урон колеса %", t.damageWheelPercent),
        "</div>",
        "<div class='inline' style='margin-top:8px'><button class='smallButton " + (t.hornPressed ? "primary" : "") + "' id='horn'>Horn " + (t.hornPressed ? "pressed" : "released") + "</button><button class='smallButton' id='hornEvent'>Событие HornPressed</button></div>",
        "<button class='smallButton primary' id='applyTelemetry' style='margin-top:8px'>Записать телеметрию</button>"
      ].join("");
    }

    if (id === "environment") {
      const e = snapshot.environment;
      return [
        "<div class='field'><label>Погода</label><input id='weather' value='" + escapeHtml(e.weather) + "'></div>",
        "<div class='field' style='margin-top:6px'><label>Дождь %</label><input id='rain' value='" + e.rainPercent + "'></div>",
        "<div class='field' style='margin-top:6px'><label>Игровое время</label><input id='gameTime' value='" + escapeHtml(e.gameTime) + "'></div>",
        "<div class='field' style='margin-top:6px'><label>Видимость, м</label><input id='visibility' value='" + e.visibilityMeters + "'></div>",
        "<button class='smallButton primary' id='applyEnvironment' style='margin-top:8px'>Применить</button>"
      ].join("");
    }

    if (id === "events") {
      const expectedEvent = runtimeExpectedEvent();
      const defaultType = expectedEvent?.eventType || "CustomEvent";
      const defaultPayload = expectedEvent ? JSON.stringify(expectedEvent.payload) : "{\"quest\":\"special_marinated_shashlik\"}";
      const expectedMarkup = expectedEvent
        ? "<div class='notice' style='margin-bottom:8px'><strong>Runtime ждёт:</strong> " + escapeHtml(expectedEvent.eventType) +
          "<br>Payload: <code>" + escapeHtml(JSON.stringify(expectedEvent.payload)) + "</code></div>" +
          "<button class='smallButton primary' id='emitExpectedEvent' style='margin-bottom:8px'>Отправить ожидаемое событие</button>"
        : "<div class='notice' style='margin-bottom:8px'>Runtime сейчас не ждёт конкретного события.</div>";

      return [
        expectedMarkup,
        "<div class='field'><label>Тип события</label><input id='eventType' value='" + escapeHtml(defaultType) + "'></div>",
        "<div class='field' style='margin-top:6px'><label>Источник</label><input id='eventSource' value='Simulator'></div>",
        "<div class='field' style='margin-top:6px'><label>Payload JSON</label><textarea id='eventPayload'>" + escapeHtml(defaultPayload) + "</textarea></div>",
        "<button class='smallButton primary' id='emitEvent'>Отправить событие</button>",
        "<div class='miniLabel' style='margin:12px 0 5px'>Последние события</div>",
        "<div class='eventList' id='eventList'></div>"
      ].join("");
    }

    if (id === "system") {
      return [
        "<div class='notice'><strong>" +
          (simulationRunning ? "Симуляция запущена." : "Симуляция остановлена.") +
          "</strong><br>Квесты реагируют на мир только после запуска симуляции.</div>",
        "<div class='kv'><span>Runtime</span><span>" + (runtime?.status || "Stopped") + "</span></div>",
        "<div class='kv'><span>Текущая нода</span><span>" + escapeHtml(runtime?.currentNodeId || "—") + "</span></div>",
        "<div class='kv'><span>Ожидание</span><span>" + escapeHtml(runtime?.waitingFor || "—") + "</span></div>",
        "<div class='kv'><span>Режим</span><span>" + escapeHtml(snapshot.system.runtimeMode) + "</span></div>",
        "<div class='kv'><span>Последний переход</span><span>" + escapeHtml(snapshot.system.lastTransition) + "</span></div>",
        "<div class='kv'><span>Последнее событие</span><span>" + escapeHtml(snapshot.system.lastEvent || "—") + "</span></div>",
        "<div class='notice' style='margin-top:10px'>Системный канал диагностический и не является источником игровых данных.</div>"
      ].join("");
    }

    return "";
  }

  function field(key, label, value) {
    return "<div class='field'><label>" + label + "</label><input data-t='" + key + "' value='" + value + "'></div>";
  }

  function bindInputs() {
    side.querySelectorAll("[data-player]").forEach(input => {
      input.addEventListener("change", () => {
        send({
          action: "set_player_position",
          x: Number(side.querySelector("[data-player='x']").value),
          y: Number(side.querySelector("[data-player='y']").value),
          z: Number(side.querySelector("[data-player='z']").value)
        });
      });
    });

    side.querySelectorAll("[data-fact]").forEach(input => {
      input.addEventListener("change", () => send({ action: "set_fact", key: input.dataset.fact, value: input.value }));
    });

    side.querySelectorAll("[data-flag]").forEach(input => {
      input.addEventListener("change", () => send({ action: "set_flag", key: input.dataset.flag, value: input.checked }));
    });

    side.querySelectorAll("[data-var]").forEach(input => {
      input.addEventListener("change", () => send({ action: "set_variable", key: input.dataset.var, value: input.value }));
    });

    side.querySelectorAll("[data-item]").forEach(input => {
      input.addEventListener("change", () => send({ action: "set_inventory", key: input.dataset.item, amount: Number(input.value) }));
    });

    side.querySelector("#applyVitals")?.addEventListener("click", () => {
      send({
        action: "set_vitals",
        health: Number(side.querySelector("[data-t='health']").value),
        energy: Number(side.querySelector("[data-t='energy']").value),
        hydration: Number(side.querySelector("[data-t='hydration']").value),
        fatigue: Number(side.querySelector("[data-t='fatigue']").value)
      });
    });

    side.querySelector("#applyProgress")?.addEventListener("click", () => {
      send({
        action: "set_progress",
        money: Number(side.querySelector("[data-t='money']").value),
        experience: Number(side.querySelector("[data-t='experience']").value),
        reserve: Number(side.querySelector("[data-t='reserve']").value)
      });
    });

    side.querySelector("#applyCharacter")?.addEventListener("click", () => {
      side.querySelectorAll("[data-stat]").forEach(input => {
        send({
          action: "set_character_stat",
          stat: input.dataset.stat,
          value: Number(input.value)
        });
      });
    });

    side.querySelectorAll("[data-rep]").forEach(input => {
      input.addEventListener("change", () => send({ action: "set_reputation", key: input.dataset.rep, amount: Number(input.value) }));
    });

    side.querySelector("#addFact")?.addEventListener("click", () => {
      const key = side.querySelector("#newFactKey").value.trim();
      if (key) send({ action: "set_fact", key, value: side.querySelector("#newFactValue").value });
    });

    side.querySelector("#addVar")?.addEventListener("click", () => {
      const key = side.querySelector("#newVarKey").value.trim();
      if (key) send({ action: "set_variable", key, value: side.querySelector("#newVarValue").value });
    });

    side.querySelector("#addItem")?.addEventListener("click", () => {
      const key = side.querySelector("#newItemKey").value.trim();
      if (key) send({ action: "set_inventory", key, amount: Number(side.querySelector("#newItemValue").value) });
    });

    side.querySelector("#addRep")?.addEventListener("click", () => {
      const key = side.querySelector("#newRepKey").value.trim();
      if (key) send({ action: "set_reputation", key, amount: Number(side.querySelector("#newRepValue").value) });
    });

    side.querySelector("#applyStatus")?.addEventListener("click", () => {
      send({
        action: "set_quest_status",
        questId: side.querySelector("#questId").value,
        status: side.querySelector("#questStatus").value,
        step: side.querySelector("#questStep").value
      });
    });

    side.querySelector("#applyTelemetry")?.addEventListener("click", () => {
      const payload = { action: "set_telemetry", horn: snapshot.telemetry.hornPressed };
      side.querySelectorAll("[data-t]").forEach(input => payload[input.dataset.t] = Number(input.value));
      send(payload);
    });

    side.querySelector("#horn")?.addEventListener("click", () => {
      send({ action: "set_telemetry", horn: !snapshot.telemetry.hornPressed });
    });

    side.querySelector("#hornEvent")?.addEventListener("click", () => {
      send({ action: "emit_event", eventType: "HornPressed", source: "Simulator", payload: {} });
    });

    side.querySelector("#applyEnvironment")?.addEventListener("click", () => {
      send({
        action: "set_environment",
        weather: side.querySelector("#weather").value,
        rain: Number(side.querySelector("#rain").value),
        gameTime: side.querySelector("#gameTime").value,
        visibility: Number(side.querySelector("#visibility").value)
      });
    });

    side.querySelector("#runtimeStartSide")?.addEventListener("click", () => send({ action: "runtime_start" }));
    side.querySelector("#runtimeStopSide")?.addEventListener("click", () => send({ action: "runtime_stop" }));

    side.querySelector("#emitExpectedEvent")?.addEventListener("click", () => {
      const expected = runtimeExpectedEvent();
      if (expected) send({ action: "emit_event", ...expected });
    });

    side.querySelector("#emitEvent")?.addEventListener("click", () => {
      let payload = {};
      try {
        payload = JSON.parse(side.querySelector("#eventPayload").value || "{}");
      } catch {
        window.alert("Payload JSON не разобран.");
        return;
      }
      send({
        action: "emit_event",
        eventType: side.querySelector("#eventType").value,
        source: side.querySelector("#eventSource").value,
        payload
      });
    });
  }

  function renderEvent(event) {
    if (!event) return "";
    return "<div class='eventRow'><strong>" + escapeHtml(event.eventType || "Событие") + "</strong>" +
      formatEventTimestamp(event.timestamp) + " · " + escapeHtml(event.source || "Источник неизвестен") + "</div>";
  }

  function receive(message) {
    if (!message) {
      window.assistWebLog?.("WARN", "Simulator получил пустое сообщение.");
      return;
    }

    if (message.type === "snapshot") {
      const hadSnapshot = !!snapshot;
      snapshot = message.snapshot;
      window.__assistItemCatalog = Array.isArray(message.itemCatalog) ? message.itemCatalog : [];
      runtime = message.runtime || null;
      simulationRunning = !!message.simulationRunning;
      campaigns = Array.isArray(message.campaigns) ? message.campaigns : [];
      questActivations = Array.isArray(message.questActivations) ? message.questActivations : [];
      questGraph = message.questGraph || questGraph;
      journalDetached = !!message.journalDetached;
      selectedPointId = snapshot.selection?.point?.id || null;

      window.assistQuestLog?.("INFO", "Quest UI: snapshot загружен.", {
        graph: questGraph ? {
          id: questGraph.id,
          name: questGraph.name,
          nodes: questGraph.nodes?.length || 0,
          connections: questGraph.connections?.length || 0
        } : null,
        runtime: runtime ? {
          status: runtimeStatusName(runtime.status),
          rawStatus: runtime.status,
          currentNodeId: runtime.currentNodeId,
          waitingFor: runtime.waitingFor,
          lastEvent: runtime.lastEvent,
          lastTransition: runtime.lastTransition
        } : null
      });

      const targetInfo = logQuestTargetResolution("snapshot");
      focusRuntimeTarget();
      window.assistQuestLog?.("INFO", "Quest UI: камера и ожидаемая цель обработаны.", {
        currentNodeId: runtime?.currentNodeId || null,
        targetPointId: targetInfo.point?.id || null,
        targetFound: !!targetInfo.point,
        camera
      });

      window.assistWebLog?.("INFO", "Simulator получил snapshot.", {
        points: snapshot.world?.points?.length ?? 0,
        selectedPointId,
        player: snapshot.player?.position,
        version: message.version
      });
      if (!hadSnapshot) fitWorld();
      scheduleUiRender();
      renderSide();
      return;
    }

    if (message.type === "runtime_event") {
      runtime = message.runtime || runtime;
      window.assistQuestLog?.("INFO", "Quest UI: Runtime event получен.", {
        event: message["@event"] || message.event || null,
        runtime: runtime ? {
          status: runtimeStatusName(runtime.status),
          rawStatus: runtime.status,
          currentNodeId: runtime.currentNodeId,
          waitingFor: runtime.waitingFor,
          lastTransition: runtime.lastTransition
        } : null
      });
      logQuestTargetResolution("runtime_event");
      focusRuntimeTarget();
      const runtimeEvent = message["@event"] || message.event;
      if (runtimeEvent) {
        if (runtimeEvent.eventType === "InventoryChanged") {
          handleInventoryEvent(runtimeEvent);
        }
        eventHistory.unshift(runtimeEvent);
        eventHistory.splice(12);
      }
      scheduleUiRender();
      renderSide();
      return;
    }

    if (message.type === "event") {
      window.assistQuestLog?.("INFO", "Quest UI: Simulator event получен.", {
        event: message["@event"] || message.event || null,
        runtime: runtime ? {
          status: runtimeStatusName(runtime.status),
          currentNodeId: runtime.currentNodeId,
          waitingFor: runtime.waitingFor
        } : null
      });
      focusRuntimeTarget();
      const simulatorEvent = message["@event"] || message.event;
      if (simulatorEvent) {
        if (simulatorEvent.eventType === "InventoryChanged") {
          handleInventoryEvent(simulatorEvent);
        }
        eventHistory.unshift(simulatorEvent);
        eventHistory.splice(12);
      }
      scheduleUiRender();
      renderSide();
      return;
    }

    if (message.type === "error") {
      window.alert(message.message || "Ошибка симулятора");
    }
  }

  const webview = window.chrome?.webview;
  webview?.addEventListener("message", event => {
    try {
      const data = typeof event.data === "string" ? JSON.parse(event.data) : event.data;
      receive(data);
    } catch (error) {
      window.assistWebLog?.("ERROR", "Ошибка обработки сообщения от host.", { error: String(error) });
    }
  });
  window.assistWebLog?.("INFO", "Simulator Web UI готов.", {
    canvas: !!map,
    side: !!side,
    url: location.href
  });

  map.addEventListener("pointerdown", event => {
    const pos = pointerPosition(event);

    if (event.button === 0) {
      if (hitPlayer(pos.x, pos.y)) {
        draggingPlayer = true;
        dragPlayerPosition = { x: snapshot.player.position.x, z: snapshot.player.position.z };
        map.setPointerCapture?.(event.pointerId);
        event.preventDefault();
        return;
      }

      const point = hitPoint(pos.x, pos.y);
      if (point && !point.isCity) {
        send({ action: "select_point", id: point.id });
      } else if (!point && snapshot?.selection?.point) {
        send({ action: "clear_selection" });
      }
      return;
    }

    if (event.button === 1 || event.button === 2) {
      panning = true;
      panStart = { x: pos.x, y: pos.y, cx: camera.cx, cz: camera.cz };
      map.setPointerCapture?.(event.pointerId);
      event.preventDefault();
    }
  });

  map.addEventListener("pointermove", event => {
    const pos = pointerPosition(event);

    if (!draggingPlayer && !panning && snapshot) {
      const hovered = hitPoint(pos.x, pos.y);
      const nextHoveredPointId = hovered?.id || null;
      if (nextHoveredPointId !== hoveredPointId) {
        hoveredPointId = nextHoveredPointId;
        map.style.cursor = hovered ? (hovered.isCity ? "default" : "pointer") : "default";
        drawMap();
      }
    }

    if (draggingPlayer && snapshot) {
      const world = screenToWorld(pos.x, pos.y);
      dragPlayerPosition = { x: world.x, z: world.z };
      drawMap();
      return;
    }

    if (panning && panStart) {
      camera.cx = panStart.cx - (pos.x - panStart.x) * camera.mpp;
      camera.cz = panStart.cz - (pos.y - panStart.y) * camera.mpp;
      drawMap();
    }
  });

  map.addEventListener("pointerup", event => {
    const pos = pointerPosition(event);

    if (draggingPlayer && snapshot) {
      const world = screenToWorld(pos.x, pos.y);
      send({
        action: "set_player_position",
        x: world.x,
        y: snapshot.player.position.y,
        z: world.z
      });
      window.assistQuestLog?.("INFO", "Quest UI: игрок перемещён.", {
        position: { x: world.x, y: snapshot.player.position.y, z: world.z },
        runtimeStatus: runtimeStatusName(runtime?.status),
        waitingFor: runtime?.waitingFor || null,
        currentNodeId: runtime?.currentNodeId || null
      });
      draggingPlayer = false;
      dragPlayerPosition = null;
      map.releasePointerCapture?.(event.pointerId);
      map.style.cursor = "default";
      return;
    }

    if (panning) {
      panning = false;
      panStart = null;
      map.releasePointerCapture?.(event.pointerId);
      map.style.cursor = "default";
    }
  });

  map.addEventListener("pointerleave", event => {
    if (hoveredPointId !== null) {
      hoveredPointId = null;
      map.style.cursor = "default";
      drawMap();
    }

    if (draggingPlayer && snapshot) {
      const pos = pointerPosition(event);
      const world = screenToWorld(pos.x, pos.y);
      send({
        action: "set_player_position",
        x: world.x,
        y: snapshot.player.position.y,
        z: world.z
      });
      draggingPlayer = false;
      dragPlayerPosition = null;
    }
  });

  map.addEventListener("wheel", event => {
    const pos = pointerPosition(event);
    const before = screenToWorld(pos.x, pos.y);
    camera.mpp = Math.max(0.1, Math.min(50000, camera.mpp * (event.deltaY < 0 ? 0.8 : 1.25)));
    const after = screenToWorld(pos.x, pos.y);
    camera.cx += before.x - after.x;
    camera.cz += before.z - after.z;
    drawMap();
    event.preventDefault();
  }, { passive: false });

  map.addEventListener("contextmenu", event => event.preventDefault());

  backpackButton?.addEventListener("click", () => toggleGameplayInventory());

  // Горячая клавиша инвентаря не должна зависеть от языка ввода.
  // `event.key` содержит символ текущей раскладки: в русской ЙЦУКЕН та же
  // физическая клавиша отдаёт «ш», поэтому проверка по «i» не срабатывала.
  // `event.code` привязан к позиции клавиши и одинаков во всех раскладках.
  const INVENTORY_HOTKEY_CODES = new Set(["KeyI"]);
  const INVENTORY_HOTKEY_KEYS = new Set(["i", "ш"]);

  const isInventoryHotkey = event =>
    INVENTORY_HOTKEY_CODES.has(event.code) ||
    INVENTORY_HOTKEY_KEYS.has(String(event.key || "").toLowerCase());

  document.addEventListener("keydown", event => {
    if (event.repeat) return;
    const tag = event.target?.tagName;
    if (tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT" || event.target?.isContentEditable) return;
    if (isInventoryHotkey(event)) {
      event.preventDefault();
      toggleGameplayInventory();
    }
  });

  let activeTooltip = null;
  document.addEventListener("mouseover", event => {
    const target = event.target?.closest?.("[data-game-tooltip]");
    if (!target) return;

    const textValue = target.getAttribute("data-game-tooltip");
    if (!textValue) return;

    if (!activeTooltip) {
      activeTooltip = document.createElement("div");
      activeTooltip.id = "assistGameTooltip";
      activeTooltip.className = "gameTooltip";
      document.body.appendChild(activeTooltip);
    }

    activeTooltip.textContent = textValue;
    activeTooltip.style.display = "block";
    const rect = target.getBoundingClientRect();
    activeTooltip.style.left = Math.max(8, Math.min(window.innerWidth - 310, rect.left)) + "px";
    activeTooltip.style.top = Math.max(8, rect.top - activeTooltip.offsetHeight - 7) + "px";
  });

  document.addEventListener("mouseout", event => {
    const target = event.target?.closest?.("[data-game-tooltip]");
    const related = event.relatedTarget;
    if (target && related && target.contains(related)) return;
    if (activeTooltip) activeTooltip.style.display = "none";
  });

  document.getElementById("simulationToggle")?.addEventListener("click", () => {
    send({ action: simulationRunning ? "simulation_stop" : "simulation_start" });
  });

  document.getElementById("reset")?.addEventListener("click", () => send({ action: "reset" }));
  window.addEventListener("resize", () => {
    drawMap();
  });
})();
