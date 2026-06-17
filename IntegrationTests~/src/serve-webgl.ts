import fs from "node:fs";
import http from "node:http";
import path from "node:path";

// Unity WebGL builds store framework, code, and data as pre-compressed .gz files.
// Servers must respond with Content-Encoding: gzip so the browser decompresses them.
// Content-Type must reflect the underlying payload, not the archive wrapper.
const GZ_CONTENT_TYPES: Record<string, string> = {
  ".data.gz": "application/octet-stream",
  ".framework.js.gz": "application/javascript",
  ".wasm.gz": "application/wasm",
  ".symbols.json.gz": "application/json",
};

const CONTENT_TYPES: Record<string, string> = {
  ".html": "text/html",
  ".js": "application/javascript",
  ".json": "application/json",
  ".css": "text/css",
  ".wasm": "application/wasm",
  ".ico": "image/x-icon",
  ".png": "image/png",
  ".svg": "image/svg+xml",
  ".data": "application/octet-stream",
};

function resolveContentType(
  filePath: string,
): { contentType: string; isPrecompressedGzip: boolean } {
  // Check longest-match suffixes first (e.g. .framework.js.gz before .gz)
  for (const [suffix, contentType] of Object.entries(GZ_CONTENT_TYPES)) {
    if (filePath.endsWith(suffix)) {
      return { contentType, isPrecompressedGzip: true };
    }
  }
  // Fallback: strip .gz and derive from the remaining extension
  const base = filePath.endsWith(".gz") ? filePath.slice(0, -3) : filePath;
  const ext = path.extname(base);
  return {
    contentType: CONTENT_TYPES[ext] ?? "application/octet-stream",
    isPrecompressedGzip: filePath.endsWith(".gz"),
  };
}

export interface WebGLServer {
  /** Base URL, e.g. http://localhost:18765 */
  url: string;
  close: () => void;
}

/**
 * Starts a minimal HTTP server that correctly serves a Unity WebGL build directory,
 * including pre-compressed .gz artifacts with the required Content-Encoding header.
 *
 * @param rootDir Absolute path to the WebGL build directory (the folder containing index.html).
 * @param port    Port to listen on. Pass 0 to let the OS assign a free port.
 */
export function startWebGLServer(
  rootDir: string,
  port = 0,
): Promise<WebGLServer> {
  return new Promise((resolve, reject) => {
    const server = http.createServer((req, res) => {
      const reqPath = req.url === "/" ? "/index.html" : (req.url ?? "/index.html");
      // Strip query string
      const filePath = path.join(rootDir, reqPath.split("?")[0]);

      if (!fs.existsSync(filePath) || !fs.statSync(filePath).isFile()) {
        res.writeHead(404, { "Content-Type": "text/plain" });
        res.end("Not found");
        return;
      }

      const { contentType, isPrecompressedGzip } = resolveContentType(filePath);
      const headers: Record<string, string> = {
        "Content-Type": contentType,
        "Access-Control-Allow-Origin": "*",
      };
      if (isPrecompressedGzip) {
        headers["Content-Encoding"] = "gzip";
      }

      res.writeHead(200, headers);
      fs.createReadStream(filePath).pipe(res);
    });

    server.on("error", reject);
    server.listen(port, "127.0.0.1", () => {
      const addr = server.address();
      const actualPort =
        typeof addr === "object" && addr !== null ? addr.port : port;
      resolve({
        url: `http://127.0.0.1:${actualPort}`,
        close: () => server.close(),
      });
    });
  });
}
