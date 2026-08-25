#!/bin/bash

# Ensure script fails if any command fails
set -euo pipefail

# Define colors for better output
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
NC='\033[0m' # No Color

# Git Bash rewrites arguments that look like Unix paths into Windows paths before a native
# .exe sees them, which turns the container-side `-v ...:/omi` mount target into something
# like C:/Program Files/Git/omi and makes the build fail with confusing "no such file"
# errors from inside the container. Exporting this here is equivalent to prefixing it on
# the invocation (verified), so it does not have to be the caller's problem. Unused and
# harmless on Linux and macOS.
export MSYS_NO_PATHCONV=1

# Check if Docker is installed
if ! command -v docker &> /dev/null; then
    echo -e "${RED}Error: Docker is not installed or not in PATH${NC}"
    exit 1
fi

# Parse command line arguments
CLEAN_BUILD=0
for arg in "$@"; do
    case $arg in
        --clean)
            CLEAN_BUILD=1
            shift
            ;;
        *)
            # Unknown option
            ;;
    esac
done

# Make script executable
chmod +x $(dirname "$0")/build-firmware-in-docker.sh

# Detect platform - for M1/M2/M3 Macs
PLATFORM_FLAG=""
if [[ $(uname -m) == "arm64" ]]; then
    echo -e "${YELLOW}Detected ARM64 platform (M1/M2/M3 Mac)${NC}"
fi

# Get the absolute path to the repository root
REPO_ROOT=$(cd "$(dirname "$0")/../.." && pwd)

# Clean build if requested
if [ $CLEAN_BUILD -eq 1 ]; then
    echo -e "${YELLOW}Cleaning previous build...${NC}"
    rm -rf "$REPO_ROOT/firmware/v2.7.0"
    rm -rf "$REPO_ROOT/firmware/build/docker_build"
    # Also clean the build directory inside app if it exists from previous runs
    rm -rf "$REPO_ROOT/firmware/app/build"
fi

echo -e "${YELLOW}Starting Docker container for firmware build...${NC}"
echo -e "${YELLOW}This might take a while the first time.${NC}"

# Run the Docker container with the repository mounted correctly
# Rely on the environment variables set within the ghcr.io/zephyrproject-rtos/ci image
#
# The tag is PINNED, deliberately. This application targets nRF Connect SDK 2.7.0, which
# ships Zephyr 3.6.99, which requires Zephyr SDK 0.16.x. That is a fixed target: no future
# SDK will ever satisfy it. Following `latest` is therefore not "staying current", it is
# drifting out of the only range that can work - it reached SDK 1.0.1 and CMake refused to
# configure at all. v0.26.14 carries SDK 0.16.8, the version NCS 2.7.0 specifies, and is
# the newest image whose SDK is a release rather than a release candidate.
#
# Verified end to end on 2026-08-25. Still overridable - see the runbook, section 8.2.
ZEPHYR_CI_IMAGE="${ZEPHYR_CI_IMAGE:-ghcr.io/zephyrproject-rtos/ci:v0.26.14}"
echo -e "${YELLOW}Image: ${ZEPHYR_CI_IMAGE}${NC}"

# Two things here are deliberate:
#
# 1. PATH is NOT passed with -e. Doing that replaces the image's own PATH with the HOST's,
#    which on Git Bash is a list of Windows paths that mean nothing inside the container -
#    so west/cmake/ninja stop being found. Prepend inside the container instead, in single
#    quotes so $PATH expands there and not here.
# 2. adafruit-nrfutil is installed by the inner script, not chained onto it with &&. It is
#    only needed for the optional OTA package, and newer CI images ship a PEP 668
#    "externally managed" Python that refuses a plain pip install - which used to abort the
#    whole build before a single line was compiled.
docker run --rm -it $PLATFORM_FLAG \
    -v "$REPO_ROOT:/omi" \
    -e CMAKE_PREFIX_PATH=/opt/toolchains \
    "$ZEPHYR_CI_IMAGE" \
    bash -c 'export PATH="/root/.local/bin:$PATH"; /omi/firmware/scripts/build-firmware-in-docker.sh'

# Check if the build was successful
if [ -d "$REPO_ROOT/firmware/build/docker_build" ] && [ "$(ls -A "$REPO_ROOT/firmware/build/docker_build")" ]; then
    echo -e "${GREEN}Build artifacts are available at:${NC}"
    echo -e "${GREEN}$(realpath "$REPO_ROOT/firmware/build/docker_build")${NC}"

    # List the generated files
    echo -e "${YELLOW}Generated files:${NC}"
    ls -la "$REPO_ROOT/firmware/build/docker_build"
else
    echo -e "${RED}Build may have failed. Check the logs above for errors.${NC}"
    exit 1
fi
