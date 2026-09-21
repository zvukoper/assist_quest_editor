/**
 * Диагностическая проверка гипотезы: рендерит ли браузер управляющие
 * последовательности ANSI как цвет в обычном текстовом узле.
 *
 * Именно так VS Code вставляет текст в пункт строки состояния — см.
 * `renderLabelWithIcons` в `src/vs/base/browser/ui/iconLabel/iconLabels.ts`:
 * текст разбивается на строки и `<span class="codicon">`, без какой-либо
 * обработки ANSI. Поэтому проверка ожидаемо показывает, что коды видны как
 * мусор, а фон не применяется — это обоснование стиля `ciMonitor.badgeStyle`.
 *
 * Запуск: node tests/ansi-probe.mjs
 * Требуется Playwright с Chromium. Если его нет — скрипт сообщает об этом и
 * завершается успешно, потому что проверка вспомогательная.
 */

let chromium;
try {
  ({ chromium } = await import('playwright'));
} catch {
  console.log('Playwright недоступен — проверка ANSI пропущена.');
  console.log('Установка: npm install --no-save playwright && npx playwright install chromium');
  process.exit(0);
}

const ANSI = {
  black: '\u001b[48;2;0;0;0m',
  orange: '\u001b[38;2;255;165;0m',
  green: '\u001b[48;2;26;127;55m',
  reset: '\u001b[0m',
};

let browser;
try {
  browser = await chromium.launch();
} catch (error) {
  console.log(`Не удалось запустить Chromium: ${error.message}`);
  process.exit(0);
}

const page = await browser.newPage();
// Разметка повторяет структуру пункта строки состояния VS Code:
// <li class="statusbar-item"><a><span class="statusbar-item-label">текст</span></a></li>
await page.setContent(`
  <div id="statusbar" style="font-family: monospace; font-size: 11px">
    <div class="statusbar-item" id="running">
      <a><span class="statusbar-item-label" style="display:inline-block"></span></a>
    </div>
  </div>
`);

const results = await page.evaluate((ansi) => {
  const target = document.querySelector('#running .statusbar-item-label');
  // Именно так VS Code задаёт текст: обычным текстовым узлом, без разметки.
  const text = `${ansi.black}${ansi.orange}$(sync~spin) #147${ansi.reset}`;
  target.textContent = text;

  const style = getComputedStyle(target);
  return {
    backgroundColor: style.backgroundColor,
    color: style.color,
    // Если управляющие коды видны как текст — в innerText окажутся лишние символы.
    innerTextCodepoints: Array.from(target.innerText).map((c) => c.codePointAt(0)),
    // Ширина текста: видимые ESC-коды заметно увеличивают её.
    width: target.getBoundingClientRect().width,
  };
}, ANSI);

await page.close();
await browser.close();

const visible = results.innerTextCodepoints.filter((code) => code === 0x1b).length;
const colored = results.backgroundColor !== 'rgba(0, 0, 0, 0)' && results.backgroundColor !== 'transparent';

console.log(`ESC-символов в видимом тексте: ${visible}`);
console.log(`Вычисленный цвет текста:       ${results.color}`);
console.log(`Вычисленный фон:               ${results.backgroundColor}`);
console.log(`Фон применён:                  ${colored ? 'да' : 'нет'}`);

if (colored && visible === 0) {
  console.log('');
  console.log('Вывод: браузер отрисовал ANSI как цвет, управляющие символы не видны.');
  console.log('Значит цвета плашки (чёрный фон + оранжевый значок, зелёный, красный) работают.');
} else {
  console.log('');
  console.log('Вывод: ANSI в текстовом узле не даёт цвет.');
  console.log('Плашке следует работать без ANSI: отключите ciMonitor.useAnsiColors.');
}
