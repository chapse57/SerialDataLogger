namespace SerialDataLogger
{
    /// <summary>
    /// 차트에 그릴 최근 데이터만 채널별로 들고 있는 버퍼.
    /// DB에는 전부 쌓이지만, 화면에 그리는 건 최근 것뿐이다.
    /// </summary>
    public class ChartBuffer
    {
        private readonly int _capacity;
        private readonly object _lock = new();

        // 채널 이름 -> (시각, 값) 목록
        private readonly Dictionary<string, Queue<(DateTime Ts, double Value)>> _series = new();

        public ChartBuffer(int capacity = 300)   // 초당 1건 기준 5분치
        {
            _capacity = capacity;
        }

        public void Add(string channel, DateTime ts, double value)
        {
            lock (_lock)
            {
                if (!_series.TryGetValue(channel, out var q))
                {
                    q = new Queue<(DateTime, double)>();
                    _series[channel] = q;
                }

                q.Enqueue((ts, value));

                // 새 걸 넣은 만큼 오래된 걸 버린다. 그래서 메모리가 일정하게 유지된다.
                while (q.Count > _capacity)
                    q.Dequeue();
            }
        }

        /// <summary>차트가 바로 쓸 수 있는 배열 형태로 복사해서 넘긴다.</summary>
        public Dictionary<string, (double[] Xs, double[] Ys)> Snapshot()
        {
            var result = new Dictionary<string, (double[], double[])>();

            lock (_lock)
            {
                foreach (var (channel, q) in _series)
                {
                    var items = q.ToArray();
                    var xs = new double[items.Length];
                    var ys = new double[items.Length];

                    for (int i = 0; i < items.Length; i++)
                    {
                        xs[i] = items[i].Ts.ToOADate();   // 차트가 이해하는 날짜 숫자
                        ys[i] = items[i].Value;
                    }

                    result[channel] = (xs, ys);
                }
            }

            return result;
        }

        public void Clear()
        {
            lock (_lock)
            {
                _series.Clear();
            }
        }
    }
}
