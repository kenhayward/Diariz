"""Tests for the job orchestration + temp-file cleanup in worker.handle()."""
import json
import os

import redis

import worker


def _job(transcription_id="tid-1", **extra):
    return {"TranscriptionId": transcription_id, "BlobKey": "user/blob.wav", "Model": "whisperx-large-v3", **extra}


def test_handle_forwards_speaker_hints_to_transcribe(monkeypatch, tmp_path):
    audio = tmp_path / "audio.wav"
    audio.write_text("fake")
    monkeypatch.setattr(worker.storage, "download", lambda key: str(audio))

    captured = {}

    def fake_transcribe(path, min_s=None, max_s=None):
        captured.update(min=min_s, max=max_s)
        return {"language": "en", "segments": [], "speakers": []}

    monkeypatch.setattr(worker.pipeline, "transcribe", fake_transcribe)
    monkeypatch.setattr(worker.callback, "post_result", lambda *a, **k: None)

    worker.handle(_job("tid-h", MinSpeakers=2, MaxSpeakers=None))

    assert captured["min"] == 2
    assert captured["max"] is None


def test_handle_success_posts_result_and_removes_temp_file(monkeypatch, tmp_path):
    audio = tmp_path / "audio.wav"
    audio.write_text("fake")
    monkeypatch.setattr(worker.storage, "download", lambda key: str(audio))
    monkeypatch.setattr(worker.pipeline, "transcribe",
                        lambda path, min_s=None, max_s=None: {"language": "en", "segments": [{"Text": "hi"}],
                                      "speakers": [{"Speaker": "SPEAKER_00", "Embedding": [0.1]}]})

    posted = {}
    monkeypatch.setattr(worker.callback, "post_result",
                        lambda tid, lang, segs, speakers=None, duration_ms=None, processing_ms=None: posted.update(
                            tid=tid, lang=lang, segs=segs, speakers=speakers,
                            duration_ms=duration_ms, processing_ms=processing_ms))
    monkeypatch.setattr(worker.callback, "post_failure",
                        lambda *a, **k: posted.update(failed=True))

    worker.handle(_job("tid-1"))

    assert posted["tid"] == "tid-1"
    assert posted["lang"] == "en"
    assert posted["speakers"] == [{"Speaker": "SPEAKER_00", "Embedding": [0.1]}]
    assert isinstance(posted["processing_ms"], int) and posted["processing_ms"] >= 0  # wall-clock measured
    assert "failed" not in posted
    assert not os.path.exists(str(audio))  # temp file cleaned up


def test_handle_transcribe_failure_reports_failure_and_cleans_temp(monkeypatch, tmp_path):
    audio = tmp_path / "audio.wav"
    audio.write_text("fake")
    monkeypatch.setattr(worker.storage, "download", lambda key: str(audio))

    def boom(path, min_s=None, max_s=None):
        raise RuntimeError("model exploded")

    monkeypatch.setattr(worker.pipeline, "transcribe", boom)

    outcome = {}
    monkeypatch.setattr(worker.callback, "post_result", lambda *a, **k: outcome.update(result=True))
    monkeypatch.setattr(worker.callback, "post_failure", lambda tid, err: outcome.update(tid=tid, err=err))

    worker.handle(_job("tid-2"))

    assert outcome["tid"] == "tid-2"
    assert "model exploded" in outcome["err"]
    assert "result" not in outcome
    assert not os.path.exists(str(audio))


def test_handle_download_failure_reports_failure_without_crashing(monkeypatch):
    def boom(key):
        raise IOError("blob missing")

    monkeypatch.setattr(worker.storage, "download", boom)

    outcome = {}
    monkeypatch.setattr(worker.callback, "post_result", lambda *a, **k: outcome.update(result=True))
    monkeypatch.setattr(worker.callback, "post_failure", lambda tid, err: outcome.update(tid=tid, err=err))

    worker.handle(_job("tid-3"))

    assert outcome["tid"] == "tid-3"
    assert "result" not in outcome


# ---- Audio-merge orchestration ----

def _merge_job(**extra):
    return {
        "RecordingId": "rec-1",
        "BlobKeys": ["u/a.webm", "u/b.webm"],
        "OutputKey": "u/merged.webm",
        "DeleteRecordingIds": ["rec-2"],
        **extra,
    }


