/// Diariz list endpoints return the whole collection rather than a page, so Return All / Limit is applied
/// here after the response arrives. If the API later gains skip/take this becomes real server-side paging.
export function applyLimit<T>(items: T[], returnAll: boolean, limit: number): T[] {
  if (!Array.isArray(items)) return [];
  return returnAll ? items : items.slice(0, limit);
}
