/**
 * Точечное обновление уже отрисованной разметки — БЕЗ замены узлов.
 *
 * ЗАЧЕМ ЭТО НУЖНО. Разметка окон пересобирается на каждом обновлении данных
 * (живое состояние приходит четыре раза в секунду). Привычный способ подставить
 * её — `container.innerHTML = markup` — вместе с содержимым заменяет и ВСЕ узлы.
 * Замена узла означает три вещи сразу, и все три видны пользователя:
 *
 *   • `:hover` сбрасывается: под курсором оказывается НОВЫЙ элемент, и на
 *     следующем кадре он уже не «под мышью». Снаружи это «плашка мигает»;
 *   • начатый переход оформления (`transition:background-color 50ms`) у нового
 *     узла начинается с НУЛЯ, поэтому фон и рамка успевают «сходить» туда и
 *     обратно — самое заметное мерцание;
 *   • узел покидает документ, и CSS-анимация (пульсация) перезапускается.
 *
 * Это уже трижды ловилось в репозитории («кнопки постоянно мерцают то выделением,
 * то без него»): сначала в правом сайдбаре, потом в левом, потом в чипах
 * монитора. Каждый раз лечили точечно — кэшем подписи разметки, а от подмены
 * узлов внутри изменившейся разметки не спасал и он. Поэтому лечение вынесено
 * сюда, в ОДНО место.
 *
 * ЧТО ДЕЛАЕТ `reconcile`. Разбирает разметку и переносит в контейнер ТОЛЬКО
 * изменения, сверяя узлы по устойчивому ключу (тег + id + ключевой `data-`атрибут):
 *
 *   • узел с тем же ключом сохраняется — обновляются его атрибуты, а внутри
 *     рекурсивно повторяется та же сверка. Именно поэтому элемент, стоящий под
 *     курсором, документ не покидает и переход оформления не начинается заново;
 *   • новый узел вставляется, исчезнувший удаляется, переехавший — перемещается.
 *
 * Ключом СЛУЧАЙНО НЕ сделаны `class` и вообще любые атрибуты, которые меняются от
 * состояния (`class="acc open"`, `disabled`, `aria-*`). Иначе кнопка, у которой
 * поменялся класс, считалась бы НОВЫМ узлом — и мигание вернулось бы ровно в том
 * случае, ради которого всё это и делается.
 *
 * Границы применимости: контейнер должен содержать только разметку окна (без
 * `<script>`), потому что разбор идёт через временный `div`.
 */
