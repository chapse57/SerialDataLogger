using System.Net.Sockets;
using System.Text;

namespace SerialDataLogger
{
    public partial class Form1 : Form
    {
        private TcpClient? _client;
        private NetworkStream? _stream;
        private readonly StringBuilder _buffer = new();
        private TextBox _log = new();
        private Button _btnConnect = new();

        public Form1()
        {
            Text = "데이터 수집기";
            Width = 600; Height = 400;

            _btnConnect.Text = "연결";
            _btnConnect.Dock = DockStyle.Top;
            _btnConnect.Height = 40;
            _btnConnect.Click += BtnConnect_Click;

            _log.Multiline = true;
            _log.ScrollBars = ScrollBars.Vertical;
            _log.Dock = DockStyle.Fill;
            _log.ReadOnly = true;

            Controls.Add(_log);
            Controls.Add(_btnConnect);
        }

        private async void BtnConnect_Click(object? sender, EventArgs e)
        {
            _btnConnect.Enabled = false;
            AddLog("연결 시도 중...");

            _client = new TcpClient();
            await _client.ConnectAsync("127.0.0.1", 5000);
            _stream = _client.GetStream();

            AddLog("연결됨. 수신 대기.");
            _ = ReceiveLoop();
        }

        private async Task ReceiveLoop()
        {
            byte[] buf = new byte[1024];

            while (_stream != null)
            {
                int n = await _stream.ReadAsync(buf, 0, buf.Length);
                if (n == 0) break;

                _buffer.Append(Encoding.ASCII.GetString(buf, 0, n));
                ExtractLines();
            }
        }

        private void ExtractLines()
        {
            while (true)
            {
                string s = _buffer.ToString();
                int idx = s.IndexOf("\r\n");
                if (idx < 0) break;

                string line = s.Substring(0, idx);
                _buffer.Remove(0, idx + 2);

                AddLog("수신: " + line);
            }
        }

        private void AddLog(string msg)
        {
            if (InvokeRequired)
            {
                Invoke(() => AddLog(msg));
                return;
            }
            _log.AppendText(msg + Environment.NewLine);
        }
    }
}