using Microsoft.Data.Sqlite;

namespace SerialDataLogger
{
    /// <summary>
    /// SQLite 저장 담당. 연결 생성, 스키마 준비, 삽입.
    /// </summary>
    public class Database
    {
        private readonly string _connectionString;
        private readonly object _lock = new();

        public string FilePath { get; }

        public Database(string fileName = "data.db")
        {
            // exe 옆에 두면 Program Files 아래 설치됐을 때 쓰기 권한이 없어 실패한다.
            // 사용자별 AppData는 항상 쓸 수 있다.
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SerialDataLogger");

            Directory.CreateDirectory(dir);

            FilePath = Path.Combine(dir, fileName);
            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = FilePath
            }.ToString();
        }

        /// <summary>
        /// 테이블이 없으면 만든다. 이미 있으면 아무 일도 안 한다.
        /// 앱 시작 때마다 호출해도 안전하다.
        /// </summary>
        public void Initialize()
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS readings (
                    id       INTEGER PRIMARY KEY AUTOINCREMENT,
                    ts       TEXT NOT NULL,
                    channel  TEXT NOT NULL,
                    value    REAL NOT NULL,
                    unit     TEXT,
                    raw      TEXT
                );
                CREATE INDEX IF NOT EXISTS idx_readings_ts ON readings(ts);
                CREATE INDEX IF NOT EXISTS idx_readings_channel ON readings(channel);

                CREATE TABLE IF NOT EXISTS thresholds (
                    channel  TEXT PRIMARY KEY,
                    lo       REAL,
                    hi       REAL,
                    enabled  INTEGER NOT NULL DEFAULT 1
                );

                CREATE TABLE IF NOT EXISTS alarms (
                    id       INTEGER PRIMARY KEY AUTOINCREMENT,
                    ts       TEXT NOT NULL,
                    channel  TEXT NOT NULL,
                    value    REAL NOT NULL,
                    kind     TEXT NOT NULL,   -- 'HI' 또는 'LO'
                    limit_v  REAL NOT NULL    -- 그때 걸려 있던 한계값
                );

                CREATE INDEX IF NOT EXISTS idx_alarms_ts ON alarms(ts);
            ";
            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// 측정값 여러 건을 한 트랜잭션으로 저장한다.
        /// 파싱 실패분도 그대로 받는다 — ok=false면 값 대신 원문만 남긴다.
        /// </summary>
        public void InsertMany(IEnumerable<Reading> readings)
        {
            lock (_lock)
            {
                using var conn = new SqliteConnection(_connectionString);
                conn.Open();

                using var tx = conn.BeginTransaction();
                using var cmd = conn.CreateCommand();

                // 파라미터 바인딩: 값을 SQL 문자열에 직접 붙이지 않는다.
                cmd.CommandText = @"
                    INSERT INTO readings (ts, channel, value, unit, raw)
                    VALUES ($ts, $channel, $value, $unit, $raw)";

                var pTs = cmd.CreateParameter(); pTs.ParameterName = "$ts"; cmd.Parameters.Add(pTs);
                var pCh = cmd.CreateParameter(); pCh.ParameterName = "$channel"; cmd.Parameters.Add(pCh);
                var pVal = cmd.CreateParameter(); pVal.ParameterName = "$value"; cmd.Parameters.Add(pVal);
                var pUnit = cmd.CreateParameter(); pUnit.ParameterName = "$unit"; cmd.Parameters.Add(pUnit);
                var pRaw = cmd.CreateParameter(); pRaw.ParameterName = "$raw"; cmd.Parameters.Add(pRaw);

                foreach (var r in readings)
                {
                    // 문자열 정렬 = 시간 정렬이 되도록 고정 형식으로 저장
                    pTs.Value = r.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff");
                    pCh.Value = r.Channel;
                    pVal.Value = r.Value;
                    pUnit.Value = (object?)r.Unit ?? DBNull.Value;
                    pRaw.Value = (object?)r.Raw ?? DBNull.Value;

                    cmd.ExecuteNonQuery();
                }

                tx.Commit();
            }
        }

        /// <summary>
        /// 기간과 채널로 걸러서 조회한다.
        /// channel이 null이면 전체 채널.
        /// </summary>
        public List<Reading> Query(DateTime from, DateTime to, string? channel, int limit = 5000)
        {
            var result = new List<Reading>();

            lock (_lock)
            {
                using var conn = new SqliteConnection(_connectionString);
                conn.Open();

                using var cmd = conn.CreateCommand();

                // 최신 것부터 limit개만. 전체를 다 읽으면 수십만 행에서 화면이 멈춘다.
                cmd.CommandText = @"
                    SELECT ts, channel, value, unit, raw
                    FROM readings
                    WHERE ts BETWEEN $from AND $to
                      AND ($channel IS NULL OR channel = $channel)
                    ORDER BY id DESC
                    LIMIT $limit";

                cmd.Parameters.AddWithValue("$from", from.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                cmd.Parameters.AddWithValue("$to", to.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                cmd.Parameters.AddWithValue("$channel", (object?)channel ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$limit", limit);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    result.Add(new Reading
                    {
                        Timestamp = DateTime.Parse(reader.GetString(0)),
                        Channel = reader.GetString(1),
                        Value = reader.GetDouble(2),
                        Unit = reader.IsDBNull(3) ? "" : reader.GetString(3),
                        Raw = reader.IsDBNull(4) ? "" : reader.GetString(4),
                    });
                }
            }

            return result;
        }

        // ---------- 임계값 ----------

        public List<Threshold> LoadThresholds()
        {
            var list = new List<Threshold>();

            lock (_lock)
            {
                using var conn = new SqliteConnection(_connectionString);
                conn.Open();

                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT channel, lo, hi, enabled FROM thresholds ORDER BY channel";

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    list.Add(new Threshold
                    {
                        Channel = reader.GetString(0),
                        Lo = reader.IsDBNull(1) ? null : reader.GetDouble(1),
                        Hi = reader.IsDBNull(2) ? null : reader.GetDouble(2),
                        Enabled = reader.GetInt32(3) != 0,
                    });
                }
            }

            return list;
        }

