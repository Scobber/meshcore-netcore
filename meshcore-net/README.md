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

# Optional overrides (Pi 5 with spidev0.2 overlay — see setup section below):
# spi = 0
# cs = 2       # /dev/spidev0.2 — GPIO 25 hardware CS via dtoverlay
# nss = -1     # kernel handles CS; no manual GPIO toggle needed
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

#### 2. Add the GPIO 25 chip-select overlay

Add the following line to `/boot/firmware/config.txt`:

```
dtoverlay=spi0-3cs,cs2_pin=25
```

Reboot, then verify the third device node is present:

```bash
ls /dev/spidev*
# /dev/spidev0.0  /dev/spidev0.1  /dev/spidev0.2
```

`/dev/spidev0.2` now uses GPIO 25 as its hardware chip select.

In `config.dragino-pi5.toml` (or your equivalent config) set:

```toml
spi = 0
cs  = 2      # opens /dev/spidev0.2 — GPIO 25 hardware CS via overlay
nss = -1     # kernel handles CS; no manual GPIO toggle needed
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


