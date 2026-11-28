import * as cheerio from 'cheerio';
import {
  cleanText,
  extractParagraphsFromContainer,
  fetchHtmlOrThrow,
  parseCompactNumber,
  parseFloatLoose,
  parseIntLoose,
  type SidecarReviewItem,
  type SidecarReviewsPage,
  type SidecarStats,
} from './types';

const SITE = 'https://novelfire.net';

interface AggregateRatingLd {
  ratingValue?: string | number;
  ratingCount?: string | number;
}

function readAggregateRating($: cheerio.CheerioAPI): { rating: number | null; count: number | null } {
  let rating: number | null = null;
  let count: number | null = null;

  $('script[type="application/ld+json"]').each((_, el) => {
    const raw = $(el).text();
    if (!raw || !raw.includes('AggregateRating')) return;
    try {
      const parsed = JSON.parse(raw) as AggregateRatingLd;
      if (parsed && typeof parsed === 'object') {
        if (rating === null && parsed.ratingValue != null) {
          rating = parseFloatLoose(String(parsed.ratingValue));
        }
        if (count === null && parsed.ratingCount != null) {
          count = parseIntLoose(String(parsed.ratingCount));
        }
      }
    } catch {
      const rv = /"ratingValue"\s*:\s*"?([\d.]+)"?/i.exec(raw);
      const rc = /"ratingCount"\s*:\s*"?([\d,]+)"?/i.exec(raw);
      if (rating === null && rv) rating = parseFloatLoose(rv[1]);
      if (count === null && rc) count = parseIntLoose(rc[1]);
    }
  });

  return { rating, count };
}

export function parseNovelfireStats(html: string): SidecarStats {
  const $ = cheerio.load(html);
  const ld = readAggregateRating($);

  const rankText = cleanText($('.rank').first().text());
  const rankMatch = /RANK\s*([\d,]+)/i.exec(rankText);
  const rank = rankMatch ? parseIntLoose(rankMatch[1]) : null;

  const ratingAttr = $('.rating .my-rating, .rating-star [data-rating]').first().attr('data-rating');
  const ratingNub = cleanText($('.rating .nub').first().text());
  const rating = parseFloatLoose(ratingNub) ?? parseFloatLoose(ratingAttr) ?? ld.rating;

  let chapters: number | null = null;
  let views: number | null = null;
  let bookmarks: number | null = null;
  let status: string | null = null;

  $('.header-stats > span').each((_, span) => {
    const label = cleanText($(span).find('small').text()).toLowerCase();
    const valText = cleanText($(span).find('strong').text());
    if (!label || !valText) return;
    if (label.includes('chapter')) chapters = parseCompactNumber(valText);
    else if (label.includes('view')) views = parseCompactNumber(valText);
    else if (label.includes('bookmark')) bookmarks = parseCompactNumber(valText);
    else if (label.includes('status')) status = valText;
  });

  const chaptersCard = $('a[href*="/chapters"]').filter((_, el) => $(el).find('.latest, .update').length > 0).first();
  const latestChapterName = cleanText(chaptersCard.find('.latest').text()) || null;
  const rawUpdated = cleanText(chaptersCard.find('.update').text());
  const latestChapterUpdated = rawUpdated ? rawUpdated.replace(/^Updated\s+/i, '').trim() : null;

  const reviewsCard = $('a[href*="/reviews"]').filter((_, el) => $(el).find('.latest, .update').length > 0).first();
  const revLatest = cleanText(reviewsCard.find('.latest').text());
  const revUpdate = cleanText(reviewsCard.find('.update').text());
  const revCountMatch = /([\d,.]+[KMB]?)\s*readers?/i.exec(revLatest) ?? /([\d,.]+[KMB]?)\s*reviews?/i.exec(revLatest);
  const reviewCount = (revCountMatch ? parseCompactNumber(revCountMatch[1]) : null) ?? ld.count;
  const revAvgMatch = /Average\s+score\s+is\s*([\d.]+)/i.exec(revUpdate);
  const reviewAverage = (revAvgMatch ? parseFloatLoose(revAvgMatch[1]) : null) ?? rating;

  const ratingCount = ld.count ?? reviewCount;

  const summaryParas = $('.summary .content p')
    .map((_, p) => cleanText($(p).text()))
    .get()
    .filter(Boolean);
  const summary = summaryParas.length ? summaryParas.join('\n\n') : null;

  return {
    summary,
    rank,
    rating,
    ratingCount,
    views,
    bookmarks,
    chapters,
    status,
    latestChapterName,
    latestChapterUpdated,
    reviewCount,
    reviewAverage,
  };
}

export function parseNovelfireReviews(html: string, page: number): SidecarReviewsPage {
  const $ = cheerio.load(html);

  const avgAttr = $('.rating-star .all-user-rating, .rating-star [data-rating]').first().attr('data-rating');
  const avgStrong = cleanText($('.rating-star strong').first().text());
  const average = parseFloatLoose(avgStrong) ?? parseFloatLoose(avgAttr);

  let count: number | null = null;
  $('h2, h3, .reviews-count').each((_, el) => {
    if (count !== null) return;
    const match = /([\d,]+)\s*Reviews?/i.exec(cleanText($(el).text()));
    if (match) count = parseIntLoose(match[1]);
  });

  const items: SidecarReviewItem[] = [];
  $('.review-item').each((_, el) => {
    const $item = $(el);
    const author =
      cleanText($item.find('.user-info [itemprop="name"]').first().text()) ||
      cleanText($item.find('.username').first().text()) ||
      'Anonymous';
    const date = cleanText($item.find('.review-date').first().text()) || null;

    const scoreAttr = $item.find('.rating-info .user-rating').first().attr('data-rating');
    const scoreStrong = cleanText($item.find('.rating-info strong').first().text());
    const score = parseFloatLoose(scoreAttr) ?? parseFloatLoose(scoreStrong);

    const likesText = cleanText($item.find('.like-button span').first().text());
    const likes = likesText ? parseIntLoose(likesText) : null;

    const bodyEl =
      $item.find('.review-text [itemprop="reviewBody"]').get(0) ??
      $item.find('.review-text').get(0);
    const paragraphs = extractParagraphsFromContainer($, bodyEl);
    if (paragraphs.length === 0) return;

    items.push({ author, date, score, likes, paragraphs });
  });

  let hasNextPage = $('.pagination a[rel="next"]').length > 0;
  if (!hasNextPage) {
    $('.pagination a[href*="page="]').each((_, a) => {
      const href = $(a).attr('href') || '';
      const m = /[?&]page=(\d+)/.exec(href);
      if (m && Number.parseInt(m[1], 10) > page) hasNextPage = true;
    });
  }

  return { average, count, page, hasNextPage, items };
}

export async function fetchNovelfireStats(novelPath: string): Promise<SidecarStats> {
  const cleanPath = novelPath.replace(/\/+$/, '');
  const html = await fetchHtmlOrThrow(`${SITE}${cleanPath}`);
  return parseNovelfireStats(html);
}

export async function fetchNovelfireReviews(novelPath: string, page: number): Promise<SidecarReviewsPage> {
  const cleanPath = novelPath.replace(/\/+$/, '');
  const html = await fetchHtmlOrThrow(`${SITE}${cleanPath}/reviews?page=${page}`);
  return parseNovelfireReviews(html, page);
}
