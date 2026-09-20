import { writeFileSync, mkdirSync } from "node:fs";
import { dirname } from "node:path";

const owner = "zvukoper";
const sourceRepo = "ets2_assist";
const ref = "main";
const apiRoot = "https://api.github.com/repos/" + owner + "/" + sourceRepo;
const outPath = "data/world/sdo_points.json";

async function apiJson(url) {
  const response = await fetch(url, {
    headers: { Accept: "application/vnd.github+json", "User-Agent": "assist-quest-editor-sdo-import" }
  });
  if (!response.ok) throw new Error("GitHub API " + response.status + " for " + url);
  return await response.json();
}

async function rawText(url) {
  const response = await fetch(url, {
    headers: { Accept: "text/plain", "User-Agent": "assist-quest-editor-sdo-import" }
  });
  if (!response.ok) throw new Error("GitHub raw " + response.status + " for " + url);
  return await response.text();
}

const listing = await apiJson(apiRoot + "/contents/data/editor_static_data?ref=" + ref);
const metaEntry = listing.find(file => file.type === "file" && file.name.toLowerCase() === "meta.json");
if (!metaEntry?.download_url) throw new Error("meta.json не найден в ETS2 Assist.");

const meta = JSON.parse(await rawText(metaEntry.download_url));
const commit = await apiJson(apiRoot + "/commits/" + ref);

const categories = Object.fromEntries(
  Object.entries(meta.categories || {}).map(([key, value]) => [
    key,
    {
      name: String(value?.name || key),
      color: String(value?.color || "#78c8f0")
    }
  ])
);

const sourceFiles = listing.filter(file =>
  file.type === "file" &&
  file.name.toLowerCase().endsWith(".json") &&
  file.name.toLowerCase() !== "meta.json"
);

const points = [];

for (const file of sourceFiles) {
  if (!file.download_url) continue;
  const document = JSON.parse(await rawText(file.download_url));
  const category = String(
    document.category ||
    file.name.replace(/\.json$/i, "").replace(/^model_/i, "").replace(/^overlay_/i, "")
  );
  const categoryMeta = categories[category] || { name: category, color: "#78c8f0" };

  for (const object of Array.isArray(document.objects) ? document.objects : []) {
    const uid = String(object.uid || "");
    const x = Number(object.x);
    const y = Number(object.y ?? 0);
    const z = Number(object.z);

    if (!uid || !Number.isFinite(x) || !Number.isFinite(y) || !Number.isFinite(z)) continue;

    points.push({
      id: "sdo:" + category + ":" + uid,
      category,
      name: categoryMeta.name,
      color: categoryMeta.color,
      x,
      y,
      z
    });
  }
}

points.sort((a, b) =>
  a.category.localeCompare(b.category, "en") ||
  a.id.localeCompare(b.id, "en")
);

const bundle = {
  schemaVersion: 1,
  source: "zvukoper/ets2_assist:data/editor_static_data",
  sourceRef: ref,
  sourceCommit: commit.sha,
  categoryCount: Object.keys(categories).length,
  pointCount: points.length,
  categories,
  points
};

mkdirSync(dirname(outPath), { recursive: true });
writeFileSync(outPath, JSON.stringify(bundle) + "\n", "utf8");
console.log("SDO import: " + points.length + " точек, " + Object.keys(categories).length + " категорий, " + sourceFiles.length + " JSON.");
