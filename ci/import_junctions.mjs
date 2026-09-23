// Импорт перекрёстков дорожной сети из репозитория ETS2 Assist.
//
// Зачем отдельный шаг, а не расчёт в редакторе: поиск перекрёстков требует
// НОДИРОВКИ всей сети — рассечения 98 341 отрезка во всех пересечениях и сборки
// графа. Это сотни миллисекунд и десятки мегабайт промежуточных структур; при
// каждом старте редактора такое недопустимо. Готовый список весит ~30 КБ.
//
// Почему источник — GeoJSON, а не data/world/roads.json: roads.json уже развёрнут
// в плоские отрезки и потерял признак принадлежности полилинии (uid). Для сшивки
// «конец полилинии -> чужая дорога» этот признак обязателен, иначе своя же
// полилиния считалась бы чужой дорогой и давала ложные узлы.
//
// МЕТОД (проверен замерами, см. MemoryAI/ARCHITECTURE.md):
//  1. Нодировка: рассечь каждый отрезок во всех внутренних пересечениях.
//  2. Сшивка «конец -> линия» (SLACK): если конец полилинии ближе SLACK к чужой
//     дороге, спроецировать его на эту дорогу. Без этого шага теряются Т-образные
//     примыкания: полилиния упирается в КРАЙ поперечной дороги, а не в осевую
//     линию (ширина проезжей части ~13 м). Замер: +351 Т-образный узел.
//  3. Склейка совпадающих концов дуг в узлы (SNAP).
//  4. Сбор направлений примыкающих дуг у каждого узла.
//  5. Перекрёсток = узел с >= 3 РАЗЛИЧНЫМИ направлениями. Направления, совпадающие
//     в пределах DIR_MERGE, схлопываются: одна дорога = одно направление, иначе
//     дубли полос (6% отрезков имеют параллельную «двойню» на зазоре 13 м) давали
//     бы перекрёсток на каждом повороте.
//  6. Дедупликация узлов ближе DEDUP (соседние узлы одного перекрёстка).
//
// Проверка качества (замер на реальных данных): минимальный угол между ветками
// у 85.7% Т-образных >= 45°, подозрительных (< 15°, две ветки в одну сторону) — 0%.
//
// Запуск:
//   node ci/import_junctions.mjs <путь к roads.geojson> <выходной junctions.json>

import fs from "node:fs";
import path from "node:path";

const [source, target] = process.argv.slice(2);

if (!source || !target) {
  console.error("Использование: node ci/import_junctions.mjs <roads.geojson> <junctions.json>");
  process.exit(2);
}

if (!fs.existsSync(source)) {
  console.error("Файл дорог не найден: " + source);
  process.exit(2);
}

// --- Параметры метода. Значения проверены на устойчивость (см. отчёт). ---

/** Допуск сшивки концов дуг в узел, метры. 1–15 м дают разброс ~6% (проверено). */
const SNAP = 6;

/** Насколько конец полилинии может «не дотягивать» до осевой чужой дороги, метры. */
const SLACK = 20;

/** Схлопывание направлений: две ветки ближе этого угла — одна дорога. */
const DIR_MERGE_DEG = 30;

/** Объединение соседних узлов одного перекрёстка, метры. */
const DEDUP = 40;

/** Размер ячейки пространственной сетки для поиска пересечений, метры. */
const CELL = 250;

const geo = JSON.parse(fs.readFileSync(source, "utf8"));
if (geo?.type !== "FeatureCollection" || !Array.isArray(geo.features)) {
  console.error("Ожидался GeoJSON FeatureCollection. Получено: " + geo?.type);
  process.exit(2);
}

// Отрезки с признаком полилинии: `fi` нужен, чтобы отличать «дорога сама с собой»
// от схождения РАЗНЫХ дорог.
const segs = [];
const ends = [];
const polylineCount = geo.features.length;

geo.features.forEach((f, fi) => {
  const c = f?.geometry?.coordinates;
  if (!Array.isArray(c) || c.length < 2) return;
  for (let i = 1; i < c.length; i++) {
    const x1 = Number(c[i - 1][0]), z1 = Number(c[i - 1][1]);
    const x2 = Number(c[i][0]), z2 = Number(c[i][1]);
    if (![x1, z1, x2, z2].every(Number.isFinite)) continue;
    segs.push({ x1, z1, x2, z2, fi });
  }
  ends.push({ x: Number(c[0][0]), z: Number(c[0][1]), fi });
  ends.push({ x: Number(c[c.length - 1][0]), z: Number(c[c.length - 1][1]), fi });
});

