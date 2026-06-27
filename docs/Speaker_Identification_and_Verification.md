# Speaker Embeddings for Identification & Verification — an Open-Source Survey

**Status:** Research note (no implementation). **Audience:** Diariz maintainers.
**Date:** 2026-06-27.

## 1. Summary

Diariz today performs **speaker diarization** — it answers *"how many speakers are there, and which
segments belong to speaker A vs. speaker B?"* — using `pyannote.audio` 3.1 in the Python worker. Diarization
produces **anonymous, recording-local** labels (`SPEAKER_00`, `SPEAKER_01`); the same person gets a fresh,
unrelated label in the next recording.

This note researches the next capability: **speaker identification and verification by embedding comparison** —
recognising a *known, enrolled* person ("is this Alice?") and carrying a stable identity **across** recordings.
The enabling technology is a **speaker embedding** (a.k.a. *speaker vector* / *voiceprint*): a fixed-length
vector (typically 192–256 dimensions) that maps a voice into a space where **same-speaker** vectors are close
and **different-speaker** vectors are far apart, measured by cosine similarity. Enrolment stores a reference
vector per person; recognition compares a new vector against the references.

The short answer to *"does ECAPA-TDNN work for this?"* is **yes** — ECAPA-TDNN is the current mainstream,
well-supported choice (≈0.7–1.0% Equal Error Rate on VoxCeleb1), with Apache-2.0 tooling in SpeechBrain. There
are several equally strong alternatives (WeSpeaker ResNet/CAM++, NeMo TitaNet, 3D-Speaker ERes2Net) and a
higher-accuracy but heavier SSL option (WavLM). **The dominant decision driver is licensing**, because most
top models are trained on **VoxCeleb**, whose data is non-commercial — the same caveat that already applies to
Diariz's diarization models.

A particularly attractive fact for Diariz: **`pyannote.audio` 3.1 already extracts a speaker embedding
internally** (a WeSpeaker ResNet34 model) to do diarization. Identification can largely **reuse the vector the
pipeline already computes**, rather than adding a second model.

---

## 2. The three tasks, precisely

| Task | Question | Output | Needs enrolment? |
|---|---|---|---|
| **Diarization** | Who spoke when? | Time segments grouped by anonymous speaker | No |
| **Verification (1:1)** | Is this *claimed* person who they say? | Accept / reject | Yes — one claimed identity |
| **Identification (1:N)** | *Which* of my known people is this (if any)? | A name, or "unknown" | Yes — a gallery of N people |

Identification has two flavours:

- **Closed-set:** the speaker is guaranteed to be one of the N enrolled people → pick the nearest.
- **Open-set:** the speaker may be a stranger → pick the nearest **only if** its similarity clears a
  threshold; otherwise return **"unknown."** This is the realistic mode for a meeting tool and is materially
  harder (it needs a well-calibrated rejection threshold).

All three reduce to the same primitive: **extract an embedding, then score it** (against a centroid for
diarization clustering; against an enrolled reference for verification/identification).

---

## 3. How embedding-based recognition works

```
            enrolment (once per person)
  Alice's    ┌──────────────┐   e1,e2,e3   ┌──────────┐   ā = mean(ei)   ┌─────────────┐
  clips ───► │ VAD + window │ ───────────► │ embedder │ ───────────────► │  gallery    │
            └──────────────┘              └──────────┘                   │ Alice → ā   │
                                                                          │ Bob   → b̄  │
            recognition (per new utterance)                               └─────────────┘
  segment   ┌──────────────┐    x         ┌──────────┐   cos(x, ā), …          │
  audio ──► │ VAD + window │ ───────────► │ embedder │ ──────────────► score & threshold
            └──────────────┘              └──────────┘                  → "Alice" / "unknown"
```

1. **Pre-process:** resample to 16 kHz mono, run voice-activity detection (VAD), and take a sufficiently long
   speech window (accuracy climbs steeply up to ~10–20 s of speech; sub-2 s utterances are unreliable).
2. **Embed:** push the window through the speaker-embedding network → an L2-normalised vector.
3. **Enrol:** average several embeddings of a person into a single **centroid** (more enrolment audio → more
   robust reference). Store it.
