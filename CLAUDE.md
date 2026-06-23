# CLAUDE.md — HarryDataServer V2
## Master Instruction File for Claude Code

> This file is the single source of truth for the entire project.
> Read it completely before writing any code, creating any file, or making any architectural decision.
> Update this file when specifications change.

---

## 1. Project Overview

**HarryDataServer V2** is an industrial data acquisition and quality management system for a 5-blade razor head production line. It runs on a Windows Server embedded in the production machine.

- **Framework:** C# WPF .NET 8.0
- **Database:** MySQL (local, data on drive E:\)
- **Configuration:** INI file (Harry.ini) + JSON template files per camera
- **Language:** All code, comments, variable names, log messages in **English**
- **Architecture:** Multi-threaded, one thread per camera client, ConcurrentQueues for DB writes

---

## 2. Production Line Layout

Two parallel production strands that merge at M50, then packaging:

```
Strand 1:  M10 → M20 → M30/M31/M32 ─┐
                                       ├─→ M50 (St160 Packaging)
Strand 2:  M11 → M21 → M33/M34/M35 ─┘
```

Everything that happens in Strand 1 happens identically in Strand 2.

### Module Descriptions

| Module | Function |
|--------|----------|
| M10 | Lubrastrip glued onto frame. ST30: glue points + frame cosmetics. ST60: lubrastrip position/cleanliness |
| M11 | Identical to M10 (Strand 2) |
| M20 | Trimmer Sub-Assembly preparation. ST60: KF1=top view (2 parts), KF3=side view (2 parts) |
| M21 | Identical to M20 (Strand 2) |
| M30-M35 | Blade assembly modules (no direct camera connection to our system) |
| M50 | Final assembly + full inspection. ST40, ST110(x2), ST120, ST130, ST140 |
| St160 | Packaging station — triggers our Part Exit event |

---

## 3. Camera Controllers (Keyence — we are always TCP Client)

| INI Key | Camera Name | Module | Station | IP | Port |
|---------|-------------|--------|---------|-----|------|
| Camera1 | M10_ST030_KF1 | M10 | ST30 | 172.29.10.30 | 8500 |
| Camera2 | M10_ST060_KF1 | M10 | ST60 | 172.29.10.60 | 8500 |
| Camera3 | M11_ST030_KF1 | M11 | ST30 | 172.29.11.30 | 8500 |
| Camera4 | M11_ST060_KF1 | M11 | ST60 | 172.29.11.60 | 8500 |
| Camera5 | M20_ST060_KF1 | M20 | ST60 | 172.29.20.61 | 8500 |
| Camera6 | M20_ST060_KF3 | M20 | ST60 | 172.29.20.62 | 8500 |
| Camera7 | M21_ST060_KF1 | M21 | ST60 | 172.29.21.61 | 8500 |
| Camera8 | M21_ST060_KF3 | M21 | ST60 | 172.29.21.62 | 8500 |
| Camera9 | M50_ST040_KF1 | M50 | ST40 | 172.29.50.40 | 8500 |
| Camera10 | M50_ST110_KF1 | M50 | ST110 | 172.29.50.111 | 8500 |
| Camera11 | M50_ST110_KF3 | M50 | ST110 | 172.29.50.112 | 8500 |
| Camera12 | M50_ST120_KF1 | M50 | ST120 | 172.29.50.120 | 8500 |
| Camera13 | M50_ST130_KF1 | M50 | ST130 | 172.29.50.130 | 8500 |
| Camera14 | M50_ST140_KF1 | M50 | ST140 | 172.29.50.140 | 8500 |

> All cameras use port 8500.
> Subnet mask: 255.255.0.0 throughout.
> Number of cameras is dynamic — read from INI, never hardcode camera count.

---

## 4. Camera Telegram Protocol

### General Rules
- Delimiter: comma (`,`)
- Decimal separator: dot (`.`)
- End of telegram: carriage return (`\r`)
- TCP buffer: 8192 bytes
- Reconnect strategy: exponential backoff (3s, 6s, 12s, max 60s)
- Keepalive: continuously send version variable request; if no response → camera offline

### Telegram Header (positions 0–3, identical for all types)

| Position | Content | Example |
|----------|---------|---------|
| 0 | Controller name | `M50_ST110_KF1` |
| 1 | Camera program version | `1.2.3` |
| 2 | Signal word | `Results` / `Settings` / `Diagnostic` |
| 3 | Operating mode | `Normal` / `MSA1` / `MSA3` / `LimitSample` |

### Serial Number Fields (positions 4–67)

| Positions | Normal Mode | MSA Modes |
|-----------|-------------|-----------|
| 4–35 (32 chars) | SZID (frame serial) or Virtual Serial (M20/M21) | BaseID (14 chars) + 3-digit loop counter |
| 36–67 (32 chars) | Virtual Serial (M20/M21) / empty otherwise | DMC code of test part |

> In MSA/LimitSample mode the BaseID lives in the **first** serial field (positions 4–35),
> immediately followed by the 3-digit loop counter (e.g. `10260623083000` + `001`); the DMC
> of the test part is in the **second** serial field (positions 36–67).

### Telegram Types (detected by signal word at position 2)

#### "Results" Telegram
- Positions 71+: alternating `R_` (SINT result) and `V_` (Float value) pairs
- Structure defined per camera in `Result_CameraName.json`

#### "Settings" Telegram
- Sent at controller startup or when limits change
- Positions 3+: Min/Max pairs per ParameterSet
- Structure defined per camera in `Settings_CameraName.json`

#### "Diagnostic" Telegram
- Special results → write directly to CSV only (not to main DB)
- Do not process through normal measurement pipeline

### Result Codes (R_ values)

| Value | Meaning |
|-------|---------|
| -2 | Not Validated |
| -1 | Position Adjustment Error |
| 0 | Result BAD |
| 1 | Result GOOD |
| 2 | Not Evaluated (deactivated) |

### Variable Naming Convention

