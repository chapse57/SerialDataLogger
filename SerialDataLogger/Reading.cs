namespace SerialDataLogger
{
    /// <summary>
    /// 장비에서 수신한 측정값 한 건.
    /// Raw는 원문 그대로 보관 — 파싱 규칙이 바뀌거나 오류 추적이 필요할 때 재해석 가능해야 함.
    /// </summary>
    public class Reading
    {
        public DateTime Timestamp { get; set; }
        public string Channel { get; set; } = "";
        public double Value { get; set; }
        public string Unit { get; set; } = "";
        public string Raw { get; set; } = "";
    }
}