4. **Score:** compute **cosine similarity** between the test vector and each enrolled centroid. Optionally
   apply **score normalisation** (AS-norm) and/or a **PLDA** back-end for harder, cross-domain conditions.
5. **Decide:** threshold the score. Verification: accept if `score ≥ τ`. Open-set identification: take the
   best match if `score ≥ τ`, else "unknown."

**Choosing τ** is a calibration exercise, not a guess. Standard metrics:

- **EER (Equal Error Rate):** the operating point where false-accepts = false-rejects (lower is better; the
  headline number quoted per model). Good single-number comparison, but a *deployment* picks a threshold off
  the EER point depending on whether false-accepts or false-rejects are costlier.
- **minDCF:** a cost-weighted detection metric used when the two error types have very different costs.

Thresholds **do not transfer** cleanly between models, audio conditions, or languages — each deployment should
calibrate on a small held-out set of its own enrolled speakers.

---

## 4. Open-source models & toolkits

All of the following are open source and can run on the existing GPU worker (or CPU, slower). EERs are on the
standard **VoxCeleb1-O** test for rough comparison; real-world numbers on meeting/room audio are worse.

### 4.1 ECAPA-TDNN — SpeechBrain *(the default recommendation)*
- **What:** A 1-D convolutional TDNN with squeeze-excitation, multi-scale (Res2Net) features, and **attentive
  statistics pooling**; trained with Additive-Margin (AAM) softmax; cosine scoring. Embedding dim **192**.
- **Numbers:** **≈0.69% EER** for the official `speechbrain/spkrec-ecapa-voxceleb` checkpoint (VoxCeleb1+2 training).
- **Framework/License:** SpeechBrain, **Apache-2.0** code and model weights; trivial `EncoderClassifier`/
  `SpeakerRecognition` API for embed + verify. PyTorch.
- **Why pick it:** the best-documented, easiest-to-adopt, widely-cited baseline; permissive code license;
  small (≈20 MB). The mainstream "just works" choice. (Caveat: VoxCeleb data lineage — see §6.)

### 4.2 WeSpeaker — ResNet34 / ResNet293 / CAM++ *(production-oriented; already in Diariz's stack)*
- **What:** A research-**and-production** toolkit (wenet-e2e) with ResNet-based "r-vectors," CAM++, and
  others; strong challenge-winning recipes; **JIT/ONNX/TensorRT export** for Triton serving.
- **Relevance to Diariz:** `pyannote.audio` 3.1's diarization pipeline uses the WeSpeaker model
  **`pyannote/wespeaker-voxceleb-resnet34-LM`** to embed speakers. So Diariz **already downloads and runs this
  embedder.** Reusing it for identification avoids a second model and keeps one vector space.
- **Numbers:** ResNet-based WeSpeaker models reach **≈0.7% EER** (larger ResNet293 lower still).
- **License:** the `pyannote/wespeaker-voxceleb-resnet34-LM` weights are published as **CC-BY-4.0**
  (commercial use permitted *with attribution*, per the model card — note the VoxCeleb nuance in §6).

### 4.3 NVIDIA NeMo — TitaNet-Large
- **What:** Depth-wise separable 1-D conv network, ~23 M params; embeddings for both verification and
  diarization. Embedding dim **192**.
- **Numbers:** **≈0.66% EER** on VoxCeleb1-O. Trained on VoxCeleb 1/2 + Fisher + Switchboard + LibriSpeech
  (telephone + wideband), which can help channel robustness.
- **License:** model under **CC-BY-4.0**; NeMo framework Apache-2.0. Heavier dependency stack (NeMo).

### 4.4 3D-Speaker — CAM++ / ERes2Net / ERes2NetV2 *(best commercial-licensing story)*
- **What:** Alibaba DAMO toolkit (`modelscope/3D-Speaker`), **Apache-2.0**. CAM++ (fast, context-aware
  masking), ERes2Net and **ERes2NetV2** (their strongest). ONNX exports available.
- **Distinctive:** several checkpoints are trained on a **200k-speaker in-house corpus** (not VoxCeleb),
  which sidesteps the VoxCeleb non-commercial problem — though the largest models are Mandarin-leaning, so
  **cross-lingual generalisation should be validated** on your audio.