| Prefix | Type | DB Column | Format |
|--------|------|-----------|--------|
| `R_` | Result | result_status | SINT (5 digits) |
| `V_` | Value | measurement_value | Float (2 decimal) |
| `SET_MIN_` | Setting minimum | min_value | Float (2 decimal) |
| `SET_MAX_` | Setting maximum | max_value | Float (2 decimal) |
| `SET_EVA_` | Evaluation on/off | — | Int (0/1) |
| `CNT_` | Counter | — | Int |

---

## 5. SPS / PLC Connections (we are always TCP Server)

**7 channels, same IP, different ports. All ports configurable in Harry.ini.**

### Channel 1 — KeepAlive / Status
- PLC connects, sends telegram, we mirror it back
- **On success:** mirror + camera status string (one `1`/`0` per camera, semicolon-separated, in INI order)
- **On error:** different response + current error description in plain English
- Example response: `<mirrored_telegram>;1;1;0;1;1;1;0;1;1;1;1;1;1;1`

### Channel 2 — Part Exit (St160 Packaging)
Telegram fields (semicolon-separated):

| Field | Content |
|-------|---------|
| DMC | DataMatrix code lasered on part |
| SZID | Frame serial number (32 chars) |
| VirtualSerial | Trimmer serial number (32 chars) |
| OrderName | Current production order name |
| Mode | `Normal` / `MSA1` / `MSA3` / `LimitSample` |
| M1X_Module | Which M1x module (10 or 11) |
| M1X_Nest | Nest number in M1x |
| M3X_Module | Which M3x blade module |
| M3X_Nest | Nest number in M3x |
| M50_Nest | Nest number in M50 |
| Humidity | Humidity value from M1x (float) |
| ResultStatus | `OK` / `NG` / `DE` (deleted) |

**Triggers after receiving:** CSV export, Collage generation, MSA evaluation (if MSA mode)

### Channels 3–7 — MSA Evaluation Trigger

| Channel | Module |
|---------|--------|
| 3 | M10 |
| 4 | M11 |
| 5 | M20 |
| 6 | M21 |
| 7 | M50 |

**Request telegram:** `Request;<BaseID>` — `<BaseID>` is the bare **14-char** BaseID (no loop
counter). The completion handler aggregates `msa_measurements` on an **exact** `base_id`
match, scoped by `controller_name` (module) for safety.
**Responses:**
- `Wait` — currently processing, try again
- `Error;<description>` — error occurred
- `OK` — MSA passed
- `NG` — MSA failed

---

## 6. Serial Number Concept

### Normal Mode
- **M10/M11:** SZID (frame identity) in positions 4–35. Positions 36–67 empty.
- **M20/M21:** Virtual Serial (trimmer identity) in positions 4–35. Positions 36–67 empty.
- **M50:** SZID in positions 4–35. Positions 36–67 empty.
- **Part Exit (Ch2):** All three known: DMC + SZID + VirtualSerial.

### MSA Modes (MSA1, MSA3, LimitSample)
- Positions 4–35: BaseID (14 chars) + 3-digit loop counter
- Positions 36–67: DMC of test part

### BaseID Format (14 characters: `10260623083000` = M10, 2026-06-23, 08:30:00)

| Field | Chars | Example |
|-------|-------|---------|
| Module | 2 | `10` |
| Year | 2 | `26` |
| Month | 2 | `06` |
| Day | 2 | `23` |
| Hour | 2 | `08` |
| Minute | 2 | `30` |
| Second | 2 | `00` |

The BaseID (14 chars) stays constant across all stations for one loop of a run. During
the run, **each loop telegram appends a 3-digit loop counter** to the BaseID in the serial
field: loop 1 → `…001` (17 chars total), loop 2 → `…002`, etc. The counter increments each
time the run cycles through again. In storage the **`base_id` column holds only the 14-char
BaseID**, and the loop counter is parsed out into the integer **`loop_number`** column.

When the run completes, the SPS sends the completion signal on the MSA channel as
`Request;<BaseID>` — **the bare 14-char BaseID, with no loop counter** (CLAUDE.md §5).
There is no longer any "MoverNumber" / TrayRow / TrayCol field.

### Image Filename Search
Image filenames start with a 12-character abbreviated serial + underscore.
When searching for images, use only the **first 12 characters** of the SZID.

---

## 7. MSA Calculations

### Pass Criteria Summary

| Test | Pass Condition |
|------|---------------|
| MSA1 Cg | ≥ 1.33 |
| MSA1 Cgk | ≥ 1.33 |
| MSA3 %Tolerance | ≤ 20% |
| LimitSample | 100% of prepared errors must be rejected |

### MSA1 Formulas (50 measurements of 1 part)

```
Tolerance = USL - LSL  (from Settings/Grenzen for that measurement)
σ = StdDev of all 50 measured values

Cg  = (0.20 × Tolerance) / (6 × σ)
Cgk = ((0.20 × Tolerance) - |x̄ - xm|) / (6 × σ)

where:
  x̄  = mean of all 50 measurements
  xm = reference value from MSA JSON reference file
```

### MSA3 Formula (parts × repeated measurements each)

```
Tolerance = USL - LSL

For each part i: x̄i = mean of its measurements
SumSquares = ΣΣ(x̄i - xij)²   (sum over all parts and all measurements)
DegreesOfFreedom = Σ over parts (measurementsPerPart − 1)

%Tolerance = 6 × √(SumSquares / DegreesOfFreedom) / Tolerance
```

> The classic layout is 32 parts × 3 measurements → DoF = 32 × (3 − 1) = 64. **The number
> of parts and loops is controlled entirely by the SPS/PLC and is never hardcoded** — the
> implementation (`MsaCalculator.Msa3`) computes DoF dynamically as
> Σ(measurementsPerPart − 1) over the parts actually present, so it is correct for any
> sample/loop count (e.g. 30 × 3 → DoF = 60). Parts are grouped by DMC; loops are the
> repeated measurements of one part.

### MSA JSON Reference Files
- Location: configurable per module in Harry.ini
- One file per module per MSA version
- Contains: `reference_value` (xm) per measurement name for MSA1
- Contains: expected pass/fail per measurement for LimitSample