console.log(`Полилиний: ${polylineCount}; отрезков: ${segs.length}; концов: ${ends.length}`);

// --- Пространственная сетка ---
const grid = new Map();
segs.forEach((s, i) => {
  const minx = Math.min(s.x1, s.x2), maxx = Math.max(s.x1, s.x2);
  const minz = Math.min(s.z1, s.z2), maxz = Math.max(s.z1, s.z2);
  for (let cx = Math.floor(minx / CELL); cx <= Math.floor(maxx / CELL); cx++)
    for (let cz = Math.floor(minz / CELL); cz <= Math.floor(maxz / CELL); cz++) {
      const k = `${cx}|${cz}`;
      if (!grid.has(k)) grid.set(k, []);
      grid.get(k).push(i);
    }
});

// --- Шаг 1. Нодировка: параметры рассечения каждого отрезка ---
const cuts = segs.map(() => [0, 1]);
let intersections = 0;
{
  const seen = new Set();
  for (const ids of grid.values()) {
    for (let i = 0; i < ids.length; i++) {
      for (let j = i + 1; j < ids.length; j++) {
        const a = ids[i], b = ids[j];
        const pk = a < b ? `${a}|${b}` : `${b}|${a}`;
        if (seen.has(pk)) continue;
        seen.add(pk);

        const A = segs[a], B = segs[b];
        const d1x = A.x2 - A.x1, d1z = A.z2 - A.z1;
        const d2x = B.x2 - B.x1, d2z = B.z2 - B.z1;
        const den = d1x * d2z - d1z * d2x;
        // Параллельные (в том числе совпадающие полосы) пересечений не дают.
        if (Math.abs(den) < 1e-12) continue;

        const t = ((B.x1 - A.x1) * d2z - (B.z1 - A.z1) * d2x) / den;
        const u = ((B.x1 - A.x1) * d1z - (B.z1 - A.z1) * d1x) / den;
        // Строго внутренние: касание концами — это стык, он обрабатывается SNAP.
        if (t < 1e-9 || t > 1 - 1e-9 || u < 1e-9 || u > 1 - 1e-9) continue;

        cuts[a].push(t);
        cuts[b].push(u);
        intersections++;
      }
    }
  }
}
console.log(`Точек пересечения: ${intersections}`);

// --- Шаг 2. Сшивка «конец -> линия» ---
const project = (px, pz, s) => {
  const dx = s.x2 - s.x1, dz = s.z2 - s.z1;
  const L2 = dx * dx + dz * dz;
  let t = L2 > 0 ? ((px - s.x1) * dx + (pz - s.z1) * dz) / L2 : 0;
  t = Math.max(0, Math.min(1, t));
  return { t, d: Math.hypot(px - (s.x1 + t * dx), pz - (s.z1 + t * dz)) };
};

let snapped = 0;
for (const e of ends) {
  if (!Number.isFinite(e.x) || !Number.isFinite(e.z)) continue;

  const gx = Math.floor(e.x / CELL), gz = Math.floor(e.z / CELL);
  const cells = Math.max(1, Math.ceil(SLACK / CELL));
  const done = new Set();
  let best = null;

  for (let dx = -cells; dx <= cells; dx++)
    for (let dz = -cells; dz <= cells; dz++)
      for (const si of grid.get(`${gx + dx}|${gz + dz}`) || []) {
        if (done.has(si)) continue;
        done.add(si);
        // Своя же полилиния не считается: иначе её концы «примыкали» бы к самой себе.
        if (segs[si].fi === e.fi) continue;
        const p = project(e.x, e.z, segs[si]);
        if (p.d <= SLACK && (!best || p.d < best.d)) best = { si, ...p };
      }

  if (best && best.t > 1e-9 && best.t < 1 - 1e-9) {
    cuts[best.si].push(best.t);
    snapped++;
  }
}
console.log(`Сшивок «конец -> линия»: ${snapped}`);

// --- Шаг 3. Узлы: склейка концов дуг ---
const nodePoints = [];
const nodeGrid = new Map();

