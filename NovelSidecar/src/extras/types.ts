import * as cheerio from 'cheerio';
import type { CheerioAPI } from 'cheerio';
import type { AnyNode, Element } from 'domhandler';
import { fetchApi } from '../shims/fetch';

export interface SidecarStats {
  rank: number | null;
  rating: number | null;
  ratingCount: number | null;
  views: number | null;
  bookmarks: number | null;
  chapters: number | null;
  status: string | null;
  latestChapterName: string | null;
  latestChapterUpdated: string | null;
  reviewCount: number | null;
  reviewAverage: number | null;
  /** Full synopsis as plain text, paragraphs separated by blank lines. */
  summary: string | null;
}

export interface SidecarReviewParagraph {
  text: string;
  spoiler: boolean;
}

export interface SidecarReviewItem {
  author: string;
  date: string | null;
  score: number | null;
  likes: number | null;
  paragraphs: SidecarReviewParagraph[];
}

export interface SidecarReviewsPage {
  average: number | null;
  count: number | null;
  page: number;
  hasNextPage: boolean;
  items: SidecarReviewItem[];
}

export interface RoyalRoadSearchHit {
  title: string;
  path: string;
  cover: string | null;
}

export interface RoyalRoadFictionReviews {
  title: string | null;
  overallScore: number | null;
  ratings: number | null;
  views: number | null;
  followers: number | null;
  favorites: number | null;
  count: number | null;
  page: number;
  hasNextPage: boolean;
  items: SidecarReviewItem[];
}

export async function fetchHtmlOrThrow(url: string): Promise<string> {
  const res = await fetchApi(url);
  if (!res.ok) {
    throw new Error(`Upstream returned HTTP ${res.status}`);
  }
  return await res.text();
}

export function cleanText(value: string | null | undefined): string {
  if (!value) return '';
  return value.replace(/\s+/g, ' ').trim();
}

export function parseCompactNumber(value: string | null | undefined): number | null {
  if (!value) return null;
  const cleaned = value.replace(/,/g, '').trim();
  const match = /(-?\d+(?:\.\d+)?)\s*([KMB])?\b/i.exec(cleaned);
  if (!match) return null;
  const base = Number.parseFloat(match[1]);
  if (!Number.isFinite(base)) return null;
  const suffix = (match[2] || '').toUpperCase();
  const mult = suffix === 'B' ? 1_000_000_000 : suffix === 'M' ? 1_000_000 : suffix === 'K' ? 1_000 : 1;
  return Math.round(base * mult);
}

export function parseFloatLoose(value: string | null | undefined): number | null {
  if (!value) return null;
  const match = /(\d+(?:\.\d+)?)/.exec(value.replace(/,/g, ''));
  if (!match) return null;
  const num = Number.parseFloat(match[1]);
  return Number.isFinite(num) ? num : null;
}

export function parseIntLoose(value: string | null | undefined): number | null {
  if (!value) return null;
  const match = /(-?\d[\d,]*)/.exec(value);
  if (!match) return null;
  const num = Number.parseInt(match[1].replace(/,/g, ''), 10);
  return Number.isFinite(num) ? num : null;
}

export function splitPlainParagraphs(text: string, spoiler: boolean): SidecarReviewParagraph[] {
  const lines = text
    .replace(/\r\n?/g, '\n')
    .split(/\n{2,}/)
    .map(chunk => cleanText(chunk))
    .filter(Boolean);
  return lines.map(t => ({ text: t, spoiler }));
}

function isSpoilerElement($: CheerioAPI, el: Element): boolean {
  const tag = (el.tagName || '').toLowerCase();
  if (tag === 'details') return true;
  const $el = $(el);
  if ($el.attr('data-spoiler') !== undefined) return true;
  const cls = ($el.attr('class') || '').toLowerCase();
  return /\bspoiler(?:-new|-inner|-content|-box|-body)?\b/.test(cls);
}

function isSpoilerChrome($: CheerioAPI, el: Element): boolean {
  const tag = (el.tagName || '').toLowerCase();
  if (tag === 'button' || tag === 'input' || tag === 'label' || tag === 'summary') return true;
  const cls = ($(el).attr('class') || '').toLowerCase();
  return /\bspoiler-btn\b|\bspoiler-toggle\b|\bspoiler-header\b/.test(cls);
}

export function extractParagraphsFromContainer(
  $: CheerioAPI,
  container: AnyNode | undefined,
): SidecarReviewParagraph[] {
  if (!container) return [];
  const paragraphs: SidecarReviewParagraph[] = [];

  function pushText(raw: string, spoiler: boolean): void {
    const text = cleanText(raw);
    if (text) paragraphs.push({ text, spoiler });
  }

  function walk(node: AnyNode, inSpoiler: boolean): void {
    if (node.type === 'text') {
      const text = cleanText((node as { data?: string }).data || '');
      if (text) paragraphs.push({ text, spoiler: inSpoiler });
      return;
    }
    if (node.type !== 'tag') return;

    const el = node as Element;
    const tag = (el.tagName || '').toLowerCase();
    if (tag === 'script' || tag === 'style' || tag === 'svg') return;
    if (inSpoiler && isSpoilerChrome($, el)) return;

    const nextSpoiler = inSpoiler || isSpoilerElement($, el);

    if (isSpoilerElement($, el)) {
      const $clone = $(el).clone();
      $clone.find('button, input, label, summary, .spoiler-btn, .spoiler-toggle, .spoiler-header, script, style').remove();
      const innerPs = $clone.find('p');
      if (innerPs.length > 0) {
        innerPs.each((_, p) => pushText($(p).text(), true));
      } else {
        for (const p of splitPlainParagraphs($clone.text(), true)) {
          paragraphs.push(p);
        }
      }
      return;
    }

    if (tag === 'p' || tag === 'blockquote' || tag === 'li') {
      const nestedSpoiler = $(el).find('.spoiler, .spoiler-new, .spoiler-inner, details, [data-spoiler]');
      if (nestedSpoiler.length > 0) {
        for (const child of el.children || []) walk(child, nextSpoiler);
        return;
      }
      const htmlWithBreaks = ($(el).html() || '').replace(/<br\s*\/?>/gi, '\n\n');
      const $tmp = cheerio.load(`<div>${htmlWithBreaks}</div>`);
      for (const p of splitPlainParagraphs($tmp('div').text(), nextSpoiler)) {
        paragraphs.push(p);
      }
      return;
    }

    if (tag === 'br') return;

    const children = el.children || [];
    const hasBlockChildren = children.some(
      c =>
        c.type === 'tag' &&
        /^(p|div|section|article|blockquote|ul|ol|li|details|h[1-6])$/i.test((c as Element).tagName || ''),
    );
    if (hasBlockChildren) {
      for (const child of children) walk(child, nextSpoiler);
    } else {
      const htmlWithBreaks = ($(el).html() || '').replace(/<br\s*\/?>/gi, '\n\n');
      const $tmp = cheerio.load(`<div>${htmlWithBreaks}</div>`);
      for (const p of splitPlainParagraphs($tmp('div').text(), nextSpoiler)) {
        paragraphs.push(p);
      }
    }
  }

  const rootChildren = (container as Element).children || [];
  for (const child of rootChildren) {
    walk(child, false);
  }

  return paragraphs;
}
