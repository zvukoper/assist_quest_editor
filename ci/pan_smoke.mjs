// Проверка: pan средней кнопкой «несёт» содержимое вместе с курсором.
//
// Инвариант: если pan следует за курсором, графовая точка под указателем
// НЕ меняется за время протяжки. Поэтому измеряется |g_after - g_before| для
// одной и той же экранной позиции; 0 означает «следует за курсором».
//
// Почему нужна отдельная проверка, а не только существующие pan-ассерты
// в quest_graph_smoke / scene_graph_smoke:
//
//   Они утверждают лишь, что viewBox изменился по какой-то оси. Этого мало.
//   SVG имеет width/height 100% и viewBox без явного preserveAspectRatio,
//   поэтому браузер применяет значение по умолчанию «xMidYMid meet»:
//   viewBox вписывается с сохранением пропорций и центрируется, а по одной из
//   осей появляются поля. Прежний код брал масштаб как viewBox.w / rect.w и
//   viewBox.h / rect.h — это верно ТОЛЬКО при совпадении аспектов. При
//   несовпадении pan разбегался по оси с полями, но viewBox всё равно менялся,
//   и слабые ассерты проходили. Разница видна лишь на канвасе, аспект которого
//   не совпадает с аспектом viewBox (1200x720 = 5:3).
//
// Здесь проверяются три геометрии: аспект уже viewBox, шире viewBox и точно
// равный. Плюс самопроверка: требуется, чтобы хотя бы в одной геометрии
// масштаб по осям реально отличался от «наивного», иначе проверка стала бы
// пустой и перестала ловить регрессию.

import fs from "node:fs";
import path from "node:path";
import { openBrowser, closeBrowser } from "./lib/browser.mjs";

const root = process.cwd();
const theme = fs.readFileSync(
  path.join(root, "src", "AssistQuestEditor.App", "Web", "theme.css"),
  "utf8"
);

// Допуск в графовых единицах. «Убежавший» pan даёт десятки единиц
// (замерено 21.00 и 40.50) на фоне дробного шума с плавающей точкой.
const MAX_DRIFT = 0.5;

const GEOMETRIES = [
  { width: 1000, height: 800, label: "аспект уже viewBox (1000x800 против 1200x720)" },
  { width: 1600, height: 600, label: "аспект шире viewBox (1600x600 против 1200x720)" },
  { width: 1200, height: 720, label: "аспект равен viewBox (1200x720)" }
];

const failures = [];
const check = (condition, message) => {
  if (!condition) failures.push(message);
};

const { browser } = await openBrowser();

/**
 * Прогоняет pan во всех геометриях для одного редактора.
 * Возвращает замеры: дрейф, факт сдвига viewBox и соотношение масштабов.
 */
async function measurePan({ name, source, id, html, boot }) {
  const page = await browser.newPage({ viewport: { width: 1280, height: 900 } });
  const pageErrors = [];
  page.on("pageerror", error => pageErrors.push(String(error)));

  await page.setContent(html);
  await page.addStyleTag({ content: theme });
  await page.evaluate(() => {
    window.__messages = [];
    window.__assistSend = payload => window.__messages.push(payload);
  });
  await page.addScriptTag({ content: source });

  await boot(page);

  if (!await page.locator("#" + id).count()) {
    failures.push(name + ": канвас #" + id + " не создан.");
    await page.close();
    return [];
  }

  const measurements = [];

  for (const geometry of GEOMETRIES) {
    // Размер задаётся явно, чтобы аспект канваса не зависел от вьюпорта.
    await page.evaluate(({ id, geometry }) => {
      const svg = document.getElementById(id);
      svg.style.width = geometry.width + "px";
      svg.style.height = geometry.height + "px";
    }, { id, geometry });
    await page.waitForTimeout(30);

    const geometryInfo = await page.evaluate(id => {
      const svg = document.getElementById(id);
      const rect = svg.getBoundingClientRect();
      const viewBox = svg.viewBox.baseVal;
      const ctm = svg.getScreenCTM();
      return {
        rectWidth: rect.width,
        rectHeight: rect.height,
        trueScaleX: ctm.a,
        trueScaleY: ctm.d,
        naiveScaleX: viewBox.width / rect.width,
        naiveScaleY: viewBox.height / rect.height
      };
    }, id);

    const viewBoxBefore = await page.locator("#" + id).getAttribute("viewBox");

    const result = await page.evaluate(id => {
      const svg = document.getElementById(id);
      const rect = svg.getBoundingClientRect();
      const toGraph = (clientX, clientY) => {
        const point = new DOMPoint(clientX, clientY)
          .matrixTransform(svg.getScreenCTM().inverse());
        return { x: point.x, y: point.y };
      };

      const startX = rect.left + rect.width / 2;
      const startY = rect.top + rect.height / 2;
      const deltaX = 90;
      const deltaY = 70;

      const before = toGraph(startX, startY);

      const fire = (type, clientX, clientY, buttons) => svg.dispatchEvent(
        new PointerEvent(type, {
          bubbles: true,
          cancelable: true,
          pointerId: 91,
          pointerType: "mouse",
          isPrimary: true,
          button: type === "pointermove" ? -1 : 1,
          buttons,
          clientX,
          clientY
        })
      );

      fire("pointerdown", startX, startY, 4);
      fire("pointermove", startX + deltaX, startY + deltaY, 4);
      fire("pointerup", startX + deltaX, startY + deltaY, 0);

      const after = toGraph(startX + deltaX, startY + deltaY);
      return {
        driftX: Math.abs(after.x - before.x),
        driftY: Math.abs(after.y - before.y)
      };
    }, id);

    const viewBoxAfter = await page.locator("#" + id).getAttribute("viewBox");
    const moved = viewBoxBefore !== viewBoxAfter;

    measurements.push({ geometry, geometryInfo, ...result, moved });

    check(
      moved,
      name + " [" + geometry.label + "]: pan не сдвинул viewBox — фикстура вырождена."
    );
    check(
      result.driftX <= MAX_DRIFT && result.driftY <= MAX_DRIFT,
      name + " [" + geometry.label + "]: pan не следует за курсором — дрейф (" +
        result.driftX.toFixed(2) + ", " + result.driftY.toFixed(2) +
        ") превышает " + MAX_DRIFT + "."
    );
  }

  if (pageErrors.length) {
    failures.push(name + ": ошибки выполнения — " + pageErrors.join(" | "));
  }

  await page.close();
  return measurements;
}

