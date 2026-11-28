import path from 'node:path';
import { fileURLToPath } from 'node:url';
import esbuild from 'esbuild';

const root = path.dirname(fileURLToPath(import.meta.url));

const aliasPlugin = {
  name: 'lnreader-aliases',
  setup(build) {
    const map = {
      '@libs/fetch': path.join(root, 'src/shims/fetch.ts'),
      '@libs/storage': path.join(root, 'src/shims/storage.ts'),
      '@libs/filterInputs': path.join(root, 'plugins/libs/filterInputs.ts'),
      '@libs/novelStatus': path.join(root, 'plugins/libs/novelStatus.ts'),
      '@libs/aes': path.join(root, 'plugins/libs/aes.ts'),
      '@/types/plugin': path.join(root, 'plugins/types/plugin.ts'),
    };
    build.onResolve({ filter: /^(@libs\/|@\/types\/)/ }, args => {
      const target = map[args.path];
      if (target) return { path: target };
      return null;
    });
  },
};

export async function buildBundle(entry = 'src/server.ts', outfile = 'dist/server.js') {
  await esbuild.build({
    entryPoints: [path.join(root, entry)],
    outfile: path.join(root, outfile),
    bundle: true,
    platform: 'node',
    target: 'node22',
    format: 'esm',
    packages: 'external',
    sourcemap: false,
    plugins: [aliasPlugin],
  });
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  await buildBundle('src/server.ts', 'dist/server.js');
  await buildBundle('src/app.ts', 'dist/app.js');
  console.log('Built dist/server.js and dist/app.js');
}
