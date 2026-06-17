import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    // Unity WebGL loads in ~5 s locally; the smoke test runs sub-second once
    // Start() fires. 15 s leaves ~10 s of headroom for the actual bridge
    // round-trip and surfaces hangs (e.g. a stuck DynCall) quickly during
    // development. CI runners may need to raise this.
    testTimeout: 15_000,
    hookTimeout: 30_000,
  },
});
