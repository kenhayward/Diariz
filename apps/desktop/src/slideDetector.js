"use strict";

// Slide-change detection for auto-capture. Pure: no Electron, no image library, no timers. `main.js`
// owns the frame source and the clock; everything here is decided from a stream of hashes, which is what
// lets the whole state machine be unit-tested against synthetic frames rather than against a screen.
//
// The approach follows docs/Automatic_Slide_Capture.md: perceptual hashing, not pixel differencing.
// A moving mouse cursor, a blinking text caret and codec noise all move a large number of pixels while
// changing nothing a viewer would call a new slide, and only a structural hash survives them.

const HASH_SIZE = 16; // dHash grid: (HASH_SIZE + 1) x HASH_SIZE samples -> HASH_SIZE^2 = 256 bits

const DEFAULTS = {
  /// Distance above which the screen is considered to have changed at all.
  changeThreshold: 24,
  /// Distance within which two consecutive samples count as "the same content, still there".
  /// Tighter than changeThreshold: this decides when something has *settled*.
  stabilityThreshold: 12,
  /// Consecutive steady samples before a change is committed. The single most important setting here -
  /// see the note on `observe` below.
  ///
  /// 3, not 2. A cross-fade does not move through the hash space smoothly: dHash records the *sign* of
  /// each adjacent-pixel comparison, and those signs flip in a cluster around the midpoint, so a
  /// multi-second transition sits on one intermediate digest for several samples in a row (measured:
  /// 25% and 50% through a fade hash identically). At 2 that intermediate accumulates a streak and a
  /// half-drawn frame commits as a slide - the exact failure this window exists to prevent. See the
  /// "settles, not mid-transition" cases in the tests.
  ///
  /// The cost is dwell time: a slide must hold for stableSamples x the caller's sample interval to be
  /// captured at all. That is the argument for sampling faster than 1 Hz rather than for lowering this.
  stableSamples: 3,
  /// Distance within which a newly committed slide is judged to be one already captured (the presenter
  /// went back). Stricter than changeThreshold on purpose: a false duplicate silently loses a slide,
  /// which is worse than an extra capture the user can delete.
  dedupeThreshold: 10,
};

/// Population count of a byte, by table - a per-bit loop over 32 bytes runs on every sample.
const POPCOUNT = new Uint8Array(256);
for (let i = 0; i < 256; i++) POPCOUNT[i] = (i & 1) + POPCOUNT[i >> 1];

/**
 * Difference hash of one sample. `bitmap` is BGRA bytes (Electron's nativeImage bitmap layout) for an
 * image of exactly (HASH_SIZE + 1) x HASH_SIZE - the caller is responsible for the resize, because the
 * resize chain has to be identical for every hash that will ever be compared (see the note in
 * docs/Automatic_Slide_Capture.md §4.4).
 *
 * Each bit is one horizontal comparison: is this pixel brighter than the one to its right. That is why
 * the hash tracks *structure* rather than brightness - dimming the whole frame leaves every comparison
 * unchanged, so a projector fading has no effect, while text or a chart moving does.
 *
 * Returns a 32-byte Uint8Array (256 bits).
 */
function dhash(bitmap, width, height) {
  const out = new Uint8Array((width - 1) * height / 8);
  let bit = 0;
  for (let y = 0; y < height; y++) {
    let left = luma(bitmap, (y * width) * 4);
    for (let x = 1; x < width; x++) {
      const right = luma(bitmap, (y * width + x) * 4);
      if (left > right) out[bit >> 3] |= 0x80 >> (bit & 7);
      left = right;
      bit++;
    }
  }
  return out;
}

/// Rec. 601 luma from a BGRA pixel at `i`. Matching the eye's sensitivity matters here: a green-on-black
/// chart and a blue-on-black one are very different to a viewer and nearly identical to a plain average.
function luma(bitmap, i) {
  return 0.114 * bitmap[i] + 0.587 * bitmap[i + 1] + 0.299 * bitmap[i + 2];
}

/// Number of differing bits between two digests: 0 is identical, 256 maximally different.
function hamming(a, b) {
  let total = 0;
  for (let i = 0; i < a.length; i++) total += POPCOUNT[a[i] ^ b[i]];
  return total;
}

