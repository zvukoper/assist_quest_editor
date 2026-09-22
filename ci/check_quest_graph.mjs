// Проверка целостности Quest Graph по правилам QuestGraphValidator.
// Запуск: node ci/check_quest_graph.mjs data/quests/tutorial_ruslan_shashlik.aqquest
import fs from "node:fs";

const path = process.argv[2] || "data/quests/tutorial_ruslan_shashlik.aqquest";
const document = JSON.parse(fs.readFileSync(path, "utf8"));
const graph = document.definition.graph;

const problems = [];
const nodes = new Map();
for (const node of graph.nodes) {
  if (nodes.has(node.nodeId)) problems.push(`duplicate nodeId ${node.nodeId}`);
  nodes.set(node.nodeId, node);
}

const sockets = new Map();
for (const node of graph.nodes) {
  const map = new Map();
  for (const socket of node.sockets) {
    if (map.has(socket.socketId)) problems.push(`duplicate socket ${socket.socketId} in ${node.nodeId}`);
    map.set(socket.socketId, socket);
  }
  sockets.set(node.nodeId, map);
}

const inputs = new Map();
const exact = new Set();
const adjacency = new Map([...nodes.keys()].map(id => [id, []]));
for (const connection of graph.connections) {
  const from = nodes.get(connection.fromNodeId);
  const to = nodes.get(connection.toNodeId);
  if (!from) { problems.push(`unknown from ${connection.fromNodeId}`); continue; }
  if (!to) { problems.push(`unknown to ${connection.toNodeId}`); continue; }

  const fromSocket = sockets.get(from.nodeId).get(connection.fromSocketId);
  const toSocket = sockets.get(to.nodeId).get(connection.toSocketId);
  if (!fromSocket) { problems.push(`missing fromSocket ${connection.fromSocketId} on ${from.nodeId}`); continue; }
  if (!toSocket) { problems.push(`missing toSocket ${connection.toSocketId} on ${to.nodeId}`); continue; }
  if (fromSocket.direction !== "Output" || toSocket.direction !== "Input") {
    problems.push(`wrong direction ${connection.fromSocketId} -> ${connection.toSocketId}`);
  }

  // QuestGraphValidator запрещает только точные дубликаты связи. Несколько
  // веток в один Input допустимы (иначе не свести несколько отказов в общий
  // узел), поэтому здесь проверяется лишь полный дубль.
  const exactKey = [connection.fromNodeId, connection.fromSocketId, connection.toNodeId, connection.toSocketId].join("\u001f");
  if (exact.has(exactKey)) problems.push(`duplicate connection ${exactKey}`);
  exact.add(exactKey);

  const key = `${to.nodeId}\u001f${toSocket.socketId}`;
  inputs.set(key, (inputs.get(key) || 0) + 1);
  adjacency.get(from.nodeId).push(to.nodeId);
}

const starts = graph.nodes.filter(n => n.nodeType === "Start").map(n => n.nodeId);
const ends = graph.nodes.filter(n => n.nodeType === "End").map(n => n.nodeId);
if (starts.length !== 1) problems.push(`expected 1 Start, found ${starts.length}`);
if (ends.length === 0) problems.push("no End node");

const reachable = new Set();
const queue = [...starts];
while (queue.length) {
  const id = queue.shift();
  if (reachable.has(id)) continue;
  reachable.add(id);
  for (const next of adjacency.get(id)) queue.push(next);
}
for (const node of graph.nodes) {
  if (!reachable.has(node.nodeId)) problems.push(`unreachable node ${node.nodeId}`);
}

// Каждая нода с входом должна иметь входящую связь (кроме Start), иначе
// Runtime молча зависнет на ней.
for (const node of graph.nodes) {
  const hasInput = node.sockets.some(s => s.direction === "Input");
  if (!hasInput) continue;
  const connected = node.sockets.some(s =>
    s.direction === "Input" && inputs.has(`${node.nodeId}\u001f${s.socketId}`));
  if (!connected) problems.push(`node ${node.nodeId} has an unconnected Input`);
}

// Один Output socket должен вести ровно в одну ноду: иначе ветвление
// раздваивается произвольно и результат зависит от порядка связей в файле.
for (const node of graph.nodes) {
  const outputs = new Map();
  for (const connection of graph.connections) {
    if (connection.fromNodeId !== node.nodeId) continue;
    outputs.set(connection.fromSocketId, (outputs.get(connection.fromSocketId) || 0) + 1);
  }
  for (const [socketId, count] of outputs) {
    if (count > 1) problems.push(`output ${node.nodeId}.${socketId} used by ${count} connections`);
  }
}

// Каждая ссылка на sceneId должна существовать в data/scenes.
for (const node of graph.nodes) {
  if (node.nodeType !== "DialogueScene") continue;
  const sceneId = node.parameters?.sceneId;
  const scenePath = `data/scenes/${sceneId}.aqscene`;
  if (!fs.existsSync(scenePath)) problems.push(`scene ${sceneId} missing (${scenePath})`);
}

// sceneIds документа должны совпадать с используемыми в графе.
const declared = new Set(document.definition.sceneIds || []);
const used = new Set(graph.nodes.filter(n => n.nodeType === "DialogueScene").map(n => n.parameters.sceneId));
for (const id of used) if (!declared.has(id)) problems.push(`sceneId ${id} used but not declared`);
for (const id of declared) if (!used.has(id)) problems.push(`sceneId ${id} declared but not used`);

console.log(`nodes=${graph.nodes.length} connections=${graph.connections.length}`);
if (problems.length) {
  console.log("PROBLEMS:");
  for (const problem of problems) console.log(" -", problem);
  process.exit(1);
}
console.log("OK");