- **Numbers:** ERes2NetV2 is their top performer; CAM++ is competitive at much lower compute.

### 4.5 WavLM (SSL) — highest accuracy, heaviest
- **What:** Microsoft self-supervised speech model; fine-tuned with an x-vector head + AAM-softmax for SV.
- **Numbers:** **≈0.38% EER** on VoxCeleb1 — effectively state-of-the-art.
- **Trade-off:** large (~300 M+ params for WavLM-Large) → more GPU memory and latency. WavLM weights are
  **MIT-licensed** by Microsoft, but the **SV fine-tuning data is again VoxCeleb** (see §6). Worth it only if
  the accuracy ceiling matters more than footprint.

### 4.6 Resemblyzer / GE2E d-vectors — lightweight legacy
- **What:** An LSTM d-vector encoder (Generalized End-to-End loss), `Resemblyzer`, **MIT**. Embedding dim 256.
- **Numbers:** materially worse than ECAPA/ResNet (several-percent EER); fast and dependency-light.
- **When:** only if you need a tiny CPU-friendly model and can tolerate lower accuracy. Not recommended when a
  GPU worker is already present.

### 4.7 Comparison

| Model / toolkit | VoxCeleb1-O EER | Dim | Framework | Code license | Weights license | Notes |
|---|---|---|---|---|---|---|
| **ECAPA-TDNN** (SpeechBrain) | ~0.69% | 192 | PyTorch/SpeechBrain | Apache-2.0 | Apache-2.0* | Easiest, best-documented default |
| **WeSpeaker ResNet34-LM** (via pyannote) | ~0.7% | 256 | PyTorch | Apache-2.0 | CC-BY-4.0* | **Already in Diariz's pyannote pipeline** |
| **TitaNet-Large** (NeMo) | ~0.66% | 192 | NeMo | Apache-2.0 | CC-BY-4.0* | Multi-corpus (telephone+wideband) |
| **ERes2NetV2 / CAM++** (3D-Speaker) | ~0.6–0.9% | 192/512 | PyTorch | Apache-2.0 | per-model* | 200k non-VoxCeleb checkpoints exist; Mandarin-leaning |
| **WavLM-Large + SV head** | ~0.38% | 512 | HF Transformers | MIT | MIT (SV data: VoxCeleb)* | SOTA accuracy, heavy |
| **Resemblyzer (GE2E)** | several % | 256 | PyTorch | MIT | MIT | Legacy, lightweight, lower accuracy |

\* See §6 — the **VoxCeleb training-data licence** is the real constraint, separate from the code/weights licence.

---

## 5. Scoring back-ends & calibration

- **Cosine similarity** on length-normalised embeddings is the standard, model-native scorer (ECAPA, TitaNet,
  WeSpeaker, 3D-Speaker all train for it). Simple and strong.
- **PLDA** (Probabilistic Linear Discriminant Analysis) can outperform cosine under heavy domain mismatch but
  needs in-domain data to train; usually unnecessary for modern AAM-softmax embeddings.
- **AS-norm (Adaptive Score Normalisation)** stabilises thresholds across conditions using a cohort of
  background speakers; cheap and worthwhile when enrolling across varied audio.
- **Quality gating:** reject enrolment/probe windows that are too short or too noisy *before* scoring;
  garbage-in dominates error.
- **Multiple enrolment utterances** (3–5+, from different sessions if possible) markedly improve robustness vs.
  a single clip.

---

## 6. Licensing, privacy & ethics (read before building)

**Licensing has three independent layers — judge all three:**

1. **Framework/code licence** — is the toolkit OSI-permissive? SpeechBrain, WeSpeaker, NeMo and 3D-Speaker
   are all **Apache-2.0** code.
2. **Model-weights licence** — *this is where "true open source" actually differs.* SpeechBrain's
   `spkrec-ecapa-voxceleb` weights are **Apache-2.0** (a real OSI software licence). NeMo TitaNet and the
   pyannote/WeSpeaker model publish weights under **CC-BY-4.0** — permissive and commercial-friendly *with
   attribution*, but a Creative-Commons *content* licence rather than an OSI one.
