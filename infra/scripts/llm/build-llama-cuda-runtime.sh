#!/usr/bin/env bash
set -euo pipefail

LLAMA_CPP_TAG="${LLAMA_CPP_TAG:-b10098}"
LLAMA_CPP_REVISION="${LLAMA_CPP_REVISION:-0278d8362d78c5de291bc03b76016f7f74b2ab77}"
IMAGE_TAG="${IMAGE_TAG:-saaia/llama.cpp:server-cuda-b10098}"
CUDA_VERSION="${CUDA_VERSION:-12.4.1}"
UBUNTU_VERSION="${UBUNTU_VERSION:-22.04}"
GCC_VERSION="${GCC_VERSION:-12}"
CUDA_ARCHITECTURES="${CUDA_ARCHITECTURES:-75}"

build_root="$(mktemp -d)"
cleanup() {
  rm -rf -- "$build_root"
}
trap cleanup EXIT

git clone --filter=blob:none --branch "$LLAMA_CPP_TAG" \
  https://github.com/ggml-org/llama.cpp.git "$build_root/llama.cpp"

actual_revision="$(git -C "$build_root/llama.cpp" rev-parse HEAD)"
if [[ "$actual_revision" != "$LLAMA_CPP_REVISION" ]]; then
  echo "Revision llama.cpp inattendue: $actual_revision" >&2
  exit 1
fi

docker build \
  --file "$build_root/llama.cpp/.devops/cuda.Dockerfile" \
  --target server \
  --build-arg "APP_VERSION=$LLAMA_CPP_TAG" \
  --build-arg "APP_REVISION=$LLAMA_CPP_REVISION" \
  --build-arg "CUDA_VERSION=$CUDA_VERSION" \
  --build-arg "UBUNTU_VERSION=$UBUNTU_VERSION" \
  --build-arg "GCC_VERSION=$GCC_VERSION" \
  --build-arg "CUDA_DOCKER_ARCH=$CUDA_ARCHITECTURES" \
  --tag "$IMAGE_TAG" \
  "$build_root/llama.cpp"

docker run --rm --gpus all "$IMAGE_TAG" --list-devices
