# HarryDataServer V2 — Suite Overview

Industrial data-acquisition and quality system for the 5-blade razor-head line, plus a set
of companion desktop tools that read from the same MySQL database (`camera_data`).

- **Master spec:** [`CLAUDE.md`](CLAUDE.md)
- **Phase / implementation status:** [`STATUS.md`](STATUS.md)
- **Companion-tools build guide & live-test checklist:** [`COMPANION_TOOLS.md`](COMPANION_TOOLS.md)

---

## Applications

| App | Purpose |
|-----|---------|
| **HarryDataServer** | Main server: 14 camera TCP clients, 7-channel SPS server, measurement/settings/diagnostic pipelines, CSV export, collage, MSA engine, dark WPF dashboard. Writes to MySQL (`SettData`). |
| **HarryAnalysis** | Scan a DMC / SZID / VirtualSerial → load the part header and every measurement (value, limits, OK/NG). Scan-history list (last 20), sortable grid, export one part or **all** scans to CSV. |
| **HarryGraph** | Time-series of one or more measurements. 1–6 graph panels, per-panel multi-select, Live or fixed range, X-only zoom + Lock Y, point-by-point Min/Max envelope, full-screen detail window. |
| **HarryCounter** | NG error counter over a date range. Multi-level tree (grouping order chosen via dropdowns) **and** a bar chart, yield summary, live mode, CSV export. |
| **HarryLimitSample** | Scan a part → mark each measurement Should Pass / Should Fail / Ignore → save a per-module `MSA_<module>.json` reference for the MSA engine's LimitSample evaluation. |
| **HarryCollageCreator** | Visual editor for `Collage.ini`: add sample images, place/zoom/crop/mirror with a live composite (matches the server's renderer), save the layout. |

> The companion DB tools connect **read-only** with the `GetData` account.
> `HarrySimulator` is the customer's own test program (not part of this repo).

---

## Build

```
dotnet build HarryDataServer.sln -c Release
```

Target `net8.0-windows`, expected **0 warnings / 0 errors**. Each app's executable lands in:

```
<App>\bin\Release\net8.0-windows\<App>.exe
```

| App | Release executable |
|-----|--------------------|
| HarryDataServer | `HarryDataServer\bin\Release\net8.0-windows\HarryDataServer.exe` |
| HarryAnalysis | `HarryAnalysis\bin\Release\net8.0-windows\HarryAnalysis.exe` |
| HarryGraph | `HarryGraph\bin\Release\net8.0-windows\HarryGraph.exe` |
| HarryCounter | `HarryCounter\bin\Release\net8.0-windows\HarryCounter.exe` |
| HarryLimitSample | `HarryLimitSample\bin\Release\net8.0-windows\HarryLimitSample.exe` |
| HarryCollageCreator | `HarryCollageCreator\bin\Release\net8.0-windows\HarryCollageCreator.exe` |

These are **framework-dependent** builds — the target machine needs the **.NET 8 Desktop
Runtime**. For a self-contained, single-folder deployment use
`dotnet publish -c Release -r win-x64 --self-contained`.

---

## Configuration

All apps read **`Harry.ini`** from (in priority order):

1. the `HARRY_CONFIG_DIR` environment variable,
2. `F:\002_Configs`,
3. next to the executable,
4. legacy `D:\HarryDataServer`.

Relevant keys for the companion tools:

- `[MySQL] Server`, `Database` — connection target. Read-only login defaults to
  `GetData` / `1234Get`, overridable via `[MySQL] GetUser` / `GetPassword`.
- `[CSV] CSV_BasePath` — default folder for CSV exports.
- `[MSA] ReferencePath` — folder for the `MSA_<module>.json` LimitSample references.
- `[Collage] Collage_IniPath`, `Collage_SingleImages` — used by HarryCollageCreator.

---

## LimitSample reference model

- **One file per module:** `MSA_<module>.json` in `[MSA] ReferencePath`.
- Entries are keyed by measurement **`display_name`**; a module's file accumulates all its entries.
- **Add / edit:** scan a part, mark rows, *Save* (the `references` / xm block is preserved).
- **Remove an entry:** *Load existing* → set its row to **Ignore** → *Save* (Save writes only the
  non-Ignore rows, replacing the module's `limit_sample_expected`).

---

## Desktop shortcuts

Shortcuts to the six Release executables can be placed on the desktop (each carries its
embedded app icon). They keep working after rebuilds because the executable path is stable.

---

*Repo:* `https://github.com/CustomHelp/HarryDataServerV2` · branch `main`.
