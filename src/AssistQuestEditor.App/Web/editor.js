(() => {
  const root = document.getElementById("root");
  const title = document.getElementById("editorTitle");
  const configs = {
    graph: { title: "Нодовый редактор квестов", draw: renderGraph },
    scene: { title: "Редактор сцен и диалогов", draw: renderScene },
    world: { title: "Редактор мира и координат", draw: renderWorld },
    channels: { title: "Инспектор Data Channels", draw: renderChannels },
    conditions: { title: "Редактор условий и действий", draw: renderConditions },
    localization: { title: "Редактор локализации", draw: renderLocalization },
    validation: { title: "Проверка проекта", draw: renderValidation }
  };

  const send = payload => window.chrome?.webview?.postMessage(payload);

  root.innerHTML = [
    "<div class='layoutThree'>",
      "<section class='panel'><div class='panelTitle'>Редакторы</div><div class='navStack' id='nav'></div></section>",
      "<section class='panel'><div class='panelTitle' id='workspaceTitle'></div><div class='panelBody' id='workspace'></div></section>",
      "<section class='panel'><div class='panelTitle'>Свойства</div><div class='panelBody' id='inspector'></div></section>",
    "</div>"
  ].join("");

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
    ws.innerHTML = [
      "<div class='toolbar' style='margin-bottom:10px'>",
        "<button class='toolButton primary' id='addNode'>Добавить ноду</button>",
        "<button class='toolButton' id='fit'>По размеру</button>",
        "<span class='badge accent'>Quest Graph</span>",
        "<span class='badge'>Normal Flow</span>",
        "<span class='badge red'>Cut Flow</span>",
      "</div>",
      "<div style='height:calc(100% - 46px);min-height:560px;border:1px solid var(--border);border-radius:8px;overflow:hidden;background:#111419'>",
        "<svg id='graphSvg' viewBox='0 0 1000 620'>",
          "<path class='edge' d='M235 150 C310 150 310 150 385 150'></path>",
          "<path class='edge' d='M555 150 C630 150 630 260 705 260'></path>",
          "<path class='edge cut' d='M555 150 C650 90 700 90 705 100'></path>",
          graphNode("start", 70, 120, "Start", "Начало"),
          graphNode("condition", 385, 120, "Condition", "Проверить шаг"),
          graphNode("dialogue", 705, 230, "DialogueScene", "Сцена диалога"),
          graphNode("end", 705, 70, "End", "Завершение"),
        "</svg>",
      "</div>"
    ].join("");

    const svg = ws.querySelector("#graphSvg");
    ws.querySelectorAll(".node").forEach(node => {
      node.addEventListener("click", () => select(node.dataset.id));
      node.addEventListener("pointerdown", startNodeDrag);
    });

    ws.querySelector("#addNode").addEventListener("click", () => {
      const id = "wait-" + Date.now();
      const group = document.createElementNS("http://www.w3.org/2000/svg", "g");
      group.setAttribute("class", "node");
      group.dataset.id = id;
      group.setAttribute("transform", "translate(140 390)");
      group.innerHTML = "<rect class='nodeRect' rx='8' width='175' height='78'></rect><text class='nodeTitle' x='14' y='28'>WaitForEvent</text><text x='14' y='49' fill='#a6a6a6' font-size='11'>Ожидание события</text><circle class='socket output' cx='175' cy='39' r='6'></circle>";
      svg.appendChild(group);
      group.addEventListener("click", () => select(id));
      group.addEventListener("pointerdown", startNodeDrag);
      select(id);
    });

    ws.querySelector("#fit").addEventListener("click", () => svg.setAttribute("viewBox", "0 0 1000 620"));
    select("start");

    function select(id) {
      ws.querySelectorAll(".node").forEach(x => x.classList.toggle("selected", x.dataset.id === id));
      const meta = {
        start: ["Start", "Точка входа в Quest Graph"],
        condition: ["Condition", "Логическое дерево условий"],
        dialogue: ["DialogueScene", "Ссылка на отдельный Scene Graph"],
        end: ["End", "Конечное состояние"]
      }[id] || ["WaitForEvent", "Ожидание события через Event Channel"];

      ins.innerHTML = [
        "<div class='badge accent'>" + meta[0] + "</div>",
        "<h3 style='margin:10px 0 4px'>" + meta[1] + "</h3>",
        "<div class='fieldGrid'>",
          "<div class='field full'><label>NodeId</label><input value='" + id + "'></div>",
          "<div class='field'><label>X</label><input value='140'></div>",
          "<div class='field'><label>Y</label><input value='390'></div>",
        "</div>",
        "<div class='notice' style='margin-top:12px'>NodeId постоянен. Положение на канвасе влияет только на представление.</div>"
      ].join("");
      send({ action: "editor_selection", editor: "graph", nodeId: id });
    }
  }

  function graphNode(id, x, y, type, label) {
    return [
      "<g class='node' data-id='" + id + "' transform='translate(" + x + " " + y + ")'>",
        "<rect class='nodeRect' rx='8' width='170' height='82'></rect>",
        "<text class='nodeTitle' x='14' y='27'>" + type + "</text>",
        "<text x='14' y='48' fill='#a6a6a6' font-size='11'>" + label + "</text>",
        "<circle class='socket output' cx='170' cy='41' r='6'></circle>",
      "</g>"
    ].join("");
  }

  function startNodeDrag(e) {
    if (e.button !== 0) return;
    const node = e.currentTarget;
    const svg = node.ownerSVGElement;
    const start = svgPoint(svg, e.clientX, e.clientY);
    const match = /translate\(([-\d.]+)\s+([-\d.]+)\)/.exec(node.getAttribute("transform") || "");
    const ox = match ? Number(match[1]) : 0;
    const oy = match ? Number(match[2]) : 0;

    const move = event => {
      const point = svgPoint(svg, event.clientX, event.clientY);
      node.setAttribute("transform", "translate(" + (ox + point.x - start.x) + " " + (oy + point.y - start.y) + ")");
    };
    const up = () => {
      window.removeEventListener("pointermove", move);
      window.removeEventListener("pointerup", up);
    };
    e.preventDefault();
    window.addEventListener("pointermove", move);
    window.addEventListener("pointerup", up);
  }

  function svgPoint(svg, x, y) {
    const point = svg.createSVGPoint();
    point.x = x;
    point.y = y;
    return point.matrixTransform(svg.getScreenCTM().inverse());
  }

  function renderScene(ws, ins) {
    ws.innerHTML = [
      "<div class='layoutTwo'>",
        "<section class='panel'><div class='panelTitle'>Scene Graph</div><div class='panelBody'>",
          sceneItem("ruslan_start", "Начало разговора с Русланом", true),
          sceneItem("ruslan_job_offer", "Предложение поручения", false),
          sceneItem("gosha_meat", "Получение мяса", false),
          sceneItem("ruslan_completed", "Завершение", false),
        "</div></section>",
        "<section class='panel'><div class='panelTitle'>Предпросмотр</div><div class='panelBody'>",
          "<div class='notice'>Scene Graph отвечает за подачу сцены. QuestState и Data Channels здесь только читаются.</div>",
          "<div class='card zoomIn' style='margin-top:12px'><div class='miniLabel'>Руслан</div><h3>Есть для тебя особое предложение.</h3><p>Предпросмотр диалогового блока. В будущем сюда добавятся варианты, таймлайн, актёры, звук, камера и VFX.</p><div class='toolbar' style='margin-top:12px'><button class='toolButton'>Показать выбор</button><button class='toolButton primary'>Продолжить</button></div></div>",
        "</div></section>",
      "</div>"
    ].join("");

    ins.innerHTML = [
      "<div class='badge blue'>Dialogue</div>",
      "<div class='fieldGrid' style='margin-top:10px'>",
        "<div class='field full'><label>Идентификатор сцены</label><input value='ruslan_start'></div>",
        "<div class='field full'><label>Говорящий</label><input value='Руслан'></div>",
        "<div class='field full'><label>Текст</label><textarea>Есть для тебя особое предложение.</textarea></div>",
      "</div>"
    ].join("");
  }

  function sceneItem(id, description, active) {
    return "<div class='listItem" + (active ? " active" : "") + "'><strong>" + id + "</strong><span>" + description + "</span></div>";
  }

  function renderWorld(ws, ins) {
    const points = [
      ["ruslan", "Руслан", 180, 80],
      ["gosha", "Гоша", 430, -120],
      ["yard", "Испытательный двор", -140, -60],
      ["village", "Деревня", 30, 260]
    ];

    ws.innerHTML = [
      "<div class='toolbar' style='margin-bottom:10px'><button class='toolButton primary'>Добавить точку</button><button class='toolButton'>Добавить локацию</button><span class='badge accent'>X / Z</span></div>",
      "<div class='layoutTwo' style='height:calc(100% - 48px)'>",
        "<section class='panel'><div class='panelTitle'>Карта мира</div><div class='panelBody' style='padding:0'><div class='mapSurface'><svg viewBox='-300 -300 900 700'>",
          "<path d='M-240 190 L-60 70 L180 120 L420 40 L520 220 L390 360 L90 320 L-130 360 Z' fill='#171b20' stroke='rgba(255,255,255,.12)'/>",
          "<path d='M-180 280 C-40 190 40 220 130 170 S360 80 500 150' fill='none' stroke='#6d7680' stroke-width='5' opacity='.35'/>",
          points.map((p, i) => "<g class='poi " + (i === 0 ? "active" : "") + "' transform='translate(" + p[2] + " " + p[3] + ")'><circle r='8'></circle><text x='12' y='-10'>" + p[1] + "</text></g>").join(""),
        "</svg><div class='mapLegend'>WorldPoint · координаты хранятся отдельно от Quest Graph</div></div></div></section>",
        "<section class='panel'><div class='panelTitle'>Объекты</div><div class='panelBody'>" +
          points.map((p, i) => "<div class='listItem " + (i === 0 ? "active" : "") + "'><strong>" + p[1] + "</strong><span>" + p[0] + " · X " + p[2] + " · Z " + p[3] + "</span></div>").join("") +
        "</div></section>",
      "</div>"
    ].join("");

    ins.innerHTML = [
      "<div class='badge accent'>WorldPoint</div>",
      "<div class='fieldGrid' style='margin-top:10px'>",
        "<div class='field full'><label>Id</label><input value='ruslan'></div>",
        "<div class='field full'><label>Название</label><input value='Руслан'></div>",
        "<div class='field'><label>X</label><input value='180'></div><div class='field'><label>Y</label><input value='0'></div>",
        "<div class='field'><label>Z</label><input value='80'></div><div class='field'><label>Радиус</label><input value='35'></div>",
      "</div>",
      "<div class='notice' style='margin-top:12px'>Для 2D-представления симулятора используется проекция X/Z, при этом полная координата остаётся X/Y/Z.</div>"
    ].join("");
  }

  function renderChannels(ws, ins) {
    const rows = [
      ["player","Игрок","PlayerState"],
      ["world","Мир","WorldState"],
      ["facts","Факты","FactState"],
      ["quest-statuses","Статусы квестов","QuestStatusesState"],
      ["states","Состояния","RuntimeStatesState"],
      ["inventory","Инвентарь","InventoryState"],
      ["reputation","Репутация","ReputationState"],
      ["telemetry","Телеметрия","TelemetryState"],
      ["environment","Окружение","EnvironmentState"],
      ["system","Система","SystemState"]
    ];
    ws.innerHTML = "<table class='table'><thead><tr><th>Ключ</th><th>Канал</th><th>Тип</th><th>Изменение</th></tr></thead><tbody>" +
      rows.map(r => "<tr><td>" + r[0] + "</td><td>" + r[1] + "</td><td>" + r[2] + "</td><td><span class='badge lime'>Разрешено</span></td></tr>").join("") +
      "</tbody></table><div class='notice' style='margin-top:12px'>Источник заменяем: Simulator сейчас пишет сюда, позднее те же контракты будут получать данные ETS2 или DayZ.</div>";
    ins.innerHTML = "<div class='badge blue'>IDataChannelHub</div><h3 style='margin:10px 0'>Граница данных</h3><div class='kv'><span>Каналов</span><span>10</span></div><div class='kv'><span>Event Channel</span><span>активен</span></div><div class='kv'><span>Game-specific логики</span><span>0</span></div>";
  }

  function renderConditions(ws, ins) {
    ws.innerHTML = [
      "<div class='notice'>Условие — дерево выражений. Вложенность AND/OR/NOT не ограничивается специальными ветками runtime.</div>",
      "<div class='card' style='margin-top:12px'>",
        "<span class='badge accent'>AND</span>",
        "<div class='listItem active' style='margin-top:10px'><strong>QuestStepIs</strong><span>special_marinated_shashlik = return_to_ruslan</span></div>",
        "<div class='listItem'><strong>ItemCountCompare</strong><span>special_marinade_meat ≥ 1</span></div>",
        "<div class='listItem'><strong>NOT → FlagEquals</strong><span>ruslan.offerPending = true</span></div>",
        "<div class='toolbar' style='margin-top:10px'><button class='toolButton primary'>Добавить условие</button><button class='toolButton'>AND</button><button class='toolButton'>OR</button><button class='toolButton'>NOT</button></div>",
      "</div>"
    ].join("");
    ins.innerHTML = "<div class='badge accent'>Condition operator</div><div class='fieldGrid' style='margin-top:10px'><div class='field full'><label>Оператор</label><select><option>QuestStepIs</option><option>ItemCountCompare</option><option>FactCompare</option><option>EventOccurred</option></select></div><div class='field'><label>Операнд A</label><input value='QuestStep'></div><div class='field'><label>Сравнение</label><select><option>==</option><option>>=</option><option><=</option></select></div><div class='field full'><label>Операнд B</label><input value='return_to_ruslan'></div></div>";
  }

  function renderLocalization(ws, ins) {
    const rows = [
      ["quest.ruslan.title","Спецмаринад для Руслана","Квест"],
      ["dialogue.ruslan.offer","Есть для тебя особое предложение.","Диалог"],
      ["item.special_marinade_meat","Мясо в спецмаринаде","Предмет"],
      ["notification.quest.completed","Задание выполнено","Уведомление"]
    ];
    ws.innerHTML = "<div class='toolbar' style='margin-bottom:10px'><span class='badge accent'>ru-RU</span><button class='toolButton primary'>Добавить ключ</button><button class='toolButton'>Проверить отсутствующие</button></div><table class='table'><thead><tr><th>Ключ</th><th>Значение</th><th>Контекст</th></tr></thead><tbody>" +
      rows.map(r => "<tr><td>" + r[0] + "</td><td>" + r[1] + "</td><td>" + r[2] + "</td></tr>").join("") +
      "</tbody></table>";
    ins.innerHTML = "<div class='badge accent'>ru-RU</div><div class='fieldGrid' style='margin-top:10px'><div class='field full'><label>Ключ</label><input value='dialogue.ruslan.offer'></div><div class='field full'><label>Значение</label><textarea>Есть для тебя особое предложение.</textarea></div><div class='field full'><label>Будущая локаль</label><input placeholder='en-US'></div></div>";
  }

  function renderValidation(ws, ins) {
    const checks = [
      ["Quest Graph","Connections ссылаются на sockets","lime","OK"],
      ["Scene Graph","Ссылки на сцены разрешаются","lime","OK"],
      ["World","WorldPoint содержит X/Y/Z","lime","OK"],
      ["Conditions","Операторы зарегистрированы","lime","OK"],
      ["Serialization","SchemaVersion подготовлена","lime","OK"],
      ["Localization","ru-RU ресурс присутствует","lime","OK"],
      ["Runtime","Интеграционный сценарий Руслана","accent","Ожидает Runtime"]
    ];
    ws.innerHTML = "<div class='notice'>Валидатор относится к canonical model. Открытое или закрытое окно симулятора не влияет на результат.</div><div style='margin-top:12px'>" +
      checks.map(c => "<div class='listItem'><div class='inline'><strong>" + c[0] + "</strong><span style='margin-left:auto' class='badge " + c[2] + "'>" + c[3] + "</span></div><span>" + c[1] + "</span></div>").join("") +
      "</div>";
    ins.innerHTML = "<div class='badge lime'>0 ошибок</div><h3 style='margin:10px 0'>Диагностика</h3><div class='kv'><span>Ошибок</span><span>0</span></div><div class='kv'><span>Предупреждений</span><span>0</span></div><div class='kv'><span>Ожидает Runtime</span><span>1</span></div>";
  }

  render();
})();
