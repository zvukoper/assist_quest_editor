/**
 * Окно «Перки, баффы, скиллы».
 *
 * Четыре раздела: перки, скиллы, баффы, дебаффы. Разделение не косметическое —
 * у пунктов РАЗНЫЙ набор действий, и свалить их в один список значило бы
 * показать кнопку «Убрать» у баффа, который нельзя убрать, или поле очков у
 * перка, которому очки некуда девать.
 *
 * Страница НИЧЕГО не решает сама: и перечень пунктов, и признак «действует
 * сейчас», и собранное имя с пометками приходят от Хоста. Список задуманного —
 * это знание о мире (какие эффекты вообще бывают), и вторая его копия в
 * JavaScript означала бы, что добавленный в движок эффект не появится в окне.
 */
(function (global) {
  "use strict";

  var body = null;
  var errorNode = null;
  var payload = null;
  var lastMarkup = "";

  /**
   * Id пункта, на который сослались из монитора или журнала.
   *
   * Состояние ОКНА, а не данных: Хост присылает его отдельным сообщением, и
   * живёт оно до следующего клика. В общем списке пунктов держать его нельзя —
   * «что подсветить» относится к тому, ОТКУДА пришли, а не к самим пунктам.
   */
  var highlightId = "";
  var HIGHLIGHT_MS = 4000;
  var highlightTimer = null;

  /** Разделы в порядке показа. */
  var SECTIONS = [
    {
      key: "perk",
      title: "Перки",
      hint: "постоянные последствия действий"
    },
    {
      key: "skill",
      title: "Скиллы",
      hint: "уровень набирается очками"
    },
    {
      key: "buff",
      title: "Баффы",
      hint: "временная выгода"
    },
    {
      key: "debuff",
      title: "Дебаффы",
      hint: "временная расплата"
    }
  ];

  function v() { return global.AssistVitals; }

  function escapeHtml(value) {
    return v()
      ? v().escapeHtml(value)
      : String(value == null ? "" : value);
  }

  function entries() {
    return payload && Array.isArray(payload.entries) ? payload.entries : [];
  }

  function send(payloadToSend) {
    if (global.chrome && global.chrome.webview) {
      global.chrome.webview.postMessage(payloadToSend);
    }
  }

  function showError(message) {
    if (!errorNode) return;
    errorNode.textContent = message || "";
  }

  /**
   * Описание пункта: «как работает» и «на что влияет».
   *
   * Два отдельных абзаца, а не одна строка: автор просил объяснить механику И
   * перечислить показатели, а это разные вопросы. Слитая строка читалась бы как
   * один длинный текст, в котором игрок не находит нужную половину.
   */
  function descriptionHtml(entry) {
    var html = "<div class='perkEntryText'>" +
      escapeHtml(entry.description || "") + "</div>";

    if (entry.affects) {
      html += "<div class='perkEntryText'>" +
        "<span class='perkEntryLabel'>Влияет: </span>" +
        escapeHtml(entry.affects) + "</div>";
    }

    return html;
  }

  /**
   * Кнопка действия пункта.
   *
   * Для неподключённых пунктов кнопка ОТКЛЮЧЕНА и объясняет причину: пункт без
   * кнопки выглядел бы забытым, а работающая кнопка у незаконченного пункта
   * начисляла бы эффект, которого движок не знает.
   */
  function actionButton(entry, label, action, disabled) {
    return "<button type='button' class='perkAction'" +
      " data-perk-id='" + escapeHtml(entry.id) + "'" +
      " data-perk-action='" + action + "'" +
      (disabled
        ? " disabled data-game-tooltip='Пункт ещё не подключён к симулятору'"
        : "") +
      ">" + escapeHtml(label) + "</button>";
  }

  function skillControls(entry) {
    return "<span class='perkEntryDuration'>Очки:</span>" +
      "<input class='perkLevelInput' type='number' min='0' max='" +
        Number(entry.maxLevel || 0) + "' step='1'" +
        " value='" + Number(entry.level || 0) + "'" +
        " data-perk-id='" + escapeHtml(entry.id) + "'" +
        " data-perk-level='1'" +
        " aria-label='Очки умения " + escapeHtml(entry.name) + "'" +
      ">" +
      "<span class='perkEntryDuration'>из " +
        Number(entry.maxLevel || 0) + "</span>";
  }

  function entryHtml(entry) {
    var classes = "perkEntry";

    if (entry.active) classes += " active";
    // Выделение жирнее признака активности: оно отвечает на «куда я только что
    // нажал», а не на «что у меня есть».
    if (String(entry.id).toLowerCase() === highlightId.toLowerCase())
      classes += " highlight";

    var footer = "<div class='perkEntryFooter'>";

    if (entry.durationLabel) {
      footer += "<span class='perkEntryDuration'>" +
        escapeHtml(entry.durationLabel) + "</span>";
    }

    if (entry.category === "skill") {
      footer += skillControls(entry);
    } else if (entry.category === "perk") {
      footer += entry.active
        ? actionButton(entry, "Убрать", "remove", !entry.implemented)
        : actionButton(entry, "Добавить", "add", !entry.implemented);
    } else {
      footer += entry.active
        ? actionButton(entry, "Деактивировать", "deactivate", false)
        : actionButton(entry, "Активировать", "activate", false);
    }

    footer += "</div>";

    return "<div class='" + classes + "' data-perk-entry='" +
      escapeHtml(entry.id) + "'>" +
      "<div class='perkEntryName'>" + escapeHtml(entry.displayName) + "</div>" +
      descriptionHtml(entry) +
      footer +
    "</div>";
  }

  function sectionHtml(section) {
    var list = entries().filter(function (entry) {
      return entry.category === section.key;
    });

    // Пустой раздел печатается с явным «нет»: пропущенный раздел читался бы как
    // ошибка окна, а не как «у игрока такого нет».
    var content = list.length
      ? list.map(entryHtml).join("")
      : "<div class='perkEntryText'>Нет ни одного пункта.</div>";

    return "<section class='perksCard'>" +
      "<div class='perksCardTitle'><span>" + escapeHtml(section.title) + "</span>" +
        "<span class='perksCount'>" + list.length + " · " +
          escapeHtml(section.hint) + "</span></div>" +
      content +
    "</section>";
  }

  function render() {
    if (!body) return;

    var markup = SECTIONS.map(sectionHtml).join("");

    // Пересборка только при изменении: снимок приходит часто, и замена узлов
    // сбрасывала бы набранное значение в поле очков, а с ним и фокус.
    if (markup === lastMarkup) return;
    lastMarkup = markup;
    body.innerHTML = markup;

    if (highlightId) scrollToHighlight();
  }

  function scrollToHighlight() {
    var node = body.querySelector(
      "[data-perk-entry='" + highlightId + "']");
    if (!node || !node.scrollIntoView) return;

    node.scrollIntoView({ block: "center" });
  }

  /**
   * Ставит выделение на пункт и через время снимает его.
   *
   * Таймер один на окно: повторная ссылка на другой пункт не должна оставлять
   * висеть таймер от предыдущей, иначе старое выделение снималось бы уже после
   * нового и рамка исчезала бы раньше, чем игрок её заметил.
   */
  function setHighlight(id) {
    highlightId = String(id || "");

    if (highlightTimer) global.clearTimeout(highlightTimer);
    highlightTimer = null;

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

    if (message.type === "perks") {
      payload = message;
      if (message.error) showError(message.error);
      else showError("");
      render();
      return;
    }

    if (message.type === "highlight") {
      setHighlight(message.perkId || message.key || "");
      return;
    }

    if (message.type === "perks_closed") {
      send({ action: "close_perks" });
    }
  }

  function levelOf(entry) {
    var id = String(entry.id || "").toLowerCase();
    var found = entries().find(function (item) {
      return String(item.id).toLowerCase() === id;
    });
    return found ? Number(found.level || 0) : 0;
  }

  function init() {
    body = document.getElementById("perksBody");
    errorNode = document.getElementById("perksError");
    if (!body) return;

    if (v()) v().attachTooltips(document);

    // Делегирование, а не подписка на каждый узел: разметка пересобирается на
    // каждом обновлении, и обработчики на узлах терялись бы вместе с ней.
    body.addEventListener("click", function (event) {
      var button = event.target && event.target.closest
        ? event.target.closest("[data-perk-action]")
        : null;
      if (!button) return;

      var id = button.getAttribute("data-perk-id") || "";
      var action = button.getAttribute("data-perk-action") || "";
      var entry = entries().find(function (item) {
        return String(item.id).toLowerCase() === id.toLowerCase();
      });
      if (!entry) return;

      if (entry.category === "perk") {
        send({
          action: "set_perk",
          perkId: id,
          enabled: action === "add"
        });
        return;
      }

      send({
        action: "set_effect",
        effectId: id,
        enabled: action === "activate"
      });
    });

    // Поле очков отправляет значение по УХОДУ ФОКУСА и по Enter, а не на каждое
    // нажатие: «1» — это префикс «12», и отправка по символу переписывала бы
    // уровень в процессе набора.
    body.addEventListener("change", function (event) {
      var input = event.target;
      if (!input || !input.getAttribute || !input.getAttribute("data-perk-level"))
        return;

      sendLevel(input);
    });

    body.addEventListener("keydown", function (event) {
      if (event.key !== "Enter") return;

      var input = event.target;
      if (!input || !input.getAttribute || !input.getAttribute("data-perk-level"))
        return;

      event.preventDefault();
      sendLevel(input);
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

    send({ action: "perks_ready" });
  }

  /**
   * Отправляет введённый уровень и ОТКАТЫВАЕТ поле, если Хост его отверг.
   *
   * Откат обязателен: значение вне диапазона Хост не примет, и без отката поле
   * показывало бы уровень, которого в мире нет — игрок считал бы, что очки
   * вложены. Проверка диапазона здесь дублирует проверку Хоста только для
   * сообщения; правду всё равно задаёт домен.
   */
  function sendLevel(input) {
    var id = input.getAttribute("data-perk-id") || "";
    var maximum = Number(input.getAttribute("max")) || 0;
    var value = Math.round(Number(input.value));

    if (!Number.isFinite(value) || value < 0 || value > maximum) {
      showError("Очков можно вложить от 0 до " + maximum + ".");
      input.value = String(levelOf({ id: id }));
      return;
    }

    showError("");
    send({
      action: "set_skill_level",
      skillId: id,
      level: value
    });
  }

  // Escape закрывает окно: форму Windows страница закрыть не может.
  document.addEventListener("keydown", function (event) {
    if (event.key === "Escape") send({ action: "close_perks" });
  });

  global.AssistPerks = {
    render: render,
    setHighlight: setHighlight
  };

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", init);
  } else {
    init();
  }
})(typeof window !== "undefined" ? window : this);
