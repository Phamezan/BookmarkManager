# NovelSidecar

Internal Node 22 HTTP sidecar that runs vendored [LNReader](https://github.com/LNReader/lnreader-plugins) novel source plugins for BookmarkManager. Not exposed to the host; reached only by `BookmarkManager.Api` over the Docker Compose network.

## Vendored upstream plugins

- Repository: `https://github.com/LNReader/lnreader-plugins` (MIT, see [`plugins/LICENSE`](./plugins/LICENSE))
- Branch: `master`
- Pinned upstream commit SHA: `7bdf6a50906bcb254584a6c53e8321edaa978564`

Bundled plugins:

| Source ID | Plugin file | Upstream site | Locked / premium chapter behavior |
|-----------|-------------|---------------|-----------------------------------|
| `hangulplanet` | `plugins/english/HangulPlanet.ts` | `https://hangulplanet.com` | Unauthenticated pages omit individual premium chapter links (only showing a `#premium-chapters` count accordion); all returned chapters are free. |
| `wetriedtls` | `plugins/english/wetriedtls.ts` | `https://wetriedtls.com` | Fetches `/chapters/{id}/paid` and prefixes locked chapter names with `🔒 `; sidecar strips the prefix and sets `locked: true`. |
| `fenrirrealm` | `plugins/english/fenrirrealm.ts` | `https://fenrirealm.com` | Checks `c.locked?.price` from the chapters API and prefixes locked chapter names with `🔒 `; sidecar strips the prefix and sets `locked: true`. |
| `novelfire` | `plugins/multisrc/novelfire/template.ts` (`sources.json` `novelfire` entry only) | `https://novelfire.net` | Free aggregator only; no locked chapters. |

## How to update plugins

Re-copy the files from upstream `LNReader/lnreader-plugins` (`master`) into `NovelSidecar/plugins/`:

1. `plugins/english/HangulPlanet.ts`, `plugins/english/wetriedtls.ts`, `plugins/english/fenrirrealm.ts` → `NovelSidecar/plugins/english/`
2. `plugins/multisrc/novelfire/template.ts`, `plugins/multisrc/novelfire/sources.json` → `NovelSidecar/plugins/multisrc/novelfire/`
3. `src/types/plugin.ts`, `src/types/filters.ts`, `src/types/constants.ts` → `NovelSidecar/plugins/types/`
4. `src/libs/filterInputs.ts`, `src/libs/novelStatus.ts` → `NovelSidecar/plugins/libs/`
5. `LICENSE` → `NovelSidecar/plugins/LICENSE`
6. Update the pinned commit SHA above, run `npm run build && npm test`.

## Commands

```bash
npm ci
npm run build
npm test
npm start
```
