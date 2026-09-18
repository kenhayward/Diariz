from config import redact_url


def test_redact_url_hides_the_password():
    assert redact_url("redis://:s3cr3tvalue@redis:6379/0") == "redis://:***@redis:6379/0"


def test_redact_url_hides_the_password_but_keeps_the_user():
    assert redact_url("redis://worker:s3cr3tvalue@redis:6379/1") == "redis://worker:***@redis:6379/1"


def test_redact_url_leaves_a_url_without_credentials_alone():
    assert redact_url("redis://redis:6379/0") == "redis://redis:6379/0"


def test_redact_url_never_raises_on_garbage():
    assert "s3cr3t" not in redact_url("not a url :s3cr3t@")
