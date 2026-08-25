#!/bin/bash

# Compiles and runs the pure-logic unit tests on the host toolchain inside the
# pinned build container. There is no C compiler on a stock Windows machine, and
# the container already carries gcc, so this needs no extra prerequisite beyond
# the one the firmware build already has.
#
# No `-it` here, unlike build-docker.sh: this must run from CI and other
# non-interactive shells, where a TTY-enabled container is refused with
# "cannot attach stdin to a TTY-enabled container because stdin is not a
# terminal".

set -euo pipefail

# See build-docker.sh for why this is exported rather than left to the caller.
export MSYS_NO_PATHCONV=1

GREEN='\033[0;32m'
RED='\033[0;31m'
NC='\033[0m'

REPO_ROOT=$(cd "$(dirname "$0")/../.." && pwd)
ZEPHYR_CI_IMAGE="${ZEPHYR_CI_IMAGE:-ghcr.io/zephyrproject-rtos/ci:v0.26.14}"

if docker run --rm -v "$REPO_ROOT:/omi" "$ZEPHYR_CI_IMAGE" bash -c '
    set -e
    cd /omi/firmware/devkit
    gcc -std=c11 -Wall -Wextra -Werror -O1 \
        -o /tmp/test_usb_mode src/usb_mode.c tests/test_usb_mode.c
    /tmp/test_usb_mode
'; then
    echo -e "${GREEN}Host tests passed${NC}"
else
    echo -e "${RED}Host tests FAILED${NC}"
    exit 1
fi
