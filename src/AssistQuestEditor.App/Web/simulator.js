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
  const onlyQuestsToggle = document.getElementById("onlyQuestsToggle");
  const citiesToggle = document.getElementById("citiesToggle");
  const mapStatusHint = document.getElementById("mapStatusHint");
  const send = payload => window.chrome?.webview?.postMessage(payload);

  let snapshot = null;
  let runtime = null;
  let draggingPlayer = false;
  let panning = false;
  let panStart = null;
  let selectedPointId = null;
  let hoveredPointId = null;
  let questGraph = null;

  let questCatalog = [];
  let selectedQuest = { campaignId: "", questId: "" };
  let onlyQuestsFilter = false;
  // Города гасятся отдельной галочкой: «только квесты» их НЕ отключает, иначе
  // нельзя было бы показать карту без СДО, но с ориентирами-городами.
  let citiesFilter = true;
  let questHitAreas = [];
  // Точки, реально нарисованные в текущем кадре. Клик проверяется только по
  // ним: иначе можно было бы выбрать точку, которой на карте не видно
  // (скрытую фильтром или уехавшую за пределы видимой области).
  let visiblePoints = [];
  // Квест под курсором. Хранится отдельно от СДО-точки: квестовая графика
  // лежит верхним слоем и имеет собственный приоритет подсветки.
  let hoveredQuest = null;
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

  // Акцентный оранжевый приложения. Квестовая графика и подсветка выделения
  // обязаны совпадать с цветом в C#-окне кампаний, поэтому значение задано
  // строкой, а не вычисляется из темы: canvas не читает CSS-переменные.
  const ACCENT_COLOR = "#fab003";
  const CITY_COLOR = "#ffff00";
  // Плашка названия квеста вдвое уже прежней: текст переносится по словам.
  const QUEST_PLATE_WIDTH = 120;
  const QUEST_PLATE_PADDING = 6;
  const QUEST_PLATE_LINE_HEIGHT = 12;
  // Неактивный квест: тёмно-серый фон и приглушённо-белый текст, серая точка.
  const INACTIVE_PLATE_FILL = "rgba(74,79,86,.55)";
  const INACTIVE_PLATE_STROKE = "rgba(150,155,165,.45)";
  const INACTIVE_PLATE_TEXT = "#e6eaee";
  const INACTIVE_POINT_COLOR = "#6d7480";

  function formatPosition(position) {
    if (!position) return "—";
    return "X " + Math.round(position.x) + " · Y " + Math.round(position.y) + " · Z " + Math.round(position.z);
  }

  const escapeHtml = value => String(value ?? "").replace(/[&<>"']/g, char => ({
    "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;", "'": "&#39;"
  }[char]));

  /**
   * Разбор CSS-цвета в компоненты.
   *
   * Данные приходят извне (WorldPoint.Color), поэтому поддерживаются и
   * `#rgb`/`#rrggbb`, и `rgb()`/`rgba()`. Непонятный цвет возвращается как
   * null: вызывающий код тогда оставляет исходное значение, а не рисует мусор.
   */
  function parseColor(value) {
    const text = String(value ?? "").trim();
    if (!text) return null;

    const hex = text.match(/^#([0-9a-f]{3}|[0-9a-f]{6})$/i);
    if (hex) {
      const body = hex[1];
      const expanded = body.length === 3
        ? body.split("").map(char => char + char).join("")
        : body;
      return {
        r: parseInt(expanded.slice(0, 2), 16),
        g: parseInt(expanded.slice(2, 4), 16),
        b: parseInt(expanded.slice(4, 6), 16),
        a: 1
      };
    }

    const rgb = text.match(/^rgba?\(([^)]+)\)$/i);
    if (rgb) {
      const parts = rgb[1].split(/[\s,\/]+/).filter(Boolean).map(Number);
      if (parts.length < 3 || parts.slice(0, 3).some(number => !Number.isFinite(number))) return null;
      return {
        r: parts[0], g: parts[1], b: parts[2],
        a: Number.isFinite(parts[3]) ? parts[3] : 1
      };
    }

    return null;
  }

  /** Затемняет цвет на заданную долю (по умолчанию 25%) и возвращает CSS-строку. */
  function darkenColor(value, amount = 0.25) {
    const parsed = parseColor(value);
    if (!parsed) return value;

    const factor = Math.max(0, Math.min(1, 1 - amount));
    const channel = raw => Math.max(0, Math.min(255, Math.round(raw * factor)));
    const base = "rgb(" + channel(parsed.r) + "," + channel(parsed.g) + "," + channel(parsed.b) + ")";
    return parsed.a < 1
      ? "rgba(" + channel(parsed.r) + "," + channel(parsed.g) + "," + channel(parsed.b) + "," + parsed.a + ")"
      : base;
  }

  /** Полупрозрачная версия цвета — для теней и заливок. */
  function withAlpha(value, alpha) {
    const parsed = parseColor(value);
    if (!parsed) return value;
    const round = raw => Math.max(0, Math.min(255, Math.round(raw)));
    return "rgba(" + round(parsed.r) + "," + round(parsed.g) + "," + round(parsed.b) + "," + alpha + ")";
  }

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

    const points = snapshot.world?.points || [];
    const showAllLabels = camera.mpp < 24;
    const labelLimit = camera.mpp < 70 ? 140 : (camera.mpp < 180 ? 55 : 24);
    const occupied = new Set();

    // Фильтры видимости точек. Две галочки независимы:
    //   «только квесты» — прячет СДО, но НЕ города (города остаются
    //     ориентирами, иначе карта превратилась бы в пустое поле);
    //   «города» — отдельно гасит города, не трогая СДО.
    // Скрытая точка не попадает ни в отрисовку, ни в зоны попадания.
    const drawnPoints = points.filter(point => {
      const isCity = point.isCity === true;
      if (isCity && !citiesFilter) return false;
      if (!isCity && onlyQuestsFilter) return false;
      return true;
    });
    visiblePoints = [];

    // Порядок слоёв задаётся требованием «вся квестовая графика — самый верхний
    // слой». Внутри квестового слоя сначала рисуются неактивные квесты, затем
    // активные: активные визуально перекрывают неактивные. Сортировка идёт по
    // порядковому номеру из файла кампании, затем по имени файла.
    const questLayer = buildQuestLayer(points);
    const activeQuests = questLayer.filter(entry => entry.active);
    const inactiveQuests = questLayer.filter(entry => !entry.active);

    // Зоны попадания перезаписываются на каждой перерисовке: они зависят от
    // камеры, поэтому хранить их между кадрами бессмысленно.
    questHitAreas = [];

    for (const point of drawnPoints) {
      drawWorldPoint(ctx, point, width, height, occupied, showAllLabels, labelLimit);
      visiblePoints.push(point);
    }

    // Квестовая графика не зависит от галочек видимости точек: «только квесты»
    // должна ПОКАЗЫВАТЬ квесты, а не прятать их вместе с их СДО. Квест,
    // привязанный к СДО, остаётся на карте — его ромб и помечают место.
    for (const entry of inactiveQuests) drawQuestMarker(ctx, entry, false);
    for (const entry of activeQuests) drawQuestMarker(ctx, entry, true);

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

    // Легенда категорий СДО намеренно не рисуется: она перекрывала карту и
    // дублировала правую панель. Плотность мира читается по самим точкам.
    drawScaleBar(ctx, width, height);
    drawHud();
  }

  /**
   * Квестовый слой карты.
   *
   * Возвращает плоский список квестов с точкой, радиусом, признаком
   * доступности и порядком. Порядок — из файла кампании (порядковый номер
   * квеста); при совпадении номеров сортирует имя файла, чтобы порядок не
   * зависел от порядка строк в JSON.
   */
  function buildQuestLayer(points) {
    const entries = [];

    for (const campaign of questCatalog) {
      for (const quest of campaign.quests || []) {
        if (!quest.worldPointId) continue;

        const point = points.find(item =>
          String(item.id || "").toLowerCase() === String(quest.worldPointId).toLowerCase());

        entries.push({
          campaignId: campaign.campaignId,
          campaignName: campaign.campaignName,
          questId: quest.questId,
          questTitle: quest.questTitle || quest.questId,
          activationTitle: quest.activationTitle || "",
          step: quest.step || "",
          stepTitle: quest.stepTitle || "",
          status: quest.status || "Available",
          statusLabel: quest.statusLabel || "",
          // «Активен» = включён в кампании. Так решил Host: Runtime-активность
          // приходит отдельным полем, иначе при остановленной симуляции все
          // включённые квесты рисовались бы серыми.
          active: quest.active === true,
          runtimeActive: quest.runtimeActive === true,
          radius: Math.max(0, Number(quest.radius) || 0),
          order: Number(quest.order) || 0,
          fileName: quest.fileName || "",
          // Квест без найденной СДО остаётся в каталоге (он виден в панели
          // кампаний), но на карту не попадает: рисовать его негде.
          point: point || null
        });
      }
    }

    return entries
      .filter(entry => !!entry.point)
      .sort((a, b) =>
        (a.order <= 0 ? Number.MAX_SAFE_INTEGER : a.order) -
        (b.order <= 0 ? Number.MAX_SAFE_INTEGER : b.order) ||
        a.fileName.localeCompare(b.fileName, "ru") ||
        a.questId.localeCompare(b.questId, "ru"));
  }

  function isSelectedQuest(entry) {
    return String(entry.campaignId || "").toLowerCase() === String(selectedQuest.campaignId || "").toLowerCase() &&
      String(entry.questId || "").toLowerCase() === String(selectedQuest.questId || "").toLowerCase();
  }

  function isHoveredQuest(entry) {
    return !!hoveredQuest &&
      hoveredQuest.campaignId.toLowerCase() === String(entry.campaignId || "").toLowerCase() &&
      hoveredQuest.questId.toLowerCase() === String(entry.questId || "").toLowerCase();
  }

  function drawWorldPoint(ctx, point, width, height, occupied, showAllLabels, labelLimit) {
    const q = worldToScreen(point.position.x, point.position.z);
    if (q.x < -18 || q.y < -18 || q.x > width + 18 || q.y > height + 18) return;

    const selected = point.id === selectedPointId;
    const hovered = point.id === hoveredPointId;
    const isCity = point.isCity === true;
    const radius = isCity
      ? (hovered ? 5.2 : 3.4)
      : (selected ? 5.5 : (hovered ? 5.2 : (camera.mpp > 250 ? 3.8 : 4.2)));

    // Город — максимально жёлтый: он должен читаться как ориентир, а не как
    // очередная точка категории.
    const fill = isCity ? CITY_COLOR : darkenColor(point.color || "#78c8f0");

    ctx.save();
    ctx.beginPath();
    ctx.arc(q.x, q.y, radius, 0, Math.PI * 2);
    ctx.fillStyle = fill;
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
      ctx.strokeStyle = ACCENT_COLOR;
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

    // Город подписывается всегда: его название привязано к точке по центру, а
    // не раскладывается по сетке подписей. Для СДО действует общий лимит.
    if (isCity || selected || hovered ||
        shouldLabel(q, showAllLabels, labelLimit, occupied, point)) {
      drawPointLabel(ctx, point, q, selected, isCity);
    }
  }

  /**
   * Маркер квеста: пунктирная зона триггера, точка и плашка с названием.
   *
   * Неактивный квест не исчезает с карты, но теряет акцент: серые точка и
   * пунктир, тёмно-серый фон плашки и приглушённо-белый текст.
   */
  function drawQuestMarker(ctx, entry, active) {
    const q = worldToScreen(entry.point.position.x, entry.point.position.z);
    const size = visibleSize();
    const radiusPx = entry.radius / camera.mpp;

    if (q.x < -140 || q.y < -60 || q.x > size.width + 140 || q.y > size.height + 70) return;

    const selected = isSelectedQuest(entry);

    // Радиус — статичная геометрия зоны триггера.
    //
    // Раньше размер ромба считался от `Date.now()`, то есть квест «пульсировал»
    // не по времени, а по факту перерисовки: движение мыши по карте вызывает
    // drawMap(), и ромб дёргался под курсором. Радиус теперь постоянный.
    if (Number.isFinite(radiusPx) && radiusPx >= 2) {
      ctx.save();
      ctx.beginPath();
      ctx.arc(q.x, q.y, radiusPx, 0, Math.PI * 2);
      ctx.setLineDash([7, 5]);
      ctx.lineWidth = active ? 2 : 1.5;
      // Активный квест акцентирован тем же оранжевым, что и фон его точки на
      // карте; неактивный серый и в два раза прозрачнее.
      //
      // Заливка задаётся через globalAlpha с непрозрачным цветом: полупрозрачные
      // rgba-строки на канвасе читаются хуже, а тут видно намерение — «намёк на
      // радиус», а не реальная заливка, которая закрыла бы карту.
      ctx.strokeStyle = active ? ACCENT_COLOR : "rgba(120,128,140,.30)";
      ctx.fillStyle = active ? ACCENT_COLOR : "#78808c";
      ctx.globalAlpha = active ? 0.055 : 0.012;
      ctx.fill();
      ctx.globalAlpha = 1;
      ctx.stroke();
      ctx.restore();
    }

    const pulse = 8;

    // Точка квеста: ромб с центром в позиции. Зона попадания — ромб
    // (манхэттенское расстояние), чтобы клик мимо углов не срабатывал.
    questHitAreas.push({
      shape: "diamond",
      cx: q.x, cy: q.y, pulse: pulse + 4,
      left: q.x - pulse - 4, top: q.y - pulse - 4,
      right: q.x + pulse + 4, bottom: q.y + pulse + 4,
      entry
    });

    ctx.save();
    ctx.beginPath();
    ctx.moveTo(q.x, q.y - pulse);
    ctx.lineTo(q.x + pulse, q.y);
    ctx.lineTo(q.x, q.y + pulse);
    ctx.lineTo(q.x - pulse, q.y);
    ctx.closePath();
    ctx.fillStyle = active ? ACCENT_COLOR : INACTIVE_POINT_COLOR;

    // Тень точки квеста: акцентная оранжевая у активного, серая у неактивного.
    ctx.shadowColor = active ? withAlpha(ACCENT_COLOR, .85) : "rgba(90,96,106,.6)";
    ctx.shadowBlur = selected ? 16 : (active ? 10 : 5);
    ctx.shadowOffsetX = 0;
    ctx.shadowOffsetY = 2.5;
    ctx.fill();
    ctx.shadowColor = "transparent";
    ctx.shadowBlur = 0;
    ctx.shadowOffsetY = 0;

    ctx.lineWidth = 2.5;
    ctx.strokeStyle = "rgba(0,0,0,.95)";
    ctx.stroke();

    if (selected) {
      ctx.beginPath();
      ctx.arc(q.x, q.y, pulse + 6, 0, Math.PI * 2);
      ctx.lineWidth = 3;
      ctx.strokeStyle = ACCENT_COLOR;
      ctx.stroke();
    }

    ctx.font = "900 9px Open Sans, Arial, sans-serif";
    ctx.textAlign = "center";
    ctx.textBaseline = "middle";
    ctx.fillStyle = "#101319";
    ctx.fillText("Q", q.x, q.y + 0.5);
    ctx.restore();

    drawQuestPlate(ctx, entry, q.x, q.y + pulse + 8, active);
  }

  /**
   * Плашка названия квеста: название переносится по словам, затем разделитель,
   * затем статус и этап. Ширина вдвое меньше прежней, фон — акцентный
   * оранжевый на 25% прозрачности с чёрным текстом и удвоенной тенью.
   */
  function drawQuestPlate(ctx, entry, centerX, topY, active) {
    const size = visibleSize();
    const maxTextWidth = QUEST_PLATE_WIDTH - QUEST_PLATE_PADDING * 2;

    ctx.save();
    ctx.font = "700 9px Open Sans, Arial, sans-serif";
    const titleLines = wrapText(ctx, entry.questTitle || "Квест", maxTextWidth);

    ctx.font = "600 8px Open Sans, Arial, sans-serif";
    const statusText = [entry.statusLabel, entry.stepTitle || entry.step]
      .filter(part => !!part && String(part).trim().length > 0)
      .join(" · ");
    const statusLines = statusText ? wrapText(ctx, statusText, maxTextWidth) : [];

    const dividerSpace = statusLines.length ? 6 : 0;
    const boxWidth = QUEST_PLATE_WIDTH;
    const boxHeight = QUEST_PLATE_PADDING * 2 +
      titleLines.length * QUEST_PLATE_LINE_HEIGHT +
      dividerSpace +
      statusLines.length * 10;

    const x = Math.max(6, Math.min(size.width - boxWidth - 6, centerX - boxWidth / 2));
    const y = Math.max(6, Math.min(size.height - boxHeight - 6, topY));

    // Тень удвоена относительно точки квеста.
    ctx.shadowColor = active ? "rgba(0,0,0,.94)" : "rgba(0,0,0,.85)";
    ctx.shadowBlur = 12;
    ctx.shadowOffsetX = 0;
    ctx.shadowOffsetY = 5;

    ctx.beginPath();
    ctx.roundRect(x, y, boxWidth, boxHeight, 5);
    ctx.fillStyle = active ? withAlpha(ACCENT_COLOR, .25) : INACTIVE_PLATE_FILL;
    ctx.fill();
    ctx.shadowColor = "transparent";
    ctx.shadowBlur = 0;
    ctx.shadowOffsetY = 0;
    // Рамка плашки: подсветка курсора -> выделение -> обычное состояние.
    ctx.lineWidth = isHoveredQuest(entry) ? 2.5 : selectedStrokeWidth(entry);
    ctx.strokeStyle = isSelectedQuest(entry)
      ? ACCENT_COLOR
      : (isHoveredQuest(entry)
        ? "#ffffff"
        : (active ? withAlpha(ACCENT_COLOR, .55) : INACTIVE_PLATE_STROKE));
    ctx.stroke();

    // Плашка — прямоугольная зона попадания: ЛКМ по названию квеста открывает
    // список кампаний с подсветкой этого квеста.
    questHitAreas.push({
      shape: "rect",
      left: x, top: y, right: x + boxWidth, bottom: y + boxHeight,
      entry
    });
    // Текст рисуется с чёрным контуром: фон плашки полупрозрачный, и без
    // обводки чёрный текст терялся бы на светлых участках карты.
    ctx.textAlign = "left";
    ctx.textBaseline = "top";
    let cursorY = y + QUEST_PLATE_PADDING;
    ctx.font = "700 9px Open Sans, Arial, sans-serif";
    for (const line of titleLines) {
      strokeAndFillText(ctx, line, x + QUEST_PLATE_PADDING, cursorY,
        active ? "#000000" : INACTIVE_PLATE_TEXT, "rgba(0,0,0,.85)");
      cursorY += QUEST_PLATE_LINE_HEIGHT;
    }

    if (statusLines.length) {
      cursorY += 2;
      ctx.strokeStyle = active ? withAlpha(ACCENT_COLOR, .7) : INACTIVE_PLATE_STROKE;
      ctx.lineWidth = 1;
      ctx.beginPath();
      ctx.moveTo(x + QUEST_PLATE_PADDING, cursorY + 1);
      ctx.lineTo(x + boxWidth - QUEST_PLATE_PADDING, cursorY + 1);
      ctx.stroke();
      cursorY += 4;

      ctx.font = "600 8px Open Sans, Arial, sans-serif";
      for (const line of statusLines) {
        strokeAndFillText(ctx, line, x + QUEST_PLATE_PADDING, cursorY,
          active ? "#000000" : INACTIVE_PLATE_TEXT, "rgba(0,0,0,.85)");
        cursorY += 10;
      }
    }

    ctx.restore();
  }

  function selectedStrokeWidth(entry) {
    return isSelectedQuest(entry) ? 2.5 : 1;
  }

  function strokeAndFillText(ctx, text, x, y, fill, stroke) {
    ctx.lineWidth = 2.5;
    ctx.strokeStyle = stroke;
    ctx.strokeText(text, x, y);
    ctx.fillStyle = fill;
    ctx.fillText(text, x, y);
  }

  /** Перенос текста по словам. Длинное слово без пробелов режется посимвольно. */
  function wrapText(ctx, text, maxWidth) {
    const words = String(text ?? "").split(/\s+/).filter(Boolean);
    if (!words.length) return [];

    const lines = [];
    let current = "";
    for (const word of words) {
      const candidate = current ? current + " " + word : word;
      if (ctx.measureText(candidate).width <= maxWidth) {
        current = candidate;
        continue;
      }

      if (current) lines.push(current);
      if (ctx.measureText(word).width <= maxWidth) {
        current = word;
        continue;
      }

      // Слово само длиннее строки: режем по символам, чтобы текст не вылезал.
      let chunk = "";
      for (const char of word) {
        if (ctx.measureText(chunk + char).width > maxWidth && chunk) {
          lines.push(chunk);
          chunk = char;
        } else {
          chunk += char;
        }
      }
      current = chunk;
    }

    if (current) lines.push(current);
    return lines.slice(0, 3);
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
    ctx.save();
    ctx.textBaseline = "middle";
    // Тень под названием удваивается вместе с тенью точки: подпись должна
    // оставаться читаемой поверх светлой плашки квеста.
    ctx.lineWidth = selected ? 5.5 : (isCity ? 5 : 4.5);
    ctx.strokeStyle = "rgba(0,0,0,.98)";
    ctx.fillStyle = isCity ? CITY_COLOR : darkenColor(point.color || "#78c8f0");

    if (isCity) {
      // Название города — строго по центру НАД точкой. Ориентация в мире
      // важнее, чем компактность списка, поэтому город не участвует в
      // раскладке подписей по сетке (shouldLabel) и рисуется всегда, когда
      // видна сама точка: центр над точкой однозначно привязан к ней.
      ctx.font = "600 11px Open Sans, Arial, sans-serif";
      ctx.textAlign = "center";
      const baseline = q.y - 10;
      ctx.strokeText(text, q.x, baseline);
      ctx.fillText(text, q.x, baseline);
      ctx.restore();
      return;
    }

    ctx.font = selected
      ? "700 12px Open Sans, Arial, sans-serif"
      : "600 10px Open Sans, Arial, sans-serif";
    ctx.textAlign = "left";
    ctx.strokeText(text, q.x + 10, q.y - 10);
    ctx.fillText(text, q.x + 10, q.y - 10);
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
    // Размеры обычные (не удвоенные): квестовая графика читается за счёт
    // собственного слоя, а не за счёт гигантской тени.
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
    ctx.fillStyle = ACCENT_COLOR;
    ctx.fill();

    // Обводки идут снаружи внутрь, каждая перекрывает половину предыдущей по
    // нормали радиуса. Порядок от края к центру: чёрная → красная → чёрная →
    // оранжевая заливка.
    //
    // Первая чёрная отделяет маркер от светлых элементов карты и от линий
    // перекреста. Раньше красная обводка граничила с фоном напрямую, и на
    // светлых участках (плашки квестов, жёлтые города) маркер сливался.
    ctx.lineWidth = 4;
    ctx.strokeStyle = "rgba(0,0,0,.95)";
    ctx.beginPath();
    ctx.arc(q.x, q.y, radius + 3, 0, Math.PI * 2);
    ctx.stroke();

    // Красная обводка поверх внутренней половины чёрной.
    ctx.lineWidth = 3;
    ctx.strokeStyle = "#d83a3a";
    ctx.beginPath();
    ctx.arc(q.x, q.y, radius, 0, Math.PI * 2);
    ctx.stroke();

    // Чёрная обводка 2px между красной и оранжевой заливкой.
    //
    // Цвет непрозрачный: полупрозрачный чёрный поверх оранжевой заливки даёт
    // коричневатый оттенок, а не чёрный, и разделительная линия «пропадает».
    ctx.lineWidth = 2;
    ctx.strokeStyle = "#000000";
    ctx.beginPath();
    ctx.arc(q.x, q.y, radius - 2, 0, Math.PI * 2);
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

  /**
   * Проходит ли точка текущие галочки видимости.
   *
   * Отдельная проверка нужна в обработчиках галочек: они снимают подсветку и
   * выделение ДО перерисовки, когда `visiblePoints` ещё содержит прошлый кадр.
   */
  function isPointVisible(pointId) {
    const point = (snapshot?.world?.points || []).find(item =>
      String(item.id || "") === String(pointId || ""));
    if (!point) return false;
    const isCity = point.isCity === true;
    if (isCity && !citiesFilter) return false;
    if (!isCity && onlyQuestsFilter) return false;
    return true;
  }

  /**
   * Точка под курсором. Проверяются только точки, нарисованные в этом кадре.
   *
   * Список `visiblePoints` заполняет drawMap: он уже учитывает галочки «только
   * квесты» и «города» и выход за границы видимой области. Без этой проверки
   * скрытую фильтром СДО можно было бы «выбрать» вслепую — она осталась бы
   * кликабельной невидимой мишенью.
   */
  function hitPoint(px, py) {
    let best = null;
    let bestDistance = Math.min(18, Math.max(8, 10 / Math.sqrt(camera.mpp)));
    for (const point of visiblePoints) {
      const q = worldToScreen(point.position.x, point.position.z);
      const distance = Math.hypot(q.x - px, q.y - py);
      if (distance <= bestDistance) {
        best = point;
        bestDistance = distance;
      }
    }
    return best;
  }

  /**
   * Квестовые зоны под курсором.
   *
   * Зоны рисуются верхним слоем, поэтому ЛКМ обязан проверять их ПЕРВЫМ:
   * иначе квест под плашкой или точкой СДО был бы недоступен.
   *
   * Проверка идёт в два прохода: сначала зоны активных квестов, затем
   * неактивных. Активный квест перекрывает неактивный визуально, значит и
   * попадание должно выбирать его даже если сверху лежит плашка неактивного.
   * Внутри прохода плашка проверяется раньше точки: она перекрывает точку
   * соседнего квеста чаще, чем наоборот.
   */
  function hitQuestMarker(px, py) {
    return hitQuestArea(px, py, true) || hitQuestArea(px, py, false);
  }

  function hitQuestArea(px, py, activeOnly) {
    const inside = area =>
      px >= area.left && py >= area.top && px <= area.right && py <= area.bottom;
    const matches = area =>
      area.shape === "diamond"
        ? Math.abs(px - area.cx) + Math.abs(py - area.cy) <= area.pulse
        : (area.shape === "point"
          ? Math.hypot(px - area.cx, py - area.cy) <= area.radius
          : true);

    // Обратный порядок: последние нарисованные зоны проверяются первыми.
    for (let index = questHitAreas.length - 1; index >= 0; index -= 1) {
      const area = questHitAreas[index];
      if (area.entry.active !== activeOnly) continue;
      if (area.shape !== "rect") continue;
      if (inside(area) && matches(area)) return area.entry;
    }

    for (let index = questHitAreas.length - 1; index >= 0; index -= 1) {
      const area = questHitAreas[index];
      if (area.entry.active !== activeOnly) continue;
      if (area.shape === "rect") continue;
      if (inside(area) && matches(area)) return area.entry;
    }

    return null;
  }

  /** ЛКМ по точке квеста: выделяет квест и открывает окно кампаний с ним. */
  function selectQuestFromMap(entry) {
    selectedQuest = { campaignId: entry.campaignId, questId: entry.questId };
    const point = entry.point;
    if (point && !point.isCity) {
      // Точка квеста может совпадать с СДО: тогда выделяется и она, чтобы
      // правая панель показала координаты, а левая — выбранный квест.
      selectedPointId = point.id;
      send({ action: "select_point", id: point.id });
    }
    send({
      action: "open_campaigns",
      campaignId: entry.campaignId,
      questId: entry.questId
    });
    drawMap();
    renderRuntimeSidebar();
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
                "<span class='characterSkillLevel'>" +
                  escapeHtml(skill.kind === "Levelled"
                    ? ("Уровень " + Number(skill.level || 0) + "/" + Number(skill.maxLevel || 100))
                    : (skill.unlocked ? "Получен" : "Не изучен")) +
                "</span>" +
                // Описание обязано быть закрыто ДО закрытия .characterSkill.
                // Раньше `</div>` закрывал .characterSkillDesc, а внешний
                // .characterSkill оставался открытым, поэтому каждый следующий
                // скилл оказывался вложен в предыдущий (визуально «Очумелые
                // ручки» внутри «Автошкольника»).
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

  /**
   * Квест, выделенный на карте или в окне кампаний.
   *
   * Сайдбар показывает информацию ТОЛЬКО о нём: без выделения блоки о ноде,
   * цели и событиях не имеют смысла, потому что непонятно, к какому квесту
   * они относятся.
   */
  function selectedQuestEntry() {
    if (!selectedQuest.questId) return null;
    for (const campaign of questCatalog) {
      for (const quest of campaign.quests || []) {
        if (String(quest.questId || "").toLowerCase() !== String(selectedQuest.questId).toLowerCase()) continue;
        if (String(campaign.campaignId || "").toLowerCase() !== String(selectedQuest.campaignId || "").toLowerCase()) continue;
        return { campaign, quest };
      }
    }
    return null;
  }

  function selectedQuestBlock(entry) {
    const { campaign, quest } = entry;
    const point = findPoint(quest.worldPointId);
    const player = snapshot?.player?.position;
    const distance = point ? distanceMeters(player, point.position) : null;
    const radius = Number(quest.radius);
    const active = quest.active === true;
    const running = quest.runtimeActive === true;

    const statusText = [quest.statusLabel, quest.stepTitle || quest.step]
      .filter(part => !!part && String(part).trim().length > 0)
      .join(" · ");

    return "<section class='runtimeBlock selectedQuestBlock" + (active ? " active" : " inactive") + "'>" +
      "<div class='miniLabel'>Выбранный квест</div>" +
      "<div class='selectedQuestTitle'>" + escapeHtml(quest.questTitle || quest.questId) + "</div>" +
      "<div class='kv'><span>Кампания</span><span>" + escapeHtml(campaign.campaignName || campaign.campaignId) + "</span></div>" +
      "<div class='kv'><span>Порядок</span><span>#" + (Number(quest.order) || 0) + "</span></div>" +
      "<div class='kv'><span>Состояние</span><span>" +
        (active ? "Включён" : "Отключён") + (statusText ? " · " + escapeHtml(statusText) : "") + "</span></div>" +
      "<div class='kv'><span>Runtime</span><span>" +
        (running ? "Выполняется" : "Простаивает") + "</span></div>" +
      "<div class='kv'><span>Точка</span><span>" +
        (point ? escapeHtml(point.name || point.id) : escapeHtml(quest.worldPointId || "—")) + "</span></div>" +
      (point ? "<div class='kv'><span>Координаты</span><span>" + formatPosition(point.position) + "</span></div>" : "") +
      (point ? "<div class='kv'><span>Радиус</span><span>" + formatMeters(radius) + "</span></div>" : "") +
      (point ? "<div class='kv'><span>До игрока</span><span>" + formatMeters(distance) + "</span></div>" : "") +
      "<button class='smallButton' id='focusSelectedQuest' style='margin-top:8px'>Показать на карте</button>" +
      "</section>";
  }

  function renderRuntimeSidebar() {
    if (!runtimeSide || !snapshot) return;

    const status = runtimeStatusName(runtime?.status || "Stopped");
    const node = getRuntimeNode();
    const info = runtimeTargetInfo();
    const expectedEvent = runtimeExpectedEvent();
    const pointCount = snapshot.world?.points?.length || 0;
    const entry = selectedQuestEntry();

    runtimeSide.innerHTML =
      "<div class='runtimeSideHeader'>" +
        "<div>" +
          "<div class='panelTitle'>Квесты</div>" +
          "<div class='runtimeQuestName'>" +
            (simulationRunning ? "Симуляция запущена" : "Симуляция остановлена") +
          "</div>" +
        "</div>" +
      "</div>" +
      "<div class='runtimeControls'>" +
        "<button class='smallButton' id='fitWorldSide'>Все СДО</button>" +
        "<button class='smallButton' id='openCampaignsSide'>Кампании</button>" +
      "</div>" +
      "<div class='miniLabel' style='margin-top:8px'>СДО на карте: " +
        pointCount.toLocaleString("ru-RU") + "</div>" +
      (entry
        ? selectedQuestBlock(entry)
        : "<section class='runtimeBlock'><div class='notice'>Квест не выбран. " +
          "Выбери его на карте или в окне кампаний.</div></section>") +
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
    runtimeSide.querySelector("#openCampaignsSide")?.addEventListener("click", () => {
      send({
        action: "open_campaigns",
        campaignId: selectedQuest.campaignId,
        questId: selectedQuest.questId
      });
    });
    runtimeSide.querySelector("#focusSelectedQuest")?.addEventListener("click", () => {
      const selected = selectedQuestEntry();
      const point = selected ? findPoint(selected.quest.worldPointId) : null;
      if (!point) return;
      camera.cx = Number(point.position.x);
      camera.cz = Number(point.position.z);
      drawMap();
    });
    runtimeSide.querySelector("#emitExpectedEventSide")?.addEventListener("click", () => {
      const expected = runtimeExpectedEvent();
      if (expected) send({ action: "emit_event", ...expected });
    });
    runtimeSide.querySelector("#hornEventSide")?.addEventListener("click", () => {
      send({ action: "emit_event", eventType: "HornPressed", source: "Simulator", payload: {} });
    });

  }

  function drawHud() {
    const p = snapshot.player.position;
    const selected = snapshot.selection?.point;
    const points = snapshot.world?.points || [];
    const sdoCount = points.filter(point => !point.isCity).length;
    const cityCount = points.filter(point => point.isCity).length;
    const questCount = questCatalog.reduce((sum, campaign) => sum + (campaign.quests?.length || 0), 0);
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
      "<span class='badge accent'>Квесты " + questCount.toLocaleString("ru-RU") + "</span>",
      "<span class='badge blue'>X " + Math.round(p.x) + "</span>",
      "<span class='badge blue'>Y " + Math.round(p.y) + "</span>",
      "<span class='badge blue'>Z " + Math.round(p.z) + "</span>",
      "<span class='badge accent'>" + (selected ? "Выбрана: " + escapeHtml(selected.name || selected.category) : "Точка не выбрана") + "</span>"
    ].join("");

    // Статус-панель внизу карты: галочки фильтров и краткая сводка.
    if (onlyQuestsToggle) onlyQuestsToggle.checked = onlyQuestsFilter;
    if (citiesToggle) citiesToggle.checked = citiesFilter;
    if (mapStatusHint) {
      // Сводка должна описывать именно то, что нарисовано: две галочки
      // независимы, поэтому считаем по каждому типу отдельно.
      const parts = [];
      if (citiesFilter) parts.push("города (" + cityCount.toLocaleString("ru-RU") + ")");
      else parts.push("города скрыты (" + cityCount.toLocaleString("ru-RU") + ")");
      if (onlyQuestsFilter) parts.push("СДО скрыты (" + sdoCount.toLocaleString("ru-RU") + ")");
      else parts.push("СДО (" + sdoCount.toLocaleString("ru-RU") + ")");
      mapStatusHint.textContent = parts.join(" · ");
    }

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

  /**
   * Каталог квестов в форме «кампания со вложенным списком квестов».
   *
   * Host присылает именно эту форму, но нормализация терпима и к плоскому
   * списку квестов с полями кампании: WebView2 кеширует web-ресурсы, поэтому
   * новая страница может получить payload от старого host. Без нормализации
   * каталог в этом случае молча оказался бы пустым — ни квестов на карте, ни
   * счётчика в статусе.
   */
  function normalizeQuestCatalog(raw) {
    if (!Array.isArray(raw) || !raw.length) return [];

    const grouped = new Map();
    const campaigns = [];

    for (const item of raw) {
      if (!item) continue;

      if (Array.isArray(item.quests)) {
        campaigns.push({
          campaignId: item.campaignId || "",
          campaignName: item.campaignName || item.campaignId || "",
          quests: item.quests.slice()
        });
        continue;
      }

      const key = String(item.campaignId || "").toLowerCase();
      let campaign = grouped.get(key);
      if (!campaign) {
        campaign = {
          campaignId: item.campaignId || "",
          campaignName: item.campaignName || item.campaignId || "",
          quests: []
        };
        grouped.set(key, campaign);
        campaigns.push(campaign);
      }
      campaign.quests.push(item);
    }

    return campaigns;
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
      questCatalog = normalizeQuestCatalog(message.questCatalog);
      selectedQuest = {
        campaignId: message.selectedQuest?.campaignId || "",
        questId: message.selectedQuest?.questId || ""
      };
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

      // Квестовая графика рисуется верхним слоем, поэтому первой проверяется
      // именно она: иначе плашка квеста «съедалась» бы точкой СДО под ней.
      const quest = hitQuestMarker(pos.x, pos.y);
      if (quest) {
        selectQuestFromMap(quest);
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
      const overQuest = hitQuestMarker(pos.x, pos.y);
      const hoveredPoint = overQuest ? null : hitPoint(pos.x, pos.y);

      let needsRedraw = false;
      if (overQuest !== hoveredQuest) needsRedraw = true;
      hoveredQuest = overQuest;

      const nextHoveredPointId = hoveredPoint?.id || null;
      if (nextHoveredPointId !== hoveredPointId) {
        hoveredPointId = nextHoveredPointId;
        needsRedraw = true;
      }

      // Курсор показывает, что квест кликабелен, даже если под плашкой лежит
      // точка СДО: приоритет клика у квестовой графики.
      const cursor = overQuest ? "pointer" : (hoveredPoint ? (hoveredPoint.isCity ? "default" : "pointer") : "default");
      if (map.style.cursor !== cursor) map.style.cursor = cursor;

      if (needsRedraw) drawMap();
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
    if (hoveredPointId !== null || hoveredQuest !== null) {
      hoveredPointId = null;
      hoveredQuest = null;
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

  document.getElementById("openCampaigns")?.addEventListener("click", () => {
    send({
      action: "open_campaigns",
      campaignId: selectedQuest.campaignId,
      questId: selectedQuest.questId
    });
  });

  onlyQuestsToggle?.addEventListener("change", () => {
    onlyQuestsFilter = !!onlyQuestsToggle.checked;
    // Скрытая точка перестаёт быть кликабельной: курсор и подсветка снимаются
    // вместе с фильтром, иначе под указателем остался бы «призрак» selection.
    if (hoveredPointId && !isPointVisible(hoveredPointId)) hoveredPointId = null;
    if (selectedPointId && !isPointVisible(selectedPointId)) selectedPointId = null;
    drawMap();
  });

  citiesToggle?.addEventListener("change", () => {
    citiesFilter = !!citiesToggle.checked;
    if (hoveredPointId && !isPointVisible(hoveredPointId)) hoveredPointId = null;
    if (selectedPointId && !isPointVisible(selectedPointId)) selectedPointId = null;
    drawMap();
  });

  document.getElementById("simulationToggle")?.addEventListener("click", () => {
    send({ action: simulationRunning ? "simulation_stop" : "simulation_start" });
  });

  document.getElementById("reset")?.addEventListener("click", () => send({ action: "reset" }));

  // Кнопка нужна, когда файл квеста правили вне редактора: каталог квестов
  // кэшируется в Host, и без перечитывания карта показывала бы старую точку
  // активации. Сохранение из редактора обновляет каталог автоматически.
  document.getElementById("reloadCatalog")?.addEventListener("click", () => send({ action: "reload_catalog" }));

  window.addEventListener("resize", () => {
    drawMap();
  });
})();