const editorSource = fs.readFileSync(
  path.join(root, "src", "AssistQuestEditor.App", "Web", "editor.js"),
  "utf8"
);
const sceneSource = fs.readFileSync(
  path.join(root, "src", "AssistQuestEditor.App", "Web", "sceneEditor.js"),
  "utf8"
);

try {
  const reports = [];

  reports.push(await measurePan({
    name: "Quest Graph",
    source: editorSource,
    id: "questGraphSvg",
    html: `
      <!doctype html>
      <html lang="ru">
        <body>
          <header><strong id="editorTitle"></strong></header>
          <main id="root"></main>
        </body>
      </html>
    `,
    boot: async page => {
      await page.evaluate(() => {
        history.replaceState({}, "", "#graph");
        window.dispatchEvent(new MessageEvent("message", {
          data: JSON.stringify({
            type: "quest_graph",
            graph: {
              id: "pan", name: "Pan Fixture",
              nodes: [
                {
                  nodeId: "start", nodeType: "Start", title: "Start",
                  x: 80, y: 250, parameters: {},
                  sockets: [{ socketId: "start.out", name: "Далее", direction: "Output", flowKind: "Normal" }]
                },
                {
                  nodeId: "end", nodeType: "End", title: "End",
                  x: 400, y: 250, parameters: {},
                  sockets: [{ socketId: "end.in", name: "Вход", direction: "Input", flowKind: "Normal" }]
                }
              ],
              connections: []
            },
            canUndo: false, canRedo: false, validation: [],
            documentPath: "", lastDocumentPath: "", documentDirty: false
          })
        }));
      });
      await page.locator("#questGraphSvg .node").first().waitFor();
    }
  }));

  reports.push(await measurePan({
    name: "Scene Graph",
    source: sceneSource,
    id: "sceneGraphSvg",
    html: `
      <!doctype html>
      <html lang="ru">
        <body>
          <main>
            <div id="workspace"></div>
            <div id="inspector"></div>
          </main>
        </body>
      </html>
    `,
    boot: async page => {
      await page.evaluate(() => {
        history.replaceState({}, "", "#scene");
        window.dispatchEvent(new MessageEvent("message", {
          data: JSON.stringify({ type: "scene_catalog", scenes: [{ id: "pan", title: "Pan Fixture" }] })
        }));
        window.dispatchEvent(new MessageEvent("message", {
          data: JSON.stringify({
            type: "scene_definition",
            definition: {
              id: "pan", title: "Pan Fixture", description: "",
              graph: {
                id: "pan", name: "Pan Fixture",
                nodes: [
                  {
                    nodeId: "start", nodeType: "SceneStart", title: "Start",
                    x: 80, y: 250, parameters: {},
                    sockets: [{ socketId: "start.out", name: "Далее", direction: "Output", flowKind: "Normal" }]
                  },
                  {
                    nodeId: "end", nodeType: "SceneEnd", title: "End",
                    x: 400, y: 250, parameters: {},
                    sockets: [{ socketId: "end.in", name: "Вход", direction: "Input", flowKind: "Normal" }]
                  }
                ],
                connections: []
              },
              dialogues: [], choices: []
            },
            canUndo: false, canRedo: false, validation: [],
            documentPath: "", lastDocumentPath: "", documentDirty: false
          })
        }));
      });
      await page.locator("#sceneGraphSvg .node").first().waitFor();
    }
  }));

  // Самопроверка: хотя бы в одной геометрии масштаб по осям обязан отличаться
  // от «наивного». Иначе набор геометрий перестал покрывать letterbox, и
  // проверка дрейфа стала бы бессмысленной.
  const exercise = reports.flat().some(measurement => {
    const info = measurement.geometryInfo;
    return Math.abs(info.trueScaleX - info.naiveScaleX) > 0.1 ||
      Math.abs(info.trueScaleY - info.naiveScaleY) > 0.1;
  });
  check(
    exercise,
    "Ни одна геометрия не отличается от наивного масштаба — letterbox не покрыт."
  );

  if (failures.length === 0) {
    const total = reports.flat().length;
    console.log("Pan smoke: OK (" + total + " геометрий, дрейф <= " + MAX_DRIFT + ")");
  } else {
    for (const failure of failures) console.error("FAIL: " + failure);
    process.exitCode = 1;
  }
} finally {
  await closeBrowser(browser);
}