(function (global) {
  "use strict";

  /** Узлы документа: числа, а не `Node.*` — модуль работает и без DOM-глобалей. */
  var TEXT_NODE = 3;
  var ELEMENT_NODE = 1;

  /**
   * Устойчивый ключ узла.
   *
   * `id` — самый надёжный признак. Если его нет, берётся ПЕРВЫЙ из ключевых
   * `data-`атрибутов: они описывают роль узла в разметке (шкала, предмет,
   * порция, пункт меню) и от состояния не зависят.
   *
   * Узлы без ключа получают ключ по тегу: одинаковые соседи различаются
   * ПОРЯДКОМ (в накопителе ключа они лежат по документу), и это ровно то
   * поведение, которого ждёт разметка вида «список одинаковых строк».
   */
  var KEY_ATTRIBUTES = [
    "data-key",
    "data-scale-key",
    "data-section",
    "data-stomach-item",
    "data-item-id",
    "data-inventory-slot",
    "data-inventory-item",
    "data-use-item",
    "data-player-tab",
    "data-panel",
    "data-save-path"
  ];

  function keyOf(node) {
    if (!node) return "";
    if (node.nodeType === TEXT_NODE) return "#text";
    if (node.nodeType !== ELEMENT_NODE) return "";
    if (!node.getAttribute) return "";

    var id = node.getAttribute("id") || "";
    var marker = "";
    for (var i = 0; i < KEY_ATTRIBUTES.length; i++) {
      marker = node.getAttribute(KEY_ATTRIBUTES[i]) || "";
      if (marker) break;
    }

    return String(node.tagName || "").toLowerCase() + "\u0001" + id + "\u0001" + marker;
  }

  /**
   * Атрибуты, которые переносятся по особому правилу: они описывают СОСТОЯНИЕ
   * ПОЛЬЗОВАТЕЛЯ, а не разметку.
   *
   * `value` поля ввода и `checked` галочки трогать нельзя, ПОКА ПОЛЕ В ФОКУСЕ:
   * снимок приходит четыре раза в секунду, и перенос затирал бы набираемое число
   * прямо во время правки («1» — это префикс «12»).
   *
   * Но и НЕ переносить их нельзя: значение поля в разметке — это способ, которым
   * окно показывает данные мира (координата игрока, скорость, деньги). Отказ от
   * переноса оставил бы поле с прежним числом навсегда, хотя рядом всё живое.
   * Поэтому правило различает именно ФОКУС, а не тип узла.
   */
  var LIVE_ATTRIBUTES = { value: 1, checked: 1 };

  function isField(node) {
    var tag = String(node.tagName || "").toUpperCase();
    return tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT";
  }

  /** Правится ли это поле игроком прямо сейчас. */
  function isBeingEdited(node) {
    if (!isField(node)) return false;
    var doc = node.ownerDocument;
    return !!(doc && doc.activeElement === node);
  }

  /**
   * Переносит объявления из `style` свежего узла, НЕ затирая чужие.
   *
   * ЗАЧЕМ ОТДЕЛЬНОЕ ПРАВИЛО. `setAttribute("style", …)` заменяет объявление
   * ЦЕЛИКОМ. А на плитке желудка координаты и размер (`left`, `top`, `width`,
   * `height`) ставит УКЛАДЧИК уже после вставки разметки — в самой разметке их
   * нет. Поэтому строки не совпадали НИКОГДА (`background: rgb(…); left: 0px;
   * …` против `background:#4b8fe8;color:…`), и на КАЖДОМ обновлении плитка
   * получала `setAttribute("style", …)`, который:
   *
   *   • выбрасывал координаты и размер, то есть плитка НА МГНОВЕНИЕ теряла своё
   *     место (сжималась в точку и уезжала в угол области), а затем укладчик
   *     ставил их заново — это лишний пересчёт вёрстки на каждой плитке;
   *   • обесценивал оформление ДО того, как укладчик прочтёт `area.clientWidth`
   *     для пересчёта миллилитров в пиксели, — то есть заставлял браузер
   *     пересчитывать вёрстку СИНХРОННО, четыре раза в секунду. Именно это и
   *     делало прокрутку монитора подтормаживающей.
   *
   * Теперь сравнивается КАЖДОЕ объявление отдельно и пишется только изменившееся;
   * объявления, которых нет в свежей разметке, НЕ снимаются — ими владеет укладчик.
   */
  function patchStyle(keep, cssText) {
    if (!keep.style) return;
    var declarations = String(cssText || "").split(";");
    for (var i = 0; i < declarations.length; i++) {
      var declaration = declarations[i];
      var colon = declaration.indexOf(":");
      if (colon < 0) continue;
      var property = declaration.slice(0, colon).trim();
      if (!property) continue;
      var value = declaration.slice(colon + 1).trim();
      // `getPropertyValue` отдаёт то же представление, что записано через
      // `setProperty`: сравнение не зависит от того, как браузер нормализовал
      // цвет или число, поэтому «то же значение» не считается изменением.
      if (keep.style.getPropertyValue(property) !== value) {
        keep.style.setProperty(property, value);
      }
    }
  }

  /** Переносит атрибуты свежего узла на сохранённый, не трогая поля ввода. */
  function patchAttributes(keep, fresh) {
    var name, value, index;
    var wanted = Object.create(null);
    var editing = isBeingEdited(keep);

    for (index = 0; index < fresh.attributes.length; index++) {
      name = fresh.attributes[index].name;
      value = fresh.attributes[index].value;
      wanted[name] = true;

      if (editing && LIVE_ATTRIBUTES[name]) continue;
      if (name === "style") {
        patchStyle(keep, value);
        continue;
      }
      if (keep.getAttribute(name) !== value) keep.setAttribute(name, value);
    }

    // Лишние атрибуты снимаются: иначе, например, `disabled` остался бы на
    // кнопке, которая по новым данным снова доступна.
    for (index = keep.attributes.length - 1; index >= 0; index--) {
      name = keep.attributes[index].name;
      if (wanted[name]) continue;
      if (editing && LIVE_ATTRIBUTES[name]) continue;
      keep.removeAttribute(name);
    }
  }

  /**
   * Расставляет сохранённые и новые узлы в нужном порядке и убирает лишние.
   *
   * `cursor` — первый узел, положение которого ещё не подтверждено. Пока
   * очередной нужный узел совпадает с курсором, курсор идёт дальше; в противном
   * случае нужный узел переезжает ПЕРЕД курсором. Всё, что осталось за курсором,
   * в новой разметке отсутствует, поэтому удаляется.
   *
   * В УСТОЯВШЕМСЯ СОСТОЯНИИ ЗДЕСЬ НЕ ПРОИСХОДИТ НИ ОДНОЙ ОПЕРАЦИИ: ключи и
   * порядок узлов не меняются от обновления к обновлению, поэтому первый же шаг
   * сверки совпадает с курсором на каждом узле. Это и есть цель: обновление
   * значений не должно двигать узлы, иначе элемент под курсором на мгновение
   * покидает документ и теряет `:hover`.
   */
  function applyOrder(container, ordered) {
    var cursor = container.firstChild;
    var index, dead;

    for (index = 0; index < ordered.length; index++) {
      var wanted = ordered[index];
      if (wanted === cursor) {
        cursor = cursor.nextSibling;
        continue;
      }
      container.insertBefore(wanted, cursor);
    }

    while (cursor) {
      dead = cursor;
      cursor = cursor.nextSibling;
      container.removeChild(dead);
    }
  }

  /**
   * Индекс старых узлов: ключ → очередь узлов, а для безымянных — тег → очередь.
   *
   * ЗАЧЕМ ИНДЕКС ВМЕСТО `keyOf` НА КАЖДОМ УЗЛЕ. `keyOf` — это чтение `id` и
   * перебор одиннадцати `data-`атрибутов, то есть до двенадцати обращений к
   * атрибутам на узел. Прежде он звался и на КАЖДОМ старом узле, и на КАЖДОМ
   * узле свежей разметки: монитор отдаёт около двухсот узлов четыре раза в
   * секунду, и это складывалось в тысячи операций в секунду — а измеримо это
   * выглядело как 2,8 мс на обновление и подтормаживающая прокрутка.
   *
   * Здесь дорогой ключ считается ТОЛЬКО у узлов, у которых вообще есть ключевые
   * признаки. Остальным ключом служит тег — ровно как и раньше (в прежнем
   * накопителе безымянный узел получал ключ «тег + пусто», то есть тег), но
   * теперь это не требует ни одного обращения к атрибутам.
   */
  function buildIndex(old) {
    var index = { keyed: Object.create(null), tags: Object.create(null), text: [] };

    for (var i = 0; i < old.length; i++) {
      var node = old[i];

      // Текстовые узлы тоже переиспользуются, и это не мелочь: текстовая
      // «прослойка» есть в разметке чипов (кнопка, пробел, вклад), а замена
      // текстового узла — это снятие и вставка в документе, за которыми тянется
      // пересчёт вёрстки. Пара «текст — текст» ставится в порядке следования.
      if (node.nodeType === TEXT_NODE) {
        index.text.push(node);
        continue;
      }

      if (node.nodeType !== ELEMENT_NODE || !node.getAttribute) continue;

      var key = keyWithoutId(node);
      if (key) {
        if (!index.keyed[key]) index.keyed[key] = [];
        index.keyed[key].push(node);
        continue;
      }

      var tag = String(node.tagName || "").toLowerCase();
      if (!index.tags[tag]) index.tags[tag] = [];
      index.tags[tag].push(node);
    }

    return index;
  }

  /**
   * Ключ узла БЕЗ обращения к `keyOf`, если ключевых признаков нет вовсе.
   *
   * `id` проверяется первым: он и самый частый, и самый дешёвый признак.
   */
  function keyWithoutId(node) {
    if (!node.getAttribute) return "";
    if (node.getAttribute("id")) return keyOf(node);

    for (var i = 0; i < KEY_ATTRIBUTES.length; i++) {
      if (node.getAttribute(KEY_ATTRIBUTES[i])) return keyOf(node);
    }

    return "";
  }

  /** Забирает из индекса узел, годный для подстановки, — или `null`. */
  function takeKeep(index, node) {
    if (node.nodeType === TEXT_NODE) {
      return index.text.length ? index.text.shift() : null;
    }

    if (node.nodeType !== ELEMENT_NODE || !node.getAttribute) return null;

    var key = keyWithoutId(node);
    if (key) {
      var bucket = index.keyed[key];
      return bucket && bucket.length ? bucket.shift() : null;
    }

    var queue = index.tags[String(node.tagName || "").toLowerCase()];
    return queue && queue.length ? queue.shift() : null;
  }

  /** Сверяет содержимое узла с разметкой свежего узла — тем же правилом. */
  function reconcileChildren(keep, fresh) {
    if (!keep.ownerDocument) return;
    // Текстовое содержимое без элементов (самый частый случай: одно число в
    // ячейке) правится напрямую — разбор разметки здесь не нужен.
    if (!fresh.firstElementChild) {
      // Значение пишется в СУЩЕСТВУЮЩИЙ текстовый узел. Присваивание
      // `textContent` заменяет текстовый узел целиком: снятие и вставка в
      // документ, за которыми тянется пересчёт вёрстки. Для монитора с двумя
      // сотнями узлов и четырьмя обновлениями в секунду это и была та работа,
      // из-за которой подтормаживала прокрутка.
      var existing = keep.firstChild;
      if (existing && existing.nodeType === TEXT_NODE && existing === keep.lastChild) {
        if (existing.nodeValue !== fresh.textContent) existing.nodeValue = fresh.textContent;
        return;
      }

      if ((keep.textContent || "") !== (fresh.textContent || "")) {
        keep.textContent = fresh.textContent;
      }
      return;
    }
    reconcile(keep, fresh.innerHTML);
  }

  /**
   * Подставляет разметку в контейнер, СОХРАНЯЯ узлы с теми же ключами.
   *
   * @param {Element} container контейнер разметки окна.
   * @param {string} html свежая разметка (полное содержимое контейнера).
   * @param {{preserve?: string}} [options] `preserve` — селектор узлов, которые
   *        нужно ОСТАВИТЬ КАК ЕСТЬ и вернуть на прежнее место.
   *
   * Зачем `preserve`. Некоторые узлы живут ВНЕ разметки окна и лишь переставляются
   * при её обновлении (раздел «Инструменты» левого сайдбара: его кнопки созданы
   * один раз и не пересоздаются никогда — именно так они переживают обновления).
   * Такой узел НЕ упомянут в разметке, поэтому обычная сверка сочла бы его
   * лишним и удалила — а совпадение по тегу связало бы его с чужим блоком и
   * стёрло содержимое. `preserve` снимает узел перед сверкой и возвращает на
   * место после неё.
   */
  function reconcile(container, html, options) {
    if (!container || !container.ownerDocument) return;
    var shell = container.ownerDocument.createElement("div");
    shell.innerHTML = html;

    var selector = options && options.preserve ? String(options.preserve) : "";
    var kept = [];

    if (selector) {
      var children = Array.prototype.slice.call(container.children);
      for (var i = 0; i < children.length; i++) {
        var child = children[i];
        if (!child.matches || !child.matches(selector)) continue;
        kept.push({ node: child, index: i, next: child.nextSibling });
        container.removeChild(child);
      }
    }

    reconcileElement(container, shell);

    // Возврат на прежнее место: якорь — узел, стоявший сразу ЗА сохранённым.
    // Если якорь тоже исчез, вставляем по исходному номеру, чтобы порядок не
    // зависел от того, что успело измениться.
    for (var j = 0; j < kept.length; j++) {
      var entry = kept[j];
      var anchor = entry.next && entry.next.parentNode === container
        ? entry.next
        : (container.children[entry.index] || null);
      container.insertBefore(entry.node, anchor);
    }
  }

  /**
   * Ядро: сверка содержимого `shell` с содержимым `container`.
   *
   * Ничего не удаляется и не вставляется, пока узел можно сохранить: сверка
   * СНАЧАЛА строит список узлов для подстановки, и только потом, если он
   * отличается от текущего содержимого, зовёт `applyOrder`. Поэтому узел, у
   * которого не изменился ни ключ, ни место, документ не покидает НИ В КАКОЙ
   * момент — ни `insertBefore`, ни `removeChild` для него не вызываются вовсе.
   */
  function reconcileElement(container, shell) {
    var fresh = Array.prototype.slice.call(shell.childNodes);
    var old = Array.prototype.slice.call(container.childNodes);
    var index = buildIndex(old);

    var ordered = [];
    var i;

    for (i = 0; i < fresh.length; i++) {
      var node = fresh[i];
      var keep = takeKeep(index, node);

      if (!keep) {
        ordered.push(node);
        continue;
      }

      // Текстовый узел: у него нет ни атрибутов, ни детей — правится только
      // само значение, и только если оно изменилось.
      if (keep.nodeType === TEXT_NODE) {
        if (keep.nodeValue !== node.nodeValue) keep.nodeValue = node.nodeValue;
        ordered.push(keep);
        continue;
      }

      patchAttributes(keep, node);
      reconcileChildren(keep, node);
      ordered.push(keep);
    }

    if (sameNodes(old, ordered)) return;

    applyOrder(container, ordered);
  }

  /**
   * Совпадает ли список узлов с тем, что уже стоит в контейнере.
   *
   * Сравнение идёт ПОСЛЕДОВАТЕЛЬНО и по длине: этого достаточно, потому что
   * подставляемый список строится сверкой тех же самых узлов, и любой перенос
   * или замена меняет именно последовательность. Пока она совпадает, документ
   * можно не трогать вовсе.
   */
  function sameNodes(old, ordered) {
    if (old.length !== ordered.length) return false;
    for (var i = 0; i < ordered.length; i++) {
      if (old[i] !== ordered[i]) return false;
    }
    return true;
  }

  global.AssistDom = {
    reconcile: reconcile,
    keyOf: keyOf
  };
})(typeof window !== "undefined" ? window : this);