3. **Training-data licence** — the deepest layer, and the genuine blocker (see below).

> **NeMo vs SpeechBrain (the question at hand):** both toolkits are Apache-2.0 code, but **SpeechBrain's ECAPA
> weights are Apache-2.0 whereas NeMo's TitaNet weights are CC-BY-4.0**, and **NeMo is a much heavier,
> NVIDIA-ecosystem dependency**. For a "prefer true open source, keep it light" goal, **SpeechBrain ECAPA-TDNN
> is the cleaner choice** at effectively the same accuracy (~0.69% vs ~0.66% EER). Neither escapes the VoxCeleb
> *data* caveat below.

- **VoxCeleb data is non-commercial.** Almost every top open model is trained (or SV-fine-tuned) on VoxCeleb,
  which is licensed for **academic / non-commercial use only** (it is sourced from YouTube). Several
  derivations state that VoxCeleb-trained models inherit **CC-BY-NC-SA 4.0** (no commercial use). Confusingly,
  some model cards (e.g. `pyannote/wespeaker-voxceleb-resnet34-LM`, NeMo TitaNet) publish weights under
  **CC-BY-4.0** (commercial use *with attribution*). **This is a genuine grey area.** For Diariz this mirrors,
  and refines, the disclaimer already shown in the About box about the pyannote diarization models: treat
  VoxCeleb-derived speaker models as **non-commercial unless a specific model card grants otherwise**, and
  re-read each card before any commercial deployment.
- **Commercial-safe path:** prefer checkpoints trained on **non-VoxCeleb** corpora (e.g. 3D-Speaker's
  200k-speaker models) — validating cross-lingual accuracy — or fine-tune on a permissively-licensed/owned
  dataset.
- **Voiceprints are biometric data.** An enrolled speaker embedding is biometric personal data under GDPR and
  laws like Illinois BIPA. Implications: obtain **explicit consent** to enrol; allow **deletion** of voiceprints
  (right to erasure); store them **encrypted and per-user**; document purpose and retention. This is a policy
  decision, not just an engineering one.
- **Failure modes & fairness:** accuracy degrades on short/noisy/cross-channel audio and can vary across
  demographics; always keep an **"unknown"** outcome and surface confidence rather than asserting identity.
- **Anti-spoofing** (replay/synthetic-voice detection) is a separate problem; embedding similarity alone does
  not prove liveness.

---

## 7. How this would fit Diariz (architecture sketch, not a commitment)

Diariz's current shape makes this a natural extension:

- **Worker** (`src/Diariz.Worker`) already runs `pyannote.audio` 3.1 on the GPU and therefore already loads a
  WeSpeaker ResNet34 **embedder**. The diarization pipeline computes a per-speaker embedding to cluster
  segments — the same vectors needed for identification.
- **pgvector** is already in the stack (`Segment.Embedding` is a `vector(768)` column, currently unused and
  sized for a *text* embedder). Speaker embeddings are 192–256-dim, so they'd want their **own** column/table
  sized accordingly — cosine-distance KNN in Postgres is exactly what pgvector is for.

A plausible flow (future work):

```
  Enrolment:  user records / labels a few clips of a known person
              → worker extracts speaker embedding(s) → centroid
              → store per-user "SpeakerProfile { name, embedding(vector) }" in Postgres (pgvector)

  Recognition (during the existing transcribe→diarize job):
              for each diarized speaker cluster in the recording:
                centroid_x = mean(cluster embeddings)        # reuse pyannote's vectors
                match = nearest SpeakerProfile by cosine, for THIS user only
                if cos(match) ≥ τ:  label cluster = match.name      # e.g. SPEAKER_00 → "Alice"
                else:               label cluster = "unknown"
              → seed/merge into the existing Speaker rows (DisplayName), preserving manual renames
```

Key design points specific to Diariz:
- **Per-user galleries** — enrolled voiceprints are scoped to the owning user, exactly like every other
  resource (the `UserId`-ownership rule).
- **Reuse the diarizer's embedding** to avoid a second model and a second vector space; only add a dedicated SV
  model (ECAPA / 3D-Speaker) if its accuracy or licensing is preferable.
- **Calibrate τ** on the user's own enrolled speakers; keep an **"unknown"** label and never overwrite a manual
  speaker rename.
