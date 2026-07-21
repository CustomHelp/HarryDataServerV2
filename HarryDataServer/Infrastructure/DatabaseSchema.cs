namespace HarryDataServer.Infrastructure;

/// <summary>One expected column of a table, used by the automatic schema-check.</summary>
/// <param name="Name">Column name as it appears in INFORMATION_SCHEMA.COLUMNS.</param>
/// <param name="Definition">
/// The column definition appended after the name in <c>ALTER TABLE ... ADD COLUMN</c>,
/// e.g. "VARCHAR(50) NOT NULL".
/// </param>
public readonly record struct ColumnSpec(string Name, string Definition);

/// <summary>
/// One expected secondary index, used by the automatic index-check at startup.
/// MySQL has no <c>CREATE INDEX IF NOT EXISTS</c>, so the index is looked up in
/// INFORMATION_SCHEMA.STATISTICS first and only created when missing (mirrors the
/// ADD COLUMN schema-check). Adding an entry here is enough to deploy a new index
/// by a code change alone — no manual SQL, no production stop (CLAUDE.md section 8).
/// PRIMARY / UNIQUE / foreign-key indexes are owned by the CREATE statements and
/// are intentionally not listed here.
/// </summary>
/// <param name="Table">Table the index lives on.</param>
/// <param name="Name">Index name as it appears in INFORMATION_SCHEMA.STATISTICS.</param>
/// <param name="Columns">Comma-separated column list, e.g. "serial_number, measured_at".</param>
public readonly record struct IndexSpec(string Table, string Name, string Columns);

/// <summary>
/// Describes a table the application owns: the full CREATE statement plus the
/// list of expected columns. If a column is added here, the startup schema-check
/// applies it automatically via ALTER TABLE — no manual SQL, no production stop
/// (CLAUDE.md section 8).
/// </summary>
public sealed class TableSchema
{
    public required string Name { get; init; }
    public required string CreateSql { get; init; }
    public required IReadOnlyList<ColumnSpec> Columns { get; init; }

    /// <summary>True for the day/range-partitioned measurement tables.</summary>
    public bool IsPartitioned { get; init; }
}

/// <summary>
/// Single source of truth for the database structure. Partitioned tables are
/// created with only a catch-all p_future partition; <see cref="PartitionManager"/>
/// splits in the concrete monthly partitions at startup so no calendar dates are
/// hardcoded in the CREATE statements.
/// </summary>
public static class DatabaseSchema
{
    public const string DefaultCharset = "CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci";

    public static readonly IReadOnlyList<TableSchema> Tables = new[]
    {
        Cameras,
        MeasurementDefinitions,
        SettingDefinitions,
        Settings,
        DmcSerial,
        MeasurementsSerial,
        MeasurementsSerialTrimmer,
        MsaMeasurements,
        MsaResults,
    };

    /// <summary>Names of the partitioned measurement tables, in dependency order.</summary>
    public static readonly IReadOnlyList<string> PartitionedTables = new[]
    {
        "measurements_serial",
        "measurements_serial_trimmer",
    };

    /// <summary>
    /// Serial columns whose width is enforced by a table rebuild at startup. Serial1
    /// (SZID / Virtual Serial / MSA BaseID+loop) is stored in VARCHAR(22) (CLAUDE.md §4).
    /// Changing a column type with ALTER is awkward on the partitioned measurement tables, and
    /// the data is disposable during trial operation, so the cleanest migration is DROP +
    /// re-CREATE at the new width: <see cref="MySqlRepository"/> drops and recreates a table
    /// only when its serial column is not already the expected width (one-time transition).
    /// </summary>
    public static readonly IReadOnlyList<(string Table, string Column, int Length)> SerialColumnWidths = new[]
    {
        ("dmcserial", "serial_number", 22),
        ("dmcserial", "serial_trimmer", 22),
        ("measurements_serial", "serial_number", 22),
        ("measurements_serial_trimmer", "serial_trimmer", 22),
    };

