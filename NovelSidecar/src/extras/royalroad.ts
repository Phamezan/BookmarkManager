import * as cheerio from 'cheerio';
import {
  cleanText,
  extractParagraphsFromContainer,
  fetchHtmlOrThrow,
  parseCompactNumber,
  parseFloatLoose,
  parseIntLoose,
  type RoyalRoadFictionReviews,
  type RoyalRoadSearchHit,
  type SidecarReviewItem,
} from './types';

const SITE = 'https://www.royalroad.com';
export const ROYALROAD_PATH_RE = /^\/fiction\/\d+\/[a-z0-9-]+$/;

function normalizeFictionPath(href: string | undefined): string | null {
  if (!href) return null;
  let pathname = href.trim();
  if (/^https?:\/\//i.test(pathname)) {
    try {
      pathname = new URL(pathname).pathname;
    } catch {
      return null;
    }
  }
  const match = /^(\/fiction\/\d+\/[a-z0-9-]+)/i.exec(pathname);
  if (!match) return null;
  return match[1].toLowerCase();
}

export function parseRoyalRoadSearch(html: string): RoyalRoadSearchHit[] {
  const $ = cheerio.load(html);
  const hits: RoyalRoadSearchHit[] = [];
  const seen = new Set<string>();

  $('.fiction-list-item').each((_, el) => {
    const $card = $(el);
    const $link = $card.find('h2.fiction-title a[href*="/fiction/"]').first();
    const title = cleanText($link.text());
    const path = normalizeFictionPath($link.attr('href'));
    if (!title || !path || seen.has(path)) return;
    seen.add(path);

    const $img = $card.find('img.cover, img').first();
    const rawCover = ($img.attr('src') || $img.attr('data-src') || '').trim();
    const cover =
      rawCover && /^https?:\/\//i.test(rawCover)
        ? rawCover
        : rawCover.startsWith('/')
          ? `${SITE}${rawCover}`
          : null;

    hits.push({ title, path, cover });
  });

  return hits;
}

function parseScoreFromStar($: cheerio.CheerioAPI, $scope: cheerio.Cheerio<any>): number | null {
  const star = $scope.find('.star[data-content], .star[aria-label], .star[title]').first();
  if (star.length > 0) {
    const raw = star.attr('data-content') || star.attr('aria-label') || star.attr('title') || '';
    const m = /([\d.]+)\s*(?:\/\s*5|out of\s*5|stars?)?/i.exec(raw);
    if (m) return parseFloatLoose(m[1]);
  }
  const scoreText = cleanText($scope.find('.overall-score-container .score, .review-score').first().text());
  return parseFloatLoose(scoreText);
}

export function parseRoyalRoadFiction(html: string, page: number): RoyalRoadFictionReviews {
  const $ = cheerio.load(html);

  const title =
    cleanText($('.fic-header h1, h1').first().text()) ||
    cleanText($('meta[property="og:title"]').attr('content')) ||
    null;

  let overallScore: number | null = parseFloatLoose($('meta[property="books:rating:value"]').attr('content'));
  if (overallScore === null) {
    const starEl = $('.fiction-stats .star[data-content], .fiction-stats .star[aria-label], .fiction-stats .star[title]').first();
    const raw = starEl.attr('data-content') || starEl.attr('aria-label') || starEl.attr('title') || '';
    overallScore = parseFloatLoose(raw);
  }

  let ratings: number | null = null;
  let views: number | null = null;
  let followers: number | null = null;
  let favorites: number | null = null;

  const statItems = $('.fiction-stats .stats-content li');
  for (let i = 0; i < statItems.length; i++) {
    const label = cleanText($(statItems[i]).text()).toLowerCase();
    const nextText = i + 1 < statItems.length ? cleanText($(statItems[i + 1]).text()) : '';
    if (label.startsWith('total views')) {
      views = parseCompactNumber(nextText) ?? parseCompactNumber(label.replace('total views', ''));
    } else if (label.startsWith('followers')) {
      followers = parseCompactNumber(nextText) ?? parseCompactNumber(label.replace('followers', ''));
    } else if (label.startsWith('favorites') || label.startsWith('favourites')) {
      favorites = parseCompactNumber(nextText) ?? parseCompactNumber(label.replace(/favou?rites/, ''));
    } else if (label.startsWith('ratings')) {
      ratings = parseCompactNumber(nextText) ?? parseCompactNumber(label.replace('ratings', ''));
    }
  }

  if (ratings === null) {
    const m = /([\d,]+)\s*ratings?/i.exec($('.fiction-stats').text());
    if (m) ratings = parseIntLoose(m[1]);
  }

  const items: SidecarReviewItem[] = [];
  $('.reviews-container .review, .review-item').each((_, el) => {
    const $rev = $(el);
    const author =
      cleanText($rev.find('.review-meta a[href*="/profile/"], .review-author, .username').first().text()) ||
      'Anonymous';

    const $time = $rev.find('time').first();
    const date = cleanText($time.text()) || cleanText($rev.find('.review-date').first().text()) || null;

    const score = parseScoreFromStar($, $rev);
    const likesEl = $rev.find('.review-helpful, .upvotes, [data-upvotes]').first();
    const likes = likesEl.length > 0
      ? parseIntLoose(likesEl.attr('data-upvotes') || likesEl.text())
      : null;

    const contentEl = $rev.find('.review-content').get(0) ?? $rev.find('.review-inner').get(0);
    const paragraphs = extractParagraphsFromContainer($, contentEl);
    if (paragraphs.length === 0) return;

    items.push({ author, date, score, likes, paragraphs });
  });

  let count: number | null = null;
  const revHeader = cleanText($('.reviews-container .caption, .reviews-header, #reviews h2, #reviews h3').first().text());
  const countMatch = /\(([\d,]+)\)/.exec(revHeader) ?? /([\d,]+)\s*reviews?/i.exec(revHeader);
  if (countMatch) count = parseIntLoose(countMatch[1]);
  if (count === null && items.length > 0) count = items.length;

  let hasNextPage = $('.reviews-container .pagination a[rel="next"], .pagination a[rel="next"]').length > 0;
  if (!hasNextPage) {
    $('.reviews-container .pagination a, .pagination a').each((_, a) => {
      const $a = $(a);
      const dataPage = parseIntLoose($a.attr('data-page'));
      if (dataPage !== null && dataPage > page) hasNextPage = true;
      const href = $a.attr('href') || '';
      const m = /[?&]reviews=(\d+)/.exec(href);
      if (m && Number.parseInt(m[1], 10) > page) hasNextPage = true;
    });
  }

  return {
    title,
    overallScore,
    ratings,
    views,
    followers,
    favorites,
    count,
    page,
    hasNextPage,
    items,
  };
}

export async function fetchRoyalRoadSearch(title: string): Promise<RoyalRoadSearchHit[]> {
  const url = `${SITE}/fictions/search?title=${encodeURIComponent(title.trim())}`;
  const html = await fetchHtmlOrThrow(url);
  return parseRoyalRoadSearch(html);
}

export async function fetchRoyalRoadFiction(fictionPath: string, page: number): Promise<RoyalRoadFictionReviews> {
  const query = page > 1 ? `?reviews=${page}` : '';
  const html = await fetchHtmlOrThrow(`${SITE}${fictionPath}${query}`);
  return parseRoyalRoadFiction(html, page);
}
