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
 *   • Подсказка при наведении обязана называть три вещи: что это за шкала,
 *     реальное значение и ОТДЕЛЬНО кумулятивное.
 */
(function (global) {
  "use strict";

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
        "Заполняется за 18 игровых часов; снимается отдыхом и сном."
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

  /**
   * Значения одной шкалы из снимка.
   *
   * Возвращает проценты для отрисовки и ЧИСЛА для подсказки: процент без
   * абсолютного значения не объясняет, почему шкала стоит на месте, — при
   * закреплённом кумулятивом максимуме 80 из 100 и 80 из 80 выглядят одинаково.
   */
  function read(snapshot, scale) {
    var vitals = (snapshot && snapshot.playerVitals) || {};
    var conditions = (snapshot && snapshot.conditions) || {};

    var cumulative = Number(conditions["cumulative" + capitalize(scale.key)]);
    var cumulativePercent = Number.isFinite(cumulative) ? clampPercent(cumulative) : 0;

    if (scale.key === "stress") {
      var stress = Number(conditions.stress);
      var percent = Number.isFinite(stress) ? clampPercent(stress) : 0;
      return {
        soft: percent,
        cumulative: cumulativePercent,
        actual: percent,
        maximum: 100,
        maximumLabel: null,
        percent: percent
      };
    }

    var maximum = Number(vitals[scale.maxKey]);
    maximum = Number.isFinite(maximum) && maximum > 0 ? maximum : 100;

    var actual = Number(vitals[scale.key]);
    actual = Number.isFinite(actual) ? Math.max(0, Math.min(maximum, actual)) : 0;

    return {
      soft: clampPercent(actual / maximum * 100),
      cumulative: cumulativePercent,
      actual: actual,
      maximum: maximum,
      maximumLabel: null,
      percent: clampPercent(actual / maximum * 100)
    };
  }

  function capitalize(value) {
    return String(value || "").charAt(0).toUpperCase() + String(value || "").slice(1);
  }

  /** Итоговая подпись значения: у стресса это проценты, у остальных — проценты. */
  function valueLabel(scale, values) {
    return Math.round(values.percent) + "%";
  }

  /**
   * Расшифровка для наведения: что за шкала, реальное значение и ОТДЕЛЬНО
   * кумулятивное. Три части идут именно в этом порядке: сначала игрок
   * понимает, на что смотрит, потом видит текущее число, и только потом —
   * почему на шкале есть жёлтая часть.
   */
  function tooltip(scale, values) {
    // Абсолютные числа берутся из снимка, а не из процентов: при закреплённом
    // кумулятивом максимум у расходуемых шкал УМЕНЬШАЕТСЯ, и «80%» без «80 из 80»
    // читалось бы как «ещё есть запас».
    var actualText = Math.round(values.actual) + " из " + Math.round(values.maximum);

    var lines = [
      scale.label,
      "Реальное значение: " + actualText + " (" + Math.round(values.percent) + "%)",
      "Кумулятивное значение: " + Math.round(values.cumulative) + "%",
      scale.desc
    ];

    return lines.join("\n");
  }

  /** Одна шкала: заголовок со значением и полоса из мягкой и кумулятивной части. */
  function barMarkup(scale, values) {
    // Кумулятив у расходуемых шкал растёт с конца, у наполняемых — с начала.
    var cumulativeClass = scale.fill === "fill" ? "fromStart" : "fromEnd";
    var text = escapeHtml(tooltip(scale, values));

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
        return barMarkup(scale, read(snapshot, scale));
      }).join("");

      var className = rowKeys.length > 1 ? "conditionTopGrid" : "conditionRow";
      return "<div class='" + className + "' data-vital-row='" + rowIndex + "'>" +
        cells + "</div>";
    }).join("");

    return "<div class='conditionBars'>" + rows + "</div>";
  }

  /** Поля ввода значений — общие для сайдбара; стресс правится отдельно. */
  function fieldsMarkup(snapshot) {
    var vitals = (snapshot && snapshot.playerVitals) || {};
    var conditions = (snapshot && snapshot.conditions) || {};

    function field(key, label, value) {
      return "<div class='field'><label>" + label + "</label>" +
        "<input data-t='" + key + "' value='" + Math.round(Number(value) || 0) + "'></div>";
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
    SCALES: SCALES,
    LAYOUT: LAYOUT,
    escapeHtml: escapeHtml,
    scaleByKey: scaleByKey,
    read: read,
    tooltip: tooltip,
    barMarkup: barMarkup,
    markup: markup,
    fieldsMarkup: fieldsMarkup,
    attachTooltips: attachTooltips
  };
})(typeof window !== "undefined" ? window : this);
