(() => {
  const post = payload => window.chrome?.webview?.postMessage(payload);
  const stringify = value => {
    try { return typeof value === "string" ? value : JSON.stringify(value); }
    catch { return String(value); }
  };

  window.assistWebLog = (level, message, details = null) => {
    try {
      post({ action: "web_log", level, message, details });
    } catch {
    }
  };

  const originalError = console.error.bind(console);
  const originalWarn = console.warn.bind(console);

  console.error = (...args) => {
    originalError(...args);
    window.assistWebLog("ERROR", "console.error", args.map(stringify).join(" | "));
  };

  console.warn = (...args) => {
    originalWarn(...args);
    window.assistWebLog("WARN", "console.warn", args.map(stringify).join(" | "));
  };

  window.addEventListener("error", event => {
    window.assistWebLog("ERROR", "window.error", {
      message: event.message,
      filename: event.filename,
      line: event.lineno,
      column: event.colno
    });
  });

  window.addEventListener("unhandledrejection", event => {
    window.assistWebLog("ERROR", "unhandledrejection", {
      reason: stringify(event.reason)
    });
  });

  window.addEventListener("load", () => {
    window.assistWebLog("INFO", "Web document load завершён.", {
      title: document.title,
      url: location.href
    });
  });
})();
