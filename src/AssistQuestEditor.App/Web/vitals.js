/**
 * Шкалы состояния игрока: одна разметка, цвета и расшифровка для ВСЕХ мест,
 * где они показываются — правого сайдбара Симулятора и окна «Игрок».
 *
 * Модуль появился потому, что шкалы рисовались в двух местах разными копиями
 * разметки. Копии разошлись ровно так, как и должны были: в окне «Игрок» не
 * оказалось шкалы «Стресс», цвета не совпадали с сайдбаром, и подсказок о том,
 * что означает жёлтое деление, не было ни там, ни там. Держать правила в одном
 * месте дешевле, чем синхронизировать их вручную.
 *
 * Правила, зашитые здесь (заданы постановкой задачи):
 *
 *   • Двойная шкала у КАЖДОГО состояния: мягкое (обычное) значение и
 *     кумулятивное. Кумулятивное ВСЕГДА ярко-жёлтое и рисуется с края:
 *     у расходуемых — с конца, у наполняемых — с начала.
 *   • Цвета мягких шкал: здоровье lime, энергия тёмно-оранжевая, жидкость
 *     синяя, усталость красная, стресс фиолетовый.
 *   • Подсказка при наведении обязана называть четыре вещи: что это за шкала,
 *     реальное значение, скорость изменения и ОТДЕЛЬНО кумулятивное.
 *
 * ЕДИНИЦЫ И ПРОЦЕНТЫ. В снимке состояние приходит в ЕДИНИЦАХ шкалы 0..10000
 * (домен хранит именно их), а игроку показывается процент: одна сотая шкалы —
 * это ровно 100 единиц. Проценты выводятся делением на 100, поэтому округление
 * не «съедает» движение — скорость изменения шкалы измеряется единицами за
 * игровую минуту, и её видно целиком, а не как 0%.
 */
