# Spike: alternatives to pyannote for diarization and speaker identification

Status: **in progress** (started 2026-10-02). Timebox: about 4 working days. No change to the worker until
the spike reports; everything here runs in throwaway containers.

## Why

pyannote has been the diarizer since the first release. The question is whether the market has moved past
it: the open pyannote model has not improved since `community-1` (Sep 2025) while the accuracy gains went to
pyannoteAI's paid models (Precision-2/3, Live-1), and new open models have appeared since - most notably
NVIDIA's Nemotron-3-Diarization (2026-09-23).

Two facts reframe the question before any benchmark:

- **pyannote the library is maintained** (4.0 in Sep 2025, 4.0.1-4.0.7 through Jun 2026). It is the *open
  model* that has stalled.
- **We are not running pyannote's best open model.** `DIARIZATION_MODEL` still defaults to
  `pyannote/speaker-diarization-3.1` (`src/Diariz.Worker/config.py`), though pyannote 4 shipped
  `speaker-diarization-community-1`, which is better on every published benchmark. It is a config change.

And one fact from production data (aggregates from the 2026-09-30 backup, 210 recordings with audio):
the current transcripts carry **6.9 diarized labels on average against 2.9 named people**, and 26% of all
labels have under 30 s of speech. **Over-splitting is the dominant error** - one person spread across
several `SPEAKER_nn` labels - so speaker-count accuracy and confusion matter more than boundary precision.

## Two separate jobs

| Job | Question | Today | What a replacement must provide |
|---|---|---|---|
| **Diarization** | who spoke when, within one recording | pyannote 3.1 via WhisperX | (start, end, speaker) turns - the only seam is `pipeline._diarize`, consumed by `whisperx.assign_word_speakers` |
| **Identification** | whose voice is this, across recordings | SpeechBrain ECAPA, 192-d, in `Speakers.Embedding` / `SpeakerProfiles` | a fixed-size embedding per speaker; changing it re-embeds every voiceprint and recalibrates the accept / suggest / ignore bands |

Resemblyzer, the Vosk speaker model and CAM++ are **embedding models** - they only compete for the second
job. A diarizer built on them still needs VAD, segmentation and clustering around them.

## Candidates

Constraints: open source, fully local, licence allows commercial self-hosting (code **and** weights),
diarization stage within ~9 GB VRAM, CUDA 12.8 / Blackwell, AMD ROCm a bonus.

### Diarization

| # | Candidate | Type | Licence (code / weights) | Why | Known risks |
|---|---|---|---|---|---|
| D0 | pyannote 4 + **3.1** | pipeline | MIT / MIT | **control** - production today | - |
| D1 | pyannote 4 + **community-1** (regular and exclusive) | pipeline | MIT / CC-BY-4.0 | config-only upgrade; published DER better everywhere | open model has stalled |
| D2 | **NVIDIA Nemotron-3-Diarization** | end-to-end (Sortformer family), 100M | Apache (NeMo) / OpenMDW-1.1 | best published open DER (DIHARD3 12.7%); native streaming 80 ms-30 s | **8-speaker cap**; NeMo 3 + Python 3.12; 9 days old; ROCm unknown |
| D3 | **sherpa-onnx** (pyannote segmentation ONNX + 3D-Speaker CAM++ / ERes2NetV2) | pipeline, no torch | Apache-2.0 / Apache-2.0 | escapes torch/CUDA pinning; very active; ROCm via ONNX Runtime EP | plain agglomerative clustering; no published DER |
| D4 | **3D-Speaker** diarization pipeline | pipeline | Apache-2.0 / Apache-2.0 | fast (~0.03 RTF CPU) | Mandarin-leaning; self-reported weak under 30 s (bad for live windows) |

Excluded with reasons: **DiariZen** (weights CC-BY-NC), **Sortformer v1** (CC-BY-NC), **Sortformer v2**
(4-speaker cap; superseded by D2), **diart** (stalled since Feb 2025), **Senko** (AMI-IHM 26.5%, no overlap).
The 8-speaker cap on D2 is less of a problem than it sounds: only 2 of 210 recordings with audio have more
than 8 named people.

### Identification (embedding models)

