# HarryDataServer V2 — Live Database Schema

**Database:** `camera_data` · **Engine:** InnoDB · **Charset:** utf8mb4 / utf8mb4_unicode_ci
**Generated:** 2026-06-20 — queried directly from `INFORMATION_SCHEMA` of the live MySQL 9.7.1 server.

> This document is a snapshot of the **actual** database (not `DatabaseSchema.cs`). It was produced
> from `INFORMATION_SCHEMA.COLUMNS`, `INFORMATION_SCHEMA.STATISTICS`, `INFORMATION_SCHEMA.PARTITIONS`
> and `INFORMATION_SCHEMA.KEY_COLUMN_USAGE`. All columns currently have an empty `COLUMN_COMMENT`;
> the **Description** column below is annotated from the project spec (CLAUDE.md) for readability.
> `Default` shows the literal `COLUMN_DEFAULT`; `—` means no default (NULL).

---

## TABLE: cameras
**Purpose:** One row per configured Keyence camera controller, synced from Harry.ini at startup.

| Column | Type | Nullable | Key | Default | Description |
|--------|------|----------|-----|---------|-------------|
| id | int | NO | PRI | — | Surrogate primary key (AUTO_INCREMENT). |
| camera_name | varchar(100) | NO | UNI | — | Controller name, e.g. `M50_ST110_KF1`. |
| module | varchar(10) | NO | | — | Module the camera belongs to (M10, M50, …). |
| ip_address | varchar(15) | NO | | — | Camera IP address. |
| port | int | NO | | — | Camera TCP port. |
| active | tinyint | NO | | 1 | 1 = active in config, 0 = retired. |
| created_at | datetime | NO | | CURRENT_TIMESTAMP | Row creation timestamp. |

**Indexes:**
| Index Name | Columns | Type |
|------------|---------|------|
| PRIMARY | id | BTREE (UNIQUE) |
| uk_camera_name | camera_name | BTREE (UNIQUE) |

**Partitioning:** NONE

**Relations:** NONE (referenced by `measurement_definitions`, `setting_definitions`, `settings`).

---

## TABLE: measurement_definitions
**Purpose:** Versioned catalogue of measurement variables per camera (from the Result JSON templates), with effective-date history.

| Column | Type | Nullable | Key | Default | Description |
|--------|------|----------|-----|---------|-------------|
| id | int | NO | PRI | — | Surrogate primary key (AUTO_INCREMENT). |
| camera_id | int | NO | MUL | — | FK → `cameras.id`. |
| telegram_place | int | NO | | — | Position of the variable in the camera telegram. |
| variable_name | varchar(100) | NO | MUL | — | Raw variable name (`R_`/`V_` prefixed). |
| display_name | varchar(100) | NO | | — | Human-readable measurement name. |
| var_type | varchar(10) | NO | | — | `Result` or `Value`. |
| parameter_set | int | NO | | — | Parameter-set index for limit grouping. |
| module_ref | varchar(10) | NO | | NoRef | Module reference, `NoRef` if none. |
| feature_group | varchar(100) | NO | | NoGroup | Error category (used by HarryCounter). |
| effective_from | date | NO | | — | Date this definition version became active. |
| effective_end | date | YES | | — | Date retired; NULL = currently active. |

**Indexes:**
| Index Name | Columns | Type |
|------------|---------|------|
| PRIMARY | id | BTREE (UNIQUE) |
| idx_camera | camera_id | BTREE |
| idx_variable | variable_name | BTREE |

**Partitioning:** NONE

**Relations:** `camera_id` references `cameras(id)` (FK `fk_measdef_camera`).

---

## TABLE: setting_definitions
**Purpose:** Catalogue of limit (Min/Max) parameters per camera (from the Settings JSON templates).

| Column | Type | Nullable | Key | Default | Description |
|--------|------|----------|-----|---------|-------------|
| id | int | NO | PRI | — | Surrogate primary key (AUTO_INCREMENT). |
| camera_id | int | NO | MUL | — | FK → `cameras.id`. |
| telegram_place | int | NO | | — | Position of the setting in the Settings telegram. |
| setting_name | varchar(100) | NO | | — | Setting / limit name. |
| parameter_set | int | NO | | — | Parameter-set index. |
| limit_type | varchar(5) | NO | | — | `Min` or `Max`. |

