using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DeviceSimulator
{
    public partial class Form1 : Form
    {
        // 채널 정의: 이름, 단위, 기준값, 변동폭
        private sealed record Channel(string Name, string Unit, double Center, double Swing);

        private static readonly Channel[] Channels =
        {
            new("T1", "C",   25.0,  5.0),   // 온도 20~30
            new("T2", "C",   65.0, 10.0),   // 온도 55~75
            new("P1", "kPa", 100.0, 5.0),   // 압력 95~105
        };

        private TcpListener? _listener;
        private TcpClient? _client;
        private NetworkStream? _stream;
        private System.Windows.Forms.Timer _timer = new();
        private readonly Random _rnd = new();

        private TextBox _log = new();
        private Button _btnStart = new();
        private CheckBox _chkCorrupt = new();
        private Panel _top = new();

        public Form1()
        {
            Text = "장비 시뮬레이터";
            Width = 500; Height = 400;

            _btnStart.Text = "송신 시작";
            _btnStart.Dock = DockStyle.Left;
            _btnStart.Width = 120;
            _btnStart.Click += BtnStart_Click;

            _chkCorrupt.Text = "불량 데이터 섞기";
            _chkCorrupt.Dock = DockStyle.Left;
            _chkCorrupt.Width = 140;
            _chkCorrupt.Padding = new Padding(10, 0, 0, 0);
            _chkCorrupt.TextAlign = ContentAlignment.MiddleLeft;

            _top.Dock = DockStyle.Top;
            _top.Height = 40;
            _top.Controls.Add(_chkCorrupt);
            _top.Controls.Add(_btnStart);

            _log.Multiline = true;
            _log.ScrollBars = ScrollBars.Vertical;
            _log.Dock = DockStyle.Fill;
            _log.ReadOnly = true;

            Controls.Add(_log);
            Controls.Add(_top);

            _timer.Interval = 1000;
            _timer.Tick += Timer_Tick;
        }

        private async void BtnStart_Click(object? sender, EventArgs e)
        {
            _btnStart.Enabled = false;
            AddLog("포트 5000에서 대기 중...");

            _listener = new TcpListener(IPAddress.Loopback, 5000);
            _listener.Start();

            _client = await _listener.AcceptTcpClientAsync();
            _stream = _client.GetStream();

            AddLog("본체 연결됨. 송신 시작.");
            _timer.Start();
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            if (_stream == null) return;

            // 한 틱에 모든 채널을 연달아 전송.
            // 실제 장비도 이렇게 몰아서 보내므로 수신 측 버퍼 분리가 제대로 도는지 확인된다.
            var sb = new StringBuilder();

            foreach (var ch in Channels)
            {
                double value = ch.Center + (_rnd.NextDouble() * 2 - 1) * ch.Swing;
                sb.Append(BuildLine(ch, value));
            }

            // 불량 데이터: 파싱 실패 처리와 raw 컬럼의 존재 이유를 시연하기 위한 옵션
            if (_chkCorrupt.Checked && _rnd.NextDouble() < 0.25)
                sb.Append(BuildCorruptLine());

            byte[] bytes = Encoding.ASCII.GetBytes(sb.ToString());
            _stream.Write(bytes, 0, bytes.Length);

            foreach (string line in sb.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
                AddLog("송신: " + line);
        }

        private string BuildLine(Channel ch, double value)
        {
            // InvariantCulture: 한국 로케일에서도 소수점이 '.'로 나가도록 고정
            return string.Format(CultureInfo.InvariantCulture,
                "$DATA,{0},{1:F1},{2},{3:yyyy-MM-ddTHH:mm:ss}\r\n",
                ch.Name, value, ch.Unit, DateTime.Now);
        }

        private string BuildCorruptLine()
        {
            return _rnd.Next(3) switch
            {
                0 => "$DATA,T1,ERR,C,2026-01-01T00:00:00\r\n",  // 값이 숫자가 아님
                1 => "$DATA,T1,23.5\r\n",                       // 필드 잘림
                _ => "@@NOISE@@\r\n",                           // 헤더 불일치
            };
        }

        private void AddLog(string msg)
        {
            _log.AppendText(msg + Environment.NewLine);
        }
    }
}