| # | Model | Dim | VoxCeleb1-O EER | Licence |
|---|---|---|---|---|
| E0 | SpeechBrain ECAPA (**control**) | 192 | 0.80% | Apache-2.0 |
| E1 | 3D-Speaker ERes2NetV2 | 192 (verify) | 0.61% | Apache-2.0 |
| E2 | 3D-Speaker CAM++ | 192 / 512 (verify) | 0.65% | Apache-2.0 |
| E3 | NVIDIA TitaNet-Large | 192 | 0.66% | CC-BY-4.0 |
| E4 | Resemblyzer (GE2E) | 256 | not published | Apache-2.0 |
| E5 | Vosk spk-0.4 (x-vector) | ~128 (verify) | not published | Apache-2.0 |

E4 and E5 are included as reference points because they were asked about. The research expects both to
lose to ECAPA: Resemblyzer has had no release since 2023 and Vosk's speaker model since about 2022. The
"voicebrain" repository that was mentioned could not be found; SpeechBrain (E0) is the likely referent.

## Evaluation set

Built from the platform backup by `harness/build_evalset.py`, into a directory **outside the repo**
(`C:\Users\kenha\diariz-spike-data`). Real meeting audio never enters git, and every report carries
counts and rates only (see "Never put production data in the repo" in CLAUDE.md).

1. **Silver set (built).** 18 recordings, about 8 hours, capped at 30 min each. There are 6 in each
   bucket of named people (2, 3-4, 5-8), picked deterministically and spread over capture sources.
   - Eligible means: a full-file transcription, at least 90% of speech attributed to a named person, at
     least 2 named people, at least 5 minutes long, and no speaker flagged multi-speaker.
   - 84 of 210 recordings qualified.
   - The reference is the current transcript, with every label the user named as the same person merged.
     That merge is the human correction of an over-split.
   - **Bias:** its boundaries came from the old pipeline, and a split nobody noticed (two people under
     one name, unflagged) stays in the reference. It favours D0 on boundaries, so read **confusion** and
     **speaker-count error** from it, not raw DER.
2. **Gold set (to do, needs about 2 hours of human labelling).** About 10 excerpts of 5 minutes from the
   silver recordings, with speaker turns corrected by hand. This removes the silver set's bias and is the
   tie-breaker if silver results are close.
3. **Public set (to do).** The AMI test set (Mix-Headset) and the VoxConverse test set, with their
   published RTTMs. They make our numbers comparable with published ones and act as a sanity check on the
   harness.
4. **Identification set (built, thin).** 60 voiceprint clips (up to 120 s each, the worker's
   `EMBED_MAX_SECONDS`) for 21 named people, 9 of whom appear in 2+ recordings. To be widened to every
   recording with audio, not just the 18, before Day 3.

## Metrics

- **Accuracy:** DER (collars of 0.25 s and 0 s) with its parts (confusion, missed speech, false alarm),
  plus JER, speaker-count error and over-split ratio (hypothesis speakers / reference speakers).
- **Cost:** real-time factor and peak VRAM, on both the 8 GB laptop (RTX 4070) and the 32 GB card.
- **Live mode:** the same metrics on 60 s windows, plus whether per-window speakers can be stitched
  across windows with the identification embeddings (the current live design).
- **Identification:** leave-one-recording-out top-1 accuracy, the gap between true-match and impostor
  cosine distances, and how the current accept / suggest / ignore bands would split the results.

## Harness

```
spikes/diarization/
  harness/          CPU image: build_evalset.py, score.py, rttm.py (+ tests)
  candidates/
    common/runner.py   contract: /data/eval/audio/*.wav -> /data/out/<candidate>/<uri>.rttm + runs.jsonl
    pyannote/          D0 + D1
    nemotron/          D2 (to do)
    sherpa/            D3 (to do)
    3dspeaker/         D4 (to do)
```

Each candidate is its own image, so no candidate's dependencies touch the worker's pins. See `README.md`
for the commands.

## Plan

| Day | Work | Output |
|---|---|---|
| 0 | Evaluation set + harness + D0/D1 end to end (**done 2026-10-02**) | silver set, scorer, first scores |
| 1 | D2 Nemotron, D3 sherpa-onnx; public set download + scoring | full silver + public table on 8 GB |
| 2 | D4; 60 s window runs; repeat on 32 GB card; gold excerpts labelled | live-mode table; gold scores |
| 3 | Identification track E0-E5 on the widened set | identification table, band fit |
| 4 | Write-up + recommendation; issue(s) for the chosen change | `FINDINGS.md` |

## Decision rule

