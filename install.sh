#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" >/dev/null 2>&1 && pwd)"
PUBLISH_DIR="${SCRIPT_DIR}/publish/linux-arm64"
BIN_DIR="/usr/local/bin"
CONFIG_DIR="/etc/meshcore-netcore"
CREDENTIAL_DIR="/etc/meshcore-netcore"
LEGACY_CREDENTIAL_DIR="/etc/metcore-netcore"
DATA_DIR="/var/lib/meshcore"
LOG_DIR="/var/log/meshcore-netcore"
SYSTEMD_DIR="/etc/systemd/system"
EXECUTABLE="meshcore-net"
WEB_EXECUTABLE="meshcore-web"
REPEATER_EXECUTABLE="meshcore-repeater"
COMPANION_EXECUTABLE="meshcore-companion"
WEB_SERVICE_NAME="meshcore-web.service"
REPEATER_SERVICE_NAME="meshcore-repeater.service"
COMPANION_SERVICE_NAME="meshcore-companion.service"
LEGACY_SERVICE_NAME="meshcore-netcore.service"
SERVICE_USER="meshcore-netcore"
SERVICE_GROUP="meshcore-netcore"

usage() {
  cat <<EOF
Usage: ${0##*/} [options]

Options:
  -b, --bin-dir <path>        Binary install directory (default: /usr/local/bin)
  -c, --config-dir <path>     Configuration directory (default: /etc/meshcore-netcore)
  -C, --credential-dir <path> Credential directory (default: /etc/meshcore-netcore)
  -d, --data-dir <path>       Data/binary directory (default: /var/lib/meshcore)
  -p, --publish-dir <path>    Publish artifacts directory (default: publish/linux-arm64)
  -h, --help                  Show this help message

Examples:
  ./install.sh
  ./install.sh --publish-dir /tmp/publish/linux-arm64
  ./install.sh --config-dir /etc/meshcore-netcore --credential-dir /etc/meshcore-netcore --data-dir /var/lib/meshcore
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    -b|--bin-dir)
      BIN_DIR="${2:-}"
      shift 2
      ;;
    -c|--config-dir)
      CONFIG_DIR="${2:-}"
      shift 2
      ;;
    -C|--credential-dir)
      CREDENTIAL_DIR="${2:-}"
      shift 2
      ;;
    -d|--data-dir)
      DATA_DIR="${2:-}"
      shift 2
      ;;
    -p|--publish-dir)
      PUBLISH_DIR="${2:-}"
      shift 2
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

ensure_libgpiod() {
  if ldconfig -p 2>/dev/null | grep -q "libgpiod\.so\.2"; then
    return 0
  fi

  echo "libgpiod.so.2 is missing; attempting to install runtime dependency..."

  if command -v apt-get >/dev/null 2>&1; then
    sudo apt-get update
    sudo apt-get install -y libgpiod2 || \
      sudo apt-get install -y libgpiod2t64 || \
      sudo apt-get install -y libgpiod3 || \
      sudo apt-get install -y libgpiod1 || \
      sudo apt-get install -y libgpiod0 || \
      sudo apt-get install -y gpiod
  elif command -v dnf >/dev/null 2>&1; then
    sudo dnf install -y libgpiod
  elif command -v yum >/dev/null 2>&1; then
    sudo yum install -y libgpiod
  elif command -v zypper >/dev/null 2>&1; then
    sudo zypper --non-interactive install libgpiod2 || sudo zypper --non-interactive install libgpiod
  else
    echo "Could not auto-install libgpiod: no supported package manager found." >&2
  fi

  if ! ldconfig -p 2>/dev/null | grep -q "libgpiod\.so\.2"; then
    echo "ERROR: libgpiod.so.2 is still unavailable. Install it manually and rerun install." >&2
    echo "Debian/Ubuntu: sudo apt-get install -y libgpiod2" >&2
    echo "RHEL/Fedora:   sudo dnf install -y libgpiod" >&2
    return 1
  fi
}