def test_handle_merge_concatenates_uploads_and_reports(monkeypatch, tmp_path):
    downloaded = []
    a = tmp_path / "a.webm"; a.write_text("a")
    b = tmp_path / "b.webm"; b.write_text("b")
    out = tmp_path / "merged.webm"; out.write_text("ab")

    def fake_download(key):
        downloaded.append(key)
        return str(a if key.endswith("a.webm") else b)

    monkeypatch.setattr(worker.storage, "download", fake_download)
    monkeypatch.setattr(worker.audio_merge, "concat", lambda paths: (str(out), 5000, 1234))
    uploaded = {}
    monkeypatch.setattr(worker.storage, "upload",
                        lambda key, path, ct: uploaded.update(key=key, ct=ct))
    posted = {}
    monkeypatch.setattr(worker.callback, "post_merge_result",
                        lambda rid, key, ct, size, dur, dels: posted.update(
                            rid=rid, key=key, size=size, dur=dur, dels=dels))
    monkeypatch.setattr(worker.callback, "post_merge_failure", lambda *a, **k: posted.update(failed=True))

    worker.handle_merge(_merge_job())

    assert downloaded == ["u/a.webm", "u/b.webm"]  # downloaded in order
    assert uploaded["key"] == "u/merged.webm"
    assert posted == {"rid": "rec-1", "key": "u/merged.webm", "size": 1234, "dur": 5000, "dels": ["rec-2"]}
    assert not os.path.exists(str(out))  # temp output cleaned up


def test_handle_merge_failure_reports_and_cleans_up(monkeypatch, tmp_path):
    a = tmp_path / "a.webm"; a.write_text("a")
    monkeypatch.setattr(worker.storage, "download", lambda key: str(a))

    def boom(paths):
        raise RuntimeError("ffmpeg missing")

    monkeypatch.setattr(worker.audio_merge, "concat", boom)
    outcome = {}
    monkeypatch.setattr(worker.callback, "post_merge_result", lambda *a, **k: outcome.update(result=True))
    monkeypatch.setattr(worker.callback, "post_merge_failure", lambda rid, err: outcome.update(rid=rid, err=err))

    worker.handle_merge(_merge_job())

    assert outcome["rid"] == "rec-1"
    assert "ffmpeg missing" in outcome["err"]
    assert "result" not in outcome
    assert not os.path.exists(str(a))  # downloaded sources cleaned up


# ---- Consume loop (dispatch + resilience) ----

class _FakeRedis:
    """Minimal Redis stand-in: `responses` is a list of XREADGROUP return values, yielded per poll."""
    def __init__(self, responses):
        self._responses = list(responses)
        self.acked = []

    def xreadgroup(self, group, consumer, streams, count, block):
        return self._responses.pop(0) if self._responses else []

    def xack(self, stream, group, msg_id):
        self.acked.append((stream, msg_id))


def _keep_going(n):
    """Return a keep_going() callable that is True n times, then False (bounds the test loop)."""
    seq = iter([True] * n + [False])
    return lambda: next(seq)


def test_run_loop_dispatches_transcription_job_and_acks(monkeypatch):
    handled = []
    monkeypatch.setattr(worker, "handle", lambda job: handled.append(job))
    msg = [(worker.config.STREAM_KEY,
            [("5-0", {"job": json.dumps({"TranscriptionId": "t1", "BlobKey": "b"})})])]
    r = _FakeRedis([msg])

    worker.run_loop(r, keep_going=_keep_going(2))

    assert handled == [{"TranscriptionId": "t1", "BlobKey": "b"}]
    assert r.acked == [(worker.config.STREAM_KEY, "5-0")]


def test_run_loop_routes_merge_jobs_to_handle_merge(monkeypatch):
    merged = []
    monkeypatch.setattr(worker, "handle_merge", lambda job: merged.append(job))
    msg = [(worker.config.MERGE_STREAM_KEY,
            [("9-0", {"job": json.dumps({"RecordingId": "r1"})})])]
    r = _FakeRedis([msg])

    worker.run_loop(r, keep_going=_keep_going(2))

    assert merged == [{"RecordingId": "r1"}]
    assert r.acked == [(worker.config.MERGE_STREAM_KEY, "9-0")]


def test_run_loop_survives_redis_timeout_and_connection_errors(monkeypatch):
    """A socket read timeout or dropped connection must be retried, not crash the worker."""
    monkeypatch.setattr(worker.time, "sleep", lambda *_: None)  # don't actually back off in the test
    errors = iter([redis.TimeoutError("timed out"), redis.ConnectionError("reset")])

    class Flaky:
        def __init__(self):
            self.polls = 0

        def xreadgroup(self, *a, **k):
            self.polls += 1
            raise next(errors)

    r = Flaky()
    worker.run_loop(r, keep_going=_keep_going(2))  # two flaky polls, then stop — must not raise

    assert r.polls == 2
