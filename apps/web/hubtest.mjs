import { HubConnectionBuilder } from "@microsoft/signalr";
import { readFileSync } from "node:fs";
const token = readFileSync("C:/Users/kenha/AppData/Local/Temp/claude/C--Users-kenha-repos-Diariz/a5e9c4cf-2a8e-4c3e-abf8-9e4623165c99/scratchpad/dz.tok", "utf8").trim();
const conn = new HubConnectionBuilder()
  .withUrl("http://localhost:8080/hubs/transcription", { accessTokenFactory: () => token })
  .build();
conn.on("RecordingStatusChanged", (e) => console.log("STATUS", JSON.stringify(e)));
conn.on("LiveTranscriptAppended", (e) => console.log("APPENDED", JSON.stringify(e)));
conn.on("LiveTranscriptDegraded", (e) => console.log("DEGRADED", JSON.stringify(e)));
await conn.start();
console.log("connected; listening 150s");
setTimeout(() => { conn.stop(); process.exit(0); }, 150000);
