from config import redact_url


def test_redact_url_hides_the_password():
    assert redact_url("redis://:s3cr3tvalue@redis:6379/0") == "redis://:***@redis:6379/0"


def test_redact_url_hides_the_password_but_keeps_the_user():
    assert redact_url("redis://worker:s3cr3tvalue@redis:6379/1") == "redis://worker:***@redis:6379/1"


def test_redact_url_leaves_a_url_without_credentials_alone():
    assert redact_url("redis://redis:6379/0") == "redis://redis:6379/0"


def test_redact_url_never_raises_on_garbage():
    assert "s3cr3t" not in redact_url("not a url :s3cr3t@")


def test_word_speaker_split_is_on_for_full_files_and_off_for_live_by_default():
    """Issue #803: the live switch stays off until a replay of real chunks has shown the stitcher is
    unaffected; both are env switches, so turning either one is a redeploy, not a code change."""
    from config import Config

    assert Config.SPLIT_SEGMENTS_BY_WORD_SPEAKER is True
    assert Config.SPLIT_LIVE_SEGMENTS_BY_WORD_SPEAKER is False
    assert Config.SPLIT_MIN_WORDS == 2
    assert Config.SPLIT_MIN_MS == 1000
