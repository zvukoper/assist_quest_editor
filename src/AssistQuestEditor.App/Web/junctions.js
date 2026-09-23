// Окно ручной проверки перекрёстков.
//
// Задача окна: дать автору пройти по найденным узлам и решить, какие из них
// ложные (мост, эстакада — пересекаются в плане, но повернуть там нельзя), а
// какие поиск пропустил. Первое уходит в список исключений, второе — в список
// добавленных; оба записываются в файл и учитываются критерием
// «в радиусе от перекрёстка».
//
// Рисуется только дорожная сетка, подписи городов и сами перекрёстки. Точки СДО
// намеренно не показываются: их тысячи, они забили бы карту, а к перекрёсткам
// отношения не имеют.
//
// ПОДПИСКА НА СООБЩЕНИЯ: и `window`, и `chrome.webview`. Host отправляет через
// CoreWebView2.PostWebMessageAsJson, и WebView2 доставляет это слушателям
// `chrome.webview` — НЕ `window`. Один канал дал бы «глухое» окно: карта
// нарисовалась бы пустой без единой ошибки на странице (проверено в этом репо).
(() => {
  "use strict";

  const canvas = document.getElementById("junctionCanvas");
  const host = document.getElementById("junctionCanvasHost");
  const counter = document.getElementById("junctionCounter");
  const hint = document.getElementById("junctionHint");
  const status = document.getElementById("junctionStatus");

  // --- Состояние ---

  let roads = [];              // плоский массив [x1,z1,x2,z2, ...]
  let cities = [];             // [{ name, x, z }]
  let junctions = [];          // итоговый список: найденные минус исключённые плюс добавленные
  let selected = new Set();    // индексы выбранных узлов (мультивыбор)
  let focused = -1;            // текущий узел для «следующий/предыдущий»
  let excluded = [];           // [{x,z}] — пометки автора
  let added = [];              // [{x,z}] — добавленные вручную
  let reviewPath = "";
  let camera = { cx: 0, cz: 0, mpp: 1 };
  let dragging = false;
  let dragMoved = false;
  let dragStart = null;

  const POINT_RADIUS = 6;      // радиус попадания курсора в узел, пиксели
  const ACCENT = "#ffcc00";    // жёлтая точка — найденный перекрёсток

  /** Радиус, в пределах которого координата считается тем же узлом. Должен
   *  совпадать с JunctionReview.MatchRadius в Host, иначе пометка «исключён»
   *  не найдёт свой узел после пересборки списка. */
  const MATCH_RADIUS = 0.5;

  function log(level, message, details) {
    window.assistWebLog?.(level, message, details);
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

  /** Подгоняет камеру под все данные: дороги, города и узлы. */
  function fitAll() {
    let minX = Infinity, maxX = -Infinity, minZ = Infinity, maxZ = -Infinity;

    for (let i = 0; i < roads.length; i += 4) {
      minX = Math.min(minX, roads[i], roads[i + 2]);
      maxX = Math.max(maxX, roads[i], roads[i + 2]);
      minZ = Math.min(minZ, roads[i + 1], roads[i + 3]);
      maxZ = Math.max(maxZ, roads[i + 1], roads[i + 3]);
    }

    // Города и добавленные узлы могут лежать вне дорог — они тоже участвуют в
    // подгонке, иначе добавленный узел оказался бы за кадром.
    for (const city of cities) {
      minX = Math.min(minX, city.x); maxX = Math.max(maxX, city.x);
      minZ = Math.min(minZ, city.z); maxZ = Math.max(maxZ, city.z);
    }
    for (const point of added) {
      minX = Math.min(minX, point.x); maxX = Math.max(maxX, point.x);
      minZ = Math.min(minZ, point.z); maxZ = Math.max(maxZ, point.z);
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

  /** Приближает камеру к узлу так, чтобы было видно и дороги, и узел. */
  function focusJunction(index) {
    const junction = junctions[index];
    if (!junction) return;

    camera.cx = junction.x;
    camera.cz = junction.z;
    // 120 м по ширине кадра: видно и сам перекрёсток, и подходящие дороги.
    const s = visibleSize();
    camera.mpp = Math.max(0.05, 240 / Math.max(1, Math.min(s.width, s.height)));
    draw();
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
    drawCities(ctx);
    drawJunctions(ctx);
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

  function drawCities(ctx) {
    ctx.save();
    ctx.fillStyle = "rgba(255,224,138,.95)";
    ctx.font = "12px Open Sans, Arial, sans-serif";
    ctx.textBaseline = "bottom";

    for (const city of cities) {
      const screen = worldToScreen(city.x, city.z);
      if (screen.x < -200 || screen.y < -40) continue;
      const size = visibleSize();
      if (screen.x > size.width + 200 || screen.y > size.height + 40) continue;

      // Только имя: точка рядом с названием не нужна, город и так читается как
      // ориентир, а лишний маркер спорил бы с жёлтыми точками перекрёстков.
      ctx.fillText(city.name, screen.x + 3, screen.y - 3);
    }

    ctx.restore();
  }

  function drawJunctions(ctx) {
    const size = visibleSize();

    junctions.forEach((junction, index) => {
      const screen = worldToScreen(junction.x, junction.z);
      if (screen.x < -20 || screen.y < -20) return;
      if (screen.x > size.width + 20 || screen.y > size.height + 20) return;

      const isFocused = index === focused;
      const isSelected = selected.has(index);

      // Кольцо выбора рисуется ПОД точкой и крупнее её: иначе на плотной карте
      // выбранный узел не отличался бы от соседних.
      if (isSelected) {
        ctx.save();
        ctx.strokeStyle = "#4dc3ff";
        ctx.lineWidth = 2;
        ctx.beginPath();
        ctx.arc(screen.x, screen.y, 11, 0, Math.PI * 2);
        ctx.stroke();
        ctx.restore();
      }

      if (isFocused) {
        ctx.save();
        ctx.strokeStyle = "#ffffff";
        ctx.lineWidth = 1.5;
        ctx.beginPath();
        ctx.arc(screen.x, screen.y, 15, 0, Math.PI * 2);
        ctx.stroke();
        ctx.restore();
      }

      ctx.beginPath();
      ctx.fillStyle = ACCENT;
      ctx.arc(screen.x, screen.y, 5, 0, Math.PI * 2);
      ctx.fill();
    });

    drawAddedPoints(ctx);
  }

  /**
   * Добавленные вручную узлы — зелёные.
   *
   * Отдельный цвет, а не тот же жёлтый: автор должен видеть, что это ЕГО находка,
   * а не результат поиска. Иначе непонятно, что именно было добавлено руками.
   */
  function drawAddedPoints(ctx) {
    ctx.save();
    ctx.fillStyle = "#54d16a";

    for (const point of added) {
      const screen = worldToScreen(point.x, point.z);
      ctx.beginPath();
      ctx.arc(screen.x, screen.y, 6, 0, Math.PI * 2);
      ctx.fill();

      // Крестик внутри: отличает добавленную точку от жёлтой даже в чёрно-белом
      // скриншоте и на мелком масштабе.
      ctx.strokeStyle = "#0d1014";
      ctx.lineWidth = 1.5;
      ctx.beginPath();
      ctx.moveTo(screen.x - 3, screen.y - 3);
      ctx.lineTo(screen.x + 3, screen.y + 3);
      ctx.moveTo(screen.x + 3, screen.y - 3);
      ctx.lineTo(screen.x - 3, screen.y + 3);
      ctx.stroke();
    }

    ctx.restore();
  }

  // --- Работа с узлами ---

  /** Индекс узла под курсором или -1. */
  function junctionAt(px, py) {
    for (let i = 0; i < junctions.length; i++) {
      const screen = worldToScreen(junctions[i].x, junctions[i].z);
      if (Math.hypot(screen.x - px, screen.y - py) <= POINT_RADIUS + 4) return i;
    }
    return -1;
  }

  /** Индекс добавленной точки под курсором или -1. */
  function addedAt(px, py) {
    for (let i = 0; i < added.length; i++) {
      const screen = worldToScreen(added[i].x, added[i].z);
      if (Math.hypot(screen.x - px, screen.y - py) <= POINT_RADIUS + 4) return i;
    }
    return -1;
  }

  function updateStatus() {
    if (!counter) return;
    const position = focused >= 0 ? `${focused + 1} / ${junctions.length}` : `— / ${junctions.length}`;
    counter.textContent = position;
    if (hint) {
      hint.textContent = ` · выбрано ${selected.size}` +
        ` · исключено ${excluded.length} · добавлено ${added.length}`;
    }
  }

  function moveFocus(step) {
    if (!junctions.length) return;

    focused = focused < 0
      ? (step > 0 ? 0 : junctions.length - 1)
      : (focused + step + junctions.length) % junctions.length;

    selected.clear();
    selected.add(focused);
    focusJunction(focused);
    updateStatus();
  }

  /**
   * Исключает выбранные узлы.
   *
   * Выбранные узлы не удаляются из списка сразу: они убираются только при
   * сохранении, когда Host пересобирает список. Так «Вернуть» работает без
   * обращения к Host, и автор может передумать до записи файла.
   */
  function excludeSelected() {
    if (!selected.size) return;

    for (const index of selected) {
      const junction = junctions[index];
      if (!junction) continue;
      if (!excluded.some(point => Math.hypot(point.x - junction.x, point.z - junction.z) <= MATCH_RADIUS))
        excluded.push({ x: junction.x, z: junction.z });
      // Если узел был добавлен вручную, исключение должно снять добавление:
      // иначе он остался бы в списке как добавленный и как исключённый.
      added = added.filter(point => Math.hypot(point.x - junction.x, point.z - junction.z) > MATCH_RADIUS);
    }

    log("INFO", "Исключены перекрёстки.", { count: selected.size });
    selected.clear();
    updateStatus();
    draw();
  }

  /** Снимает пометку «исключён» с выбранных узлов и возвращает их в список. */
  function restoreSelected() {
    if (!selected.size) return;

    const removed = [];
    for (const index of selected) {
      const junction = junctions[index];
      if (!junction) continue;
      removed.push({ x: junction.x, z: junction.z });
    }

    excluded = excluded.filter(point =>
      !removed.some(other => Math.hypot(point.x - other.x, point.z - other.z) <= MATCH_RADIUS));

    // Восстановленные узлы возвращаются в список, если их там нет: список в окне
    // уже не содержит исключённые, поэтому без дописывания они не появились бы
    // до пересборки на стороне Host.
    for (const point of removed) {
      if (junctions.some(j => Math.hypot(j.x - point.x, j.z - point.z) <= MATCH_RADIUS)) continue;
      junctions.push(point);
    }
    junctions.sort((a, b) => a.x - b.x || a.z - b.z);

    log("INFO", "Возвращены перекрёстки.", { count: removed.length });
    selected.clear();
    updateStatus();
    draw();
  }

  /**
   * Добавляет узел по клику на пустое место.
   *
   * Повторный клик по существующей зелёной точке убирает её: это защита от
   * случайного клика, из-за которого появился бы лишний узел.
   */
  function toggleAddPoint(px, py) {
    const existing = addedAt(px, py);
    if (existing >= 0) {
      added.splice(existing, 1);
      log("INFO", "Добавленная точка убрана.", { remaining: added.length });
      updateStatus();
      draw();
      return;
    }

    const world = screenToWorld(px, py);

    // Клик рядом с найденным узлом ничего не добавляет: автор, скорее всего,
    // промахнулся мимо жёлтой точки, а не хотел создать дубликат.
    if (junctions.some(j => Math.hypot(j.x - world.x, j.z - world.z) <= 10)) {
      log("INFO", "Рядом уже есть найденный перекрёсток: добавление пропущено.", world);
      return;
    }

    added.push({ x: world.x, z: world.z });
    log("INFO", "Добавлен перекрёсток вручную.", world);
    updateStatus();
    draw();
  }

  function saveReview() {
    // Исключённые и добавленные уходят раздельно: Host не может вывести их из
    // показанного списка, потому что тот уже не содержит исключённые.
    send({
      action: "save_junction_review",
      excluded: excluded.map(point => ({ x: point.x, z: point.z })),
      added: added.map(point => ({ x: point.x, z: point.z }))
    });
  }

  // --- Ввод ---

  canvas?.addEventListener("pointerdown", event => {
    const rect = canvas.getBoundingClientRect();
    const px = event.clientX - rect.left;
    const py = event.clientY - rect.top;

    dragging = true;
    dragMoved = false;
    dragStart = { x: event.clientX, y: event.clientY, cx: camera.cx, cz: camera.cz };
  });

  canvas?.addEventListener("pointermove", event => {
    if (!dragging) return;

    const dx = event.clientX - dragStart.x;
    const dy = event.clientY - dragStart.y;

    // Порог в 3 пикселя: без него дрожание мыши при клике сдвигало бы карту и
    // клик по узлу не срабатывал.
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

    // Ctrl (и Cmd) — мультивыбор: автор отмечает несколько узлов и исключает их
    // одним нажатием. Обычный клик одиночный, иначе нельзя было бы перейти к
    // одному узлу, не потеряв предыдущий выбор.
    const multi = event.ctrlKey || event.metaKey;

    // Сначала проверяем зелёные точки: они рисуются поверх и должны отзываться
    // на клик даже рядом с жёлтым узлом. Мультивыбор работает только по найденным
    // узлам — зелёная точка не узел списка, поэтому клик по ней всегда переключает
    // её наличие.
    if (addedAt(px, py) >= 0) {
      toggleAddPoint(px, py);
      return;
    }

    const index = junctionAt(px, py);
    if (index >= 0) {
      if (multi) {
        // Ctrl+клик по выбранному узлу снимает выбор: иначе мультивыбор нельзя
        // было бы исправить, не сбросив всё.
        if (selected.has(index)) selected.delete(index);
        else selected.add(index);
      } else {
        selected.clear();
        selected.add(index);
        focused = index;
      }

      updateStatus();
      draw();
      return;
    }

    // Пустое место: Ctrl+клик — это сброс выбора, обычный клик — добавить узел.
    if (multi) {
      selected.clear();
      updateStatus();
      draw();
      return;
    }

    toggleAddPoint(px, py);
  });

  // Колесо мыши — масштаб вокруг курсора: проверять узлы в городе без этого
  // невозможно, там они стоят в десятках метров друг от друга.
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

  document.getElementById("junctionPrev")?.addEventListener("click", () => moveFocus(-1));
  document.getElementById("junctionNext")?.addEventListener("click", () => moveFocus(1));
  document.getElementById("junctionExclude")?.addEventListener("click", excludeSelected);
  document.getElementById("junctionRestore")?.addEventListener("click", restoreSelected);
  document.getElementById("junctionSave")?.addEventListener("click", saveReview);
  document.getElementById("junctionFitAll")?.addEventListener("click", () => { fitAll(); draw(); });
  document.getElementById("junctionReload")?.addEventListener("click", () => send({ action: "reload_junction_review" }));

  // --- Приём сообщений Host ---

  function applyReview(data) {
    roads = Array.isArray(data.roads) ? data.roads : [];
    cities = Array.isArray(data.cities) ? data.cities : [];
    junctions = (Array.isArray(data.junctions) ? data.junctions : [])
      .map(point => ({ x: Number(point.x), z: Number(point.z) }))
      .filter(point => Number.isFinite(point.x) && Number.isFinite(point.z));

    reviewPath = typeof data.reviewPath === "string" ? data.reviewPath : "";
    selected.clear();
    focused = -1;

    // Правки ПРИХОДЯТ от Host, а не восстанавливаются из показанного списка:
    // список уже не содержит исключённые узлы, и вывести из него, что было
    // исключено, невозможно.
    excluded = (Array.isArray(data.excluded) ? data.excluded : [])
      .map(point => ({ x: Number(point.x), z: Number(point.z) }))
      .filter(point => Number.isFinite(point.x) && Number.isFinite(point.z));
    added = (Array.isArray(data.added) ? data.added : [])
      .map(point => ({ x: Number(point.x), z: Number(point.z) }))
      .filter(point => Number.isFinite(point.x) && Number.isFinite(point.z));

    fitAll();
    draw();
    updateStatus();

    log("INFO", "Правка перекрёстков: данные получены.", {
      reason: data.reason || "",
      detected: data.detectedCount,
      shown: junctions.length,
      excluded: data.excludedCount,
      added: data.addedCount
    });
  }

  function handleMessage(event) {
    let data = event?.data;
    if (typeof data === "string") {
      try { data = JSON.parse(data); } catch { return; }
    }
    if (!data || typeof data !== "object") return;

    if (data.type === "junction_review") {
      applyReview(data);
      return;
    }

    if (data.type === "junction_review_saved") {
      log("INFO", "Правки перекрёстков записаны.", {
        excluded: data.excludedCount,
        added: data.addedCount,
        path: data.path
      });
      if (hint) hint.textContent = ` · сохранено: исключено ${data.excludedCount}, добавлено ${data.addedCount}`;
      return;
    }

    if (data.type === "junction_review_empty") {
      if (hint) hint.textContent = " · " + (data.message || "нет данных");
      log("WARN", "Правка перекрёстков: нет данных.", data);
      return;
    }

    if (data.type === "junction_review_error") {
      if (hint) hint.textContent = " · " + (data.message || "ошибка");
      log("ERROR", "Правка перекрёстков: ошибка.", data);
    }
  }

  // Оба канала: Host доставляет сообщения слушателям chrome.webview, а window
  // нужен для синтетических сообщений в smoke-проверках.
  window.addEventListener("message", handleMessage);
  if (typeof window.chrome?.webview?.addEventListener === "function") {
    window.chrome.webview.addEventListener("message", handleMessage);
  }

  window.addEventListener("resize", draw);

  // Карта пустая до первого сообщения Host — рисуем фон, чтобы окно не выглядело
  // сломанным, пока данные идут.
  window.__assistJunctionReview = {
    applyReview,
    getState: () => ({
      junctions: junctions.length,
      selected: selected.size,
      excluded: excluded.length,
      added: added.length,
      focused,
      // Координаты выбранного узла отдаются отдельно от индекса: проверка
      // обязана убедиться, что «следующий» приводит к НУЖНОМУ узлу, а не просто
      // увеличивает счётчик. По одному индексу подмена порядка обхода
      // (например, сортировка по координатам) осталась бы незамеченной.
      focusX: focused >= 0 && junctions[focused] ? junctions[focused].x : null,
      focusZ: focused >= 0 && junctions[focused] ? junctions[focused].z : null
    })
  };

  draw();
})();
