#!/usr/bin/env bash
# Prints the content tags of the two C++ base images, one KEY=value per line:
#
#   TA_DEPS_TAG=<hash>    tag of treealarm/ta-deps   (built from ta-deps/)
#   ROI_DEPS_TAG=<hash>   tag of treealarm/roi-deps  (built from roitrc/ta_roienc/docker/intel/)
#
# The tag is the git tree hash of the directory the image is built from, so it changes exactly
# when the Dockerfile or the triplet changes and never otherwise. That is what lets CI pull a
# base image someone built hours earlier on another machine and know it is the right one:
# neither base can be built on a hosted runner (ta-deps alone is ~4 h, mostly openvino).
#
# Shared by .github/workflows/images.yml and ../ta_install/scripts/{build-images,push-deps-images}.sh
# -- three callers that must agree on the tag, hence one place that computes it.
#
# The hash is of the *committed* tree. Uncommitted edits in either directory would make the tag
# name an image that is not what gets built, so this refuses rather than lies.
set -euo pipefail

TA_VMS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ENCODER_DIR="$TA_VMS_DIR/roitrc/ta_roienc"

[ -f "$ENCODER_DIR/CMakeLists.txt" ] || {
    echo "roitrc/ta_roienc is empty -- run: git -C $TA_VMS_DIR submodule update --init --recursive" >&2
    exit 1
}

tree_tag() {
    local repo="$1" dir="$2"
    if [ -n "$(git -C "$repo" status --porcelain -- "$dir")" ]; then
        echo "$repo/$dir has uncommitted changes -- commit them first, the image tag is the committed tree hash" >&2
        exit 1
    fi
    git -C "$repo" rev-parse --short=12 "HEAD:$dir"
}

echo "TA_DEPS_TAG=$(tree_tag "$TA_VMS_DIR" ta-deps)"
echo "ROI_DEPS_TAG=$(tree_tag "$ENCODER_DIR" docker/intel)"
