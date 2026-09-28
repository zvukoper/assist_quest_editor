/**
 * Единая отрисовка шкал состояния персонажа.
 * Все значения приходят из Domain/Host в единицах 0..10000.
 */
(function (global) {
  "use strict";

  var SCALE_MAXIMUM = 10000;
  var UNITS_PER_PERCENT = 100;
  var CUMULATIVE_COLOR = "#ffd400";

  var SCALES = [
    {
      key: "health",
      label: "Здоровье",
      color: "#32cd32",
      fill: "consume",
      maxKey: "maxHealth",
      desc: "Здоровье: 100% — максимум."
    },
    {
      key: "stress",
      label: "Стресс",
      color: "#8c63d9",
      fill: "fill",
      maxKey: null,
      desc: "Стресс выше 50% запускает риск «Выгорания»."
    },
    {
      key: "energy",
      label: "Энергия",
      color: "#ff8c00",
      fill: "consume",
      maxKey: "maxEnergy",
      desc: "Расходуется в дороге и во сне медленнее."
    },
    {
      key: "hydration",
      label: "Жидкость",
      color: "#2f7ff0",
      fill: "consume",
      maxKey: "maxHydration",
      desc: "Уровень гидратации."
    },
    {
      key: "fatigue",
      label: "Усталость",
      color: "#e03131",
      fill: "fill",
      maxKey: "maxFatigue",
      desc: "Растёт от времени и дистанции, отдых и сон уменьшают."
    },
    {
      key: "resilience",
      label: "Устойчивость",
      color: "linear-gradient(90deg,#173b19,#318a35,#67cf67)",
      fill: "fill",
      maxKey: null,
      derived: true,
      desc: "Снижает вероятность негативных эффектов и замедляет стресс."
    },
    {
      key: "metabolism",
      label: "Метаболизм",
      color: "#8a9a68",
      fill: "fill",
      maxKey: null,
      derived: true,
      desc: "45–75% — нормальная зона. Ниже расход медленнее, от 75% — выше."
    }
  ];

  var LAYOUT = [
    ["health", "stress"],
    ["energy"],
    ["hydration"],
    ["fatigue"],
    ["resilience", "metabolism"]
  ];

  function escapeHtml(value) {
    return String(value == null ? "" : value)
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;")
      .replace(/'/g, "&#39;");
  }

  function scaleByKey(key) {
    return SCALES.find(function (item) {
      return item.key === key;
    }) || null;
  }

  function clampPercent(value) {
    var number = Number(value);
    return Number.isFinite(number)
      ? Math.max(0, Math.min(100, number))
      : 0;
  }

  function unitsToPercent(units) {
    return clampPercent(Number(units) / UNITS_PER_PERCENT);
  }

  function overchargeFor(conditions, key) {
    var value = Number(
      conditions["overcharge" +
        key.charAt(0).toUpperCase() +
        key.slice(1)]);
    return Number.isFinite(value) ? Math.max(0, value) : 0;
  }

  function cumulativeFor(conditions, key) {
    var value = Number(
      conditions["cumulative" +
        key.charAt(0).toUpperCase() +
        key.slice(1)]);
    return Number.isFinite(value) ? Math.max(0, value) : 0;
  }

  function read(snapshot, scale) {
    var vitals = (snapshot && snapshot.playerVitals) || {};
    var conditions = (snapshot && snapshot.conditions) || {};

    if (scale.derived) {
      var derivedUnits = Number(vitals[scale.key]);
      derivedUnits = Number.isFinite(derivedUnits)
        ? Math.max(0, Math.min(SCALE_MAXIMUM, derivedUnits))
        : 0;
      return {
        soft: unitsToPercent(derivedUnits),
        cumulative: 0,
        overcharge: 0,
        actual: derivedUnits,
        cumulativeUnits: 0,
        maximum: SCALE_MAXIMUM,
        percent: unitsToPercent(derivedUnits),
        displayPercent: unitsToPercent(derivedUnits)
      };
    }

    var cumulativeUnits = cumulativeFor(conditions, scale.key);
    var overchargeUnits = overchargeFor(conditions, scale.key);

    if (scale.key === "stress") {
      var stressUnits = Number(conditions.stress);
      stressUnits = Number.isFinite(stressUnits)
        ? Math.max(0, Math.min(SCALE_MAXIMUM, stressUnits))
        : 0;
      var stressPercent = unitsToPercent(stressUnits);
      return {
        soft: stressPercent,
        cumulative: unitsToPercent(cumulativeUnits),
        overcharge: overchargeUnits,
        actual: stressUnits,
        cumulativeUnits: cumulativeUnits,
        maximum: SCALE_MAXIMUM,
        percent: stressPercent,
        displayPercent: stressPercent + unitsToPercent(overchargeUnits)
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
      cumulative: unitsToPercent(cumulativeUnits),
      overcharge: overchargeUnits,
      actual: actualUnits,
      cumulativeUnits: cumulativeUnits,
      maximum: maximumUnits,
      percent: percent,
      displayPercent: percent + unitsToPercent(overchargeUnits)
    };
  }

  function valueLabel(scale, values) {
    return Math.round(
      values.displayPercent == null
        ? values.percent
        : values.displayPercent) + "%";
  }

  function formatRate(unitsPerMinute) {
    var value = Number(unitsPerMinute);
    if (!Number.isFinite(value))
      return "0 ед./мин";
    var rounded = Math.round(value);
    return (rounded > 0 ? "+" : "") +
      rounded + " ед./мин";
  }

  function rateFor(snapshot, scale) {
    var rates = (snapshot && snapshot.conditionRates) || {};
    return rates[scale.key];
  }

  function hasEffect(snapshot, id) {
    return !!(((snapshot &&
      snapshot.conditions &&
      snapshot.conditions.effects) || [])
      .some(function (effect) {
        return String(effect.id || "").toLowerCase() ===
          String(id).toLowerCase() &&
          Number(effect.remainingRealSeconds) > 0;
      }));
  }

  function tooltip(scale, values, snapshot) {
    var lines = [
      scale.label,
      "Реальное значение: " +
        Math.round(values.actual) + "/" +
        Math.round(values.maximum) +
        " (" + Math.round(values.percent) + "%)",
      "Форсаж: " +
        (values.overcharge > 0
          ? "+" + Math.round(unitsToPercent(values.overcharge)) +
            "% (расходуется первым)"
          : "нет"),
      "Скорость: " + formatRate(rateFor(snapshot, scale))
    ];

    if (!scale.derived)
      lines.push(
        "Кумулятивное значение: " +
        Math.round(values.cumulativeUnits) +
        "/" + SCALE_MAXIMUM +
        " (" + Math.round(values.cumulative) + "%)");

    if (scale.key === "energy" || scale.key === "stress") {
      var conditions = (snapshot && snapshot.conditions) || {};
      var caffeineLoad = Number(conditions.caffeineLoadMg);
      var caffeineDaily = Number(conditions.caffeineDailyMg);
      var caffeineDependence = Number(conditions.caffeineDependence);
      if (Number.isFinite(caffeineLoad) ||
          Number.isFinite(caffeineDaily) ||
          Number.isFinite(caffeineDependence)) {
        lines.push(
          "Кофеин: " +
          (Number.isFinite(caffeineLoad) ? Math.round(caffeineLoad) : 0) +
          " мг в организме; " +
          (Number.isFinite(caffeineDaily) ? Math.round(caffeineDaily) : 0) +
          " мг за игровые сутки; адаптация " +
          (Number.isFinite(caffeineDependence) ? Math.round(caffeineDependence) : 0) +
          "%");
      }
    }

    lines.push(scale.desc);
    return lines.join("\n");
  }

  function barMarkup(scale, values, snapshot) {
    var cumulativeClass =
      scale.fill === "fill"
        ? "fromStart"
        : "fromEnd";
    var overchargeClass = cumulativeClass;
    var surgeClass =
      hasEffect(snapshot, "power_surge")
        ? " powerSurge"
        : "";
    var tooltipText =
      escapeHtml(tooltip(scale, values, snapshot));
    var cumulative =
      values.cumulative > 0
        ? "<div class='dualBarCumulative " +
          cumulativeClass +
          "' style=\"width:" +
          values.cumulative +
          "%;background:" +
          CUMULATIVE_COLOR +
          "\"></div>"
        : "";
    var overchargePercent =
      unitsToPercent(values.overcharge);
    var overcharge =
      overchargePercent > 0
        ? "<div class='dualBarOvercharge " +
          overchargeClass +
          "' style=\"width:" +
          Math.min(100, overchargePercent) +
          "%\"></div>"
        : "";

    return "<div class='dualStat" + surgeClass +
      "' data-vital-scale='" + scale.key +
      "' data-game-tooltip=\"" +
      tooltipText + "\">" +
      "<div class='dualStatHead'>" +
        "<span>" + escapeHtml(scale.label) + "</span>" +
        "<span>" + valueLabel(scale, values) + "</span>" +
      "</div>" +
      "<div class='dualBar " + scale.key + "'>" +
        "<div class='dualBarSoft' style=\"width:" +
          values.soft +
          "%;background:" +
          scale.color +
          "\"></div>" +
        cumulative +
        overcharge +
      "</div>" +
    "</div>";
  }

  function markup(snapshot) {
    return "<div class='conditionBars'>" +
      LAYOUT.map(function (rowKeys, rowIndex) {
        var cells = rowKeys.map(function (key) {
          var scale = scaleByKey(key);
          return scale
            ? barMarkup(
                scale,
                read(snapshot, scale),
                snapshot)
            : "";
        }).join("");
        var className =
          rowKeys.length > 1
            ? "conditionTopGrid"
            : "conditionRow";
        return "<div class='" +
          className +
          "' data-vital-row='" +
          rowIndex + "'>" +
          cells +
          "</div>";
      }).join("") +
    "</div>";
  }

  function fieldsMarkup(snapshot) {
    var vitals = (snapshot && snapshot.playerVitals) || {};
    var conditions = (snapshot && snapshot.conditions) || {};

    function field(key, label, units) {
      return "<div class='field'>" +
        "<label>" + label + "</label>" +
        "<input data-t='" + key +
        "' value='" +
        Math.round(unitsToPercent(units)) +
        "'></div>";
    }

    return "<div class='fieldGrid' style='margin-top:8px'>" +
      field("health", "Здоровье", vitals.health) +
      field("stress", "Стресс", conditions.stress) +
      field("energy", "Энергия", vitals.energy) +
      field("hydration", "Жидкость", vitals.hydration) +
      field("fatigue", "Усталость", vitals.fatigue) +
      field("resilience", "Устойчивость", vitals.resilience) +
      field("metabolism", "Метаболизм", vitals.metabolism) +
    "</div>";
  }

  var tooltipElement = null;
  var tooltipsAttached = false;

  function attachTooltips(doc) {
    var target = doc ||
      (typeof document !== "undefined"
        ? document
        : null);
    if (!target || tooltipsAttached)
      return;
    tooltipsAttached = true;

    function ensureElement() {
      if (tooltipElement &&
          tooltipElement.ownerDocument === target)
        return tooltipElement;
      tooltipElement = target.createElement("div");
      tooltipElement.id = "assistGameTooltip";
      tooltipElement.className = "gameTooltip";
      target.body.appendChild(tooltipElement);
      return tooltipElement;
    }

    target.addEventListener(
      "mouseover",
      function (event) {
        var element =
          event.target &&
          event.target.closest
            ? event.target.closest(
                "[data-game-tooltip]")
            : null;
        if (!element)
          return;

        var text =
          element.getAttribute(
            "data-game-tooltip");
        if (!text)
          return;

        var tip = ensureElement();
        tip.textContent = text;
        tip.style.display = "block";

        var rect =
          element.getBoundingClientRect();
        tip.style.left =
          Math.max(
            8,
            Math.min(
              window.innerWidth - 310,
              rect.left)) + "px";
        tip.style.top =
          Math.max(
            8,
            rect.top -
              tip.offsetHeight -
              7) + "px";
      });

    target.addEventListener(
      "mouseout",
      function (event) {
        var element =
          event.target &&
          event.target.closest
            ? event.target.closest(
                "[data-game-tooltip]")
            : null;
        if (element &&
            event.relatedTarget &&
            element.contains(
              event.relatedTarget))
          return;
        if (tooltipElement)
          tooltipElement.style.display = "none";
      });
  }

  attachTooltips(
    typeof document !== "undefined"
      ? document
      : null);

  global.AssistVitals = {
    CUMULATIVE_COLOR: CUMULATIVE_COLOR,
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
