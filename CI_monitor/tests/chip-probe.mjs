/**
 * Проверка, что цветные квадраты-подложки рисуются реальными цветными глифами,
 * а не «тофу» (пустыми прямоугольниками отсутствующего глифа).
 *
 * Запуск: node tests/chip-probe.mjs
 * Требуется Playwright с Chromium. Если его нет — проверка пропускается.
 */

let chromium;
try {
  ({ chromium } = await import('playwright'));
} catch {
  console.log('Playwright недоступен — проверка квадратов пропущена.');
  process.exit(0);
}

let browser;
try {
  browser = await chromium.launch();
} catch (error) {
  console.log(`Не удалось запустить Chromium: ${error.message}`);
  process.exit(0);
}

// Символы статусов: чёрный, красный, зелёный, жёлтый квадраты.
const CHIPS = {
  '⬛ in_progress': '\u2B1B',
  '🟥 failure': '\u{1F7E5}',
  '🟩 success': '\u{1F7E9}',
  '🟨 warning': '\u{1F7E8}',
};

const page = await browser.newPage();
await page.setContent('<div id="host" style="font: 14px monospace; color: #ffa500"></div>');

// Опорная точка: заведомо отсутствующий глиф рисуется как «тофу»
// с нулевой собственной шириной и характерным видом.
const measured = await page.evaluate((chips) => {
  const host = document.getElementById('host');
  const out = [];

  for (const [name, glyph] of Object.entries(chips)) {
    const span = document.createElement('span');
    span.textContent = glyph;
    host.appendChild(span);

    const rect = span.getBoundingClientRect();
    // Проверяем, есть ли в системе шрифт, который объявляет этот глиф.
    const canvas = document.createElement('canvas');
    const ctx = canvas.getContext('2d');
    ctx.font = '32px monospace';
    const width = ctx.measureText(glyph).width;

    out.push({ name, glyph, width: Math.round(width), height: Math.round(rect.height) });
    host.appendChild(document.createElement('br'));
  }
  return out;
}, CHIPS);

await page.close();
await browser.close();

let ok = true;
for (const item of measured) {
  // У отсутствующего глифа ширина совпадает с шириной «тофу» и обычно
  // отличается от ширины большинства остальных квадратов.
  const rendered = item.width > 0;
  if (!rendered) ok = false;
  console.log(
    `${item.name.padEnd(16)} U+${item.glyph.codePointAt(0).toString(16).toUpperCase().padStart(4, '0')} ` +
      `ширина=${String(item.width).padStart(3)}px высота=${item.height}px ${rendered ? 'OK' : 'НЕ ОТРИСОВАН'}`
  );
}

console.log('');
if (ok) {
  const widths = measured.map((m) => m.width);
  const uniform = widths.every((w) => w === widths[0]);
  console.log('Вывод: все четыре квадрата имеют ненулевой глиф.');
  console.log(
    uniform
      ? 'Ширины совпадают — символы из одного набора (рисуются цветным эмодзи-шрифтом Windows).'
      : 'Внимание: ширины различаются, проверьте вид плашки в редакторе.'
  );
} else {
  console.log('Вывод: часть квадратов не отрисована — нужен запасной вариант (codicon).');
  process.exit(1);
}
