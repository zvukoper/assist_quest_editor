// Окно рисования черты города.
//
// Единственное взаимодействие с картой — РИСОВАНИЕ МНОГОУГОЛЬНИКА. Точки не
// выделяются, не перетаскиваются и не редактируются: окно существует ради одной
// задачи, и любое лишнее действие отнимало бы внимание от контура. Автор обводит
// город кликами, замыкает контур на начальной точке и сохраняет.
//
// Название черты определяет город, оказавшийся ВНУТРИ контура: «Черта города
// Казань». Проверка «сколько городов внутри» делается в Host, а не здесь: у него
// полный список точек мира, а окно знает только то, что ему прислали.
//
// ПОДПИСКА НА СООБЩЕНИЯ: и `window`, и `chrome.webview`. Host отправляет через
// CoreWebView2.PostWebMessageAsJson, и WebView2 доставляет это слушателям
// `chrome.webview` — НЕ `window`. Один канал дал бы «глухое» окно: карта
// нарисовалась бы пустой без единой ошибки на странице.
(() => {
  "use strict";

  const canvas = document.getElementById("boundaryCanvas");
  const host = document.getElementById("boundaryCanvasHost");
  const counter = document.getElementById("boundaryCounter");
  const hint = document.getElementById("boundaryHint");
  const message = document.getElementById("boundaryMessage");

  // --- Состояние ---

  let roads = [];              // плоский массив [x1,z1,x2,z2, ...]
  let cities = [];             // [{ name }] — только имена, точки городов уже есть в worldPoints
  let worldPoints = [];        // [{ name, category, x, z, isCity }]
  let boundaries = [];         // [{ cityId, cityName, title, points: [x,z,...] }]
  let outline = [];            // текущий контур автора: [{x,z}]
  let boundaryPath = "";       // путь к файлу черт (для состояния окна)
  let missingCount = 0;        // сколько городов осталось без черты
  let receivedOnce = false;    // данные получены хотя бы раз
  let camera = { cx: 0, cz: 0, mpp: 1 };
  let dragging = false;
  let dragMoved = false;
  let dragStart = null;

  // Радиус попадания курсора в первую вершину: чтобы замкнуть контур, автор
  // кликает по ней, и промах на пару пикселей не должен добавлять лишнюю вершину.
  const CLOSE_RADIUS = 10;

  function log(level, note, details) {
    window.assistWebLog?.(level, note, details);
  }

  function send(payload) {
    if (typeof window.__assistSend === "function") {
      window.__assistSend(payload);
      return;
    }
    window.chrome?.webview?.postMessage(payload);
  }

  // --- Геометрия ---

  function visibleSize() {
    return { width: host.clientWidth || 1, height: host.clientHeight || 1 };
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

  /** Подгоняет камеру под все данные: дороги, города, точки и черты. */
  function fitAll() {
    let minX = Infinity, maxX = -Infinity, minZ = Infinity, maxZ = -Infinity;

    for (let i = 0; i < roads.length; i += 4) {
      minX = Math.min(minX, roads[i], roads[i + 2]);
      maxX = Math.max(maxX, roads[i], roads[i + 2]);
      minZ = Math.min(minZ, roads[i + 1], roads[i + 3]);
      maxZ = Math.max(maxZ, roads[i + 1], roads[i + 3]);
    }

    if (!Number.isFinite(minX) || !Number.isFinite(minZ)) {
      camera = { cx: 0, cz: 0, mpp: 50 };
      return;
    }

    const s = visibleSize();
    camera.cx = (minX + maxX) / 2;
    camera.cz = (minZ + maxZ) / 2;
    camera.mpp = Math.max(
      0.05,
      Math.max(maxX - minX, 100) / Math.max(1, s.width - 60),
      Math.max(maxZ - minZ, 100) / Math.max(1, s.height - 60)
    );
  }

  // --- Отрисовка ---

  function draw() {
    const dpr = window.devicePixelRatio || 1;
    const width = host.clientWidth || 1;
    const height = host.clientHeight || 1;

    canvas.width = Math.max(1, Math.round(width * dpr));
    canvas.height = Math.max(1, Math.round(height * dpr));

    const ctx = canvas.getContext("2d");
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.fillStyle = "#0d1014";
    ctx.fillRect(0, 0, width, height);

    drawRoads(ctx, width, height);
    drawWorldPoints(ctx);
    drawCities(ctx);
    drawSavedBoundaries(ctx);
    drawOutline(ctx);
  }

  function drawRoads(ctx, width, height) {
    if (!roads.length) return;

    const lineWidth = Math.max(0.6, Math.min(2.6, 20 / camera.mpp));
    const margin = 40;

    ctx.save();
    ctx.lineCap = "round";
    ctx.lineJoin = "round";
    ctx.lineWidth = lineWidth;
    ctx.strokeStyle = "rgba(118,130,146,.85)";
    ctx.beginPath();

    for (let i = 0; i + 3 < roads.length; i += 4) {
      const a = worldToScreen(roads[i], roads[i + 1]);
      const b = worldToScreen(roads[i + 2], roads[i + 3]);

      if (Math.max(a.x, b.x) < -margin || Math.min(a.x, b.x) > width + margin) continue;
      if (Math.max(a.y, b.y) < -margin || Math.min(a.y, b.y) > height + margin) continue;

      ctx.moveTo(a.x, a.y);
      ctx.lineTo(b.x, b.y);
    }

    ctx.stroke();
    ctx.restore();
  }

  /**
   * Точки мира с подписями.
   *
   * Показываются ВСЕ точки, а не только города: черта нужна именно для отбора
   * точек, и автору важно видеть, что в неё попадает. Точки не выделяются —
   * клик по карте ставит вершину контура, а не выбирает точку.
   *
   * На мелком масштабе подписи не рисуются: тысячи названий слились бы в кашу и
   * закрыли бы дороги, по которым автор ориентируется.
   */
  function drawWorldPoints(ctx) {
    if (!worldPoints.length) return;

    const withLabels = camera.mpp < 12;
    const size = visibleSize();

    ctx.save();
    ctx.font = "11px Open Sans, Arial, sans-serif";
    ctx.textBaseline = "bottom";

    for (const point of worldPoints) {
      const screen = worldToScreen(point.x, point.z);
      if (screen.x < -80 || screen.y < -20) continue;
      if (screen.x > size.width + 80 || screen.y > size.height + 20) continue;

      // Города крупнее и ярче остальных точек: по ним автор понимает, какой
      // населённый пункт обводит.
      ctx.beginPath();
      ctx.fillStyle = point.isCity ? "#e2c85f" : "rgba(120,200,240,.75)";
      ctx.arc(screen.x, screen.y, point.isCity ? 4 : 2, 0, Math.PI * 2);
      ctx.fill();

      if (withLabels) {
        ctx.fillStyle = point.isCity ? "rgba(255,224,138,.95)" : "rgba(150,190,215,.7)";
        ctx.fillText(point.name, screen.x + 4, screen.y - 3);
      }
    }

    ctx.restore();
  }

  /**
   * Названия городов.
   *
   * Отдельно от точек: у города точка есть в списке точек мира, но её подпись
   * может не попасть в кадр на мелком масштабе, а название нужно всегда — по
   * нему определяется черта.
   */
  function drawCities(ctx) {
    ctx.save();
    ctx.fillStyle = "rgba(255,224,138,.95)";
    ctx.font = "bold 13px Open Sans, Arial, sans-serif";
    ctx.textBaseline = "bottom";

    for (const city of cities) {
      const screen = worldToScreen(city.x, city.z);
      if (!Number.isFinite(screen.x) || !Number.isFinite(screen.y)) continue;
      const size = visibleSize();
      if (screen.x < -200 || screen.y < -40) continue;
      if (screen.x > size.width + 200 || screen.y > size.height + 40) continue;

      ctx.fillText(city.name, screen.x + 5, screen.y - 5);
    }

    ctx.restore();
  }

  /** Уже сохранённые черты — тонкой зелёной линией, чтобы их не спутать с текущей. */
  function drawSavedBoundaries(ctx) {
    if (!boundaries.length) return;

    ctx.save();
    ctx.strokeStyle = "rgba(84,209,106,.65)";
    ctx.lineWidth = 1.5;
    ctx.setLineDash([6, 4]);

    for (const boundary of boundaries) {
      const points = boundary.points;
      if (!points || points.length < 6) continue;

      ctx.beginPath();
      for (let i = 0; i + 1 < points.length; i += 2) {
        const screen = worldToScreen(points[i], points[i + 1]);
        if (i === 0) ctx.moveTo(screen.x, screen.y);
        else ctx.lineTo(screen.x, screen.y);
      }
      ctx.closePath();
      ctx.stroke();
    }

    ctx.restore();
  }

  /** Текущий контур автора: замкнутый — сплошной заливкой, незамкнутый — линией. */
  function drawOutline(ctx) {
    if (!outline.length) return;

    const closed = isClosed();
    const screens = outline.map(point => worldToScreen(point.x, point.z));

    ctx.save();

    if (closed) {
      // Замкнутый контур заливается полупрозрачно: автор видит саму ОБЛАСТЬ,
      // а не только линию, и сразу замечает, попал ли город внутрь.
      ctx.beginPath();
      ctx.moveTo(screens[0].x, screens[0].y);
      for (let i = 1; i < screens.length; i++) ctx.lineTo(screens[i].x, screens[i].y);
      ctx.closePath();
      ctx.fillStyle = "rgba(77,195,255,.18)";
      ctx.fill();
    }

    ctx.strokeStyle = closed ? "#4dc3ff" : "#ffcc00";
    ctx.lineWidth = 2;
    ctx.lineJoin = "round";
    ctx.beginPath();
    ctx.moveTo(screens[0].x, screens[0].y);
    for (let i = 1; i < screens.length; i++) ctx.lineTo(screens[i].x, screens[i].y);
    if (closed) ctx.closePath();
    else ctx.lineTo(screens[screens.length - 1].x, screens[screens.length - 1].y);
    ctx.stroke();

    // Вершины: первая крупнее и с кольцом — по ней замыкают контур.
    screens.forEach((screen, index) => {
      ctx.beginPath();
      ctx.fillStyle = index === 0 ? "#ffffff" : "#4dc3ff";
      ctx.arc(screen.x, screen.y, index === 0 ? 6 : 4, 0, Math.PI * 2);
      ctx.fill();

      if (index === 0) {
        ctx.strokeStyle = "#4dc3ff";
        ctx.lineWidth = 2;
        ctx.beginPath();
        ctx.arc(screen.x, screen.y, 11, 0, Math.PI * 2);
        ctx.stroke();
      }
    });

    ctx.restore();
  }

  // --- Работа с контуром ---

  /**
   * Признак замкнутости хранится ОТДЕЛЬНЫМ флагом, а не выводится из геометрии.
   *
   * Автор замыкает контур кликом по первой вершине, и эта вершина остаётся в
   * списке ровно один раз. Решать «замкнут ли» по совпадению последней и первой
   * координаты значило бы, что контур, у которого последняя точка случайно легла
   * на первую, считается замкнутым без ведома автора.
   */
  let closed = false;

  function isClosed() {
    return closed && outline.length >= 3;
  }

  function setMessage(text, isError) {
    if (!message) return;
    message.textContent = text ? " · " + text : "";
    message.style.color = isError ? "#ff7b72" : "#ffb454";
  }

  function updateStatus() {
    if (counter) counter.textContent = "вершин: " + outline.length + (isClosed() ? " (замкнут)" : "");
    if (hint && outline.length === 0) {
      hint.textContent = " · клик — вершина контура, замкните контур на первой точке";
    }
  }

  function addVertex(world) {
    // Контур уже замкнут: новые вершины не добавляются, иначе автор случайно
    // перечертил бы готовую область вместо сохранения.
    if (isClosed()) {
      setMessage("черта замкнута — сохраните её или очистите контур", true);
      return;
    }

    // Клик рядом с первой вершиной ЗАМЫКАЕТ контур, а не добавляет вершину:
    // промах на пару пикселей не должен портить область.
    if (outline.length >= 3) {
      const first = worldToScreen(outline[0].x, outline[0].z);
      const current = worldToScreen(world.x, world.z);
      if (Math.hypot(first.x - current.x, first.y - current.y) <= CLOSE_RADIUS) {
        closeOutline();
        return;
      }
    }

    outline.push(world);
    setMessage("");
    updateStatus();
    draw();
  }

  function closeOutline() {
    if (outline.length < 3) {
      setMessage("для черты нужно не меньше трёх вершин", true);
      return;
    }

    closed = true;
    setMessage("черта замкнута — можно сохранять");
    updateStatus();
    draw();
  }

  function clearOutline() {
    outline = [];
    closed = false;
    setMessage("");
    updateStatus();
    draw();
  }

  function saveBoundary() {
    if (!isClosed()) {
      setMessage("сначала замкните черту на начальной точке", true);
      return;
    }

    // Отправляется только контур: город внутри определяет Host, у него полный
    // список точек мира.
    send({
      action: "save_city_boundary",
      points: outline.map(point => ({ x: point.x, z: point.z }))
    });
  }

  // --- Ввод ---

  canvas?.addEventListener("pointerdown", event => {
    dragging = true;
    dragMoved = false;
    dragStart = { x: event.clientX, y: event.clientY, cx: camera.cx, cz: camera.cz };
  });

  canvas?.addEventListener("pointermove", event => {
    if (!dragging) return;

    const dx = event.clientX - dragStart.x;
    const dy = event.clientY - dragStart.y;

    // Порог в 3 пикселя: без него дрожание мыши при клике сдвигало бы карту и
    // вершина контура ставилась бы не там, куда автор целился.
    if (!dragMoved && Math.hypot(dx, dy) < 3) return;

    dragMoved = true;
    camera.cx = dragStart.cx - dx * camera.mpp;
    camera.cz = dragStart.cz - dy * camera.mpp;
    draw();
  });

  canvas?.addEventListener("pointerup", event => {
    const wasDragging = dragging;
    dragging = false;
    if (!wasDragging || dragMoved) return;

    const rect = canvas.getBoundingClientRect();
    const px = event.clientX - rect.left;
    const py = event.clientY - rect.top;

    // Правая кнопка — отмена последней вершины: при обводке города ошибку
    // замечают сразу, и тянуться к кнопке на панели неудобно.
    if (event.button === 2) {
      removeLastVertex();
      return;
    }

    // Замкнутая область не перечерчивается: addVertex сам сообщит автору, что
    // контур уже замкнут и его надо сохранить или очистить.
    addVertex(screenToWorld(px, py));
  });

  // Правая кнопка используется для отмены вершины, поэтому браузерное меню на
  // карте подавляется.
  canvas?.addEventListener("contextmenu", event => event.preventDefault());

  // Колесо мыши — масштаб вокруг курсора: без этого на окраине города вершины
  // ставятся с точностью в километр.
  canvas?.addEventListener("wheel", event => {
    event.preventDefault();

    const rect = canvas.getBoundingClientRect();
    const px = event.clientX - rect.left;
    const py = event.clientY - rect.top;

    const before = screenToWorld(px, py);
    const factor = Math.exp(event.deltaY * 0.0015);
    camera.mpp = Math.max(0.05, Math.min(200, camera.mpp * factor));
    const after = screenToWorld(px, py);

    // Курсор остаётся над той же точкой мира.
    camera.cx += before.x - after.x;
    camera.cz += before.z - after.z;

    draw();
  }, { passive: false });

  function removeLastVertex() {
    if (!outline.length) return;

    // Снятие вершины размыкает контур: иначе получилась бы замкнутая область с
    // вырезанной вершиной, и «замкнут» перестал бы означать «автор подтвердил».
    outline.pop();
    closed = false;
    setMessage("");
    updateStatus();
    draw();
  }

  document.getElementById("boundaryClose")?.addEventListener("click", closeOutline);
  document.getElementById("boundarySave")?.addEventListener("click", saveBoundary);
  document.getElementById("boundaryUndo")?.addEventListener("click", removeLastVertex);
  document.getElementById("boundaryClear")?.addEventListener("click", clearOutline);
  document.getElementById("boundaryFitAll")?.addEventListener("click", () => { fitAll(); draw(); });
  document.getElementById("boundaryReload")?.addEventListener("click", () => send({ action: "reload_city_boundaries" }));

  document.getElementById("boundaryDelete")?.addEventListener("click", () => {
    // Удаляется первая нарисованная черта — та, что автор видит на карте. Отдельного
    // списка выбора нет: черт десятки, и городить ради них панель незачем; если
    // черт несколько, автор стирает их по одной.
    if (!boundaries.length) {
      setMessage("ни одной черты не нарисовано", true);
      return;
    }

    send({ action: "delete_city_boundary", city: boundaries[boundaries.length - 1].cityName });
  });

  // --- Приём сообщений Host ---

  function applyReview(data) {
    roads = Array.isArray(data.roads) ? data.roads : [];
    cities = Array.isArray(data.cities) ? data.cities : [];
    worldPoints = (Array.isArray(data.worldPoints) ? data.worldPoints : [])
      .map(point => ({
        name: String(point.name ?? ""),
        category: String(point.category ?? ""),
        x: Number(point.x),
        z: Number(point.z),
        isCity: !!point.isCity
      }))
      .filter(point => Number.isFinite(point.x) && Number.isFinite(point.z));

    boundaries = (Array.isArray(data.boundaries) ? data.boundaries : [])
      .map(boundary => ({
        cityId: String(boundary.cityId ?? ""),
        cityName: String(boundary.cityName ?? ""),
        title: String(boundary.title ?? ""),
        points: Array.isArray(boundary.points) ? boundary.points : []
      }));

    boundaryPath = typeof data.boundaryPath === "string" ? data.boundaryPath : "";
    missingCount = Number(data.missingCount) || 0;

    // Контур ПРИХОДИТ пустым после сохранения: Host пересобирает состояние, и
    // оставлять дорисованный контур значило бы предложить сохранить его второй раз.
    outline = [];
    closed = false;
    updateStatus();

    // Камера подгоняется только при ПЕРВОМ получении данных: иначе каждое
    // сохранение сбрасывало бы масштаб, которым автор только что пользовался.
    if (!receivedOnce) {
      receivedOnce = true;
      fitAll();
    }

    draw();

    if (missingCount > 0) {
      setMessage("без черты городов: " + missingCount + " — " +
        (Array.isArray(data.missingCities) ? data.missingCities.slice(0, 6).join(", ") : ""), true);
    }

    log("INFO", "Черты городов: данные получены.", {
      reason: data.reason || "",
      cities: cities.length,
      worldPoints: worldPoints.length,
      boundaries: boundaries.length,
      missing: missingCount
    });
  }

  function handleMessage(event) {
    let data = event?.data;
    if (typeof data === "string") {
      try { data = JSON.parse(data); } catch { return; }
    }
    if (!data || typeof data !== "object") return;

    if (data.type === "city_boundary_review") {
      applyReview(data);
      return;
    }

    if (data.type === "city_boundary_saved") {
      setMessage("сохранено: " + (data.title || data.cityName || ""));
      log("INFO", "Черта города записана.", {
        city: data.cityName,
        title: data.title,
        vertices: data.vertexCount,
        path: data.path
      });
      return;
    }

    if (data.type === "city_boundary_deleted") {
      setMessage("черта удалена: " + (data.city || ""));
      log("INFO", "Черта города удалена.", { city: data.city, path: data.path });
      return;
    }

    if (data.type === "city_boundary_rejected") {
      setMessage(data.message || "контур отклонён", true);
      log("WARN", "Черта города отклонена.", data);
      return;
    }

    if (data.type === "city_boundary_error") {
      setMessage(data.message || "ошибка", true);
      log("ERROR", "Черты городов: ошибка.", data);
    }
  }

  // Оба канала: Host доставляет сообщения слушателям chrome.webview, а window
  // нужен для синтетических сообщений в smoke-проверках.
  window.addEventListener("message", handleMessage);
  if (typeof window.chrome?.webview?.addEventListener === "function") {
    window.chrome.webview.addEventListener("message", handleMessage);
  }

  window.addEventListener("resize", draw);

  // Состояние для smoke-проверок: по нему проверка видит и контур, и число
  // черт, и что именно ушло в Host при сохранении.
  window.__assistCityBoundary = {
    applyReview,
    getState: () => ({
      cities: cities.length,
      worldPoints: worldPoints.length,
      boundaries: boundaries.length,
      vertices: outline.length,
      closed: isClosed(),
      missingCount,
      boundaryPath
    })
  };

  draw();
})();