(function (global) {
  "use strict";

  /** Максимум любой шкалы в единицах: совпадает с PlayerConditionScale.Maximum. */
  var SCALE_MAXIMUM = 10000;

  /** Единиц в одном проценте: показ процентов не должен дублировать домен. */
  var UNITS_PER_PERCENT = SCALE_MAXIMUM / 100;
  /** Кумулятивная часть всегда одного цвета — так её видно на любой шкале. */
  var CUMULATIVE_COLOR = "#ffd400";

  /**
   * Пять состояний: порядок, подписи, цвета и направление.
   *
   * `fill` = "consume" — шкала расходуется (здоровье, энергия, жидкость):
   * кумулятивный эффект отнимает максимум и растёт с конца шкалы.
   * `fill` = "fill" — шкала наполняется (усталость, стресс): кумулятивный
   * эффект закрепляет минимум и растёт с начала.
   */
  var SCALES = [
    {
      key: "health",
      label: "Здоровье",
      color: "#32cd32",
      fill: "consume",
      maxKey: "maxHealth",
      desc: "Здоровье: 100 — полностью здоров, 0 — смерть."
    },
    {
      key: "stress",
      label: "Стресс",
      color: "#8c63d9",
      fill: "fill",
      maxKey: null,
      desc: "Стресс: 0 — спокоен, 100 — предел. Выше 50% вызывает «Выгорание»."
    },
    {
      key: "energy",
      label: "Энергия",
      color: "#ff8c00",
      fill: "consume",
      maxKey: "maxEnergy",
      desc: "Энергия: запас сил. Расходуется в дороге, восстанавливается отдыхом."
    },
    {
      key: "hydration",
      label: "Жидкость",
      color: "#2f7ff0",
      fill: "consume",
      maxKey: "maxHydration",
      desc: "Жидкость: уровень гидратации игрока."
    },
    {
      key: "fatigue",
      label: "Усталость",
      color: "#e03131",
      fill: "fill",
      maxKey: "maxFatigue",
      desc: "Усталость: 0 — полностью отдохнул, 100 — предельная. " +
        "Растёт и по игровым часам (100% за 18 часов в пути), и по пройденной " +
        "дистанции (100% за 900 км) — стоящий на месте игрок восстанавливается, " +
        "сон снимает усталость полностью."
    }
  ];

  /**
   * Раскладка: здоровье и стресс стоят парой (стресс — справа от здоровья),
   * остальные — во всю ширину. Одинакова в сайдбаре и в окне «Игрок».
   */
  var LAYOUT = [["health", "stress"], ["energy"], ["hydration"], ["fatigue"]];

  function escapeHtml(value) {
    return String(value == null ? "" : value)
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;")
      .replace(/'/g, "&#39;");
  }

  function scaleByKey(key) {
    return SCALES.find(function (item) { return item.key === key; }) || null;
  }

  function clampPercent(value) {
    return Math.max(0, Math.min(100, Number(value) || 0));
  }

  /** Единицы → проценты: шкала ведётся целочисленно, поэтому деление точное. */
  function unitsToPercent(units) {
    return clampPercent((Number(units) || 0) / UNITS_PER_PERCENT);
  }

  /**
   * Значения одной шкалы из снимка.
   *
   * Возвращает единицы для подсказки и проценты для отрисовки. Единицы нужны
   * потому, что процент слишком груб: скорость изменения шкалы (единицы за
   * игровую минуту) при показе процентов округлялась бы до нуля, и игрок не
   * видел бы, что шкала вообще движется.
   */
  function read(snapshot, scale) {
    var vitals = (snapshot && snapshot.playerVitals) || {};
    var conditions = (snapshot && snapshot.conditions) || {};

    var cumulativeUnits = Number(conditions["cumulative" + capitalize(scale.key)]);
    cumulativeUnits = Number.isFinite(cumulativeUnits) ? Math.max(0, cumulativeUnits) : 0;
    var cumulativePercent = unitsToPercent(cumulativeUnits);

    if (scale.key === "stress") {
      var stressUnits = Number(conditions.stress);
      stressUnits = Number.isFinite(stressUnits) ? Math.max(0, stressUnits) : 0;
      var stressPercent = unitsToPercent(stressUnits);
      return {
        soft: stressPercent,
        cumulative: cumulativePercent,
        actual: stressUnits,
        cumulativeUnits: cumulativeUnits,
        maximum: SCALE_MAXIMUM,
        percent: stressPercent
      };
    }

    var maximumUnits = Number(vitals[scale.maxKey]);
    maximumUnits = Number.isFinite(maximumUnits) && maximumUnits > 0
      ? maximumUnits
      : SCALE_MAXIMUM;

    var actualUnits = Number(vitals[scale.key]);
    actualUnits = Number.isFinite(actualUnits)
      ? Math.max(0, Math.min(maximumUnits, actualUnits))
      : 0;

    var percent = unitsToPercent(actualUnits);

    return {
      soft: percent,
      cumulative: cumulativePercent,
      actual: actualUnits,
      cumulativeUnits: cumulativeUnits,
      maximum: maximumUnits,
      percent: percent
    };
  }

  function capitalize(value) {
    return String(value || "").charAt(0).toUpperCase() + String(value || "").slice(1);
  }

  /** Итоговая подпись значения: игрок всегда видит проценты. */
  function valueLabel(scale, values) {
    return Math.round(values.percent) + "%";
  }

  /**
   * Скорость изменения шкалы в ЕДИНИЦАХ за игровую минуту.
   *
   * Знак важен: «+55» — шкала растёт, «-55» — восстанавливается, «0» — стоит.
   * Округление до целого делается здесь, потому что в снимке скорость приходит
   * дробной и её точность нужна только расчёту, а не подписи.
   */
  function formatRate(unitsPerMinute) {
    var value = Number(unitsPerMinute);
    if (!Number.isFinite(value)) return "0 ед./мин";
    var rounded = Math.round(value);
    var sign = rounded > 0 ? "+" : "";
    return sign + rounded + " ед./мин";
  }

  /** Скорость шкалы из снимка: домен считает её, Web только показывает. */
  function rateFor(snapshot, scale) {
    var rates = (snapshot && snapshot.conditionRates) || {};
    return rates[scale.key];
  }

  /**
   * Расшифровка для наведения: что за шкала, реальное значение, скорость
   * изменения и ОТДЕЛЬНО кумулятивное.
   *
   * Порядок частей именно такой: сначала игрок понимает, на что смотрит, потом
   * видит текущее число, затем — куда шкала движется и с какой скоростью, и
   * только потом узнаёт, почему на полосе есть жёлтая часть.
   */
  function tooltip(scale, values, snapshot) {
    // Абсолютные числа берутся из снимка в ЕДИНИЦАХ шкалы, а не из процентов:
    // при закреплённом кумулятивом максимум у расходуемых шкал УМЕНЬШАЕТСЯ, и
    // «80%» без «8000 из 8000» читалось бы как «ещё есть запас».
    var actualText = Math.round(values.actual) + "/" + Math.round(values.maximum);

    var lines = [
      scale.label,
      "Реальное значение: " + actualText + " (" + Math.round(values.percent) + "%)",
      "Скорость: " + formatRate(rateFor(snapshot, scale)),
      "Кумулятивное значение: " +
        Math.round(values.cumulativeUnits) + "/" + SCALE_MAXIMUM +
        " (" + Math.round(values.cumulative) + "%)",
      scale.desc
    ];

    return lines.join("\n");
  }

  /** Одна шкала: заголовок со значением и полоса из мягкой и кумулятивной части. */
  function barMarkup(scale, values, snapshot) {
    // Кумулятив у расходуемых шкал растёт с конца, у наполняемых — с начала.
    var cumulativeClass = scale.fill === "fill" ? "fromStart" : "fromEnd";
    var text = escapeHtml(tooltip(scale, values, snapshot));

    // Подсказка навешена на всю плитку, а не только на полосу: у 8-пиксельной
    // полосы цель слишком мелкая, и подсказку было почти невозможно вызвать.
    return "<div class='dualStat' data-vital-scale='" + scale.key + "' " +
        "data-game-tooltip=\"" + text + "\">" +
      "<div class='dualStatHead'>" +
        "<span>" + escapeHtml(scale.label) + "</span>" +
        "<span>" + valueLabel(scale, values) + "</span>" +
      "</div>" +
      "<div class='dualBar " + scale.key + "'>" +
        "<div class='dualBarSoft' style=\"width:" + values.soft +
          "%;background:" + scale.color + "\"></div>" +
        "<div class='dualBarCumulative " + cumulativeClass +
          "' style=\"width:" + values.cumulative +
          "%;background:" + CUMULATIVE_COLOR + "\"></div>" +
      "</div>" +
    "</div>";
  }

  /**
   * Полный блок шкал.
   *
   * @param {object} snapshot снимок Симулятора
   */
  function markup(snapshot) {
    var rows = LAYOUT.map(function (rowKeys, rowIndex) {
      var cells = rowKeys.map(function (key) {
        var scale = scaleByKey(key);
        if (!scale) return "";
        return barMarkup(scale, read(snapshot, scale), snapshot);
      }).join("");

      var className = rowKeys.length > 1 ? "conditionTopGrid" : "conditionRow";
      return "<div class='" + className + "' data-vital-row='" + rowIndex + "'>" +
        cells + "</div>";
    }).join("");

    return "<div class='conditionBars'>" + rows + "</div>";
  }

  /**
   * Поля ввода значений — общие для сайдбара; стресс правится отдельно.
   *
   * Поля показывают ПРОЦЕНТЫ: автору привычнее править «80», а не «8000», и
   * перевод в единицы делает Хост (SetPlayerVitals/SetStress). Здесь единицы
   * делятся на 100 — иначе поле показывало бы «8000» и правка уводила бы шкалу
   * за максимум.
   */
  function fieldsMarkup(snapshot) {
    var vitals = (snapshot && snapshot.playerVitals) || {};
    var conditions = (snapshot && snapshot.conditions) || {};

    function field(key, label, units) {
      return "<div class='field'><label>" + label + "</label>" +
        "<input data-t='" + key + "' value='" +
        Math.round(unitsToPercent(units)) + "'></div>";
    }

    return "<div class='fieldGrid' style='margin-top:8px'>" +
      field("health", "Здоровье", vitals.health) +
      field("stress", "Стресс", conditions.stress) +
      field("energy", "Энергия", vitals.energy) +
      field("hydration", "Жидкость", vitals.hydration) +
      field("fatigue", "Усталость", vitals.fatigue) +
    "</div>";
  }

  var tooltipElement = null;
  var tooltipsAttached = false;

  /**
   * Подсказки для элементов с `data-game-tooltip`.
   *
   * Общий обработчик на документ, а не на каждый элемент: разметка шкал
   * перерисовывается на каждом снимке (несколько раз в секунду), и навешивать
   * обработчики на новые узлы означало бы либо утечку, либо пропущенный узел.
   */
  function attachTooltips(doc) {
    var target = doc || (typeof document !== "undefined" ? document : null);
    if (!target || tooltipsAttached) return;
    tooltipsAttached = true;

    function ensureElement() {
      if (tooltipElement && tooltipElement.ownerDocument === target) return tooltipElement;
      tooltipElement = target.createElement("div");
      tooltipElement.id = "assistGameTooltip";
      tooltipElement.className = "gameTooltip";
      target.body.appendChild(tooltipElement);
      return tooltipElement;
    }

    target.addEventListener("mouseover", function (event) {
      var element = event.target && event.target.closest
        ? event.target.closest("[data-game-tooltip]")
        : null;
      if (!element) return;

      var text = element.getAttribute("data-game-tooltip");
      if (!text) return;

      var tip = ensureElement();
      tip.textContent = text;
      tip.style.display = "block";

      var rect = element.getBoundingClientRect();
      tip.style.left = Math.max(8, Math.min(window.innerWidth - 310, rect.left)) + "px";
      tip.style.top = Math.max(8, rect.top - tip.offsetHeight - 7) + "px";
    });

    target.addEventListener("mouseout", function (event) {
      var element = event.target && event.target.closest
        ? event.target.closest("[data-game-tooltip]")
        : null;
      if (element && event.relatedTarget && element.contains(event.relatedTarget)) return;
      if (tooltipElement) tooltipElement.style.display = "none";
    });
  }

  // Скрипты подключаются в конце body, поэтому документ уже готов.
  attachTooltips(typeof document !== "undefined" ? document : null);

  global.AssistVitals = {
    CUMULATIVE_COLOR: CUMULATIVE_COLOR,
    /** Максимум шкалы в единицах — для сверки с доменом и тестов. */
    SCALE_MAXIMUM: SCALE_MAXIMUM,
    UNITS_PER_PERCENT: UNITS_PER_PERCENT,
    SCALES: SCALES,
    LAYOUT: LAYOUT,
    escapeHtml: escapeHtml,
    scaleByKey: scaleByKey,
    unitsToPercent: unitsToPercent,
    formatRate: formatRate,
    rateFor: rateFor,
    read: read,
    tooltip: tooltip,
    barMarkup: barMarkup,
    markup: markup,
    fieldsMarkup: fieldsMarkup,
    attachTooltips: attachTooltips
  };
})(typeof window !== "undefined" ? window : this);
