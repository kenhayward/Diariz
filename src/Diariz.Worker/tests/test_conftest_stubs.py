"""The stand-in modules conftest puts in sys.modules must survive being monkeypatched.

whisperx, speechbrain and whisper are GPU-only and not installed in the test env, so
conftest registers a stub for each one and every test in the session shares that single
object. Almost every pipeline test patches an attribute on one of them, so a stub that
cannot be patched twice is a stub that works for the first test to touch a name and for
no test after it - which is a failure mode that reads as a broken pipeline rather than
as broken test scaffolding, and names a different test every time the order changes.
"""
import sys

import pytest

# A name no other test patches, so this probe cannot poison the real suite if it regresses.
PROBE = "stub_survival_probe"


@pytest.mark.parametrize("module_name", ["whisperx", "speechbrain", "whisper"])
def test_a_stub_attribute_can_be_patched_again_after_an_undo(module_name):
    stub = sys.modules[module_name]

    for attempt in range(2):
        with pytest.MonkeyPatch.context() as mp:
            mp.setattr(stub, PROBE, lambda: attempt)
            assert getattr(stub, PROBE)() == attempt

        # The patch is undone here, and the attribute must still RESOLVE. pytest 9.2
        # reads the old value out of target.__dict__, where a MagicMock keeps none of its
        # auto-created children, so it records "absent" and undo() deletes the attribute.
        # Deletion on a mock is sticky, which leaves the shared stub poisoned for the rest
        # of the session.
        assert getattr(stub, PROBE, None) is not None, (
            f"{module_name}.{PROBE} did not survive monkeypatch undo (attempt {attempt})"
        )
