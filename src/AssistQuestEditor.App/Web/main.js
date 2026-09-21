
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
  };

  if (typeof window.chrome?.webview?.addEventListener === "function") {
    window.chrome.webview.addEventListener("message", handleWebviewMessage);
  }
})();
