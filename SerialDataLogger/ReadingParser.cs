using System.Globalization;

namespace SerialDataLogger
{
    /// <summary>
    /// 수신 문자열 → Reading 변환.
    /// 형식: $DATA,채널,값,단위,시각
    /// 예:   $DATA,T1,23.5,C,2026-08-22T14:03:11
    /// </summary>
    public static class ReadingParser
    {
        private const string Header = "$DATA";
        private const int FieldCount = 5;

        /// <summary>
        /// 파싱 성공 시 true. 실패해도 reading은 null이 아니며 Raw에 원문이 담긴다.
        /// 장비 데이터는 깨져서 들어오는 게 정상이라, 예외를 던지지 않고 false로 알린다.
        /// </summary>
        public static bool TryParse(string line, out Reading reading, out string error)
        {
            reading = new Reading { Raw = line };
            error = "";

            if (string.IsNullOrWhiteSpace(line))
            {
                error = "빈 줄";
                return false;
            }

            string[] f = line.Split(',');

            if (f.Length != FieldCount)
            {
                error = $"필드 수 {f.Length}개 (기대 {FieldCount}개)";
                return false;
            }

            if (f[0] != Header)
            {
                error = $"시작 표시 불일치: '{f[0]}'";
                return false;
            }

            if (string.IsNullOrWhiteSpace(f[1]))
            {
                error = "채널 없음";
                return false;
            }

            // InvariantCulture 고정: 로케일에 따라 소수점이 쉼표(,)로 해석되면
            // 값이 통째로 어긋난다. 장비 데이터는 항상 '.' 기준.
            if (!double.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            {
                error = $"값 파싱 실패: '{f[2]}'";
                return false;
            }

            if (!DateTime.TryParse(f[4], CultureInfo.InvariantCulture,
                                   DateTimeStyles.None, out DateTime ts))
            {
                error = $"시각 파싱 실패: '{f[4]}'";
                return false;
            }

            reading.Channel = f[1].Trim();
            reading.Value = value;
            reading.Unit = f[3].Trim();
            reading.Timestamp = ts;
            return true;
        }
    }
}
