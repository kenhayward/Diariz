import { useCallback, useEffect, useRef, useState } from "react";
import type { ClipRequest } from "./clipPlayback";

/// Fetches the audio for one span of one speaker. Supplied by the caller because the two surfaces that
/// audition speech reach it through different routes: a person's Voiceprint tab through their attributions,
/// the review queue through a pending suggestion, which is by definition not attributed to anyone yet.
export type ClipFetcher = (speakerId: string, fromMs: number, toMs: number) => Promise<Blob>;

/// Plays a queue of clips through one `<audio>` element, one segment at a time.
///
/// Each clip is fetched as a Blob rather than pointed at by a token-bearing URL - see `api.personClip` for
/// why. Only one clip plays at a time per hook instance, so two rows cannot talk over each other.
///
/// `fetchClip` is a dependency of the playback callbacks, so callers must keep it stable (a `useCallback`,
/// or a module-level function). An inline arrow would rebuild the queue machinery on every render.
export function useClipPlayer(fetchClip: ClipFetcher) {
  const audioRef = useRef<HTMLAudioElement | null>(null);
  const urlRef = useRef<string | null>(null);
  const queueRef = useRef<{ speakerId: string; items: ClipRequest[]; index: number } | null>(null);
  const [playingSegmentId, setPlayingSegmentId] = useState<string | null>(null);

  const release = useCallback(() => {
    if (urlRef.current) {
      URL.revokeObjectURL(urlRef.current);
      urlRef.current = null;
    }
  }, []);

  const stop = useCallback(() => {
    queueRef.current = null;
    audioRef.current?.pause();
    release();
    setPlayingSegmentId(null);
  }, [release]);

  const playAt = useCallback(
    async (index: number) => {
      const q = queueRef.current;
      if (!q || index >= q.items.length) {
        stop();
        return;
      }
      const item = q.items[index];
      q.index = index;

      const blob = await fetchClip(q.speakerId, item.fromMs, item.toMs);
      // A newer press may have superseded this fetch while it was in flight.
      if (queueRef.current !== q || q.index !== index) return;

      release();
      urlRef.current = URL.createObjectURL(blob);

      const audio = (audioRef.current ??= new Audio());
      audio.onended = () => void playAt(index + 1);
      audio.src = urlRef.current;
      setPlayingSegmentId(item.segmentId);
      await audio.play();
    },
    [fetchClip, release, stop],
  );

  const play = useCallback(
    async (speakerId: string, items: ClipRequest[]) => {
      if (items.length === 0) return;
      queueRef.current = { speakerId, items, index: 0 };
      await playAt(0);
    },
    [playAt],
  );

  useEffect(() => () => release(), [release]);

  return { play, stop, playingSegmentId };
}
