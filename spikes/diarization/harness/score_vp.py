"""Score speaker-identification embeddings (/data/vp/emb/<model>.json) the way production decides.

  python score_vp.py

Production keeps one centroid per person (the mean of their samples, L2-normalised) and compares a new
speaker's embedding by cosine distance, then decides accept / suggest / ignore with the PlatformSettings
bands (IdentificationRules.Decide): accept when the best distance is <= threshold AND beats the runner-up
by the margin, suggest when <= the confirm band, else ignore.

Closed set (people in 2+ recordings): every clip is a probe, against centroids built from that person's
*other* recordings and everyone else's clips - leave-one-recording-out, so a probe never matches itself.
Open set (people in 1 recording): their clips are impostor probes against all enrolled centroids; any
accept is a false identification of someone who is not enrolled.

The production bands were set for ECAPA. Other models' distances live on different scales, so besides
"at production bands" each model also gets bands calibrated to the same impostor behaviour as ECAPA:
its threshold is the distance at which it would falsely accept as often as ECAPA does at 0.30.
"""
from __future__ import annotations

import json
from collections import defaultdict
from pathlib import Path

import numpy as np

THRESHOLD, CONFIRM, MARGIN = 0.30, 0.40, 0.05  # PlatformSettings on the backup


def norm(v):
    v = np.asarray(v, dtype=np.float64)
    return v / (np.linalg.norm(v) or 1)


def centroids(emb, exclude_rec=None):
    by = defaultdict(list)
    for key, v in emb.items():
        p, r = key.split("/")
        if r != exclude_rec:
            by[p].append(v)
    return {p: norm(np.mean(vs, axis=0)) for p, vs in by.items()}


def decide(dists: dict, threshold, confirm, margin):
    ranked = sorted(dists.items(), key=lambda kv: kv[1])
    best, d = ranked[0]
    runner = ranked[1][1] if len(ranked) > 1 else 2.0
    if d <= threshold and runner - d >= margin:
        return "accept", best
    if d <= confirm:
        return "suggest", best
    return "ignore", best


def eer(genuine, impostor):
    thr = np.sort(np.concatenate([genuine, impostor]))
    best = (1.0, 0)
    for t in thr:
        frr = np.mean(genuine > t)
        far = np.mean(impostor <= t)
        if abs(frr - far) < best[0]:
            best = (abs(frr - far), (frr + far) / 2)
    return best[1]


def evaluate(emb, threshold, confirm, margin):
    emb = {k: norm(v) for k, v in emb.items()}
    recs_of = defaultdict(set)
    for k in emb:
        p, r = k.split("/")
        recs_of[p].add(r)
    multi = {p for p, rs in recs_of.items() if len(rs) >= 2}
    genuine, impostor, top1, decisions = [], [], [], defaultdict(int)
    for key, v in emb.items():
        p, r = key.split("/")
        if p not in multi:
            continue
        cents = centroids(emb, exclude_rec=r)
        dists = {q: 1 - float(v @ c) for q, c in cents.items()}
        genuine.append(dists[p])
        impostor += [d for q, d in dists.items() if q != p]
        top1.append(min(dists, key=dists.get) == p)
        verdict, who = decide(dists, threshold, confirm, margin)
        decisions[f"{verdict}_{'right' if who == p else 'wrong'}" if verdict != "ignore" else "ignore"] += 1
    # Open set: people with one recording, against the enrolled (multi-recording) centroids only.
    enrolled = {q: c for q, c in centroids({k: v for k, v in emb.items() if k.split('/')[0] in multi}).items()}
    open_accept = open_suggest = n_open = 0
    for key, v in emb.items():
        if key.split("/")[0] in multi:
            continue
        n_open += 1
        verdict, _ = decide({q: 1 - float(v @ c) for q, c in enrolled.items()}, threshold, confirm, margin)
        open_accept += verdict == "accept"
        open_suggest += verdict == "suggest"
    return {
        "probes": len(top1), "top1": float(np.mean(top1)), "eer": eer(np.array(genuine), np.array(impostor)),
        "genuine_median": float(np.median(genuine)), "impostor_p01": float(np.percentile(impostor, 1)),
        "decisions": dict(decisions), "open_probes": n_open,
        "open_false_accept": open_accept, "open_suggest": open_suggest,
        "_impostor": np.array(impostor),
    }


def main():
    root = Path("/data/vp/emb")
    models = {f.stem: json.loads(f.read_text()) for f in sorted(root.glob("*.json"))}
    if "ecapa" not in models:
        raise SystemExit("ecapa.json missing - run embed_worker.py first; it anchors the calibrated bands")
    ecapa = evaluate(models["ecapa"], THRESHOLD, CONFIRM, MARGIN)
    target_far = float(np.mean(ecapa["_impostor"] <= THRESHOLD))

    lines = ["# Identification - leave-one-recording-out", "",
             f"Probes: {ecapa['probes']} clips of people in 2+ recordings; open set: {ecapa['open_probes']} clips "
             f"of people in one recording. Production bands: accept <= {THRESHOLD} (margin {MARGIN}), "
             f"suggest <= {CONFIRM}. Calibrated bands match ECAPA's impostor accept rate "
             f"({100 * target_far:.2f}% of impostor comparisons at <= {THRESHOLD}).", "",
             "| model | dim | top-1 % | EER % | genuine median | bands | accept right | accept WRONG | suggest "
             "| ignore | open-set false accept |",
             "|---|---|---|---|---|---|---|---|---|---|---|"]
    for name, emb in models.items():
        dim = len(next(iter(emb.values())))
        base = evaluate(emb, THRESHOLD, CONFIRM, MARGIN)
        variants = [("production", base)]
        if name != "ecapa":
            thr = float(np.quantile(base["_impostor"], target_far)) if target_far > 0 else THRESHOLD
            scale = thr / THRESHOLD
            variants.append((f"calibrated ({thr:.2f})", evaluate(emb, thr, CONFIRM * scale, MARGIN * scale)))
        for label, r in variants:
            d = r["decisions"]
            sug = d.get("suggest_right", 0) + d.get("suggest_wrong", 0)
            lines.append(
                f"| {name} | {dim} | {100 * r['top1']:.1f} | {100 * r['eer']:.1f} | {r['genuine_median']:.2f} | {label} "
                f"| {d.get('accept_right', 0)} | {d.get('accept_wrong', 0)} | {sug} | {d.get('ignore', 0)} "
                f"| {r['open_false_accept']}/{r['open_probes']} |")
    report = "\n".join(lines) + "\n"
    (Path("/data/vp") / "report.md").write_text(report)
    print(report)


if __name__ == "__main__":
    main()