    /// <summary>
    /// Every expected secondary index. New installs get these from the CREATE
    /// statements; existing installs get any missing one applied by the startup
    /// index-check (CREATE TABLE IF NOT EXISTS never alters an existing table).
    /// Keep this list in sync with the INDEX clauses in the CreateSql above — the
    /// same dual-listing convention used for columns (Columns + CreateSql).
    /// </summary>
    public static readonly IReadOnlyList<IndexSpec> ExpectedIndexes = new[]
    {
        // dmcserial — part lookups (HarryAnalysis) and NG counting (HarryCounter).
        new IndexSpec("dmcserial", "idx_dmc", "dmc"),
        new IndexSpec("dmcserial", "idx_trimmer", "serial_trimmer"),
        new IndexSpec("dmcserial", "idx_order", "order_name"),
        new IndexSpec("dmcserial", "idx_created", "created_at"),
        new IndexSpec("dmcserial", "idx_result_status", "result_status"),

        // measurements_serial — by-serial (HarryAnalysis) and by-definition (HarryGraph).
        // The composites also satisfy the ORDER BY measured_at without a filesort.
        new IndexSpec("measurements_serial", "idx_serial", "serial_number"),
        new IndexSpec("measurements_serial", "idx_def", "definition_id"),
        new IndexSpec("measurements_serial", "idx_measured", "measured_at"),
        new IndexSpec("measurements_serial", "idx_serial_measured", "serial_number, measured_at"),
        new IndexSpec("measurements_serial", "idx_def_measured", "definition_id, measured_at"),

        // measurements_serial_trimmer — same access patterns for M20/M21.
        new IndexSpec("measurements_serial_trimmer", "idx_trimmer", "serial_trimmer"),
        new IndexSpec("measurements_serial_trimmer", "idx_def", "definition_id"),
        new IndexSpec("measurements_serial_trimmer", "idx_measured", "measured_at"),
        new IndexSpec("measurements_serial_trimmer", "idx_trimmer_measured", "serial_trimmer, measured_at"),
        new IndexSpec("measurements_serial_trimmer", "idx_def_measured", "definition_id, measured_at"),

        // msa_measurements — raw MSA samples. idx_baseid_controller serves the MSA completion
        // handler's exact lookup: WHERE base_id = @x AND controller_name LIKE 'M50%' (task 2b/3).
        new IndexSpec("msa_measurements", "idx_dmc_baseid", "dmc, base_id"),
        new IndexSpec("msa_measurements", "idx_baseid_controller", "base_id, controller_name"),
        new IndexSpec("msa_measurements", "idx_controller", "controller_name"),
        new IndexSpec("msa_measurements", "idx_measured", "measured_at"),

        // msa_results — by base_id (a run), by dmc, and per-module run navigation.
        new IndexSpec("msa_results", "idx_dmc", "dmc"),
        new IndexSpec("msa_results", "idx_controller_type", "controller_name, msa_type"),
        new IndexSpec("msa_results", "idx_base_id", "base_id"),
        new IndexSpec("msa_results", "idx_controller_type_eval", "controller_name, msa_type, evaluated_at"),
    };

    private static TableSchema Cameras => new()
    {
        Name = "cameras",
        Columns = new ColumnSpec[]
        {
            new("id", "INT AUTO_INCREMENT PRIMARY KEY"),
            new("camera_name", "VARCHAR(100) NOT NULL"),
            new("module", "VARCHAR(10) NOT NULL DEFAULT ''"),
            new("ip_address", "VARCHAR(15) NOT NULL DEFAULT ''"),
            new("port", "INT NOT NULL DEFAULT 0"),
            new("active", "TINYINT NOT NULL DEFAULT 1"),
            new("created_at", "DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP"),
        },
        CreateSql = $@"
CREATE TABLE IF NOT EXISTS cameras (
  id           INT AUTO_INCREMENT PRIMARY KEY,
  camera_name  VARCHAR(100) NOT NULL,
  module       VARCHAR(10)  NOT NULL,
  ip_address   VARCHAR(15)  NOT NULL,
  port         INT          NOT NULL,
  active       TINYINT      NOT NULL DEFAULT 1,
  created_at   DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
  UNIQUE KEY uk_camera_name (camera_name)
) ENGINE=InnoDB {DefaultCharset};",
    };