Adopt a diarizer only if, against D0 on our own audio, it:

1. lowers confusion and speaker-count error on **both** the silver and gold sets;
2. fits the 9 GB budget alongside the rest of the worker;
3. is no slower than D0 per hour of audio;
4. does not regress on 60 s windows.

The expected shape of the answer is to switch to D1 straight away, since it costs nothing and is very
likely better. D2 or D3 would then be adopted only if they clear the bar by a margin worth a dependency
change.

Change the embedding model only if it beats ECAPA clearly on our identification set. A tie is not worth
re-embedding every voiceprint and recalibrating the identification bands.

## Day 0 results (silver set, RTX 4070 Laptop 8 GB, collar 0.25 s)

| candidate | DER % | confusion % | spk count err | hyp/ref spk | RTF | peak VRAM |
|---|---|---|---|---|---|---|
| D0 3.1 exclusive | 22.0 | 5.8 | 0.6 | 1.00 | 0.027 | 2.4 GB |
| D0 3.1 regular | 23.6 | 5.3 | 0.6 | 1.03 | 0.027 | 2.4 GB |
| D1 community-1 exclusive | 22.9 | 6.7 | 1.1 | 1.08 | 0.029 | 2.4 GB |
| D1 community-1 regular | 24.5 | 6.2 | 1.0 | 1.11 | 0.029 | 2.4 GB |

Missed speech is 12.6% for every candidate. That is the silver reference's Whisper-segment granularity,
as expected, not a diarizer difference.

Reading so far:

- **community-1 is not a free win on our audio.** It is slightly worse than 3.1 on confusion and speaker
  count. This runs against its published benchmarks, but the silver set favours 3.1 (see the bias note).
- **Selection bias.** On these 18 recordings, 3.1 does *not* over-split (1.00 hyp/ref), although
  production shows 6.9 labels against 2.9 named people overall. The eligibility rule (90% of speech
  named) selects the recordings that diarized cleanly enough for someone to name everyone, which is
  exactly the easy end.
- **The gold set must therefore come from the hard end.** Recordings with many unnamed labels are where
  the over-splitting lives, and they cannot have a silver reference at all.

## Day 1 results (RTX 4070 Laptop 8 GB)

Confusion % / DER %; ours = silver set, collar 0.25 s; AMI = test set, Mix-Headset, collar 0 s.

| candidate | ours conf | ours DER | AMI conf | AMI DER | RTF | peak VRAM |
|---|---|---|---|---|---|---|
| pyannote 3.1 (regular) - **production** | 5.3 | 23.6 | 4.3 | 17.3 | 0.027-0.031 | 2.6 GB |
| pyannote community-1 (regular) | 6.2 | 24.5 | 3.9 | **16.9** | 0.029-0.032 | 2.6 GB |
| Nemotron offline, NeMo default post-proc | 5.7 | 28.4 | **1.0** | 25.9 | **0.003** | 3.2 GB |
| Nemotron offline, CALLHOME post-proc | 6.1 | 24.8 | 1.4 | 20.3 | 0.005 | 3.1 GB |
| Nemotron 1 s streaming ("low") | 5.7 | 28.5 | 1.0 | 26.1 | 0.068 | 3.3-6.9 GB (re-measure) |
| sherpa-onnx + TitaNet, threshold 1.2 (CPU) | 8.3 | 25.5 | 11.5 | 24.5 | 0.10 CPU | 0 |

**Harness check:** pyannote's published AMI-IHM DER is 18.8 for 3.1 and 17.0 for community-1. We
measure 17.3 and 16.9. Our figure is a per-file mean, theirs is time-weighted, so the small gap is
expected.

Reading so far:

- **Nemotron attributes speech far better than anything else on AMI.** Its confusion is 1.0-1.4%,
  against 2.5-4.3% for pyannote. On our own silver set it ties pyannote, though the silver reference
  favours 3.1 there.
- **Nemotron misses more speech.** That is all of its DER deficit:
  - Its missed speech on AMI is 23.6% with NeMo's untuned default post-processing, and 12.8% with
    NVIDIA's CALLHOME settings, against 9.3% for pyannote.
  - Post-processing is a threshold choice, so the gap can be tuned. Tuning should use AMI dev, not the
    test set.