**Indexes:**
| Index Name | Columns | Type |
|------------|---------|------|
| PRIMARY | id | BTREE (UNIQUE) |
| idx_camera | camera_id | BTREE |

**Partitioning:** NONE

**Relations:** `camera_id` references `cameras(id)` (FK `fk_setdef_camera`).

---

## TABLE: settings
**Purpose:** Time-stamped history of limit values (USL/LSL) per camera/definition/parameter-set.

| Column | Type | Nullable | Key | Default | Description |
|--------|------|----------|-----|---------|-------------|
| id | int | NO | PRI | — | Surrogate primary key (AUTO_INCREMENT). |
| camera_id | int | NO | MUL | — | FK → `cameras.id`. |
| definition_id | int | NO | MUL | — | FK → `setting_definitions.id`. |
| parameter_set | int | NO | | — | Parameter-set index. |
| limit_value | double | NO | | — | The limit value recorded. |
| version | varchar(20) | YES | | — | Camera program version that set the limit. |
| recorded_at | datetime | NO | | CURRENT_TIMESTAMP | When the limit was recorded. |

**Indexes:**
| Index Name | Columns | Type |
|------------|---------|------|
| PRIMARY | id | BTREE (UNIQUE) |
| idx_camera_recorded | camera_id, recorded_at | BTREE |
| fk_settings_def | definition_id | BTREE |

**Partitioning:** NONE

**Relations:** `camera_id` references `cameras(id)` (FK `fk_settings_camera`); `definition_id` references `setting_definitions(id)` (FK `fk_settings_def`).

---

## TABLE: dmcserial
**Purpose:** One row per finished part (written at Part Exit), holding identity, routing nests, environment and overall result.

| Column | Type | Nullable | Key | Default | Description |
|--------|------|----------|-----|---------|-------------|
| id | int | NO | PRI | — | Surrogate primary key (AUTO_INCREMENT). |
| serial_number | varchar(50) | NO | UNI | — | SZID (frame serial); unique business key. |
| serial_trimmer | varchar(50) | YES | MUL | — | Virtual serial (trimmer identity). |
| dmc | varchar(50) | YES | MUL | — | DataMatrix code lasered on the part. |
| m1x_module | tinyint | YES | | — | M1x module number (10 or 11). |
| m1x_nest | int | YES | | — | Nest number in the M1x module. |
| m2x_module | tinyint | YES | | — | M2x module number. |
| m2x_nest | int | YES | | — | Nest number in the M2x module. |
| m3x_module | varchar(10) | YES | | — | M3x blade module. |
| m3x_nest | varchar(10) | YES | | — | Nest number in the M3x module. |
| m50_nest | varchar(10) | YES | | — | Nest number in M50. |
| order_name | varchar(100) | YES | MUL | — | Production order name. |
| m1x_temperature | double | YES | | — | Temperature from M1x. |
| m1x_humidity | double | YES | | — | Humidity from M1x. |
| result_status | tinyint | NO | MUL | 0 | 1 = OK, 0 = NG, -1 = deleted. |
| created_at | datetime | NO | MUL | CURRENT_TIMESTAMP | Part-exit timestamp. |

**Indexes:**
| Index Name | Columns | Type |
|------------|---------|------|
| PRIMARY | id | BTREE (UNIQUE) |
| uk_serial | serial_number | BTREE (UNIQUE) |
| idx_dmc | dmc | BTREE |
| idx_trimmer | serial_trimmer | BTREE |
| idx_order | order_name | BTREE |
| idx_created | created_at | BTREE |
| idx_result_status | result_status | BTREE |

**Partitioning:** NONE

**Relations:** NONE (no declared foreign keys; linked to measurements by serial value).

---

## TABLE: measurements_serial
**Purpose:** Per-measurement results for frame-serial parts (R_/V_ pairs combined into one row); partitioned by day.