function nodeAt(x, z) {
  const gx = Math.floor(x / SNAP), gz = Math.floor(z / SNAP);
  for (let dx = -1; dx <= 1; dx++)
    for (let dz = -1; dz <= 1; dz++)
      for (const ni of nodeGrid.get(`${gx + dx}|${gz + dz}`) || []) {
        const n = nodePoints[ni];
        if (Math.hypot(n.x - x, n.z - z) <= SNAP) {
          n.sumX += x; n.sumZ += z; n.count++;
          return ni;
        }
      }

  const ni = nodePoints.length;
  nodePoints.push({ x, z, sumX: x, sumZ: z, count: 1, arms: [] });
  const k = `${gx}|${gz}`;
  if (!nodeGrid.has(k)) nodeGrid.set(k, []);
  nodeGrid.get(k).push(ni);
  return ni;
}

// --- Шаг 4. Дуги между рассечениями + направления примыкания ---
segs.forEach((s, i) => {
  const ts = [...new Set(cuts[i])].sort((a, b) => a - b);
  const dx = s.x2 - s.x1, dz = s.z2 - s.z1;

  for (let k = 0; k + 1 < ts.length; k++) {
    const t0 = ts[k], t1 = ts[k + 1];
    if (t1 - t0 < 1e-12) continue;

    const ax = s.x1 + t0 * dx, az = s.z1 + t0 * dz;
    const bx = s.x1 + t1 * dx, bz = s.z1 + t1 * dz;
    const na = nodeAt(ax, az), nb = nodeAt(bx, bz);
    if (na === nb) continue;  // петля нулевой длины

    nodePoints[na].arms.push(Math.atan2(bz - az, bx - ax));
    nodePoints[nb].arms.push(Math.atan2(az - bz, ax - bx));
  }
});

// --- Шаг 5. Перекрёстки: >= 3 различных направлений ---
const mergeRad = (DIR_MERGE_DEG * Math.PI) / 180;
const raw = [];

for (const np of nodePoints) {
  if (np.arms.length < 3) continue;

  const uniq = [];
  for (const a of np.arms) {
    // Полный угол (0..2PI), а не mod PI: сквозная дорога обязана давать ДВА
    // направления. Схлопывание по mod PI сделало бы Т-образный узел
    // неотличимым от сквозного проезда.
    const norm = ((a % (2 * Math.PI)) + 2 * Math.PI) % (2 * Math.PI);
    const close = uniq.some(u => {
      let d = Math.abs(u - norm);
      if (d > Math.PI) d = 2 * Math.PI - d;
      return d < mergeRad;
    });
    if (!close) uniq.push(norm);
  }

  if (uniq.length < 3) continue;
  raw.push({ x: np.sumX / np.count, z: np.sumZ / np.count, branches: uniq.length });
}

// --- Шаг 6. Дедупликация ---
const dedup = [];
for (const j of raw.sort((a, b) => b.branches - a.branches)) {
  if (dedup.some(d => Math.hypot(d.x - j.x, d.z - j.z) < DEDUP)) continue;
  dedup.push(j);
}

console.log(`Узлов-кандидатов: ${raw.length}; перекрёстков после дедупликации: ${dedup.length}`);

const byBranches = new Map();
for (const j of dedup) byBranches.set(j.branches, (byBranches.get(j.branches) || 0) + 1);
console.log("ветки: " + [...byBranches.entries()].sort((a, b) => a[0] - b[0])
  .map(([b, c]) => `${b}:${c}`).join(" "));

// Плоский массив x,z — тот же приём, что у дорог: JSON из тысяч объектов
// читается заметно дольше, а для поиска ближайшего нужны только координаты.
const round = v => Math.round(v * 100) / 100;
const points = [];
for (const j of dedup) points.push(round(j.x), round(j.z));

const payload = {
  schemaVersion: 1,
  source: "zvukoper/ets2_assist:data/GeoJson/roads.geojson",
  method: `noding+snap${SNAP}m+slack${SLACK}m+dir${DIR_MERGE_DEG}deg+dedup${DEDUP}m`,
  units: "meters",
  layout: "x,z",
  junctionCount: dedup.length,
  // Распределение по числу веток пишется В ДАННЫЕ, а не только в консоль.
  // По нему проверка видит, что сшивка «конец -> линия» действительно
  // сработала: без неё доля Т-образных примыканий падает почти до нуля, и
  // подмена одного шага построения не останется незамеченной.
  branches: [...byBranches.entries()].sort((a, b) => a[0] - b[0]),
  points
};

fs.mkdirSync(path.dirname(target), { recursive: true });
fs.writeFileSync(target, JSON.stringify(payload), "utf8");

const sizeKb = (fs.statSync(target).size / 1024).toFixed(1);
console.log(`Записано: ${target} — ${sizeKb} КБ, ${dedup.length} перекрёстков.`);
