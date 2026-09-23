// Импорт перекрёстков дорожной сети из репозитория ETS2 Assist.
//
// ДВА НЕЗАВИСИМЫХ МЕТОДА, потому что перекрёстки в этих данных бывают двух
// физических видов, и ни один метод не ловит оба:
//
//   A. НОДИРОВКА (--method crossing) — пересечение ЛИНИЙ. Работает там, где
//      полотна действительно пересекаются координатами. Требует GeoJSON: для
//      сшивки «конец полилинии -> чужая дорога» нужен признак принадлежности
//      полилинии (uid), иначе своя же полилиния считается чужой дорогой.
//      НЕ ВИДИТ разрывов: полотно обрывается, не доходя до перекрёстка, поэтому
//      фактического пересечения нет вообще (автор отметил 97 таких узлов
//      вручную, и ни один из них этот метод не нашёл).
//
//   B. РАЗРЫВЫ ПОЛОТНА (--method gaps, по умолчанию) — именно то, что автор
//      видит глазами: полотно ПРЕРЫВАЕТСЯ, и в сторону отходит другое полотно,
//      а между линиями остаётся пустое пятно 10-25 м. Два правила:
//        1) сходящиеся обрывы: продолжения обрывов сходятся в пустом пятне, и
//           там набирается >=3 голоса с РАЗНЫХ направлений (схлопывание 30°);
//        2) примыкание по лучу: продолжение ОДИНОЧНОГО обрыва упирается в ЧУЖОЕ
//           полотно поперёк (>=30°) на 4-50 м — «перекрёсток из одной дороги»;
//           сквозная дорога при этом нарисована целой и разрыва не даёт.
//
//      Работает по `data/world/roads.json` — уже развёрнутым отрезкам, нодировка
//      не нужна. Кольца (roundabout) ловятся правилом 1: короткие отрезки по
//      кругу дают обрывы со всех направлений.
//
// Проверка на 97 узлах, размеченных автором вручную: метод B находит 96 (99%),
// медиана расхождения 9 м. Метод A не находил НИ ОДНОГО из них — поэтому он
// оставлен только как дополнение для настоящих пересечений координат.
//
// Метод A (нодировка) требует GeoJSON: рассечения 98 341 отрезка в пересечениях
// и сборки графа — это сотни миллисекунд и десятки мегабайт промежуточных
// структур, поэтому результат готовится заранее, а не считается в редакторе.
//
// Запуск:
//   node ci/import_junctions.mjs <roads.geojson> <выходной junctions.json>
//   node ci/import_junctions.mjs --method gaps <roads.json> <выходной junctions.json>
//   node ci/import_junctions.mjs --method both <roads.geojson> <выходной junctions.json> --roads <roads.json>

import fs from "node:fs";
import path from "node:path";

// --- Аргументы ---
// Разбор вручную, а не через парсер: у скрипта два входных файла разного
// назначения, и порядок аргументов должен читаться из строки запуска.
const argv = process.argv.slice(2);
let method = "both";
let roadsPath = null;
const positional = [];

for (let i = 0; i < argv.length; i++) {
  if (argv[i] === "--method") { method = argv[++i]; continue; }
  if (argv[i] === "--roads") { roadsPath = argv[++i]; continue; }
  positional.push(argv[i]);
}

const [source, target] = positional;

if (!source || !target || !["crossing", "gaps", "both"].includes(method)) {
  console.error("Использование: node ci/import_junctions.mjs [--method crossing|gaps|both] " +
    "<roads.geojson|roads.json> <junctions.json> [--roads <roads.json>]");
  process.exit(2);
}

if (!fs.existsSync(source)) {
  console.error("Файл дорог не найден: " + source);
  process.exit(2);
}

// Европейская часть исключается: она не участвует в игровом мире.
//
// Граница получена ИЗ ДАННЫХ, а не назначена: в дорожном наборе между Европой и
// Сибирью есть разрыв 45 км без дорог (столбцы по 5 км с 70000 по 115000), и все
// 77 городов игрового мира лежат правее. Рабочая область — Казань..Барабинск.
const EUROPE_MAX_X = 115_000;

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

// --- Параметры метода «разрывы полотна» ---

/** Разрешение сетки голосования за центр перекрёстка, метры. */
const VOTE_CELL = 20;

/** Где ставится голос вдоль продолжения обрыва: от MIN до MAX, с шагом STEP. */
const VOTE_MIN = 10;
const VOTE_MAX = 120;
const VOTE_STEP = 5;

