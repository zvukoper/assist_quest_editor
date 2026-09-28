/**
 * Окно «Предметы»: каталог всего, что существует в симуляторе.
 *
 * Название, плитка 48×48, описание, пищевая ценность, эффекты и количество.
 * Количество по умолчанию 0 (сколько предметов у игрока), а микрокнопки «+»/«−»
 * меняют запас на единицу.
 *
 * Страница НИЧЕГО не считает: и каталог, и пищевая ценность, и текущее
 * количество приходят от Хоста. Пищевая ценность — это числа мира (сколько ккал
 * в пайке), и вторая их таблица в JavaScript разошлась бы с движком: игрок
 * прочитал бы «400 ккал», а движок начислил другое.
 *
 * Изображений 48×48 у предметов нет, и автор прямо просил их не выдумывать:
 * плитка — это цвет предмета с первой буквой названия.
 */
(function (global) {
  "use strict";

  var body = null;
  var errorNode = null;
  var searchNode = null;
  var payload = null;
  var lastMarkup = "";

  /** Строка поиска живёт в окне, а не в данных: это фильтр показа. */
  var query = "";

  var highlightId = "";
  var HIGHLIGHT_MS = 4000;
  var highlightTimer = null;

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

  /** Число без лишних нулей: «400», а не «400.0». */
  function fmt(value) {
    var number = num(value);
    var rounded = number.toFixed(number === Math.round(number) ? 0 : 1);
    return rounded;
  }

  function entries() {
    return payload && Array.isArray(payload.entries) ? payload.entries : [];
  }

  function send(message) {
    if (global.chrome && global.chrome.webview) {
      global.chrome.webview.postMessage(message);
    }
  }

  function showError(message) {
    if (errorNode) errorNode.textContent = message || "";
  }

  function visibleEntries() {
    if (!query) return entries();

    var needle = query.toLowerCase();

    return entries().filter(function (entry) {
      return String(entry.name || "").toLowerCase().indexOf(needle) >= 0 ||
        String(entry.id || "").toLowerCase().indexOf(needle) >= 0;
    });
  }

  /**
   * Пищевая ценность и эффекты.
   *
   * Блок печатается ТОЛЬКО у еды и питья: у прочих предметов нет ни калорий, ни
   * воды, и «0 ккал» в карточке читалось бы как «еда, но бесполезная».
   *
   * Рядом с абсолютными величинами идут проценты шкалы: игрок ищет в мониторе
   * именно проценты, а «250 мл» без перевода заставил бы его считать в уме.
   */
  function nutritionHtml(entry) {
    if (!entry.feeds) return "";

    var parts = [];

    if (num(entry.grams) > 0) {
      parts.push("Порция " + fmt(entry.grams) + " г");
    }

    if (num(entry.kilocalories) > 0) {
      parts.push("Энергия " + fmt(entry.kilocalories) + " ккал (" +
        fmt(entry.energyPercent) + "% шкалы)");
    }

    if (num(entry.waterMilliliters) > 0) {
      parts.push("Жидкость " + fmt(entry.waterMilliliters) + " мл (" +
        fmt(entry.hydrationPercent) + "% шкалы)");
    }

    if (!parts.length) return "";

    return "<div class='itemNutrition'><span class='itemNutritionLabel'>" +
      "Пищевая ценность: </span>" + escapeHtml(parts.join(" · ")) + "</div>";
  }

  function cardHtml(entry) {
    var classes = "itemCard";

    if (String(entry.id).toLowerCase() === highlightId.toLowerCase())
      classes += " itemHighlight";

    var quantity = Math.max(0, Math.round(num(entry.quantity)));

    return "<div class='" + classes + "' data-item-id='" +
      escapeHtml(entry.id) + "'>" +
      "<div class='itemTile' style='background:" + escapeHtml(entry.color || "#888") +
        "' aria-hidden='true'>" + escapeHtml(entry.letter || "?") + "</div>" +
      "<div class='itemMain'>" +
        "<div class='itemTitle'>" +
          "<span class='itemName'>" + escapeHtml(entry.name) + "</span>" +
          "<span class='itemCategory'>" + escapeHtml(entry.category) + "</span>" +
        "</div>" +
        "<div class='itemDescription'>" + escapeHtml(entry.description) + "</div>" +
        nutritionHtml(entry) +
        "<div class='itemFooter'>" +
          "<span class='itemInInventory'>В инвентаре:</span>" +
          "<input class='itemQtyInput' type='number' min='0' max='999' step='1'" +
            " value='" + quantity + "'" +
            " data-item-qty='" + escapeHtml(entry.id) + "'" +
            " aria-label='Количество предмета " + escapeHtml(entry.name) + "'>" +
          "<button type='button' class='itemMicro' data-item-step='-1'" +
            " data-item-id='" + escapeHtml(entry.id) + "'" +
            " aria-label='Убрать одну единицу предмета " + escapeHtml(entry.name) + "'" +
            (quantity <= 0 ? " disabled" : "") +
          ">−</button>" +
          "<button type='button' class='itemMicro' data-item-step='1'" +
            " data-item-id='" + escapeHtml(entry.id) + "'" +
            " aria-label='Добавить одну единицу предмета " + escapeHtml(entry.name) + "'" +
            (quantity >= 999 ? " disabled" : "") +
          ">+</button>" +
        "</div>" +
      "</div>" +
    "</div>";
  }

  function render() {
    if (!body) return;

    var list = visibleEntries();

    var markup = list.length
      ? list.map(cardHtml).join("")
      : "<div class='itemsEmpty'>" +
        (entries().length
          ? "Ничего не найдено по запросу."
          : "Каталог предметов пуст.") +
        "</div>";

    // Пересборка только при изменении: снимок приходит часто, и замена узлов
    // сбрасывала бы фокус в поле количества вместе с набранным числом.
    if (markup === lastMarkup) return;
    lastMarkup = markup;
    body.innerHTML = markup;

    if (highlightId) scrollToHighlight();
  }

  function scrollToHighlight() {
    var node = body.querySelector("[data-item-id='" + highlightId + "']");
    if (!node || !node.scrollIntoView) return;

    node.scrollIntoView({ block: "center" });
  }

  /**
   * Ставит выделение на предмет и через время снимает его.
   *
   * Таймер один на окно: повторная ссылка на другой предмет не должна оставлять
   * висеть таймер от предыдущей, иначе старое выделение снималось бы уже после
   * нового и рамка исчезала бы раньше, чем её заметили.
   */
  function setHighlight(id) {
    highlightId = String(id || "");

    if (highlightTimer) global.clearTimeout(highlightTimer);
    highlightTimer = null;

    // Ссылку из журнала не должен прятать фильтр: иначе клик по названию не
    // показал бы ничего, и выглядело бы это как сломанная ссылка.
    if (highlightId && query) {
      query = "";
      if (searchNode) searchNode.value = "";
    }

    render();

    if (!highlightId) return;

    highlightTimer = global.setTimeout(function () {
      highlightTimer = null;
      highlightId = "";
      render();
    }, HIGHLIGHT_MS);
  }

  function applyMessage(message) {
    if (!message) return;

    if (message.type === "items") {
      payload = message;
      if (message.error) showError(message.error);
      else showError("");
      render();
      return;
    }

    if (message.type === "highlight") {
      setHighlight(message.itemId || message.key || "");
      return;
    }

    if (message.type === "items_closed") {
      send({ action: "close_items" });
    }
  }

  function quantityOf(itemId) {
    var lower = String(itemId || "").toLowerCase();
    var found = entries().find(function (entry) {
      return String(entry.id).toLowerCase() === lower;
    });

    return found ? Math.max(0, Math.round(num(found.quantity))) : 0;
  }

  function setQuantity(itemId, quantity) {
    var value = Math.max(0, Math.min(999, Math.round(num(quantity))));

    showError("");
    send({
      action: "set_item_quantity",
      itemId: itemId,
      quantity: value
    });
  }

  function init() {
    body = document.getElementById("itemsBody");
    errorNode = document.getElementById("itemsError");
    searchNode = document.getElementById("itemsSearch");
    if (!body) return;

    if (searchNode) {
      searchNode.addEventListener("input", function () {
        query = String(searchNode.value || "").trim();
        render();
      });
    }

    // Делегирование, а не подписка на каждый узел: разметка пересобирается на
    // каждом обновлении, и обработчики на узлах терялись бы вместе с ней.
    body.addEventListener("click", function (event) {
      var button = event.target && event.target.closest
        ? event.target.closest("[data-item-step]")
        : null;
      if (!button || button.disabled) return;

      var itemId = button.getAttribute("data-item-id") || "";
      var step = Number(button.getAttribute("data-item-step"));

      setQuantity(itemId, quantityOf(itemId) + (Number.isFinite(step) ? step : 0));
    });

    // Поле количества отправляет значение по уходу фокуса и по Enter, а не на
    // каждое нажатие: «1» — это префикс «12», и отправка по символу переписывала
    // бы запас в процессе набора.
    body.addEventListener("change", function (event) {
      var input = event.target;
      if (!input || !input.getAttribute || !input.getAttribute("data-item-qty"))
        return;

      sendQuantity(input);
    });

    body.addEventListener("keydown", function (event) {
      if (event.key !== "Enter") return;

      var input = event.target;
      if (!input || !input.getAttribute || !input.getAttribute("data-item-qty"))
        return;

      event.preventDefault();
      sendQuantity(input);
    });

    if (global.chrome && global.chrome.webview) {
      global.chrome.webview.addEventListener("message", function (event) {
        try {
          applyMessage(typeof event.data === "string"
            ? JSON.parse(event.data)
            : event.data);
        } catch (error) {
          // Разбор сообщения не должен ронять окно.
        }
      });
    }

    send({ action: "items_ready" });
  }

  /**
   * Отправляет введённое количество и ОТКАТЫВАЕТ поле, если значение вне
   * диапазона. Без отката поле показывало бы запас, которого в мире нет.
   */
  function sendQuantity(input) {
    var itemId = input.getAttribute("data-item-qty") || "";
    var value = Math.round(Number(input.value));

    if (!Number.isFinite(value) || value < 0 || value > 999) {
      showError("Количество может быть от 0 до 999.");
      input.value = String(quantityOf(itemId));
      return;
    }

    setQuantity(itemId, value);
  }

  document.addEventListener("keydown", function (event) {
    // Escape закрывает окно. Поле количества Escape-ом не отменяется: окно
    // предметов открывается из журнала, и повторное нажатие ожидаемо закрывает
    // его — это привычнее, чем «Escape сработал не всегда».
    if (event.key === "Escape") send({ action: "close_items" });
  });

  global.AssistItems = {
    render: render,
    setHighlight: setHighlight
  };

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", init);
  } else {
    init();
  }
})(typeof window !== "undefined" ? window : this);