    private static TableSchema MeasurementDefinitions => new()
    {
        Name = "measurement_definitions",
        Columns = new ColumnSpec[]
        {
            new("id", "INT AUTO_INCREMENT PRIMARY KEY"),
            new("camera_id", "INT NOT NULL"),
            new("telegram_place", "INT NOT NULL"),
            new("variable_name", "VARCHAR(100) NOT NULL"),
            new("display_name", "VARCHAR(100) NOT NULL"),
            new("var_type", "VARCHAR(10) NOT NULL"),
            new("parameter_set", "INT NOT NULL"),
            new("module_ref", "VARCHAR(10) NOT NULL DEFAULT 'NoRef'"),
            new("feature_group", "VARCHAR(100) NOT NULL DEFAULT 'NoGroup'"),
            new("effective_from", "DATE NOT NULL"),
            new("effective_end", "DATE NULL"),
        },
        CreateSql = $@"
CREATE TABLE IF NOT EXISTS measurement_definitions (
  id              INT AUTO_INCREMENT PRIMARY KEY,
  camera_id       INT          NOT NULL,
  telegram_place  INT          NOT NULL,
  variable_name   VARCHAR(100) NOT NULL,
  display_name    VARCHAR(100) NOT NULL,
  var_type        VARCHAR(10)  NOT NULL,
  parameter_set   INT          NOT NULL,
  module_ref      VARCHAR(10)  NOT NULL DEFAULT 'NoRef',
  feature_group   VARCHAR(100) NOT NULL DEFAULT 'NoGroup',
  effective_from  DATE         NOT NULL,
  effective_end   DATE         NULL,
  INDEX idx_camera (camera_id),
  INDEX idx_variable (variable_name),
  CONSTRAINT fk_measdef_camera FOREIGN KEY (camera_id) REFERENCES cameras(id)
) ENGINE=InnoDB {DefaultCharset};",
    };

    private static TableSchema SettingDefinitions => new()
    {
        Name = "setting_definitions",
        Columns = new ColumnSpec[]
        {
            new("id", "INT AUTO_INCREMENT PRIMARY KEY"),
            new("camera_id", "INT NOT NULL"),
            new("telegram_place", "INT NOT NULL"),
            new("setting_name", "VARCHAR(100) NOT NULL"),
            new("parameter_set", "INT NOT NULL"),
            new("limit_type", "VARCHAR(5) NOT NULL"),
        },
        CreateSql = $@"
CREATE TABLE IF NOT EXISTS setting_definitions (
  id             INT AUTO_INCREMENT PRIMARY KEY,
  camera_id      INT          NOT NULL,
  telegram_place INT          NOT NULL,
  setting_name   VARCHAR(100) NOT NULL,
  parameter_set  INT          NOT NULL,
  limit_type     VARCHAR(5)   NOT NULL,
  INDEX idx_camera (camera_id),
  CONSTRAINT fk_setdef_camera FOREIGN KEY (camera_id) REFERENCES cameras(id)
) ENGINE=InnoDB {DefaultCharset};",
    };

    private static TableSchema Settings => new()
    {
        Name = "settings",
        Columns = new ColumnSpec[]
        {
            new("id", "INT AUTO_INCREMENT PRIMARY KEY"),
            new("camera_id", "INT NOT NULL"),
            new("definition_id", "INT NOT NULL"),
            new("parameter_set", "INT NOT NULL"),
            new("limit_value", "DOUBLE NOT NULL"),
            new("version", "VARCHAR(20) NULL"),
            new("recorded_at", "DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP"),
        },
        CreateSql = $@"
CREATE TABLE IF NOT EXISTS settings (
  id             INT AUTO_INCREMENT PRIMARY KEY,
  camera_id      INT      NOT NULL,
  definition_id  INT      NOT NULL,
  parameter_set  INT      NOT NULL,
  limit_value    DOUBLE   NOT NULL,
  version        VARCHAR(20),
  recorded_at    DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  INDEX idx_camera_recorded (camera_id, recorded_at),
  CONSTRAINT fk_settings_camera FOREIGN KEY (camera_id) REFERENCES cameras(id),
  CONSTRAINT fk_settings_def FOREIGN KEY (definition_id) REFERENCES setting_definitions(id)
) ENGINE=InnoDB {DefaultCharset};",
    };

