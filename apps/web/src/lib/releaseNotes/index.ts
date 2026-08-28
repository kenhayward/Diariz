/// Barrel for the release history.
///
/// Exports everything the eager side of the app may touch: the shapes, and the releases since the last
/// closed epoch. It deliberately does **not** re-export `ARCHIVE` - re-exporting it here would pull the
/// whole archive into any chunk that imports this module, which is exactly what the split exists to
/// prevent. The drill-down page imports `./archive` directly, and is itself behind a lazy route.
export type { Release, Epoch } from "./types";
export { RECENT } from "./current";
