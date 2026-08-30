# transcription-bench

Measures how long the transcription pipeline takes on short clips, so a chunk size for live
transcription can be chosen from data rather than guessed.

The question these answer: **as a clip gets shorter, what does the per-job cost converge to?**
That fixed floor decides whether live chunked transcription can keep up with a meeting - a 30 s
chunk costing 45 s of wall clock falls behind without bound for the whole meeting.

Results, interpretation and the design they informed:
[`docs/Streaming_Capture_and_Live_Transcript.md`](../../docs/Streaming_Capture_and_Live_Transcript.md)
sections 3 and 16.

Nothing here is part of a deployable. It is developer tooling, run by hand.

## The two harnesses

|  | `chunk_floor.py` | `api_floor.py` |
|---|---|---|
| Runs | inside the worker container | anywhere, against a REST endpoint |
| Needs | Docker + the GPU + the image | a `dz_api_` token |
| Reports | **per stage** - decode, ASR, align, diarize, shape, embeddings | total `ProcessingMs` + queue wait |
| Touches the instance | no - reads the models and nothing else | yes - creates and deletes recordings |

Prefer `chunk_floor.py`. The per-stage breakdown is what tells you *why* a chunk costs what it
does (diarization is 75-80% of it), and it has no side effects. Use `api_floor.py` when you have
no shell on the box, or to confirm the queue behaves end to end.

## Generating the test audio

Synthetic, so benchmark material is never production audio.

```powershell
.\make-audio.ps1        # Windows TTS -> base.wav (two voices, ~290 s, invented content)
python slice.py         # -> clips/clip-005s.wav ... clip-240s.wav
```

`make-audio.ps1` needs two English Windows TTS voices (Hazel and Zira by default). On a machine
without them, edit the `$turns` voice names, or supply any other two-speaker WAV as
`slice.py --src`.

**One property of this ladder matters when reading results.** The opening turns are long, so
clips shorter than about 20 s contain a *single* speaker and pyannote's clustering has almost
nothing to do. They are therefore far cheaper than a real chunk would be. The step at 20 s is
the second voice arriving, not a duration threshold. Size a chunk against the 20 s row, never
the 15 s one.

## Running it

Inside the worker image, per stage:

```powershell
.\run-local.ps1 -Build                       # first time: builds diariz-worker:bench
.\run-local.ps1 -Repeats 3 -JsonOut local.json
```

Against a remote instance, end to end:

```bash
export DIARIZ_TOKEN=dz_api_...               # or pass --token
python api_floor.py --base http://host:8080 --clips ./clips --repeats 3
```

Check that the fast box really diarized rather than silently skipping the expensive stage:

```bash
python speakers.py --base http://host:8080   # while the recordings still exist
```

Check the synthetic fit against real audio, which has overlap, noise and more speakers:

```bash
python validate.py --base http://host:8080 --floor 1.26 --slope 0.029
```

Remove anything an interrupted run left behind:

```bash
python cleanup.py --base http://host:8080            # dry run
python cleanup.py --base http://host:8080 --delete
```

## Things that will catch you out

**`chunk_floor.py` must apply the worker's shims before importing `pipeline`.** It calls
`rocm_env.clean_gfx_override()` and `torch_compat.restore_legacy_torch_load()` first, because
`worker.py` does and a script that imports `pipeline` directly does not. Without them the
pyannote VAD checkpoint fails to load on torch >= 2.6 with *"Weights only load failed ...
Unsupported global"*, and an empty `HSA_OVERRIDE_GFX_VERSION` silently drops a ROCm box to CPU.

**`api_floor.py` costs LLM calls.** Each upload also triggers the owner's summary, actions and
tags jobs. A full ladder at three repeats is 33 uploads. Use `--only 20,30,45` to shorten it, or
prefer `chunk_floor.py`.

**Cleanup matches on `Recording.Title`, never `Name`.** The summariser overwrites `Name` with a
generated title - it will invent a plausible meeting name even for synthetic audio - so a
name-based filter stops matching the moment summarisation runs. `Title` is the auto descriptor
and stays `floor-bench <n>s`.

**A negative "fixed floor" means the fit is invalid.** Both harnesses fit `total = floor +
slope * seconds`. That holds only when the curve has no regime change. A GPU that runs out of
memory partway up the ladder produces a step, and one line across a step yields a negative
intercept. Read such a ladder row by row. Prefer a measured row over the fit generally - the fit
is least accurate mid-ladder, which is exactly where the live chunk sizes are.

**`validate.py` is the only tool here that reads production data.** It reports counts, ratios
and percentiles only. Keep it that way: this repo is public, and the people in those recordings
did not consent to appearing in it.

## Generated files

`base.wav`, `clips/`, `clips-*/`, and any `*.json` / `*.log` output are git-ignored.
