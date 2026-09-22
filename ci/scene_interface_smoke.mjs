import fs from "node:fs";
import path from "node:path";
import { chromium } from "playwright";

const root = process.cwd();
const source = fs.readFileSync(
  path.join(root, "src", "AssistQuestEditor.App", "Web", "interface.js"),
  "utf8"
);
const theme = fs.readFileSync(
  path.join(root, "src", "AssistQuestEditor.App", "Web", "theme.css"),
  "utf8"
);

const browser = await chromium.launch({ headless: true });
try {
  const page = await browser.newPage({ viewport: { width: 1200, height: 800 } });
  await page.setContent(
    "<!doctype html><html lang='ru'><body>" +
    "<div id='interfaceLayer' class='interfaceLayer'></div>" +
    "<script>window.__messages=[]; window.chrome={webview:{postMessage:v=>window.__messages.push(v),addEventListener:()=>{}}};</script>" +
    "</body></html>"
  );
  await page.addStyleTag({ content: theme });
  await page.addScriptTag({ content: source });

  const dialogue = {
    requestId: "scene:dialogue:test",
    title: "Руслан: приветствие",
    speaker: "Руслан",
    text: "Есть для тебя особое предложение."
  };

  await page.evaluate(value => {
    window.dispatchEvent(new MessageEvent("message", {
      data: JSON.stringify({
        type: "snapshot",
        snapshot: { interfaces: { activeDialogue: value, activeDialog: null } }
      })
    }));
  }, dialogue);

  await page.getByText("Руслан: приветствие", { exact: true }).waitFor();
  await page.getByRole("button", { name: "Продолжить", exact: true }).waitFor();

  const rendered = await page.locator(".interfaceDialogueContinue").count();
  if (rendered !== 1) throw new Error("Dialogue интерфейс не отрисовал кнопку «Продолжить».");

  await page.getByRole("button", { name: "Продолжить", exact: true }).click();

  const continueMessage = await page.evaluate(() =>
    window.__messages.find(item => item.action === "interface_dialogue_continue")
  );
  if (!continueMessage || continueMessage.requestId !== dialogue.requestId) {
    throw new Error("Dialogue интерфейс не отправил корректный interface_dialogue_continue.");
  }

  await page.evaluate(() => {
    window.dispatchEvent(new MessageEvent("message", {
      data: JSON.stringify({
        type: "snapshot",
        snapshot: {
          interfaces: {
            activeDialogue: null,
            activeDialog: {
              requestId: "scene:choice:test",
              title: "Предложение",
              speaker: "Руслан",
              text: "Возьмёшься?",
              options: [
                { id: "accept", text: "Да, берусь." },
                { id: "decline", text: "Нет." }
              ]
            }
          }
        }
      })
    }));
  });

  await page.getByRole("button", { name: "Да, берусь." }).waitFor();
  await page.getByRole("button", { name: "Да, берусь." }).click();

  const choiceMessage = await page.evaluate(() =>
    window.__messages.find(item => item.action === "interface_choice")
  );
  if (!choiceMessage || choiceMessage.requestId !== "scene:choice:test" || choiceMessage.index !== 1) {
    throw new Error("Choice интерфейс потерял существующий контракт.");
  }

  console.log("Scene interface smoke: OK");
} finally {
  await browser.close();
}
