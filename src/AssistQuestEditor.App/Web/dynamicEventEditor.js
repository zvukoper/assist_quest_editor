(() => {
  const send = payload => {
    if (typeof window.__assistSend === "function") {
      window.__assistSend(payload);
      return;
    }
    window.chrome?.webview?.postMessage(payload);
  };

  let state = {
    definition: null,
    documentPath: "",
    documentDirty: false,
    readOnly: false,
    events: [],
    locations: [],
    runtime: { instances: [], schedules: [] }
  };

  function blankDefinition() {
    return {
      id: "event_" + Math.random().toString(36).slice(2, 10),
      name: "Новое динамическое событие",
      description: "",
      locationId: "",
      questId: "",
      triggerRadius: 35,
      category: "Dynamic Event",
      trigger: {
        type: "DistanceTravelled",
        minDistanceMeters: 5000,
        maxDistanceMeters: 10000,
        minGameHours: null,
        maxGameHours: null,
        minRealHours: null,
        maxRealHours: null,
        sourceDefinitionId: "",
        eventType: "",
        eventPayload: {}
      },
      spawnPolicy: {
        maxActiveInstances: 1,
        spawnChance: 1,
        cooldownGameHours: null,
        cooldownRealHours: null,
        lifetimeGameHours: 24,
        lifetimeRealHours: null,
        spawnOnSimulationStart: false,
        respawnOnExpired: false,
        removeOnCompleted: true
      },
      presentation: {
        discovery: "WorldMarker"
      }
    };
  }

  function esc(value) {
    return String(value ?? "")
      .replaceAll("&", "&amp;")
      .replaceAll("<", "&lt;")
      .replaceAll(">", "&gt;")
      .replaceAll('"', "&quot;")
      .replaceAll("'", "&#39;");
  }

  function number(value, fallback = 0) {
    const n = Number(value);
    return Number.isFinite(n) ? n : fallback;
  }

  function collect() {
    const current = state.definition || blankDefinition();
    const type = document.getElementById("dynamicEventTrigger")?.value || current.trigger?.type || "Manual";

    const definition = {
      ...current,
      id: document.getElementById("dynamicEventId")?.value.trim() || current.id,
      name: document.getElementById("dynamicEventName")?.value.trim() || "Динамическое событие",
      description: document.getElementById("dynamicEventDescription")?.value || "",
      locationId: document.getElementById("dynamicEventLocation")?.value.trim() || "",
      questId: document.getElementById("dynamicEventQuest")?.value.trim() || null,
      triggerRadius: number(document.getElementById("dynamicEventRadius")?.value, 35),
      category: document.getElementById("dynamicEventCategory")?.value.trim() || "Dynamic Event",
      trigger: {
        ...(current.trigger || {}),
        type,
        minDistanceMeters: number(document.getElementById("dynamicEventMinDistance")?.value, 5000),
        maxDistanceMeters: number(document.getElementById("dynamicEventMaxDistance")?.value, 10000),
        minGameHours: optionalNumber("dynamicEventMinGame"),
        maxGameHours: optionalNumber("dynamicEventMaxGame"),
        minRealHours: optionalNumber("dynamicEventMinReal"),
        maxRealHours: optionalNumber("dynamicEventMaxReal"),
        sourceDefinitionId: document.getElementById("dynamicEventSourceDefinition")?.value.trim() || null,
        eventType: document.getElementById("dynamicEventEventType")?.value.trim() || null,
        eventPayload: current.trigger?.eventPayload || {}
      },
      spawnPolicy: {
        ...(current.spawnPolicy || {}),
        maxActiveInstances: Math.max(0, Math.floor(number(document.getElementById("dynamicEventMaxActive")?.value, 1))),
        spawnChance: Math.min(1, Math.max(0, number(document.getElementById("dynamicEventChance")?.value, 1))),
        cooldownGameHours: optionalNumber("dynamicEventCooldownGame"),
        cooldownRealHours: optionalNumber("dynamicEventCooldownReal"),
        lifetimeGameHours: optionalNumber("dynamicEventLifetimeGame"),
        lifetimeRealHours: optionalNumber("dynamicEventLifetimeReal"),
        spawnOnSimulationStart: Boolean(document.getElementById("dynamicEventSpawnOnStart")?.checked),
        respawnOnExpired: Boolean(document.getElementById("dynamicEventRespawnOnExpired")?.checked),
        removeOnCompleted: Boolean(document.getElementById("dynamicEventRemove")?.checked)
      },
      presentation: {
        ...(current.presentation || {}),
        discovery: document.getElementById("dynamicEventDiscovery")?.value || "WorldMarker"
      }
    };

    return definition;
  }

  function optionalNumber(id) {
    const value = document.getElementById(id)?.value.trim();
    if (value === "") return null;
    const n = Number(value);
    return Number.isFinite(n) ? n : null;
  }

  function updateDirty() {
    state.definition = collect();
    state.documentDirty = true;
    send({ action: "dynamic_event_mark_dirty", definition: state.definition });
  }

  function locationOptions(selected) {
    return (state.locations || []).map(location =>
      "<option value='" + esc(location.id) + "'" +
      (String(location.id).toLowerCase() === String(selected || "").toLowerCase() ? " selected" : "") +
      ">" + esc(location.name || location.id) + " · " + esc(location.mode || "") + "</option>"
    ).join("");
  }

  function renderInspector(inspector, definition) {
    const instances = state.runtime?.instances || [];
    const schedules = state.runtime?.schedules || [];
    const own = instances.filter(item =>
      String(item.definitionId || "").toLowerCase() === String(definition.id || "").toLowerCase());

    inspector.innerHTML =
      "<div class='badge blue'>Runtime: " + own.length + " экземпляр(ов)</div>" +
      "<div class='kv'><span>Триггер</span><span>" + esc(definition.trigger?.type || "Manual") + "</span></div>" +
      "<div class='kv'><span>Location</span><span>" + esc(definition.locationId || "не задан") + "</span></div>" +
      "<div class='kv'><span>Активных правил</span><span>" + esc(schedules.length) + "</span></div>" +
      "<div style='margin-top:12px' class='miniLabel'>Экземпляры этого правила</div>" +
      (own.length
        ? own.map(item =>
            "<div class='notice' style='margin-top:6px'>" +
            "<strong>" + esc(item.instanceId) + "</strong><br>" +
            esc(item.status || "") + " · " +
            esc(item.point?.name || item.point?.id || "") +
            "</div>").join("")
        : "<div class='miniLabel'>Пока ничего не создано.</div>");
  }

  function render(ws, inspector) {
    const definition = state.definition || blankDefinition();
    state.definition = definition;

    const trigger = definition.trigger || {};
    const policy = definition.spawnPolicy || {};

    ws.innerHTML =
      "<div class='toolbar' style='margin-bottom:10px;flex-wrap:wrap'>" +
        "<button class='toolButton' id='newDynamicEvent'>Новый</button>" +
        "<button class='toolButton' id='openDynamicEvent'>Открыть</button>" +
        "<button class='toolButton primary' id='saveDynamicEvent'>Сохранить</button>" +
        "<button class='toolButton' id='saveDynamicEventAs'>Сохранить как…</button>" +
        "<button class='toolButton' id='deleteDynamicEvent'>Удалить</button>" +
        "<button class='toolButton' id='spawnDynamicEvent'>Создать сейчас</button>" +
        "<span class='badge " + (state.documentDirty ? "accent" : "blue") + "'>" +
          (state.documentDirty ? "Не сохранено" : "Сохранено") +
        "</span>" +
        "<span class='badge'>" + esc(state.documentPath || "Новый документ") + "</span>" +
      "</div>" +

      "<div class='notice'>" +
        "<strong>Это правило, а не созданная точка.</strong> Здесь задаётся, когда Dispatcher должен " +
        "создать runtime-экземпляр и через какой Location найти конкретный WorldPoint. " +
        "Результат «Создать сейчас» не записывается обратно в .aqevent." +
      "</div>" +

      "<div class='field'><label>ID</label><input id='dynamicEventId' value='" + esc(definition.id) + "'></div>" +
      "<div class='field'><label>Название</label><input id='dynamicEventName' value='" + esc(definition.name) + "'></div>" +
      "<div class='field'><label>Описание</label><textarea id='dynamicEventDescription' rows='3'>" + esc(definition.description) + "</textarea></div>" +
      "<div class='field'><label>Location</label><select id='dynamicEventLocation'>" +
        "<option value=''>— не выбрано —</option>" + locationOptions(definition.locationId) +
      "</select></div>" +
      "<div class='field'><label>Связанный Quest (необязательно)</label><input id='dynamicEventQuest' value='" + esc(definition.questId || "") + "' placeholder='Quest ID после обнаружения'></div>" +
      "<div class='field'><label>Радиус события, м</label><input id='dynamicEventRadius' type='number' min='0' value='" + esc(number(definition.triggerRadius, 35)) + "'></div>" +
      "<div class='field'><label>Категория</label><input id='dynamicEventCategory' value='" + esc(definition.category || "Dynamic Event") + "'></div>" +

      "<div class='sectionTitle' style='margin-top:16px'>Триггер генерации</div>" +
      "<div class='field'><label>Тип</label><select id='dynamicEventTrigger'>" +
        ["Manual","DistanceTravelled","GameTime","RealTime","WorldEvent","DynamicEventDiscovery"].map(type =>
          "<option value='" + type + "'" + (type === (trigger.type || "Manual") ? " selected" : "") + ">" + type + "</option>"
        ).join("") +
      "</select></div>" +

      "<div id='dynamicEventDistanceFields'>" +
        "<div class='field'><label>Минимальная дистанция, м</label><input id='dynamicEventMinDistance' type='number' min='0' value='" + esc(trigger.minDistanceMeters ?? 5000) + "'></div>" +
        "<div class='field'><label>Максимальная дистанция, м</label><input id='dynamicEventMaxDistance' type='number' min='0' value='" + esc(trigger.maxDistanceMeters ?? 10000) + "'></div>" +
      "</div>" +

      "<div id='dynamicEventDiscoveryFields' style='display:none'>" +
        "<div class='field'><label>Источник обнаружения</label><input id='dynamicEventSourceDefinition' value='" + esc(trigger.sourceDefinitionId || "") + "' placeholder='ID Dynamic Event'></div>" +
      "</div>" +

      "<div id='dynamicEventGameFields' style='display:none'>" +
        "<div class='field'><label>Минимальный интервал, игровых часов</label><input id='dynamicEventMinGame' type='number' min='0' value='" + esc(trigger.minGameHours ?? "") + "'></div>" +
        "<div class='field'><label>Максимальный интервал, игровых часов</label><input id='dynamicEventMaxGame' type='number' min='0' value='" + esc(trigger.maxGameHours ?? "") + "'></div>" +
      "</div>" +

      "<div id='dynamicEventRealFields' style='display:none'>" +
        "<div class='field'><label>Минимальный интервал, реальных часов</label><input id='dynamicEventMinReal' type='number' min='0' value='" + esc(trigger.minRealHours ?? "") + "'></div>" +
        "<div class='field'><label>Максимальный интервал, реальных часов</label><input id='dynamicEventMaxReal' type='number' min='0' value='" + esc(trigger.maxRealHours ?? "") + "'></div>" +
      "</div>" +

      "<div id='dynamicEventWorldFields' style='display:none'>" +
        "<div class='field'><label>Тип события в Event Bus</label><input id='dynamicEventEventType' value='" + esc(trigger.eventType || "") + "' placeholder='например HornPressed'></div>" +
        "<div class='miniLabel'>Дополнительный payload пока сохраняется в canonical JSON и может быть задан вручную. Это позволяет не потерять provider-specific события.</div>" +
      "</div>" +

      "<div class='sectionTitle' style='margin-top:16px'>Политика генерации</div>" +
      "<div class='field'><label>Максимум активных экземпляров</label><input id='dynamicEventMaxActive' type='number' min='0' value='" + esc(policy.maxActiveInstances ?? 1) + "'></div>" +
      "<div class='field'><label>Вероятность появления</label><input id='dynamicEventChance' type='number' min='0' max='1' step='0.01' value='" + esc(policy.spawnChance ?? 1) + "'></div>" +
      "<div class='field'><label>Cooldown, игровых часов</label><input id='dynamicEventCooldownGame' type='number' min='0' value='" + esc(policy.cooldownGameHours ?? "") + "'></div>" +
      "<div class='field'><label>Cooldown, реальных часов</label><input id='dynamicEventCooldownReal' type='number' min='0' value='" + esc(policy.cooldownRealHours ?? "") + "'></div>" +
      "<div class='field'><label>Lifetime, игровых часов</label><input id='dynamicEventLifetimeGame' type='number' min='0' value='" + esc(policy.lifetimeGameHours ?? "") + "'></div>" +
      "<div class='field'><label>Lifetime, реальных часов</label><input id='dynamicEventLifetimeReal' type='number' min='0' value='" + esc(policy.lifetimeRealHours ?? "") + "'></div>" +
      "<label class='checkRow'><input id='dynamicEventSpawnOnStart' type='checkbox' " + (policy.spawnOnSimulationStart ? "checked" : "") + "> создать первый экземпляр при запуске симуляции</label>" +
      "<label class='checkRow'><input id='dynamicEventRespawnOnExpired' type='checkbox' " + (policy.respawnOnExpired ? "checked" : "") + "> пересоздавать после истечения lifetime</label>" +
      "<label class='checkRow'><input id='dynamicEventRemove' type='checkbox' " + (policy.removeOnCompleted !== false ? "checked" : "") + "> удалять экземпляр после завершения</label>" +
      "<div class='field'><label>Presentation discovery</label><select id='dynamicEventDiscovery'>" +
        ["WorldMarker","Hidden","Minimap","AR","Radio"].map(type =>
          "<option value='" + type + "'" + (type === (definition.presentation?.discovery || "WorldMarker") ? " selected" : "") + ">" + type + "</option>"
        ).join("") +
      "</select></div>" +

      "<div class='notice' style='margin-top:14px'>" +
        "<strong>Runtime Test:</strong> «Создать сейчас» материализует экземпляр через Dispatcher. " +
        "Кнопка не меняет Location и не сохраняет resolved WorldPoint в этот ресурс." +
      "</div>";

    const triggerSelect = ws.querySelector("#dynamicEventTrigger");
    const syncTriggerFields = () => {
      const type = triggerSelect.value;
      ws.querySelector("#dynamicEventDistanceFields").style.display = type === "DistanceTravelled" ? "" : "none";
      ws.querySelector("#dynamicEventDiscoveryFields").style.display = type === "DynamicEventDiscovery" ? "" : "none";
      ws.querySelector("#dynamicEventGameFields").style.display =
        type === "GameTime" || type === "DynamicEventDiscovery" ? "" : "none";
      ws.querySelector("#dynamicEventRealFields").style.display = type === "RealTime" ? "" : "none";
      ws.querySelector("#dynamicEventWorldFields").style.display = type === "WorldEvent" ? "" : "none";
    };
    triggerSelect.addEventListener("change", () => {
      syncTriggerFields();
      updateDirty();
    });
    syncTriggerFields();

    ws.querySelectorAll("input, textarea, select").forEach(element => {
      element.addEventListener("change", updateDirty);
      element.addEventListener("input", () => {
        if (element.id !== "dynamicEventTrigger") updateDirty();
      });
    });

    // В CI/read-only режиме редактор не должен позволять открыть диалоги
    // записи: backend всё равно защитит библиотеку, но UX должен показывать это
    // заранее, как уже делает Location Editor.
    const writable = !state.readOnly;
    ws.querySelectorAll("input, textarea, select").forEach(element => {
      element.disabled = !writable;
    });
    ["#newDynamicEvent", "#openDynamicEvent", "#saveDynamicEvent", "#saveDynamicEventAs", "#deleteDynamicEvent"]
      .forEach(selector => {
        const button = ws.querySelector(selector);
        if (button) button.disabled = !writable;
      });
    const spawnButton = ws.querySelector("#spawnDynamicEvent");
    if (spawnButton) spawnButton.disabled = !Boolean(state.definition?.locationId);

    ws.querySelector("#newDynamicEvent").addEventListener("click", () => send({ action: "dynamic_event_new" }));
    ws.querySelector("#openDynamicEvent").addEventListener("click", () => send({ action: "dynamic_event_open" }));
    ws.querySelector("#saveDynamicEvent").addEventListener("click", () => send({
      action: "dynamic_event_save",
      definition: collect()
    }));
    ws.querySelector("#saveDynamicEventAs").addEventListener("click", () => send({
      action: "dynamic_event_save_as",
      definition: collect()
    }));
    ws.querySelector("#deleteDynamicEvent").addEventListener("click", () => send({ action: "dynamic_event_delete" }));
    ws.querySelector("#spawnDynamicEvent").addEventListener("click", () => {
      state.definition = collect();
      send({ action: "dynamic_event_spawn" });
    });

    renderInspector(inspector, definition);
  }

  const handleMessage = event => {
    const data = typeof event.data === "string" ? (() => {
      try { return JSON.parse(event.data); } catch { return null; }
    })() : event.data;

    if (!data) return;

    if (data.type === "dynamic_event_editor_state") {
      state = {
        definition: data.definition || blankDefinition(),
        documentPath: data.documentPath || "",
        documentDirty: Boolean(data.documentDirty),
        readOnly: Boolean(data.readOnly),
        events: Array.isArray(data.events) ? data.events : [],
        locations: Array.isArray(data.locations) ? data.locations : [],
        runtime: data.runtime || { instances: [], schedules: [] }
      };
      if (location.hash.toLowerCase() === "#events")
        render(
          document.getElementById("workspace"),
          document.getElementById("inspector"));
      return;
    }

    if (data.type === "simulator_event") {
      state.runtime = data.runtime || state.runtime;
      if (location.hash.toLowerCase() === "#events")
        render(document.getElementById("workspace"), document.getElementById("inspector"));
    }
  };

  window.addEventListener("message", handleMessage);
  const webview = window.chrome?.webview;
  if (typeof webview?.addEventListener === "function")
    webview.addEventListener("message", handleMessage);

  window.__assistDynamicEventEditor = { render };
})();
