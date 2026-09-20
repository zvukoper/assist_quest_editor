(() => {
  const map = document.getElementById("map");
  const side = document.getElementById("side");
  const hud = document.getElementById("hud");
  const send = payload => window.chrome?.webview?.postMessage(payload);
  let snapshot = null;
  let drag = null;
  const eventHistory = [];

  const escapeHtml = value => String(value ?? "").replace(/[&<>"']/g, char => ({
    "&": "&amp;",
    "<": "&lt;",
    ">": "&gt;",
    "\"": "&quot;",
    "'": "&#39;"
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

  function draw() {
    if (!snapshot) return;
    const p = snapshot.player.position;

    map.innerHTML = [
      "<path d='M-250 170 L-55 35 L185 100 L445 0 L560 175 L410 355 L85 320 L-155 365 Z' fill='#171b20' stroke='rgba(255,255,255,.12)'/>",
      "<path d='M-210 280 C-45 180 35 235 130 165 S350 70 535 145' fill='none' stroke='#707a85' stroke-width='8' opacity='.24'/>",
      "<path d='M-235 95 C-80 135 65 150 170 255 S345 330 505 285' fill='none' stroke='#505862' stroke-width='3' opacity='.32'/>",
      snapshot.world.points.map(point => {
        const active = point.id === "ruslan" || point.id === "gosha";
        return "<g class='poi " + (active ? "active" : "") + "' transform='translate(" + point.position.x + " " + point.position.z + ")' data-point='" + escapeHtml(point.id) + "'>" +
          "<circle r='" + (active ? 9 : 7) + "'></circle>" +
          "<text x='13' y='-12'>" + escapeHtml(point.name) + "</text>" +
          "<text x='13' y='2' fill='#7d8792' font-size='9'>" + Math.round(point.position.x) + ", " + Math.round(point.position.z) + "</text>" +
        "</g>";
      }).join(""),
      "<g class='playerMarker' id='playerMarker' transform='translate(" + p.x + " " + p.z + ")'><circle r='10'></circle><circle r='3' fill='#fff' stroke='none'></circle></g>"
    ].join("");

    map.querySelectorAll("[data-point]").forEach(el => {
      el.addEventListener("click", () => send({ action: "move_player_to_point", id: el.dataset.point }));
    });

    const marker = map.querySelector("#playerMarker");
    marker.addEventListener("pointerdown", startDrag);

    const q = snapshot.questStatuses.quests[0] || {};
    hud.innerHTML = [
      "<span class='badge blue'>X " + Math.round(p.x) + "</span>",
      "<span class='badge blue'>Y " + Math.round(p.y) + "</span>",
      "<span class='badge blue'>Z " + Math.round(p.z) + "</span>",
      "<span class='badge accent'>Квест: " + escapeHtml(q.status || "—") + "</span>",
      "<span class='badge'>" + escapeHtml(q.step || "—") + "</span>"
    ].join("");

    renderSide();
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

    side.querySelectorAll("[data-move]").forEach(button => {
      button.addEventListener("click", () => send({ action: "move_player_to_point", id: button.dataset.move }));
    });

    bindInputs();
    const eventList = side.querySelector("#eventList");
    if (eventList) {
      eventList.innerHTML = eventHistory.map(event => renderEvent(event)).join("");
    }
  }

  function sectionBody(id) {
    if (id === "player") {
      const p = snapshot.player.position;
      return [
        "<div class='fieldGrid'>",
          "<div class='field'><label>X</label><input data-player='x' value='" + p.x + "'></div>",
          "<div class='field'><label>Y</label><input data-player='y' value='" + p.y + "'></div>",
          "<div class='field'><label>Z</label><input data-player='z' value='" + p.z + "'></div>",
          "<div class='field'><label>Скорость</label><input value='" + snapshot.player.speedKmh + "' disabled></div>",
        "</div>",
        "<div class='quickGrid' style='margin-top:8px'>",
          "<button class='quick' data-move='ruslan'>К Руслану</button><button class='quick' data-move='gosha'>К Гоше</button>",
          "<button class='quick' data-move='yard'>К двору</button><button class='quick' data-move='village'>В деревню</button>",
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

  const webview = window.chrome?.webview;
  webview?.addEventListener("message", event => {
    const data = typeof event.data === "string" ? JSON.parse(event.data) : event.data;
    receive(data);
  });

  function startDrag(event) {
    event.preventDefault();
    drag = true;

    const move = pointerEvent => {
      if (!drag || !snapshot) return;
      const point = svgPoint(pointerEvent.clientX, pointerEvent.clientY);
      map.querySelector("#playerMarker")?.setAttribute("transform", "translate(" + point.x + " " + point.y + ")");
    };

    const up = pointerEvent => {
      if (!snapshot) return;
      const point = svgPoint(pointerEvent.clientX, pointerEvent.clientY);
      send({ action: "set_player_position", x: point.x, y: snapshot.player.position.y, z: point.y });
      drag = null;
      window.removeEventListener("pointermove", move);
      window.removeEventListener("pointerup", up);
    };

    window.addEventListener("pointermove", move);
    window.addEventListener("pointerup", up);
  }

  function svgPoint(x, y) {
    const point = map.createSVGPoint();
    point.x = x;
    point.y = y;
    return point.matrixTransform(map.getScreenCTM().inverse());
  }

  document.getElementById("reset").addEventListener("click", () => send({ action: "reset" }));
  document.getElementById("mapWrap").addEventListener("dblclick", event => {
    if (!snapshot) return;
    const point = svgPoint(event.clientX, event.clientY);
    send({ action: "set_player_position", x: point.x, y: snapshot.player.position.y, z: point.y });
  });
})();
