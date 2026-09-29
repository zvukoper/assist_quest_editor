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
  var edibleNode = null;
  var payload = null;

  /**
   * Подпись ПОКАЗАННОГО списка — по ней видно, нужно ли вообще собирать разметку.
   * Смотри пояснение в render().
   */
  var lastSignature = null;

  /** Строка поиска живёт в окне, а не в данных: это фильтр показа. */
  var query = "";

  /**
   * Галочка «только съедобное» — тоже фильтр ПОКАЗА, и живёт рядом с запросом.
   *
   * Хранится в окне, а не в данных: автор просил скрывать неСъедобное именно в
   * СПИСКЕ, а сам каталог обязан остаться полным — иначе «выбросить мыло» стало
   * бы негде, и фильтр превратился бы в потерю предметов.
   */
  var onlyEdible = false;

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
    var source = entries().slice();
    source.sort(function (a, b) {
      var byName = String(a.name || "").localeCompare(
        String(b.name || ""), "ru", { sensitivity: "base" }
      );
      return byName || String(a.id || "").localeCompare(String(b.id || ""), "en");
    });

    // Съедобность приходит от Хоста (`edible` = CharacterVitalsEngine.CanConsume),
    // а не выводится здесь из калорий: у таблеток и мыла калорий нет, но они
    // употребимы, и признак «есть ккал» отфильтровал бы их как неСъедобное.
    if (onlyEdible) {
      source = source.filter(function (entry) { return entry.edible === true; });
    }

    if (!query) return source;
    var needle = query.toLowerCase();
    return source.filter(function (entry) {
      return String(entry.name || "").toLowerCase().indexOf(needle) >= 0 ||
        String(entry.id || "").toLowerCase().indexOf(needle) >= 0;
    });
  }

  /**
   * Объём порции и пищевая ценность.
   *
   * ОБЪЁМ печатается у КАЖДОГО предмета (требование автора) и идёт первым: это
   * единственное число, по которому видно, сколько предмет займёт в желудке.
   * Хост отдаёт его в `milliliters` — том же числе, что подписано в меню желудка,
   * поэтому «150 мл» здесь и «150 мл» там совпадают.
   *
   * Блок пищевой ценности печатается ТОЛЬКО у еды и питья: у прочих предметов нет
   * ни калорий, ни воды, и «0 ккал» в карточке читалось бы как «еда, но
   * бесполезная».
   *
   * Рядом с абсолютными величинами идут проценты шкалы: игрок ищет в мониторе
   * именно проценты, а «250 мл» без перевода заставил бы его считать в уме.
   */
  function nutritionHtml(entry) {
    var parts = [];

    if (num(entry.milliliters) > 0) {
      parts.push("Объём порции " + fmt(entry.milliliters) + " мл");
    }

    if (entry.feeds) {
      if (num(entry.kilocalories) > 0) {
        parts.push("Энергия " + fmt(entry.kilocalories) + " ккал (" +
          fmt(entry.energyPercent) + "% шкалы)");
      }

      if (num(entry.waterMilliliters) > 0) {
        parts.push("Жидкость " + fmt(entry.milliliters) + " мл (" +
          fmt(entry.hydrationPercent) + "% шкалы)");
      }
    }

    if (!parts.length) return "";

    return "<div class='itemNutrition'><span class='itemNutritionLabel'>" +
      "Пищевая ценность: </span>" + escapeHtml(parts.join(" · ")) + "</div>";
  }

  /**
   * Ключ карточки для сверки узлов.
   *
   * Сверка (`AssistDom.reconcile`) ключуется по `id` и ключевым `data-`атрибутам.
   * У карточки нет ни того, ни другого — `id` был бы обязан быть уникальным в
   * документе, а роль узла задаёт именно `data-item-id`. Поэтому признаки
   * добавляются ПРЯМО В РАЗМЕТКУ: без них сверка считала бы карточку безымянной
   * и сохраняла бы её по ПОРЯДКУ, а порядок задаёт сортировка по названию —
   * смена количества у одного предмета переставляла бы карточки местами.
   *
   * `data-key` — тот самый атрибут, который сверка читает первым; `data-item-id`
   * уже стоял в разметке и раньше (по нему ищет подсветка), и сверка читает его
   * тоже, но порядок перебора оставлен явным.
   */
  function cardHtml(entry) {
    var classes = "itemCard";

    if (String(entry.id).toLowerCase() === highlightId.toLowerCase())
      classes += " itemHighlight";

    var quantity = Math.max(0, Math.round(num(entry.quantity)));

    return "<div class='" + classes + "' data-key='" + escapeHtml(entry.id) +
      "' data-item-id='" + escapeHtml(entry.id) + "'>" +
      "<div class='itemTile' style='background:" + escapeHtml(entry.color || "#888") +
        "' aria-hidden='true'>" + escapeHtml(entry.letter || "?") + "</div>" +
      "<div class='itemMain'>" +
        "<div class='itemTitle'>" +
          "<span class='itemName'>" + escapeHtml(entry.name) + "</span>" +
          "<span class='itemCategory'>" + escapeHtml(entry.category) + "</span>" +
          (entry.edible ? "<span class='itemEdibleBadge'>съедобно</span>" : "") +
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

  /**
   * Сколько ЕДИНИЦ предмета показано в открытом окне — ключ для проверки «список
   * не изменился».
   *
   * ЗАЧЕМ ОТДЕЛЬНО ОТ СВЕРКИ УЗЛОВ. Сверка узлов переживает смену данных, но она
   * стоит разбора разметки: двадцать тысяч символов четыре раза в секунду. Между
   * тем живые обновления Хоста — это ПОВТОР того же списка: количество меняется
   * только когда игрок правит его сам или съедает предмет. По одной этой строке
   * видно, изменился ли список, и в самом частом случае разметка не собирается
   * вовсе.
   *
   * Признак намеренно берётся из ТЕХ ЖЕ полей, что печатает карточка
   * (название, количество, съедобность, объём, ккал, вода, категория, описание,
   * цвет): любое поле, влияющее на показ, обязано сбрасывать список, иначе
   * карточка осталась бы со старым содержимым.
   */
  function entriesSignature(list) {
    var parts = [];
    for (var i = 0; i < list.length; i++) {
      var entry = list[i];
      parts.push([entry.id, entry.name, entry.category, entry.description,
        entry.color, entry.letter, entry.edible === true ? 1 : 0,
        Math.max(0, Math.round(num(entry.quantity))),
        num(entry.milliliters), num(entry.kilocalories), num(entry.waterMilliliters)]
        .join("\u0001"));
    }
    return parts.join("\u0002");
  }

  function render() {
    if (!body) return;

    var list = visibleEntries();

    // Список не изменился — разметку не собираем вовсе. Прокрутка, выделение и
    // карточка под курсором остаются ровно как были.
    var signature = entriesSignature(list);
    if (signature === lastSignature) return;
    lastSignature = signature;

    var markup = list.length
      ? list.map(cardHtml).join("")
      : "<div class='itemsEmpty'>" +
        (onlyEdible && !query
          ? "Нет съедобных предметов в каталоге."
          : entries().length
            ? "Ничего не найдено по запросу."
            : "Каталог предметов пуст.") +
        "</div>";

    // Разметка ставится СВЕРКОЙ УЗЛОВ, а не `innerHTML`.
    //
    // Прежняя замена всех карточек сразу давала три беды, и все три заметны при
    // ВКЛЮЧЁННОЙ симуляции: карточка под курсором теряла `:hover` (мерцание
    // плашек), выделение текста сбрасывалось, а прокрутка дёргалась — список
    // высотой в несколько экранов пересобирался целиком четыре раза в секунду.
    // Автор: «сильно тормозит список предметов. Прокрутка колесом или скроллом
    // тормозит. Если выключить симуляцию, всё плавно передвигается».
    //
    // Ключом карточки служит `data-key` (см. cardHtml), поэтому сверка сохраняет
    // узлы и меняет только то, что действительно изменилось. Запасной путь —
    // прежняя замена целиком, чтобы старая страница из кеша WebView2 осталась
    // рабочей.
    if (global.AssistDom && global.AssistDom.reconcile) {
      global.AssistDom.reconcile(body, markup);
    } else {
      body.innerHTML = markup;
    }

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
    // показал бы ничего, и выглядело бы это как сломанная ссылка. Галочку
    // «только съедобное» снимаем по той же причине — ссылка может вести на мазь.
    if (highlightId && (query || onlyEdible)) {
      query = "";
      onlyEdible = false;
      if (searchNode) searchNode.value = "";
      if (edibleNode) edibleNode.checked = false;
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
    edibleNode = document.getElementById("itemsOnlyEdible");
    if (!body) return;

    if (searchNode) {
      searchNode.addEventListener("input", function () {
        query = String(searchNode.value || "").trim();
        render();
      });
    }

    // Галочка «только съедобное» — фильтр ПОКАЗА, как и поиск: она скрывает
    // неСъедобное из СПИСКА, а каталог остаётся полным. Снимать её при выделении
    // из журнала не нужно: ссылка может вести на мазь, и галочка спрятала бы
    // предмет, на который сослался игрок.
    if (edibleNode) {
      edibleNode.addEventListener("change", function () {
        onlyEdible = edibleNode.checked === true;
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
