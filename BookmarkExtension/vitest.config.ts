import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    globals: true,
    environment: "node",
    include: ["tests/**/*.test.ts"],
    passWithNoTests: true,
    coverage: {
      provider: "v8",
      include: ["src/**/*.ts"],
      exclude: ["src/popup/popup.ts", "src/background/service-worker.ts"],
    },
  },
  resolve: {
    alias: {
      "@shared": new URL("./src/shared", import.meta.url).pathname,
      "@api": new URL("./src/api", import.meta.url).pathname,
      "@bookmarks": new URL("./src/bookmarks", import.meta.url).pathname,
      "@storage": new URL("./src/storage", import.meta.url).pathname,
      "@commands": new URL("./src/commands", import.meta.url).pathname,
      "@background": new URL("./src/background", import.meta.url).pathname,
    },
  },
});
