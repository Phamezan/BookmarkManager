import * as esbuild from "esbuild";
import { cpSync, mkdirSync, existsSync, rmSync } from "node:fs";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = dirname(fileURLToPath(import.meta.url));
const isWatch = process.argv.includes("--watch");
const distDir = join(__dirname, "dist");

function cleanDist() {
  if (existsSync(distDir)) {
    rmSync(distDir, { recursive: true, force: true });
  }
  mkdirSync(distDir, { recursive: true });
}

function copyStaticFiles() {
  cpSync(join(__dirname, "manifest.json"), join(distDir, "manifest.json"));
  cpSync(join(__dirname, "popup"), join(distDir, "popup"), { recursive: true });
  cpSync(join(__dirname, "palette-host.html"), join(distDir, "palette-host.html"));
  cpSync(join(__dirname, "toast.html"), join(distDir, "toast.html"));

  const iconsSrc = join(__dirname, "assets", "icons");
  const iconsDest = join(distDir, "assets", "icons");
  if (existsSync(iconsSrc)) {
    cpSync(iconsSrc, iconsDest, { recursive: true });
  } else {
    mkdirSync(iconsDest, { recursive: true });
  }
}

const entrypoints = {
  "service-worker": "src/background/service-worker.ts",
  "popup/popup": "src/popup/popup.ts",
  "palette-host": "src/palette/palette-host.ts",
  "palette-injector": "src/palette/palette-injector.ts",
  "toast-page": "src/toast/toast-page.ts",
};

const commonOptions = {
  bundle: true,
  platform: "browser",
  target: "chrome120",
  format: "esm",
  sourcemap: false,
  legalComments: "none",
  logLevel: "info",
  define: {
    "process.env.NODE_ENV": '"production"',
  },
};

async function build() {
  cleanDist();
  copyStaticFiles();

  for (const [name, entry] of Object.entries(entrypoints)) {
    const entryPath = join(__dirname, entry);
    if (!existsSync(entryPath)) {
      throw new Error(`Missing entrypoint: ${entry}`);
    }
    await esbuild.build({
      ...commonOptions,
      entryPoints: [entryPath],
      outfile: join(distDir, `${name}.js`),
    });
  }

  const manifest = JSON.parse(
    await import("node:fs/promises").then((m) =>
      m.readFile(join(distDir, "manifest.json"), "utf-8"),
    ),
  );

  const referencedFiles = [
    manifest.background?.scripts?.[0],
    manifest.action?.default_popup,
    ...(manifest.icons ? Object.values(manifest.icons) : []),
    ...(manifest.web_accessible_resources?.flatMap((entry) => entry.resources) ?? []),
  ].filter(Boolean);

  for (const file of referencedFiles) {
    if (!existsSync(join(distDir, file))) {
      throw new Error(`Manifest references missing file: ${file}`);
    }
  }

  console.log("Build complete: dist/");
}

if (isWatch) {
  const ctx = await esbuild.context({
    ...commonOptions,
    entryPoints: Object.values(entrypoints).map((e) => join(__dirname, e)),
    outdir: distDir,
    outbase: join(__dirname, "src"),
  });
  await ctx.watch();
  console.log("Watching for changes...");
} else {
  await build();
}
