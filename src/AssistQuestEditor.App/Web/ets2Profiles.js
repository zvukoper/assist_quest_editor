/**
 * Окно «Профили ETS2».
 *
 * ЧТО ЗДЕСЬ ЕСТЬ. Селект профилей (по умолчанию БЕЗ выбора), справа от него —
 * язык интерфейса, выбранный в игре, и под ним — данные выбранного профиля
 * НЕНУМЕРОВАННЫМ списком: имя, статистика, аватар и три отдельных перечня
 * подключённого (моды, карты, DLC) и сохранения.
 *
 * ЧЕГО ЗДЕСЬ НЕТ. Страница НЕ читает файлы игры и не разбирает их форматы:
 * профили, язык, моды, карты и DLC собирает Хост (см. Ets2ProfileReader), и
 * вторая копия этих правил в JavaScript разошлась бы с первой при первом же
 * обновлении игры. Здесь только разметка присланного.
 */
(function (global) {
  "use strict";

  var select = null;
  var body = null;
  var errorNode = null;
  var placeholder = null;
  var languageValue = null;
  var languageCode = null;
  var rootNode = null;

  /** Последний каталог профилей, присланный Хостом. */
  var catalog = null;

  /** Выбранный профиль: { area, hexFolder }. Пусто означает «выбор снят». */
  var selected = null;

  function log(level, message, details) {
    global.assistWebLog?.(level, message, details);
  }

  function send(payload) {
    if (global.chrome && global.chrome.webview) {
      // Объект, а не строка: так отправляют ВСЕ страницы приложения, и WebView2
      // сериализует его сам. Своё JSON.stringify здесь давало бы вторую копию
      // формата, которая разошлась бы с первой при смене канала.
      global.chrome.webview.postMessage(payload);
    }
  }

  function escapeHtml(value) {
    return String(value == null ? "" : value)
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;")
      .replace(/'/g, "&#39;");
  }

  function showError(message) {
    if (errorNode) errorNode.textContent = message || "";
  }

  function text(value) {
    return value == null || value === "" ? null : String(value);
  }

  /** Значение свойства или подпись «—»: пустое поле не должно выглядеть как ноль. */
  function propertyValue(value, suffix) {
    var shown = text(value);
    if (shown == null)
      return "<span class='ets2PropertyValue ets2Missing'>нет данных</span>";

    return "<span class='ets2PropertyValue'>" + escapeHtml(shown) +
      (suffix ? escapeHtml(suffix) : "") + "</span>";
  }

  function propertyRow(name, value, suffix) {
    return "<li><span class='ets2PropertyName'>" + escapeHtml(name) + "</span>" +
      propertyValue(value, suffix) + "</li>";
  }

  /**
   * Дата в местном времени.
   *
   * Смещение Host присылает в ISO с зоной; если поле пустое — это честное «нет
   * данных», а не 01.01.1970.
   */
  function formatDate(value) {
    if (!value) return null;
    var date = new Date(value);
    if (isNaN(date.getTime())) return null;

    var pad = function (n) { return n < 10 ? "0" + n : String(n); };
    return pad(date.getDate()) + "." + pad(date.getMonth() + 1) + "." + date.getFullYear() +
      " " + pad(date.getHours()) + ":" + pad(date.getMinutes());
  }

  /**
   * Число с разделителями разрядов: 39 130 читается, 39130 — нет.
   *
   * Разряды разделяются ОБЫЧНЫМ пробелом, а не `toLocaleString`: тот ставит
   * неразрывный пробел, и строка «39 130» перестала бы находиться поиском по
   * странице и совпадать в проверках.
   */
  function formatNumber(value) {
    if (value == null || value === "") return null;

    var number = Number(value);
    if (!Number.isFinite(number)) return String(value);

    var negative = number < 0;
    var digits = String(Math.abs(Math.trunc(number)));
    var grouped = "";

    for (var i = 0; i < digits.length; i++) {
      if (i > 0 && (digits.length - i) % 3 === 0) grouped += " ";
      grouped += digits[i];
    }

    return (negative ? "-" : "") + grouped;
  }

  /** Заполняет селект профилями. Выбор при этом НЕ навязывается. */
  function renderCatalog() {
    if (!select) return;

    var profiles = Array.isArray(catalog?.profiles) ? catalog.profiles : [];
    var previous = select.value;

    select.innerHTML = "<option value=''>— выберите профиль —</option>" +
      profiles.map(function (profile) {
        var label = profile.name || profile.hexFolder;
        // Место хранения называется так же, как в карточке профиля: у облачного
        // профиля данных может не быть в документах игры, и без подписи пустая
        // карточка читалась бы как сломанное окно, а не как свойство облака.
        var parts = [label, "(" + (profile.sourceLabel || "документы игры") + ")"];
        if (profile.isActive) parts.push("· сейчас в игре");
        if (profile.saveCount > 0) parts.push("· сохранений: " + profile.saveCount);

        return "<option value='" + escapeHtml(profile.area + "|" + profile.hexFolder) + "'>" +
          escapeHtml(parts.join(" ")) + "</option>";
      }).join("");

    // Выбор восстанавливается только если он всё ещё есть в списке: иначе после
    // «Обновить список» селект показывал бы пустое значение, а в окне оставались
    // бы данные исчезнувшего профиля.
    if (previous && select.querySelector("option[value='" + cssEscape(previous) + "']"))
      select.value = previous;

    if (rootNode) {
      var parts = [];
      if (catalog?.steamCloudRoot) parts.push("облако: " + catalog.steamCloudRoot);
      if (catalog?.gameRoot) parts.push(catalog.gameRoot);
      if (catalog?.steamRoot) parts.push("Steam: " + catalog.steamRoot);
      rootNode.textContent = parts.join(" · ");
    }

    // Язык интерфейса для НЕВЫБРАННОГО профиля не показывается: у профилей он
    // может различаться, и подставлять чужой означало бы показывать неправду.
    if (!selected)
      setLanguage(null, null);
  }

  function cssEscape(value) {
    return String(value).replace(/'/g, "\\'");
  }

  /**
   * Язык интерфейса справа от селекта.
   *
   * Показывается и понятное название, и код в скобках: по названию игрок узнаёт
   * свой язык, а по коду — сверяется с конфигурацией игры.
   */
  function setLanguage(label, code) {
    if (languageValue)
      languageValue.textContent = text(label) || "не определён";
    if (languageCode)
      languageCode.textContent = text(code) ? "(" + code + ")" : "";
  }

  /** Свойства профиля — ненумерованным списком. */
  function propertiesHtml(profile) {
    var rows = [
      propertyRow("Имя профиля", profile.profileName || profile.name),
      propertyRow("Папка профиля", profile.hexFolder),
      propertyRow("Где хранится", profile.area === "profiles" ? "локальный профиль" : "облако Steam"),
      propertyRow("Компания", profile.companyName),
      propertyRow("Марка грузовика", profile.brand),
      propertyRow("Грузовик", profile.truck),
      propertyRow("Карта мира", profile.mapPath),
      propertyRow("Пол персонажа", profile.male === true ? "мужской" : profile.male === false ? "женский" : null),
      propertyRow("Лицо (номер)", profile.faceIndex),
      propertyRow("Язык интерфейса", profile.languageLabel || profile.language, profile.language ? " · " + profile.language : null),
      propertyRow("Валюта в игре", profile.currencyLabel || profile.currency),
      propertyRow("Опыт", formatNumber(profile.experience)),
      propertyRow("Пробег, км", formatNumber(profile.distanceKm)),
      propertyRow("Сохранений", formatNumber(profile.saveCount)),
      propertyRow("Профиль создан", formatDate(profile.profileCreated)),
      propertyRow("Профиль сохранён", formatDate(profile.profileSaved))
    ];

    return "<ul class='ets2Properties'>" + rows.join("") + "</ul>";
  }

  /** Перечень компонентов: имя и код. Пустой перечень говорит об этом словами. */
  function componentsHtml(entries, emptyText) {
    var list = Array.isArray(entries) ? entries : [];
    if (list.length === 0)
      return "<div class='ets2Empty'>" + escapeHtml(emptyText) + "</div>";

    return "<ul class='ets2ComponentList'>" + list.map(function (entry) {
      return "<li>" +
        "<span class='ets2ComponentName'>" + escapeHtml(entry.name || entry.id) + "</span>" +
        (entry.id && entry.id !== entry.name
          ? "<span class='ets2ComponentId' title='" + escapeHtml(entry.id) + "'>" + escapeHtml(entry.id) + "</span>"
          : "") +
        "</li>";
    }).join("") + "</ul>";
  }

  function card(title, count, inner) {
    return "<section class='ets2Card'>" +
      "<div class='ets2CardTitle'><span>" + escapeHtml(title) + "</span>" +
      (count != null ? "<span class='ets2Count'>" + escapeHtml(String(count)) + "</span>" : "") +
      "</div>" + inner + "</section>";
  }

  function savesHtml(saves) {
    var list = Array.isArray(saves) ? saves : [];
    if (list.length === 0)
      return "<div class='ets2Empty'>Сохранений нет.</div>";

    var rows = list.map(function (save) {
      return "<tr>" +
        "<td>" + escapeHtml(save.name || save.slot) + "</td>" +
        "<td>" + escapeHtml(save.slot) + "</td>" +
        "<td class='ets2Num'>" + escapeHtml(save.inGameTimeLabel || "—") + "</td>" +
        "<td class='ets2Num'>" + escapeHtml(formatNumber(save.money) || "—") + "</td>" +
        "<td class='ets2Num'>" + escapeHtml(formatNumber(save.experience) || "—") + "</td>" +
        "<td class='ets2Num'>" + escapeHtml(formatNumber(save.visitedCities) || "—") + "</td>" +
        "<td class='ets2Num'>" + escapeHtml(formatNumber(save.dependencyCount) || "—") + "</td>" +
        "<td>" + escapeHtml(formatDate(save.modifiedAt) || "—") + "</td>" +
        "</tr>";
    }).join("");

    return "<table class='ets2Saves'><thead><tr>" +
      "<th>Название</th><th>Слот</th><th>Игровое время</th><th>Деньги</th>" +
      "<th>Опыт</th><th>Города</th><th>Компонентов</th><th>Изменено</th>" +
      "</tr></thead><tbody>" + rows + "</tbody></table>";
  }

  function warningsHtml(warnings) {
    var list = Array.isArray(warnings) ? warnings.filter(Boolean) : [];
    if (list.length === 0) return "";

    return "<ul class='ets2Warnings'>" + list.map(function (warning) {
      return "<li>" + escapeHtml(warning) + "</li>";
    }).join("") + "</ul>";
  }

  /**
   * Рисует данные профиля.
   *
   * Аватар показывается, только если игра его сохранила; иначе на его месте
   * стоит ПОДПИСЬ «аватара нет» — пустое место выглядело бы как ошибка загрузки.
   */
  function renderProfile(message) {
    if (!body) return;

    var profile = message.profile || {};
    selected = { area: profile.area, hexFolder: profile.hexFolder };

    // При открытии из блока «Синхронизация с ETS2» Host уже знает lineage. Сразу
    // отражаем его и в селекте, иначе карточка была бы правильной, а список
    // визуально оставался на «— выберите профиль —».
    if (select && profile.area && profile.hexFolder) {
      var selectedValue = profile.area + "|" + profile.hexFolder;
      if (select.querySelector("option[value='" + cssEscape(selectedValue) + "']"))
        select.value = selectedValue;
    }

    setLanguage(profile.languageLabel || profile.language, profile.language);

    var avatar = message.avatarDataUrl
      ? "<img class='ets2Avatar' alt='Аватар профиля' src='" + escapeHtml(message.avatarDataUrl) + "'>"
      : "<div class='ets2AvatarMissing'>аватара нет</div>";

    var header =
      "<section class='ets2Card'><div class='ets2ProfileHead'>" + avatar +
      "<div class='ets2ProfileHeadBody'>" +
      "<h2 class='ets2ProfileName'>" + escapeHtml(profile.profileName || profile.name || profile.hexFolder) + "</h2>" +
      "<div class='ets2ProfileSub'>" + escapeHtml(
        (profile.area === "profiles" ? "Локальный профиль" : "Профиль Steam") +
        " · папка " + profile.hexFolder +
        " · данные: " + (profile.sourceLabel || "документы игры")) + "</div>" +
      warningsHtml(profile.warnings) +
      "</div></div></section>";

    body.innerHTML = header +
      card("Свойства профиля", null, propertiesHtml(profile)) +
      card("Подключённые моды", (profile.mods || []).length,
        componentsHtml(profile.mods, "Подключённых модов нет.")) +
      card("Подключённые карты", (profile.maps || []).length,
        componentsHtml(profile.maps, "Дополнительных карт нет.")) +
      card("Подключённые DLC", (profile.dlc || []).length,
        componentsHtml(profile.dlc, "DLC не найдены или сохранений нет: список DLC игра хранит только в сохранениях.")) +
      card("Сохранения", (profile.saves || []).length, savesHtml(profile.saves));

    // Подсказка «выберите профиль» исчезла вместе с innerHTML выше, и держать
    // ссылку на выброшенный узел нельзя: следующая же запись в него не попала бы
    // на страницу.
    placeholder = null;

    log("INFO", "Окно профилей ETS2: данные профиля показаны.", {
      area: profile.area,
      hexFolder: profile.hexFolder,
      mods: (profile.mods || []).length,
      maps: (profile.maps || []).length,
      dlc: (profile.dlc || []).length,
      saves: (profile.saves || []).length
    });
  }

  /** Сбрасывает окно к подсказке «выберите профиль». */
  function clearProfile() {
    selected = null;
    setLanguage(null, null);
    if (!body) return;

    body.innerHTML =
      "<div class='ets2Notice' id='ets2Placeholder'>" +
      "Выберите профиль ETS2 в списке выше — в окне появятся его данные: имя, " +
      "язык интерфейса, статистика, подключённые моды, карты, DLC и сохранения." +
      "</div>";
    placeholder = document.getElementById("ets2Placeholder");
  }

  function applyMessage(message) {
    if (!message) return;

    if (message.type === "ets2_profiles") {
      catalog = message;
      renderCatalog();
      showError("");

      if (Array.isArray(message.warnings) && message.warnings.length > 0)
        log("WARN", "Профили ETS2 прочитаны с замечаниями.", message.warnings.join(" | "));
      return;
    }

    if (message.type === "ets2_profile") {
      renderProfile(message);
      showError("");
      return;
    }

    if (message.type === "ets2_profile_cleared") {
      clearProfile();
      return;
    }

    if (message.type === "ets2_profiles_error") {
      showError(message.message || "Не удалось прочитать профили ETS2.");
      return;
    }
  }

  function init() {
    select = document.getElementById("ets2ProfileSelect");
    body = document.getElementById("ets2Body");
    errorNode = document.getElementById("ets2Error");
    placeholder = document.getElementById("ets2Placeholder");
    languageValue = document.getElementById("ets2LanguageValue");
    languageCode = document.getElementById("ets2LanguageCode");
    rootNode = document.getElementById("ets2Root");

    select?.addEventListener("change", function () {
      var value = select.value || "";
      if (!value) {
        selected = null;
        send({ action: "ets2_select_profile", area: "", hexFolder: "" });
        return;
      }

      var separator = value.indexOf("|");
      send({
        action: "ets2_select_profile",
        area: value.slice(0, separator),
        hexFolder: value.slice(separator + 1)
      });
    });

    document.getElementById("ets2Refresh")?.addEventListener("click", function () {
      send({ action: "ets2_request_profiles" });
    });

    if (global.chrome && global.chrome.webview) {
      global.chrome.webview.addEventListener("message", function (event) {
        try {
          applyMessage(typeof event.data === "string" ? JSON.parse(event.data) : event.data);
        } catch (error) {
          // Разбор сообщения не должен ронять окно: следующее сообщение придёт
          // снимком и всё поправит.
        }
      });
    }

    // Escape закрывает окно: форму Windows страница закрыть не может.
    document.addEventListener("keydown", function (event) {
      if (event.key === "Escape") send({ action: "close_ets2_profiles" });
    });

    // Список запрашивается СРАЗУ: без него селект остался бы пустым, и окно
    // показывало бы подсказку при живом диске с профилями.
    send({ action: "ets2_request_profiles" });
  }

  global.AssistEts2Profiles = { render: renderProfile, clear: clearProfile };

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", init);
  } else {
    init();
  }
})(typeof window !== "undefined" ? window : this);