- **DER mixes in an error users rarely see.** Missed speech at turn edges mostly does not reach the
  user, because WhisperX assigns each word to the speaker it overlaps most. Confusion does reach them.
  Day 2 adds a **word-level speaker error** metric (each Whisper word's speaker against the
  reference's), which is the metric this decision should rest on.
- **Nemotron is about 10x faster than pyannote**, at 0.003-0.005 RTF. Its 1 s streaming mode keeps
  offline accuracy at 0.068 RTF, which is directly relevant to live transcription.
- **Nemotron's dependency cost.** It needs NeMo from `main`, because 3.0.0 predates the rope attention
  the model uses. Its streaming-mode VRAM peak needs re-measuring, since 6.9 GB on AMI is an outlier.
- **sherpa-onnx is out for diarization quality.** Its confusion is 8-12%. The CAM++ and ResNet34
  embeddings collapse speakers at any threshold. TitaNet works, but only in a narrow threshold band
  (1.2 gives 6 speakers where 2 are present, 1.3 merges everyone into 2), which is too brittle to ship.
- **community-1 against 3.1:** slightly better on AMI and slightly worse on our silver set. Not decisive
  either way; the gold set will settle it.

## Day 2 results

**New metric: word-level speaker error** (`harness/words.py`). It is the share of words shown under the
wrong speaker, with word timings from the production worker's own WhisperX run on the same audio:

- **seg:** what the transcript shows. A Whisper segment takes the speaker it overlaps most, per
  `whisperx.assign_word_speakers`, and every word in it inherits that speaker.
- **word:** each word is assigned on its own.

| candidate | AMI word err seg / word | AMI DER | ours word err seg / word | RTF |
|---|---|---|---|---|
| pyannote 3.1 (production) | 8.6 / 6.4 | 17.3 | 2.5* / 6.9 | 0.03 |
| pyannote community-1 | 8.4 / **6.3** | **16.9** | 3.5* / 7.8 | 0.03 |
| Nemotron, NeMo default post-proc | 8.4 / 7.6 | 25.9 | 4.1* / 8.1 | 0.003 |
| Nemotron, **post-proc tuned on AMI dev** | **8.2** / 8.5 | 19.3 | 4.1* / 8.6 | 0.002 |

\* Our silver reference *is* production 3.1's segment labelling (with named labels merged), so the seg
column on our set mostly measures agreement with 3.1. Only the gold set can settle our audio.

Findings:

1. **On the user-visible metric the diarizer barely matters on AMI.** All four sit at 8.2-8.6% word
   error as displayed. Nemotron's large confusion lead (1.0-1.4% against 3.9-4.3%) does not survive the
   step from time to words, and pyannote is better word by word.
2. **The display rule costs more than the choice of diarizer.** Segment-level display adds about 2
   points over word-level assignment for pyannote on AMI (8.6 against 6.4). Whisper segments regularly
   span a change of speaker, and the whole segment goes to one person. Splitting segments where the
   word-level speaker changes is a cheaper and larger win than any model swap measured so far.
   **Recommend as its own change**, independent of the diarizer decision.
3. **Tuning Nemotron's post-processing on AMI dev** (a 324-setting grid, `nemotron/tune_postproc.py`):
   - On dev, DER falls from 21.6% to 16.5%.
   - On test, the tuned settings bring DER from 25.9% to 19.3%. That is still behind pyannote's 17%,
     because Nemotron's speech boundaries stay worse than pyannote's even when tuned.
   - Tuned settings: onset 0.6, offset 0.7, pad_onset 0.1, pad_offset 0.2, min_duration_off 0.3.
4. **Nemotron's 1 s streaming mode peaks at 4.0-4.3 GB**, measured cleanly on the two longest AMI
   meetings (49 and 44 min). The 6.9 GB seen on Day 1 came from files that ran alongside another GPU
   job.
5. **The gold set is built** (`harness/build_gold.py`): 10 excerpts of 5 minutes from the hardest
   recordings, where production produced 10-24 diarized labels against 0-2 named people.
   - Pre-filled label files alternate between pyannote 3.1 and Nemotron output.
   - They are waiting on human labelling (about 2 hours).

## Open questions to answer along the way

- Nemotron's real VRAM use and speed on the 4070, and whether it runs on ROCm at all.
- Whether pyannote 4's usage telemetry is on by default. The spike images set
  `PYANNOTE_METRICS_ENABLED=0`, and the production worker should probably do the same.
- The actual output dimension of the 3D-Speaker models, and of Vosk's model.
