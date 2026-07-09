using BODA.CMS.Collector.Dashboard;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace BODA.CMS.Collector.Storage
{
    /// <summary>
    /// TimescaleDB(PostgreSQL) 적재 — 공용 프레임 기준 단일 hypertable, 벤더/채널은 태그 컬럼
    /// (ROADMAP §4 P1: "벤더별 테이블 금지, 태그로 구분"). 배치는 바이너리 COPY로 적재.
    /// TimescaleDB 확장이 없으면 일반 테이블 + 인덱스로 동작(개발 환경 배려).
    /// </summary>
    public sealed class TimescaleFrameStore : IFrameStore
    {
        private const string CreateTableSql = """
            CREATE TABLE IF NOT EXISTS telemetry_frames (
                time                timestamptz NOT NULL,
                robot_id            text        NOT NULL,
                vendor              text        NOT NULL,
                channel             text        NOT NULL,
                controller_clock    double precision,
                position_deg        real[]      NOT NULL,
                velocity_degs       real[],
                torque_nm           real[],
                model_torque_nm     real[],
                external_torque_nm  real[],
                current_a           real[],
                temperature_c       real[],
                vendor_raw          jsonb
            );
            """;

        private const string CopySql = """
            COPY telemetry_frames
                (time, robot_id, vendor, channel, controller_clock,
                 position_deg, velocity_degs, torque_nm, model_torque_nm,
                 external_torque_nm, current_a, temperature_c, vendor_raw)
            FROM STDIN (FORMAT BINARY)
            """;

        private readonly NpgsqlDataSource _dataSource;
        private readonly string _connectionString;
        private readonly ILogger<TimescaleFrameStore> _logger;

        public TimescaleFrameStore(IOptions<CollectorOptions> options, ILogger<TimescaleFrameStore> logger)
        {
            _connectionString = options.Value.Storage.ConnectionString;
            _dataSource = NpgsqlDataSource.Create(_connectionString);
            _logger = logger;
        }

        public async Task InitializeAsync(CancellationToken ct)
        {
            await EnsureDatabaseAsync(ct);

            await using var conn = await _dataSource.OpenConnectionAsync(ct);

            await using (var cmd = new NpgsqlCommand(CreateTableSql, conn))
                await cmd.ExecuteNonQueryAsync(ct);

            // TimescaleDB가 설치된 서버라면 hypertable로 승격 — 없으면 일반 테이블로 계속.
            try
            {
                await using var hyper = new NpgsqlCommand(
                    "SELECT create_hypertable('telemetry_frames', 'time', if_not_exists => TRUE);", conn);
                await hyper.ExecuteNonQueryAsync(ct);
                _logger.LogInformation("telemetry_frames hypertable 준비 완료 (TimescaleDB).");
            }
            catch (PostgresException ex) when (ex.SqlState == "42883") // create_hypertable 함수 없음
            {
                _logger.LogWarning("TimescaleDB 확장이 없어 일반 테이블로 동작합니다 (성능·보존정책 제한).");
            }

            await using (var idx = new NpgsqlCommand(
                "CREATE INDEX IF NOT EXISTS idx_telemetry_robot_time ON telemetry_frames (robot_id, time DESC);", conn))
                await idx.ExecuteNonQueryAsync(ct);

            // 알림 이력 (CBM/ML/비전 — 저빈도 단건 insert)
            await using (var alerts = new NpgsqlCommand("""
                CREATE TABLE IF NOT EXISTS telemetry_alerts (
                    time      timestamptz NOT NULL,
                    robot_id  text        NOT NULL,
                    vendor    text        NOT NULL,
                    channel   text        NOT NULL,
                    signal    text        NOT NULL,
                    axis      int         NOT NULL,
                    severity  text        NOT NULL,
                    kind      text        NOT NULL,
                    message   text        NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_alerts_robot_time ON telemetry_alerts (robot_id, time DESC);
                """, conn))
                await alerts.ExecuteNonQueryAsync(ct);
        }

        /// <summary>
        /// 접속 문자열의 데이터베이스가 서버에 없으면 만든다 — 통합 설치(PostgreSQL 동봉 번들) 직후의
        /// 빈 서버에서 psql 없이 자가 구성되도록. 유지보수 DB(postgres) 접속 권한이 없으면 원래 예외 그대로.
        /// </summary>
        private async Task EnsureDatabaseAsync(CancellationToken ct)
        {
            try
            {
                await using var probe = await _dataSource.OpenConnectionAsync(ct);
                return;
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InvalidCatalogName)
            {
                // 아래에서 생성 시도.
            }

            var builder = new NpgsqlConnectionStringBuilder(_connectionString);
            string dbName = builder.Database ?? "boda_cms";
            builder.Database = "postgres";

            await using var maintenance = NpgsqlDataSource.Create(builder);
            await using var conn = await maintenance.OpenConnectionAsync(ct);
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{dbName.Replace("\"", "\"\"")}\"", conn);
            await cmd.ExecuteNonQueryAsync(ct);
            _logger.LogInformation("데이터베이스 {Db} 가 없어 새로 만들었습니다.", dbName);
        }

        public async Task WriteAlertAsync(AlertRecord a, CancellationToken ct)
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO telemetry_alerts (time, robot_id, vendor, channel, signal, axis, severity, kind, message)
                VALUES (@t, @r, @v, @c, @s, @a, @sev, @k, @m);
                """, conn);
            cmd.Parameters.AddWithValue("t", a.AtUtc);
            cmd.Parameters.AddWithValue("r", a.RobotId);
            cmd.Parameters.AddWithValue("v", a.Vendor);
            cmd.Parameters.AddWithValue("c", a.Channel);
            cmd.Parameters.AddWithValue("s", a.Signal);
            cmd.Parameters.AddWithValue("a", a.Axis);
            cmd.Parameters.AddWithValue("sev", a.Severity);
            cmd.Parameters.AddWithValue("k", a.Kind);
            cmd.Parameters.AddWithValue("m", a.Message);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public async Task WriteBatchAsync(IReadOnlyList<TelemetryRecord> batch, CancellationToken ct)
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            await using var writer = await conn.BeginBinaryImportAsync(CopySql, ct);

            foreach (TelemetryRecord record in batch)
            {
                TelemetryRow row = TelemetryRow.FromRecord(record);

                await writer.StartRowAsync(ct);
                await writer.WriteAsync(row.TimeUtc, NpgsqlDbType.TimestampTz, ct);
                await writer.WriteAsync(row.RobotId, NpgsqlDbType.Text, ct);
                await writer.WriteAsync(row.Vendor, NpgsqlDbType.Text, ct);
                await writer.WriteAsync(row.Channel, NpgsqlDbType.Text, ct);
                await WriteNullableAsync(writer, row.ControllerClock, NpgsqlDbType.Double, ct);
                await writer.WriteAsync(row.PositionDeg, NpgsqlDbType.Array | NpgsqlDbType.Real, ct);
                await WriteNullableAsync(writer, row.VelocityDegS, NpgsqlDbType.Array | NpgsqlDbType.Real, ct);
                await WriteNullableAsync(writer, row.TorqueNm, NpgsqlDbType.Array | NpgsqlDbType.Real, ct);
                await WriteNullableAsync(writer, row.ModelTorqueNm, NpgsqlDbType.Array | NpgsqlDbType.Real, ct);
                await WriteNullableAsync(writer, row.ExternalTorqueNm, NpgsqlDbType.Array | NpgsqlDbType.Real, ct);
                await WriteNullableAsync(writer, row.CurrentA, NpgsqlDbType.Array | NpgsqlDbType.Real, ct);
                await WriteNullableAsync(writer, row.TemperatureC, NpgsqlDbType.Array | NpgsqlDbType.Real, ct);
                await WriteNullableAsync(writer, row.VendorRawJson, NpgsqlDbType.Jsonb, ct);
            }

            await writer.CompleteAsync(ct);
        }

        private static async Task WriteNullableAsync<T>(NpgsqlBinaryImporter writer, T? value, NpgsqlDbType type, CancellationToken ct)
        {
            if (value is null) await writer.WriteNullAsync(ct);
            else await writer.WriteAsync(value, type, ct);
        }
    }
}
