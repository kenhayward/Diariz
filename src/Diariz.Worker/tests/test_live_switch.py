"""The platform's live-transcription switch as the worker reads it: one Redis key the API writes."""
import redis

import live_switch
from config import config


class _Redis:
    def __init__(self, value=None, fail=False):
        self.value, self.fail = value, fail

    def get(self, key):
        if self.fail:
            raise redis.ConnectionError("down")
        assert key == config.LIVE_TRANSCRIPTION_FLAG_KEY
        return self.value


def test_on_when_the_key_has_never_been_written():
    # A fresh deployment, or a Redis that lost its data: the API still refuses chunks on its own
    # authority, so the worker's default only decides whether to drop what is already queued.
    assert live_switch.is_enabled(_Redis(None)) is True


def test_follows_the_value_the_api_wrote():
    assert live_switch.is_enabled(_Redis("1")) is True
    assert live_switch.is_enabled(_Redis("0")) is False


def test_an_unreadable_flag_counts_as_on():
    assert live_switch.is_enabled(_Redis(fail=True)) is True
