"""storage.py must call boto3's client methods with the correct positional argument order.

These are easy to get wrong because upload_file and download_file take their file/bucket/key
arguments in *different* orders — a mix-up uploads the bucket name as if it were the local file
(FileNotFoundError: 'recordings'). The worker's own tests stub storage.upload wholesale, so this is
the only place the real argument wiring is checked.
"""
import pytest
import storage


class FakeS3:
    def __init__(self):
        self.calls = []

    def upload_file(self, *args, **kwargs):
        self.calls.append(("upload_file", args, kwargs))

    def download_file(self, *args, **kwargs):
        self.calls.append(("download_file", args, kwargs))


def test_upload_passes_boto3_args_in_order(monkeypatch):
    fake = FakeS3()
    monkeypatch.setattr(storage, "_s3", fake)

    storage.upload("u/merged.webm", "/tmp/local.webm", "audio/webm")

    name, args, kwargs = fake.calls[0]
    assert name == "upload_file"
    # boto3 client.upload_file(Filename, Bucket, Key, ExtraArgs=...)
    assert args[0] == "/tmp/local.webm"          # Filename — the local file to send
    assert args[1] == storage.config.S3_BUCKET   # Bucket
    assert args[2] == "u/merged.webm"            # Key — the destination object
    assert kwargs["ExtraArgs"] == {"ContentType": "audio/webm"}


def test_download_passes_boto3_args_in_order(monkeypatch):
    fake = FakeS3()
    monkeypatch.setattr(storage, "_s3", fake)

    path = storage.download("u/a.webm")

    name, args, kwargs = fake.calls[0]
    assert name == "download_file"
    # boto3 client.download_file(Bucket, Key, Filename)
    assert args[0] == storage.config.S3_BUCKET   # Bucket
    assert args[1] == "u/a.webm"                 # Key — the source object
    assert args[2] == path                       # Filename — the temp path returned

def test_download_of_a_deleted_blob_is_a_missing_blob(monkeypatch, tmp_path):
    """Live chunks race their own recording's finalise (issue #759).

    Finalise merges the chunk blobs into the canonical recording and deletes the individual ones, so a
    live-chunk job still queued at Stop downloads a key that no longer exists. That is an ordinary end
    of a meeting, not a transcription failure - naming it here is what lets the worker tell the two
    apart, instead of reading botocore response dictionaries at the call site.
    """
    from botocore.exceptions import ClientError

    class Gone:
        def download_file(self, *args, **kwargs):
            raise ClientError({"Error": {"Code": "404", "Message": "Not Found"},
                               "ResponseMetadata": {"HTTPStatusCode": 404}}, "HeadObject")

    monkeypatch.setattr(storage, "_s3", Gone())
    monkeypatch.setattr(storage.tempfile, "tempdir", str(tmp_path))

    with pytest.raises(storage.MissingBlob):
        storage.download("u/r/chunks/00003.webm")

    assert list(tmp_path.iterdir()) == [], "the temp file it opened must not be left behind"


def test_download_does_not_disguise_any_other_failure(monkeypatch):
    """Only a missing object is an ordinary outcome. Credentials, permissions and a broken bucket are
    faults, and swallowing them as "already merged away" would hide an outage as a quiet meeting."""
    from botocore.exceptions import ClientError

    class Denied:
        def download_file(self, *args, **kwargs):
            raise ClientError({"Error": {"Code": "AccessDenied"},
                               "ResponseMetadata": {"HTTPStatusCode": 403}}, "GetObject")

    monkeypatch.setattr(storage, "_s3", Denied())

    with pytest.raises(ClientError):
        storage.download("u/r/chunks/00003.webm")
