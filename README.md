# wiisl — Why is it so laggy?

**English** · [简体中文](README.zh-CN.md)

[![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078D6?logo=windows&logoColor=white)](#requirements)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](#build-from-source)
[![License](https://img.shields.io/badge/license-MIT-green)](LICENSE)

> A Windows desktop tool that watches your game while it runs, then tells you *why* it stutters — with numbers instead of guesses.

Pick any running process (or let wiisl find your installed games), sample CPU / GPU / memory / disk in real time, read the game's own graphics config, and get a diagnostic report in **Markdown + PDF + CSV + JSON** with findings ranked by severity.

> **Safety promise: wiisl only ever reads your game files. It never writes to or modifies any game file, config, or save.**
> Every recommendation targets the system layer (power plan, GPU driver settings, background processes) or in-game graphics options — and only you decide whether to apply it.

---

## Why this exists

Most "optimizer" tools tell you to disable services and hope for the best. wiisl does the opposite: it measures, then refuses to conclude when the data doesn't support a conclusion. If a sensor is unreliable, the report says so instead of printing a confident wrong number. See [Known limitations](#known-limitations-measured-not-guessed) — every item there was measured on real hardware, and several of them are cases where wiisl deliberately reports *nothing*.

## Features

- **Any process, or your whole game library** — pick a PID from a live process list, or auto-scan Steam / Epic / Battle.net / Ubisoft / GOG / WeGame / Xbox libraries.
- **12 live readouts + chart** — CPU usage, real clock, temperature, package power; GPU usage, clock, temperature, power; process memory; system available memory; disk; page file.
- **Reads the game's graphics config** — parses INI / JSON / XML / VDF config files and translates settings like shadow quality or ambient occlusion into a performance-impact rating.
- **Ranked findings, not a wall of numbers** — every conclusion carries its evidence, an explanation, concrete recommended actions, and expected gain.
- **Four output formats** — Markdown for reading, PDF for sharing, CSV/JSON for your own analysis.
- **Timed or manual sampling** — run for a fixed duration, or start and stop by hand; sampling also ends automatically when the game exits.
- **Graceful degradation** — every collector probes itself first. Anything unavailable is reported as unavailable, never faked.

## Requirements

- Windows 10 or Windows 11 (x64)
- **Nothing else, if you use the portable build** — it bundles the .NET runtime
- .NET 10 SDK only if you [build from source](#build-from-source)
- Administrator rights are **optional** — see [Data sources & permissions](#data-sources--permissions)

## Quick start

### Option A — portable build (no install, no .NET needed)

Download both files from [Releases](../../releases) into the **same folder**, then run `wiisl.exe`:

| File | Purpose |
|---|---|
| `wiisl.exe` | The application (self-contained, ~78 MB) |
| `QuestPDF.Fonts.Lato.br` | Font resource required for PDF output |

> ⚠️ **Keep the two files together.** If only the exe is copied, Markdown/CSV/JSON still work but PDF generation fails with `The text "..." uses font families that are not available: 'Lato'`. wiisl exits with code `5` and deletes the empty file it left behind rather than leaving you a broken PDF.

### Option B — build from source

```bash
git clone https://github.com/chioplet/Why-is-it-so-laggy_.git
cd Why-is-it-so-laggy_
dotnet build wiisl.slnx -c Release
```

The executable lands in `src/Gpd.App/bin/Release/net10.0-windows/win-x64/wiisl.exe`.

To produce the portable single-file build instead:

```powershell
powershell -ExecutionPolicy Bypass -File publish.ps1
```

## Using the GUI

| Area | What it does |
|---|---|
| **Any process** (left) | Lists every process, windowed ones first; filter by name, window title, or PID |
| **Game library** (left) | Scans installed games across 8 launchers; selecting one resolves its running process |
| **Live monitor** (right) | Set sampling interval and duration (leave duration empty for manual stop), watch 12 readouts and a live chart |
| **Diagnostic report** (right) | Choose output folder and formats, generate the report, and read every finding inline |

## Using the CLI

```bash
wiisl.exe --cli (--pid <PID> | --process <NAME>) [options]
```

| Option | Description |
|---|---|
| `--interval <sec>` | Sampling interval, default `1.0` (minimum `0.2`) |
| `--seconds <sec>` | Sampling duration. Omit to keep sampling until the target process exits |
| `--out <dir>` | Output directory. Defaults to `Documents\wiisl reports` |
| `--formats <list>` | Any combination of `md,pdf,csv,json`. Defaults to all |
| `--fps` | Enable the FPS module (requires administrator; degrades automatically without it) |
| `--scan-games` | Scan game libraries and link the target process to its game (enables graphics-config analysis) |
| `--game <name>` | Target a game by name (scans libraries first) |
| `--no-stop-on-exit` | Keep sampling after the target process exits |
| `--help` | Show help |

Example:

```bash
wiisl.exe --cli --process cs2 --seconds 60 --scan-games --out D:\reports
```

**Exit codes:** `0` success · `2` bad argument or target process · `3` zero samples collected · `4` no report file written · `5` some format failed to generate.

> wiisl is a WinExe (no console of its own), so its text output is only visible when the caller redirects stdout.

## What's in the report

| Section | Contents |
|---|---|
| 1. Summary | Bottleneck verdict, findings table ranked by severity, and "the one thing to do first" |
| 2. Hardware & system | CPU, memory, GPUs, power plan, Game Mode, HAGS, driver version |
| 3. Sampling overview | Sample count, duration, interval, why sampling stopped |
| 4. Aggregate metrics | Avg / max / min for CPU, GPU, memory, page file, disk, frame rate |
| 5. Time series | Per-sample detail (sampled down to 200 rows, keeping first and last) |
| 6. Findings | Evidence, explanation, recommended actions, and expected gain per finding |
| 7. Graphics analysis | Settings read from the game's config, with performance-impact ratings |
| 8. Per-core snapshot | Average usage and peak temperature for each logical processor |
| 9. Data sources & confidence | Which collectors worked, which were restricted, and why |

## Data sources & permissions

| Metric | Source | Admin required |
|---|---|---|
| CPU / per-core usage | Performance counter `Processor Information` | No |
| CPU actual clock | Performance counter `Actual Frequency` | No |
| CPU package power | Performance counter `Energy Meter → RAPL_Package0_PKG` | No |
| GPU usage / clock / temp / power / VRAM | `nvidia-smi` (CSV path) | No |
| Per-process GPU usage & VRAM | Performance counters `GPU Engine` / `GPU Process Memory` | Partially |
| Memory / commit / page file / disk | Performance counters `Memory`, `Paging File`, `PhysicalDisk` | No |
| FPS / frame time / stutter | PresentMon (ETW) | **Yes** |
| Hardware & OS info | WMI + registry + `powercfg` | No |

**FPS is optional by design.** PresentMon needs an ETW kernel session, which requires administrator rights. Without them the FPS module reports itself as unavailable and every other metric is unaffected. To get frame data, use **Restart as administrator** in the top-right of the window, then enable the FPS module.

The app manifest uses `asInvoker` rather than `requireAdministrator`: on a standard user account a `requireAdministrator` manifest means the app cannot start at all, and the primary data sources don't need elevation anyway.

## Known limitations (measured, not guessed)

1. **CPU package temperature is not available.** On the development machine, `Thermal Zone Information\_TZ.TZ0` turned out to be a **motherboard ACPI thermal zone**, not the CPU package: under a 16-thread full load it read 82.7 °C — *lower* than 85.1 °C at idle. A real CPU temperature cannot drop under load, so the report refuses to judge CPU throttling from it and instead emits "CPU package temperature unavailable, no thermal conclusion drawn". Accurate readings need a tool like HWiNFO64 that goes through MSR/DTS; wiisl deliberately does not install kernel drivers.
   wiisl performs a load-correlation check automatically and only treats a zone as CPU temperature if it genuinely rises with load (high-load average ≥ 3 °C above low-load average, with ≥ 3 samples in each group).
2. **The `Processor Frequency` counter is a static nominal base clock and must not be used as the current clock.** It measured constant at idle and under full load (`_Total` always 2250 MHz, P-cores 2400 MHz, E-cores 1800 MHz). `Actual Frequency` is the counter that actually tracks load.
3. **Virtual display adapters distort GPU counter readings.** The development machine has a `GameViewer Virtual Display Adapter`, splitting `GPU Engine` across two LUIDs. Whole-GPU data from nvidia-smi is unaffected, but per-process GPU counters must be disambiguated by LUID. The report flags virtual adapters in the hardware table.
4. **nvidia-smi on driver 617.14 does not support `--format=xml`** (exit code 2, `Format modifier is not recognized.`), so wiisl uses the CSV path. That driver also rejects the `voltage.gpu` field, and **a single invalid field fails the entire query while printing the error to stdout** — so field availability is probed at runtime. On this machine `power.limit` and `fan.speed` both return `[N/A]`.
5. **The Epic / Battle.net / GOG / Xbox code paths have no real-device coverage.** No games from those launchers were installed, so only the "no games found, explain why, don't throw" path was verified. Steam (5 games) and WeGame (1 game) were tested against real data.
6. **Graphics-impact ratings (0–5) are experienced-based estimates**, not measured frame deltas. The report states this.
7. Nine leftover Steam library folders (Aim Lab, PUBG, …) contained no `.exe` at all — uninstall residue. These are reported as warnings rather than fabricated into game entries.

## Project structure

```
wiisl.slnx
├── src/Gpd.Core/                 Class library (no UI dependency, reusable)
│   ├── Contracts.cs              All data contracts
│   ├── Collect/                  Collectors: CPU / GPU / memory / FPS / machine info / sampler
│   ├── Games/                    Library scanning (8 launchers) + config parsing (INI/JSON/XML/VDF)
│   ├── Analysis/                 SummaryCalculator + DiagnosisEngine + self-test
│   └── Reporting/                Markdown / CSV / JSON writers
└── src/Gpd.App/                  WPF application
    ├── MainWindow.xaml(.cs)      UI and interaction
    ├── CliRunner.cs              Headless mode
    ├── ReportBuilder.cs          Report assembly shared by GUI and CLI (so both agree)
    └── Reporting/PdfReportWriter.cs   QuestPDF report with embedded font subset
```

> `Gpd` is the original internal codename (Game Performance Doctor) and is kept as the namespace prefix; the shipped product is `wiisl`.

Build:

```bash
dotnet build wiisl.slnx -c Release
```

## Contributing

Issues and pull requests are welcome. If you report a performance-analysis bug, please include the generated `report.json` — it contains the raw samples the conclusion was drawn from.

## License

[MIT](LICENSE)