### Database Strategy for MSA
- Use **separate table** `msa_measurements` (identical structure to `measurements_serial`)
- Unique business key: DMC + BaseID + loop_number + controller_name
- Primary key: always `BIGINT AUTO_INCREMENT`

---

## 8. Database Schema

### Network
```
Server (this machine):  172.29.1.5   / 255.255.0.0
NAS:                    172.29.1.6   / 255.255.0.0
All cameras:            Port 8500
SPS channels:           Ports 6000–6006
```

### Connection
```
Server:   localhost
Database: camera_data
DataDir:  E:\MySQL\Data\
```

### Database Startup Logic (runs every application start)

```
1. Connect to MySQL (retry with exponential backoff if not available)
2. CREATE DATABASE IF NOT EXISTS camera_data
3. For each table: CREATE TABLE IF NOT EXISTS (full schema)
4. For each table: Schema-Check
   → Query INFORMATION_SCHEMA.COLUMNS
   → Compare with expected columns
   → If column missing: ALTER TABLE ADD COLUMN automatically
   → Log every schema change to Serilog
5. Partition-Check for measurements_serial + measurements_serial_trimmer
   → Check if partitions exist for current month + next 3 months
   → If missing: CREATE partition automatically
6. Camera sync: INSERT or UPDATE cameras table from INI config
7. Definition sync: INSERT or UPDATE measurement_definitions + setting_definitions from JSON files
   → Set effective_end on removed definitions
   → Log all changes
```

Adding a new column only requires a code change — software detects and applies it automatically on next start. No manual SQL, no production stop.

### All tables must be created automatically at application startup if not present.

---

### Table: `cameras`
```sql
CREATE TABLE IF NOT EXISTS cameras (
  id           INT AUTO_INCREMENT PRIMARY KEY,
  camera_name  VARCHAR(100) NOT NULL,
  module       VARCHAR(10)  NOT NULL,
  ip_address   VARCHAR(15)  NOT NULL,
  port         INT          NOT NULL,
  active       TINYINT      NOT NULL DEFAULT 1,
  created_at   DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
  UNIQUE KEY uk_camera_name (camera_name)
);
```

### Table: `measurement_definitions`
```sql
CREATE TABLE IF NOT EXISTS measurement_definitions (
  id              INT AUTO_INCREMENT PRIMARY KEY,
  camera_id       INT          NOT NULL,
  telegram_place  INT          NOT NULL,
  variable_name   VARCHAR(100) NOT NULL,
  display_name    VARCHAR(100) NOT NULL,
  var_type        VARCHAR(10)  NOT NULL,  -- 'Result' or 'Value'
  parameter_set   INT          NOT NULL,
  module_ref      VARCHAR(10)  NOT NULL DEFAULT 'NoRef',
  feature_group   VARCHAR(100) NOT NULL DEFAULT 'NoGroup',
  effective_from  DATE         NOT NULL,
  effective_end   DATE,
  FOREIGN KEY (camera_id) REFERENCES cameras(id)
);
```

### Table: `setting_definitions`
```sql
CREATE TABLE IF NOT EXISTS setting_definitions (
  id             INT AUTO_INCREMENT PRIMARY KEY,
  camera_id      INT          NOT NULL,
  telegram_place INT          NOT NULL,
  setting_name   VARCHAR(100) NOT NULL,
  parameter_set  INT          NOT NULL,
  limit_type     VARCHAR(5)   NOT NULL,  -- 'Min' or 'Max'
  FOREIGN KEY (camera_id) REFERENCES cameras(id)
);
```

### Table: `settings` (limit history)
```sql
CREATE TABLE IF NOT EXISTS settings (
  id             INT AUTO_INCREMENT PRIMARY KEY,
  camera_id      INT      NOT NULL,
  definition_id  INT      NOT NULL,
  parameter_set  INT      NOT NULL,
  limit_value    DOUBLE   NOT NULL,
  version        VARCHAR(20),
  recorded_at    DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  INDEX idx_camera_recorded (camera_id, recorded_at),
  FOREIGN KEY (camera_id) REFERENCES cameras(id),
  FOREIGN KEY (definition_id) REFERENCES setting_definitions(id)
);
```

### Table: `dmcserial` (one row per finished part)
```sql
CREATE TABLE IF NOT EXISTS dmcserial (
  id              INT AUTO_INCREMENT PRIMARY KEY,
  serial_number   VARCHAR(50)   NOT NULL,
  serial_trimmer  VARCHAR(50),
  dmc             VARCHAR(50),
  m1x_module      TINYINT,
  m1x_nest        INT,
  m2x_module      TINYINT,
  m2x_nest        INT,
  m3x_module      VARCHAR(10),
  m3x_nest        VARCHAR(10),
  m50_nest        VARCHAR(10),
  order_name      VARCHAR(100),
  m1x_temperature DOUBLE,
  m1x_humidity    DOUBLE,
  result_status   TINYINT      NOT NULL DEFAULT 0,  -- 1=OK, 0=NG, -1=deleted
  created_at      DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
  UNIQUE KEY uk_serial (serial_number),
  INDEX idx_dmc (dmc),
  INDEX idx_trimmer (serial_trimmer),
  INDEX idx_order (order_name),
  INDEX idx_created (created_at)
);
```

