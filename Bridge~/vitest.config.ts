import { defineConfig } from "vitest/config";
import { resolve } from "node:path";

export default defineConfig({
  resolve: {
    // @elevenlabs/client does not export its platform/web sub-paths in its
    // package.json "exports" map, so Vite's resolver rejects deep imports at
    // test time. These aliases bypass the restriction and point directly to the
    // compiled dist files — Rolldown has no such limitation in production.
    alias: {
      "@elevenlabs/client/dist/platform/web/input.js": resolve(
        "node_modules/@elevenlabs/client/dist/platform/web/input.js",
      ),
      "@elevenlabs/client/dist/platform/web/output.js": resolve(
        "node_modules/@elevenlabs/client/dist/platform/web/output.js",
      ),
    },
  },
  test: {
    include: ["src/**/*.test.ts"],
    unstubGlobals: true,
  },
});
