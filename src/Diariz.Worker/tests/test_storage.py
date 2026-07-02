"""storage.py must call boto3's client methods with the correct positional argument order.

These are easy to get wrong because upload_file and download_file take their file/bucket/key
arguments in *different* orders — a mix-up uploads the bucket name as if it were the local file
(FileNotFoundError: 'recordings'). The worker's own tests stub storage.upload wholesale, so this is
the only place the real argument wiring is checked.
"""
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
