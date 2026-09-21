/**
 * Минимальная заглушка модуля `vscode` для автоматических проверок.
 *
 * Позволяет запускать реальные `out/extension.js` и `out/poller.js` в обычном
 * Node: проверяются регистрация команд, обновление плашки, открытие страницы.
 * Заглушка реализует ТОЛЬКО то, что использует расширение.
 *
 * Не используется в работе редактора — подменяется только в тестах.
 */

class ThemeColor {
  constructor(id) {
    this.id = id;
  }
}

class Uri {
  constructor(value) {
    this.value = value;
  }

  static parse(value) {
    return new Uri(value);
  }

  toString() {
    return this.value;
  }
}

/** Один пункт строки состояния со слежением за вызовами. */
class StatusBarItem {
  constructor(alignment, priority) {
    this.alignment = alignment;
    this.priority = priority;
    this.text = '';
    this.tooltip = undefined;
    this.color = undefined;
    this.backgroundColor = undefined;
    this.command = undefined;
    this.name = undefined;
    this.accessibilityInformation = undefined;
    this.visible = false;
    this.disposed = false;
  }

  show() {
    this.visible = true;
  }

  hide() {
    this.visible = false;
  }

  dispose() {
    this.disposed = true;
  }
}

class OutputChannel {
  constructor(name) {
    this.name = name;
    this.lines = [];
    this.shown = false;
    this.disposed = false;
  }

  appendLine(line) {
    this.lines.push(line);
  }

  show() {
    this.shown = true;
  }

  dispose() {
    this.disposed = true;
  }
}

/** Настройки расширения, задаваемые тестом. */
const settings = {
  ciMonitor: {
    enabled: true,
    repository: 'zvukoper/assist_quest_editor',
    branch: 'main',
    refreshInterval: 10,
    showIdle: true,
    badgeStyle: 'square',
    notifications: 'windows',
    notifyOnStatuses: ['failure', 'success'],
    notifyOnStartup: false,
    requestTimeout: 15000,
    logLevel: 'debug',
  },
};

const state = {
  statusBarItems: [],
  outputChannels: [],
  commands: new Map(),
  openedUris: [],
  messages: [],
  configurationListeners: [],
};

/** Информационные сообщения со кнопками: { message, items } для проверок. */
const infoCalls = [];

const api = {
  StatusBarAlignment: { Left: 1, Right: 2 },
  ThemeColor,
  Uri,
  window: {
    createStatusBarItem(alignment, priority) {
      const item = new StatusBarItem(alignment, priority);
      state.statusBarItems.push(item);
      return item;
    },
    createOutputChannel(name) {
      const channel = new OutputChannel(name);
      state.outputChannels.push(channel);
      return channel;
    },
    showInformationMessage(message, ...items) {
      state.messages.push(message);
      infoCalls.push({ message, items });
      return Promise.resolve(undefined);
    },
    showWarningMessage(message, ...items) {
      state.messages.push(message);
      infoCalls.push({ message, items });
      return Promise.resolve(undefined);
    },
    showErrorMessage(message) {
      state.messages.push(message);
      return Promise.resolve(undefined);
    },
  },
  workspace: {
    workspaceFolders: undefined,
    getConfiguration(section) {
      const values = settings[section] ?? {};
      return {
        get(key, fallback) {
          return key in values ? values[key] : fallback;
        },
      };
    },
    onDidChangeConfiguration(listener) {
      state.configurationListeners.push(listener);
      return { dispose() {} };
    },
  },
  commands: {
    registerCommand(id, handler) {
      state.commands.set(id, handler);
      return { dispose() {} };
    },
    executeCommand(...args) {
      return Promise.resolve(args);
    },
  },
  env: {
    openExternal(uri) {
      state.openedUris.push(uri.toString());
      return Promise.resolve(true);
    },
  },
};

/** Внутреннее состояние для тестов и повторного запуска. */
api.__test = {
  settings,
  state,
  infoCalls,
  reset() {
    state.statusBarItems.length = 0;
    state.outputChannels.length = 0;
    state.commands.clear();
    state.openedUris.length = 0;
    state.messages.length = 0;
    state.configurationListeners.length = 0;
    infoCalls.length = 0;
  },
};

module.exports = api;
