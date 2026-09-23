// Импорт дорожной геометрии из репозитория ETS2 Assist.
//
// Источник — `data/GeoJson/roads.geojson` (FeatureCollection, ~98 000 LineString
// фич, 45 МБ). Для редактора такой файл непригоден: он и читается долго, и в
// каждом снимке карты весил бы ~19 МБ. Поэтому геометрия переводится в плоский
// массив чисел [x1,z1,x2,z2] с округлением до сантиметра:
//   - 45 МБ -> ~3.6 МБ (в 12 раз меньше);
//   - исчезает вся структура GeoJSON (uid, roadType, length), которая редактору
//     не нужна: дорога здесь — это линия на карте и признак «рядом с дорогой».
//
// Округление до 0.01 м намеренное: координаты ETS2 имеют порядок 10^5, и точность
// в сантиметр заведомо превышает всё, что видно на карте и что может повлиять на
// выбор точки (минимальные расстояния в критериях — метры и километры).
//
// Запуск:
//   node ci/import_roads.mjs <путь к roads.geojson> <путь к выходному файлу>
import fs from "node:fs";
import path from "node:path";

const [source, target] = process.argv.slice(2);

if (!source || !target) {
  console.error("Использование: node ci/import_roads.mjs <roads.geojson> <roads.json>");
  process.exit(2);
}

if (!fs.existsSync(source)) {
  console.error("Файл дорог не найден: " + source);
  process.exit(2);
}

const rawSize = fs.statSync(source).size;
const geo = JSON.parse(fs.readFileSync(source, "utf8"));

if (geo?.type !== "FeatureCollection" || !Array.isArray(geo.features)) {
  console.error("Ожидался GeoJSON FeatureCollection. Получено: " + geo?.type);
  process.exit(2);
}

/** Округление до сантиметра: меньше мусора в данных, точность выше всякой нужды. */
const round = value => Math.round(value * 100) / 100;

const segments = [];
let skipped = 0;

/** Разворачивает LineString/MultiLineString в отрезки. */
function addLine(coordinates) {
  if (!Array.isArray(coordinates) || coordinates.length < 2) return;

  for (let i = 1; i < coordinates.length; i++) {
    const ax = Number(coordinates[i - 1]?.[0]);
    const az = Number(coordinates[i - 1]?.[1]);
    const bx = Number(coordinates[i]?.[0]);
    const bz = Number(coordinates[i]?.[1]);

    if (![ax, az, bx, bz].every(Number.isFinite)) { skipped++; continue; }

    // Вырожденные отрезки (точка в точку) отбрасываются: они не рисуются и
    // искажают «расстояние до дороги» нулём длины.
    if (ax === bx && az === bz) { skipped++; continue; }

    segments.push(round(ax), round(az), round(bx), round(bz));
  }
}

for (const feature of geo.features) {
  const type = feature?.geometry?.type;
  const coordinates = feature?.geometry?.coordinates;
  if (!coordinates) { skipped++; continue; }

  if (type === "LineString") addLine(coordinates);
  else if (type === "MultiLineString")
    for (const line of coordinates) addLine(line);
  else skipped++;
}

// Плоский массив читается быстрее вложенного и не создаёт миллионы мелких
// объектов при разборе в браузере и в C#.
const payload = {
  schemaVersion: 1,
  source: "zvukoper/ets2_assist:data/GeoJson/roads.geojson",
  // Формат задан в самом файле: читатель не должен угадывать раскладку чисел.
  layout: "x1,z1,x2,z2",
  units: "meters",
  segmentCount: segments.length / 4,
  // Отрезки хранятся плоским массивом, а не вложенными парами.
  segments
};

fs.mkdirSync(path.dirname(target), { recursive: true });
fs.writeFileSync(target, JSON.stringify(payload) + "\n", "utf8");

const outSize = fs.statSync(target).size;
console.log("источник:        " + source);
console.log("фич GeoJSON:     " + geo.features.length);
console.log("отрезков:        " + payload.segmentCount);
console.log("пропущено:       " + skipped);
console.log("размер:          " + (rawSize / 1048576).toFixed(1) + " МБ -> " +
  (outSize / 1048576).toFixed(1) + " МБ");
console.log("записано:        " + target);
