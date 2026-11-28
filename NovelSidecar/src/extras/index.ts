import { fetchHangulplanetReviews, fetchHangulplanetStats } from './hangulplanet';
import { fetchNovelfireReviews, fetchNovelfireStats } from './novelfire';
import { fetchRoyalRoadFiction, fetchRoyalRoadSearch, ROYALROAD_PATH_RE } from './royalroad';
import type {
  RoyalRoadFictionReviews,
  RoyalRoadSearchHit,
  SidecarReviewsPage,
  SidecarStats,
} from './types';

export type ExtrasSourceId = 'novelfire' | 'hangulplanet';

export function isExtrasSource(source: string | null): source is ExtrasSourceId {
  if (!source) return false;
  const normalized = source.trim().toLowerCase();
  return normalized === 'novelfire' || normalized === 'hangulplanet';
}

export async function getSourceStats(source: ExtrasSourceId, path: string): Promise<SidecarStats> {
  return source === 'novelfire'
    ? await fetchNovelfireStats(path)
    : await fetchHangulplanetStats(path);
}

export async function getSourceReviews(
  source: ExtrasSourceId,
  path: string,
  page: number,
): Promise<SidecarReviewsPage> {
  return source === 'novelfire'
    ? await fetchNovelfireReviews(path, page)
    : await fetchHangulplanetReviews(path, page);
}

export {
  fetchRoyalRoadFiction,
  fetchRoyalRoadSearch,
  ROYALROAD_PATH_RE,
  type RoyalRoadFictionReviews,
  type RoyalRoadSearchHit,
  type SidecarReviewsPage,
  type SidecarStats,
};
