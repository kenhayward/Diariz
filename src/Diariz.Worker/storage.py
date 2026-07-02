"""Download audio blobs from MinIO/S3 to a local temp file."""
import os
import tempfile

import boto3
from botocore.client import Config as BotoConfig

from config import config

_s3 = boto3.client(
    "s3",
    endpoint_url=config.S3_ENDPOINT,
    aws_access_key_id=config.S3_ACCESS_KEY,
    aws_secret_access_key=config.S3_SECRET_KEY,
    config=BotoConfig(signature_version="s3v4", s3={"addressing_style": "path"}),
    region_name="us-east-1",
)


def download(blob_key: str) -> str:
    """Download the object to a temp file and return the local path."""
    suffix = os.path.splitext(blob_key)[1] or ".audio"
    fd, path = tempfile.mkstemp(suffix=suffix)
    os.close(fd)
    _s3.download_file(config.S3_BUCKET, blob_key, path)
    return path


def upload(blob_key: str, local_path: str, content_type: str) -> None:
    """Upload a local file to the given object key (used for merged/concatenated audio)."""
    extra = {"ContentType": content_type} if content_type else {}
    # boto3's client.upload_file takes (Filename, Bucket, Key) — the opposite file/bucket order to
    # download_file(Bucket, Key, Filename). Getting it wrong sends the bucket name as the local file.
    _s3.upload_file(local_path, config.S3_BUCKET, blob_key, ExtraArgs=extra)
