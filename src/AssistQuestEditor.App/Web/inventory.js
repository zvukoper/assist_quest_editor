/**
 * Отрисовка инвентаря: сетка предметов, потребности и кошелёк.
 *
 * Вынесено из simulator.js в отдельный модуль, потому что инвентарь показывается
 * в ДВУХ местах — панелью в Симуляторе и отдельным окном (клавиша I). Две копии
 * этой разметки неизбежно разошлись бы: в одной сетка 6×3, в другой 6×4, в одной
 * подписи предметов, в другой нет. А расхождение здесь заметно сразу — автор
 * видит разное содержимое в зависимости от способа открытия.
 *
 * Модуль НЕ знает, куда его выводят: он только собирает строки разметки и
 * разбирает данные снимка. Поэтому он годится и для страницы Симулятора, и для
 * страницы окна инвентаря.
 */
(function (global) {
  "use strict";

  /**
   * Размер сетки инвентаря: 6 столбцов × 3 строки = 18 ячеек.
   *
   * Не «сколько предметов» и не простое число: это ФОРМА сумки, заданная автором.
   * Пустые ячейки рисуются намеренно — по ним видно свободное место, тогда как
   * список из одних предметов не отвечает на вопрос «сколько я ещё унесу».
   */
  var COLUMNS = 6;
  var ROWS = 3;
  var CAPACITY = COLUMNS * ROWS;

  function escapeHtml(value) {
    return String(value == null ? "" : value)
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;")
      .replace(/'/g, "&#39;");
  }

  /**
   * Описание предмета из каталога, пришедшего в снимке.
   *
   * Отсутствие описания — не ошибка: предмет мог быть выдан из сохранения
   * прежней сборки, и вместо пустой ячейки показывается его Id.
   */
  function itemDefinition(catalog, itemId) {
    var key = String(itemId || "").toLowerCase();
    var found = (catalog || []).find(function (item) {
      return String(item.id || "").toLowerCase() === key;
    });

    return found || {
      id: itemId,
      name: itemId,
      description: "Предмет без зарегистрированного описания.",
      category: "Неизвестный предмет",
      color: "#59636d"
    };
  }

  /**
   * Ячейки сетки: предметы, затем пустые до заполнения формы.
   *
   * Предметы сортируются по Id, а не по количеству или названию: порядок обязан
   * быть СТАБИЛЬНЫМ между обновлениями снимка. При сортировке по количеству
   * ячейки перескакивали бы при каждом изменении, и попасть курсором в предмет
   * было бы невозможно.
   *
   * Если предметов БОЛЬШЕ, чем ячеек в трёх строках, снизу добавляются новые
   * ряды, а лишнее уходит в прокрутку. Число рядов считается от содержимого, а
   * не берётся постоянным: иначе девятнадцатый предмет было бы некуда положить,
   * и он молча исчезал бы из инвентаря.
   */
  function slotMarkup(snapshot, catalog, options) {
    var opts = options || {};
    var seenIds = opts.seenItemIds || new Set();
    var newIds = new Set(
      ((snapshot && snapshot.inventory && snapshot.inventory.newItemIds) || [])
        .map(function (value) { return String(value).toLowerCase(); }));

    var items = Object.entries((snapshot && snapshot.inventory && snapshot.inventory.items) || {})
      .filter(function (pair) { return Number(pair[1]) > 0; })
      .sort(function (a, b) { return a[0].localeCompare(b[0]); });

    // Округляем ВВЕРХ до целых рядов: неполный ряд остаётся рядом с пустыми
    // ячейками, и форма сетки (6 столбцов) не ломается.
    var rows = Math.max(ROWS, Math.ceil(items.length / COLUMNS));
    var capacity = rows * COLUMNS;

    var slots = [];
    for (var index = 0; index < capacity; index++) {
      var pair = items[index];
      if (!pair) {
        slots.push("<div class='inventorySlot empty' data-game-tooltip='Свободная ячейка инвентаря.'></div>");
        continue;
      }

      var itemId = pair[0];
      var quantity = pair[1];
      var item = itemDefinition(catalog, itemId);
      var key = String(itemId).toLowerCase();
      var isNew = newIds.has(key) && !seenIds.has(key);

      slots.push(
        "<div class='inventorySlot' data-game-tooltip='" +
          escapeHtml(item.description || item.name) + "'>" +
          "<button class='inventoryItemButton' type='button' data-inventory-item='" +
            escapeHtml(itemId) + "'>" +
            "<span class='inventoryItemSquare' style='background:" +
              escapeHtml(item.color || "#59636d") + "'>" +
              (key === "ruslan.raw_meat" ? "М" : "") + "</span>" +
            "<span class='inventoryItemName'>" + escapeHtml(item.name || itemId) +
              (isNew ? " <span class='inventoryNewDot' aria-label='Новый предмет'></span>" : "") +
            "</span>" +
            "<span class='inventoryItemQty'>×" + Number(quantity) + "</span>" +
          "</button>" +
        "</div>");
    }

    return slots.join("");
  }

  /**
   * Сетка предметов. Стилизуется классом .inventoryGrid — тот задаёт 6 столбцов.
   */
  function gridMarkup(snapshot, catalog, options) {
    return "<div class='inventoryGrid'>" + slotMarkup(snapshot, catalog, options) + "</div>";
  }

  function percent(value, max) {
    var maximum = Number(max);
    return Math.max(0, Math.min(100,
      maximum > 0 ? Number(value || 0) / maximum * 100 : 0));
  }

  /**
   * Потребности игрока: здоровье, энергия, жидкость, усталость.
   */
  function vitalsMarkup(snapshot) {
    var v = (snapshot && snapshot.playerVitals) || {};
    var n = function (value) { return Math.round(Number(value || 0)); };

    return [
      "<div class='gameVitals'>",
        "<div class='vitalRow' data-game-tooltip='Здоровье: текущее значение от 0 до максимума.'>" +
          "<span class='vitalLabel'>Здоровье</span>" +
          "<div class='vitalTrack'><div class='vitalFill health' style='width:" +
            percent(v.health, v.maxHealth) + "%'></div></div>" +
          "<span class='vitalValue'>" + n(v.health) + "%</span></div>",
        "<div class='vitalDual'>",
          "<div class='vitalDualCell' data-game-tooltip='Энергия: запас сил игрока.'>" +
            "<span class='vitalLabel'>Энергия</span>" +
            "<div class='vitalTrack'><div class='vitalFill energy' style='width:" +
              percent(v.energy, v.maxEnergy) + "%'></div></div></div>",
          "<div class='vitalDualCell' data-game-tooltip='Жидкость: уровень гидратации игрока.'>" +
            "<span class='vitalLabel'>Жидкость</span>" +
            "<div class='vitalTrack'><div class='vitalFill hydration' style='width:" +
              percent(v.hydration, v.maxHydration) + "%'></div></div></div>",
        "</div>",
        "<div class='vitalRow' data-game-tooltip='Усталость: 0 — полностью отдохнул, 100 — максимальная усталость.'>" +
          "<span class='vitalLabel'>Усталость</span>" +
          "<div class='vitalTrack'><div class='vitalFill fatigue' style='width:" +
            percent(v.fatigue, v.maxFatigue) + "%'></div></div>" +
          "<span class='vitalValue'>" + n(v.fatigue) + "%</span></div>",
      "</div>"
    ].join("");
  }

  /**
   * Кошелёк: деньги, опыт и резерв.
   */
  function walletMarkup(snapshot) {
    var progress = (snapshot && snapshot.playerProgress) || {};
    var n = function (value) { return Number(value || 0).toLocaleString("ru-RU"); };

    return "<div class='inventoryFooter'>" +
      "<div class='inventoryFooterCell' data-game-tooltip='Деньги игрока.'>₽ <strong>" +
        n(progress.money) + "</strong></div>" +
      "<div class='inventoryFooterCell' data-game-tooltip='Опыт игрока. Увеличивается через AddExperience.'>XP <strong>" +
        n(progress.experience) + "</strong></div>" +
      "<div class='inventoryFooterCell' data-game-tooltip='Резервный ресурс для будущих механик.'>Резерв <strong>" +
        n(progress.reserve) + "</strong></div>" +
    "</div>";
  }

  /**
   * Полное содержимое панели инвентаря: заголовок, потребности, сетка, кошелёк.
   *
   * Заголовок необязателен: в отдельном окне он уже есть в системной рамке, и
   * второй такой же читался бы как дубликат.
   */
  function panelMarkup(snapshot, catalog, options) {
    var opts = options || {};
    var header = opts.withHeader === false
      ? ""
      : "<div class='gamePanelHeader'><div><div class='gamePanelTitle'>Инвентарь</div>" +
        "<div class='gamePanelSub'>Состояние и содержимое</div></div>" +
        "<div class='gamePanelSub'>" + escapeHtml(opts.hotkeyHint || "I — открыть / закрыть") + "</div></div>";

    return header + vitalsMarkup(snapshot) + gridMarkup(snapshot, catalog, opts) + walletMarkup(snapshot);
  }

  global.AssistInventory = {
    COLUMNS: COLUMNS,
    ROWS: ROWS,
    CAPACITY: CAPACITY,
    escapeHtml: escapeHtml,
    itemDefinition: itemDefinition,
    slotMarkup: slotMarkup,
    gridMarkup: gridMarkup,
    vitalsMarkup: vitalsMarkup,
    walletMarkup: walletMarkup,
    panelMarkup: panelMarkup
  };
})(window);