/** Минимум РАЗЛИЧНЫХ направлений, чтобы место считалось перекрёстком. */
const VOTE_MIN_DIRS = 3;

/** Примыкание по лучу: попадание в чужое полотно и угол к подходу. */
const TEE_MIN = 4;
const TEE_MAX = 50;
const TEE_ANGLE_DEG = 30;

/**
 * Метод «разрывы полотна» — основной.
 *
 * Перекрёсток здесь не пересечение линий, а место, где полотно ПРЕРЫВАЕТСЯ. Два
 * правила, потому что случаи физически разные:
 *  1) продолжения НЕСКОЛЬКИХ обрывов сходятся в пустом пятне — там набирается
 *     голоса с разных направлений (крест, Т-образное, кольцо);
 *  2) продолжение ОДИНОЧНОГО обрыва упирается в ЧУЖОЕ полотно поперёк — сквозная
 *     дорога нарисована целой и разрыва не даёт, а боковая обрывается.
 */
function detectGaps(segments) {
  const segs = [];
  const segGrid = new Map();

  for (let i = 0; i < segments.length; i += 4) {
    const x1 = segments[i], z1 = segments[i + 1];
    const x2 = segments[i + 2], z2 = segments[i + 3];
    // Европейская часть не участвует в игровом мире.
    if (Math.max(x1, x2) < EUROPE_MAX_X) continue;

    const s = { x1, z1, x2, z2, dirX: x2 - x1, dirZ: z2 - z1 };
    segs.push(s);
    for (let cx = Math.floor(Math.min(x1, x2) / CELL); cx <= Math.floor(Math.max(x1, x2) / CELL); cx++)
      for (let cz = Math.floor(Math.min(z1, z2) / CELL); cz <= Math.floor(Math.max(z1, z2) / CELL); cz++) {
        const k = `${cx}|${cz}`;
        if (!segGrid.has(k)) segGrid.set(k, []);
        segGrid.get(k).push(s);
      }
  }

  const segsNear = (px, pz, radius) => {
    const reach = Math.ceil(radius / CELL) + 1;
    const found = [];
    const cx = Math.floor(px / CELL), cz = Math.floor(pz / CELL);
    for (let ix = cx - reach; ix <= cx + reach; ix++)
      for (let iz = cz - reach; iz <= cz + reach; iz++)
        found.push(...(segGrid.get(`${ix}|${iz}`) || []));
    return found;
  };

  // --- Обрывы: конец, к которому подходит ровно ОДИН отрезок ---
  // Сравнение с округлением до дециметра: координаты хранятся сантиметрами, и
  // точного совпадения пар концов ждать нельзя.
  const endMap = new Map();
  for (const s of segs) {
    for (const [x, z, ox, oz] of [[s.x1, s.z1, s.x2, s.z2], [s.x2, s.z2, s.x1, s.z1]]) {
      const k = `${x.toFixed(1)}|${z.toFixed(1)}`;
      const e = endMap.get(k) || { x, z, n: 0, dirX: x - ox, dirZ: z - oz };
      e.n++;
      endMap.set(k, e);
    }
  }

  const dangling = [...endMap.values()].filter(e => e.n === 1);
  for (const e of dangling) {
    const len = Math.hypot(e.dirX, e.dirZ) || 1;
    e.ux = e.dirX / len;
    e.uz = e.dirZ / len;
    e.angle = Math.atan2(e.uz, e.ux);
  }
  console.log(`Обрывов полотна: ${dangling.length}`);

  /** Схлопывание близких углов: одна дорога = одно направление. */
  const mergeAngles = (angles) => {
    const mergeRad = (DIR_MERGE_DEG * Math.PI) / 180;
    const uniq = [];
    for (const a of angles) {
      const norm = ((a % (2 * Math.PI)) + 2 * Math.PI) % (2 * Math.PI);
      if (!uniq.some(u => {
        let d = Math.abs(u - norm);
        if (d > Math.PI) d = 2 * Math.PI - d;
        return d < mergeRad;
      })) uniq.push(norm);
    }
    return uniq;
  };

  // --- Правило 1: сходящиеся обрывы ---
  //
  // Продолжение полотна ведёт В пустое пятно, поэтому вдоль направления обрыва
  // ставятся голоса. Перекрёсток — ячейка, набравшая голоса с разных сторон.
  const votes = new Map();
  for (const e of dangling) {
    const marked = new Set();
    for (let t = VOTE_MIN; t <= VOTE_MAX; t += VOTE_STEP) {
      const k = `${Math.floor((e.x + e.ux * t) / VOTE_CELL)}|${Math.floor((e.z + e.uz * t) / VOTE_CELL)}`;
      // Один обрыв голосует за ячейку один раз: иначе длинная прямая залила бы
      // голосами весь путь и «перекрёстком» стала бы любая дорога.
      if (marked.has(k)) continue;
      marked.add(k);
      if (!votes.has(k)) votes.set(k, []);
      votes.get(k).push(e);
    }
  }

  const converging = [];
  for (const [k, voters] of votes) {
    const unique = [...new Set(voters)];
    if (unique.length < VOTE_MIN_DIRS) continue;
    if (mergeAngles(unique.map(v => v.angle)).length < VOTE_MIN_DIRS) continue;

    const [cx, cz] = k.split("|").map(Number);
    converging.push({
      x: (cx + 0.5) * VOTE_CELL,
      z: (cz + 0.5) * VOTE_CELL,
      branches: mergeAngles(unique.map(v => v.angle)).length,
      rule: "converge"
    });
  }
  console.log(`Сходящихся обрывов: ${converging.length} ячеек`);

  // --- Правило 2: примыкание по лучу ---
  //
  // Пересечение ЛУЧА продолжения с чужим полотном. Именно луч, а не ближайшая
  // точка: боковая полоса может лежать рядом, но не на пути продолжения.
  const teeHit = (e) => {
    const approach = Math.atan2(e.uz, e.ux);
    let best = null;
    for (const s of segsNear(e.x, e.z, TEE_MAX + 10)) {
      const v1x = e.x - s.x1, v1z = e.z - s.z1;
      const denom = e.ux * s.dirZ - e.uz * s.dirX;
      if (Math.abs(denom) < 1e-12) continue;   // параллельно

      const t = (v1x * s.dirZ - v1z * s.dirX) / -denom;
      const u = (v1x * e.uz - v1z * e.ux) / denom;
      if (t < TEE_MIN || t > TEE_MAX || u < 0 || u > 1) continue;

      // Своё продолжение идёт ВДОЛЬ подхода, примыкание — ПОПЕРЁК. Без угла
      // параллельная полоса двойной дороги (13 м) считалась бы перекрёстком.
      const road = Math.atan2(s.dirZ, s.dirX);
      const diff = Math.abs(((approach - road) % Math.PI + Math.PI) % Math.PI);
      const sep = Math.min(diff, Math.PI - diff) * 180 / Math.PI;
      if (sep < TEE_ANGLE_DEG) continue;

      if (!best || t < best.t) best = { t };
    }
    return best;
  };

  const tees = [];
  for (const e of dangling) {
    const hit = teeHit(e);
    if (!hit) continue;
    tees.push({
      // Узел ставится НА ЧУЖОМ ПОЛОТНЕ, в точке попадания луча: физически
      // перекрёсток там, а обрыв остаётся стороной подхода.
      x: e.x + e.ux * hit.t,
      z: e.z + e.uz * hit.t,
      branches: 3,
      rule: "tee"
    });
  }
  console.log(`Примыканий по лучу: ${tees.length}`);

  // --- Склейка правил + дедупликация ---
  const all = [];
  const push = (p) => {
    if (all.some(a => Math.hypot(a.x - p.x, a.z - p.z) < DEDUP)) return;
    all.push(p);
  };
  // Сначала сходящиеся (у них больше веток), затем примыкания.
  for (const p of converging.sort((a, b) => b.branches - a.branches)) push(p);
  for (const p of tees) push(p);

  return all;
}

