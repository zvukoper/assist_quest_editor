// Негативные контроли для правок раунда R32 (инвентарь, дерево, плашка времени).
//
// Смысл: КАЖДАЯ новая проверка обязана упасть на «мутанте» — исходном коде с
// возвращённым дефектом. Иначе проверка не доказывает ничего: она проходит и
// тогда, когда дефект на месте. Мутант вносится во ВРЕМЕННУЮ копию дерева:
// рабочие файлы не трогаются.
//
// Проверка построена так: копия каталога исходников → мутация → прогон
// соответствующей пробы → ОЖИДАЕТСЯ ПАДЕНИЕ. Копия удаляется в любом случае.
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { execFileSync } from "node:child_process";

const root = process.cwd();
const failures = [];
const results = [];

// Мутации описывают: файл, замену и какая проверка обязана это поймать.
const mutations = [
  {
    name: "инвентарь без динамических рядов",
    file: "src/AssistQuestEditor.App/Web/inventory.js",
    probe: "inventory",
    from: "var rows = Math.max(ROWS, Math.ceil(items.length / COLUMNS));",
    to: "var rows = ROWS;",
    why: "девятнадцатый предмет должен исчезать"
  },
  {
    name: "инвентарь с прежним размером ячейки",
    file: "src/AssistQuestEditor.App/Web/theme.css",
    probe: "inventory",
    from: "--inventory-cell:39px",
    to: "--inventory-cell:78px",
    why: "ячейка обязана быть 39 px"
  },
  {
    name: "окно инвентаря без проверки сохранённой геометрии",
    file: "src/AssistQuestEditor.App/Host/InventoryForm.cs",
    probe: "inventory",
    from: 'if (!WindowGeometryStore.HasSaved("inventory"))',
    to: "if (true)",
    why: "размер и место обязаны сохраняться"
  },
  {
    name: "инвентарь без обработки закрытия",
    file: "src/AssistQuestEditor.App/Host/InventoryForm.cs",
    probe: "inventory",
    from: 'case "close_inventory":',
    to: 'case "close_inventory_disabled":',
    why: "клавиша I внутри окна обязана закрывать его"
  },
  {
    name: "плашка времени со старым зелёным «идёт»",
    file: "src/AssistQuestEditor.App/Web/theme.css",
    probe: "environment",
    from: '[data-sim-state="running"]{color:var(--accent);',
    to: '[data-sim-state="running"]{color:#11fb06;',
    why: "идёт симуляция — оранжевая, а не зелёная"
  },
  {
    name: "плашка времени с текстом состояния",
    file: "src/AssistQuestEditor.App/Web/simulator.js",
    probe: "environment",
    from: '    const autoSave = document.getElementById("simAutoSave");',
    to: '    const autoSave = document.getElementById("simAutoSave");\n    document.getElementById("simStatusText").textContent = simulationPaused ? "На паузе" : "Идет симуляция";',
    why: "подпись плашки не должна меняться по состояниям"
  },
  {
    name: "плашка паузы без пульсации",
    file: "src/AssistQuestEditor.App/Web/theme.css",
    probe: "environment",
    from: "animation:simPausePulse 1500ms ease-in-out infinite;",
    to: "animation:none;",
    why: "пауза обязана отличаться пульсацией"
  },
  {
    name: "обводка строки квеста прямоугольная",
    file: "src/AssistQuestEditor.App/Host/CampaignsForm.cs",
    probe: "storage",
    from: "using var path = RoundedPath(bounds, ButtonRadius);",
    to: "using var path = new System.Drawing.Drawing2D.GraphicsPath();",
    why: "обводка выделенной строки должна быть скруглённой"
  },
  {
    name: "кампании без отступа вложенности",
    file: "src/AssistQuestEditor.App/Host/CampaignsForm.cs",
    probe: "storage",
    from: "Margin = new Padding(TreeLevelIndent, 0, 0, 8)",
    to: "Margin = new Padding(0, 0, 0, 8)",
    why: "уровни дерева обязаны читаться по отступу слева"
  },
  {
    name: "строка квеста с подобранными отступами",
    file: "src/AssistQuestEditor.App/Host/CampaignsForm.cs",
    probe: "storage",
    from: "Anchor = AnchorStyles.Right, Margin = new Padding(0, 0, 7, 0)",
    to: "Margin = new Padding(0, 14, 7, 0)",
    why: "вертикаль обязана задаваться якорем, а не отступом"
  }
];

