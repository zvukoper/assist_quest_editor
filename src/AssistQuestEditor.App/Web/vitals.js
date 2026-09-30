/**
 * Единая отрисовка шкал состояния персонажа.
 * Все значения приходят из Domain/Host в единицах 0..10000.
 *
 * ВАЖНО про «потолок». У двух шкал есть ФИЗИЧЕСКАЯ размерность, и домен
 * присылает её отдельными полями: `energyKilocalories` / `maxEnergyKilocalories`
 * (0..5000 ккал) и `hydrationMilliliters` / `maxHydrationMilliliters`
 * (0..3000 мл). Внутренние единицы хранения (0..10000) видны только
 * разработчику — игроку показывается ФИЗИЧЕСКАЯ величина, и потолок у неё
 * 5000 и 3000, а не 10000. Раньше интерфейс читал только единицы, из-за чего
 * «Жидкость» показывала «7000 / 10000», хотя это 2100 из 3000 мл.
 *
 * Числа размерности (5000 и 3000) в JavaScript НЕ дублируются: их присылает
 * домен вместе со снимком, и Web берёт их из полей `physical`/`maxPhysical`.
 */
(function (global) {
  "use strict";

  var SCALE_MAXIMUM = 10000;
  var UNITS_PER_PERCENT = 100;
  var CUMULATIVE_COLOR = "#ffd400";

  /**
   * «Полезное направление» шкалы: +1 — игроку выгодно, чтобы значение росло,
   * −1 — чтобы падало.
   *
   * Знак нужен потому, что «динамика вверх» для энергии и для усталости
   * означает ПРОТИВОПОЛОЖНОЕ по смыслу: треугольник обязан показывать пользу
   * для игрока, а не арифметическое направление. Тот же словарь, что в мониторе
   * показателей: два разных ответа на один вопрос «растёт или падает» были бы
   * расхождением интерфейса с самим собой.
   */
  var GOOD_DIRECTION = {
    health: 1,
    energy: 1,
    hydration: 1,
    stress: -1,
    fatigue: -1,
    resilience: 1,
    metabolism: 1
  };

  /** Ниже этой скорости (единиц шкалы в игровую минуту) считаем «не меняется». */
  var FLAT_RATE_EPSILON = 0.05;

  var SCALES = [
    {
      key: "health",
      label: "Здоровье",
      color: "#32cd32",
      fill: "consume",
      maxKey: "maxHealth",
      desc: "Здоровье: 100% — максимум.",
      affects:
        "На что влияет: при 0% персонаж погибает; пока здоровья нет, " +
        "восстановление съедает энергию и жидкость и удваивает усталость."
    },
    {
      key: "stress",
      label: "Стресс",
      color: "#8c63d9",
      fill: "fill",
      maxKey: null,
      desc: "Стресс выше 50% запускает риск «Выгорания».",
      affects:
        "На что влияет: снижает получаемый опыт, замедляет восстановление " +
        "здоровья и ускоряет истощение организма."
    },
    {
      key: "energy",
      label: "Энергия",
      color: "#ff8c00",
      fill: "consume",
      maxKey: "maxEnergy",
      // ФИЗИЧЕСКАЯ размерность шкалы: игроку показываются килокалории, а не
      // единицы хранения. Поля приходят из домена вместе со снимком.
      physicalKey: "energyKilocalories",
      physicalMaxKey: "maxEnergyKilocalories",
      physicalUnit: "ккал",
      desc: "Шкала 0–5000 ккал. Расходуется в дороге, во сне медленнее.",
      affects:
        "На что влияет: без энергии падает работоспособность и здоровье; " +
        "восстанавливается едой и сном."
    },
    {
      key: "hydration",
      label: "Жидкость",
      color: "#2f7ff0",
      fill: "consume",
      maxKey: "maxHydration",
      // Единицы — миллилитры: шкала 0–3000 мл, а не 0–10000 единиц.
      physicalKey: "hydrationMilliliters",
      physicalMaxKey: "maxHydrationMilliliters",
      physicalUnit: "мл",
      desc:
        "Шкала 0–3000 мл. Ниже 1500 мл начинается «Жажда», " +
        "ниже 500 мл — «Обезвоживание».",
      affects:
        "На что влияет: обезвоживание ускоряет усталость и мешает " +
        "восстановлению здоровья; восстанавливается питьём."
    },
    {
      key: "fatigue",
      label: "Усталость",
      color: "#e03131",
      fill: "fill",
      maxKey: "maxFatigue",
      desc: "Растёт от времени и дистанции, отдых и сон уменьшают.",
      affects:
        "На что влияет: выше 80% начинается критическое истощение — " +
        "ускоряется расход энергии и жидкости и падает здоровье."
    },
    {
      key: "resilience",
      label: "Устойчивость",
      color: "linear-gradient(90deg,#173b19,#318a35,#67cf67)",
      fill: "fill",
      maxKey: null,
      derived: true,
      desc: "Снижает вероятность негативных эффектов и замедляет стресс.",
      affects:
        "На что влияет: выше 60% даёт +1% к восстановлению здоровья " +
        "за каждые 10 игр. мин."
    },
    {
      key: "metabolism",
      label: "Метаболизм",
      color: "#8a9a68",
      fill: "fill",
      maxKey: null,
      derived: true,
      desc: "45–75% — нормальная зона. Ниже расход медленнее, от 75% — выше.",
      affects:
        "На что влияет: выше 60% даёт +1% к восстановлению здоровья " +
        "за каждые 10 игр. мин; от 75% растёт расход энергии и жидкости."
    }
  ];

  var LAYOUT = [
    ["health", "stress"],
    ["energy"],
    ["hydration"],
    ["fatigue"],
    ["resilience", "metabolism"]
  ];

  /**
   * С КАКОЙ СТОРОНЫ кладётся кумулятивная (истощённая) часть шкалы —
   * задано автором и выведено из смысла истощения, а не из вида шкалы.
   *
   *   • «end» — истощение ОГРАНИЧИВАЕТ МАКСИМУМ: закреплённая часть растёт от
   *     правого края, и видно, что доступный потолок стал ниже 100%. Так ведут
   *     себя расходуемые шкалы: истощение здоровья, энергии и жидкости.
   *   • «start» — истощение СОЗДАЁТ НЕСНИЖАЕМЫЙ ПОРОГ: закреплённая часть растёт
   *     от левого края, и видно, что ниже неё значение уже не опустится. Так
   *     ведут себя накопительные шкалы: истощение стресса и усталости.
   *
   * Сторона объявлена ЯВНО на каждой шкале, а не выведена из fill/consume:
   * вид шкалы и смысл истощения совпадают лишь случайно, и следующая шкала
   * с другим fill молча получила бы закрепление не с той стороны.
   */
  var CUMULATIVE_SIDE = {
    health: "end",
    energy: "end",
    hydration: "end",
    stress: "start",
    fatigue: "start"
  };

  function cumulativeClassFor(scale) {
    var side = CUMULATIVE_SIDE[scale.key] || "start";
    return side === "end" ? "fromEnd" : "fromStart";
  }

  /**
   * Описания эффектов: что бафф или дебафф делает с показателями.
   *
   * Тексты живут здесь, а не в домене: они объясняют механику игроку, и их
   * правка не должна менять поведение мира. Ключ — стабильный id эффекта
   * (в нижнем регистре), а не подпись: подпись может меняться, id — нет.
   */
  var EFFECT_DESCRIPTIONS = {
    burnout:
      "Выгорание (дебафф, 15 реальных минут): усталость копится на 10% быстрее. " +
      "Выдаётся разово при входе стресса в критическую зону (выше 50%), " +
      "разом снимает 5% здоровья, энергии и жидкости.",
    relaxation:
      "Расслабление (бафф, 15 реальных минут): +15% опыта, " +
      "накопление стресса замедлено на своё значение процента.",
    rested:
      "Отдохнувший (бафф, 25 реальных минут): +25% опыта, " +
      "накопление стресса замедлено на своё значение процента. " +
      "Заменяет «Расслабление», а не складывается с ним.",
    bull:
      "Бык (бафф, 2 игровых дня): прекращает ЛЮБОЕ негативное накопление " +
      "(истощение и кумулятивный стресс). Метаболизм расходует энергию и " +
      "жидкость как при сниженном, восстановление здоровья +2% за 10 минут, " +
      "полученный форсаж умножается на 1,5.",
    strong_bones:
      "Крепкие кости (бафф, 3 игровых дня): даётся за регулярное употребление " +
      "молочных продуктов; полностью исключает риск переломов.",
    power_surge:
      "Прилив сил (бафф, 15 реальных минут): блокирует любое изменение шкал — " +
      "значения просто замораживаются.",
    sorbent:
      "Сорбент (бафф, 4 игровых часа): связывает токсины, ускоряя очищение " +
      "организма.",
    analgesia:
      "Обезболивание (бафф, 2 игровых часа): снимает боль, поддержка здоровья.",
    analgesic_overuse:
      "Злоупотребление обезболивающими (дебафф, 6 игровых часов): " +
      "начинается после 4-го приёма — препарат перестаёт помогать и добавляет стресс.",
    drowsiness:
      "Сонливость (дебафф, 2 игровых часа): побочный эффект успокоительных " +
      "и адаптогенов.",
    nicotine_rebound:
      "Никотиновый откат (дебафф, 60 реальных минут): расплата за сигарету — " +
      "стресс возвращается после краткого облегчения.",
    alcohol_aftereffect:
      "Последействие алкоголя (дебафф, 3 игровых часа): обезвоживание " +
      "и разбитость после выпивки.",
    unkempt:
      "Неопрятный (дебафф, гигиена ниже 50%): стресс копится в 1,5 раза быстрее, " +
      "обаяние снижено на 1. Снимается полноценным сном или в отпуске.",
    bum:
      "Бомж (дебафф, гигиена ниже 20%): стресс копится в 2,5 раза быстрее, " +
      "обаяние снижено на 3, устойчивость не поднимается выше 50%. " +
      "Снимается полноценным сном или в отпуске.",
    thirst:
      "Жажда (дебафф, жидкость ниже 1500 мл): копится истощение жидкости, " +
      "стресс растёт на 0,5% в минуту, метаболизм теряет 1% за 10 игровых " +
      "минут. Снимается, как только в пищеварении начинает усваиваться что-то " +
      "с жидкостью.",
    dehydration:
      "Обезвоживание (дебафф, жидкость ниже 500 мл): метаболизм теряет 2% " +
      "за 10 игровых минут, стресс растёт на 25% быстрее, устойчивость падает " +
      "вместе с расходом жидкости, действия с физической нагрузкой недоступны " +
      "(кроме движения). Снимается при положительной динамике жидкости.",
    caffeine_overuse:
      "Кофеин — перенапряжение (дебафф): слишком много кофеина за сутки; " +
      "сердце и нервы на пределе.",
    caffeine_excess:
      "Кофеин — избыток (дебафф): превышена комфортная доза кофеина.",
    caffeine_jitter:
      "Кофеиновая возбудимость (дебафф): тремор и тревожность от кофеина.",
    late_caffeine:
      "Поздний кофеин (дебафф): кофеин выпит слишком поздно — сон будет хуже.",
    caffeine_withdrawal:
      "Кофеиновый откат (дебафф): организм требует привычную дозу кофеина."
  };

  function effectDescription(id) {
    var key = String(id == null ? "" : id).toLowerCase();
    return EFFECT_DESCRIPTIONS[key] || "";
  }

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
      displayPercent: percent + unitsToPercent(overchargeUnits),
      physical: physicalValue(vitals, scale.physicalKey, scale),
      maxPhysical: physicalValue(vitals, scale.physicalMaxKey, scale),
      physicalUnit: scale.physicalUnit || ""
    };
  }

  /**
   * ФИЗИЧЕСКАЯ величина шкалы (ккал или мл) для показа игроку.
   *
   * Домен присылает её готовым полем (`energyKilocalories` и т.п.) — Web НЕ
   * пересчитывает её сам, иначе второе правило пересчёта разошлось бы с первым.
   * Если размерности у шкалы нет (стресс, усталость, здоровье) или поле не
   * пришло, возвращается null: такой шкале показывается процент, а не выдуманное
   * «ккал» у стресса. Потолок размерности (5000 ккал, 3000 мл) тоже приходит из
   * домена — в JavaScript его числа не дублируются.
   */
  function physicalValue(vitals, fieldKey, scale) {
    if (!fieldKey || !scale.physicalUnit)
      return null;
    var value = Number(vitals[fieldKey]);
    return Number.isFinite(value) ? value : null;
  }

  /**
   * Подпись значения шкалы.
   *
   * У шкал с физической размерностью показывается ФИЗИЧЕСКОЕ число (2750 из
   * 5000 ккал), у остальных — процент. Автор просил именно это: потолок энергии
   * 5000 и жидкости 3000, а не внутренние 10000.
   */
  function valueLabel(scale, values) {
    if (values.maxPhysical != null) {
      return Math.round(values.physical) +
        " / " +
        Math.round(values.maxPhysical) +
        " " +
        values.physicalUnit;
    }
    return Math.round(
      values.displayPercent == null
        ? values.percent
        : values.displayPercent) + "%";
  }

  /**
   * Треугольник динамики слева от процентов шкалы.
   *
   * Вверх — значение растёт, вниз — падает; lime, если это ПОЛЕЗНО игроку, и
   * красный, если во вред. Направление берётся из домена (`conditionRates`),
   * поэтому треугольник не может разойтись с механикой: вторая таблица правил
   * в Web неизбежно показывала бы стрелку вверх там, где шкала падает.
   *
   * Пока скорости нет (шкала не двигается или снимок ещё не пришёл), значка
   * тоже нет: пустой треугольник читался бы как «динамики нет — и это нормально».
   */
  function dynamicsMarkup(scale, snapshot) {
    var rates = (snapshot && snapshot.conditionRates) || {};
    var rate = Number(rates[scale.key]);
    if (!Number.isFinite(rate) || Math.abs(rate) < FLAT_RATE_EPSILON)
      return { markup: "", text: "" };

    var rising = rate > 0;
    var good = GOOD_DIRECTION[scale.key] || 0;
    var positive = good !== 0 && rising === (good > 0);
    var arrow = rising ? "▲" : "▼";
    var meaning = positive ? "полезная динамика" : "вредная динамика";
    var direction = rising ? "растёт" : "падает";
    var text = direction + " (" + (rate > 0 ? "+" : "") +
      Math.round(rate * 100) / 100 + " ед./мин) — " + meaning;

    return {
      markup: "<span class='vitalTrend " +
        (positive ? "vitalTrendGood" : "vitalTrendBad") +
        "' data-vital-trend='" + scale.key +
        "' title='" + escapeHtml(text) + "'>" + arrow + "</span>",
      text: text
    };
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
    // Строка «Реальное значение» показывает ФИЗИЧЕСКУЮ величину, если она есть
    // (2750/5000 ккал, 2100/3000 мл), и процент — всегда. Внутренние единицы
    // хранения в подсказку больше не попадают: автор видел в них «потолок
    // 10000» и справедливо считал это ошибкой.
    var physical =
      values.maxPhysical != null
        ? Math.round(values.physical) +
          "/" +
          Math.round(values.maxPhysical) +
          " " +
          values.physicalUnit +
          " (" +
          Math.round(values.percent) +
          "%)"
        : Math.round(values.actual) +
          "/" +
          Math.round(values.maximum) +
          " ед. (" +
          Math.round(values.percent) +
          "%)";
    var lines = [
      scale.label,
      "Реальное значение: " + physical,
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

  /**
   * Геометрия дорожки шкалы без внешней обёртки `.dualStat`.
   *
   * Вынесена из `barMarkup`, чтобы монитор показателей рисовал шкалу ВНУТРИ
   * карточки той же геометрией и теми же цветами, что и сайдбар. Два независимых
   * рисунка одной шкалы разошлись бы: «двойная шкала» перестала бы быть двойной
   * ровно там, где её и сверяют.
   */
  function barTrack(scale, values) {
    var cumulativeClass = cumulativeClassFor(scale);
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
          cumulativeClass +
          "' style=\"width:" +
          Math.min(100, overchargePercent) +
          "%\"></div>"
        : "";

    return "<div class='dualBar " + scale.key + "'>" +
      "<div class='dualBarSoft' style=\"width:" +
        values.soft +
        "%;background:" +
        scale.color +
        "\"></div>" +
      cumulative +
      overcharge +
    "</div>";
  }

  function barMarkup(scale, values, snapshot) {
    var surgeClass =
      hasEffect(snapshot, "power_surge")
        ? " powerSurge"
        : "";
    var tooltipText =
      escapeHtml(tooltip(scale, values, snapshot));
    var dynamics = dynamicsMarkup(scale, snapshot);

    return "<div class='dualStat" + surgeClass +
      "' data-vital-scale='" + scale.key +
      "' data-game-tooltip=\"" +
      tooltipText + "\">" +
      "<div class='dualStatHead'>" +
        "<span>" + escapeHtml(scale.label) + "</span>" +
        // Треугольник стоит ВНУТРИ общего span с процентами, а именно ПЕРЕД
        // ними: автор просил значок слева от процентов у КАЖДОЙ шкалы.
        "<span>" + dynamics.markup + valueLabel(scale, values) + "</span>" +
      "</div>" +
      barTrack(scale, values) +
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

    // У КАЖДОГО поля своя кнопка «←»: она применяет только это значение.
    //
    // Прежняя общая кнопка «Применить» отправляла все поля сразу, и правка
    // одного показателя перезаписывала остальные значениями из формы — то есть
    // затирала всё, что успело измениться в мире после отрисовки.
    function field(key, label, units) {
      return "<div class='field'>" +
        "<label>" + label + "</label>" +
        "<div class='fieldRow'>" +
        "<input data-t='" + key +
        "' value='" +
        Math.round(unitsToPercent(units)) +
        "'>" +
        "<button class='fieldApplyButton' type='button' data-apply-t='" +
        key +
        "' title='Применить только это значение'>←</button>" +
        "</div></div>";
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
    EFFECT_DESCRIPTIONS: EFFECT_DESCRIPTIONS,
    escapeHtml: escapeHtml,
    scaleByKey: scaleByKey,
    unitsToPercent: unitsToPercent,
    formatRate: formatRate,
    rateFor: rateFor,
    read: read,
    tooltip: tooltip,
    dynamicsMarkup: dynamicsMarkup,
    barTrack: barTrack,
    valueLabel: valueLabel,
    barMarkup: barMarkup,
    markup: markup,
    fieldsMarkup: fieldsMarkup,
    effectDescription: effectDescription,
    attachTooltips: attachTooltips
  };
})(typeof window !== "undefined" ? window : this);
