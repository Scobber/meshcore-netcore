#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" >/dev/null 2>&1 && pwd)"
PUBLISH_DIR="${SCRIPT_DIR}/publish/linux-arm64"
BIN_DIR="/usr/local/bin"
CONFIG_DIR="/etc/meshcore-netcore"
DATA_DIR="/var/lib/meshcore"
LOG_DIR="/var/log/meshcore-netcore"
SYSTEMD_DIR="/etc/systemd/system"
EXECUTABLE="meshcore-net"
SERVICE_NAME="meshcore-netcore.service"
SERVICE_USER="meshcore-netcore"
SERVICE_GROUP="meshcore-netcore"

usage() {
  cat <<EOF
Usage: ${0##*/} [options]

Options:
  -b, --bin-dir <path>        Binary install directory (default: /usr/local/bin)
  -c, --config-dir <path>     Configuration directory (default: /etc/meshcore-netcore)
  -d, --data-dir <path>       Data/binary directory (default: /var/lib/meshcore)
  -p, --publish-dir <path>    Publish artifacts directory (default: publish/linux-arm64)
  -h, --help                  Show this help message

Examples:
  ./install.sh
  ./install.sh --publish-dir /tmp/publish/linux-arm64
  ./install.sh --config-dir /etc/meshcore-netcore --data-dir /var/lib/meshcore
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

if [ ! -d "$PUBLISH_DIR" ]; then
  echo "Publish directory does not exist: $PUBLISH_DIR"
  echo "Run ./build.sh --publish first."
  exit 1
fi

if systemctl list-unit-files | grep -q "^$SERVICE_NAME"; then
  sudo systemctl stop "$SERVICE_NAME" >/dev/null 2>&1 || true
fi

sudo mkdir -p "$BIN_DIR" "$CONFIG_DIR" "$DATA_DIR"
sudo mkdir -p "$LOG_DIR"

if ! getent group "$SERVICE_GROUP" >/dev/null; then
  sudo groupadd --system "$SERVICE_GROUP"
fi

if ! id -u "$SERVICE_USER" >/dev/null 2>&1; then
  sudo useradd --system --no-create-home --home-dir "$DATA_DIR" --shell /usr/sbin/nologin -g "$SERVICE_GROUP" "$SERVICE_USER"
fi

sudo rm -rf "$DATA_DIR"/*
sudo cp -r "$PUBLISH_DIR"/* "$DATA_DIR"
sudo ln -sf "$DATA_DIR/$EXECUTABLE" "$BIN_DIR/$EXECUTABLE"

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

sudo tee "$SYSTEMD_DIR/$SERVICE_NAME" >/dev/null <<'EOF'
[Unit]
Description=MeshCore .NET worker service
Documentation=man:systemd.service(5)
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=meshcore-netcore
Group=meshcore-netcore
ExecStart=/usr/local/bin/meshcore-net /etc/meshcore-netcore/config.toml
WorkingDirectory=/var/lib/meshcore
Restart=always
RestartSec=10
StartLimitIntervalSec=60
StartLimitBurst=3
TimeoutStopSec=30
KillMode=control-group
StandardOutput=journal
StandardError=journal
SyslogIdentifier=meshcore-netcore
Environment=DOTNET_CLI_TELEMETRY_OPTOUT=1
Environment=MESHCORE_LOG_DIR=/var/log/meshcore-netcore
ProtectSystem=full
ProtectHome=yes
NoNewPrivileges=yes
AmbientCapabilities=CAP_NET_BIND_SERVICE
CapabilityBoundingSet=CAP_NET_BIND_SERVICE
ReadWritePaths=/etc/meshcore-netcore /var/lib/meshcore /var/log/meshcore-netcore

[Install]
WantedBy=multi-user.target
Alias=meshcore.service
EOF

sudo ln -sf "$SYSTEMD_DIR/$SERVICE_NAME" "$SYSTEMD_DIR/meshcore.service"

sudo chown -R "$SERVICE_USER:$SERVICE_GROUP" "$DATA_DIR" "$CONFIG_DIR" "$LOG_DIR"

sudo systemctl daemon-reload
sudo systemctl enable "$SERVICE_NAME"
if ! sudo systemctl restart "$SERVICE_NAME"; then
  sudo systemctl --no-pager --full status "$SERVICE_NAME" || true
  sudo journalctl -u "$SERVICE_NAME" -n 80 --no-pager || true
  echo "Service restart failed during install." >&2
  exit 1
fi

echo "Installed MeshCore .NET"
echo "Executable symlink: $BIN_DIR/$EXECUTABLE"
echo "Data directory: $DATA_DIR"
echo "Config directory: $CONFIG_DIR"
echo "Service file: $SYSTEMD_DIR/$SERVICE_NAME"
echo "Service enabled and restarted: $SERVICE_NAME"
sudo systemctl --no-pager --full status "$SERVICE_NAME" | sed -n '1,20p'
