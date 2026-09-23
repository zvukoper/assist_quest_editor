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
    locations: [],
    worldPoints: []
  };
  let testResult = null;
  let simulatorContext = { selection: { point: null }, player: null };
  // Текст поиска точки держится в модуле: перерисовка панели создаёт поле
  // заново, и без этого запрос пользователя терялся бы после каждого выбора.
  let pointSearch = "";
  // Количество раундов тоже держится в модуле. Значение было жёстко прописано
  // в разметке как value='8', поэтому после каждого теста панель перерисовывалась
  // и введённое число сбрасывалось обратно к 8.
  let testRounds = 8;
  // Пульс плашки «Подходящих кандидатов нет» показывается ОДИН раз на новый
  // результат теста. Панель перерисовывается и на посторонние сообщения
  // (simulator_context, состояние документа), а CSS-анимация перезапускается
  // при каждом появлении класса — без этого флага плашка мигала бы постоянно.
  let noCandidatesPulsePending = false;

  // Текст диагностики, по которому узнаём пустой результат. Строка совпадает с
  // формулировкой домена (LocationResolver.Test).
  const NO_CANDIDATES_MARKER = "Подходящих кандидатов нет";

  function hasNoCandidatesDiagnostic(result) {
    const diagnostics = result?.diagnostics || [];
    return diagnostics.some(message => String(message).includes(NO_CANDIDATES_MARKER));
  }

  const CRITERIA = [
    ["CategoryIs", "Категория равна"],
    ["CategoryContains", "Категория содержит"],
    ["NameContains", "Название содержит"],
    ["WithinDistanceOfPoint", "В радиусе точки"],
    ["FartherThanPoint", "Дальше от точки"],
    // Радиус от игрока: принимает число (минимум) или диапазон «100-1000».
    ["DistanceFromPlayer", "Радиус от игрока"],
    // Рядом с дорогой: кандидат не дальше указанного расстояния от любой дороги.
    ["NearbyRoad", "Рядом с дорогой"],
    // Радиус от перекрёстка: принимает число (минимум) или диапазон «100-500».
    // Перекрёстки — отдельный предпосчитанный слой (нодировка дорожной сети),
    // поэтому поиск идёт по нему, а не по категории точки.
    ["NearbyJunction", "В радиусе от перекрёстка"],
    // Черта города — нарисованная автором область, а не признак точки.
    // Два критерия: «в любом городе» отделяет город от сельской местности
    // глобально, «в черте города X» привязывает к одному городу. Галочка «Нет»
    // даёт пригороды — то, ради чего они и заведены.
    ["InAnyCity", "В любом городе"],
    ["InCityBoundary", "В черте города"],
    ["ExcludeCategory", "Исключить категорию"],
    // Соседство по категории. Два разных критерия, а не «НЕ» над первым:
    // «НЕ (есть сосед)» и «(нет соседей)» — разные условия, когда соседей несколько.
    ["NearbyCategory", "Рядом есть категория"],
    ["NoNearbyCategory", "В радиусе нет категории"],
    // Это не фильтр кандидатов, а стратегия отбора: раунды расходятся по площади.
    ["MinDistanceBetweenCandidates", "Минимальная дистанция между точками"],
    ["ProviderCriterion", "Критерий World Provider"]
  ];

  const HISTORY_FIELDS = [
    ["minVisitCount", "Минимум посещений"],
    ["maxVisitCount", "Максимум посещений"],
    ["maxSelectionCount", "Максимум выборов"],
    ["minGameHoursSinceLastVisit", "Игровых часов с последнего посещения"],
    ["minRealHoursSinceLastVisit", "Реальных часов с последнего посещения"],
    ["minGameHoursSinceLastSelection", "Игровых часов с последнего выбора"],
    ["minRealHoursSinceLastSelection", "Реальных часов с последнего выбора"]
  ];

  function escapeHtml(value) {
    return String(value ?? "").replace(/[&<>"']/g, char => ({
      "&": "&amp;",
      "<": "&lt;",
      ">": "&gt;",
      "\"": "&quot;",
      "'": "&#39;"
    }[char]));
  }

  function formatPosition(position) {
    if (!position) return "—";
    return "X " + Math.round(Number(position.x) || 0) +
      " · Y " + Math.round(Number(position.y) || 0) +
      " · Z " + Math.round(Number(position.z) || 0);
  }

  function referenceId(entry) {
    return String(entry?.id ?? "");
  }

  function referenceName(entry) {
    return String(entry?.name ?? entry?.title ?? referenceId(entry));
  }

  function findWorldPoint(id) {
    return state.worldPoints.find(point =>
      referenceId(point).toLowerCase() === String(id || "").toLowerCase()) || null;
  }

  /**
   * Категории мира — уникальные, отсортированные.
   *
   * В мире их около семидесяти, поэтому <datalist> здесь работает хорошо.
   * Подсказки обязательны: вручную введённое имя категории почти всегда
   * расходится с реальным написанием (регистр, «_» против пробела), и критерий
   * молча не находит ничего — именно так и выглядел симптом «поле не ищет».
   */
  function worldCategories() {
    return [...new Set(
      (state.worldPoints || []).map(point => String(point.category || "").trim()).filter(Boolean)
    )].sort((a, b) => a.localeCompare(b, "ru"));
  }

  /** Имена точек мира — уникальные, отсортированные. Для критерия по названию. */
  function worldNames() {
    return [...new Set(
      (state.worldPoints || []).map(point => String(referenceName(point) || "").trim()).filter(Boolean)
    )].sort((a, b) => a.localeCompare(b, "ru"));
  }

  /**
   * Города мира — уникальные, отсортированные. Для критерия «в черте города X».
   *
   * Берутся ГОРОДСКИЕ точки (признак isCity), а не отдельный список: черта
   * определяется городом, который оказался внутри контура, и он же — городская
   * точка мира. Другой источник дал бы подсказки, которых нет среди черт, и автор
   * выбирал бы город, для которого черта никогда не найдётся.
   */
  function worldCities() {
    return [...new Set(
      (state.worldPoints || [])
        .filter(point => point.isCity)
        .map(point => String(referenceName(point) || "").trim())
        .filter(Boolean)
    )].sort((a, b) => a.localeCompare(b, "ru"));
  }

  /**
   * Поле со списком подсказок.
   *
   * Значения попадают в <datalist>, поэтому браузер фильтрует их по мере ввода.
   * Подпись под полем говорит, откуда список: автор иначе не поймёт, почему в нём
   * только то, что есть в текущем мире.
   */
  function suggestionFieldHtml(listId, values, value, placeholder, hint) {
    return "<input class='toolButton' list='" + listId + "' data-criterion-parameter='value' " +
        "style='min-width:200px' value='" + escapeHtml(value || "") + "' placeholder='" +
        escapeHtml(placeholder) + "' title='" + escapeHtml(hint) + "'>" +
      "<datalist id='" + listId + "'>" +
        values.map(item => "<option value='" + escapeHtml(item) + "'></option>").join("") +
      "</datalist>";
  }

  function clone(value) {
    return JSON.parse(JSON.stringify(value));
  }

  function blankDefinition() {
    return {
      id: "location_" + Math.random().toString(36).slice(2, 10),
      name: "Новая локация",
      description: "",
      mode: "Fixed",
      worldPointId: "",
      triggerRadius: 35,
      query: {
        criteria: [],
        history: {}
      }
    };
  }

  function ensureDefinition() {
    if (!state.definition) state.definition = blankDefinition();
    state.definition.query ||= {};
    state.definition.query.criteria ||= [];
    state.definition.query.history ||= {};
    return state.definition;
  }

  /**
   * Перерисовка активной панели изнутри модуля.
   *
   * render() принимает рабочую область и инспектор аргументами, потому что его
   * вызывает editor.js. Внутренние обработчики обязаны идти через этот помощник:
   * вызов render() без аргументов давал TypeError на ws.innerHTML, панель не
   * обновлялась, и «Взять выбранную из Simulator» выглядела как «ничего не
   * произошло» (значение уходило в Host, но на экране не менялось).
   */
  function rerender() {
    const ws = document.getElementById("workspace");
    const ins = document.getElementById("inspector");
    if (!ws || !ins) return;
    render(ws, ins);
  }

  function markDirty() {
    state.documentDirty = true;
    send({
      action: "location_mark_dirty",
      definition: state.definition
    });
    rerender();
  }

  function valueNumber(value) {
    const n = Number(value);
    return Number.isFinite(n) ? n : null;
  }

  function collectLocation() {
    const form = document.getElementById("locationForm");
    const current = ensureDefinition();
    const criteria = [];

    form?.querySelectorAll("[data-criterion]").forEach(row => {
      const type = row.querySelector("[data-criterion-type]")?.value?.trim();
      if (!type) return;

      const negate = !!row.querySelector("[data-criterion-negate]")?.checked;
      const parameters = {};

      if (type === "ProviderCriterion") {
        const providerType = row.querySelector("[data-provider-type]")?.value?.trim();
        if (!providerType) return;
        parameters.type = providerType;
        const raw = row.querySelector("[data-provider-parameters]")?.value?.trim();
        if (raw) {
          try {
            const parsed = JSON.parse(raw);
            Object.assign(parameters, parsed);
          } catch {
            parameters.raw = raw;
          }
        }
      } else {
        row.querySelectorAll("[data-criterion-parameter]").forEach(input => {
          const key = input.dataset.criterionParameter;
          let value = input.value.trim();
          if (key === "pointId") {
            const match = state.worldPoints.find(point =>
              referenceId(point).toLowerCase() === value.toLowerCase() ||
              referenceName(point).toLowerCase() === value.toLowerCase());
            if (match) value = referenceId(match);
          }
          if (key && value) parameters[key] = value;
        });
      }

      criteria.push({ type, parameters, negate });
    });

    const history = {};
    form?.querySelectorAll("[data-history-field]").forEach(input => {
      const key = input.dataset.historyField;
      const value = input.value.trim();
      if (!key || !value) return;
      const number = valueNumber(value);
      if (number !== null) history[key] = number;
    });

    const definition = {
      id: form?.querySelector("[data-location-field='id']")?.value?.trim() || current.id,
      name: form?.querySelector("[data-location-field='name']")?.value?.trim() || "Без названия",
      description: form?.querySelector("[data-location-field='description']")?.value || "",
      mode: form?.querySelector("[data-location-field='mode']")?.value || "Fixed",
      // Скрытое поле хранит уже канонический ID: видимое поле — только поиск,
      // поэтому переводить имя в ID здесь больше не требуется.
      worldPointId: form?.querySelector("[data-location-field='worldPointId']")?.value?.trim() || "",
      triggerRadius: valueNumber(form?.querySelector("[data-location-field='radius']")?.value) ?? 35,
      query: { criteria, history }
    };

    return definition;
  }

  function criterionParameterHtml(type, parameters, index) {
    const p = parameters || {};

    // Категории и названия предлагаются списком из мира.
    //
    // Раньше здесь было голое поле с placeholder='Значение': ни поиска, ни
    // подсказок, поэтому категорию приходилось вводить по памяти, и любая
    // опечатка или другой регистр молча давали «кандидатов нет».
    if (type === "CategoryIs" || type === "CategoryContains" || type === "ExcludeCategory") {
      return suggestionFieldHtml(
        "location-category-value-" + index,
        worldCategories(),
        p.value,
        "Начните вводить категорию",
        "Категории текущего мира");
    }

    if (type === "NameContains") {
      return suggestionFieldHtml(
        "location-name-value-" + index,
        worldNames(),
        p.value,
        "Начните вводить название",
        "Названия точек текущего мира");
    }

    if (type === "WithinDistanceOfPoint" || type === "FartherThanPoint") {
      const listId = "location-point-options-" + index;
      return "<input class='toolButton' data-criterion-parameter='pointId' list='" + listId +
        "' value='" + escapeHtml(pointDisplayValue(p.pointId)) + "' placeholder='Точка'>" +
        "<datalist id='" + listId + "'>" +
          state.worldPoints.map(point =>
            "<option value='" + escapeHtml(referenceName(point)) + "' label='" +
            escapeHtml(referenceId(point) + " · " + (point.category || "")) + "'></option>" +
            (referenceName(point).toLowerCase() === referenceId(point).toLowerCase() ? "" :
              "<option value='" + escapeHtml(referenceId(point)) + "' label='" +
              escapeHtml(referenceName(point)) + "'></option>")
          ).join("") +
        "</datalist>" +
        // Захват выбранной точки нужен и здесь: точка в критерии — та же точка
        // мира, что и в Fixed Location, и заставлять автора уходить в другой
        // режим и вручную искать имя в списке из тысяч точек незачем.
        "<button class='toolButton' type='button' data-criterion-capture-point='pointId' " +
          (capturableSelection() ? "" : "disabled") +
          " title='Подставить точку, выбранную на карте Симулятора'>Взять выбранную из Simulator</button>" +
        "<input class='toolButton' style='width:110px' type='number' min='0' data-criterion-parameter='meters' value='" +
        escapeHtml(p.meters || "") + "' placeholder='метров'>";
    }

    // Соседство по категории: категория-сосед и радиус, в котором её искать.
    // Список категорий тот же, что и у «Категория равна» — берётся из мира.
    if (type === "NearbyCategory" || type === "NoNearbyCategory") {
      const listId = "location-category-options-" + index;
      return "<input class='toolButton' list='" + listId + "' style='min-width:220px' " +
          "data-criterion-parameter='value' value='" + escapeHtml(p.value || "") +
          "' placeholder='Начните вводить категорию' title='Категории текущего мира'>" +
        "<datalist id='" + listId + "'>" +
          worldCategories().map(category =>
            "<option value='" + escapeHtml(category) + "'></option>").join("") +
        "</datalist>" +
        "<input class='toolButton' style='width:110px' type='number' min='1' " +
          "data-criterion-parameter='meters' value='" + escapeHtml(p.meters || "500") +
          "' placeholder='метров' title='Радиус поиска соседей'>";
    }

    // Радиус от игрока.
    //
    // Поле текстовое, а не number: значение может быть диапазоном «100-1000», и
    // числовое поле с дефисом не справилось бы (оно молча обрежет ввод до
    // пустой строки или до первого числа).
    if (type === "DistanceFromPlayer") {
      return "<input class='toolButton' style='width:130px' data-criterion-parameter='meters' value='" +
        escapeHtml(p.meters || "") + "' placeholder='100-1000 или 150' " +
        "title='Диапазон от игрока: одно число — минимальная дистанция, два числа через дефис — от и до'>" +
        "<span class='miniLabel' style='align-self:center'>от игрока, м</span>";
    }

    // Рядом с дорогой: только расстояние до ближайшей дороги.
    //
    // Дорог в мире ~98 000, поэтому это ОТДЕЛЬНЫЙ слой, а не категория точки:
    // в списке точек они весили бы ~19 МБ в каждом снимке карты. Критерий
    // проверяет расстояние до ближайшего отрезка, а не соседство по категории.
    if (type === "NearbyRoad") {
      return "<input class='toolButton' style='width:130px' type='number' min='1' " +
        "data-criterion-parameter='meters' value='" + escapeHtml(p.meters || "100") +
        "' placeholder='метров' title='Максимальное расстояние от точки до дороги'>" +
        "<span class='miniLabel' style='align-self:center'>до дороги, м</span>";
    }

    // В радиусе от перекрёстка: диапазон, а не только максимум.
    //
    // Текстовое поле, а не type=number: «100-500» в числовом поле браузер молча
    // портит (дефис не цифра, ввод обрезается). Здесь допустимы оба вида, как у
    // «Радиуса от игрока»; разбирает их домен.
    if (type === "NearbyJunction") {
      return "<input class='toolButton' style='width:130px' " +
        "data-criterion-parameter='meters' value='" + escapeHtml(p.meters || "100-500") +
        "' placeholder='100-500 или 300' " +
        "title='Диапазон от перекрёстка: одно число — минимальная дистанция, два числа через дефис — от и до'>" +
        "<span class='miniLabel' style='align-self:center'>от перекрёстка, м</span>";
    }

    // «В любом городе» параметров не имеет — важен сам факт попадания внутрь
    // любой нарисованной черты. Подсказка объясняет, откуда берутся черты.
    if (type === "InAnyCity") {
      return "<span class='miniLabel' style='align-self:center'>" +
        "внутри любой нарисованной черты города</span>";
    }

    // «В черте города X»: город выбирается из СПИСКА городов мира.
    //
    // Список, а не голое поле: критерий сравнивает значение с именем или
    // идентификатором нарисованной черты, и опечатка молча давала бы «у города
    // нет черты». Источник подсказок — тот же список городов, по которому
    // работают черты (файл городов мира).
    if (type === "InCityBoundary") {
      const listId = "location-city-options-" + index;
      return "<input class='toolButton' list='" + listId + "' style='min-width:180px' " +
          "data-criterion-parameter='city' value='" + escapeHtml(p.city || "") +
          "' placeholder='Начните вводить город' title='Города текущего мира'>" +
        "<datalist id='" + listId + "'>" +
          worldCities().map(city =>
            "<option value='" + escapeHtml(city) + "'></option>").join("") +
        "</datalist>" +
        "<span class='miniLabel' style='align-self:center'>черта города</span>";
    }

    // Минимальная дистанция — только расстояние: это разброс раундов, а не фильтр.
    if (type === "MinDistanceBetweenCandidates") {
      return "<input class='toolButton' style='width:130px' type='number' min='1' " +
        "data-criterion-parameter='meters' value='" + escapeHtml(p.meters || "5000") +
        "' placeholder='метров' title='Минимальное расстояние между выбранными точками'>" +
        "<span class='miniLabel' style='align-self:center'>между выбранными точками</span>";
    }

    if (type === "ProviderCriterion") {
      return "<input class='toolButton' data-provider-type value='" + escapeHtml(p.type || "") +
        "' placeholder='например: HouseTypeIs'>" +
        "<input class='toolButton' style='min-width:220px' data-provider-parameters value='" +
        escapeHtml(JSON.stringify(stripProviderType(p))) +
        "' placeholder='JSON параметров, например {&quot;types&quot;:[5,6]}'>";
    }

    return "";
  }

  function stripProviderType(parameters) {
    const copy = { ...(parameters || {}) };
    delete copy.type;
    return copy;
  }

  function pointDisplayValue(id) {
    if (!id) return "";
    const point = findWorldPoint(id);
    return point ? referenceName(point) : id;
  }

  /** Поиск точки по ID ИЛИ по имени: findWorldPoint умеет только ID. */
  function findAnyWorldPoint(value) {
    const raw = String(value || "").trim().toLowerCase();
    if (!raw) return null;
    return state.worldPoints.find(point =>
      referenceId(point).toLowerCase() === raw ||
      referenceName(point).toLowerCase() === raw) || null;
  }

  /** Временная точка живёт только в выборе Simulator; её ID имеет префикс. */
  function isTemporaryPoint(point) {
    return String(point?.id || "").toLowerCase().startsWith("temporary:");
  }

  /**
   * Значение для поля точки, взятое из выбора Simulator.
   *
   * Поле показывает ИМЯ, а collectLocation переводит имя обратно в ID по списку
   * мира. Временной точки в списке мира нет, поэтому её приходится показывать
   * сырым ID: иначе несколько временных точек выглядели бы одинаково как
   * «Временная точка», и в файл ушло бы имя вместо идентификатора.
   */
  function pointCaptureValue(point) {
    return findWorldPoint(referenceId(point)) ? referenceName(point) : referenceId(point);
  }

  /**
   * Выбор Simulator, пригодный для захвата.
   *
   * Город не подходит: это справочная точка карты, а не СДО для квеста. Всё
   * остальное — включая временную точку — захватывается: пользователь ожидает,
   * что кнопка «Взять выбранную» заберёт и её.
   */
  function capturableSelection() {
    const point = simulatorContext.selection?.point || null;
    return point && !point.isCity ? point : null;
  }

  const POINT_RESULT_LIMIT = 40;

  function pointMatches(point, query) {
    const q = String(query || "").trim().toLowerCase();
    if (!q) return true;
    return referenceName(point).toLowerCase().includes(q) ||
      referenceId(point).toLowerCase().includes(q);
  }

  function pointCandidates() {
    return (state.worldPoints || []).filter(point => !point.isCity);
  }

  /**
   * Список совпадений поиска точки.
   *
   * Явный список вместо <datalist>: у 5000+ пунктов браузерный datalist даёт
   * непредсказуемый поиск, а здесь видно и имя, и ID одновременно.
   *
   * Пока запрос пуст, списка НЕТ вообще. Раньше пустой запрос считался «совпадает
   * всё», и панель сразу вываливала первые 40 произвольных точек мира — это
   * выглядело как мусор, не имеющий отношения к тому, что автор собирается искать.
   * Список формируется только когда начинается поиск по вводу.
   */
  function searchResultsHtml() {
    const query = String(pointSearch || "").trim();

    if (!query) {
      return "<div class='notice'>Начните вводить имя или ID точки — " +
        "список появится по мере ввода.</div>";
    }

    const matches = pointCandidates().filter(point => pointMatches(point, pointSearch));
    const selectedId = String(state.definition?.worldPointId || "").toLowerCase();

    if (!matches.length) {
      // Та же красная плашка, что и у пустого теста: «ничего не найдено» —
      // это отказ, и он должен быть заметен, а не сливаться с подсказками.
      return "<div class='notice noticePulseAlert'>Ничего не найдено по «" +
        escapeHtml(pointSearch) + "». Поиск идёт по имени и по ID.</div>";
    }

    const shown = matches.slice(0, POINT_RESULT_LIMIT);
    const rows = shown.map(point => {
      const selected = referenceId(point).toLowerCase() === selectedId;
      return "<button type='button' class='toolButton' data-point-result='" +
        escapeHtml(referenceId(point)) + "' style=\"display:block;width:100%;text-align:left;margin-top:4px" +
        (selected ? ";border-color:var(--accent)" : "") + "\">" +
        escapeHtml(referenceName(point)) +
        "<span class='miniLabel' style='display:block;margin-top:2px'>" +
          escapeHtml(referenceId(point) + " · " + (point.category || "")) +
        "</span></button>";
    }).join("");

    return rows +
      (matches.length > shown.length
        ? "<div class='notice' style='margin-top:6px'>Показаны первые " + shown.length +
          " из " + matches.length + ". Уточните запрос.</div>"
        : "");
  }

  function wirePointResults() {
    document.querySelectorAll("[data-point-result]").forEach(button => {
      button.addEventListener("click", () => {
        state.definition = ensureDefinition();
        state.definition.mode = "Fixed";
        state.definition.worldPointId = button.dataset.pointResult;
        pointSearch = "";
        markDirty();
      });
    });
  }

  function criterionRowHtml(criterion, index) {
    const type = criterion?.type || "CategoryIs";
    // «НЕ» есть не у каждого критерия: минимальная дистанция — это стратегия
    // отбора раундов, а не условие на точку, и инвертировать её бессмысленно.
    // Показанный чекбокс, который ни на что не влияет, — обман пользователя.
    const negatable = type !== "MinDistanceBetweenCandidates";
    return "<div class='card' data-criterion data-index='" + index + "' style='margin-top:8px;padding:10px'>" +
      "<div class='toolbar' style='gap:6px;flex-wrap:wrap'>" +
        "<select class='toolButton' data-criterion-type style='min-width:210px'>" +
          CRITERIA.map(([value, label]) =>
            "<option value='" + value + "'" + (value === type ? " selected" : "") + ">" +
              escapeHtml(label) + "</option>"
          ).join("") +
        "</select>" +
        (negatable
          ? "<label class='miniLabel' style='display:flex;align-items:center;gap:5px'>" +
              "<input type='checkbox' data-criterion-negate " + (criterion?.negate ? "checked" : "") + "> НЕ" +
            "</label>"
          : "") +
        "<button class='toolButton' type='button' data-remove-criterion>Удалить</button>" +
      "</div>" +
      "<div class='toolbar' style='margin-top:7px;gap:6px;flex-wrap:wrap'>" +
        criterionParameterHtml(type, criterion?.parameters, index) +
      "</div>" +
      (type === "ProviderCriterion"
        ? "<div class='miniLabel' style='margin-top:6px'>Сохраняется в Location даже без поддержки Sandbox. Реальный World Provider сможет зарегистрировать собственный evaluator.</div>"
        : "") +
    "</div>";
  }

  function historyHtml(history) {
    return "<div class='card' style='margin-top:12px'>" +
      "<div class='miniLabel'>История использования кандидатов</div>" +
      "<div class='notice' style='margin-top:6px'>Сейчас значения сохраняются в Location Query. Само накопление выборов/посещений подключим к Runtime/Data Channel, не к файлу Location.</div>" +
      "<div class='fieldGrid' style='margin-top:8px'>" +
        HISTORY_FIELDS.map(([key, label]) =>
          "<div class='field'><label>" + escapeHtml(label) + "</label>" +
          "<input type='number' min='0' step='0.1' data-history-field='" + key + "' value='" +
          escapeHtml(history?.[key] ?? "") + "'></div>"
        ).join("") +
      "</div>" +
    "</div>";
  }

  /**
   * Набор, который уже показан на тестовой карте.
   *
   * «Показать в симуляторе» показывает ИМЕННО ЕГО, а не результат нового
   * поиска: тест каждый раз заново выбирает точки, поэтому повторный прогон дал
   * бы другой набор, и автор сравнивал бы не то, что проверял.
   */
  function displayedCandidates() {
    return Array.isArray(testResult?.candidates) ? testResult.candidates : [];
  }

  /**
   * Набор для показа на основной карте.
   *
   * Для Dynamic Location это результат теста, для Fixed — единственная
   * выбранная точка. Один способ нужен обоим режимам: пользователь ожидает,
   * что кнопка «Показать в симуляторе» работает одинаково.
   */
  function visualisationEntries() {
    const definition = state.definition || {};

    if (definition.mode === "Fixed") {
      const id = String(definition.worldPointId || "").trim();
      return id ? [{ pointId: id }] : [];
    }

    return displayedCandidates().map(candidate => ({ pointId: candidate.candidateId }));
  }

  function visualisationReady() {
    return visualisationEntries().length > 0;
  }

  function visualisationDisabledReason() {
    return state.definition?.mode === "Fixed"
      ? "Сначала выберите точку: показывать пока нечего"
      : "Сначала нажмите «Тест»: показывать пока нечего";
  }

  function renderMap(result) {
    const svg = document.getElementById("locationMap");
    if (!svg) return;

    const rawCandidates = Array.isArray(result?.candidates) ? result.candidates : [];
    const current = ensureDefinition();
    const fixed = current.mode === "Fixed" ? findWorldPoint(current.worldPointId) : null;
    const candidates = rawCandidates.length
      ? rawCandidates
      : (fixed ? [{
          candidateId: fixed.id,
          name: fixed.name,
          category: fixed.category,
          position: fixed.position,
          selected: true,
          message: "Фиксированная точка."
        }] : []);
    const points = candidates.map(item => item.position).filter(Boolean);

    if (!points.length) {
      // fill, а не class='miniLabel': в SVG текст рисуется через fill, и класс
      // с CSS color оставлял подпись чёрной на тёмной карте — нечитаемой.
      svg.innerHTML =
        "<text x='50%' y='50%' text-anchor='middle' fill='var(--accent)' " +
        "font-size='12' font-weight='800'>Нет точек для отображения</text>";
      return;
    }

    const xs = points.map(p => Number(p.x) || 0);
    const zs = points.map(p => Number(p.z) || 0);
    let minX = Math.min(...xs), maxX = Math.max(...xs);
    let minZ = Math.min(...zs), maxZ = Math.max(...zs);
    const spanX = Math.max(1, maxX - minX);
    const spanZ = Math.max(1, maxZ - minZ);
    minX -= Math.max(100, spanX * 0.15);
    maxX += Math.max(100, spanX * 0.15);
    minZ -= Math.max(100, spanZ * 0.15);
    maxZ += Math.max(100, spanZ * 0.15);

    const width = 900, height = 520;
    const project = position => ({
      x: (Number(position.x) - minX) / Math.max(1, maxX - minX) * width,
      y: (maxZ - Number(position.z)) / Math.max(1, maxZ - minZ) * height
    });

    const selectedIds = new Set(candidates.map(item => item.candidateId));
    const circles = candidates.map((candidate, index) => {
      const p = project(candidate.position);
      const repeated = candidates.filter(item => item.candidateId === candidate.candidateId).length > 1;
      return "<g>" +
        "<circle cx='" + p.x.toFixed(2) + "' cy='" + p.y.toFixed(2) + "' r='9' fill='var(--accent)' stroke='white' stroke-width='2' />" +
        "<text x='" + p.x.toFixed(2) + "' y='" + (p.y + 4).toFixed(2) +
          "' text-anchor='middle' fill='#111419' font-size='10' font-weight='700'>" + (index + 1) + "</text>" +
        "<title>" + escapeHtml((index + 1) + ". " + candidate.name + " · " + candidate.candidateId +
          (repeated ? " · повтор" : "")) + "</title>" +
      "</g>";
    }).join("");

    const player = simulatorContext.player?.position;
    const playerSvg = player ? (() => {
      const p = project(player);
      return "<path d='M " + p.x + " " + (p.y - 12) + " L " + (p.x - 9) + " " + (p.y + 9) +
        " L " + (p.x + 9) + " " + (p.y + 9) + " Z' fill='var(--red)' stroke='white' stroke-width='2'><title>Игрок</title></path>";
    })() : "";

    const selectedHint = selectedIds.size
      ? "<div class='miniLabel' style='position:absolute;left:10px;top:10px'>" + selectedIds.size +
        " уникальных кандидатов / " + candidates.length + " выборов</div>"
      : "";

    svg.setAttribute("viewBox", "0 0 " + width + " " + height);
    svg.innerHTML =
      "<rect x='0' y='0' width='" + width + "' height='" + height + "' fill='#111419'></rect>" +
      "<path d='M0 260 H900 M450 0 V520' stroke='var(--border)' stroke-width='1' opacity='.45'></path>" +
      "<g opacity='.9'>" + circles + "</g>" +
      playerSvg;
  }

  function render(ws, ins) {
    const definition = ensureDefinition();
    const readonly = state.readOnly;
    const currentPoint = findWorldPoint(definition.worldPointId);
    const selection = simulatorContext.selection?.point || null;
    const capturable = capturableSelection();
    const dynamic = definition.mode === "Dynamic";

    ws.innerHTML =
      "<div class='toolbar' style='margin-bottom:10px;flex-wrap:wrap'>" +
        "<button class='toolButton' id='newLocation'>Новая</button>" +
        "<button class='toolButton' id='openLocation'>Открыть</button>" +
        "<button class='toolButton primary' id='saveLocation' " + (readonly ? "disabled" : "") + ">Сохранить</button>" +
        "<button class='toolButton' id='saveLocationAs' " + (readonly ? "disabled" : "") + ">Сохранить как…</button>" +
        "<button class='toolButton' id='deleteLocation' " + (readonly ? "disabled" : "") + ">Удалить</button>" +
        "<span class='badge " + (state.documentDirty ? "accent" : "blue") + "'>" +
          (state.documentDirty ? "Не сохранено" : "Сохранено") + "</span>" +
        "<span class='badge'>" + escapeHtml(state.documentPath || "Новая Location") + "</span>" +
        "<span class='badge accent'>" + escapeHtml(definition.mode) + "</span>" +
      "</div>" +

      "<div id='locationForm'>" +
        "<div class='card'>" +
          "<div class='fieldGrid'>" +
            "<div class='field'><label>ID</label><input class='toolButton' data-location-field='id' value='" + escapeHtml(definition.id) + "'></div>" +
            "<div class='field'><label>Название</label><input class='toolButton' data-location-field='name' value='" + escapeHtml(definition.name) + "'></div>" +
          "</div>" +
          "<div class='field' style='margin-top:8px'><label>Описание</label><textarea class='toolButton' data-location-field='description' style='min-height:70px;resize:vertical'>" +
            escapeHtml(definition.description) + "</textarea></div>" +
          "<div class='toolbar' style='margin-top:8px;gap:8px;flex-wrap:wrap'>" +
            "<label class='miniLabel' style='display:flex;align-items:center;gap:6px'>Тип" +
              "<select class='toolButton' data-location-field='mode'>" +
                "<option value='Fixed' " + (definition.mode === "Fixed" ? "selected" : "") + ">Fixed · фиксированная</option>" +
                "<option value='Dynamic' " + (dynamic ? "selected" : "") + ">Dynamic · динамический поиск</option>" +
              "</select>" +
            "</label>" +
            "<label class='miniLabel' style='display:flex;align-items:center;gap:6px'>Радиус срабатывания" +
              "<input class='toolButton' type='number' min='0' data-location-field='radius' style='width:100px' value='" +
                escapeHtml(definition.triggerRadius ?? 35) + "'> м</label>" +
          "</div>" +
          "<div class='notice' style='margin-top:8px'>" +
            "Радиус — расстояние в метрах, на котором игрок считается находящимся «в точке». " +
            "Он применяется в Runtime вместо triggerRadius ноды: у ноды это значение лишь " +
            "значение по умолчанию, а авторский радиус живёт здесь." +
          "</div>" +
        "</div>" +

        (dynamic
          ? "<div class='card' style='margin-top:12px'><div class='miniLabel'>Критерии поиска</div>" +
              "<div class='notice' style='margin-top:6px'>Критерии применяются как AND. «НЕ» инвертирует отдельный критерий. «В радиусе нет категории» и «Рядом есть категория» — разные условия: второе ищет хотя бы одного соседа, первое требует, чтобы соседей не было вовсе. «Минимальная дистанция» не фильтрует точки, а разводит раунды по площади. Для будущего ETS2/DayZ можно хранить provider-specific критерии без изменения Location формата.</div>" +
              "<div id='locationCriteria'>" +
                (definition.query?.criteria?.length
                  ? definition.query.criteria.map(criterionRowHtml).join("")
                  : "<div class='notice' style='margin-top:8px'>Критериев пока нет. Без критериев Sandbox выбирает произвольную точку из WorldState.</div>") +
              "</div>" +
              "<button class='toolButton primary' id='addCriterion' style='margin-top:8px'>Добавить критерий</button>" +
            "</div>"
          : "<div class='card' style='margin-top:12px'>" +
              "<div class='miniLabel'>Фиксированная точка</div>" +
              "<div class='toolbar' style='margin-top:7px;gap:6px;flex-wrap:wrap'>" +
                "<input class='toolButton' id='locationPointSearch' style='flex:1;min-width:260px' value='" +
                  escapeHtml(pointSearch) + "' placeholder='Поиск по имени или ID'>" +
                "<button class='toolButton' id='useSelectedSimulatorPoint' " +
                  (capturable ? "" : "disabled") + ">Взять выбранную из Simulator</button>" +
              "</div>" +
              // Канонический ID хранится в скрытом поле: видимое поле — только поиск.
              "<input type='hidden' data-location-field='worldPointId' value='" +
                escapeHtml(definition.worldPointId || "") + "'>" +
              "<div id='locationPointResults' style='margin-top:7px'>" + searchResultsHtml() + "</div>" +
              "<div class='notice' style='margin-top:7px'>" +
                (currentPoint
                  ? (isTemporaryPoint(currentPoint) ? "Временная точка Simulator: " : "Точка: ") +
                    "<strong>" + escapeHtml(currentPoint.name) + "</strong> · " +
                    formatPosition(currentPoint.position)
                  : (selection
                      ? "В Simulator выбрана точка «" + escapeHtml(selection.name || selection.id) +
                        "». Нажмите «Взять выбранную», чтобы подставить её."
                      : "WorldPoint ещё не выбран — найдите его выше.")) +
              "</div>" +
              "<div class='notice' style='margin-top:6px'>" +
                "«Взять выбранную из Simulator» подставляет точку, выбранную на карте Симулятора, " +
                "включая временную точку, созданную кликом по пустому месту." +
              "</div>" +
            "</div>") +

        (dynamic ? historyHtml(definition.query?.history) : "") +

        (dynamic
          ? "<div class='card' style='margin-top:12px'><div class='miniLabel'>Проверка результата</div>" +
              "<div class='toolbar' style='margin-top:7px;gap:6px;flex-wrap:wrap'>" +
                "<button class='toolButton primary' id='showLocation'>Показать точку</button>" +
                "<input class='toolButton' id='locationRounds' type='number' min='1' max='128' value='" +
                  testRounds + "' style='width:80px' title='Количество раундов'>" +
                "<button class='toolButton primary' id='testLocation'>Тест</button>" +
                "<button class='toolButton' id='showLocationInSimulator' " +
                  (visualisationReady() ? "" : "disabled") +
                  " title='" + (visualisationReady()
                    ? "Показать на основной карте набор, который уже отображён на карте ниже"
                    : visualisationDisabledReason()) + "'>" +
                  "Показать в симуляторе</button>" +
              "</div>" +
              "<div class='miniLabel' style='margin-top:6px'>Тест каждый раз заново выбирает точки. Результаты показываются на карте ниже.</div>" +
              "<div class='notice' style='margin-top:6px'>" +
                "«Показать в симуляторе» открывает на ОСНОВНОЙ карте ТОТ ЖЕ набор, который виден ниже: там есть " +
                "города и другие ориентиры, поэтому видно, ГДЕ именно оказались точки. Пока теста не было, " +
                "показывать нечего — сначала нажмите «Тест»." +
              "</div>" +
            "</div>"
          : "<div class='card' style='margin-top:12px'><div class='miniLabel'>Проверка результата</div>" +
              "<div class='toolbar' style='margin-top:7px;gap:6px;flex-wrap:wrap'>" +
                "<button class='toolButton primary' id='showLocation'>Показать точку</button>" +
                // Та же кнопка, что и у Dynamic: показать выбранную точку на
                // основной карте с городами и дорогами. Её отсутствие заставляло
                // автора искать точку глазами по координатам.
                "<button class='toolButton' id='showLocationInSimulator' " +
                  (visualisationReady() ? "" : "disabled") +
                  " title='" + (visualisationReady()
                    ? "Показать выбранную точку на основной карте Симулятора"
                    : visualisationDisabledReason()) + "'>" +
                  "Показать в симуляторе</button>" +
              "</div>" +
              "</div>") +

        "<div class='card' style='margin-top:12px;position:relative'>" +
          "<div class='miniLabel'>Карта результатов Location</div>" +
          "<div style='height:360px;margin-top:8px;border:1px solid var(--border);border-radius:8px;overflow:hidden'>" +
            "<svg id='locationMap' viewBox='0 0 900 520' xmlns='http://www.w3.org/2000/svg' style='width:100%;height:100%'></svg>" +
          "</div>" +
        "</div>" +
      "</div>";

    ins.innerHTML =
      "<div class='badge accent'>Location</div>" +
      "<h3 style='margin:10px 0 4px'>" + escapeHtml(definition.name) + "</h3>" +
      "<div class='kv'><span>ID</span><span>" + escapeHtml(definition.id) + "</span></div>" +
      "<div class='kv'><span>Режим</span><span>" + escapeHtml(definition.mode) + "</span></div>" +
      "<div class='kv'><span>Критериев</span><span>" + (definition.query?.criteria?.length || 0) + "</span></div>" +
      (testResult
        ? "<div class='card' style='margin-top:10px'><div class='miniLabel'>Последний тест</div>" +
            "<div class='kv'><span>Раундов</span><span>" + testResult.requestedRounds + "</span></div>" +
            "<div class='kv'><span>Выбрано</span><span>" + testResult.successfulRounds + "</span></div>" +
            "<div class='kv'><span>Supported</span><span>" + (testResult.supported ? "да" : "нет") + "</span></div>" +
            (testResult.diagnostics?.length
              ? "<div class='notice" + (noCandidatesPulsePending ? " noticePulseAlert" : "") +
                "' style='margin-top:8px'>" +
                testResult.diagnostics.map(escapeHtml).join("<br>") + "</div>"
              : "") +
          "</div>"
        : "") +
      (currentPoint
        ? "<div class='card' style='margin-top:10px'><div class='miniLabel'>Текущая точка</div>" +
            "<div style='margin-top:5px'>" + escapeHtml(currentPoint.name) + "</div>" +
            "<div class='miniLabel' style='margin-top:3px'>" + formatPosition(currentPoint.position) + "</div></div>"
        : "") +
      "<div class='notice' style='margin-top:10px'>Location — логическая ссылка. Конкретный WorldPoint появляется только после разрешения.</div>";

    // Флаг сгорает здесь: класс добавлен в разметку выше, следующая перерисовка
    // (в том числе от постороннего сообщения) уже не перезапустит анимацию.
    noCandidatesPulsePending = false;

    wire();
    renderMap(testResult);
  }

  function wire() {
    const form = document.getElementById("locationForm");
    form?.querySelectorAll("[data-location-field]").forEach(input => {
      input.addEventListener("change", () => {
        state.definition = collectLocation();
        markDirty();
      });
    });

    form?.querySelectorAll("[data-history-field]").forEach(input => {
      input.addEventListener("change", () => {
        state.definition = collectLocation();
        markDirty();
      });
    });

    form?.querySelectorAll("[data-criterion]").forEach(row => {
      row.querySelector("[data-criterion-type]")?.addEventListener("change", () => {
        state.definition = collectLocation();
        markDirty();
      });
      row.querySelector("[data-criterion-negate]")?.addEventListener("change", () => {
        state.definition = collectLocation();
        markDirty();
      });
      row.querySelectorAll("[data-criterion-parameter], [data-provider-type], [data-provider-parameters]")
        .forEach(input => input.addEventListener("change", () => {
          state.definition = collectLocation();
          markDirty();
        }));
      row.querySelector("[data-remove-criterion]")?.addEventListener("click", () => {
        row.remove();
        state.definition = collectLocation();
        markDirty();
      });
      // Захват выбранной точки прямо в критерий. Значение пишется в поле, а не в
      // определение напрямую: collectLocation читает параметры из DOM, и прямая
      // запись была бы затёрта следующей же перерисовкой панели.
      row.querySelector("[data-criterion-capture-point]")?.addEventListener("click", () => {
        const point = capturableSelection();
        if (!point) return;
        const input = row.querySelector("[data-criterion-parameter='pointId']");
        if (!input) return;
        input.value = pointCaptureValue(point);
        state.definition = collectLocation();
        markDirty();
      });
    });

    document.getElementById("addCriterion")?.addEventListener("click", () => {
      state.definition = collectLocation();
      state.definition.query.criteria.push({
        type: "CategoryIs",
        parameters: { value: "" },
        negate: false
      });
      markDirty();
    });

    document.getElementById("useSelectedSimulatorPoint")?.addEventListener("click", () => {
      const point = capturableSelection();
      if (!point) return;
      state.definition = ensureDefinition();
      state.definition.mode = "Fixed";
      state.definition.worldPointId = point.id;
      pointSearch = "";
      markDirty();
    });

    // Поиск точки обновляет ТОЛЬКО список совпадений, а не всю панель: полная
    // перерисовка на каждый символ отбирала бы фокус у поля ввода.
    const pointSearchInput = document.getElementById("locationPointSearch");
    pointSearchInput?.addEventListener("input", () => {
      pointSearch = pointSearchInput.value;
      const results = document.getElementById("locationPointResults");
      if (results) results.innerHTML = searchResultsHtml();
      wirePointResults();
    });
    pointSearchInput?.addEventListener("change", () => {
      const match = findAnyWorldPoint(pointSearchInput.value);
      if (!match) return;
      state.definition = ensureDefinition();
      state.definition.mode = "Fixed";
      state.definition.worldPointId = referenceId(match);
      pointSearch = "";
      markDirty();
    });
    wirePointResults();

    document.getElementById("newLocation")?.addEventListener("click", () => {
      send({ action: "location_new" });
    });
    document.getElementById("openLocation")?.addEventListener("click", () => {
      send({ action: "location_open" });
    });
    document.getElementById("saveLocation")?.addEventListener("click", () => {
      state.definition = collectLocation();
      send({ action: "location_save", definition: state.definition });
    });
    document.getElementById("saveLocationAs")?.addEventListener("click", () => {
      state.definition = collectLocation();
      send({ action: "location_save_as", definition: state.definition });
    });
    document.getElementById("deleteLocation")?.addEventListener("click", () => {
      send({ action: "location_delete" });
    });
    document.getElementById("showLocation")?.addEventListener("click", () => {
      state.definition = collectLocation();
      send({ action: "location_show", definition: state.definition });
    });
    // Значение раундов читается и сохраняется в модуль перед отправкой: панель
    // перерисовывается ответом Host, и без этого поле возвращалось к значению по
    // умолчанию (введённое число раундов сбрасывалось после каждого теста).
    const readRounds = () => {
      const raw = Number(document.getElementById("locationRounds")?.value);
      testRounds = Number.isFinite(raw) ? Math.max(1, Math.min(128, Math.round(raw))) : 8;
      return testRounds;
    };
    document.getElementById("testLocation")?.addEventListener("click", () => {
      state.definition = collectLocation();
      send({ action: "location_test", definition: state.definition, rounds: readRounds() });
    });
    // «Показать в симуляторе» НЕ запускает новый поиск: на основную карту уходит
    // тот же набор, который уже нарисован ниже. Повторный прогон дал бы другие
    // точки (тест рандомизирован), и автор сравнивал бы не то, что проверял.
    // Для Fixed это единственная выбранная точка. Без набора кнопка неактивна.
    document.getElementById("showLocationInSimulator")?.addEventListener("click", () => {
      const entries = visualisationEntries();
      if (!entries.length) return;
      send({
        action: "location_show_in_simulator",
        definition: collectLocation(),
        candidates: entries.map(entry => ({ candidateId: entry.pointId })),
        diagnostics: testResult?.diagnostics || []
      });
    });
  }

  /**
   * Приём сообщений Host.
   *
   * Подписка ОБЯЗАНА идти на chrome.webview: именно через него WebView2
   * доставляет сообщения Host. Раньше был только window-addEventListener, и
   * панель не получала НИЧЕГО — ни списка мира, ни выбранной точки. При этом
   * форма рисовалась, потому что render() сам создаёт пустое определение, так
   * что снаружи это выглядело как «кнопка не активна» и «поиск ничего не ищет».
   * window оставлен вторым каналом для совместимости с остальными модулями
   * (editor.js и sceneEditor.js слушают оба).
   */
  const handleLocationMessage = event => {
    const data = typeof event.data === "string" ? (() => {
      try { return JSON.parse(event.data); } catch { return null; }
    })() : event.data;

    if (!data) return;

    if (data.type === "location_editor_state") {
      state = {
        definition: data.definition || blankDefinition(),
        documentPath: data.documentPath || "",
        documentDirty: Boolean(data.documentDirty),
        readOnly: Boolean(data.readOnly),
        locations: Array.isArray(data.locations) ? data.locations : [],
        worldPoints: Array.isArray(data.worldPoints) ? data.worldPoints : []
      };
      pointSearch = "";
      testResult = null;
      if (locationIsVisible())
        rerender();
      return;
    }

    if (data.type === "location_test") {
      testResult = data.result || null;
      // Флаг ставится только на НОВЫЙ результат: он сгорает в первом же рендере.
      noCandidatesPulsePending = hasNoCandidatesDiagnostic(testResult);
      if (locationIsVisible())
        rerender();
      return;
    }

    // Отказ режима визуализации. Кнопка неактивна без результата теста, поэтому
    // сюда попадаем только при рассинхронизации — но тогда пользователь обязан
    // увидеть причину, а не молчание.
    if (data.type === "location_show_result" && data.ok === false) {
      window.alert(data.message || "Набор для показа пуст: сначала нажмите «Тест».");
      return;
    }

    if (data.type === "simulator_context") {
      simulatorContext = data;
      if (locationIsVisible())
        rerender();
    }
  };

  window.addEventListener("message", handleLocationMessage);

  const webview = window.chrome?.webview;
  if (typeof webview?.addEventListener === "function") {
    webview.addEventListener("message", handleLocationMessage);
  }

  function locationIsVisible() {
    return location.hash.toLowerCase() === "#locations" ||
      location.hash.toLowerCase() === "#location";
  }

  window.__assistLocationEditor = { render };
})();
