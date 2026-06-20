namespace HarryDataServer.Infrastructure;

/// <summary>One expected column of a table, used by the automatic schema-check.</summary>
/// <param name="Name">Column name as it appears in INFORMATION_SCHEMA.COLUMNS.</param>
/// <param name="Definition">
/// The column definition appended after the name in <c>ALTER TABLE ... ADD COLUMN</c>,
/// e.g. "VARCHAR(50) NOT NULL".
/// </param>
public readonly record struct ColumnSpec(string Name, string Definition);

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
            new("serial_number", "VARCHAR(50) NOT NULL"),
            new("serial_trimmer", "VARCHAR(50) NULL"),
            new("dmc", "VARCHAR(50) NULL"),
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
  serial_number   VARCHAR(50)  NOT NULL,
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
  result_status   TINYINT      NOT NULL DEFAULT 0,
  created_at      DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
  UNIQUE KEY uk_serial (serial_number),
  INDEX idx_dmc (dmc),
  INDEX idx_trimmer (serial_trimmer),
  INDEX idx_order (order_name),
  INDEX idx_created (created_at)
) ENGINE=InnoDB {DefaultCharset};",
    };

    // Column list shared by the two structurally-identical measurement tables.
    private static ColumnSpec[] MeasurementColumns(string serialColumn) => new ColumnSpec[]
    {
        new("id", "BIGINT NOT NULL AUTO_INCREMENT"),
        new(serialColumn, "VARCHAR(50) NOT NULL DEFAULT ''"),
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
  serial_number      VARCHAR(50) NOT NULL,
  definition_id      INT         NOT NULL,
  measurement_value  DOUBLE,
  measurement_string VARCHAR(20),
  result_status      TINYINT,
  run_type           TINYINT     NOT NULL DEFAULT 0,
  measured_at        DATETIME    NOT NULL,
  PRIMARY KEY (id, measured_at),
  INDEX idx_serial (serial_number),
  INDEX idx_def (definition_id),
  INDEX idx_measured (measured_at)
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
  serial_trimmer     VARCHAR(50) NOT NULL,
  definition_id      INT         NOT NULL,
  measurement_value  DOUBLE,
  measurement_string VARCHAR(20),
  result_status      TINYINT,
  run_type           TINYINT     NOT NULL DEFAULT 0,
  measured_at        DATETIME    NOT NULL,
  PRIMARY KEY (id, measured_at),
  INDEX idx_trimmer (serial_trimmer),
  INDEX idx_def (definition_id),
  INDEX idx_measured (measured_at)
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
            new("base_id", "VARCHAR(50) NOT NULL"),
            new("loop_number", "INT NOT NULL"),
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
  base_id            VARCHAR(50)  NOT NULL,
  loop_number        INT          NOT NULL,
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
  passed          TINYINT      NOT NULL,
  evaluated_at    DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
  INDEX idx_dmc (dmc),
  INDEX idx_controller_type (controller_name, msa_type)
) ENGINE=InnoDB {DefaultCharset};",
    };
}