- **Biometric-data handling** — enrolment consent + a delete-my-voiceprint path, per §6.

---

## 8. Recommendation

1. **Preferred for "true open source" + light footprint:** **SpeechBrain ECAPA-TDNN**
   (`spkrec-ecapa-voxceleb`) — **Apache-2.0 code *and* weights**, ~0.69% EER, a trivial embed/verify API, and a
   lean PyTorch dependency. Pick this over **NeMo TitaNet**, which is comparable in accuracy but ships
   CC-BY-4.0 weights and a much heavier NVIDIA-ecosystem stack.
2. **Zero-new-dependency alternative:** **reuse the WeSpeaker ResNet34 embedding pyannote already produces**
   (`pyannote/wespeaker-voxceleb-resnet34-LM`, CC-BY-4.0) for enrolment + cosine matching — one model, one
   vector space, nothing new to install. Choose this if minimising moving parts matters more than an
   Apache-2.0 weights licence.
3. **If/when commercial use is on the table:** move to a **non-VoxCeleb-trained** model — **3D-Speaker
   CAM++/ERes2NetV2** (Apache-2.0 toolkit, 200k-speaker non-VoxCeleb checkpoints) — and validate accuracy on
   representative English meeting audio; or fine-tune on owned data. (Neither SpeechBrain nor NeMo escapes the
   VoxCeleb *data* caveat.)
4. **If maximum accuracy is the goal and GPU budget allows:** evaluate **WavLM-Large + SV head** (~0.38% EER),
   accepting the larger footprint and the VoxCeleb fine-tuning caveat.
5. **Regardless of model:** add AS-norm, calibrate the open-set threshold per user, require multi-clip
   enrolment, keep an "unknown" outcome, and treat voiceprints as consented, deletable biometric data.

---

## Sources

- SpeechBrain ECAPA-TDNN model card (0.69% EER, Apache-2.0): [speechbrain/spkrec-ecapa-voxceleb](https://huggingface.co/speechbrain/spkrec-ecapa-voxceleb)
- WeSpeaker toolkit (research+production, ONNX/Triton): [Wespeaker paper (arXiv:2210.17016)](https://arxiv.org/pdf/2210.17016) · [wenet-e2e/wespeaker pretrained models](https://github.com/wenet-e2e/wespeaker/blob/master/docs/pretrained.md)
- pyannote WeSpeaker embedding wrapper (CC-BY-4.0): [pyannote/wespeaker-voxceleb-resnet34-LM](https://huggingface.co/pyannote/wespeaker-voxceleb-resnet34-LM) · [pyannote/embedding](https://huggingface.co/pyannote/embedding)
- NVIDIA NeMo TitaNet-Large (0.66% EER, CC-BY-4.0): [nvidia/speakerverification_en_titanet_large](https://huggingface.co/nvidia/speakerverification_en_titanet_large)
- 3D-Speaker toolkit + CAM++/ERes2Net (Apache-2.0, 200k speakers): [modelscope/3D-Speaker](https://github.com/modelscope/3D-Speaker) · [3D-Speaker-Toolkit paper (arXiv:2403.19971)](https://arxiv.org/html/2403.19971v3) · [CAM++ paper (arXiv:2303.00332)](https://arxiv.org/pdf/2303.00332)
- WavLM SV (0.38% EER, SSL): [WavLM paper (arXiv:2110.13900)](https://arxiv.org/pdf/2110.13900) · [microsoft/wavlm-base-sv](https://huggingface.co/microsoft/wavlm-base-sv)
- VoxCeleb dataset (non-commercial) + model licence lineage: [VoxCeleb2 (Oxford VGG)](https://www.robots.ox.ac.uk/~vgg/data/voxceleb/vox2.html) · [VoxCeleb overview](https://www.emergentmind.com/topics/voxceleb-dataset)
- ECAPA vs x-vector / Resemblyzer comparison: [Comparison of Modern Deep Learning Models for Speaker Verification (MDPI)](https://www.mdpi.com/2076-3417/14/4/1329) · [ECAPA-TDNN & x-vector representations (arXiv:2506.20190)](https://arxiv.org/html/2506.20190)
