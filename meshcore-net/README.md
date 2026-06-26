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
chip = "sx126x"

# Optional overrides:
# spi = 0
# cs = 0
# reset = 25
# busy = 24
# irq = 23
# txen = 4
# frequency = 868000000
# hal = "sx126x"
```

Exact config steps:

1. Choose the interface type: `type = "dragino-hat"` for the Raspberry Pi HAT flow.
2. Set the region: `region = "433"`, `"868"`, or `"915"` to pick the board frequency that matches your radio and local rules.
3. Select the SX HAL: `chip = "sx126x"` uses the implemented Linux SX126x backend; `chip = "sx127x"` selects the HAL slot for a future SX127x backend.
4. Optionally override the SPI/GPIO pins with the `spi`, `cs`, `reset`, `busy`, `irq`, `txen`, and `frequency` settings.

The HAL selection is centralized in the LoRa transport layer, so adding a new SX backend later only needs a new implementation behind the same config key.
