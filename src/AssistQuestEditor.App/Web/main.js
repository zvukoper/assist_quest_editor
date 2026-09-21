(() => {
  const send = payload => window.chrome?.webview?.postMessage(payload);
  document.querySelectorAll("[data-editor]").forEach(button => {
    button.addEventListener("click", () => send({ action: "open_editor", editor: button.dataset.editor }));
  });
  document.querySelectorAll("[data-action]").forEach(button => {
    button.addEventListener("click", () => send({ action: button.dataset.action }));
  });
})();
