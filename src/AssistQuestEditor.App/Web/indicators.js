/**
 * Окно «Монитор показателей».
 *
 * Показывает КАЖДУЮ шкалу состояния: текущее значение / максимум = формула,
 * то есть перечень всех факторов, влияющих на скорость её изменения. Значения
 * обновляются в реальном времени тем же снимком и live_state, что и карта, —
 * окно ничего не считает само по правилам мира, а читает присланное Хостом.
 *
 * Почему разбор формул живёт в Web, а не в домене. Домен — источник ИСТИНЫ о
 * числах (он их и считает), а этот экран объясняет игроку, ИЗ ЧЕГО эти числа
 * складываются. Формулировки — производная от правил, и держать их рядом с
 * числами значило бы менять домен при каждой правке текста.
 *
 * Значения шкал приходят в ЕДИНИЦАХ 0..10000 (как и везде): перевод в проценты
 * делает AssistVitals, чтобы окно не заводило второй шкалы.
 */
(function (global) {
  "use strict";

  var body = null;
  var snapshot = null;
  var itemCatalog = [];
  var simulationRunning = false;
  var simulationPaused = false;
  var lastMarkup = "";

  /**
   * Ключ шкалы, которую надо выделить (клик по имени показателя в журнале).
   *
   * Состояние ОКНА, а не снимка: Симулятор присылает его отдельным сообщением
   * <c>{ type: "highlight", key: "…" }</c>, и оно живёт, пока окно открыто.
   * Через снимок выделять нельзя: следующий же live_state (четыре раза в секунду)
   * сбрасывал бы рамку раньше, чем игрок успел бы её заметить.
   */
  var highlightKey = "";

  /** Через сколько снять выделение. Пока оно горит, блок не должен «убегать»
   *  от игрока, поэтому время с запасом на прочтение. */
  var HIGHLIGHT_MS = 4000;
  var highlightTimer = null;

  /**
   * «Полезное направление» шкалы: +1 — игроку выгодно, чтобы значение росло,
   * −1 — чтобы падало. Знак нужен, потому что «динамика вверх» для энергии и
   * для усталости означает ПРОТИВОПОЛОЖНОЕ по смыслу, и цвет строки обязан
   * следовать смыслу, а не арифметике.
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

  /**
   * Шкалы истощения. Истощение НЕ имеет собственной скорости в домене — оно
   * начисляется рывками по факту перерасхода, поэтому его динамику измеряем
   * САМИ: сравниваем текущее значение с образцом из скользящего окна.
   */
  var EXHAUSTION_SCALES = [
    { field: "cumulativeEnergy", label: "Истощение энергии" },
    { field: "cumulativeHydration", label: "Истощение жидкости" },
    { field: "cumulativeFatigue", label: "Истощение усталости" },
    { field: "cumulativeStress", label: "Кумулятивный стресс" }
  ];

  /**
   * В каком блоке шкалы показывать каждую запись истощения.
   *
   * Требование автора: отдельного блока «Истощение» быть не должно — запись
   * истощения живёт в блоке СВОЕЙ шкалы. Иначе про истощение энергии игрок
   * читает в одном месте, а про саму энергию — в другом, и связь между числом и
   * его причиной теряется.
   */
  var EXHAUSTION_BY_SCALE = {
    energy: ["cumulativeEnergy"],
    hydration: ["cumulativeHydration"],
    fatigue: ["cumulativeFatigue"],
    stress: ["cumulativeStress"]
  };

  /** Окно измерения: короче — шум от тиков, длиннее — «залипшая» оценка. */
  var EXHAUSTION_WINDOW_MS = 6000;

  var exhaustionSamples = [];

  function v() { return global.AssistVitals; }

  function escapeHtml(value) {
    return v()
      ? v().escapeHtml(value)
      : String(value == null ? "" : value);
  }

  function num(value) {
    var number = Number(value);
    return Number.isFinite(number) ? number : 0;
  }

  function percent(units) {
    return Math.round(num(units) / 100);
  }

  function fmt(value, digits) {
    var number = Number(value);
    if (!Number.isFinite(number)) return "0";
    var rounded = number.toFixed(digits == null ? 0 : digits);
    return rounded.replace(/\.0+$/, "").replace(/(\.\d*?)0+$/, "$1");
  }

  var CONDITIONS = function () { return (snapshot && snapshot.conditions) || {}; };
  var VITALS = function () { return (snapshot && snapshot.playerVitals) || {}; };

  function effects() {
    return Array.isArray(CONDITIONS().effects) ? CONDITIONS().effects : [];
  }

  function hasEffect(id) {
    var key = String(id).toLowerCase();
    return effects().some(function (effect) {
      return String(effect.id || "").toLowerCase() === key &&
        num(effect.remainingRealSeconds) > 0;
    });
  }

  function effectNames(isDebuff) {
    return effects()
      .filter(function (effect) {
        return !!effect.isDebuff === !!isDebuff &&
          num(effect.remainingRealSeconds) > 0;
      })
      .map(function (effect) {
        return effect.name || effect.id || "эффект";
      });
  }

  /** Стресс в процентах с учётом кумулятивной части — как в домене. */
  function totalStressPercent() {
    var units = num(CONDITIONS().stress) + num(CONDITIONS().cumulativeStress);
    return Math.min(100, percent(units));
  }

  function stressFactor() {
    var value = 1 - totalStressPercent() / 100;
    return Math.max(0, Math.min(1, value));
  }

  /** Замедление накопления стресса активными баффами, в процентах. */
  function stressSlowdownPercent() {
    return effects()
      .filter(function (effect) {
        return !effect.isDebuff && num(effect.remainingRealSeconds) > 0;
      })
      .reduce(function (sum, effect) {
        return sum + num(effect.stressAccumulationSlowdownPercent);
      }, 0);
  }

  function fatigueAccrualMultiplier() {
    var cumulativePercent = percent(CONDITIONS().cumulativeFatigue);
    var steps = Math.floor(cumulativePercent / 5);
    // База и шаг — те же числа, что в домене (×3 и +0,5 за каждые 5%).
    return 3 + steps * 0.5;
  }

  /**
   * Пункт переваривания для раздела шкалы (энергия или жидкость).
   *
   * Пока в виртуальном желудке есть неусвоенное, автор просил ПОКАЗЫВАТЬ этот
   * пункт в соответствующем разделе мониторинга: название съеденного, эффект и
   * его КОЛИЧЕСТВО. Прирост красится в lime, убыль — в красную: цвет следует
   * смыслу воздействия, а не знаку числа.
   *
   * Возвращает пустую строку, если по этой шкале переваривать нечего: пустой
   * пункт выглядел бы как поломка данных.
   */
  function digestionRow(key) {
    var stomach = CONDITIONS().stomach || {};
    var remaining = num(key === "energy"
      ? stomach.energyRemaining
      : stomach.hydrationRemaining);
    var ratePerSecond = num(key === "energy"
      ? stomach.energyPerGameSecond
      : stomach.hydrationPerGameSecond);

    if (remaining <= 0)
      return "";

    var name = consumedItemName();
    var perMinute = ratePerSecond * 60;
    var perHourPercent = ratePerSecond * 3600 / 100;
    var remainingPercent = remaining / 100;
    var className = perMinute > 0
      ? "indicatorDynamicGood"
      : "indicatorDynamicBad";
    var direction = perMinute > 0 ? "+" : "";

    return "<div class='indicatorFact indicatorDigestionRow' data-digestion='" + key + "'>" +
      "• <strong>" + escapeHtml(name) + "</strong>" +
      " переваривается: " +
      "<span class='" + className + "'>" + direction + fmt(perMinute, 1) + " ед./мин (" +
      direction + fmt(perHourPercent, 2) + "% в час)</span>" +
      ", осталось " + fmt(remainingPercent, 2) + "% (" +
      Math.round(remaining) + " ед., ~" +
      fmt(remaining / Math.max(0.000001, ratePerSecond) / 60, 1) + " игровых мин)" +
    "</div>";
  }

  /** Название последнего съеденного предмета из каталога снимка. */
  function consumedItemName() {
    var id = String(CONDITIONS().lastConsumedItemId || "");
    if (!id) return "съеденное";

    var found = (itemCatalog || []).find(function (item) {
      return String(item.id || "").toLowerCase() === id.toLowerCase();
    });

    return (found && (found.name || found.id)) || id;
  }

  function resiliencePercent() {
    var value = percent(VITALS().resilience);
    return hasEffect("bum") ? Math.min(50, value) : value;
  }

  function metabolismPercent() {
    return percent(VITALS().metabolism);
  }

  function metabolismFactor() {
    var value = metabolismPercent();
    var factor = value < 45 ? 0.9 : value >= 75 ? 1.25 : 1;
    return hasEffect("bull") ? factor * 0.5 : factor;
  }

  function isMoving() {
    return num(snapshot && snapshot.player && snapshot.player.speedKmh) > 0.001;
  }

  function gameHour() {
    var daylight = snapshot && snapshot.daylight;
    var text = (daylight && (daylight.gameClockLabel || daylight.gameTimeLabel)) || "";
    var match = String(text).match(/^(\d{1,2}):/);
    return match ? Number(match[1]) : 12;
  }

  function nightFatigueFactor() {
    var hour = gameHour();
    if (hour >= 6 && hour < 18) return 1;
    if (hour >= 18 && hour < 22) return 1.5;
    return 2.5;
  }

  function rateLine(key) {
    var rates = (snapshot && snapshot.conditionRates) || {};
    if (!Number.isFinite(Number(rates[key]))) return "";
    var perMinute = Number(rates[key]);
    var perHour = perMinute * 60;
    return "сейчас: " + (perMinute > 0 ? "+" : "") + fmt(perMinute, 1) +
      " ед./мин (" + (perHour > 0 ? "+" : "") + fmt(perHour, 1) + " ед./час)";
  }

  /**
   * Динамика шкалы: направление, скорость и КЛАСС строки.
   *
   * Скорость берём из домена (`conditionRates`), поэтому вторая таблица правил
   * не заводится: подсветка не может разойтись с механикой. «Позитивная
   * динамика» — это движение шкалы в ПОЛЕЗНУЮ игроку сторону (энергия растёт
   * или усталость падает), и только она красится в lime; движение во вред
   * красное; стояние на месте — нейтральное.
   */
  function scaleDynamics(key) {
    var rates = (snapshot && snapshot.conditionRates) || {};
    var rate = Number(rates[key]);
    var good = GOOD_DIRECTION[key] || 0;

    if (!Number.isFinite(rate) || Math.abs(rate) < FLAT_RATE_EPSILON) {
      return {
        text: "без изменений",
        detail: "0 ед./мин",
        className: "indicatorDynamicFlat"
      };
    }

    var rising = rate > 0;
    var positive = good !== 0 && rising === (good > 0);

    return {
      text: rising ? "растёт" : "падает",
      detail: (rate > 0 ? "+" : "") + fmt(rate, 1) + " ед./мин (" +
        (rate * 60 > 0 ? "+" : "") + fmt(rate * 60, 1) + " ед./час)",
      className: positive
        ? "indicatorDynamicGood"
        : "indicatorDynamicBad"
    };
  }

  /** Образец снимка для расчёта динамики истощения. */
  function exhaustionSample() {
    var conditions = CONDITIONS();
    var sample = { at: Date.now() };
    EXHAUSTION_SCALES.forEach(function (scale) {
      sample[scale.field] = num(conditions[scale.field]);
    });
    return sample;
  }

  /**
   * Запоминает текущее значение истощения в скользящем окне.
   *
   * Истощение начисляется рывками, и мгновенной «скорости» у него нет, поэтому
   * динамику получаем сравнением с образцом ~6 реальных секунд назад. Образцы
   * копятся в модуле (окно времени), иначе короче окно не измерить.
   */
  function rememberExhaustion() {
    exhaustionSamples.push(exhaustionSample());
    var cutoff = Date.now() - EXHAUSTION_WINDOW_MS;
    exhaustionSamples = exhaustionSamples.filter(function (item) {
      return item.at >= cutoff;
    });
  }

  /**
   * Динамика одного истощения: изменение за окно, переведённое в единицы в час.
   * Пока окно не набралось, честно сообщаем «наблюдаем», а не выдумываем ноль.
   */
  function exhaustionDynamics(scale) {
    var current = num(CONDITIONS()[scale.field]);
    var oldest = exhaustionSamples.length ? exhaustionSamples[0] : null;
    var spanMs = oldest ? Date.now() - oldest.at : 0;

    if (!oldest || spanMs < 1500) {
      return {
        current: current,
        ratePerHour: 0,
        text: "наблюдаем…",
        className: "indicatorDynamicFlat",
        delta: 0
      };
    }

    var delta = current - num(oldest[scale.field]);
    var perHour = delta / spanMs * 3600000;

    if (Math.abs(perHour) < FLAT_RATE_EPSILON) {
      return {
        current: current,
        ratePerHour: 0,
        text: "без изменений",
        className: "indicatorDynamicFlat",
        delta: delta
      };
    }

    return {
      current: current,
      ratePerHour: perHour,
      text: perHour > 0 ? "растёт" : "снижается",
      // Истощение — всегда вред игроку, поэтому рост красный, а снижение
      // (отпуск, «Бык») — полезное, зелёное.
      className: perHour > 0 ? "indicatorDynamicBad" : "indicatorDynamicGood",
      delta: delta
    };
  }

  /**
   * Строки истощения ОДНОЙ шкалы, для показа в её собственном блоке.
   *
   * Требование автора: отдельного блока «Истощение» нет. Если истощения по шкале
   * нет — строка всё равно показывается, но приглушённым белым: игрок должен
   * видеть, что механизм есть и что он в норме, а не гадать, почему раздела нет.
   * Если истощение есть — вся строка красная.
   *
   * Динамику меряем только когда истощение действительно накоплено: у нулевого
   * истощения скорости нет, и «наблюдаем…» на каждой шкале было бы шумом.
   */
  function exhaustionRowsFor(key) {
    var fields = EXHAUSTION_BY_SCALE[key] || [];

    return fields.map(function (field) {
      var scale = EXHAUSTION_SCALES.filter(function (item) {
        return item.field === field;
      })[0];
      if (!scale) return "";

      var units = num(CONDITIONS()[field]);
      var label = escapeHtml(scale.label);
      var value = Math.round(units) + "/" + Math.round(PlayerScaleMaximum()) +
        " (" + percent(units) + "%)";

      if (units <= 0) {
        // Норма: строка есть, но приглушена — белым и без цвета тревоги.
        return "<div class='indicatorFact indicatorExhaustionRow indicatorExhaustionNone' " +
          "data-exhaustion='" + escapeHtml(field) + "'>" +
          "• " + label + ": <strong>нет</strong> — шкала в норме." +
        "</div>";
      }

      var dynamics = exhaustionDynamics(scale);
      var rateText = (dynamics.ratePerHour > 0 ? "+" : "") +
        fmt(dynamics.ratePerHour, 1) + " ед./час";

      return "<div class='indicatorFact indicatorExhaustionRow indicatorExhaustionPresent' " +
        "data-exhaustion='" + escapeHtml(field) + "'>" +
        "• " + label + ": <strong>" + value + "</strong>" +
        " · <strong>Текущая динамика</strong>: " +
        "<span class='" + dynamics.className + "'>" +
        escapeHtml(dynamics.text) + " (" + rateText + ")</span>" +
      "</div>";
    }).join("");
  }

  function PlayerScaleMaximum() {
    var maximum = num(VITALS().maxHealth);
    return maximum > 0 ? maximum : 10000;
  }

  /**
   * Факторы скорости изменения шкалы.
   *
   * Возвращает массив строк: базовая скорость, затем каждый множитель, который
   * домен применяет. Строки описывают и то, что СЕЙЧАС действует (например,
   * «ночь ×2,5»), и то, что могло бы (например, «если усталость ≥ 80% …»).
   */
  function factorsFor(key) {
    var lines = [];
    var activeBuffs = effectNames(false);
    var activeDebuffs = effectNames(true);

    if (key === "health") {
      var energyPercent = percent(VITALS().energy);
      var hydrationPercent = percent(VITALS().hydration);
      var regen = energyPercent > 70 && hydrationPercent > 70
        ? 1.5
        : energyPercent > 35 && hydrationPercent > 35
          ? 0.5
          : 0;

      lines.push("База: +" + fmt(regen, 1) + "% за 15 игровых минут " +
        "(энергия " + energyPercent + "%, жидкость " + hydrationPercent + "%: " +
        (regen > 0 ? "выше 35%" : "ниже 35% — восстановления нет") + ").");
      lines.push("Восстановление тратит запасы: 1 единица здоровья = 1 энергии и 2 жидкости. " +
        "Нет энергии или жидкости — нет и восстановления.");
      if (totalStressPercent() > 0) lines.push("Любой стресс замедляет восстановление ×0,75.");
      if (hasEffect("bull")) lines.push("«Бык» ускоряет восстановление ×1,5.");
      lines.push("При метаболизме ниже 45% восстановление ×0,75; от 75% — ×1,5.");
      lines.push("Истощение энергии и жидкости: −(суммарный % истощения ÷ 6) за 15 игровых минут.");
      if (num(CONDITIONS().cumulativeEnergy) > 0 || num(CONDITIONS().cumulativeHydration) > 0) {
        var drain = (percent(CONDITIONS().cumulativeEnergy) +
          percent(CONDITIONS().cumulativeHydration)) / 6;
        lines.push("Сейчас: истощение " + percent(CONDITIONS().cumulativeEnergy) +
          "% + " + percent(CONDITIONS().cumulativeHydration) +
          "% → −" + fmt(drain, 2) + "% за 15 игровых минут.");
      }
      lines.push("Истощение снижает МАКСИМУМ шкалы (не даёт восстановиться выше).");
    } else if (key === "energy" || key === "hydration") {
      // Числа взяты из констант домена: расход — это полная шкала за 30 игровых
      // часов (энергия) и за 90 (жидкость). Прежние «4 часа» и «2 часа» были из
      // старой модели, где пища не успевала перекрыть расход, и динамика
      // оставалась красной даже после плотного обеда.
      var hours = key === "energy"
        ? "30 игровых часов"
        : "90 игровых часов";
      var demand = key === "energy" ? 100 / 30 : 100 / 90;
      var scale = key === "energy"
        ? "5000 ккал (100% шкалы)"
        : "15 000 мл (100% шкалы)";
      lines.push("База: 100% за " + hours + " (в покое). 100% шкалы — это " + scale + ".");
      lines.push("Метаболизм: ниже 45% расход ×0,9; от 75% расход ×1,25. Сейчас ×" +
        fmt(metabolismFactor(), 2) + (hasEffect("bull") ? " (за счёт «Быка» ÷2)" : "") + ".");
      lines.push("Во сне расход ×1/3.");
      lines.push("Истощение по этой шкале копится, когда значение падает ниже 1%" +
        (key === "hydration" ? " — в двойном размере (2 единицы на 1)" : " — 1:1") + ".");
      lines.push("Форсаж расходуется первым: пока есть запас, обычная шкала не падает.");
      // Модель пищеварения: желудок на 1 литр, усвоение идёт с ПОСТОЯННОЙ
      // скоростью, поэтому время до полного переваривания зависит от размера
      // порции — мелкая еда переваривается быстро, крупная долго. Без этой
      // строки игрок видел бы «полный обед на 2 часа» и не понимал, откуда срок.
      lines.push("Пища приходит из желудка: он вмещает 1 л и опорожняется за 2 игровых часа " +
        "(полная еда даёт 100% энергии за это время). Жидкость усваивается по 1,35 л в час, " +
        "поэтому порция воды занимает желудок на столько часов, сколько в ней литров, " +
        "делённых на 1,35.");
      lines.push("Сейчас: расход " + fmt(demand * metabolismFactor() * (hasEffect("bull") ? 0.5 : 1), 2) +
        "% в час (в движении).");
    } else if (key === "fatigue") {
      lines.push("Рост в движении: 100% за 18 игровых часов + 100% за 900 км.");
      lines.push("Время суток: 06–18 ×1,0, 18–22 ×1,5, ночь ×2,5. Сейчас ×" +
        fmt(nightFatigueFactor(), 1) + ".");
      if (hasEffect("burnout")) lines.push("«Выгорание» ускоряет рост усталости ×1,1.");
      lines.push("Восстановление в покое: 100% за 9 игровых часов, замедлено на процент стресса.");
      lines.push("Усталость ≥ 80%: копится истощение усталости каждый игровой час — " +
        "сейчас коэффициент ×" + fmt(fatigueAccrualMultiplier(), 2) +
        " (3,0 базово + 0,5 за каждые 5% истощения).");
      if (hasEffect("bull")) lines.push("«Бык» полностью прекращает накопление истощения.");
      if (isMoving()) lines.push("Сейчас: движение — усталость растёт.");
    } else if (key === "stress") {
      lines.push("Каждый 1% кумулятивной усталости даёт +0,5% стресса в игровой час.");
      lines.push("Стресс выше 50%: +1% кумулятивного стресса каждый игровой час " +
        "и дебафф «Выгорание» (разово).");
      lines.push("Устойчивость замедляет получение стресса на свой процент ÷ 4. " +
        "Сейчас ÷4 от " + resiliencePercent() + "%.");
      if (hasEffect("bum")) lines.push("«Бомж» ускоряет стресс ×2,5.");
      else if (hasEffect("unkempt")) lines.push("«Неопрятный» ускоряет стресс ×1,5.");
      if (stressSlowdownPercent() > 0) {
        lines.push("Баффы отдыха замедляют накопление стресса на " +
          fmt(stressSlowdownPercent(), 0) + "%.");
      }
      if (isMoving()) lines.push("В движении скорость ×1,5.");
      lines.push("Кумулятивный стресс снимается только отпуском.");
    } else if (key === "resilience") {
      lines.push("Норма — 60%: шкала плавно сходится к ней (0,5% за 15 игровых минут).");
      if (hasEffect("bum")) {
        lines.push("«Бомж»: цель — 50%, сходится к ней на 1% за 15 игровых минут.");
      }
      lines.push("Устойчивость замедляет негативные эффекты и стресс (÷4).");
      lines.push("Она же (÷2) даёт шанс не получить очередную порцию истощения.");
    } else if (key === "metabolism") {
      lines.push("Норма — 60%: шкала сходится к ней на 1% в игровой час.");
      lines.push("Каждая шкала истощения снижает метаболизм на 2% за 15 игровых минут.");
      var scales = (num(CONDITIONS().cumulativeStress) > 0 ? 1 : 0) +
        (num(CONDITIONS().cumulativeFatigue) > 0 ? 1 : 0) +
        (num(CONDITIONS().cumulativeHydration) > 0 ? 1 : 0) +
        (num(CONDITIONS().cumulativeEnergy) > 0 ? 1 : 0);
      lines.push("Сейчас активных шкал истощения: " + scales +
        " → −" + fmt(scales * 2, 1) + "% за 15 игровых минут.");
      lines.push("Ниже 45%: расход медленнее ×0,9. От 75%: расход ×1,25 и ускоренное избавление от негатива.");
    }

    if (key !== "stress") {
      if (activeBuffs.length) lines.push("Баффы: " + activeBuffs.join(", ") + ".");
      if (activeDebuffs.length) lines.push("Дебаффы: " + activeDebuffs.join(", ") + ".");
    }

    var rate = rateLine(key);
    if (rate) lines.push(rate);

    return lines;
  }

  /**
   * Строка-фактор.
   *
   * Истощение здесь БОЛЬШЕ НЕ помечается: у каждой шкалы есть своя строка
   * истощения (см. exhaustionRowsFor), и это единственный владелец признака.
   * Помечать ещё и упоминания в формулах значило бы красить красным половину
   * блока, и по цвету нельзя было бы понять, ГДЕ именно истощение накоплено.
   */
  function factorRow(line) {
    return "<div class='indicatorFact'>• " + escapeHtml(line) + "</div>";
  }

  function scaleRow(scale) {
    var values = v().read(snapshot, scale);
    var current = Math.round(values.actual);
    var maximum = Math.round(values.maximum);
    var lines = factorsFor(scale.key);
    var dynamics = scaleDynamics(scale.key);
    // Пункт переваривания — только у шкал, которые желудок наполняет.
    var digestion = scale.key === "energy" || scale.key === "hydration"
      ? digestionRow(scale.key)
      : "";
    // Строки истощения идут ПОСЛЕ динамики и переваривания, но ПЕРЕД факторами:
    // это состояние шкалы, а не объяснение формулы.
    var exhaustion = exhaustionRowsFor(scale.key);

    // Выделение блока по клику в журнале: имя показателя в отчёте — ссылка, и
    // без рамки игрок после клика искал бы нужный блок среди восьми.
    var highlighted = scale.key === highlightKey ? " indicatorHighlight" : "";

    return "<section class='indicatorCard" + highlighted + "' data-scale-key='" +
      escapeHtml(scale.key) + "'>" +
      "<div class='indicatorTitle'>" +
        "<span class='indicatorName'>" + escapeHtml(scale.label) + "</span>" +
        "<span class='indicatorValue'>" + current + " / " + maximum +
          " = " + Math.round(values.displayPercent) + "%</span>" +
      "</div>" +
      "<div class='indicatorFormula'>" +
        // Жирная строка «Текущая динамика» идёт ПЕРВОЙ: это ответ на главный
        // вопрос игрока («что с шкалой происходит СЕЙЧАС»), а формулы ниже —
        // объяснение, почему так.
        "<div class='indicatorFact indicatorDynamicRow'>" +
          "<strong>Текущая динамика</strong>: " +
          "<span class='" + dynamics.className + "'>" +
            escapeHtml(dynamics.text) + "</span> — " +
          escapeHtml(dynamics.detail) +
        "</div>" +
        digestion +
        exhaustion +
        lines.map(factorRow).join("") +
      "</div>" +
    "</section>";
  }

  function render() {
    if (!body || !snapshot || !v()) return;

    // Разделов «Истощение» БОЛЬШЕ НЕТ: записи о нём уехали в блоки своих шкал
    // (см. exhaustionRowsFor), поэтому здесь остаются только шкалы, пункт
    // переваривания и активные эффекты.
    var markup = v().SCALES.map(scaleRow).join("") +
      effectsSection();

    // Замена узлов сбрасывает прокрутку и снимает выделение. Пересборка идёт
    // только при фактическом изменении текста, но выделение — как раз повод
    // пересобрать: без этого клик из журнала не подсветил бы ничего.
    if (markup === lastMarkup) return;
    lastMarkup = markup;
    body.innerHTML = markup;

    // Блок с выделением доводим до экрана: он может быть значительно ниже.
    if (highlightKey) scrollToHighlight();
  }

  /**
   * Прокручивает список к выделенному блоку и ставит его на середину окна.
   *
   * Отдельная функция, потому что пересборка разметки и прокрутка — разные
   * поводы: первый приходит с каждым изменившимся снимком, вторая нужна только
   * при новом выделении.
   */
  function scrollToHighlight() {
    var node = body.querySelector("[data-scale-key='" + highlightKey + "']");
    if (!node || !node.scrollIntoView) return;

    node.scrollIntoView({ block: "center" });
  }

  /**
   * Ставит выделение на шкалу и через время снимает его.
   *
   * Таймер ОДИН на окно: повторный клик по другому показателю не должен
   * оставлять висеть таймер от предыдущего — иначе старое выделение снималось бы
   * уже после нового, и рамка исчезала бы раньше времени.
   */
  function setHighlight(key) {
    highlightKey = String(key || "");

    if (highlightTimer) global.clearTimeout(highlightTimer);

    if (!highlightKey) {
      render();
      return;
    }

    render();

    highlightTimer = global.setTimeout(function () {
      highlightTimer = null;
      highlightKey = "";
      render();
    }, HIGHLIGHT_MS);
  }

  function effectsSection() {
    var list = effects();
    if (!list.length) return "";

    var buffs = list.filter(function (effect) { return !effect.isDebuff; });
    var debuffs = list.filter(function (effect) { return effect.isDebuff; });

    /**
     * Имя эффекта — КЛИКАБЕЛЬНОЕ, с подсказкой и цветом по знаку.
     *
     * Требования автора: баффы окрашивать позитивным цветом, дебаффы —
     * негативным; название кликабельно и с тултипом; клик открывает окно перков
     * и подсвечивает ИМЕННО тот пункт, по которому щёлкнули.
     *
     * Подсветка нужна потому, что список перков длинный: без неё игрок после
     * клика оказывается в начале окна и сам ищет, куда именно он нажал.
     */
    function chip(effect) {
      var name = String(effect.name || effect.id || "");
      var description = v() && v().effectDescription
        ? v().effectDescription(effect.id)
        : "";
      var seconds = num(effect.remainingRealSeconds);

      // Подсказка: описание действия плюс остаток реального времени. Остаток
      // печатаем в реальных минутах — таймеры эффектов идут по реальному
      // времени, а не по игровому.
      var tooltip = description || name;
      if (seconds > 0) {
        tooltip += "\nОсталось: " +
          (seconds >= 60
            ? Math.floor(seconds / 60) + " мин " + Math.round(seconds % 60) + " с"
            : Math.round(seconds) + " с");
      }

      return "<button type='button' class='indicatorEffect " +
        (effect.isDebuff ? "debuff" : "buff") +
        "' data-perk-link='" + escapeHtml(effect.id) + "'" +
        " data-perk-kind='" + (effect.isDebuff ? "debuff" : "buff") + "'" +
        " data-game-tooltip=\"" + escapeHtml(tooltip) + "\" " +
        "title='" + escapeHtml(name) + "'>" +
        escapeHtml(name) +
      "</button>";
    }

    return "<section class='indicatorCard'>" +
      "<div class='indicatorTitle'><span class='indicatorName'>Активные эффекты</span>" +
        "<span class='indicatorValue'>" + list.length + "</span></div>" +
      "<div class='indicatorFormula'>" +
        (buffs.length ? "<div class='indicatorFact'>Баффы: " +
          buffs.map(chip).join("") + "</div>" : "") +
        (debuffs.length ? "<div class='indicatorFact'>Дебаффы: " +
          debuffs.map(chip).join("") + "</div>" : "") +
        "<div class='indicatorFact'>Нажмите на эффект, чтобы открыть окно " +
          "«Перки, баффы, скиллы» и увидеть его описание.</div>" +
      "</div>" +
    "</section>";
  }

  function applyMessage(message) {
    if (!message) return;

    if (message.type === "snapshot") {
      snapshot = message.snapshot || snapshot;
      // Каталог нужен, чтобы назвать съеденный предмет по Id: в желудке лежит
      // только остаток, и без каталога пункт переваривания был бы безымянным.
      if (Array.isArray(message.itemCatalog))
        itemCatalog = message.itemCatalog;
      if (snapshot && message.conditionRates) {
        snapshot.conditionRates = message.conditionRates;
      }
      if (message.daylight) snapshot.daylight = message.daylight;
      simulationRunning = !!message.simulationRunning;
      simulationPaused = !!message.simulationPaused;
    } else if (message.type === "live_state") {
      // live_state может прийти РАНЬШЕ полного снимка: симуляция уже идёт, а
      // снимок мира ещё собирается. Тогда снимок создаёт именно он — иначе окно
      // осталось бы пустым до следующего полного снимка, а его можно ждать
      // сколько угодно. Пустой объект безопасен: все обращения к снимку идут
      // через "snapshot && snapshot.<...>", и шкалы нарисуются с нулями.
      if (!snapshot) snapshot = {};
      if (message.player) snapshot.player = message.player;
      if (message.playerVitals) snapshot.playerVitals = message.playerVitals;
      if (message.conditions) snapshot.conditions = message.conditions;
      if (message.conditionRates) snapshot.conditionRates = message.conditionRates;
      if (message.daylight) snapshot.daylight = message.daylight;
      if (typeof message.simulationRunning === "boolean") simulationRunning = message.simulationRunning;
      if (typeof message.simulationPaused === "boolean") simulationPaused = message.simulationPaused;
    } else if (message.type === "indicators_closed") {
      send({ action: "close_indicators" });
      return;
    } else if (message.type === "highlight") {
      // Выделение — не снимок: его приход не должен приводить к rememberExhaustion,
      // иначе отметка истощения сдвинулась бы на пустом месте.
      setHighlight(message.key);
      return;
    }

    // Образец истощения берём ПОСЛЕ обновления снимка: динамику оно и меряет.
    rememberExhaustion();
    render();
  }

  function init() {
    body = document.getElementById("indicatorsBody");
    if (!body) return;

    // Без общей разметки шкал (AssistVitals) монитор показать нечего: она даёт и
    // состав шкал, и перевод единиц в проценты. Лучше не рисовать ничего, чем
    // показать шкалы, посчитанные второй копией правил.
    if (!v()) return;

    v().attachTooltips(document);

    // Клик по имени эффекта открывает окно «Перки, баффы, скиллы» и просит
    // подсветить именно этот пункт.
    //
    // Делегирование, а не подписка на каждый чип: разметка пересобирается на
    // каждом изменении снимка, и обработчики на узлах терялись бы вместе с ними.
    // Подсветку выполняет ХОСТ: окно перков — отдельная страница, и «какой пункт
    // подсветить» — это состояние между двумя окнами, а не свойство разметки.
    body.addEventListener("click", function (event) {
      var node = event.target && event.target.closest
        ? event.target.closest("[data-perk-link]")
        : null;
      if (!node) return;

      send({
        action: "open_perks",
        perkId: node.getAttribute("data-perk-link") || "",
        perkKind: node.getAttribute("data-perk-kind") || ""
      });
    });

    // Тикер: игровое время и «сейчас: …» должны меняться, даже если новый
    // пакет не пришёл (шлюз шлёт live_state только при запущенной симуляции).
    //
    // Образцы истощения копятся ЗДЕСЬ, а не только при приходе сообщения: между
    // пакетами значения уже изменились, и окно замера должно это видеть.
    global.setInterval(function () {
      if (!snapshot || !simulationRunning || simulationPaused) return;
      rememberExhaustion();
      render();
    }, 1000);

    if (global.chrome && global.chrome.webview) {
      global.chrome.webview.addEventListener("message", function (event) {
        try {
          applyMessage(typeof event.data === "string" ? JSON.parse(event.data) : event.data);
        } catch (error) {
          // Разбор сообщения не должен ронять окно монитора.
        }
      });
    }

    send({ action: "indicators_ready" });
  }

  function send(payload) {
    if (global.chrome && global.chrome.webview) {
      global.chrome.webview.postMessage(payload);
    }
  }

  // Клавиша Escape закрывает окно: Host закрывает форму, страница лишь просит.
  document.addEventListener("keydown", function (event) {
    if (event.key === "Escape") send({ action: "close_indicators" });
  });

  global.AssistIndicators = {
    render: render,
    factorsFor: factorsFor,
    setHighlight: setHighlight
  };

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", init);
  } else {
    init();
  }
})(typeof window !== "undefined" ? window : this);
