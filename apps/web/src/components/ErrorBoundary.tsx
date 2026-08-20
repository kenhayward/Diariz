import { Component, type ReactNode } from "react";
import { captureException } from "../lib/telemetry";
import { isStaleChunkError } from "../lib/chunkReload";

interface Props {
  children: ReactNode;
  /// When this value changes the boundary clears its error and retries (pass the route path so navigating
  /// to another page recovers from a crash instead of staying stuck on the fallback).
  resetKey: unknown;
  message: string;
  hint?: string;
  /// Copy for the missing-chunk case, supplied translated by `RouteErrorBoundary` (this is a class
  /// component, so it cannot call `useTranslation` itself - same reason `message` and `hint` are props).
  /// Omitted, the generic message is shown instead.
  staleChunk?: { title: string; hint: string; action: string };
  /// Injected only by tests; the real thing reloads the page.
  reload?: () => void;
}
interface State {
  error: Error | null;
}

/// Catches render errors in its subtree and shows a message instead of letting the error unmount the whole
/// React app (which leaves a blank `#root`). Wraps the routed detail panel so a crash there stays contained -
/// the sidebar and chat keep working - and the user sees what went wrong rather than a blank screen. Resets on
/// navigation via `resetKey`. See issue #289 (a folder page could blank the app).
export default class ErrorBoundary extends Component<Props, State> {
  state: State = { error: null };

  static getDerivedStateFromError(error: Error): State {
    return { error };
  }

  componentDidUpdate(prev: Props) {
    // A new route mounted - clear the previous page's error so the new page gets a chance to render.
    if (prev.resetKey !== this.props.resetKey && this.state.error) this.setState({ error: null });
  }

  componentDidCatch(error: Error, info: unknown) {
    // The error would otherwise vanish with the unmounted tree; log it so it is still diagnosable.
    console.error("Detail panel crashed:", error, info);
    // And report it, so a crash a user hits is visible without them reporting it.
    captureException(error);
  }

  render() {
    if (this.state.error) {
      // A page whose chunk a deploy deleted is not a crash the user can do anything about, and the raw
      // "Failed to fetch dynamically imported module ..." says nothing to them. It is one reload from fixed,
      // and only the app knows that. Reaching here means the automatic reload declined - it is mid-capture,
      // or it already tried - so this is the user's own copy of that decision, not a duplicate of it.
      const stale = this.props.staleChunk;
      if (stale && isStaleChunkError(this.state.error)) {
        return (
          <div role="alert" className="p-4 text-sm">
            <p className="font-medium dark:text-gray-100">{stale.title}</p>
            <p className="mt-1 text-gray-500 dark:text-gray-400">{stale.hint}</p>
            <button
              type="button"
              onClick={this.props.reload ?? (() => window.location.reload())}
              className="mt-3 rounded bg-blue-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-blue-700"
            >
              {stale.action}
            </button>
          </div>
        );
      }
      return (
        <div role="alert" className="p-4 text-sm">
          <p className="font-medium text-red-600 dark:text-red-400">{this.props.message}</p>
          {this.props.hint && <p className="mt-1 text-gray-500 dark:text-gray-400">{this.props.hint}</p>}
          <p className="mt-2 text-xs break-words text-gray-400 dark:text-gray-500">{this.state.error.message}</p>
        </div>
      );
    }
    return this.props.children;
  }
}
