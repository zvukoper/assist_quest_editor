(() => {
  const map = document.getElementById("mapCanvas");
  const side = document.getElementById("side");
  const hud = document.getElementById("hud");
  const send = payload => window.chrome?.webview?.postMessage(payload);

  let snapshot = null;
  let draggingPlayer = false;
  let panning = false;
  let panStart = null;
  let selectedPointId = null;
  let camera = { cx: 0, cz: 0, mpp: 50 };
  let eventHistory = [];

  const escapeHtml = value => String(value ?? "").replace(/[&<>"']/g, char => ({
    "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;", "'": "&#39;"
  }[char]));

  const sections = [
    ["player", "Игрок и мир"],
    ["facts", "Факты"],
    ["statuses", "Статусы"],
    ["states", "Состояния"],
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
    const showAllLabels = camera.mpp < 35;
    const labelLimit = camera.mpp < 120 ? 250 : 70;
    const occupied = new Set();

    for (const point of points) {
      const q = worldToScreen(point.position.x, point.position.z);
      if (q.x < -18 || q.y < -18 || q.x > width + 18 || q.y > height + 18) continue;

      const selected = point.id === selectedPointId;
      const radius = selected ? 6.5 : 4.5;

      ctx.save();
      ctx.beginPath();
      ctx.arc(q.x, q.y, radius, 0, Math.PI * 2);
      ctx.fillStyle = point.color || "#78c8f0";
      ctx.fill();
      if (selected) {
        ctx.lineWidth = 4;
        ctx.strokeStyle = "#fab003";
        ctx.stroke();
      }
      ctx.restore();

      if (selected || shouldLabel(q, showAllLabels, labelLimit, occupied, point)) {
        drawPointLabel(ctx, point, q, selected);
      }
    }

    drawPlayer(ctx, snapshot.player);
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
    ctx.strokeStyle = "rgba(255,255,255,.055)";
    ctx.lineWidth = 1;

    for (let x = Math.floor(left / step) * step; x <= right; x += step) {
      const sx = worldToScreen(x, camera.cz).x;
      ctx.beginPath();
      ctx.moveTo(sx, 0);
      ctx.lineTo(sx, height);
      ctx.stroke();
    }
    for (let z = Math.floor(top / step) * step; z <= bottom; z += step) {
      const sy = worldToScreen(camera.cx, z).y;
      ctx.beginPath();
      ctx.moveTo(0, sy);
      ctx.lineTo(width, sy);
      ctx.stroke();
    }
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

  function drawPointLabel(ctx, point, q, selected) {
    const text = point.name || point.category || "СДО";
    ctx.save();
    ctx.font = selected ? "700 12px Open Sans, Arial, sans-serif" : "600 10px Open Sans, Arial, sans-serif";
    ctx.textAlign = "left";
    ctx.textBaseline = "middle";
    ctx.lineWidth = selected ? 4 : 3;
    ctx.strokeStyle = "rgba(0,0,0,.95)";
    ctx.strokeText(text, q.x + 10, q.y - 10);
    ctx.fillStyle = point.color || "#78c8f0";
    ctx.fillText(text, q.x + 10, q.y - 10);
    ctx.restore();
  }

  function drawPlayer(ctx, player) {
    const p = player?.position;
    if (!p) return;
    const q = worldToScreen(p.x, p.z);
    if (q.x < -50 || q.y < -50 || q.x > visibleSize().width + 50 || q.y > visibleSize().height + 50) return;

    const shape = [[0, -18], [-10, 14], [0, 9], [10, 14]];
    ctx.save();
    ctx.translate(q.x, q.y);
    ctx.rotate(-Number(player.heading || 0) * 360 * Math.PI / 180);
    ctx.shadowColor = "rgba(0,0,0,.92)";
    ctx.shadowBlur = 9;
    ctx.shadowOffsetX = 0;
    ctx.shadowOffsetY = 3;
    ctx.beginPath();
    ctx.moveTo(shape[0][0], shape[0][1]);
    for (let i = 1; i < shape.length; i++) ctx.lineTo(shape[i][0], shape[i][1]);
    ctx.closePath();
    ctx.fillStyle = "#ff4d4d";
    ctx.fill();
    ctx.shadowColor = "rgba(0,0,0,0)";
    ctx.shadowBlur = 0;
    ctx.shadowOffsetY = 0;
    ctx.lineWidth = 3;
    ctx.strokeStyle = "#fff";
    ctx.stroke();
    ctx.restore();
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

  function drawHud() {
    const p = snapshot.player.position;
    const selected = snapshot.selection?.point;
    const points = snapshot.world?.points || [];
    hud.innerHTML = [
      "<span class='badge blue'>СДО " + points.length.toLocaleString("ru-RU") + "</span>",
      "<span class='badge blue'>X " + Math.round(p.x) + "</span>",
      "<span class='badge blue'>Y " + Math.round(p.y) + "</span>",
      "<span class='badge blue'>Z " + Math.round(p.z) + "</span>",
      "<span class='badge accent'>" + (selected ? "Выбрана: " + escapeHtml(selected.name || selected.category) : "Точка не выбрана") + "</span>"
    ].join("");
  }

    function renderSide() {
    side.innerHTML = sections.map((entry, index) => {
      const id = entry[0];
      const label = entry[1];
      return "<section class='acc " + (index === 0 ? "open" : "") + "' data-section='" + id + "'>" +
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

    if (id === "inventory") {
      return Object.entries(snapshot.inventory.items).map(([key, value]) =>
        "<div class='field' style='margin-top:6px'><label>" + escapeHtml(key) + "</label><input type='number' data-item='" + escapeHtml(key) + "' value='" + value + "'></div>"
      ).join("") +
      "<div class='inline' style='margin-top:8px'><input id='newItemKey' placeholder='item_id'><input id='newItemValue' value='1'><button class='smallButton primary' id='addItem'>+</button></div>";
    }

    if (id === "reputation") {
      return Object.entries(snapshot.reputation.values).map(([key, value]) =>
        "<div class='field' style='margin-top:6px'><label>" + escapeHtml(key) + "</label><input type='number' data-rep='" + escapeHtml(key) + "' value='" + value + "'></div>"
      ).join("") +
      "<div class='inline' style='margin-top:8px'><input id='newRepKey' placeholder='фракция'><input id='newRepValue' value='0'><button class='smallButton primary' id='addRep'>+</button></div>";
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
      return [
        "<div class='field'><label>Тип события</label><input id='eventType' value='CustomEvent'></div>",
        "<div class='field' style='margin-top:6px'><label>Источник</label><input id='eventSource' value='Simulator'></div>",
        "<div class='field' style='margin-top:6px'><label>Payload JSON</label><textarea id='eventPayload'>{"quest":"special_marinated_shashlik"}</textarea></div>",
        "<button class='smallButton primary' id='emitEvent'>Отправить событие</button>",
        "<div class='miniLabel' style='margin:12px 0 5px'>Последние события</div>",
        "<div class='eventList' id='eventList'></div>"
      ].join("");
    }

    if (id === "system") {
      return [
        "<div class='kv'><span>Runtime</span><span>" + (snapshot.system.runtimeRunning ? "работает" : "остановлен") + "</span></div>",
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
    return "<div class='eventRow'><strong>" + escapeHtml(event.eventType) + "</strong>" +
      new Date(event.timestamp).toLocaleTimeString("ru-RU") + " · " + escapeHtml(event.source) + "</div>";
  }

  function receive(message) {
    if (!message) return;
    if (message.type === "snapshot") {
      snapshot = message.snapshot;
      draw();
    }
    if (message.type === "event") {
      eventHistory.unshift(message["@event"] || message.event);
      eventHistory.splice(12);
      const list = side.querySelector("#eventList");
      if (list) list.innerHTML = eventHistory.map(renderEvent).join("");
    }
    if (message.type === "error") {
      window.alert(message.message || "Ошибка симулятора");
    }
  }

  function receive(message) {
    if (!message) return;

    if (message.type === "snapshot") {
      const hadSnapshot = !!snapshot;
      snapshot = message.snapshot;
      selectedPointId = snapshot.selection?.point?.id || null;
      if (!hadSnapshot) fitWorld();
      drawMap();
      renderSide();
      return;
    }

    if (message.type === "event") {
      eventHistory.unshift(message["@event"] || message.event);
      eventHistory.splice(12);
      const list = side.querySelector("#eventList");
      if (list) list.innerHTML = eventHistory.map(renderEvent).join("");
      return;
    }

    if (message.type === "error") {
      window.alert(message.message || "Ошибка симулятора");
    }
  }

  const webview = window.chrome?.webview;
  webview?.addEventListener("message", event => {
    const data = typeof event.data === "string" ? JSON.parse(event.data) : event.data;
    receive(data);
  });

  map.addEventListener("pointerdown", event => {
    const pos = pointerPosition(event);

    if (event.button === 0) {
      if (hitPlayer(pos.x, pos.y)) {
        draggingPlayer = true;
        map.setPointerCapture?.(event.pointerId);
        event.preventDefault();
        return;
      }

      const point = hitPoint(pos.x, pos.y);
      if (point) {
        send({ action: "select_point", id: point.id });
      } else if (snapshot?.selection?.point) {
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

    if (draggingPlayer && snapshot) {
      const world = screenToWorld(pos.x, pos.y);
      drawMap();
      drawPlayerPreview(world);
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
      draggingPlayer = false;
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
    }
  });

  map.addEventListener("wheel", event => {
    const pos = pointerPosition(event);
    const before = screenToWorld(pos.x, pos.y);
    camera.mpp = Math.max(0.5, Math.min(50000, camera.mpp * (event.deltaY < 0 ? 0.8 : 1.25)));
    const after = screenToWorld(pos.x, pos.y);
    camera.cx += before.x - after.x;
    camera.cz += before.z - after.z;
    drawMap();
    event.preventDefault();
  }, { passive: false });

  map.addEventListener("contextmenu", event => event.preventDefault());

  function drawPlayerPreview(world) {
    if (!snapshot) return;
    const dpr = window.devicePixelRatio || 1;
    const ctx = map.getContext("2d");
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    const temp = {
      ...snapshot.player,
      position: { ...snapshot.player.position, x: world.x, z: world.z }
    };
    drawPlayer(ctx, temp);
  }

  document.getElementById("reset").addEventListener("click", () => send({ action: "reset" }));
  window.addEventListener("resize", () => {
    drawMap();
  });
})();