### Table: `measurements_serial` (PARTITIONED by day)
```sql
CREATE TABLE IF NOT EXISTS measurements_serial (
  id                 BIGINT   NOT NULL AUTO_INCREMENT,
  serial_number      VARCHAR(50) NOT NULL,
  definition_id      INT      NOT NULL,
  measurement_value  DOUBLE,
  measurement_string VARCHAR(20),
  result_status      TINYINT,
  run_type           TINYINT  NOT NULL DEFAULT 0,  -- 0=Normal,1=MSA1,2=MSA3,3=LimitSample,4=GoldenSample
  measured_at        DATETIME NOT NULL,
  PRIMARY KEY (id, measured_at),
  INDEX idx_serial (serial_number),
  INDEX idx_def (definition_id),
  INDEX idx_measured (measured_at)
) PARTITION BY RANGE (TO_DAYS(measured_at)) (
  PARTITION p_2026_06 VALUES LESS THAN (TO_DAYS('2026-07-01')),
  PARTITION p_2026_07 VALUES LESS THAN (TO_DAYS('2026-08-01')),
  PARTITION p_2026_08 VALUES LESS THAN (TO_DAYS('2026-09-01')),
  PARTITION p_2026_09 VALUES LESS THAN (TO_DAYS('2026-10-01')),
  PARTITION p_2026_10 VALUES LESS THAN (TO_DAYS('2026-11-01')),
  PARTITION p_2026_11 VALUES LESS THAN (TO_DAYS('2026-12-01')),
  PARTITION p_2026_12 VALUES LESS THAN (TO_DAYS('2027-01-01')),
  PARTITION p_future  VALUES LESS THAN MAXVALUE
);
```

### Table: `measurements_serial_trimmer` (PARTITIONED, same structure)
Same as `measurements_serial` but uses `serial_trimmer` instead of `serial_number`.
For M20/M21 measurements only.

### Table: `msa_measurements` (MSA runs — separate from production)
```sql
CREATE TABLE IF NOT EXISTS msa_measurements (
  id                 BIGINT      NOT NULL AUTO_INCREMENT,
  dmc                VARCHAR(50) NOT NULL,
  base_id            VARCHAR(50) NOT NULL,  -- 14-char BaseID (MMYYMMDDHHmmSS); never includes the loop counter
  loop_number        INT         NOT NULL,  -- 3-digit per-loop counter parsed from the run serial field
  controller_name    VARCHAR(100) NOT NULL,
  definition_id      INT         NOT NULL,
  measurement_value  DOUBLE,
  measurement_string VARCHAR(20),
  result_status      TINYINT,
  msa_type           VARCHAR(20) NOT NULL,  -- 'MSA1', 'MSA3', 'LimitSample'
  msa_version        VARCHAR(50),
  measured_at        DATETIME    NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (id),
  INDEX idx_dmc_baseid (dmc, base_id),
  INDEX idx_baseid_controller (base_id, controller_name),  -- exact-match completion lookup
  INDEX idx_controller (controller_name),
  INDEX idx_measured (measured_at)
);
```

### Table: `msa_results` (computed MSA evaluation results)
```sql
CREATE TABLE IF NOT EXISTS msa_results (
  id              INT AUTO_INCREMENT PRIMARY KEY,
  controller_name VARCHAR(100) NOT NULL,
  dmc             VARCHAR(50)  NOT NULL,
  base_id         VARCHAR(50)  NOT NULL,
  msa_type        VARCHAR(20)  NOT NULL,
  msa_version     VARCHAR(50),
  definition_id   INT          NOT NULL,
  display_name    VARCHAR(100),
  cg_value        DOUBLE,       -- MSA1 only
  cgk_value       DOUBLE,       -- MSA1 only
  pct_tolerance   DOUBLE,       -- MSA3 only
  passed          TINYINT      NOT NULL,
  evaluated_at    DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
  INDEX idx_dmc (dmc),
  INDEX idx_controller_type (controller_name, msa_type)
);
```

### Partition Management
- **Retention:** Drop partitions older than configured days using `ALTER TABLE DROP PARTITION`
- **New partitions:** Create monthly partitions automatically (background task, runs at startup and monthly)
- **Never use DELETE for retention** on partitioned tables — always DROP PARTITION

### Database Users
```
SettData / 1234Set  → full DDL + DML, localhost only (main application)
GetData  / 1234Get  → SELECT only, network access (Power BI, analysis tools)
```
> Customer will change passwords after deployment.

### Post-Install SQL (run once after MySQL installation)
```sql
-- Fix SettData to localhost only
DROP USER 'SettData'@'%';
CREATE USER 'SettData'@'localhost' IDENTIFIED BY '1234Set';
GRANT ALL PRIVILEGES ON camera_data.* TO 'SettData'@'localhost';

-- Restrict GetData to SELECT only
GRANT SELECT ON camera_data.* TO 'GetData'@'%';

FLUSH PRIVILEGES;
```

### MySQL Performance Settings (my.ini on E:\MySQL\Data\)
Add these to [mysqld] section after installation:
```ini
# Memory - server has 64GB RAM
innodb_buffer_pool_size         = 32G
innodb_buffer_pool_instances    = 8
innodb_log_file_size            = 1G
innodb_flush_log_at_trx_commit  = 2
innodb_flush_method             = O_DIRECT

# Connections
max_connections                 = 200
thread_cache_size               = 16

# Performance
innodb_io_capacity              = 2000
innodb_io_capacity_max          = 4000
innodb_read_io_threads          = 8
innodb_write_io_threads         = 8

# Partitioning
innodb_file_per_table           = 1
```

---

## 9. JSON Template File Format

Location: configurable per camera in Harry.ini (`JsonParameters=` and `JsonSettings=`)

### Result JSON (`Result_CameraName.json`)
```json
{
  "camera": "M50_ST110_KF1",
  "signal_word": "Results",
  "measurements": [
    {
      "telegram_place": 71,
      "variable_name": "R_Anode_Flatness_L",
      "display_name": "Anode_Flatness_L",
      "type": "Result",
      "format": "SINT",
      "parameter_set": 1,
      "module_ref": "NoRef",
      "feature_group": "Anode Measured"
    },
    {
      "telegram_place": 72,
      "variable_name": "V_Anode_Flatness_L",
      "display_name": "Anode_Flatness_L",
      "type": "Value",
      "format": "Float",
      "parameter_set": 1,
      "module_ref": "NoRef",
      "feature_group": "Anode Measured"
    }
  ]
}
```

### Settings JSON (`Settings_CameraName.json`)
```json
{
  "camera": "M50_ST110_KF1",
  "signal_word": "Settings",
  "settings": [
    {
      "telegram_place": 3,
      "setting_name": "Anode_Height_Min",
      "parameter_set": 1,
      "limit_type": "Min",
      "format": "Float"
    }
  ]
}
```