| Column | Type | Nullable | Key | Default | Description |
|--------|------|----------|-----|---------|-------------|
| id | bigint | NO | PRI | — | AUTO_INCREMENT; part of composite PK. |
| serial_number | varchar(50) | NO | MUL | — | SZID this measurement belongs to. |
| definition_id | int | NO | MUL | — | Measurement definition (no FK; logical → `measurement_definitions.id`). |
| measurement_value | double | YES | | — | Measured value (from `V_`). |
| measurement_string | varchar(20) | YES | | — | String value, if applicable. |
| result_status | tinyint | YES | | — | Result code (-2..2). |
| run_type | tinyint | NO | | 0 | 0=Normal,1=MSA1,2=MSA3,3=LimitSample,4=GoldenSample. |
| measured_at | datetime | NO | PRI | — | Measurement timestamp; partition key + part of PK. |

**Indexes:**
| Index Name | Columns | Type |
|------------|---------|------|
| PRIMARY | id, measured_at | BTREE (UNIQUE) |
| idx_serial | serial_number | BTREE |
| idx_def | definition_id | BTREE |
| idx_measured | measured_at | BTREE |
| idx_serial_measured | serial_number, measured_at | BTREE |
| idx_def_measured | definition_id, measured_at | BTREE |

**Partitioning:** RANGE on `TO_DAYS(measured_at)` — 5 partitions:
| Partition | Boundary (VALUES LESS THAN) |
|-----------|------------------------------|
| p_2026_06 | 2026-07-01 (TO_DAYS 740163) |
| p_2026_07 | 2026-08-01 (TO_DAYS 740194) |
| p_2026_08 | 2026-09-01 (TO_DAYS 740225) |
| p_2026_09 | 2026-10-01 (TO_DAYS 740255) |
| p_future  | MAXVALUE |

**Relations:** NONE (no declared FK; `definition_id` is a logical reference to `measurement_definitions.id`, `serial_number` to `dmcserial.serial_number`).

---

## TABLE: measurements_serial_trimmer
**Purpose:** Per-measurement results for trimmer-serial parts (M20/M21); same structure as `measurements_serial`, partitioned by day.

| Column | Type | Nullable | Key | Default | Description |
|--------|------|----------|-----|---------|-------------|
| id | bigint | NO | PRI | — | AUTO_INCREMENT; part of composite PK. |
| serial_trimmer | varchar(50) | NO | MUL | — | Virtual serial this measurement belongs to. |
| definition_id | int | NO | MUL | — | Measurement definition (no FK; logical → `measurement_definitions.id`). |
| measurement_value | double | YES | | — | Measured value (from `V_`). |
| measurement_string | varchar(20) | YES | | — | String value, if applicable. |
| result_status | tinyint | YES | | — | Result code (-2..2). |
| run_type | tinyint | NO | | 0 | 0=Normal,1=MSA1,2=MSA3,3=LimitSample,4=GoldenSample. |
| measured_at | datetime | NO | PRI | — | Measurement timestamp; partition key + part of PK. |

**Indexes:**
| Index Name | Columns | Type |
|------------|---------|------|
| PRIMARY | id, measured_at | BTREE (UNIQUE) |
| idx_trimmer | serial_trimmer | BTREE |
| idx_def | definition_id | BTREE |
| idx_measured | measured_at | BTREE |
| idx_trimmer_measured | serial_trimmer, measured_at | BTREE |
| idx_def_measured | definition_id, measured_at | BTREE |

**Partitioning:** RANGE on `TO_DAYS(measured_at)` — 5 partitions:
| Partition | Boundary (VALUES LESS THAN) |
|-----------|------------------------------|
| p_2026_06 | 2026-07-01 (TO_DAYS 740163) |
| p_2026_07 | 2026-08-01 (TO_DAYS 740194) |
| p_2026_08 | 2026-09-01 (TO_DAYS 740225) |
| p_2026_09 | 2026-10-01 (TO_DAYS 740255) |
| p_future  | MAXVALUE |

**Relations:** NONE (no declared FK; `definition_id` is a logical reference to `measurement_definitions.id`, `serial_trimmer` to `dmcserial.serial_trimmer`).

