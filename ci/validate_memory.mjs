import fs from "node:fs";
import path from "node:path";

const root = process.cwd();

const required = [
  "README.md",
  "MemoryAI/PROJECT_MEMORY.md",
  "MemoryAI/ARCHITECTURE.md",
  "MemoryAI/IMPLEMENTATION_PLAN.md",
  "MemoryAI/CURRENT_TASK.md",
  "MemoryAI/INSTRUCTIONS.md",
  "MemoryAI/CHANGELOG.md",
  "MemoryAI/LOGS/README.md",
  ".github/workflows/ci.yml"
];

const missing = required.filter(file => !fs.existsSync(path.join(root, file)));

if (missing.length) {
  console.error("Не найдены обязательные файлы:");
  for (const file of missing) console.error("- " + file);
  process.exit(1);
}

const instructions = fs.readFileSync(path.join(root, "MemoryAI/INSTRUCTIONS.md"), "utf8");
const requiredRules = [
  "ветки",
  "Chromium",
  "Playwright",
  "CI",
  "runtime patch"
];

for (const marker of requiredRules) {
  if (!instructions.includes(marker)) {
    console.error("MemoryAI/INSTRUCTIONS.md не содержит обязательное правило: " + marker);
    process.exit(1);
  }
}

const currentTask = fs.readFileSync(path.join(root, "MemoryAI/CURRENT_TASK.md"), "utf8");
if (!currentTask.includes("1.0.40.101-QUEST-EDITOR-DUAL-WINDOW-R1")) {
  console.error("Базовая версия песочницы не зафиксирована.");
  process.exit(1);
}

const logDir = path.join(root, "MemoryAI/LOGS");
const entries = fs.readdirSync(logDir);

// В каталоге логов допустимы только диагностические файлы. Состояние локального
// прогона и признак активности редактора лежат в `.ci-state`, потому что папка
// логов очищается после успешной публикации.
const allowedFiles = new Set(["README.md", "CI_errors.md"]);

const unexpected = entries.filter(
  (name) => !allowedFiles.has(name) && !name.toLowerCase().endsWith(".log")
);

if (unexpected.length) {
  console.error("MemoryAI/LOGS может содержать только README.md, CI_errors.md и временные .log:");
  for (const name of unexpected) console.error("- " + name);
  process.exit(1);
}

console.log("Проверка памяти: OK (" + required.length + " обязательных файлов). Временно разрешены диагностические .log; compile.ps1 очищает их перед публикацией.");
