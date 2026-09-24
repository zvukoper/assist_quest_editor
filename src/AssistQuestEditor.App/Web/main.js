
(() => {
  const send = payload => {
    if (typeof window.__assistSend === "function") {
      window.__assistSend(payload);
      return;
    }
    window.chrome?.webview?.postMessage(payload);
  };

  document.querySelectorAll("[data-editor]").forEach(button => {
    button.addEventListener("click", () => send({ action: "open_editor", editor: button.dataset.editor }));
  });

  document.querySelectorAll("[data-action]").forEach(button => {
    button.addEventListener("click", () => send({ action: button.dataset.action }));
  });

  const settings = document.getElementById("settings");
  settings?.addEventListener("click", () => send({ action: "open_settings" }));

  const version = document.getElementById("version");
  const icon = version?.querySelector(".versionPushIcon");

  function setPushState(state, message) {
    if (!version) return;
    version.dataset.state = state || "";
    if (icon) {
      icon.textContent =
        state === "running" ? "↻" :
        state === "success" ? "✓" :
        state === "error" ? "✕" : "";
    }
    version.title = message || "Нажмите для загрузки MemoryAI/LOGS в GitHub";
    version.disabled = state === "running";
  }

  version?.addEventListener("click", () => {
    setPushState("running", "Подготовка MemoryAI/LOGS к загрузке…");
    send({ action: "push_logs" });
  });

  const handleWebviewMessage = event => {
    const data = typeof event.data === "string" ? JSON.parse(event.data) : event.data;
    if (data?.type === "logs_push_state") {
      setPushState(data.state, data.message);
    }
    if (data?.type === "simulator_state") {
      setSimulatorState(data.running);
    }
    if (data?.type === "world_selection") {
      applyWorldSelection(data);
    }
  };

  // --- Селектор [МИР][КАМПАНИЯ][меню] ---
  //
  // Источник истины — Host: он владеет каталогом миров и знает, какой мир
  // открыт. Web только рисует присланное и отправляет выбор обратно, поэтому
  // селектор в главном окне и в Симуляторе не может разойтись.
  const worldSelect = document.getElementById("worldSelect");
  const campaignSelect = document.getElementById("campaignSelect");
  const worldMenu = document.getElementById("worldMenu");
  const worldMenuList = document.getElementById("worldMenuList");
  const worldFolder = document.getElementById("worldFolder");

  // Идентификатор пункта «создать новый». Значение-сентинел, а не пустая
  // строка: пустое значение уже занято смыслом «пока ничего не выбрано».
  const CREATE_WORLD = "\u0000create-world";
  const CREATE_CAMPAIGN = "\u0000create-campaign";

  let selection = { worlds: [], campaigns: [], worldId: "", campaignId: "" };

  function applyWorldSelection(data) {
    selection = {
      worlds: Array.isArray(data.worlds) ? data.worlds : [],
      campaigns: Array.isArray(data.campaigns) ? data.campaigns : [],
      worldId: data.worldId || "",
      campaignId: data.campaignId || ""
    };
    renderSelector();
  }

  function renderSelector() {
    if (!worldSelect) return;

    // Списки пересобираются целиком: набор миров и кампаний меняется редко, а
    // частичное обновление легко расходится с состоянием Host.
    worldSelect.innerHTML = "";
    for (const world of selection.worlds) {
      const option = document.createElement("option");
      option.value = world.id;
      option.textContent = world.name;
      option.selected = world.id === selection.worldId;
      worldSelect.appendChild(option);
    }
    const createWorld = document.createElement("option");
    createWorld.value = CREATE_WORLD;
    createWorld.textContent = "＋ Создать мир…";
    createWorld.className = "worldSelectorCreate";
    worldSelect.appendChild(createWorld);

    campaignSelect.innerHTML = "";
    for (const campaign of selection.campaigns) {
      const option = document.createElement("option");
      option.value = campaign.id;
      option.textContent = campaign.name;
      option.selected = campaign.id === selection.campaignId;
      campaignSelect.appendChild(option);
    }
    const createCampaign = document.createElement("option");
    createCampaign.value = CREATE_CAMPAIGN;
    createCampaign.textContent = "＋ Создать кампанию…";
    campaignSelect.appendChild(createCampaign);

    const noWorlds = selection.worlds.length === 0;
    worldSelect.disabled = noWorlds;
    // Кампании без мира не существует — список заблокирован, а не пустой.
    campaignSelect.disabled = noWorlds || selection.campaigns.length === 0;
    if (worldFolder) worldFolder.disabled = noWorlds;
  }

  // Выбор пункта «создать» не должен залипать в списке: значение поля
  // сбрасывается на текущее, а действие уходит в Host.
  worldSelect?.addEventListener("change", () => {
    if (worldSelect.value === CREATE_WORLD) {
      worldSelect.value = selection.worldId;
      send({ action: "create_world" });
      return;
    }
    send({ action: "select_world", worldId: worldSelect.value });
  });

  campaignSelect?.addEventListener("change", () => {
    if (campaignSelect.value === CREATE_CAMPAIGN) {
      campaignSelect.value = selection.campaignId;
      send({ action: "create_campaign" });
      return;
    }
    send({ action: "select_campaign", campaignId: campaignSelect.value });
  });

  worldFolder?.addEventListener("click", () => send({ action: "open_world_folder" }));

  // --- Меню действий ---
  const menuItems = [
    { id: "world_edit", label: "Редактировать мир…", title: "Название, описание и изображение мира" },
    { id: "world_info", label: "Сведения о мире", title: "Автор, дата и состав мира", icon: "ℹ️" },
    { sep: true },
    { id: "campaign_edit", label: "Редактировать кампанию…", title: "Название, описание и изображение кампании", campaign: true },
    { id: "campaign_info", label: "Сведения о кампании", title: "Автор, дата и состав кампании", icon: "ℹ️", campaign: true },
    { sep: true },
    { id: "export_world", label: "Экспорт мира…", title: "Выгрузить мир папкой или архивом .aqezip" },
    { id: "export_campaign", label: "Экспорт кампании…", title: "Выгрузить кампанию папкой или архивом .aqezip", campaign: true },
    { id: "import_archive", label: "Импорт архива…", title: "Установить мир, кампанию или квест из .aqezip", create: true },
    { sep: true },
    { id: "create_world", label: "＋ Создать мир…", create: true },
    { id: "create_campaign", label: "＋ Создать кампанию…", create: true }
  ];

  function renderMenu() {
    if (!worldMenuList) return;
    worldMenuList.innerHTML = "";
    for (const item of menuItems) {
      if (item.sep) {
        const separator = document.createElement("div");
        separator.className = "worldMenuSep";
        worldMenuList.appendChild(separator);
        continue;
      }

      const button = document.createElement("button");
      button.type = "button";
      button.dataset.action = item.id;
      button.textContent = (item.icon ? item.icon + " " : "") + item.label;
      if (item.create) button.classList.add("worldMenuCreate");
      button.title = item.title || item.label;
      button.addEventListener("click", () => {
        closeMenu();
        // Действия над кампанией уносят её идентификатор: без него Host
        // вынужден угадывать, о какой кампании речь, и при расхождении
        // селектора с открытой кампанией выгрузилась бы не та. Для
        // действий над миром идентификатор не нужен: мир в окне один.
        const payload = { action: item.id };
        if (item.campaign) payload.campaignId = campaignSelect?.value || "";
        send(payload);
      });
      worldMenuList.appendChild(button);
    }
  }

  function closeMenu() {
    if (!worldMenuList) return;
    worldMenuList.hidden = true;
    worldMenu?.setAttribute("aria-expanded", "false");
  }

  worldMenu?.addEventListener("click", event => {
    event.stopPropagation();
    const open = worldMenuList.hidden;
    worldMenuList.hidden = !open;
    worldMenu.setAttribute("aria-expanded", open ? "true" : "false");
  });

  // Закрытие по внешнему клику и Escape: меню поверх содержимого, и без этого
  // оно перекрывало бы карточки, пока пользователь не нажмёт его же кнопку.
  document.addEventListener("click", event => {
    if (worldMenuList && !worldMenuList.hidden && !worldMenuList.contains(event.target) &&
        event.target !== worldMenu && !worldMenu?.contains(event.target)) {
      closeMenu();
    }
  });

  document.addEventListener("keydown", event => {
    if (event.key === "Escape") closeMenu();
  });

  renderMenu();
  renderSelector();

  // Чип показывает не «открыто ли окно симулятора», а «идёт ли симуляция»:
  // окно живёт отдельно и может быть закрыто или свёрнуто.
  const simulatorState = document.getElementById("simulatorState");

  function setSimulatorState(running) {
    if (!simulatorState) return;
    const active = running === true;
    simulatorState.textContent = active ? "Симулятор: активен" : "Симулятор: остановлен";
    // Акцентный цвет только у работающей симуляции — так состояние читается
    // без чтения текста.
    simulatorState.classList.toggle("accent", active);
  }

  if (typeof window.chrome?.webview?.addEventListener === "function") {
    window.chrome.webview.addEventListener("message", handleWebviewMessage);
  }
})();
