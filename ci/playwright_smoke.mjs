import { chromium } from "playwright";

const browser = await chromium.launch({ headless: true });

try {
  const page = await browser.newPage();
  await page.setContent(`
    <!doctype html>
    <html lang="ru">
      <body>
        <main>
          <h1 id="title">Песочница квестов</h1>
          <button type="button" id="action">Проверка</button>
          <output id="state">Ожидание</output>
        </main>
        <script>
          document.getElementById("action").addEventListener("click", () => {
            document.getElementById("state").textContent = "Событие получено";
          });
        </script>
      </body>
    </html>
  `);

  await page.getByRole("heading", { name: "Песочница квестов" }).waitFor();
  await page.getByRole("button", { name: "Проверка" }).click();
  await page.locator("#state").waitFor();
  const state = await page.locator("#state").textContent();

  if (state !== "Событие получено") {
    throw new Error("Playwright smoke test: ожидалось «Событие получено», получено «" + state + "».");
  }

  console.log("Playwright + Chromium smoke: OK");
} finally {
  await browser.close();
}
