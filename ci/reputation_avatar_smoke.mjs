// Проверка, что портрет НПЦ в списке репутации реально загружается.
//
// Причина: страницы лежат в подкаталоге Web, а ресурсы игры — в data/ рядом с
// executable. Относительный "data/images/..." со страницы Web/ уходит в
// несуществующий Web/data/... и даёт битую картинку. Симптом не виден ни тестам
// домена, ни проверкам разметки: <img> существует, ошибка только в сети.
//
// Здесь воспроизводится реальная раскладка публикации и проверяется, что
// naturalWidth > 0, то есть изображение действительно декодировано.
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { chromium } from "playwright";

const root = process.cwd();
const failures = [];

function check(condition, message) {
  if (!condition) failures.push(message);
}

const tmp = fs.mkdtempSync(path.join(os.tmpdir(), "aq-avatar-"));
try {
  // Раскладка повторяет публикацию: <root>/Web + <root>/data.
  // Навигация идёт по file://, как в приложении, поэтому проверяется именно та
  // схема резолвинга путей, которую использует WebView2.
  fs.mkdirSync(path.join(tmp, "Web"), { recursive: true });
  fs.mkdirSync(path.join(tmp, "data", "images"), { recursive: true });
  for (const name of ["simulator.js", "theme.css", "web_log.js", "interface.js"]) {
    fs.copyFileSync(
      path.join(root, "src", "AssistQuestEditor.App", "Web", name),
      path.join(tmp, "Web", name));
  }
  fs.copyFileSync(
    path.join(root, "data", "images", "avatar_placeholder.png"),
    path.join(tmp, "data", "images", "avatar_placeholder.png"));

  fs.writeFileSync(path.join(tmp, "Web", "simulator.html"), `<!doctype html>
<html lang="ru"><head><meta charset="utf-8"><link rel="stylesheet" href="theme.css">
<style>#mapCanvas,#side,#hud,#runtimeSide{display:none}</style></head>
<body>
  <canvas id="mapCanvas"></canvas>
  <div id="hud"></div><aside id="side"></aside><aside id="runtimeSide"></aside>
  <button id="backpackButton"></button><div id="inventoryNotifications"></div>
  <div id="playerOverlay">
    <section id="inventoryPanel"></section>
    <section class="gamePanel characterPanel" id="characterPanel">
      <div class="gamePanelTabs" id="characterTabs" role="tablist"></div>
      <div class="gamePanelTabBody" id="characterTabBody"></div>
    </section>
  </div>
  <script>window.chrome = { webview: { listeners: new Map(),
    addEventListener(t, h) { this.listeners.set(t, h); }, postMessage() {} } };</script>
  <script src="simulator.js"></script>
</body></html>`);

  const snapshot = {
    player: { position: { x: 0, y: 0, z: 0 }, speedKmh: 0, heading: 0, paused: false, inCab: true },
    world: { coordinateSystem: "ETS2", points: [], activeLocation: "t" },
    selection: { point: null, source: "" },
    facts: { values: {} }, questStatuses: { quests: [] },
    states: { flags: {}, variables: {}, dialogueId: null, dialogueAnchor: null },
    playerVitals: { health: 100, maxHealth: 100, energy: 100, maxEnergy: 100, hydration: 100, maxHydration: 100, fatigue: 0, maxFatigue: 100 },
    playerProgress: { money: 1500, experience: 0, reserve: 0 },
    character: { stats: { strength: 5 }, skills: [] },
    inventory: { items: {}, newItemIds: [] },
    reputation: { entries: { gosha: { npcId: "gosha", value: 400, contacted: true } } },
    npcCatalog: [{ id: "gosha", name: "Гоша", avatar: "data/images/avatar_placeholder.png", speakerAliases: ["Гоша"] }],
    reputationViews: { gosha: { value: 400, valueLabel: "+400", rangeName: "Неопасный", rangeColor: "#00a7bd", fillColor: "#44ff00", progressPercent: 4, tooltip: "t" } },
    telemetry: {}, environment: {}, system: { runtimeRunning: false, runtimeMode: "Simulator", lastEvent: "", lastTransition: "" }
  };

  const browser = await chromium.launch({ headless: true });
  try {
    const page = await browser.newPage();
    const failedRequests = [], pageErrors = [];
    page.on("requestfailed", request => failedRequests.push(decodeURIComponent(request.url())));
    page.on("pageerror", error => pageErrors.push(String(error)));

    const base = "file:///" + path.join(tmp, "Web", "simulator.html").replace(/\\/g, "/");
    await page.goto(base);
    await page.evaluate(value => {
      window.chrome.webview.listeners.get("message")({
        data: JSON.stringify({ type: "snapshot", version: "smoke", snapshot: value })
      });
    }, snapshot);
    await page.waitForTimeout(600);

    const tabs = await page.evaluate(() =>
      [...document.querySelectorAll("[data-character-tab]")].map(b => b.dataset.characterTab));
    check(tabs.includes("reputation"), "правая панель не содержит таба «Репутация»");

    await page.evaluate(() => document.querySelector("[data-character-tab='reputation']")?.click());
    await page.waitForTimeout(500);

    const avatar = await page.evaluate(() => {
      const img = document.querySelector(".reputationAvatar");
      if (!img) return null;
      return { attr: img.getAttribute("src"), width: img.naturalWidth, complete: img.complete };
    });

    check(avatar !== null, "в списке репутации нет элемента .reputationAvatar");
    if (avatar) {
      // Главный ассерт: картинка декодирована, а не просто объявлена в разметке.
      check(avatar.width > 0, `портрет не загрузился: naturalWidth=${avatar.width}, src=${avatar.attr}`);
      // Путь должен вести к корню приложения ("../data/..."), а не оставаться
      // относительным от Web/, где ресурса нет.
      check(
        typeof avatar.attr === "string" && avatar.attr.startsWith("../data/"),
        `путь портрета должен быть относительным от корня приложения, получено: ${avatar.attr}`);
    }

    check(pageErrors.length === 0, "ошибки страницы: " + pageErrors.join(" | "));
    check(failedRequests.length === 0, "неудачные запросы: " + failedRequests.join(", "));

    // Самопроверка не менее важна основного ассерта: она доказывает, что
    // харнесс действительно различает две формы пути. Иначе проверка
    // «зеленеет» и тогда, когда ресурс вообще не запрашивался.
    const forms = await page.evaluate(() => new Promise(resolve => {
      const results = {};
      const make = (key, src) => new Promise(done => {
        const img = new Image();
        img.onload = () => { results[key] = img.naturalWidth; done(); };
        img.onerror = () => { results[key] = 0; done(); };
        img.src = src;
      });
      Promise.all([
        make("webRelative", "data/images/avatar_placeholder.png"),
        make("rootRelative", "../data/images/avatar_placeholder.png")
      ]).then(() => resolve(results));
    }));
    check(
      forms.webRelative === 0,
      `форма из Web/ должна быть битой, получено naturalWidth=${forms.webRelative}`);
    check(
      forms.rootRelative > 0,
      `форма ../data/ должна загружаться, получено naturalWidth=${forms.rootRelative}`);
  } finally {
    await browser.close();
  }
} finally {
  fs.rmSync(tmp, { recursive: true, force: true });
}

if (failures.length) {
  console.error("Проверка портрета репутации не пройдена:");
  for (const failure of failures) console.error("- " + failure);
  process.exit(1);
}

console.log("Портрет НПЦ в списке репутации загружается через путь ../data/ от корня приложения.");
