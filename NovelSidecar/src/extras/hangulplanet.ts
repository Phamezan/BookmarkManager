import * as cheerio from 'cheerio';
import {
  cleanText,
  extractParagraphsFromContainer,
  fetchHtmlOrThrow,
  parseFloatLoose,
  parseIntLoose,
  type SidecarReviewItem,
  type SidecarReviewsPage,
  type SidecarStats,
} from './types';

const SITE = 'https://hangulplanet.com';
const STATUS_RE = /^(Ongoing|Completed|Hiatus|Dropped|Upcoming)$/i;

function resolveReviewsRoot($: cheerio.CheerioAPI): cheerio.CheerioAPI {
  const $reviews = $('#reviews');
  const tplId = $reviews.find('template[id^="P:"]').first().attr('id');
  if (tplId) {
    const streamedId = `S:${tplId.slice(2)}`;
    const $streamed = $(`[id="${streamedId}"]`);
    if ($streamed.length > 0) {
      return cheerio.load($streamed.html() || '');
    }
  }
  return cheerio.load($reviews.html() || '');
}

export function parseHangulplanetStats(html: string): SidecarStats {
  const $ = cheerio.load(html);

  const $hero = $('h1').first().parent().parent();
  const heroText = cleanText($hero.text());

  let status: string | null = null;
  $hero.find('[data-slot="badge"], span').each((_, el) => {
    if (status) return;
    const t = cleanText($(el).text());
    if (STATUS_RE.test(t)) status = t;
  });

  let rating: number | null = null;
  let ratingCount: number | null = null;
  const heroRatingMatch = /(\d(?:\.\d)?)\s*\((\d[\d,]*)\)/.exec(heroText);
  if (heroRatingMatch) {
    rating = parseFloatLoose(heroRatingMatch[1]);
    ratingCount = parseIntLoose(heroRatingMatch[2]);
  }

  let chapters: number | null = null;
  $hero.find('span, div, p').each((_, el) => {
    if (chapters !== null) return;
    if ($(el).children().length > 1) return;
    const m = /^(\d[\d,]*)\s*chapters?$/i.exec(cleanText($(el).text()));
    if (m) chapters = parseIntLoose(m[1]);
  });
  if (chapters === null) {
    const chMatch = /(?<![.\d])(\d[\d,]*)\s*chapters?/i.exec(heroText);
    if (chMatch) chapters = parseIntLoose(chMatch[1]);
  }

  const $reviews = $('#reviews');
  const revAvgText = cleanText($reviews.find('.text-4xl').first().text());
  const revAria = $reviews.find('[role="img"][aria-label*="out of 5"]').first().attr('aria-label');
  const reviewAverage = parseFloatLoose(revAvgText) ?? parseFloatLoose(revAria) ?? rating;

  let reviewCount: number | null = null;
  $reviews.find('span, p, div').each((_, el) => {
    if (reviewCount !== null) return;
    if ($(el).children().length > 0) return;
    const m = /^(\d[\d,]*)\s*reviews?$/i.exec(cleanText($(el).text()));
    if (m) reviewCount = parseIntLoose(m[1]);
  });
  if (reviewCount === null) {
    const revCountMatch = /(?<![.\d])(\d[\d,]*)\s*reviews?/i.exec(cleanText($reviews.text()));
    reviewCount = (revCountMatch ? parseIntLoose(revCountMatch[1]) : null) ?? ratingCount;
  }

  if (rating === null) rating = reviewAverage;
  if (ratingCount === null) ratingCount = reviewCount;

  let latestChapterName: string | null = null;
  let latestChapterUpdated: string | null = null;
  const firstChapterLink = $('a[href*="/chapter-"]')
    .filter((_, el) => $(el).find('time').length > 0)
    .first();
  if (firstChapterLink.length > 0) {
    latestChapterUpdated = cleanText(firstChapterLink.find('time').first().text()) || null;
    const spans = firstChapterLink
      .children('span')
      .map((_, s) => {
        const $s = $(s);
        if ($s.find('time').length > 0) return '';
        return cleanText($s.text());
      })
      .get()
      .filter(Boolean);
    if (spans.length >= 2 && /^\d+(?:\.\d+)?$/.test(spans[0])) {
      latestChapterName = `Chapter ${spans[0]}: ${spans[1]}`;
    } else if (spans.length > 0) {
      latestChapterName = spans.join(' - ');
    }
  }

  return {
    rank: null,
    rating,
    ratingCount,
    views: null,
    bookmarks: null,
    chapters,
    status,
    latestChapterName,
    latestChapterUpdated,
    reviewCount,
    reviewAverage,
    summary: null, // detail page already shows the plugin's full summary
  };
}

export function parseHangulplanetReviews(html: string, page: number): SidecarReviewsPage {
  const $ = cheerio.load(html);
  const $reviews = $('#reviews');

  const avgText = cleanText($reviews.find('.text-4xl').first().text());
  const avgAria = $reviews.find('[role="img"][aria-label*="out of 5"]').first().attr('aria-label');
  const average = parseFloatLoose(avgText) ?? parseFloatLoose(avgAria);

  let count: number | null = null;
  $reviews.find('span, p, div').each((_, el) => {
    if (count !== null) return;
    if ($(el).children().length > 0) return;
    const m = /^(\d[\d,]*)\s*reviews?$/i.exec(cleanText($(el).text()));
    if (m) count = parseIntLoose(m[1]);
  });
  if (count === null) {
    const countMatch = /(?<![.\d])(\d[\d,]*)\s*reviews?/i.exec(cleanText($reviews.text()));
    count = countMatch ? parseIntLoose(countMatch[1]) : null;
  }

  const $list = resolveReviewsRoot($);
  const items: SidecarReviewItem[] = [];

  $list('article').each((_, el) => {
    const $art = $list(el);
    const author =
      cleanText($art.find('span.font-medium').first().text()) ||
      cleanText($art.find('a').first().text()) ||
      'Anonymous';
    const date = cleanText($art.find('time').first().text()) || null;

    const scoreAria = $art.find('[role="img"][aria-label*="out of 5"]').first().attr('aria-label');
    const score = parseFloatLoose(scoreAria);

    const voteEl = $art.find('[aria-label="review vote"] [aria-label^="Score "]').first();
    const likes = voteEl.length > 0
      ? parseIntLoose(voteEl.text()) ?? parseIntLoose(voteEl.attr('aria-label'))
      : null;

    const proseEl = $art.find('.prose').get(0);
    const paragraphs = extractParagraphsFromContainer($list, proseEl);
    if (paragraphs.length === 0) return;

    items.push({ author, date, score, likes, paragraphs });
  });

  let hasNextPage = false;
  $list('a[href*="rpage="]').each((_, a) => {
    const href = $list(a).attr('href') || '';
    const m = /[?&]rpage=(\d+)/.exec(href);
    if (m && Number.parseInt(m[1], 10) > page) hasNextPage = true;
  });

  return { average, count, page, hasNextPage, items };
}

export async function fetchHangulplanetStats(novelPath: string): Promise<SidecarStats> {
  const cleanPath = novelPath.replace(/\/+$/, '');
  const html = await fetchHtmlOrThrow(`${SITE}${cleanPath}`);
  return parseHangulplanetStats(html);
}

export async function fetchHangulplanetReviews(novelPath: string, page: number): Promise<SidecarReviewsPage> {
  const cleanPath = novelPath.replace(/\/+$/, '');
  const query = page > 1 ? `?rpage=${page}` : '';
  const html = await fetchHtmlOrThrow(`${SITE}${cleanPath}${query}`);
  return parseHangulplanetReviews(html, page);
}
