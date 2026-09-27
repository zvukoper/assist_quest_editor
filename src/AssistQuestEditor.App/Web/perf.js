// Инструмент замера производительности карты симулятора.
//
// Зачем отдельный файл, а не «DevTools руками»: по задаче нужно было не просто
// увидеть тормоза, а ИЗМЕРИТЬ, какой слой карты их вызывает, и сравнивать
// замеры между правками. Ручной профилировщик для этого неудобен: он не
// сохраняет результат в машиночитаемом виде и его нельзя прогнать из CI.
//
// Принцип работы:
//  * счётчики ВЫКЛЮЧЕНЫ по умолчанию — вызовы start()/end() в отрисовке стоят
//    копейки (проверка флага), поэтому рабочий кадр не замедляется;
//  * включение — флагом `?perf=1` в URL или `window.AQ_PERF = true` до загрузки;
//  * замеры накапливаются по секциям (сетка+дороги, точки, квесты, маршрут,
//    игрок, HUD и т.д.) и по кадрам (время кадра → FPS и перцентили);
//  * отчёт отдаётся через `window.AssistPerf.report()` — его и снимает
//    `ci/perf_probe.mjs`, а также можно вызвать руками из консоли WebView.
//
// ВАЖНО: имя `AssistPerf` выбрано рядом с `AssistVitals` и `assistWebLog`, чтобы
// не занимать короткие общие имена в window.
(() => {
  const query = typeof location !== "undefined" ? String(location.search || "") : "";
  let enabled = window.AQ_PERF === true || /(?:^|[?&])perf=1(?:&|$)/.test(query);

  /** Секции отрисовки: имя → накопленные значения (мс). */
  const buckets = new Map();

  /** Длительности кадров (мс), ограниченное окно. */
  const frameTimes = [];
  const FRAME_WINDOW = 900;

  let frameStart = 0;
  let frameCount = 0;
  let recording = enabled;

  function start() {
    if (!enabled) return 0;
    return performance.now();
  }

  function end(label, startedAt) {
    if (!enabled || !startedAt) return;
    const elapsed = performance.now() - startedAt;
    let bucket = buckets.get(label);
    if (!bucket) {
      bucket = { count: 0, total: 0, max: 0 };
      buckets.set(label, bucket);
    }
    bucket.count += 1;
    bucket.total += elapsed;
    if (elapsed > bucket.max) bucket.max = elapsed;
  }

  /**
   * Отметка кадра. Вызывается в начале drawMap.
   *
   * Время кадра считается между двумя вызовами frame(), поэтому первый кадр
   * после включения всегда пропускается — иначе в статистику попал бы разрыв
   * между загрузкой страницы и первой отрисовкой.
   */
  function frame() {
    if (!enabled) return;
    const now = performance.now();
    if (frameStart) {
      frameTimes.push(now - frameStart);
      if (frameTimes.length > FRAME_WINDOW) frameTimes.shift();
    }
    frameStart = now;
    frameCount += 1;
  }

  function percentile(sorted, ratio) {
    if (!sorted.length) return 0;
    const index = Math.min(sorted.length - 1, Math.max(0, Math.round((sorted.length - 1) * ratio)));
    return sorted[index];
  }

  function report() {
    const sortedFrames = frameTimes.slice().sort((a, b) => a - b);
    const frameTotal = sortedFrames.reduce((sum, value) => sum + value, 0);
    const frameAvg = sortedFrames.length ? frameTotal / sortedFrames.length : 0;

    const sections = [...buckets.entries()].map(([label, bucket]) => ({
      section: label,
      calls: bucket.count,
      totalMs: Number(bucket.total.toFixed(1)),
      avgMs: Number((bucket.count ? bucket.total / bucket.count : 0).toFixed(3)),
      maxMs: Number(bucket.max.toFixed(2))
    })).sort((a, b) => b.totalMs - a.totalMs);

    return {
      enabled,
      frames: frameTimes.length,
      frameCount,
      fps: frameAvg > 0 ? Number((1000 / frameAvg).toFixed(1)) : null,
      frameAvgMs: Number(frameAvg.toFixed(2)),
      frameP50Ms: Number(percentile(sortedFrames, 0.5).toFixed(2)),
      frameP95Ms: Number(percentile(sortedFrames, 0.95).toFixed(2)),
      frameMaxMs: Number(percentile(sortedFrames, 1).toFixed(2)),
      sections
    };
  }

  function reset() {
    buckets.clear();
    frameTimes.length = 0;
    frameStart = 0;
    frameCount = 0;
  }

  function setEnabled(value) {
    enabled = !!value;
    recording = enabled;
    if (!enabled) reset();
  }

  window.AssistPerf = {
    get enabled() { return enabled; },
    get recording() { return recording; },
    setEnabled,
    start,
    end,
    frame,
    report,
    reset
  };

  window.AQ_PERF_ENABLED = enabled;
})();
