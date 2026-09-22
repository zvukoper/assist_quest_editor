(() => {
  let selectedKind = "dialogue";
  let selectedId = null;
  let searchText = "";

  const send = payload => {
    if (typeof window.__assistSend === "function") {
      window.__assistSend(payload);
      return;
    }
    window.chrome?.webview?.postMessage(payload);
  };

  const getState = () => window.__assistSceneEditorState || null;
  const getDefinition = () => getState()?.definition || null;
  const getGraph = () => getDefinition()?.graph || null;
  const getCatalog = () => window.__assistSceneEditor?.getState?.().catalog || [];

  function escapeHtml(value) {
    return String(value ?? "").replace(/[&<>"']/g, char => ({
      "&": "&amp;",
      "<": "&lt;",
      ">": "&gt;",
      "\"": "&quot;",
      "'": "&#39;"
    }[char]));
  }

  function resourceList(kind) {
    const definition = getDefinition();
    if (!definition) return [];
    return kind === "dialogue"
      ? (definition.dialogues || [])
      : (definition.choices || []);
  }

  function getResource() {
    if (!selectedId) return null;
    return resourceList(selectedKind).find(item => item.id === selectedId) || null;
  }

  function isResourceLinked(kind, id) {
    const graph = getGraph();
    if (!graph) return false;
    const key = kind === "dialogue" ? "dialogueId" : "choiceId";
    return graph.nodes?.some(node =>
      (node.nodeType === (kind === "dialogue" ? "Dialogue" : "Choice")) &&
      node.parameters?.[key] === id
    ) || false;
  }

  function linkedNodes(kind, id) {
    const graph = getGraph();
    if (!graph) return [];
    const nodeType = kind === "dialogue" ? "Dialogue" : "Choice";
    const key = kind === "dialogue" ? "dialogueId" : "choiceId";
    return (graph.nodes || []).filter(node =>
      node.nodeType === nodeType && node.parameters?.[key] === id
    );
  }

  function connectedTarget(nodeId, socketId) {
    const graph = getGraph();
    if (!graph) return null;
    const connection = (graph.connections || []).find(item =>
      item.fromNodeId === nodeId && item.fromSocketId === socketId
    );
    if (!connection) return null;
    const target = graph.nodes?.find(item => item.nodeId === connection.toNodeId);
    return target ? target.title + " (" + target.nodeId + ")" : connection.toNodeId;
  }

  function chooseDefaultResource() {
    const items = resourceList(selectedKind);
    if (selectedId && items.some(item => item.id === selectedId)) return;
    selectedId = items[0]?.id || null;
    if (!selectedId && selectedKind === "dialogue") {
      const choices = resourceList("choice");
      if (choices.length) {
        selectedKind = "choice";
        selectedId = choices[0].id;
      }
    }
  }

  function renderToolbar(ws) {
    const graph = getGraph();
    const path = getState()?.payload?.documentPath || "";
    const dirty = Boolean(getState()?.payload?.documentDirty);
    const catalog = getCatalog();

    return (
      "<div class='toolbar dialogueWorkspaceToolbar' style='flex-wrap:wrap'>" +
        (getState()?.payload?.navigationBack
          ? "<button class='toolButton' id='dialogueBackToQuest' title='Вернуться к исходной ноде Quest Graph'>↩ Вернуться в Quest Graph</button>"
          : "") +
        "<button class='toolButton' id='dialogueOpenScene'>Открыть сцену</button>" +
        "<select id='dialogueSceneResource' class='toolButton' title='Текущая Scene'>" +
          catalog.map(item =>
            "<option value='" + escapeHtml(item.id) + "'" +
            (graph?.id === item.id ? " selected" : "") + ">" +
            escapeHtml(item.title || item.id) + "</option>"
          ).join("") +
        "</select>" +
        "<button class='toolButton' id='dialogueSaveScene'>Сохранить сцену</button>" +
        "<button class='toolButton' id='dialogueSaveSceneAs'>Сохранить как…</button>" +
        "<button class='toolButton primary' id='dialogueAdd'>+ Новый диалог</button>" +
        "<button class='toolButton' id='dialogueOpenGraph'>Scene Graph</button>" +
        "<span class='badge " + (dirty ? "accent" : "blue") + "'>" +
          escapeHtml(dirty ? "Не сохранено" : "Сохранено") +
        "</span>" +
        "<span class='badge'>" + escapeHtml(path || "Новый документ") + "</span>" +
        "<span class='badge'>" +
          escapeHtml((graph?.title || graph?.name || "Scene") + " · " +
          (resourceList("dialogue").length) + " диалогов · " +
          (resourceList("choice").length) + " выборов") +
        "</span>" +
      "</div>"
    );
  }

  function renderList() {
    const dialogueItems = resourceList("dialogue");
    const choiceItems = resourceList("choice");
    const q = searchText.trim().toLowerCase();

    const filter = item => {
      if (!q) return true;
      return [
        item.id,
        item.speaker,
        item.title,
        item.text,
        ...(item.options || []).map(option => option.text)
      ].some(value => String(value ?? "").toLowerCase().includes(q));
    };

    const dialogues = dialogueItems.filter(filter);
    const choices = choiceItems.filter(filter);

    const row = (kind, item) => {
      const active = selectedKind === kind && selectedId === item.id;
      const preview = kind === "dialogue"
        ? (item.text || "Без текста")
        : ((item.title || "Без названия") + " · " + (item.text || "Без текста"));
      const link = isResourceLinked(kind, item.id);
      return (
        "<button class='dialogueResourceItem" + (active ? " active" : "") + "' " +
          "data-resource-kind='" + escapeHtml(kind) + "' data-resource-id='" + escapeHtml(item.id) + "'>" +
          "<span class='dialogueResourceItemTop'>" +
            "<strong>" + escapeHtml(item.id) + "</strong>" +
            (link ? "<span class='dialogueLinkBadge'>linked</span>" : "<span class='dialogueLinkBadge muted'>unlinked</span>") +
          "</span>" +
          "<span class='dialogueResourceSpeaker'>" + escapeHtml(item.speaker || item.title || "Без Speaker") + "</span>" +
          "<span class='dialogueResourcePreview'>" + escapeHtml(preview) + "</span>" +
        "</button>"
      );
    };

    return (
      "<div class='dialogueResourceHeader'>" +
        "<div class='dialogueResourceTabs'>" +
          "<button class='toolButton " + (selectedKind === "dialogue" ? "primary" : "") + "' id='dialogueTab'>Диалоги (" + dialogueItems.length + ")</button>" +
          "<button class='toolButton " + (selectedKind === "choice" ? "primary" : "") + "' id='choiceTab'>Выборы (" + choiceItems.length + ")</button>" +
        "</div>" +
        "<input id='dialogueSearch' class='toolButton' value='" + escapeHtml(searchText) + "' placeholder='Поиск по ID, Speaker и тексту'>" +
      "</div>" +
      "<div class='dialogueResourceList'>" +
        (selectedKind === "dialogue"
          ? (dialogues.length ? dialogues.map(item => row("dialogue", item)).join("") : "<div class='notice'>Диалоги не найдены.</div>")
          : (choices.length ? choices.map(item => row("choice", item)).join("") : "<div class='notice'>Выборы не найдены.</div>")) +
      "</div>"
    );
  }

  function renderDialogueEditor(item) {
    const linked = linkedNodes("dialogue", item.id);
    return (
      "<div class='dialogueEditorHeader'>" +
        "<div>" +
          "<div class='miniLabel'>Dialogue resource</div>" +
          "<h2>" + escapeHtml(item.speaker || "Новый диалог") + "</h2>" +
        "</div>" +
        "<div class='toolbar'>" +
          "<button class='toolButton' id='dialogueDelete'>Удалить</button>" +
          (linked.length ? "<button class='toolButton' id='dialogueGoGraph'>Открыть в Graph</button>" : "") +
        "</div>" +
      "</div>" +
      "<div class='dialogueResourceMeta'>" +
        "<span class='badge blue'>ID: " + escapeHtml(item.id) + "</span>" +
        "<span class='badge'>" + (linked.length ? "Связан с " + linked.length + " нодой" : "Не связан с Graph") + "</span>" +
      "</div>" +
      "<div class='field'>" +
        "<label>Speaker</label>" +
        "<input id='dialogueEditSpeaker' value='" + escapeHtml(item.speaker) + "'>" +
      "</div>" +
      "<div class='field' style='margin-top:12px'>" +
        "<label>Текст реплики</label>" +
        "<textarea id='dialogueEditText' rows='14' style='width:100%;resize:vertical' spellcheck='false'>" + escapeHtml(item.text) + "</textarea>" +
      "</div>" +
      "<div class='toolbar' style='margin-top:12px'>" +
        "<button class='toolButton primary' id='dialogueSaveContent'>Сохранить реплику</button>" +
        "<span class='notice'>ID ресурса стабилен и не редактируется вручную.</span>" +
      "</div>"
    );
  }

  function renderChoiceEditor(item) {
    const linked = linkedNodes("choice", item.id);
    const graph = getGraph();
    const linkedNode = linked[0] || null;
    const options = item.options || [];

    const optionRows = options.map((option, index) => {
      const socket = linkedNode?.sockets?.find(socket =>
        socket.direction === "Output" &&
        socket.socketId === option.outputSocketId
      );
      const target = socket ? connectedTarget(linkedNode.nodeId, socket.socketId) : null;
      return (
        "<div class='dialogueChoiceOption' data-choice-option-row='" + escapeHtml(option.id) + "'>" +
          "<div class='dialogueChoiceOptionIndex'>" + (index + 1) + "</div>" +
          "<div class='dialogueChoiceOptionBody'>" +
            "<div class='dialogueChoiceOptionMeta'>" +
              "<strong>" + escapeHtml(option.id) + "</strong>" +
              "<span>" + escapeHtml(option.outputSocketId) + "</span>" +
            "</div>" +
            "<input class='toolButton dialogueChoiceOptionInput' data-choice-option-text value='" + escapeHtml(option.text) + "'>" +
            "<div class='dialogueChoiceOptionTarget'>" +
              (target ? "→ " + escapeHtml(target) : "→ ветка не подключена") +
            "</div>" +
            "<button class='toolButton dialogueChoiceOptionRemove' data-remove-choice-option='" + escapeHtml(option.id) + "'>Удалить вариант</button>" +
          "</div>" +
        "</div>"
      );
    }).join("");

    return (
      "<div class='dialogueEditorHeader'>" +
        "<div>" +
          "<div class='miniLabel'>Choice resource</div>" +
          "<h2>" + escapeHtml(item.title || "Выбор") + "</h2>" +
        "</div>" +
        "<div class='toolbar'>" +
          "<button class='toolButton' id='choiceDelete' disabled title='Удаление Choice требует отдельной безопасной операции и будет добавлено после lifecycle Choice.'>Удалить</button>" +
          (linked.length ? "<button class='toolButton' id='choiceGoGraph'>Открыть в Graph</button>" : "") +
        "</div>" +
      "</div>" +
      "<div class='dialogueResourceMeta'>" +
        "<span class='badge blue'>ID: " + escapeHtml(item.id) + "</span>" +
        "<span class='badge'>" + (linked.length ? "Связан с " + linked.length + " нодой" : "Не связан с Graph") + "</span>" +
      "</div>" +
      "<div class='field'>" +
        "<label>Название</label>" +
        "<input id='choiceEditTitle' value='" + escapeHtml(item.title) + "'>" +
      "</div>" +
      "<div class='field' style='margin-top:10px'>" +
        "<label>Speaker</label>" +
        "<input id='choiceEditSpeaker' value='" + escapeHtml(item.speaker) + "'>" +
      "</div>" +
      "<div class='field' style='margin-top:10px'>" +
        "<label>Текст перед выбором</label>" +
        "<textarea id='choiceEditText' rows='6' style='width:100%;resize:vertical' spellcheck='false'>" + escapeHtml(item.text) + "</textarea>" +
      "</div>" +
      "<div class='dialogueChoiceOptionsHeader'>" +
        "<div class='miniLabel'>Варианты ответа</div>" +
        "<button class='toolButton' id='choiceAddOption' " + (options.length >= 16 ? "disabled" : "") + ">+ Добавить вариант</button>" +
      "</div>" +
      "<div class='dialogueChoiceOptions'>" +
        (optionRows || "<div class='notice'>Вариантов нет.</div>") +
      "</div>" +
      "<div class='toolbar' style='margin-top:12px'>" +
        "<button class='toolButton primary' id='choiceSaveContent'>Сохранить выбор</button>" +
        "<span class='notice'>Output socket IDs сохраняются неизменными, чтобы не ломать ветвления Scene Graph.</span>" +
      "</div>"
    );
  }

  function renderInspector(ins, item) {
    const linked = item ? linkedNodes(selectedKind, item.id) : [];
    const graph = getGraph();

    if (!graph) {
      ins.innerHTML = "<div class='notice'>Scene ещё не загружена.</div>";
      return;
    }

    if (!item) {
      ins.innerHTML =
        "<div class='badge accent'>Dialogue Workspace</div>" +
        "<h3 style='margin:10px 0 4px'>Нет выбранного ресурса</h3>" +
        "<div class='notice'>Создайте диалог или выберите ресурс из списка слева.</div>";
      return;
    }

    let html =
      "<div class='badge accent'>Связь с Scene Graph</div>" +
      "<h3 style='margin:10px 0 4px'>" + escapeHtml(item.id) + "</h3>" +
      "<div class='notice'>" +
        escapeHtml(selectedKind === "dialogue" ? "Dialogue node" : "Choice node") +
        " ссылается на этот resource через стабильный ID." +
      "</div>";

    if (!linked.length) {
      html += "<div class='notice' style='margin-top:10px'>Ресурс пока не используется ни одной Graph-нОДой.</div>";
    } else {
      html += "<div class='miniLabel' style='margin-top:14px'>Связанные ноды</div>";
      html += "<div class='tableLike' style='margin-top:6px'>" +
        linked.map(node => {
          const outputs = selectedKind === "choice"
            ? (item.options || []).map(option => {
                const target = connectedTarget(node.nodeId, option.outputSocketId);
                return "<small>" + escapeHtml(option.outputSocketId) + (target ? " → " + escapeHtml(target) : " → не подключён") + "</small>";
              }).join("")
            : "";
          return (
            "<div class='tableRow dialogueLinkedNode'>" +
              "<span><strong>" + escapeHtml(node.title) + "</strong><small>" +
                escapeHtml(node.nodeId + " · " + node.nodeType) +
              "</small>" + outputs + "</span>" +
              "<button class='toolButton' data-open-scene-node='" + escapeHtml(node.nodeId) + "'>Graph</button>" +
            "</div>"
          );
        }).join("") +
      "</div>";
    }

    if (selectedKind === "dialogue") {
      html +=
        "<div class='miniLabel' style='margin-top:16px'>Совет</div>" +
        "<div class='notice'>Текст редактируется здесь. Scene Graph задаёт только момент и порядок показа.</div>";
    } else {
      html +=
        "<div class='miniLabel' style='margin-top:16px'>Ветки</div>" +
        "<div class='notice'>Каждый вариант хранит собственный стабильный Output socket ID. Его target определяется только Graph connection.</div>";
    }

    ins.innerHTML = html;

    ins.querySelectorAll("[data-open-scene-node]").forEach(button => {
      button.addEventListener("click", () => openGraphNode(button.dataset.openSceneNode));
    });
  }

  function openGraphNode(nodeId) {
    window.__assistSceneEditor?.selectNode?.(nodeId);
    location.hash = "#scene";
  }

  function render(ws, ins) {
    const definition = getDefinition();
    if (!definition) {
      ws.innerHTML = "<div class='notice'>Ожидание Scene Definition от Host…</div>";
      ins.innerHTML = "";
      return;
    }

    chooseDefaultResource();
    const item = getResource();

    ws.innerHTML =
      renderToolbar(ws) +
      "<div class='dialogueWorkspace'>" +
        "<aside class='dialogueResourcePane'>" +
          renderList() +
        "</aside>" +
        "<section class='dialogueEditorPane'>" +
          (item
            ? (selectedKind === "dialogue" ? renderDialogueEditor(item) : renderChoiceEditor(item))
            : "<div class='notice'>В этой Scene ещё нет Dialogue/Choice resources.</div>") +
        "</section>" +
      "</div>";

    renderInspector(ins, item);
    bind(ws, ins);
  }

  function bind(ws, ins) {
    ws.querySelector("#dialogueBackToQuest")?.addEventListener("click", () => {
      const navigationBack = getState()?.payload?.navigationBack;
      if (!navigationBack?.nodeId) return;
      send({ action: "navigate_to_quest_node", nodeId: navigationBack.nodeId });
    });

    ws.querySelector("#dialogueOpenScene")?.addEventListener("click", () => {
      location.hash = "#scene";
    });

    ws.querySelector("#dialogueOpenGraph")?.addEventListener("click", () => {
      location.hash = "#scene";
    });

    ws.querySelector("#dialogueSceneResource")?.addEventListener("change", event => {
      send({ action: "scene_open_resource", sceneId: event.target.value });
    });

    ws.querySelector("#dialogueSaveScene")?.addEventListener("click", () => {
      send({ action: "scene_save" });
    });

    ws.querySelector("#dialogueSaveSceneAs")?.addEventListener("click", () => {
      send({ action: "scene_save_as" });
    });

    ws.querySelector("#dialogueAdd")?.addEventListener("click", () => {
      send({ action: "scene_add_dialogue" });
    });

    ws.querySelector("#dialogueTab")?.addEventListener("click", () => {
      selectedKind = "dialogue";
      selectedId = resourceList("dialogue")[0]?.id || null;
      render(ws, ins);
    });

    ws.querySelector("#choiceTab")?.addEventListener("click", () => {
      selectedKind = "choice";
      selectedId = resourceList("choice")[0]?.id || null;
      render(ws, ins);
    });

    ws.querySelector("#dialogueSearch")?.addEventListener("input", event => {
      searchText = event.target.value;
      render(ws, ins);
      const search = ws.querySelector("#dialogueSearch");
      if (search) {
        search.focus();
        search.setSelectionRange(search.value.length, search.value.length);
      }
    });

    ws.querySelectorAll("[data-resource-kind]").forEach(button => {
      button.addEventListener("click", () => {
        selectedKind = button.dataset.resourceKind;
        selectedId = button.dataset.resourceId;
        render(ws, ins);
      });
    });

    ws.querySelector("#dialogueSaveContent")?.addEventListener("click", () => {
      const item = getResource();
      if (!item) return;
      send({
        action: "scene_update_dialogue",
        dialogueId: item.id,
        speaker: ws.querySelector("#dialogueEditSpeaker")?.value ?? "",
        text: ws.querySelector("#dialogueEditText")?.value ?? ""
      });
    });

    ws.querySelector("#dialogueDelete")?.addEventListener("click", () => {
      const item = getResource();
      if (!item) return;
      if (!window.confirm(
        "Удалить Dialogue resource «" + item.id + "»?\\n\\n" +
        "Удаление будет отклонено автоматически, если ресурс ещё используется Scene Graph."
      )) return;
      send({ action: "scene_remove_dialogue", dialogueId: item.id });
    });

    ws.querySelector("#dialogueGoGraph")?.addEventListener("click", () => {
      const node = linkedNodes("dialogue", getResource()?.id)[0];
      if (node) openGraphNode(node.nodeId);
    });

    ws.querySelector("#choiceSaveContent")?.addEventListener("click", () => {
      const item = getResource();
      if (!item) return;
      const options = Array.from(ws.querySelectorAll("[data-choice-option-row]")).map(row => ({
        id: row.dataset.choiceOptionRow,
        text: row.querySelector("[data-choice-option-text]")?.value ?? ""
      }));
      send({
        action: "scene_update_choice",
        choiceId: item.id,
        title: ws.querySelector("#choiceEditTitle")?.value ?? "",
        speaker: ws.querySelector("#choiceEditSpeaker")?.value ?? "",
        text: ws.querySelector("#choiceEditText")?.value ?? "",
        options
      });
    });

    ws.querySelector("#choiceAddOption")?.addEventListener("click", () => {
      const item = getResource();
      const node = item ? linkedNodes("choice", item.id)[0] : null;
      if (!item || !node) {
        window.alert("Сначала привяжите Choice resource к Choice node в Scene Graph.");
        return;
      }
      send({
        action: "scene_add_choice_option",
        nodeId: node.nodeId,
        choiceId: item.id
      });
    });

    ws.querySelectorAll("[data-remove-choice-option]").forEach(button => {
      button.addEventListener("click", () => {
        const item = getResource();
        const node = item ? linkedNodes("choice", item.id)[0] : null;
        const optionId = button.dataset.removeChoiceOption;
        if (!item || !node || !optionId) return;
        if (!window.confirm(
          "Удалить вариант «" + optionId + "»?\\n\\n" +
          "Подключённый Output socket удалить нельзя."
        )) return;
        send({
          action: "scene_remove_choice_option",
          nodeId: node.nodeId,
          choiceId: item.id,
          optionId
        });
      });
    });

    ws.querySelector("#choiceGoGraph")?.addEventListener("click", () => {
      const node = linkedNodes("choice", getResource()?.id)[0];
      if (node) openGraphNode(node.nodeId);
    });
  }

  window.__assistDialogueWorkspace = {
    render,
    refresh: render,
    selectResource(kind, id) {
      selectedKind = kind || "dialogue";
      selectedId = id || null;
      const ws = document.getElementById("workspace");
      const ins = document.getElementById("inspector");
      if (location.hash.toLowerCase() === "#dialogue" && ws && ins) {
        render(ws, ins);
      }
    }
  };
})();
