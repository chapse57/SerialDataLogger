namespace SerialDataLogger
{
    /// <summary>
    /// 측정값이 설정 범위를 벗어났는지 판정한다.
    /// 벗어난 '상태'를 채널별로 기억해서, 들어갈 때 한 번만 알람을 낸다.
    /// </summary>
    public class AlarmMonitor
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, Threshold> _thresholds = new();

        // 채널별 현재 상태: "" = 정상, "HI" = 상한 초과, "LO" = 하한 미달
        private readonly Dictionary<string, string> _state = new();

        /// <summary>현재 이상 상태인 채널이 하나라도 있는가.</summary>
        public bool HasActiveAlarm
        {
            get { lock (_lock) return _state.Values.Any(s => s != ""); }
        }

        public void SetThresholds(IEnumerable<Threshold> list)
        {
            lock (_lock)
            {
                _thresholds.Clear();
                foreach (var t in list)
                    _thresholds[t.Channel] = t;

                // 설정이 바뀌면 상태를 초기화한다.
                // 안 그러면 방금 지운 임계값의 '초과 중' 상태가 남아 복구 알람이 안 뜬다.
                _state.Clear();
            }
        }

        /// <summary>
        /// 값 하나를 검사한다.
        /// 상태가 바뀐 순간에만 결과를 돌려주고, 같은 상태가 이어지면 null을 준다.
        /// </summary>
        public AlarmResult? Check(string channel, DateTime ts, double value)
        {
            lock (_lock)
            {
                if (!_thresholds.TryGetValue(channel, out var t) || !t.Enabled)
                    return null;

                string now = "";
                double limit = 0;

                if (t.Hi.HasValue && value > t.Hi.Value)
                {
                    now = "HI";
                    limit = t.Hi.Value;
                }
                else if (t.Lo.HasValue && value < t.Lo.Value)
                {
                    now = "LO";
                    limit = t.Lo.Value;
                }

                _state.TryGetValue(channel, out string? prev);
                prev ??= "";

                if (now == prev) return null;    // 상태 그대로 → 알릴 것 없음

                _state[channel] = now;

                if (now == "")
                {
                    // 이상 → 정상. 복구도 알려야 담당자가 상황을 안다.
                    return new AlarmResult
                    {
                        IsRecovery = true,
                        Event = new AlarmEvent
                        {
                            Timestamp = ts,
                            Channel = channel,
                            Value = value,
                            Kind = prev,
                            Limit = 0,
                        }
                    };
                }

                return new AlarmResult
                {
                    IsRecovery = false,
                    Event = new AlarmEvent
                    {
                        Timestamp = ts,
                        Channel = channel,
                        Value = value,
                        Kind = now,
                        Limit = limit,
                    }
                };
            }
        }
    }

    public class AlarmResult
    {
        public bool IsRecovery { get; set; }
        public AlarmEvent Event { get; set; } = new();
    }
}
