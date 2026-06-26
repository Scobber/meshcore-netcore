#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" >/dev/null 2>&1 && pwd)"
PROJECT="${SCRIPT_DIR}/meshcore-net/meshcore-net.csproj"
CONFIGURATION="Release"
RUNTIME="linux-arm64"
OUTPUT="${SCRIPT_DIR}/publish/${RUNTIME}"
RESTORE=true
PUBLISH=false

usage() {
  cat <<EOF
Usage: ${0##*/} [options]

Options:
  -c, --configuration <Debug|Release>  Build configuration (default: Release)
  -r, --runtime <RID>                 Runtime identifier for publish (default: linux-arm64)
  -o, --output <path>                 Publish output directory (default: publish/<rid>)
  --no-restore                        Skip dotnet restore
  --publish                           Publish the project instead of building
  -h, --help                          Show this help message

Examples:
  ./build.sh
  ./build.sh --publish
  ./build.sh --publish -r linux-x64 -o publish/linux-x64
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    -c|--configuration)
      CONFIGURATION="${2:-}"
      shift 2
      ;;
    -r|--runtime)
      RUNTIME="${2:-}"
      shift 2
      ;;
    -o|--output)
      OUTPUT="${2:-}"
      shift 2
      ;;
    --no-restore)
      RESTORE=false
      shift
      ;;
    --publish)
      PUBLISH=true
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "Unknown option: $1"
      usage
      exit 1
      ;;
  esac
done

if [ "$RESTORE" = true ]; then
  echo "Restoring dependencies..."
  dotnet restore "$PROJECT"
fi

if [ "$PUBLISH" = true ]; then
  echo "Publishing project for runtime '$RUNTIME' to '$OUTPUT'..."
  dotnet publish "$PROJECT" -c "$CONFIGURATION" -r "$RUNTIME" --self-contained false -o "$OUTPUT"
else
  echo "Building project..."
  dotnet build "$PROJECT" -c "$CONFIGURATION"
fi

echo "Done."
