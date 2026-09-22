(() => {
  const layer = document.getElementById("interfaceLayer");
  const send = payload => window.chrome?.webview?.postMessage(payload);
  let active = null;
  let renderedKey = "";

  const escapeHtml = value => String(value ?? "").replace(/[&<>"']/g, char => ({
    "&": "&amp;",
    "<": "&lt;",
    ">": "&gt;",
    "\"": "&quot;",
    "'": "&#39;"
  }[char]));

  function stateKey(value) {
    if (!value) return "";
    return JSON.stringify({
      kind: value.kind || "",
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

    const key = stateKey(active);
    if (key === renderedKey) return;
    renderedKey = key;

    if (!active) {
      layer.innerHTML = "";
      layer.classList.remove("visible");
      return;
    }

    const isDialogue = active.kind === "dialogue";
    const options = Array.isArray(active.options) ? active.options : [];

    layer.innerHTML =
      "<div class='interfaceBackdrop'></div>" +
      "<section class='interfaceDialog' role='dialog' aria-modal='true' aria-labelledby='interfaceDialogTitle'>" +
        "<div class='interfaceDialogHeader'>" +
          "<div class='miniLabel'>" + (isDialogue ? "Диалог" : "Игровой интерфейс") + "</div>" +
          "<div id='interfaceDialogTitle' class='interfaceDialogTitle'>" +
            escapeHtml(active.title || (isDialogue ? "Диалог" : "Выбор")) +
          "</div>" +
        "</div>" +
        "<div class='interfaceSpeaker'>" + escapeHtml(active.speaker || "Персонаж") + "</div>" +
        "<div class='interfaceText'>" + escapeHtml(active.text || "") + "</div>" +
        (isDialogue
          ? "<div class='interfaceChoices'>" +
              "<button class='interfaceChoice interfaceDialogueContinue' data-interface-dialogue-continue>" +
                "<span class='interfaceChoiceIndex' aria-hidden='true'>▶</span>" +
                "<span>Продолжить</span>" +
              "</button>" +
            "</div>" +
            "<div class='interfaceHint'>Продолжение передаётся обратно в Scene Runtime через интерфейсный канал.</div>"
          : "<div class='interfaceChoices'>" +
              options.map((option, index) =>
                "<button class='interfaceChoice' data-interface-choice='" + (index + 1) + "'>" +
                  "<span class='interfaceChoiceIndex'>" + (index + 1) + "</span>" +
                  "<span>" + escapeHtml(option.text || ("Вариант " + (index + 1))) + "</span>" +
                "</button>"
              ).join("") +
            "</div>" +
            "<div class='interfaceHint'>Выбор передаётся обратно в Scene Runtime через интерфейсный канал.</div>") +
      "</section>";

    layer.classList.add("visible");

    const continueButton = layer.querySelector("[data-interface-dialogue-continue]");
    if (continueButton) {
      continueButton.addEventListener("click", () => {
        if (!active || active.kind !== "dialogue") return;
        continueButton.disabled = true;

        window.assistQuestLog?.("INFO", "Интерфейс Dialogue: диалог продолжен.", {
          requestId: active.requestId
        });

        send({
          action: "interface_dialogue_continue",
          requestId: active.requestId
        });
      });
    }

    layer.querySelectorAll("[data-interface-choice]").forEach(button => {
      button.addEventListener("click", () => {
        if (!active || active.kind !== "choice") return;
        layer.querySelectorAll(".interfaceChoice").forEach(item => item.disabled = true);

        const index = Number(button.dataset.interfaceChoice);
        const option = options[index - 1];

        window.assistQuestLog?.("INFO", "Интерфейс Choice: выбран вариант.", {
          requestId: active.requestId,
          index,
          optionId: option?.id || null
        });

        send({
          action: "interface_choice",
          requestId: active.requestId,
          index
        });
      });
    });
  }

  function receive(message) {
    if (!message || message.type !== "snapshot") return;

    const nextDialogue = message.snapshot?.interfaces?.activeDialogue || null;
    const nextChoice = message.snapshot?.interfaces?.activeDialog || null;
    const next = nextDialogue
      ? { kind: "dialogue", ...nextDialogue }
      : nextChoice
        ? { kind: "choice", ...nextChoice }
        : null;

    const nextKey = stateKey(next);
    active = next;

    window.assistQuestLog?.("INFO", "Интерфейс Scene: состояние обновлено.", {
      kind: active?.kind || null,
      active: !!active,
      requestId: active?.requestId || null,
      changed: nextKey !== renderedKey
    });

    render();
  }

  /**
   * Единый обработчик входящих сообщений.
   *
   * Подписка ниже идёт и на `window`, и на `chrome.webview`: так делают
   * editor.js, sceneEditor.js, main.js и simulator.js. Только webview-подписка
   * делала интерфейс нерабочим вне реального WebView2 (Playwright-смоук и
   * синтетические MessageEvent), из-за чего состояние интерфейса не обновлялось.
   */
  const handleInterfaceMessage = event => {
    try {
      const data = typeof event.data === "string" ? JSON.parse(event.data) : event.data;
      receive(data);
    } catch (error) {
      window.assistWebLog?.("ERROR", "Интерфейс Scene: ошибка сообщения.", {
        error: String(error)
      });
    }
  };

  window.addEventListener("message", handleInterfaceMessage);

  const webview = window.chrome?.webview;
  if (typeof webview?.addEventListener === "function") {
    webview.addEventListener("message", handleInterfaceMessage);
  }

  document.addEventListener("keydown", event => {
    if (!active) return;

    if ((event.key === "Enter" || event.key === " ") &&
        active.kind === "dialogue" &&
        !event.ctrlKey && !event.altKey) {
      const button = layer?.querySelector("[data-interface-dialogue-continue]:not(:disabled)");
      if (button) {
        event.preventDefault();
        button.click();
      }
    }

    if (event.key >= "1" && event.key <= "9" && active.kind === "choice") {
      const index = Number(event.key);
      const button = layer?.querySelector("[data-interface-choice='" + index + "']");
      if (button && !button.disabled) {
        event.preventDefault();
        button.click();
      }
    }
  });

  window.assistWebLog?.("INFO", "Simulator interface layer готов.", {
    layer: !!layer,
    supportsDialogue: true,
    supportsChoice: true
  });
})();