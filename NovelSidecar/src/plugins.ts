import type { Plugin } from '@/types/plugin';
import hangulPlanet from '../plugins/english/HangulPlanet';
import wetriedtls from '../plugins/english/wetriedtls';
import fenrirRealm from '../plugins/english/fenrirrealm';
import {
  NovelFirePlugin,
  type NovelFireMetadata,
} from '../plugins/multisrc/novelfire/template';
import novelFireSources from '../plugins/multisrc/novelfire/sources.json';

export interface SidecarSourceMeta {
  id: string;
  name: string;
  site: string;
}

export interface SidecarNovelSummary {
  title: string;
  path: string;
  cover: string | null;
}

export interface SidecarChapter {
  name: string;
  path: string;
  number: number | null;
  releaseTime: string | null;
  locked: boolean;
}

export interface SidecarNovelDetail {
  title: string;
  path: string;
  cover: string | null;
  author: string | null;
  status: string;
  genres: string[];
  summary: string;
  chapters: SidecarChapter[];
}

export interface SourceAdapter {
  readonly id: string;
  readonly name: string;
  readonly site: string;
  popular(page: number): Promise<SidecarNovelSummary[]>;
  search(query: string, page: number): Promise<SidecarNovelSummary[]>;
  novel(path: string): Promise<SidecarNovelDetail>;
  chapter(path: string): Promise<string>;
}

const LOCK_PREFIX_RE = /^\s*🔒\s*/u;

function stripTrailingSlash(url: string): string {
  return url.replace(/\/+$/, '');
}

function normalizeGenres(genres?: string): string[] {
  if (!genres) return [];
  return genres
    .split(',')
    .map(g => g.trim())
    .filter(Boolean);
}

function defaultFilterValues(plugin: Plugin.PluginBase): Record<string, { type: string; value: unknown }> {
  const result: Record<string, { type: string; value: unknown }> = {};
  if (!plugin.filters) return result;
  for (const [key, filter] of Object.entries(plugin.filters)) {
    result[key] = { type: filter.type, value: filter.value };
  }
  return result;
}

function createAdapter(config: {
  id: string;
  name: string;
  site: string;
  plugin: Plugin.PluginBase;
  toExternalPath: (pluginPath: string, isLocked?: boolean) => string;
  toPluginPath: (externalPath: string) => string;
}): SourceAdapter {
  const { id, name, plugin, toExternalPath, toPluginPath } = config;
  const site = stripTrailingSlash(config.site);

  const mapNovels = (items: Plugin.NovelItem[]): SidecarNovelSummary[] =>
    (items || [])
      .filter(item => item && item.name && item.path)
      .map(item => ({
        title: item.name.trim(),
        path: toExternalPath(item.path),
        cover: item.cover?.trim() || null,
      }));

  return {
    id,
    name,
    site,

    async popular(page: number): Promise<SidecarNovelSummary[]> {
      const filters = defaultFilterValues(plugin);
      const items = await plugin.popularNovels(page, {
        showLatestNovels: false,
        filters: filters as never,
      });
      return mapNovels(items);
    },

    async search(query: string, page: number): Promise<SidecarNovelSummary[]> {
      const items = await plugin.searchNovels(query, page);
      return mapNovels(items);
    },

    async novel(externalPath: string): Promise<SidecarNovelDetail> {
      const pluginPath = toPluginPath(externalPath);
      const data = await plugin.parseNovel(pluginPath);
      const chapters: SidecarChapter[] = (data.chapters || [])
        .filter(c => c && c.path)
        .map(c => {
          const rawName = (c.name || '').trim();
          const locked = LOCK_PREFIX_RE.test(rawName);
          const cleanName = locked ? rawName.replace(LOCK_PREFIX_RE, '').trim() : rawName;
          const num =
            typeof c.chapterNumber === 'number' && Number.isFinite(c.chapterNumber)
              ? c.chapterNumber
              : null;
          return {
            name: cleanName || (num != null ? `Chapter ${num}` : 'Untitled'),
            path: toExternalPath(c.path, locked),
            number: num,
            releaseTime: c.releaseTime ? String(c.releaseTime) : null,
            locked,
          };
        });

      return {
        title: (data.name || '').trim(),
        path: toExternalPath(data.path || pluginPath),
        cover: data.cover?.trim() || null,
        author: data.author?.trim() || null,
        status: (data.status || 'Unknown').trim() || 'Unknown',
        genres: normalizeGenres(data.genres),
        summary: (data.summary || '').trim(),
        chapters,
      };
    },

    async chapter(externalPath: string): Promise<string> {
      const pluginPath = toPluginPath(externalPath);
      const html = await plugin.parseChapter(pluginPath);
      return typeof html === 'string' ? html : '';
    },
  };
}

const novelfireMeta = (novelFireSources as NovelFireMetadata[]).find(
  s => s.id === 'novelfire',
);
if (!novelfireMeta) {
  throw new Error('novelfire source metadata missing from sources.json');
}
const novelFirePlugin = new NovelFirePlugin(novelfireMeta);

function ensureLeadingSlash(p: string): string {
  const trimmed = p.trim();
  return trimmed.startsWith('/') ? trimmed : `/${trimmed}`;
}

function stripLeadingSlash(p: string): string {
  return p.replace(/^\/+/, '');
}

const adapters: SourceAdapter[] = [
  createAdapter({
    id: 'hangulplanet',
    name: hangulPlanet.name,
    site: hangulPlanet.site,
    plugin: hangulPlanet,
    toExternalPath: p => ensureLeadingSlash(p),
    toPluginPath: p => ensureLeadingSlash(p),
  }),
  createAdapter({
    id: 'wetriedtls',
    name: wetriedtls.name,
    site: wetriedtls.site,
    plugin: wetriedtls,
    toExternalPath: p => {
      const clean = stripLeadingSlash(p);
      return clean.startsWith('series/') ? `/${clean}` : `/series/${clean}`;
    },
    toPluginPath: p => stripLeadingSlash(p).replace(/^series\//, ''),
  }),
  createAdapter({
    id: 'fenrirrealm',
    name: fenrirRealm.name,
    site: fenrirRealm.site,
    plugin: fenrirRealm,
    toExternalPath: (p, isLocked) => {
      const base = isLocked ? p.split('~~')[0] : p;
      const clean = stripLeadingSlash(base);
      return clean.startsWith('series/') ? `/${clean}` : `/series/${clean}`;
    },
    toPluginPath: p => stripLeadingSlash(p).replace(/^series\//, ''),
  }),
  createAdapter({
    id: 'novelfire',
    name: novelFirePlugin.name,
    site: novelFirePlugin.site,
    plugin: novelFirePlugin,
    toExternalPath: p => ensureLeadingSlash(p),
    toPluginPath: p => stripLeadingSlash(p),
  }),
];

const sourceMap = new Map<string, SourceAdapter>(adapters.map(a => [a.id, a]));

export function listSources(): SidecarSourceMeta[] {
  return adapters.map(({ id, name, site }) => ({ id, name, site }));
}

export function getSource(id: string | null): SourceAdapter | undefined {
  if (!id) return undefined;
  return sourceMap.get(id.trim().toLowerCase());
}
