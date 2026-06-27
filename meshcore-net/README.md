# MeshCore .NET host

This project is a Linux-only .NET 10 host for the MeshCore protocol concepts originally implemented in Python.

For a maintainer handoff and subsystem map, read [docs/internals.md](docs/internals.md).

## Build

```bash
dotnet build
```

### Build for Raspberry Pi / ARM64

```bash
dotnet build -r linux-arm64
```

## Run

```bash
dotnet run --project meshcore-net.csproj -- ../config.toml
```

## Raspberry Pi HAT configuration

The host now understands a Linux-only Dragino-style Pi HAT interface for SX radio families. Configure it in TOML like this:

```toml
[[interfaces]]
name = "lora"
type = "dragino-hat"
region = "868"
chip = "sx127x"

# Optional overrides (stock Pi OS + Dragino manual NSS):
# spi = 0
# cs = 0       # /dev/spidev0.0
# nss = 25     # Dragino HAT v1.4 manual NSS/CS on GPIO25
# reset = 17
# busy = -1
# irq = 4
# txen = -1
# frequency = 868000000
```

### Raspberry Pi 5 — Dragino LoRa HAT v1.4 setup

The Dragino HAT v1.4 uses **GPIO 25** as its SPI chip select rather than the
standard CE0/CE1 lines.  Without the custom overlay the radio will not respond.

#### 1. Enable SPI

```bash
sudo raspi-config
# Interface Options → SPI → Enable
```

Reboot, then verify:

```bash
ls /dev/spidev*
# /dev/spidev0.0  /dev/spidev0.1
```

#### 2. Configure stock Pi 5 SPI/UART baseline

Use this baseline in `/boot/firmware/config.txt`:

```ini
dtparam=spi=on
enable_uart=1

[pi5]
dtoverlay=nospi10

[all]
```

Reboot, then verify SPI is present:

```bash
ls /dev/spidev*
# /dev/spidev0.0  /dev/spidev0.1
```

Use `/dev/spidev0.0` and set GPIO 25 as manual NSS in app config.

In `config.dragino-pi5.toml` (or your equivalent config) set:

```toml
spi = 0
cs  = 0      # opens /dev/spidev0.0
nss = 25     # Dragino HAT v1.4 manual NSS/CS on GPIO25
```

#### 3. GPS (optional)

The GPS module is wired to the UART.  First disable the serial console and
enable the hardware UART:

```bash
sudo raspi-config
# Interface Options → Serial Port
#   Login shell over serial? No
#   Enable serial hardware?  Yes
```

Then in your config set `[gps] enabled = true` and `device = "/dev/serial0"`.
See `config.dragino-pi5.toml` for the full GPS block with all available options.