/**
 * Метод «пересечение линий» — нодировка.
 *
 * Работает только там, где полотна пересекаются координатами. Разрывов не видит,
 * поэтому оставлен дополнением к методу «разрывы»: он подтверждает настоящие
 * перекрёстки и добавляет те, что метод разрывов мог пропустить.
 */
function detectCrossings() {
  const geo = JSON.parse(fs.readFileSync(source, "utf8"));
  if (geo?.type !== "FeatureCollection" || !Array.isArray(geo.features)) {
    console.error("Ожидался GeoJSON FeatureCollection. Получено: " + geo?.type);
    process.exit(2);
  }

  // Отрезки с признаком полилинии: `fi` нужен, чтобы отличать «дорога сама с
  // собой» от схождения РАЗНЫХ дорог.
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
    raw.push({ x: np.sumX / np.count, z: np.sumZ / np.count, branches: uniq.length, rule: "crossing" });
  }

  // --- Шаг 6. Дедупликация + отсечение Европы ---
  const dedup = [];
  for (const j of raw.sort((a, b) => b.branches - a.branches)) {
    if (j.x < EUROPE_MAX_X) continue;
    if (dedup.some(d => Math.hypot(d.x - j.x, d.z - j.z) < DEDUP)) continue;
    dedup.push(j);
  }

  console.log(`Узлов-кандидатов: ${raw.length}; после дедупликации в рабочей области: ${dedup.length}`);
  return dedup;
}

