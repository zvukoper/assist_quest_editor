// Проверка всех canonical Quest Graph resources.
import fs from "node:fs";
import path from "node:path";

const args = process.argv.slice(2);
const files = args.length ? args : fs.readdirSync("data/quests").filter(name => name.endsWith(".aqquest")).sort().map(name => path.join("data/quests", name));
if (!files.length) throw new Error("В data/quests нет .aqquest.");

function validate(filePath) {
  const doc = JSON.parse(fs.readFileSync(filePath, "utf8"));
  const definition = doc.definition;
  const graph = definition.graph;
  const problems = [];
  const nodes = new Map(graph.nodes.map(node => [node.nodeId, node]));
  if (nodes.size !== graph.nodes.length) problems.push("duplicate nodeId");

  const socketMap = new Map();
  for (const node of graph.nodes) {
    const map = new Map(node.sockets.map(socket => [socket.socketId, socket]));
    if (map.size !== node.sockets.length) problems.push("duplicate socket in " + node.nodeId);
    socketMap.set(node.nodeId, map);
  }

  const incoming = new Map();
  const adjacency = new Map(graph.nodes.map(node => [node.nodeId, []]));
  const exact = new Set();

  for (const c of graph.connections) {
    const from = nodes.get(c.fromNodeId), to = nodes.get(c.toNodeId);
    if (!from || !to) { problems.push("unknown node in connection"); continue; }
    const fsocket = socketMap.get(from.nodeId)?.get(c.fromSocketId);
    const tsocket = socketMap.get(to.nodeId)?.get(c.toSocketId);
    if (!fsocket || !tsocket) { problems.push("unknown socket in connection"); continue; }
    if (fsocket.direction !== "Output" || tsocket.direction !== "Input") problems.push("connection direction");
    const key = [c.fromNodeId,c.fromSocketId,c.toNodeId,c.toSocketId].join("\u001f");
    if (exact.has(key)) problems.push("duplicate connection");
    exact.add(key);
    incoming.set(to.nodeId + "\u001f" + tsocket.socketId, (incoming.get(to.nodeId + "\u001f" + tsocket.socketId) || 0) + 1);
    adjacency.get(from.nodeId).push(to.nodeId);
  }

  const starts = graph.nodes.filter(n => n.nodeType === "Start");
  const ends = graph.nodes.filter(n => n.nodeType === "End");
  if (starts.length !== 1) problems.push("expected exactly one Start");
  if (!ends.length) problems.push("no End");

  const reachable = new Set(), queue = starts.map(n => n.nodeId);
  while (queue.length) {
    const id = queue.shift();
    if (reachable.has(id)) continue;
    reachable.add(id);
    for (const next of adjacency.get(id) || []) queue.push(next);
  }
  for (const node of graph.nodes) if (!reachable.has(node.nodeId)) problems.push("unreachable node " + node.nodeId);

  for (const node of graph.nodes) {
    const inputs = node.sockets.filter(s => s.direction === "Input");
    if (inputs.length && !inputs.some(s => incoming.has(node.nodeId + "\u001f" + s.socketId)))
      problems.push("unconnected Input on " + node.nodeId);
  }

  const declaredScenes = new Set(definition.sceneIds || []);
  const usedScenes = new Set();
  for (const node of graph.nodes.filter(n => n.nodeType === "DialogueScene")) {
    const sceneId = node.parameters?.sceneId;
    usedScenes.add(sceneId);
    if (!fs.existsSync(path.join("data","scenes",sceneId + ".aqscene"))) problems.push("missing scene " + sceneId);
  }
  for (const id of usedScenes) if (!declaredScenes.has(id)) problems.push("scene not declared " + id);
  for (const id of declaredScenes) if (!usedScenes.has(id)) problems.push("scene declared but unused " + id);

  if (definition.activation?.mode === "Proximity") {
    if (!definition.activation.worldPointId) problems.push("Proximity activation missing worldPointId");
    if (!(Number(definition.activation.radius) > 0)) problems.push("Proximity activation invalid radius");
  }

  if (problems.length) {
    console.log(path.basename(filePath) + ": FAIL");
    problems.forEach(p => console.log(" - " + p));
    return false;
  }
  console.log(path.basename(filePath) + ": OK nodes=" + graph.nodes.length + " connections=" + graph.connections.length);
  return true;
}

if (!files.map(validate).every(Boolean)) process.exit(1);
console.log("All Quest resources OK");
