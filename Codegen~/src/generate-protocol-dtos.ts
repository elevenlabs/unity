import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { Parser, fromFile } from "@asyncapi/parser";

const here = dirname(fileURLToPath(import.meta.url));
const specPath = resolve(here, "../schemas/convai-asyncapi.yml");

const parser = new Parser();
const { document, diagnostics } = await fromFile(parser, specPath).parse();

for (const d of diagnostics) {
  const where = d.path?.join(".") ?? "";
  console.error(
    `[severity=${d.severity}] ${d.message}${where ? ` (${where})` : ""}`,
  );
}

if (!document) {
  console.error(`Failed to parse ${specPath}`);
  process.exit(1);
}

const info = document.info();
console.log(`Parsed ${info.title()} v${info.version()}`);
console.log(`  channels: ${document.channels().length}`);
console.log(`  messages: ${document.allMessages().length}`);
console.log("TODO: emit C# DTOs to ../Runtime/Core/Protocol/");