/**
 * The change-detection state machine. Feed it one hash per sample; it decides when the screen has
 * settled on something new.
 *
 * Lifecycle of a capture, and why it takes three calls rather than one:
 *
 *   observe(hash, atMs)  -> null, or a candidate that has held steady long enough to be worth grabbing
 *   confirm(hash)        -> the caller grabbed the screen at full resolution; is it still that content?
 *   reject()             -> the caller could not grab at all
 *
 * The split exists because the grab happens *after* the decision and can catch a different screen. The
 * detector cannot see that, so the caller has to hand back what it actually got.
 */
function createDetector(overrides) {
  const cfg = { ...DEFAULTS, ...overrides };

  // The content currently believed to be on screen. Null until the first commit.
  let committedHash = null;
  // The change in progress: what it looks like, when it first appeared, and how many consecutive
  // samples have agreed with it so far.
  let candidateHash = null;
  let candidateFirstSeenAtMs = 0;
  let streak = 0;
  // Every distinct slide captured this session, for back-navigation dedupe. Bounded in practice by the
  // caller's capture cap - a repeat is never appended, only recognised.
  const captured = [];

  const clearCandidate = () => {
    candidateHash = null;
    streak = 0;
  };

  const startCandidate = (hash, atMs) => {
    candidateHash = hash;
    candidateFirstSeenAtMs = atMs;
    streak = 1;
  };

  return {
    /**
     * Take one sample. Returns null while nothing has settled, or `{ firstSeenAtMs, hash }` for a
     * candidate the caller should now grab and confirm.
     *
     * The stability requirement is what makes this usable rather than a junk generator. Without it every
     * mid-transition frame, half-drawn animation and mid-scroll position commits as its own slide. It
     * also handles embedded video for free: nothing ever holds still, so nothing is ever committed, and
     * no separate motion detection is needed.
     */
    observe(hash, atMs) {
      // Nothing committed yet, so there is no "unchanged" to compare against - the first thing the
      // screen settles on is the first slide.
      if (committedHash !== null && hamming(hash, committedHash) <= cfg.changeThreshold) {
        // Back to what is already captured. Any change that was building was a transient - an overlay,
        // a notification, a transition frame - so it is dropped rather than left to accumulate a streak
        // out of unrelated samples.
        clearCandidate();
        return null;
      }

      if (candidateHash !== null && hamming(hash, candidateHash) <= cfg.stabilityThreshold) streak++;
      else startCandidate(hash, atMs);

      if (streak < cfg.stableSamples) return null;
      return { firstSeenAtMs: candidateFirstSeenAtMs, hash: candidateHash };
    },

    /**
     * Report what the full-resolution grab actually contained. `hash` must come through the same resize
     * chain as the samples, or the comparison is meaningless.
     *
     * Returns `{ accepted, duplicate }`:
     *   - accepted false: the screen moved on during the grab; nothing is committed and the detector
     *     goes back to watching. The caller must discard the image it grabbed.
     *   - duplicate true: this is a slide already captured (the presenter went back). It still becomes
     *     the committed state - it is genuinely what is on screen - but the caller should not file a
     *     second copy of it.
     */
    confirm(hash) {
      if (candidateHash === null) return { accepted: false, duplicate: false };
      if (hamming(hash, candidateHash) > cfg.stabilityThreshold) {
        clearCandidate();
        return { accepted: false, duplicate: false };
      }

      const duplicate = captured.some((seen) => hamming(seen, candidateHash) <= cfg.dedupeThreshold);
      // Adopt it either way. A repeat that was not adopted would leave the committed state stale, so
      // every later sample would read as a change and re-commit the same slide once per stability
      // window for the rest of the meeting.
      committedHash = candidateHash;
      if (!duplicate) captured.push(candidateHash);
      clearCandidate();
      return { accepted: true, duplicate };
    },

    /// The grab failed outright, so there is nothing to confirm against. Drops the candidate without
    /// touching the committed state, leaving the next samples to detect the same change again.
    reject: clearCandidate,

    /// Whether this content matches a slide already captured this session.
    isDuplicate: (hash) => captured.some((seen) => hamming(seen, hash) <= cfg.dedupeThreshold),

    /// How many distinct slides have been captured - the caller's cap is enforced against this.
    get capturedCount() {
      return captured.length;
    },
  };
}

module.exports = { HASH_SIZE, DEFAULTS, dhash, hamming, createDetector };
