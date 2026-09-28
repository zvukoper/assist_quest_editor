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
  var simulationRunning = false;
  var simulationPaused = false;
  var lastMarkup = "";

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
    return 2 + steps * 0.25;
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
      var hours = key === "energy"
        ? "4 игровых часа"
        : "2 игровых часа";
      var demand = key === "energy" ? 25 : 50;
      lines.push("База: 100% за " + hours + " (в покое).");
      lines.push("Метаболизм: ниже 45% расход ×0,9; от 75% расход ×1,25. Сейчас ×" +
        fmt(metabolismFactor(), 2) + (hasEffect("bull") ? " (за счёт «Быка» ÷2)" : "") + ".");
      lines.push("Во сне расход ×1/3.");
      lines.push("Истощение по этой шкале копится, когда значение падает ниже 1%" +
        (key === "hydration" ? " — в двойном размере (2 единицы на 1)" : " — 1:1") + ".");
      lines.push("Форсаж расходуется первым: пока есть запас, обычная шкала не падает.");
      lines.push("Сейчас: расход " + fmt(demand * metabolismFactor() * (hasEffect("bull") ? 0.5 : 1), 1) +
        "% в час (в движении).");
    } else if (key === "fatigue") {
      lines.push("Рост в движении: 100% за 18 игровых часов + 100% за 900 км.");
      lines.push("Время суток: 06–18 ×1,0, 18–22 ×1,5, ночь ×2,5. Сейчас ×" +
        fmt(nightFatigueFactor(), 1) + ".");
      if (hasEffect("burnout")) lines.push("«Выгорание» ускоряет рост усталости ×1,1.");
      lines.push("Восстановление в покое: 100% за 9 игровых часов, замедлено на процент стресса.");
      lines.push("Усталость ≥ 80%: копится истощение усталости каждый игровой час — " +
        "сейчас коэффициент ×" + fmt(fatigueAccrualMultiplier(), 2) +
        " (2,0 базово + 0,25 за каждые 5% истощения).");
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

  function scaleRow(scale) {
    var values = v().read(snapshot, scale);
    var current = Math.round(values.actual);
    var maximum = Math.round(values.maximum);
    var lines = factorsFor(scale.key);

    return "<section class='indicatorCard'>" +
      "<div class='indicatorTitle'>" +
        "<span class='indicatorName'>" + escapeHtml(scale.label) + "</span>" +
        "<span class='indicatorValue'>" + current + " / " + maximum +
          " = " + Math.round(values.displayPercent) + "%</span>" +
      "</div>" +
      "<div class='indicatorFormula'>" +
        lines.map(function (line) {
          return "<div class='indicatorFact'>• " + escapeHtml(line) + "</div>";
        }).join("") +
      "</div>" +
    "</section>";
  }

  function render() {
    if (!body || !snapshot || !v()) return;

    var markup = v().SCALES.map(scaleRow).join("") +
      effectsSection();

    // Пересборка только при фактическом изменении: снимок приходит 4 раза в
    // секунду, и замена узлов на каждом из них сбрасывала бы выделение текста.
    if (markup === lastMarkup) return;
    lastMarkup = markup;
    body.innerHTML = markup;
  }

  function effectsSection() {
    var buffs = effectNames(false);
    var debuffs = effectNames(true);
    if (!buffs.length && !debuffs.length) return "";

    function list(items, cls) {
      return items.map(function (name) {
        return "<span class='indicatorEffect " + cls + "'>" + escapeHtml(name) + "</span>";
      }).join("");
    }

    return "<section class='indicatorCard'>" +
      "<div class='indicatorTitle'><span class='indicatorName'>Активные эффекты</span>" +
        "<span class='indicatorValue'>" + (buffs.length + debuffs.length) + "</span></div>" +
      "<div class='indicatorFormula'>" +
        (buffs.length ? "<div class='indicatorFact'>Баффы: " + list(buffs, "buff") + "</div>" : "") +
        (debuffs.length ? "<div class='indicatorFact'>Дебаффы: " + list(debuffs, "debuff") + "</div>" : "") +
        "<div class='indicatorFact'>Наведите на эффект в «Баффы и дебаффы», чтобы прочитать его действие.</div>" +
      "</div>" +
    "</section>";
  }

  function applyMessage(message) {
    if (!message) return;

    if (message.type === "snapshot") {
      snapshot = message.snapshot || snapshot;
      if (snapshot && message.conditionRates) {
        snapshot.conditionRates = message.conditionRates;
      }
      if (message.daylight) snapshot.daylight = message.daylight;
      simulationRunning = !!message.simulationRunning;
      simulationPaused = !!message.simulationPaused;
    } else if (message.type === "live_state") {
      if (!snapshot) return;
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
    }

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

    // Тикер: игровое время и «сейчас: …» должны меняться, даже если новый
    // пакет не пришёл (шлюз шлёт live_state только при запущенной симуляции).
    global.setInterval(function () {
      if (snapshot && simulationRunning && !simulationPaused) render();
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
    factorsFor: factorsFor
  };

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", init);
  } else {
    init();
  }
})(typeof window !== "undefined" ? window : this);