---

## TABLE: msa_measurements
**Purpose:** Raw MSA sample measurements (MSA1/MSA3/LimitSample runs), kept separate from production data.

| Column | Type | Nullable | Key | Default | Description |
|--------|------|----------|-----|---------|-------------|
| id | bigint | NO | PRI | — | Surrogate primary key (AUTO_INCREMENT). |
| dmc | varchar(50) | NO | MUL | — | DMC of the test part. |
| base_id | varchar(50) | NO | | — | 19-char BaseID identifying the MSA run. |
| loop_number | int | NO | | — | Repeat/loop index of the measurement. |
| controller_name | varchar(100) | NO | MUL | — | Camera controller name. |
| definition_id | int | NO | | — | Measurement definition (logical → `measurement_definitions.id`). |
| measurement_value | double | YES | | — | Measured value. |
| measurement_string | varchar(20) | YES | | — | String value, if applicable. |
| result_status | tinyint | YES | | — | Result code. |
| msa_type | varchar(20) | NO | | — | `MSA1`, `MSA3` or `LimitSample`. |
| msa_version | varchar(50) | YES | | — | MSA reference version. |
| measured_at | datetime | NO | MUL | CURRENT_TIMESTAMP | Measurement timestamp. |

**Indexes:**
| Index Name | Columns | Type |
|------------|---------|------|
| PRIMARY | id | BTREE (UNIQUE) |
| idx_dmc_baseid | dmc, base_id | BTREE |
| idx_controller | controller_name | BTREE |
| idx_measured | measured_at | BTREE |

**Partitioning:** NONE

**Relations:** NONE (no declared FK; `definition_id` is a logical reference to `measurement_definitions.id`).

---

## TABLE: msa_results
**Purpose:** Computed MSA evaluation results per measurement (Cg/Cgk for MSA1, %Tolerance for MSA3, expected/actual for LimitSample).

| Column | Type | Nullable | Key | Default | Description |
|--------|------|----------|-----|---------|-------------|
| id | int | NO | PRI | — | Surrogate primary key (AUTO_INCREMENT). |
| controller_name | varchar(100) | NO | MUL | — | Camera controller name. |
| dmc | varchar(50) | NO | MUL | — | DMC of the evaluated part. |
| base_id | varchar(50) | NO | MUL | — | BaseID of the MSA run (a run = rows sharing base_id). |
| msa_type | varchar(20) | NO | | — | `MSA1`, `MSA3` or `LimitSample`. |
| msa_version | varchar(50) | YES | | — | MSA reference version. |
| definition_id | int | NO | | — | Measurement definition (logical → `measurement_definitions.id`). |
| display_name | varchar(100) | YES | | — | Human-readable measurement name. |
| cg_value | double | YES | | — | Cg (MSA1 only). |
| cgk_value | double | YES | | — | Cgk (MSA1 only). |
| pct_tolerance | double | YES | | — | %Tolerance (MSA3 only). |
| passed | tinyint | NO | | — | 1 = passed, 0 = failed. |
| evaluated_at | datetime | NO | | CURRENT_TIMESTAMP | When the evaluation was computed. |
| expected_value | varchar(50) | YES | | — | LimitSample expected (reject/accept). |
| actual_value | varchar(50) | YES | | — | LimitSample actual result. |

**Indexes:**
| Index Name | Columns | Type |
|------------|---------|------|
| PRIMARY | id | BTREE (UNIQUE) |
| idx_dmc | dmc | BTREE |
| idx_controller_type | controller_name, msa_type | BTREE |
| idx_base_id | base_id | BTREE |
| idx_controller_type_eval | controller_name, msa_type, evaluated_at | BTREE |

**Partitioning:** NONE

**Relations:** NONE (no declared FK; `definition_id` is a logical reference to `measurement_definitions.id`).

---

*Source of truth: the live `camera_data` database (MySQL 9.7.1). For the application's
declarative schema and auto-migration, see `HarryDataServer/Infrastructure/DatabaseSchema.cs`.*