    private static TableSchema DmcSerial => new()
    {
        Name = "dmcserial",
        Columns = new ColumnSpec[]
        {
            new("id", "INT AUTO_INCREMENT PRIMARY KEY"),
            new("serial_number", "VARCHAR(22) NOT NULL"),   // Serial1 (SZID) — 22 chars (CLAUDE.md §4)
            new("serial_trimmer", "VARCHAR(22) NULL"),       // Serial1 (Virtual Serial, M2X) — 22 chars
            new("dmc", "VARCHAR(50) NULL"),                  // Serial2 (DMC) — full width
            new("m1x_module", "TINYINT NULL"),
            new("m1x_nest", "INT NULL"),
            new("m2x_module", "TINYINT NULL"),
            new("m2x_nest", "INT NULL"),
            new("m3x_module", "VARCHAR(10) NULL"),
            new("m3x_nest", "VARCHAR(10) NULL"),
            new("m50_nest", "VARCHAR(10) NULL"),
            new("order_name", "VARCHAR(100) NULL"),
            new("m1x_temperature", "DOUBLE NULL"),
            new("m1x_humidity", "DOUBLE NULL"),
            new("result_status", "TINYINT NOT NULL DEFAULT 0"),
            new("created_at", "DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP"),
        },
        CreateSql = $@"
CREATE TABLE IF NOT EXISTS dmcserial (
  id              INT AUTO_INCREMENT PRIMARY KEY,
  serial_number   VARCHAR(22)  NOT NULL,
  serial_trimmer  VARCHAR(22),
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
  result_status   TINYINT      NOT NULL DEFAULT 0,
  created_at      DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
  UNIQUE KEY uk_serial (serial_number),
  INDEX idx_dmc (dmc),
  INDEX idx_trimmer (serial_trimmer),
  INDEX idx_order (order_name),
  INDEX idx_created (created_at),
  INDEX idx_result_status (result_status)
) ENGINE=InnoDB {DefaultCharset};",
    };

    // Column list shared by the two structurally-identical measurement tables.
    private static ColumnSpec[] MeasurementColumns(string serialColumn) => new ColumnSpec[]
    {
        new("id", "BIGINT NOT NULL AUTO_INCREMENT"),
        new(serialColumn, "VARCHAR(22) NOT NULL DEFAULT ''"),   // Serial1 — 22 chars (CLAUDE.md §4)
        new("definition_id", "INT NOT NULL"),
        new("measurement_value", "DOUBLE NULL"),
        new("measurement_string", "VARCHAR(20) NULL"),
        new("result_status", "TINYINT NULL"),
        new("run_type", "TINYINT NOT NULL DEFAULT 0"),
        new("measured_at", "DATETIME NOT NULL"),
    };

    private static TableSchema MeasurementsSerial => new()
    {
        Name = "measurements_serial",
        IsPartitioned = true,
        Columns = MeasurementColumns("serial_number"),
        CreateSql = $@"
CREATE TABLE IF NOT EXISTS measurements_serial (
  id                 BIGINT      NOT NULL AUTO_INCREMENT,
  serial_number      VARCHAR(22) NOT NULL,
  definition_id      INT         NOT NULL,
  measurement_value  DOUBLE,
  measurement_string VARCHAR(20),
  result_status      TINYINT,
  run_type           TINYINT     NOT NULL DEFAULT 0,
  measured_at        DATETIME    NOT NULL,
  PRIMARY KEY (id, measured_at),
  INDEX idx_serial (serial_number),
  INDEX idx_def (definition_id),
  INDEX idx_measured (measured_at),
  INDEX idx_serial_measured (serial_number, measured_at),
  INDEX idx_def_measured (definition_id, measured_at)
) ENGINE=InnoDB {DefaultCharset}
PARTITION BY RANGE (TO_DAYS(measured_at)) (
  PARTITION p_future VALUES LESS THAN MAXVALUE
);",
    };

    private static TableSchema MeasurementsSerialTrimmer => new()
    {
        Name = "measurements_serial_trimmer",
        IsPartitioned = true,
        Columns = MeasurementColumns("serial_trimmer"),
        CreateSql = $@"
CREATE TABLE IF NOT EXISTS measurements_serial_trimmer (
  id                 BIGINT      NOT NULL AUTO_INCREMENT,
  serial_trimmer     VARCHAR(22) NOT NULL,
  definition_id      INT         NOT NULL,
  measurement_value  DOUBLE,
  measurement_string VARCHAR(20),
  result_status      TINYINT,
  run_type           TINYINT     NOT NULL DEFAULT 0,
  measured_at        DATETIME    NOT NULL,
  PRIMARY KEY (id, measured_at),
  INDEX idx_trimmer (serial_trimmer),
  INDEX idx_def (definition_id),
  INDEX idx_measured (measured_at),
  INDEX idx_trimmer_measured (serial_trimmer, measured_at),
  INDEX idx_def_measured (definition_id, measured_at)
) ENGINE=InnoDB {DefaultCharset}
PARTITION BY RANGE (TO_DAYS(measured_at)) (
  PARTITION p_future VALUES LESS THAN MAXVALUE
);",
    };

