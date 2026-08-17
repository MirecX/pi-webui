#!/usr/bin/env node
// Scripted fake `pi --mode rpc` for testing PiRpcClient framing/parsing.
//
// Reads a scenario file (path in FAKE_PI_SCENARIO) of JSON lines, each:
//   {"when":"start","emit":[{event},...]}      -> emitted on startup
//   {"when":"<commandType>","emit":[{event},...]}" -> emitted when that command type arrives
// On every incoming command the fake replies with a correlated success response.
//
// This intentionally emits real JSONL (LF-delimited, optional \r) the way pi does,
// so PiRpcClient's framing, CR-stripping, and id-correlation are exercised for real.

import fs from "node:fs";
import readline from "node:readline";

const scenarioPath = process.env.FAKE_PI_SCENARIO;
const startEvents = [];
const byType = new Map();

if (scenarioPath) {
  for (const raw of fs.readFileSync(scenarioPath, "utf8").split("\n")) {
    const line = raw.trim();
    if (!line) continue;
    const entry = JSON.parse(line);
    if (entry.when === "start") startEvents.push(...(entry.emit ?? []));
    else if (entry.when) byType.set(entry.when, entry.emit ?? []);
  }
}

const out = (o) => process.stdout.write(JSON.stringify(o) + "\n");

for (const ev of startEvents) out(ev);

const rl = readline.createInterface({ input: process.stdin, crlfDelay: Infinity });
rl.on("line", (line) => {
  let cmd;
  try { cmd = JSON.parse(line); } catch { return; }
  if (cmd.type === "response") return;
  for (const ev of byType.get(cmd.type) ?? []) out(ev);
  const resp = { type: "response", command: cmd.type, success: true };
  if (cmd.id) resp.id = cmd.id;
  out(resp);
});
rl.on("close", () => process.exit(0));
