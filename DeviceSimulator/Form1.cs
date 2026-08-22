using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DeviceSimulator
{
    public partial class Form1 : Form
    {
        private TcpListener? _listener;
        private TcpClient? _client;
        private NetworkStream? _stream;
        private System.Windows.Forms.Timer _timer = new();
        private readonly Random _rnd = new();
        private TextBox _log = new();
        private Button _btnStart = new();

        public Form1()
        {
            Text = "장비 시뮬레이터";
            Width = 500; Height = 400;

            _btnStart.Text = "송신 시작";
            _btnStart.Dock = DockStyle.Top;
            _btnStart.Height = 40;
            _btnStart.Click += BtnStart_Click;

            _log.Multiline = true;
            _log.ScrollBars = ScrollBars.Vertical;
            _log.Dock = DockStyle.Fill;
            _log.ReadOnly = true;

            Controls.Add(_log);
            Controls.Add(_btnStart);

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

            double value = 20.0 + _rnd.NextDouble() * 10.0;
            string line = $"$DATA,T1,{value:F1},C,{DateTime.Now:yyyy-MM-ddTHH:mm:ss}\r\n";

            byte[] bytes = Encoding.ASCII.GetBytes(line);
            _stream.Write(bytes, 0, bytes.Length);

            AddLog("송신: " + line.TrimEnd());
        }

        private void AddLog(string msg)
        {
            _log.AppendText(msg + Environment.NewLine);
        }
    }
}