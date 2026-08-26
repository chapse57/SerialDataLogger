using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace SerialDataLogger
{
    public partial class Form1 : Form
    {
        private const int MaxLogLines = 500;

        private TcpClient? _client;
        private NetworkStream? _stream;
        private readonly StringBuilder _buffer = new();

        // --- 공통 ---
        private TabControl _tabs = new();
        private Button _btnConnect = new();
        private StatusStrip _status = new();
        private ToolStripStatusLabel _lblOk = new();
        private ToolStripStatusLabel _lblFail = new();
        private ToolStripStatusLabel _lblDb = new();

        // --- 탭 1: 실시간 로그 ---
        private TextBox _log = new();

        // --- 탭 2: 데이터 표 ---
        private DataGridView _grid = new();
        private DateTimePicker _dtFrom = new();
        private DateTimePicker _dtTo = new();
        private ComboBox _cboChannel = new();
        private Button _btnQuery = new();
        private Label _lblResult = new();

        private int _okCount;
        private int _failCount;

        private readonly Database _db = new();
        private readonly List<Reading> _pending = new();
        private readonly object _pendingLock = new();
        private readonly System.Windows.Forms.Timer _saveTimer = new();

        public Form1()
        {
            Text = "데이터 수집기";
            Width = 900; Height = 600;

            _btnConnect.Text = "연결";
            _btnConnect.Dock = DockStyle.Top;
            _btnConnect.Height = 40;
            _btnConnect.Click += BtnConnect_Click;

            _tabs.Dock = DockStyle.Fill;
            _tabs.TabPages.Add(BuildLogTab());
            _tabs.TabPages.Add(BuildTableTab());

            _lblOk.Text = "정상 0";
            _lblFail.Text = "오류 0";
            _lblFail.ForeColor = Color.Firebrick;
            _lblDb.IsLink = true;
            _lblDb.Click += (_, _) => OpenDbFolder();

            _status.Items.Add(_lblOk);
            _status.Items.Add(new ToolStripStatusLabel("|"));
            _status.Items.Add(_lblFail);
            _status.Items.Add(new ToolStripStatusLabel("|"));
            _status.Items.Add(_lblDb);

            Controls.Add(_tabs);
            Controls.Add(_status);
            Controls.Add(_btnConnect);

            _saveTimer.Interval = 1000;
            _saveTimer.Tick += SaveTimer_Tick;

            Load += Form1_Load;
        }

        private TabPage BuildLogTab()
        {
            var page = new TabPage("실시간 로그");

            _log.Multiline = true;
            _log.ScrollBars = ScrollBars.Vertical;
            _log.Dock = DockStyle.Fill;
            _log.ReadOnly = true;
            _log.Font = new Font("Consolas", 9F);

            page.Controls.Add(_log);
            return page;
        }

        private TabPage BuildTableTab()
        {
            var page = new TabPage("데이터 표");

            // 상단 필터 바
            var bar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 40,
                Padding = new Padding(4)
            };

            _dtFrom.Format = DateTimePickerFormat.Custom;
            _dtFrom.CustomFormat = "yyyy-MM-dd HH:mm";
            _dtFrom.Width = 140;
            _dtFrom.Value = DateTime.Today;

            _dtTo.Format = DateTimePickerFormat.Custom;
            _dtTo.CustomFormat = "yyyy-MM-dd HH:mm";
            _dtTo.Width = 140;
            _dtTo.Value = DateTime.Today.AddDays(1);

            _cboChannel.DropDownStyle = ComboBoxStyle.DropDownList;
            _cboChannel.Width = 100;

            _btnQuery.Text = "조회";
            _btnQuery.Width = 70;
            _btnQuery.Click += BtnQuery_Click;

            _lblResult.AutoSize = true;
            _lblResult.Padding = new Padding(10, 6, 0, 0);
            _lblResult.ForeColor = SystemColors.GrayText;

            bar.Controls.Add(new Label { Text = "기간", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
            bar.Controls.Add(_dtFrom);
            bar.Controls.Add(new Label { Text = "~", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
            bar.Controls.Add(_dtTo);
            bar.Controls.Add(new Label { Text = "채널", AutoSize = true, Padding = new Padding(10, 6, 0, 0) });
            bar.Controls.Add(_cboChannel);
            bar.Controls.Add(_btnQuery);
            bar.Controls.Add(_lblResult);

            // 표
            _grid.Dock = DockStyle.Fill;
            _grid.ReadOnly = true;                                  // 측정 원본은 화면에서 못 고친다
            _grid.AllowUserToAddRows = false;
            _grid.AllowUserToDeleteRows = false;
            _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _grid.RowHeadersVisible = false;
            _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;

            _grid.Columns.Add("ts", "시각");
            _grid.Columns.Add("channel", "채널");
            _grid.Columns.Add("value", "값");
            _grid.Columns.Add("unit", "단위");
            _grid.Columns.Add("raw", "원문");

            _grid.Columns["ts"]!.FillWeight = 20;
            _grid.Columns["channel"]!.FillWeight = 10;
            _grid.Columns["value"]!.FillWeight = 10;
            _grid.Columns["unit"]!.FillWeight = 8;
            _grid.Columns["raw"]!.FillWeight = 52;

            page.Controls.Add(_grid);
            page.Controls.Add(bar);
            return page;
        }

        private void Form1_Load(object? sender, EventArgs e)
        {
            try
            {
                _db.Initialize();
                _lblDb.Text = _db.FilePath;
                AddLog($"DB 준비됨 ({_db.CountRows():N0}행): {_db.FilePath}");
                _saveTimer.Start();
                ReloadChannels();
            }
            catch (Exception ex)
            {
                MessageBox.Show("데이터베이스를 열 수 없습니다.\n\n" + ex.Message,
                    "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
                _btnConnect.Enabled = false;
            }
        }

        private void ReloadChannels()
        {
            string? keep = _cboChannel.SelectedItem as string;

            _cboChannel.Items.Clear();
            _cboChannel.Items.Add("(전체)");
            foreach (string ch in _db.GetChannels())
                _cboChannel.Items.Add(ch);

            int idx = keep != null ? _cboChannel.Items.IndexOf(keep) : -1;
            _cboChannel.SelectedIndex = idx >= 0 ? idx : 0;
        }

        private void BtnQuery_Click(object? sender, EventArgs e)
        {
            string? channel = _cboChannel.SelectedIndex > 0
                ? _cboChannel.SelectedItem as string
                : null;

            _btnQuery.Enabled = false;
            Cursor = Cursors.WaitCursor;

            try
            {
                var rows = _db.Query(_dtFrom.Value, _dtTo.Value, channel);

                _grid.SuspendLayout();
                _grid.Rows.Clear();

                foreach (var r in rows)
                {
                    int i = _grid.Rows.Add(
                        r.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"),
                        r.Channel,
                        r.Channel == "(오류)" ? "" : r.Value.ToString("F1", CultureInfo.InvariantCulture),
                        r.Unit,
                        r.Raw);

                    if (r.Channel == "(오류)")
                        _grid.Rows[i].DefaultCellStyle.BackColor = Color.MistyRose;
                }

                _grid.ResumeLayout();
                _lblResult.Text = $"{rows.Count:N0}건 (최대 5,000건)";
            }
            catch (Exception ex)
            {
                MessageBox.Show("조회 실패\n\n" + ex.Message,
                    "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _btnQuery.Enabled = true;
                Cursor = Cursors.Default;
            }
        }

        private void OpenDbFolder()
        {
            string? dir = Path.GetDirectoryName(_db.FilePath);
            if (dir != null)
                System.Diagnostics.Process.Start("explorer.exe", dir);
        }

        private void SaveTimer_Tick(object? sender, EventArgs e)
        {
            List<Reading> batch;

            lock (_pendingLock)
            {
                if (_pending.Count == 0) return;
                batch = new List<Reading>(_pending);
                _pending.Clear();
            }

            try
            {
                _db.InsertMany(batch);
                _lblDb.Text = $"{_db.FilePath}  ({_db.CountRows():N0}행)";
            }
            catch (Exception ex)
            {
                AddLog("저장 실패: " + ex.Message);
            }
        }

        private async void BtnConnect_Click(object? sender, EventArgs e)
        {
            _btnConnect.Enabled = false;
            AddLog("연결 시도 중...");

            try
            {
                _client = new TcpClient();
                await _client.ConnectAsync("127.0.0.1", 5000);
                _stream = _client.GetStream();
            }
            catch (SocketException ex)
            {
                AddLog("연결 실패: " + ex.Message);
                _btnConnect.Enabled = true;
                return;
            }

            AddLog("연결됨. 수신 대기.");
            _ = ReceiveLoop();
        }

        private async Task ReceiveLoop()
        {
            byte[] buf = new byte[1024];

            try
            {
                while (_stream != null)
                {
                    int n = await _stream.ReadAsync(buf, 0, buf.Length);
                    if (n == 0) break;

                    _buffer.Append(Encoding.ASCII.GetString(buf, 0, n));
                    ExtractLines();
                }
            }
            catch (IOException ex)
            {
                AddLog("수신 중단: " + ex.Message);
            }

            AddLog("연결 종료됨.");
        }

        /// <summary>
        /// 스트림에는 메시지 경계가 없다. \r\n이 나올 때까지 버퍼에 쌓아두고
        /// 완성된 줄만 잘라내야 잘린 데이터가 파서로 넘어가지 않는다.
        /// </summary>
        private void ExtractLines()
        {
            while (true)
            {
                string s = _buffer.ToString();
                int idx = s.IndexOf("\r\n");
                if (idx < 0) break;

                string line = s.Substring(0, idx);
                _buffer.Remove(0, idx + 2);

                HandleLine(line);
            }
        }

        private void HandleLine(string line)
        {
            if (ReadingParser.TryParse(line, out Reading r, out string error))
            {
                _okCount++;
                AddLog(string.Format(CultureInfo.InvariantCulture,
                    "{0:HH:mm:ss}  {1,-4} {2,8:F1} {3}",
                    r.Timestamp, r.Channel, r.Value, r.Unit));
            }
            else
            {
                _failCount++;
                AddLog($"[오류] {error}  원문: {line}");

                r.Channel = "(오류)";
                r.Timestamp = DateTime.Now;
            }

            lock (_pendingLock)
            {
                _pending.Add(r);
            }

            UpdateCounters();
        }

        private void UpdateCounters()
        {
            if (InvokeRequired) { Invoke(UpdateCounters); return; }

            _lblOk.Text = $"정상 {_okCount}";
            _lblFail.Text = $"오류 {_failCount}";
        }

        /// <summary>
        /// 수신은 백그라운드 스레드에서 돈다. UI 컨트롤을 직접 건드리면 크래시하므로
        /// InvokeRequired로 UI 스레드에 넘긴다.
        /// </summary>
        private void AddLog(string msg)
        {
            if (InvokeRequired)
            {
                Invoke(() => AddLog(msg));
                return;
            }

            if (_log.Lines.Length > MaxLogLines)
                _log.Lines = _log.Lines.Skip(MaxLogLines / 2).ToArray();

            _log.AppendText(msg + Environment.NewLine);
        }
    }
}