if [ ! -d "$PUBLISH_DIR" ]; then
  echo "Publish directory does not exist: $PUBLISH_DIR"
  echo "Run ./build.sh --publish first."
  exit 1
fi

ensure_libgpiod

for service in "$WEB_SERVICE_NAME" "$REPEATER_SERVICE_NAME" "$COMPANION_SERVICE_NAME" "$LEGACY_SERVICE_NAME"; do
  if systemctl list-unit-files | grep -q "^$service"; then
    sudo systemctl stop "$service" >/dev/null 2>&1 || true
  fi
done

sudo mkdir -p "$BIN_DIR" "$CONFIG_DIR" "$DATA_DIR"
sudo mkdir -p "$CREDENTIAL_DIR"
sudo mkdir -p "$LOG_DIR"

if ! getent group "$SERVICE_GROUP" >/dev/null; then
  sudo groupadd --system "$SERVICE_GROUP"
fi

if ! id -u "$SERVICE_USER" >/dev/null 2>&1; then
  sudo useradd --system --no-create-home --home-dir "$DATA_DIR" --shell /usr/sbin/nologin -g "$SERVICE_GROUP" "$SERVICE_USER"
fi

for hw_group in gpio spi; do
  if getent group "$hw_group" >/dev/null; then
    sudo usermod -a -G "$hw_group" "$SERVICE_USER"
  fi
done

