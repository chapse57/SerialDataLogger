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
        private Button _btnExport = new();
        private Label _lblResult = new();

        // 화면에 뿌린 것과 엑셀로 나가는 것이 반드시 같아야 한다.
        // 내보낼 때 DB를 다시 조회하면 그사이 들어온 데이터가 섞여 어긋난다.
        private List<Reading> _lastQuery = new();

        // --- 탭 3: 차트 ---
        private ScottPlot.WinForms.FormsPlot _plot = new();
        private CheckBox _chkAutoScale = new();
        private readonly ChartBuffer _chartBuffer = new(300);
        private readonly System.Windows.Forms.Timer _chartTimer = new();

        // --- 탭 4: 알람 설정 ---
        private DataGridView _gridThreshold = new();
        private DataGridView _gridAlarmLog = new();
        private readonly AlarmMonitor _monitor = new();
        private readonly List<AlarmEvent> _pendingAlarms = new();
        private ToolStripStatusLabel _lblAlarm = new();

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
            _tabs.TabPages.Add(BuildChartTab());
            _tabs.TabPages.Add(BuildAlarmTab());

            _lblOk.Text = "정상 0";
            _lblFail.Text = "오류 0";
            _lblFail.ForeColor = Color.Firebrick;
            _lblDb.IsLink = true;
            _lblDb.Click += (_, _) => OpenDbFolder();

            _lblAlarm.Text = "알람 없음";

            _status.Items.Add(_lblAlarm);
            _status.Items.Add(new ToolStripStatusLabel("|"));
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

            // 데이터는 초당 3건 들어오지만 화면은 초당 2번만 다시 그린다.
            // 사람 눈에는 차이가 없고, CPU는 훨씬 덜 쓴다.
            _chartTimer.Interval = 500;
            _chartTimer.Tick += ChartTimer_Tick;

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

            _btnExport.Text = "엑셀 내보내기";
            _btnExport.Width = 110;
            _btnExport.Enabled = false;          // 조회 전에는 내보낼 게 없다
            _btnExport.Click += BtnExport_Click;

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
            bar.Controls.Add(_btnExport);
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

        private TabPage BuildChartTab()
        {
            var page = new TabPage("차트");

            var bar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 36,
                Padding = new Padding(4)
            };

            _chkAutoScale.Text = "자동 축 맞춤";
            _chkAutoScale.Checked = true;
            _chkAutoScale.AutoSize = true;
            _chkAutoScale.Padding = new Padding(0, 4, 0, 0);

            var btnClear = new Button { Text = "차트 지우기", Width = 100 };
            btnClear.Click += (_, _) => { _chartBuffer.Clear(); _plot.Plot.Clear(); _plot.Refresh(); };

            bar.Controls.Add(_chkAutoScale);
            bar.Controls.Add(btnClear);

            _plot.Dock = DockStyle.Fill;
            _plot.Plot.Axes.DateTimeTicksBottom();

            // 한글이 깨지지 않도록 폰트를 지정한다.
            // 기본 폰트에 한글 자형이 없으면 네모나 깨진 글자로 나온다.
            _plot.Plot.Axes.Bottom.Label.FontName = "맑은 고딕";
            _plot.Plot.Axes.Left.Label.FontName = "맑은 고딕";
            _plot.Plot.Legend.FontName = "맑은 고딕";

            _plot.Plot.XLabel("시각");
            _plot.Plot.YLabel("측정값");

            // 렌더링 시간 표시는 개발용이다. 납품 화면에 남아 있으면 안 된다.
            _plot.Plot.Benchmark.IsVisible = false;

            page.Controls.Add(_plot);
            page.Controls.Add(bar);
            return page;
        }

        private TabPage BuildAlarmTab()
        {
            var page = new TabPage("알람 설정");

            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterDistance = 200
            };

            // --- 위: 임계값 설정 ---
            var topBar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, Padding = new Padding(4) };

            var btnAdd = new Button { Text = "채널 추가", Width = 90 };
            btnAdd.Click += BtnAddThreshold_Click;

            var btnDel = new Button { Text = "삭제", Width = 60 };
            btnDel.Click += BtnDeleteThreshold_Click;

            var btnSave = new Button { Text = "저장", Width = 60 };
            btnSave.Click += BtnSaveThresholds_Click;

            topBar.Controls.Add(new Label { Text = "임계값", AutoSize = true, Padding = new Padding(0, 6, 10, 0) });
            topBar.Controls.Add(btnAdd);
            topBar.Controls.Add(btnDel);
            topBar.Controls.Add(btnSave);

            _gridThreshold.Dock = DockStyle.Fill;
            _gridThreshold.AllowUserToAddRows = false;
            _gridThreshold.RowHeadersVisible = false;
            _gridThreshold.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;

            _gridThreshold.Columns.Add(new DataGridViewTextBoxColumn { Name = "channel", HeaderText = "채널" });
            _gridThreshold.Columns.Add(new DataGridViewTextBoxColumn { Name = "lo", HeaderText = "하한 (비우면 미검사)" });
            _gridThreshold.Columns.Add(new DataGridViewTextBoxColumn { Name = "hi", HeaderText = "상한 (비우면 미검사)" });
            _gridThreshold.Columns.Add(new DataGridViewCheckBoxColumn { Name = "enabled", HeaderText = "사용" });

            split.Panel1.Controls.Add(_gridThreshold);
            split.Panel1.Controls.Add(topBar);

            // --- 아래: 알람 이력 ---
            var botBar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, Padding = new Padding(4) };

            var btnRefresh = new Button { Text = "이력 새로고침", Width = 110 };
            btnRefresh.Click += (_, _) => ReloadAlarmLog();

            botBar.Controls.Add(new Label { Text = "알람 이력", AutoSize = true, Padding = new Padding(0, 6, 10, 0) });
            botBar.Controls.Add(btnRefresh);

            _gridAlarmLog.Dock = DockStyle.Fill;
            _gridAlarmLog.ReadOnly = true;
            _gridAlarmLog.AllowUserToAddRows = false;
            _gridAlarmLog.RowHeadersVisible = false;
            _gridAlarmLog.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _gridAlarmLog.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;

            _gridAlarmLog.Columns.Add("ts", "시각");
            _gridAlarmLog.Columns.Add("channel", "채널");
            _gridAlarmLog.Columns.Add("kind", "종류");
            _gridAlarmLog.Columns.Add("value", "측정값");
            _gridAlarmLog.Columns.Add("limit", "당시 한계값");

            split.Panel2.Controls.Add(_gridAlarmLog);
            split.Panel2.Controls.Add(botBar);

            page.Controls.Add(split);
            return page;
        }

        private void BtnAddThreshold_Click(object? sender, EventArgs e)
        {
            int i = _gridThreshold.Rows.Add("", "", "", true);
            _gridThreshold.CurrentCell = _gridThreshold.Rows[i].Cells[0];
            _gridThreshold.BeginEdit(true);
        }

        private void BtnDeleteThreshold_Click(object? sender, EventArgs e)
        {
            if (_gridThreshold.CurrentRow == null) return;

            string channel = _gridThreshold.CurrentRow.Cells["channel"].Value?.ToString() ?? "";
            _gridThreshold.Rows.Remove(_gridThreshold.CurrentRow);

            if (!string.IsNullOrWhiteSpace(channel))
                _db.DeleteThreshold(channel);

            ApplyThresholds();
        }

        private void BtnSaveThresholds_Click(object? sender, EventArgs e)
        {
            var list = new List<Threshold>();

            foreach (DataGridViewRow row in _gridThreshold.Rows)
            {
                string channel = row.Cells["channel"].Value?.ToString()?.Trim() ?? "";
                if (string.IsNullOrEmpty(channel)) continue;

                // 사용자가 뭘 칠지 모른다. 숫자가 아니면 거부하고 어디가 틀렸는지 알려준다.
                if (!TryParseLimit(row.Cells["lo"].Value, out double? lo))
                {
                    MessageBox.Show($"{channel} 채널의 하한값이 숫자가 아닙니다.",
                        "입력 오류", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                if (!TryParseLimit(row.Cells["hi"].Value, out double? hi))
                {
                    MessageBox.Show($"{channel} 채널의 상한값이 숫자가 아닙니다.",
                        "입력 오류", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                if (lo.HasValue && hi.HasValue && lo.Value >= hi.Value)
                {
                    MessageBox.Show($"{channel} 채널의 하한이 상한보다 크거나 같습니다.",
                        "입력 오류", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                list.Add(new Threshold
                {
                    Channel = channel,
                    Lo = lo,
                    Hi = hi,
                    Enabled = row.Cells["enabled"].Value as bool? ?? true,
                });
            }

            foreach (var t in list)
                _db.SaveThreshold(t);

            ApplyThresholds();
            AddLog($"임계값 {list.Count}건 저장됨.");
        }

        private static bool TryParseLimit(object? cell, out double? value)
        {
            value = null;
            string s = cell?.ToString()?.Trim() ?? "";

            if (string.IsNullOrEmpty(s)) return true;    // 빈 칸은 '검사 안 함'

            if (double.TryParse(s, out double d))
            {
                value = d;
                return true;
            }

            return false;
        }

        private void ApplyThresholds()
        {
            var list = _db.LoadThresholds();
            _monitor.SetThresholds(list);

            _gridThreshold.Rows.Clear();
            foreach (var t in list)
                _gridThreshold.Rows.Add(t.Channel, t.Lo?.ToString() ?? "", t.Hi?.ToString() ?? "", t.Enabled);
        }

        private void ReloadAlarmLog()
        {
            var list = _db.QueryAlarms(DateTime.Today, DateTime.Today.AddDays(1));

            _gridAlarmLog.Rows.Clear();
            foreach (var a in list)
            {
                int i = _gridAlarmLog.Rows.Add(
                    a.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"),
                    a.Channel,
                    a.Kind == "HI" ? "상한 초과" : "하한 미달",
                    a.Value.ToString("F1"),
                    a.Limit.ToString("F1"));

                _gridAlarmLog.Rows[i].DefaultCellStyle.BackColor =
                    a.Kind == "HI" ? Color.MistyRose : Color.LightCyan;
            }
        }

        private void ChartTimer_Tick(object? sender, EventArgs e)
        {
            // 차트 탭을 안 보고 있으면 그릴 이유가 없다.
            if (_tabs.SelectedTab?.Text != "차트") return;

            var data = _chartBuffer.Snapshot();
            if (data.Count == 0) return;

            _plot.Plot.Clear();

            foreach (var (channel, series) in data)
            {
                if (series.Xs.Length < 2) continue;

                var line = _plot.Plot.Add.ScatterLine(series.Xs, series.Ys);
                line.LegendText = channel;
                line.LineWidth = 2;
            }

            _plot.Plot.ShowLegend();

            if (_chkAutoScale.Checked)
                _plot.Plot.Axes.AutoScale();

            _plot.Refresh();
        }

        private void Form1_Load(object? sender, EventArgs e)
        {
            try
            {
                _db.Initialize();
                _lblDb.Text = _db.FilePath;
                AddLog($"DB 준비됨 ({_db.CountRows():N0}행): {_db.FilePath}");
                _saveTimer.Start();
                _chartTimer.Start();
                ReloadChannels();
                ApplyThresholds();
                ReloadAlarmLog();
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
                _lastQuery = rows;
                _btnExport.Enabled = rows.Count > 0;
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

        private void BtnExport_Click(object? sender, EventArgs e)
        {
            using var dlg = new SaveFileDialog
            {
                Filter = "Excel 파일 (*.xlsx)|*.xlsx",
                // 파일명에 조회 조건을 박아둔다. 나중에 어떤 데이터인지 파일만 봐도 알 수 있게.
                FileName = $"측정데이터_{_dtFrom.Value:yyyyMMdd}_{_dtTo.Value:yyyyMMdd}.xlsx"
            };

            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            _btnExport.Enabled = false;
            Cursor = Cursors.WaitCursor;

            try
            {
                ExcelExporter.Export(_lastQuery, dlg.FileName);

                var answer = MessageBox.Show(
                    $"{_lastQuery.Count:N0}건을 저장했습니다.\n\n파일을 열어보시겠습니까?",
                    "완료", MessageBoxButtons.YesNo, MessageBoxIcon.Information);

                if (answer == DialogResult.Yes)
                    System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo(dlg.FileName) { UseShellExecute = true });
            }
            catch (IOException)
            {
                // 같은 파일을 엑셀에서 열어둔 채로 덮어쓰려는 경우. 현장에서 자주 난다.
                MessageBox.Show(
                    "파일에 쓸 수 없습니다.\n\n같은 이름의 파일이 엑셀에서 열려 있다면 닫고 다시 시도하세요.",
                    "오류", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show("내보내기 실패\n\n" + ex.Message,
                    "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _btnExport.Enabled = true;
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
            List<AlarmEvent> alarmBatch;

            lock (_pendingLock)
            {
                batch = new List<Reading>(_pending);
                _pending.Clear();

                alarmBatch = new List<AlarmEvent>(_pendingAlarms);
                _pendingAlarms.Clear();
            }

            if (batch.Count == 0 && alarmBatch.Count == 0) return;

            try
            {
                if (batch.Count > 0)
                {
                    _db.InsertMany(batch);
                    _lblDb.Text = $"{_db.FilePath}  ({_db.CountRows():N0}행)";
                }

                if (alarmBatch.Count > 0)
                    _db.InsertAlarms(alarmBatch);
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

                _chartBuffer.Add(r.Channel, r.Timestamp, r.Value);
                CheckAlarm(r);
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

        private void CheckAlarm(Reading r)
        {
            var result = _monitor.Check(r.Channel, r.Timestamp, r.Value);
            if (result == null) return;

            var ev = result.Event;

            if (result.IsRecovery)
            {
                AddLog($"[복구] {ev.Channel} 정상 범위로 복귀 ({ev.Value:F1})");
            }
            else
            {
                string kindText = ev.Kind == "HI" ? "상한 초과" : "하한 미달";
                AddLog($"[알람] {ev.Channel} {kindText}  값 {ev.Value:F1} / 한계 {ev.Limit:F1}");

                // 알람도 수집 데이터와 같은 방식으로 모았다가 저장한다.
                lock (_pendingLock)
                {
                    _pendingAlarms.Add(ev);
                }
            }

            UpdateAlarmStatus();
        }

        private void UpdateAlarmStatus()
        {
            if (InvokeRequired) { Invoke(UpdateAlarmStatus); return; }

            if (_monitor.HasActiveAlarm)
            {
                _lblAlarm.Text = "● 알람 발생";
                _lblAlarm.ForeColor = Color.White;
                _lblAlarm.BackColor = Color.Firebrick;
            }
            else
            {
                _lblAlarm.Text = "알람 없음";
                _lblAlarm.ForeColor = SystemColors.ControlText;
                _lblAlarm.BackColor = Color.Transparent;
            }
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