        public void SaveThreshold(Threshold t)
        {
            lock (_lock)
            {
                using var conn = new SqliteConnection(_connectionString);
                conn.Open();

                using var cmd = conn.CreateCommand();

                // 있으면 갱신, 없으면 삽입. 두 번 조회하지 않아도 된다.
                cmd.CommandText = @"
                    INSERT INTO thresholds (channel, lo, hi, enabled)
                    VALUES ($ch, $lo, $hi, $en)
                    ON CONFLICT(channel) DO UPDATE SET
                        lo = $lo, hi = $hi, enabled = $en";

                cmd.Parameters.AddWithValue("$ch", t.Channel);
                cmd.Parameters.AddWithValue("$lo", (object?)t.Lo ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$hi", (object?)t.Hi ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$en", t.Enabled ? 1 : 0);

                cmd.ExecuteNonQuery();
            }
        }

        public void DeleteThreshold(string channel)
        {
            lock (_lock)
            {
                using var conn = new SqliteConnection(_connectionString);
                conn.Open();

                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM thresholds WHERE channel = $ch";
                cmd.Parameters.AddWithValue("$ch", channel);
                cmd.ExecuteNonQuery();
            }
        }

        // ---------- 알람 이력 ----------

        public void InsertAlarms(IEnumerable<AlarmEvent> alarms)
        {
            lock (_lock)
            {
                using var conn = new SqliteConnection(_connectionString);
                conn.Open();

                using var tx = conn.BeginTransaction();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO alarms (ts, channel, value, kind, limit_v)
                    VALUES ($ts, $ch, $val, $kind, $lim)";

                var pTs = cmd.CreateParameter(); pTs.ParameterName = "$ts"; cmd.Parameters.Add(pTs);
                var pCh = cmd.CreateParameter(); pCh.ParameterName = "$ch"; cmd.Parameters.Add(pCh);
                var pVal = cmd.CreateParameter(); pVal.ParameterName = "$val"; cmd.Parameters.Add(pVal);
                var pKind = cmd.CreateParameter(); pKind.ParameterName = "$kind"; cmd.Parameters.Add(pKind);
                var pLim = cmd.CreateParameter(); pLim.ParameterName = "$lim"; cmd.Parameters.Add(pLim);

                foreach (var a in alarms)
                {
                    pTs.Value = a.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff");
                    pCh.Value = a.Channel;
                    pVal.Value = a.Value;
                    pKind.Value = a.Kind;
                    pLim.Value = a.Limit;
                    cmd.ExecuteNonQuery();
                }

                tx.Commit();
            }
        }

        public List<AlarmEvent> QueryAlarms(DateTime from, DateTime to, int limit = 2000)
        {
            var list = new List<AlarmEvent>();

            lock (_lock)
            {
                using var conn = new SqliteConnection(_connectionString);
                conn.Open();

                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT ts, channel, value, kind, limit_v
                    FROM alarms
                    WHERE ts BETWEEN $from AND $to
                    ORDER BY id DESC
                    LIMIT $limit";

                cmd.Parameters.AddWithValue("$from", from.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                cmd.Parameters.AddWithValue("$to", to.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                cmd.Parameters.AddWithValue("$limit", limit);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    list.Add(new AlarmEvent
                    {
                        Timestamp = DateTime.Parse(reader.GetString(0)),
                        Channel = reader.GetString(1),
                        Value = reader.GetDouble(2),
                        Kind = reader.GetString(3),
                        Limit = reader.GetDouble(4),
                    });
                }
            }

            return list;
        }

        /// <summary>DB에 실제로 존재하는 채널 목록. 필터 콤보박스를 채우는 데 쓴다.</summary>
        public List<string> GetChannels()
        {
            var list = new List<string>();

            lock (_lock)
            {
                using var conn = new SqliteConnection(_connectionString);
                conn.Open();

                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT DISTINCT channel FROM readings ORDER BY channel";

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    list.Add(reader.GetString(0));
            }

            return list;
        }

        public long CountRows()
        {
            lock (_lock)
            {
                using var conn = new SqliteConnection(_connectionString);
                conn.Open();

                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM readings";
                return (long)(cmd.ExecuteScalar() ?? 0L);
            }
        }
    }
}