    private static TableSchema MsaMeasurements => new()
    {
        Name = "msa_measurements",
        Columns = new ColumnSpec[]
        {
            new("id", "BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY"),
            new("dmc", "VARCHAR(50) NOT NULL"),
            new("base_id", "VARCHAR(50) NOT NULL"),         // 14-char BaseID (MMYYMMDDHHmmSS), never with the loop counter
            new("loop_number", "INT NOT NULL"),             // 3-digit per-loop counter parsed from the run serial field
            new("controller_name", "VARCHAR(100) NOT NULL"),
            new("definition_id", "INT NOT NULL"),
            new("measurement_value", "DOUBLE NULL"),
            new("measurement_string", "VARCHAR(20) NULL"),
            new("result_status", "TINYINT NULL"),
            new("msa_type", "VARCHAR(20) NOT NULL"),
            new("msa_version", "VARCHAR(50) NULL"),
            new("measured_at", "DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP"),
        },
        CreateSql = $@"
CREATE TABLE IF NOT EXISTS msa_measurements (
  id                 BIGINT       NOT NULL AUTO_INCREMENT,
  dmc                VARCHAR(50)  NOT NULL,
  base_id            VARCHAR(50)  NOT NULL,   -- 14-char BaseID (MMYYMMDDHHmmSS); the loop counter is stored separately
  loop_number        INT          NOT NULL,   -- 3-digit per-loop counter from the run serial field
  controller_name    VARCHAR(100) NOT NULL,
  definition_id      INT          NOT NULL,
  measurement_value  DOUBLE,
  measurement_string VARCHAR(20),
  result_status      TINYINT,
  msa_type           VARCHAR(20)  NOT NULL,
  msa_version        VARCHAR(50),
  measured_at        DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (id),
  INDEX idx_dmc_baseid (dmc, base_id),
  INDEX idx_baseid_controller (base_id, controller_name),
  INDEX idx_controller (controller_name),
  INDEX idx_measured (measured_at)
) ENGINE=InnoDB {DefaultCharset};",
    };

    private static TableSchema MsaResults => new()
    {
        Name = "msa_results",
        Columns = new ColumnSpec[]
        {
            new("id", "INT AUTO_INCREMENT PRIMARY KEY"),
            new("controller_name", "VARCHAR(100) NOT NULL"),
            new("dmc", "VARCHAR(50) NOT NULL"),
            new("base_id", "VARCHAR(50) NOT NULL"),
            new("msa_type", "VARCHAR(20) NOT NULL"),
            new("msa_version", "VARCHAR(50) NULL"),
            new("definition_id", "INT NOT NULL"),
            new("display_name", "VARCHAR(100) NULL"),
            new("cg_value", "DOUBLE NULL"),
            new("cgk_value", "DOUBLE NULL"),
            new("pct_tolerance", "DOUBLE NULL"),
            new("expected_value", "VARCHAR(50) NULL"),
            new("actual_value", "VARCHAR(50) NULL"),
            new("n_values", "INT NULL"),
            new("mean_value", "DOUBLE NULL"),
            new("std_dev", "DOUBLE NULL"),
            new("reference_value", "DOUBLE NULL"),
            new("tolerance", "DOUBLE NULL"),
            new("criterion", "VARCHAR(80) NULL"),
            new("reason", "VARCHAR(255) NULL"),
            new("evaluated", "TINYINT NULL"),
            new("passed", "TINYINT NOT NULL"),
            new("evaluated_at", "DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP"),
        },
        CreateSql = $@"
CREATE TABLE IF NOT EXISTS msa_results (
  id              INT AUTO_INCREMENT PRIMARY KEY,
  controller_name VARCHAR(100) NOT NULL,
  dmc             VARCHAR(50)  NOT NULL,
  base_id         VARCHAR(50)  NOT NULL,
  msa_type        VARCHAR(20)  NOT NULL,
  msa_version     VARCHAR(50),
  definition_id   INT          NOT NULL,
  display_name    VARCHAR(100),
  cg_value        DOUBLE,
  cgk_value       DOUBLE,
  pct_tolerance   DOUBLE,
  expected_value  VARCHAR(50),
  actual_value    VARCHAR(50),
  n_values        INT,
  mean_value      DOUBLE,
  std_dev         DOUBLE,
  reference_value DOUBLE,
  tolerance       DOUBLE,
  criterion       VARCHAR(80),
  reason          VARCHAR(255),
  evaluated       TINYINT,
  passed          TINYINT      NOT NULL,
  evaluated_at    DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
  INDEX idx_dmc (dmc),
  INDEX idx_controller_type (controller_name, msa_type),
  INDEX idx_base_id (base_id),
  INDEX idx_controller_type_eval (controller_name, msa_type, evaluated_at)
) ENGINE=InnoDB {DefaultCharset};",
    };
}
