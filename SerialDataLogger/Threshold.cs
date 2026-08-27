namespace SerialDataLogger
{
    /// <summary>채널별 상·하한 설정.</summary>
    public class Threshold
    {
        public string Channel { get; set; } = "";

        // null이면 그쪽 한계는 검사하지 않는다.
        // 0으로 두면 "0 이하는 알람"이 되어버리므로 반드시 null과 구분해야 한다.
        public double? Lo { get; set; }
        public double? Hi { get; set; }

        public bool Enabled { get; set; } = true;
    }

    /// <summary>알람 발생 기록 한 건.</summary>
    public class AlarmEvent
    {
        public DateTime Timestamp { get; set; }
        public string Channel { get; set; } = "";
        public double Value { get; set; }
        public string Kind { get; set; } = "";   // "HI" / "LO"
        public double Limit { get; set; }        // 발생 시점에 적용되던 한계값
    }
}
