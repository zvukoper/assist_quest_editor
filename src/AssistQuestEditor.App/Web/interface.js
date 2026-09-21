(() => {
  const layer = document.getElementById("interfaceLayer");
  const send = payload => window.chrome?.webview?.postMessage(payload);
  let dialog = null;
  let renderedDialogKey = "";

  const escapeHtml = value => String(value ?? "").replace(/[&<>"']/g, char => ({
    "&": "&amp;",
    "<": "&lt;",
    ">": "&gt;",
    "\"": "&quot;",
    "'": "&#39;"
  }[char]));

  function dialogKey(value) {
    if (!value) return "";
    return JSON.stringify({
      requestId: value.requestId || "",
      title: value.title || "",
      speaker: value.speaker || "",
      text: value.text || "",
      options: (value.options || []).map(option => ({
        id: option.id || "",
        text: option.text || ""
      }))
    });
  }

  function render() {
    if (!layer) return;

    const key = dialogKey(dialog);
    if (key === renderedDialogKey) return;
    renderedDialogKey = key;

    if (!dialog) {
      layer.innerHTML = "";
      layer.classList.remove("visible");
      return;
    }

    const options = Array.isArray(dialog.options) ? dialog.options : [];
    layer.innerHTML =
      "<div class='interfaceBackdrop'></div>" +
      "<section class='interfaceDialog' role='dialog' aria-modal='true' aria-labelledby='interfaceDialogTitle'>" +
        "<div class='interfaceDialogHeader'>" +
          "<div class='miniLabel'>Игровой интерфейс</div>" +
          "<div id='interfaceDialogTitle' class='interfaceDialogTitle'>" + escapeHtml(dialog.title || "Выбор") + "</div>" +
        "</div>" +
        "<div class='interfaceSpeaker'>" + escapeHtml(dialog.speaker || "Персонаж") + "</div>" +
        "<div class='interfaceText'>" + escapeHtml(dialog.text || "Выберите вариант.") + "</div>" +
        "<div class='interfaceChoices'>" +
          options.map((option, index) =>
            "<button class='interfaceChoice' data-interface-choice='" + (index + 1) + "'>" +
              "<span class='interfaceChoiceIndex'>" + (index + 1) + "</span>" +
              "<span>" + escapeHtml(option.text || ("Вариант " + (index + 1))) + "</span>" +
            "</button>"
          ).join("") +
        "</div>" +
        "<div class='interfaceHint'>Выбор передаётся обратно в Quest Runtime через интерфейсный канал.</div>" +
      "</section>";

    layer.classList.add("visible");

    layer.querySelectorAll("[data-interface-choice]").forEach(button => {
      button.addEventListener("click", () => {
        if (!dialog) return;
        layer.querySelectorAll(".interfaceChoice").forEach(item => item.disabled = true);

        const index = Number(button.dataset.interfaceChoice);
        window.assistQuestLog?.("INFO", "Интерфейс Choice: выбран вариант.", {
          requestId: dialog.requestId,
          index,
          optionId: options[index - 1]?.id || null
        });

        send({
          action: "interface_choice",
          requestId: dialog.requestId,
          index
        });
      });
    });
  }

  function receive(message) {
    if (!message || message.type !== "snapshot") return;

    const nextDialog = message.snapshot?.interfaces?.activeDialog || null;
    const nextKey = dialogKey(nextDialog);
    dialog = nextDialog;

    window.assistQuestLog?.("INFO", "Интерфейс Choice: состояние обновлено.", {
      active: !!dialog,
      requestId: dialog?.requestId || null,
      changed: nextKey !== renderedDialogKey
    });

    render();
  }

  window.chrome?.webview?.addEventListener("message", event => {
    try {
      const data = typeof event.data === "string" ? JSON.parse(event.data) : event.data;
      receive(data);
    } catch (error) {
      window.assistWebLog?.("ERROR", "Интерфейс Choice: ошибка сообщения.", {
        error: String(error)
      });
    }
  });

  window.assistWebLog?.("INFO", "Simulator interface layer готов.", {
    layer: !!layer
  });
})();