### JSON Loader Behavior at Startup
1. Load all JSON files specified in INI
2. For each camera: INSERT or UPDATE `measurement_definitions` and `setting_definitions`
3. Use `effective_from` / `effective_end` for historical tracking
4. Log any changes to definition names or telegram places

---

## 10. INI Configuration (Harry.ini)

> **Config location:** All configuration lives in the central folder `F:\002_Configs`
> (Harry.ini, the `Templates\` subfolder with the JSON files, and later Collage.ini /
> MSA references). The application looks for Harry.ini in this order:
> `HARRY_CONFIG_DIR` env var → `F:\002_Configs` → next to the executable →
> legacy `D:\HarryDataServer`.
> **Relative paths** in Harry.ini (e.g. `Templates\Result_*.json`) are resolved
> against the directory that contains Harry.ini, so the whole config folder is portable.

```ini
[General]
LogFilePath=D:\HarryDataServer\Logs\
LoggingActive=true
Language=English

[MySQL]
Server=localhost
Database=camera_data
User=SettData
Password=1234Set
RetentionPeriodDays=35

[CSV]
CSV_BasePath=Y:\02_CSV_Merge
CSV_MSAPath=Y:\01_CSV_Evaluation
CSV_DiagnosticPath=Y:\03_CSV_Diagnostic
DataSetsPerFile=10000
CSV_Save=true
CSVMSA_Save=true
CSVDiagnostic_Save=true

[NAS]
NAS_BasePath=Z:\Images
LowResIndividualPath=Z:\Images\01_Low_Resolution_Individual\Input
CollagePath=Z:\Images\02_Collage\Input
HighResNGPath=Z:\Images\03_High_Resolution_NG\Input
HighResDiagnosticPath=Z:\Images\04_High_Resolution_Diagnostic\Input
HighResGoldenSamplePath=Z:\Images\05_High_Resolution_GoldenSample\Input
FullResRetentionDays=30   ; default full-res retention (SOW §5.2.3); per-type keys fall back to it
RetentionNGDays=30
RetentionDiagnosticDays=30
RetentionGoldenSampleDays=30
DeleteAfterCollage=true

[Collage]
Collage_IniPath=D:\HarryDataServer\Collage.ini
Collage_Generate=true
MaxFileSizeKB=128         ; max collage output size in KB (SOW §5.2.2)

[MSA]
ReferencePath=MSA_References   ; per-module MSA_<module>.json reference files
ReportPath=MSA_Reports         ; MSA PDF report output (SOW §3.2.1); empty → ReferencePath\Reports

[SPS]
IP=172.29.1.5
PortKeepAlive=6000
PortPartExit=6001
PortMSA_M10=6002
PortMSA_M11=6003
PortMSA_M20=6004
PortMSA_M21=6005
PortMSA_M50=6006
AutoConnect=true

[SQLSettings]
BatchSize=100
SaveIntervalSeconds=1

[Camera1]
CameraName=M10_ST030_KF1
IP=172.28.10.30
Port=8001
JsonParameters=Templates\Result_M10_ST030_KF1.json
JsonSettings=Templates\Settings_M10_ST030_KF1.json
AutoConnect=true

; ... Camera2 through Camera14 follow same pattern
```

---

## 11. Image Management

### NAS Folder Structure
```
Z:\Images\
  01_Low_Resolution_Individual\Input\   → OK images (source for collage)
  02_Collage\Input\                     → finished collages
  03_High_Resolution_NG\Input\          → NG images
  04_High_Resolution_Diagnostic\Input\  → Diagnostic images
  05_High_Resolution_GoldenSample\Input\ → GoldenSample images
```
NAS auto-sorts images into date subfolders (YYYY\MM\DD).

### Image Filename Format
`123456789012_12345678901234567890_1_M50_ST120_KF1_1_&Cam1Img.bmp`

| Part | Meaning |
|------|---------|
| `123456789012` | First 12 chars of serial (with underscore after char 12) |
| `12345678901234567890` | Serial number continuation |
| `1` | Camera result (1=OK, 0=NG) |
| `M50_ST120_KF1` | Controller name |
| `1` | Image index |
| `&Cam1Img` | Image variable name |

**Image search key:** use only the first 12 characters of SZID.

### Delete Logic
- **NG / Diagnostic / GoldenSample images:** auto-delete after configured days
- **OK images:** delete after collage is created (if configured)
- **OK images of NG final part:** delete immediately when Part Exit result = NG (images from M10/M20 that are now orphaned)
- **Age-based cleanup:** delete any images older than configured retention regardless of type

---

## 12. Collage Generator

- Triggered on Part Exit when result = OK
- Search for images by first 12 chars of SZID and VirtualSerial
- Layout defined by `Collage.ini` (created by separate CollageCreator tool)
- Output to NAS `02_Collage\Input\`
- Run as background task — must not block main thread

### Collage.ini structure (unchanged from V1)
```ini
[CollageSettings]
CanvasWidth=320
CanvasHeight=650
BackgroundColor=White

[Image1]
TemplateName=<serial_pattern>_M50_ST120_KF1_1_&Cam1Img.bmp
Pos_X=160
Pos_Y=339
Scale=1
Zoom=1.1
Crop_X=16
Crop_Y=42
Crop_Width=282
Crop_Height=147
Mirror_X=false
Mirror_Y=false
KeyName=M50_ST120_KF1
```

---

## 13. CSV Export

### Main CSV (triggered on Part Exit)
- One row per finished part containing ALL measurement values from ALL cameras
- Header: dynamic from `measurement_definitions` table
- Missing values (camera was offline): empty column
- File rotation: on order name change OR when `DataSetsPerFile` rows reached
- Filename: `YYYY-MM-DD-HH-mm-OrderName.csv`
- Path: `CSV_BasePath\YYYY\MM\DD\`

### MSA/Evaluation CSV
- Exported on MSA evaluation completion
- Contains: Cg, Cgk (MSA1) or %Tolerance (MSA3) per measurement
- Failed measurements highlighted in export (add column `Passed` 0/1)

### Diagnostic CSV
- Written immediately on each Diagnostic telegram (no waiting for Part Exit)
- File rotation: on `DataSetsPerFile` rows

---

## 14. Application Architecture

### Solution Structure
```
HarryDataServer.sln
└── HarryDataServer/                    (WPF .NET 8.0)
    ├── App.xaml / App.xaml.cs          (DI container setup)
    ├── MainWindow.xaml/.cs             (tab layout)
    ├── Views/                          (additional windows)
    ├── Controls/
    │   ├── ucCameraControl.xaml/.cs    (one per camera, dynamic)
    │   ├── ucSpsControl.xaml/.cs       (7-channel SPS server)
    │   ├── ucDatabaseControl.xaml/.cs  (DB status + stats)
    │   ├── ucCsvControl.xaml/.cs       (CSV export status)
    │   ├── ucCollageControl.xaml/.cs   (collage generator)
    │   └── ucMsaControl.xaml/.cs       (MSA tab per module)
    ├── Services/
    │   ├── IConfigService.cs + IniConfigService.cs
    │   ├── IDatabaseService.cs + MySqlDatabaseService.cs
    │   ├── ICsvService.cs + CsvService.cs
    │   ├── ICollageService.cs + CollageService.cs
    │   ├── IMsaService.cs + MsaService.cs
    │   ├── IImageCleanupService.cs + ImageCleanupService.cs
    │   └── ILogService.cs + SerilogService.cs
    ├── Communication/
    │   ├── TcpCameraClient.cs          (one instance per camera)
    │   ├── TcpSpsServer.cs             (7 listeners)
    │   └── TelegramParser.cs           (parses all 3 telegram types)
    ├── Models/
    │   ├── CameraConfig.cs
    │   ├── MeasurementDefinition.cs
    │   ├── Measurement.cs
    │   ├── Setting.cs
    │   ├── SpsPartExitData.cs
    │   ├── MsaRunData.cs
    │   └── BaseId.cs
    ├── Configuration/
    │   ├── IniConfigManager.cs
    │   └── JsonTemplateLoader.cs
    ├── Infrastructure/
    │   ├── MySqlRepository.cs
    │   ├── PartitionManager.cs
    │   └── CsvWriter.cs
    └── Resources/
        └── Templates/                  (JSON files)
```

### Threading Model

| Thread/Task | Responsibility | Priority |
|-------------|---------------|----------|
| UI Thread (STA) | WPF rendering | Normal |
| TcpCameraClient × N | One per camera, receive + enqueue | AboveNormal |
| MeasurementProcessor | ConcurrentQueue → DB | Normal |
| SettingsProcessor | ConcurrentQueue → DB | BelowNormal |
| DiagnosticProcessor | ConcurrentQueue → CSV | BelowNormal |
| TcpSpsServer × 7 | SPS channel listeners | AboveNormal |
| PartExitProcessor | CSV + Collage + MSA trigger | Normal |
| MsaCalculator × 5 | Per-module MSA evaluation | BelowNormal |
| RetentionJob | DB partition drop + image delete | Lowest |
| PartitionManager | Create future monthly partitions | Lowest |

### Key Rules
- **One MySQL connection per thread** — no shared connections
- **ConcurrentQueue<T>** for all inter-thread data passing
- **Never block camera receive thread** with DB or file I/O
- **Dispatcher.Invoke** only for UI updates from background threads
- **isProcessing flag** per processor to prevent duplicate processing tasks

### Dependency Injection
All services registered in `App.xaml.cs` as Singleton:
```csharp
services.AddSingleton<IConfigService, IniConfigService>();
services.AddSingleton<IDatabaseService, MySqlDatabaseService>();
services.AddSingleton<ICsvService, CsvService>();
services.AddSingleton<ICollageService, CollageService>();
services.AddSingleton<IMsaService, MsaService>();
services.AddSingleton<IImageCleanupService, ImageCleanupService>();
services.AddSingleton<ILogService, SerilogService>();
services.AddTransient<TcpCameraClient>();
```

---

## 15. NuGet Packages

| Package | Version | Purpose |
|---------|---------|---------|
| MySqlConnector | 2.x | MySQL (async-capable, replaces MySql.Data) |
| Serilog | 3.x | Structured logging |
| Serilog.Sinks.File | 5.x | Log to file |
| Microsoft.Extensions.DependencyInjection | 8.x | DI container |
| Microsoft.Extensions.Hosting | 8.x | Host builder |
| IniParser | 3.x | INI file reading |
| System.Text.Json | built-in | JSON template files |
| OxyPlot.Wpf | 2.x | Charts in MSA view and graph tool |
| CommunityToolkit.Mvvm | 8.x | MVVM helpers |
| QuestPDF | 2026.x | MSA PDF report generation (Community licence; SOW §3.2.1) |

**Do NOT use:**
- `MySql.Data` (use MySqlConnector instead)
- `ClosedXML` or `DocumentFormat.OpenXml` (Excel replaced by JSON)

---

## 16. Companion Tools (separate WPF applications, same solution)

### HarryAnalysis — Scanner Tool
- Operator scans DMC barcode
- Fetch all data for that part from DB
- Display: measurements + names + limits + general info (humidity etc.) + results
- Export to CSV
- DB user: `GetData` (read-only)

### HarryGraph — Measurement Graph
- Select one or more measurements from DB definition list
- Display as time-series chart (OxyPlot)
- Modes: Live (auto-refresh) or fixed time range
- Zoom/pan in chart, print option
- Save/load graph configurations as JSON

### HarryMSA — MSA Analysis Tool (or integrate as tab in main app)
- Per-module view of MSA runs
- Table of Cg/Cgk/%Tolerance per measurement
- Red highlight on failed measurements
- Show what caused CGK to fail
- Export to CSV

### HarryCounter — Error Counter (port from RazorErrorCount)
- Count NG parts by time period
- Group by error category (from JSON: FeatureGroup)
- Group by nest number
- Live view + historical

### HarryCollageCreator — Collage Layout Editor
- Visual editor for Collage.ini
- Place, zoom, crop, mirror images on canvas
- Save as Collage.ini

### HarryLimitSample — LimitSample Editor
- Scan a part DMC → load all its measurements from DB
- Mark each measurement as "should pass" / "should fail" / "ignore"
- Save as LimitSample JSON reference file
- Manage (add/delete) entries

---

## 17. Build & Deploy

### Build
```
dotnet build HarryDataServer.sln --configuration Release
```

### Deploy
1. Copy `Release` output to server
2. Place config in the central folder `F:\002_Configs`: `Harry.ini`, `Collage.ini`,
   and the `Templates\` subfolder with the JSON files (template paths are relative).
3. (Optional) Set `HARRY_CONFIG_DIR` to override the config folder location.
4. Application creates DB and all tables on first startup
5. Customer changes DB passwords after successful test

### Git Repository
`https://github.com/CustomHelp/HarryDataServerV2`

Commit message convention:
```
feat: add TcpCameraClient with reconnect logic
fix: handle empty VirtualSerial in M50 telegram
db: add msa_results table
config: update Harry.ini template
```

---

## 18. Implementation Order

Build in this sequence — each phase must compile before starting the next:

1. **Solution skeleton** — project files, DI setup, IniConfigManager, SerilogService
2. **JSON Loader + DB Schema** — JsonTemplateLoader, MySqlRepository, all CREATE TABLE
3. **Camera TCP Client** — TcpCameraClient, TelegramParser (one camera, test with M50_ST110_KF1)
4. **Measurement pipeline** — ConcurrentQueue → MeasurementProcessor → DB insert
5. **Settings pipeline** — SettingsProcessor → DB insert
6. **SPS Server** — TcpSpsServer, all 7 channels, KeepAlive + PartExit
7. **CSV Export** — CsvService, all 3 types, file rotation
8. **Image Cleanup** — ImageCleanupService, retention jobs
9. **Collage** — CollageService, Collage.ini reader
10. **MSA Engine** — MsaService, Cg/Cgk/%Tolerance calculations
11. **UI — Main Window + UserControls** — all ucXxx controls, MVVM bindings
12. **MSA UI** — ucMsaControl, results display, CSV export
13. **Integration** — all cameras running parallel, load test
14. **Companion tools** — HarryAnalysis, HarryGraph, HarryCounter (can start earlier in parallel)

---

*Last updated: 2026-06-22*
*Authors: Customer + Claude Sonnet 4.6*

---

## 19. SOW Compliance — Open Items & Known Gaps

> Source documents: `MP2 Vision SOW 2025-11-05.pdf` + `M1X Inspection Addendum 2026-02-18.docx`
> Gap analysis performed 2026-06-22. Items marked CRITICAL must be resolved before FAT.
> Items marked PRE-SAT can be deferred but must be planned now.

---

### 19.1 CRITICAL — Must fix before FAT

#### 19.1.1 Collage file size limit (SOW §5.2.2) — ✅ DONE (2026-06-22)
**Requirement:** Each collage must not exceed **128 KB**.
**Implemented:** `CollageComposer` re-encodes JPEG at iteratively lower quality (start 85, step −5, min 30) until the output is ≤ the limit; a WARNING is logged if the minimum quality is reached and the file still exceeds the limit. The limit is configurable via `[Collage] MaxFileSizeKB` (default 128).
**File:** `HarryDataServer/Infrastructure/CollageComposer.cs`, `Services/CollageService.cs`

#### 19.1.2 MSA PDF reports (SOW §3.2.1) — ✅ DONE (2026-06-22)
**Requirement:** At the end of every MSA/LimitSample run, generate **2 PDF reports**:
- Report 1: All measurement results (name, expected, actual, pass/fail)
- Report 2: Only failed entries

**Implemented:** `PdfReportService` (QuestPDF, Community licence) generates both reports after every evaluation in `MsaService`. Files: `<Module>_<Type>_<DDMMYY_HHMMSS>_AllResults.pdf` and `_FailuresOnly.pdf`, written to `[MSA] ReportPath` (fallback `[MSA] ReferencePath\Reports`). Layout: header (module, type, run datetime, overall PASS/FAIL), table (Measurement | Expected | Actual | Cg/Cgk or %P/T | Pass/Fail), footer (generated-by + timestamp + page). The MSA tab has **Open All Results** / **Open Failures Only** buttons that open the PDF (generating on demand from the loaded run if it does not yet exist).
**File:** `HarryDataServer/Services/PdfReportService.cs`, `MsaService.cs`, `Controls/ucMsaControl.xaml`

#### 19.1.3 M1X LimitSample batch confirmation flow (Addendum §2.2)
**Requirement:** M1X LimitSample run is NOT terminated automatically after a fixed number of parts. After each set of 4 parts is measured, the **operator must confirm** whether more parts are coming. Only after "no more parts" confirmation does the run complete.
**Current state:** Not implemented — our LimitSample run ends after a fixed cycle count.
**Action:** SPS channel for M1X LimitSample needs a protocol extension: after each batch of 4, send a "ready for next batch / end run?" prompt back to PLC/operator. Clarify the exact SPS signal with Harry's (which channel, what message format). This may require a new SPS channel command type.
**File:** `HarryDataServer/Communication/TcpSpsServer.cs`, `SpsChannel.cs`

---

### 19.2 PRE-SAT — Must plan, can implement after FAT

#### 19.2.1 Shift counter with PLC reset signal (SOW §4.3)
**Requirement:** Three counter types per failure group:
1. **Shift Counter** — resets on PLC "Reset" signal at shift change
2. **Resettable Counter** — resets on operator demand (any time)
3. **Last-Shift Counter** — snapshot of previous shift's counts

**Current state:** HarryCounter tool counts NG in a date range but has no shift-reset concept.
**Action:** Add a new SPS command type (e.g. on Ch1 KeepAlive or a dedicated channel) for `RESET_SHIFT_COUNTER`. Store shift-reset timestamps in a new `shift_events` table. HarryCounter reads counts between consecutive reset events. Discuss exact PLC signal format with Harry's.

#### 19.2.2 Failure Warnings — X-in-a-row (SOW §4.4)
**Requirement:** PLC-tracked warning when X failures of the same inspection group occur in a row (or X of Y). Warning hierarchy: Nest > Application Station > Overall Module. Components: Lubra, Frame, Anodes, Blades, Trimmer. Stations: all M50 camera stations.
**Current state:** Not implemented.
**Action:** Implement a sliding-window failure counter per (component × station × nest) group in `PartExitOrchestrator`. Thresholds configurable in Harry.ini (`[Warnings] X_in_a_row`, `X_of_Y_window`). Send warning flag in SPS KeepAlive response when threshold crossed. This is complex — design separately with Harry's input on threshold values.

#### 19.2.3 Last NG image on dashboard (SOW §4.1)
**Requirement:** Dashboard must show images of the last NG parts.
**Current state:** Collage tab shows last 4 OK collages. No NG image viewer.
**Action:** Add an "NG Images" section to the Overview or Collage tab. On part exit with NG result, load the most recent full-resolution NG image path from the backup folder and display thumbnail (frozen BitmapImage, off-thread load, same pattern as collage thumbnails). Keep last 4 NG thumbnails in a `Queue<(string path, string serial, DateTime time)>(4)`.
**File:** `HarryDataServer/Controls/ucCollageControl.xaml`, `MainViewModel.cs`

---

### 19.3 CLARIFY WITH HARRY'S — Questions before/during commissioning

#### 19.3.1 M1X FTP connection (SOW §5.2.1)
**SOW note:** "New: Need connection between M1X vision to FTP server."
**Question:** M1X camera images are not delivered via the existing TCP telegram channel. Does Harry's expect us to run an FTP server that M1X uploads to? Or will M1X push images to a shared NAS path directly? What is the agreed folder structure for M1X images?
**Impact:** If we need to run an FTP server, this is significant new scope. Clarify before commissioning starts.

#### 19.3.2 LimitSample tolerance entries for measurement values (SOW §3.2.1)
**SOW description:** For measurement values (non-boolean), LimitSample entries can specify `[Expected value] ([Lower tolerance]; [Upper tolerance])` rather than just pass/fail.
**Current state:** HarryLimitSample works with ShouldPass / ShouldFail / Ignore (boolean only).
**Question:** Do any M50 or M1X measurements require numeric tolerance matching in LimitSample? If yes, HarryLimitSample needs a tolerance-entry mode and `MsaCalculator` needs a numeric comparison path.

#### 19.3.3 CSV datetime format (SOW §5.1.2) — ✅ DONE (2026-06-22)
**SOW requirement:** Datetime in filenames must be `DDMMYY_HHMMSS`.
**Implemented:** Centralised in `Infrastructure/FileNaming.cs` (`DateTimePattern = "ddMMyy_HHmmss"`). All generated filenames now use it: main/MSA/diagnostic CSV (`CsvFileWriter`), the MSA-tab CSV export, the log export, and the companion-tool CSV exports (`HarryShared.Data.CsvExport`). MSA CSV files are labelled module + type and stamped DDMMYY_HHMMSS. GSM run subfolders use the same stamp (see §1.2.1 constants in `FileNaming`).
**File:** `HarryDataServer/Infrastructure/{FileNaming,CsvFileWriter}.cs`

#### 19.3.4 HMI tolerance adjustment (SOW §4.1)
**SOW requirement:** All tolerances (limits) visible and adjustable on HMI, passcode-protected.
**Current state:** Limits come from Keyence Settings telegrams and are stored in the `settings` table. Our dashboard shows them read-only.
**Question:** Does Harry's expect limits to be writable FROM our WPF dashboard (and pushed back to the Keyence controller)? Or is the Keyence HMI the only place for limit adjustment, and our dashboard just displays them? This is a significant scope difference.

#### 19.3.5 M1X 4-parts-in-1-image filename (SOW §5.2.2)
**SOW requirement:** M1X captures 4 nests in one image. Filename must include all 4 parts' SZIDs.
**Current state:** Our image file-matching logic uses the 12-char SZID prefix per part. M1X images with 4 SZIDs in the filename won't match this pattern.
**Question:** What is the exact filename format Harry's will use for M1X multi-part images? Our `ImageHandler` needs a special matching rule for M1X images.

---

### 19.4 VERIFY DURING TESTING — Implementation checks

These items are implemented but need on-site verification:

| Check | What to verify | File |
|-------|---------------|------|
| Collage sources M2X + M50 only | M1X images must NOT appear in collage | `CollageComposer.cs` |
| GSM CSV folder name | Must be "Golden Sample Data" with subfolder TestType+DDMMYY_HHMMSS+Module (constants in `FileNaming`) | `Infrastructure/FileNaming.cs` |
| GSM images folder | Must be "Golden Sample Images" with run subfolder (constants in `FileNaming`) | `Infrastructure/FileNaming.cs` |
| Full-res retention configurable | Default 30 days via `[NAS] FullResRetentionDays`; per-type NG/Diag/GSM keys fall back to it | `ImageCleanupService.cs` |
| Backup folder YYYY\MM\DD structure | Year/month/day subfolders (no hour level) | `ImageHandler.cs` |
| Low-res delete after collage | For OK parts: individual BMP deleted after confirmed collage write | `ImageHandler.cs` |
| Low-res delete for NG | NG parts: low-res kept at part exit; deleted only when the matching full-res NG image is deleted (linked by 12-char serial prefix) | `ImageCleanupService.cs`, `PartExitOrchestrator.cs` |
| Humidity stored per part | m1x_humidity in dmcserial populated from telegram | `PartExitProcessor.cs` |

> **MSA cycle count is not configured by us.** The number of measurements per MSA run
> (≈50 for MSA1, 3 per part for MSA3, batch-driven for LimitSample) is controlled entirely
> by the SPS/PLC. We receive every measurement via TCP and aggregate by BaseID, so the
> evaluation works for any number of measurements — there is no cycle-count INI key.