/**
 * Готовит временную копию дерева для мутации.
 *
 * Копируется только то, что читают пробы, и БЕЗ каталогов сборки и node_modules:
 * их перенос занял бы минуты, а нужны они только для запуска. Вместо копирования
 * они подключаются junction-ссылкой — на Windows она создаётся без прав
 * администратора, в отличие от символической.
 *
 * Прежняя версия копировала один `src`, и пробы падали на `import "playwright"`
 * ещё до своих проверок: ненулевой код возврата выглядел как «мутант пойман»,
 * хотя на самом деле проверка не запускалась.
 */
function stageCopy() {
  const copy = fs.mkdtempSync(path.join(os.tmpdir(), "aq-mutant-"));
  const skip = new Set(["node_modules", ".git", "bin", "obj", "ci-results", "LOGS"]);

  fs.cpSync(root, copy, {
    recursive: true,
    filter: source => !skip.has(path.basename(source))
  });

  // Библиотеки запуска и каталоги сборки — ссылками.
  fs.symlinkSync(path.join(root, "node_modules"), path.join(copy, "node_modules"), "junction");

  for (const project of ["AssistQuestEditor.App", "AssistQuestEditor.Domain"]) {
    const build = path.join(root, "src", project, "bin");
    if (fs.existsSync(build)) {
      fs.symlinkSync(build, path.join(copy, "src", project, "bin"), "junction");
    }
  }

  return copy;
}

// Проба запускается как отдельный процесс: её код читает файлы из cwd, поэтому
// подменять нужно ИМЕННО рабочий каталог процесса.
const probeFile = {
  inventory: "ci/inventory_smoke.mjs",
  environment: "ci/environment_clock_smoke.mjs",
  storage: "ci/world_storage_smoke.mjs"
};

/**
 * Прогон пробы в заданном каталоге.
 *
 * Возвращает разбор по УТВЕРЖДЕНИЯМ, а не «упало/не упало». Без этого негативный
 * контроль неотличим от собственной поломки проверки: синтаксическая ошибка в
 * пробе тоже дала бы ненулевой код, и мутант был бы «пойман» ни за что. Признаком
 * пойманного дефекта считается только собственное сообщение пробы о провале —
 * и то, что провалено ИМЕННО утверждение, а не весь файл целиком.
 */
function runProbe(directory, probe) {
  let stdout = "";
  let code = 0;

  try {
    stdout = execFileSync(process.execPath, [probeFile[probe]], {
      cwd: directory, stdio: "pipe", timeout: 180000, encoding: "utf8"
    });
  } catch (error) {
    code = error.status ?? 1;
    stdout = String(error.stdout ?? "");
  }

  return { code, stdout, failed: code !== 0 && /FAIL/.test(stdout) };
}

// Базовая линия: пробы обязаны ПРОЙТИ на немодифицированном дереве. Иначе
// «пойманные мутанты» означали бы, что проверки сломаны в принципе.
for (const probe of new Set(mutations.map(item => item.probe))) {
  const baseline = runProbe(root, probe);
  results.push({ name: "базовая линия " + probe, caught: null, baseline: true });

  if (baseline.code !== 0 || baseline.failed) {
    failures.push(`базовая линия «${probe}» не проходит на чистом дереве: сама проверка сломана.`);
  }
}

for (const mutation of mutations) {
  const copy = stageCopy();
  try {
    const target = path.join(copy, ...mutation.file.split("/"));
    const source = fs.readFileSync(target, "utf8");

    if (!source.includes(mutation.from)) {
      failures.push(`мутация «${mutation.name}»: образец не найден в ${mutation.file}`);
      continue;
    }

    fs.writeFileSync(target, source.split(mutation.from).join(mutation.to), "utf8");

    const result = runProbe(copy, mutation.probe);
    results.push({ name: mutation.name, caught: result.failed });

    if (!result.failed) {
      failures.push(`мутация «${mutation.name}» НЕ поймана (${mutation.why}): ` +
        `проверка проходит с дефектом (код ${result.code}).`);
    }
  } finally {
    fs.rmSync(copy, { recursive: true, force: true });
  }
}

const caughtCount = results.filter(item => item.caught === true).length;

if (failures.length) {
  console.log("Негативные контроли R32: FAIL");
  failures.forEach(item => console.log(" - " + item));
  process.exit(1);
}

console.log("Негативные контроли R32: OK базовая линия пройдена, " + caughtCount +
  " из " + mutations.length + " мутаций пойманы.");