// --- Сборка итогового списка ---
const round = v => Math.round(v * 100) / 100;

let junctions;
const methodLabels = [];

if (method === "gaps" || method === "both") {
  const roadsFile = roadsPath ?? source;
  const roads = JSON.parse(fs.readFileSync(roadsFile, "utf8"));
  if (!Array.isArray(roads?.segments)) {
    console.error("Ожидался roads.json с массивом segments. Файл: " + roadsFile);
    process.exit(2);
  }

  console.log(`\n--- Метод «разрывы полотна» (${path.basename(roadsFile)}) ---`);
  junctions = detectGaps(roads.segments);
  methodLabels.push(`gaps:converge${VOTE_MIN}-${VOTE_MAX}m>=${VOTE_MIN_DIRS}dirs` +
    `+tee${TEE_MIN}-${TEE_MAX}m>${TEE_ANGLE_DEG}deg`);
}

if (method === "crossing" || method === "both") {
  console.log(`\n--- Метод «пересечение линий» (${path.basename(source)}) ---`);
  const crossing = detectCrossings();
  methodLabels.push(`noding+snap${SNAP}m+slack${SLACK}m+dir${DIR_MERGE_DEG}deg`);

  if (!junctions) {
    junctions = crossing;
  } else {
    // Объединение: настоящее пересечение не должно потеряться из-за того, что
    // метод разрывов его не увидел.
    let carried = 0;
    for (const p of crossing) {
      if (junctions.some(j => Math.hypot(j.x - p.x, j.z - p.z) < DEDUP)) continue;
      junctions.push(p);
      carried++;
    }
    console.log(`Добавлено из пересечений: ${carried}`);
  }
}

// --- Запись результата ---
// Порядок сортируется по X, затем по Z: автор проверяет список подряд, и обход
// «следующий/предыдущий» не должен прыгать через всю карту.
const ordered = [...junctions].sort((a, b) => a.x - b.x || a.z - b.z);

const byBranches = new Map();
for (const j of ordered) byBranches.set(j.branches, (byBranches.get(j.branches) || 0) + 1);

const byRule = new Map();
for (const j of ordered) byRule.set(j.rule, (byRule.get(j.rule) || 0) + 1);

// Плоский массив x,z — тот же приём, что у дорог: JSON из тысяч объектов
// читается заметно дольше, а для поиска ближайшего нужны только координаты.
const points = [];
for (const j of ordered) points.push(round(j.x), round(j.z));

const payload = {
  schemaVersion: 2,
  source: "zvukoper/ets2_assist:data/GeoJson/roads.geojson",
  method: methodLabels.join(" + "),
  units: "meters",
  layout: "x,z",
  region: `x>=${EUROPE_MAX_X} (SibirMap: Казань..Барабинск; Европа исключена)`,
  junctionCount: ordered.length,
  // Распределение по числу веток пишется В ДАННЫЕ, а не только в консоль: по нему
  // проверка видит, что шаги построения действительно сработали.
  branches: [...byBranches.entries()].sort((a, b) => a[0] - b[0]),
  rules: Object.fromEntries([...byRule.entries()].sort()),
  points
};

fs.mkdirSync(path.dirname(target), { recursive: true });
fs.writeFileSync(target, JSON.stringify(payload), "utf8");

const sizeKb = (fs.statSync(target).size / 1024).toFixed(1);
console.log("ветки: " + payload.branches.map(([b, c]) => `${b}:${c}`).join(" "));
console.log("правила: " + Object.entries(payload.rules).map(([r, c]) => `${r}:${c}`).join(" "));
console.log(`Записано: ${target} — ${sizeKb} КБ, ${ordered.length} перекрёстков.`);
