"""Tests for the job orchestration + temp-file cleanup in worker.handle()."""
import os

import worker


def _job(transcription_id="tid-1"):
    return {"TranscriptionId": transcription_id, "BlobKey": "user/blob.wav", "Model": "whisperx-large-v3"}


def test_handle_success_posts_result_and_removes_temp_file(monkeypatch, tmp_path):
    audio = tmp_path / "audio.wav"
    audio.write_text("fake")
    monkeypatch.setattr(worker.storage, "download", lambda key: str(audio))
    monkeypatch.setattr(worker.pipeline, "transcribe",
                        lambda path: {"language": "en", "segments": [{"Text": "hi"}]})

    posted = {}
    monkeypatch.setattr(worker.callback, "post_result",
                        lambda tid, lang, segs: posted.update(tid=tid, lang=lang, segs=segs))
    monkeypatch.setattr(worker.callback, "post_failure",
                        lambda *a, **k: posted.update(failed=True))

    worker.handle(_job("tid-1"))

    assert posted["tid"] == "tid-1"
    assert posted["lang"] == "en"
    assert "failed" not in posted
    assert not os.path.exists(str(audio))  # temp file cleaned up


def test_handle_transcribe_failure_reports_failure_and_cleans_temp(monkeypatch, tmp_path):
    audio = tmp_path / "audio.wav"
    audio.write_text("fake")
    monkeypatch.setattr(worker.storage, "download", lambda key: str(audio))

    def boom(path):
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
