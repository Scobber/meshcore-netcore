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

# Optional overrides:
# spi = 0
# cs = 0
# nss = 25
# reset = 17
# busy = -1
# irq = 4
# txen = -1
# frequency = 868000000
# hal = "sx127x"
```

Exact config steps:

1. Choose the interface type: `type = "dragino-hat"` for the Raspberry Pi HAT flow.
2. Set the region: `region = "433"`, `"868"`, or `"915"` to pick the board frequency that matches your radio and local rules.
3. Select the SX HAL: `chip = "sx126x"` uses the SX126x backend; `chip = "sx127x"` uses the SX127x backend for Dragino LoRa HAT boards.
4. Optionally override the SPI/GPIO pins with the `spi`, `cs`, `nss`, `reset`, `busy`, `irq`, `txen`, and `frequency` settings.

The HAL selection is centralized in the LoRa transport layer, so adding a new SX backend later only needs a new implementation behind the same config key.
