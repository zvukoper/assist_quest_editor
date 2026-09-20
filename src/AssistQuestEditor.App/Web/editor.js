(() => {
  const root = document.getElementById("root");
  const title = document.getElementById("editorTitle");
  const send = payload => window.chrome?.webview?.postMessage(payload);

  let simulatorContext = {
    player: null,
    selection: { point: null }
  };

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
    ws.innerHTML =
      "<div class='toolbar' style='margin-bottom:10px'>" +
        "<button class='toolButton primary'>Добавить ноду</button>" +
        "<button class='toolButton'>По размеру</button>" +
        "<span class='badge accent'>Quest Graph</span>" +
        "<span class='badge'>Normal Flow</span>" +
        "<span class='badge red'>Cut Flow</span>" +
      "</div>" +
      "<div style='height:calc(100% - 46px);min-height:560px;border:1px solid var(--border);border-radius:8px;overflow:hidden;background:#111419'>" +
        "<svg viewBox='0 0 1000 620' style='width:100%;height:100%'>" +
          "<path class='edge' d='M235 150 C310 150 310 150 385 150'></path>" +
          "<path class='edge' d='M555 150 C630 150 630 260 705 260'></path>" +
          "<path class='edge cut' d='M555 150 C650 90 700 90 705 100'></path>" +
          graphNode("start", 70, 120, "Start", "Начало") +
          graphNode("condition", 385, 120, "Condition", "Проверить шаг") +
          graphNode("dialogue", 705, 230, "DialogueScene", "Сцена диалога") +
          graphNode("end", 705, 70, "End", "Завершение") +
        "</svg>" +
      "</div>";

    ws.querySelectorAll(".node").forEach(node => {
      node.addEventListener("click", () => select(node.dataset.id));
    });
    select("start");

    function select(id) {
      ws.querySelectorAll(".node").forEach(node => node.classList.toggle("selected", node.dataset.id === id));
      const meta = {
        start: ["Start", "Точка входа в Quest Graph"],
        condition: ["Condition", "Логическое дерево условий"],
        dialogue: ["DialogueScene", "Ссылка на отдельный Scene Graph"],
        end: ["End", "Конечное состояние"]
      }[id] || ["WaitForEvent", "Ожидание события через Event Channel"];

      ins.innerHTML =
        "<div class='badge accent'>" + meta[0] + "</div>" +
        "<h3 style='margin:10px 0 4px'>" + meta[1] + "</h3>" +
        "<div class='fieldGrid'>" +
          "<div class='field full'><label>NodeId</label><input value='" + escapeHtml(id) + "'></div>" +
          "<div class='field'><label>X</label><input value='140'></div>" +
          "<div class='field'><label>Y</label><input value='390'></div>" +
        "</div>" +
        "<div class='notice' style='margin-top:12px'>NodeId является источником истины. Положение — только представление.</div>";

      send({ action: "editor_selection", editor: "graph", nodeId: id });
    }
  }

  function graphNode(id, x, y, type, label) {
    return "<g class='node' data-id='" + id + "' transform='translate(" + x + " " + y + ")'>" +
      "<rect class='nodeRect' rx='8' width='170' height='82'></rect>" +
      "<text class='nodeTitle' x='14' y='27'>" + escapeHtml(type) + "</text>" +
      "<text x='14' y='48' fill='#a6a6a6' font-size='11'>" + escapeHtml(label) + "</text>" +
      "<circle class='socket output' cx='170' cy='41' r='6'></circle>" +
    "</g>";
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
    ws.innerHTML =
      "<div class='notice good'>Проектная структура проходит базовую валидацию.</div>" +
      "<div class='tableLike' style='margin-top:12px'>" +
        validationRow("Quest Graph", "SocketId connections", "OK") +
        validationRow("Scene Graph", "Scene references", "OK") +
        validationRow("World", "WorldPoint references", "OK") +
        validationRow("Data Channels", "Adapter contract", "OK") +
        validationRow("Localization", "Keys", "OK") +
      "</div>";
    ins.innerHTML =
      "<div class='badge'>Validation</div><h3 style='margin:10px 0 4px'>Проверка</h3>" +
      "<div class='notice'>Позже здесь появятся реальные диагностические идентификаторы и severity.</div>";
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

  function formatPosition(position) {
    return "X " + Math.round(position.x) + " · Y " + Math.round(position.y) + " · Z " + Math.round(position.z);
  }

  function escapeHtml(value) {
    return String(value ?? "").replace(/[&<>"']/g, char => ({
      "&": "&amp;",
      "<": "&lt;",
      ">": "&gt;",
      """: "&quot;",
      "'": "&#39;"
    }[char]));
  }

  const webview = window.chrome?.webview;
  webview?.addEventListener("message", event => {
    const data = typeof event.data === "string" ? JSON.parse(event.data) : event.data;

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

  window.addEventListener("hashchange", render);
  render();
})();
