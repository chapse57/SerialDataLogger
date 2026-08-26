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