sudo rm -rf "$DATA_DIR"/*
sudo cp -r "$PUBLISH_DIR"/* "$DATA_DIR"
sudo ln -sf "$DATA_DIR/$EXECUTABLE" "$BIN_DIR/$EXECUTABLE"
sudo ln -sf "$DATA_DIR/$EXECUTABLE" "$BIN_DIR/$WEB_EXECUTABLE"
sudo ln -sf "$DATA_DIR/$EXECUTABLE" "$BIN_DIR/$REPEATER_EXECUTABLE"
sudo ln -sf "$DATA_DIR/$EXECUTABLE" "$BIN_DIR/$COMPANION_EXECUTABLE"

if [ ! -f "$CONFIG_DIR/config.toml" ]; then
  if [ -f "$SCRIPT_DIR/config.toml" ]; then
    sudo cp "$SCRIPT_DIR/config.toml" "$CONFIG_DIR/config.toml"
    echo "Default config copied to $CONFIG_DIR/config.toml"
  else
    sudo touch "$CONFIG_DIR/config.toml"
    echo "Created empty config file at $CONFIG_DIR/config.toml"
  fi
fi

if [ ! -f "$CONFIG_DIR/readonly.toml" ] && [ -f "$SCRIPT_DIR/readonly.toml" ]; then
  sudo cp "$SCRIPT_DIR/readonly.toml" "$CONFIG_DIR/readonly.toml"
  echo "Default readonly config copied to $CONFIG_DIR/readonly.toml"
fi

if [ -d "$LEGACY_CREDENTIAL_DIR" ] && [ "$LEGACY_CREDENTIAL_DIR" != "$CREDENTIAL_DIR" ]; then
  for credential in password private public; do
    if [ -f "$LEGACY_CREDENTIAL_DIR/$credential" ] && [ ! -f "$CREDENTIAL_DIR/$credential" ]; then
      sudo cp "$LEGACY_CREDENTIAL_DIR/$credential" "$CREDENTIAL_DIR/$credential"
    fi
  done
fi

if [ ! -f "$CREDENTIAL_DIR/password" ]; then
  sudo touch "$CREDENTIAL_DIR/password"
fi

if [ ! -f "$CREDENTIAL_DIR/private" ] || [ ! -f "$CREDENTIAL_DIR/public" ] || [ ! -s "$CREDENTIAL_DIR/password" ]; then
  sudo "$DATA_DIR/$EXECUTABLE" --generate-admin-keys "$CREDENTIAL_DIR"
fi

sudo cp "$SCRIPT_DIR/meshcore-web.service" "$SYSTEMD_DIR/$WEB_SERVICE_NAME"
sudo cp "$SCRIPT_DIR/meshcore-repeater.service" "$SYSTEMD_DIR/$REPEATER_SERVICE_NAME"
sudo cp "$SCRIPT_DIR/meshcore-companion.service" "$SYSTEMD_DIR/$COMPANION_SERVICE_NAME"

if [ -e "$SYSTEMD_DIR/$LEGACY_SERVICE_NAME" ]; then
  sudo rm -f "$SYSTEMD_DIR/$LEGACY_SERVICE_NAME"
fi
if [ -L "$SYSTEMD_DIR/meshcore.service" ]; then
  sudo rm -f "$SYSTEMD_DIR/meshcore.service"
fi

sudo chown -R "$SERVICE_USER:$SERVICE_GROUP" "$DATA_DIR" "$LOG_DIR"
sudo chown root:"$SERVICE_GROUP" "$CONFIG_DIR"
sudo chmod 775 "$CONFIG_DIR"
sudo chown root:"$SERVICE_GROUP" "$CREDENTIAL_DIR"
sudo chmod 775 "$CREDENTIAL_DIR"
if [ -f "$CONFIG_DIR/config.toml" ]; then
  sudo chown root:"$SERVICE_GROUP" "$CONFIG_DIR/config.toml"
  sudo chmod 664 "$CONFIG_DIR/config.toml"
fi
if [ -f "$CONFIG_DIR/readonly.toml" ]; then
  sudo chown root:"$SERVICE_GROUP" "$CONFIG_DIR/readonly.toml"
  sudo chmod 664 "$CONFIG_DIR/readonly.toml"
fi
for credential in password private public; do
  if [ -f "$CREDENTIAL_DIR/$credential" ]; then
    sudo chown root:"$SERVICE_GROUP" "$CREDENTIAL_DIR/$credential"
    sudo chmod 664 "$CREDENTIAL_DIR/$credential"
  fi
done

sudo systemctl daemon-reload
sudo systemctl disable "$LEGACY_SERVICE_NAME" >/dev/null 2>&1 || true

for service in "$WEB_SERVICE_NAME" "$REPEATER_SERVICE_NAME" "$COMPANION_SERVICE_NAME"; do
  sudo systemctl enable "$service"
  echo "Service enable result [$service]: $(sudo systemctl is-enabled "$service" 2>/dev/null || echo unknown)"
  if ! sudo systemctl restart "$service"; then
    sudo systemctl --no-pager --full status "$service" || true
    sudo journalctl -u "$service" -n 80 --no-pager || true
    echo "Service restart failed during install: $service" >&2
    exit 1
  fi
  echo "Service active result [$service]: $(sudo systemctl is-active "$service" 2>/dev/null || echo unknown)"
done

echo "Installed MeshCore .NET"
echo "Executable symlink: $BIN_DIR/$EXECUTABLE"
echo "Executable symlink: $BIN_DIR/$WEB_EXECUTABLE"
echo "Executable symlink: $BIN_DIR/$REPEATER_EXECUTABLE"
echo "Executable symlink: $BIN_DIR/$COMPANION_EXECUTABLE"
echo "Data directory: $DATA_DIR"
echo "Config directory: $CONFIG_DIR"
echo "Credential directory: $CREDENTIAL_DIR"
echo "Service file: $SYSTEMD_DIR/$WEB_SERVICE_NAME"
echo "Service file: $SYSTEMD_DIR/$REPEATER_SERVICE_NAME"
echo "Service file: $SYSTEMD_DIR/$COMPANION_SERVICE_NAME"
for service in "$WEB_SERVICE_NAME" "$REPEATER_SERVICE_NAME" "$COMPANION_SERVICE_NAME"; do
  echo "Service enabled and restarted: $service"
  sudo systemctl --no-pager --full status "$service" | sed -n '1,20p'
done
