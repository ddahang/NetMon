// NetMon.cs - 网络流量统计工具 (WinForms, C#5, compile with .NET Framework 4 csc)
// 用法: NetMon.exe           打开主界面
//       NetMon.exe /sample   隐藏模式: 采样一次并写盘后退出 (供计划任务调用)
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Web.Script.Serialization;

namespace NetMon
{
    public class MainForm : Form
    {
        const string TaskName = "NetTrafficMonitor";
        const string AppTitle = "网络流量统计工具（by:吾爱 dingqh）";

        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string DataFile { get { return Path.Combine(baseDir, "data", "netmon.json"); } }

        // live sampling state
        Dictionary<string, long[]> lastCounters = new Dictionary<string, long[]>();
        double ourUnsavedUp = 0, ourUnsavedDown = 0;   // fallback writer pending bytes
        double liveTodayUp = 0, liveTodayDown = 0;     // live delta since last file reload
        double speedUp = 0, speedDown = 0;             // bytes/sec

        // data from json
        Dictionary<string, double[]> daily = new Dictionary<string, double[]>();
        string fileTs = "";
        bool taskInstalled = false;

        int tick = 0;
        System.Windows.Forms.Timer timer;
        Label lblSpeedDown, lblSpeedUp, lblTodayUp, lblTodayDown, lblTodayTotal;
        Label lblSummary, lblTaskStatus, lblUpdated;
        CheckBox chkSample, chkAutoStart;
        RadioButton rbDays30, rbMonth;
        Button btnInstall, btnUninstall, btnOpenData;
        Panel chartPanel;
        List<BarInfo> barRects = new List<BarInfo>();
        ToolTip tip = new ToolTip();
        string lastTip = "";
        NotifyIcon tray;
        ContextMenuStrip trayMenu;
        Icon appIcon;
        EventWaitHandle restoreEvent;
        bool watcherStarted = false;
        bool balloonShown = false;
        bool allowExit = false;
        float dpiScale = 1f;

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            ApplyDpiScale();
            chartPanel.Invalidate();
        }

        // 按系统 DPI 比例等比放大窗体与全部控件（字体用磅值随 DPI 自动放大，这里只缩放像素布局）
        void ApplyDpiScale()
        {
            using (var g = CreateGraphics()) dpiScale = g.DpiX / 96f;
            float s = dpiScale;
            if (s > 1.02f) {
                var saved = new Dictionary<Control, AnchorStyles>();
                foreach (var c in AllControls(this)) { saved[c] = c.Anchor; c.Anchor = AnchorStyles.Top | AnchorStyles.Left; }
                ClientSize = new Size((int)Math.Round(578 * s), (int)Math.Round(622 * s));
                foreach (var c in AllControls(this)) {
                    c.Location = new Point((int)Math.Round(c.Left * s), (int)Math.Round(c.Top * s));
                    if (!(c is Label && ((Label)c).AutoSize)) c.Size = new Size((int)Math.Round(c.Width * s), (int)Math.Round(c.Height * s));
                    if (c is RadioButton || c is CheckBox) c.Size = c.PreferredSize;
                }
                foreach (var kv in saved) kv.Key.Anchor = kv.Value;
                // 单选按钮为 AutoSize + 右锚点，缩放后按右边缘重新对齐，避免被布局二次移动
                int rightEdge = ClientSize.Width - (int)Math.Round(20 * s);
                rbMonth.Left = rightEdge - rbMonth.Width;
                rbDays30.Left = rbMonth.Left - (int)Math.Round(14 * s) - rbDays30.Width;
                rbMonth.Top = rbDays30.Top = (int)Math.Round(168 * s);
                lblUpdated.Left = Math.Min(lblUpdated.Left, rightEdge - lblUpdated.Width);
            }
            MinimumSize = new Size((int)Math.Round(560 * s), (int)Math.Round(622 * s));
        }

        static List<Control> AllControls(Control root)
        {
            var list = new List<Control>();
            foreach (Control c in root.Controls) { list.Add(c); list.AddRange(AllControls(c)); }
            return list;
        }

        public MainForm(EventWaitHandle restoreEvt) : this() { restoreEvent = restoreEvt; StartRestoreWatcher(); }

        public MainForm()
        {
            Text = AppTitle;
            StartPosition = FormStartPosition.CenterScreen;
            // DPI 自适应：手动按系统缩放比例等比放大（纯代码窗体 WinForms 内置 AutoScale 不可靠）
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.FromArgb(244, 246, 249);
            ClientSize = new Size(578, 622);
            Font = new Font("Microsoft YaHei UI", 9F);
            DoubleBuffered = true;
            try { appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            if (appIcon != null) Icon = appIcon;

            var title = new Label { Text = "网络流量统计工具", Location = new Point(20, 14), AutoSize = true,
                Font = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold), ForeColor = Color.FromArgb(45, 55, 72) };
            Controls.Add(title);

            lblUpdated = new Label { Text = "", Location = new Point(330, 22), AutoSize = true,
                Font = new Font("Microsoft YaHei UI", 8.5F), ForeColor = Color.FromArgb(113, 128, 150), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            Controls.Add(lblUpdated);

            lblTodayUp = MakeCard("今日上传", 20, Color.FromArgb(237, 137, 54), 13F);
            lblTodayDown = MakeCard("今日下载", 156, Color.FromArgb(66, 153, 225), 13F);
            lblTodayTotal = MakeCard("今日合计", 292, Color.FromArgb(47, 133, 90), 13F);

            // speed card: down=blue / up=orange (match today cards)
            var p4 = new Panel { Location = new Point(428, 72), Size = new Size(130, 90), BackColor = Color.White };
            p4.Paint += delegate(object s, PaintEventArgs e) {
                using (var pen = new Pen(Color.FromArgb(226, 232, 240))) e.Graphics.DrawRectangle(pen, 0, 0, p4.Width - 1, p4.Height - 1);
            };
            var t4 = new Label { Text = "实时速度", Location = new Point(12, 12), AutoSize = true,
                ForeColor = Color.FromArgb(113, 128, 150), Font = new Font("Microsoft YaHei UI", 8.5F) };
            lblSpeedDown = new Label { Text = "↓ 0 B/s", Location = new Point(12, 36), AutoSize = true,
                ForeColor = Color.FromArgb(66, 153, 225), Font = new Font("Microsoft YaHei UI", 10.5F, FontStyle.Bold) };
            lblSpeedUp = new Label { Text = "↑ 0 B/s", Location = new Point(12, 60), AutoSize = true,
                ForeColor = Color.FromArgb(237, 137, 54), Font = new Font("Microsoft YaHei UI", 10.5F, FontStyle.Bold) };
            p4.Controls.Add(t4); p4.Controls.Add(lblSpeedDown); p4.Controls.Add(lblSpeedUp);
            Controls.Add(p4);

            // mode radios (own row between cards and chart, right aligned, outside chart)
            rbDays30 = new RadioButton { Text = "近30天每日流量", AutoSize = true, Checked = false,
                ForeColor = Color.FromArgb(74, 85, 104), Location = new Point(295, 168), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            rbMonth = new RadioButton { Text = "当月每日流量", AutoSize = true, Checked = true,
                ForeColor = Color.FromArgb(74, 85, 104), Location = new Point(441, 168), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            Controls.Add(rbDays30);
            Controls.Add(rbMonth);
            rbDays30.CheckedChanged += delegate { chartPanel.Invalidate(); };
            rbMonth.CheckedChanged += delegate { chartPanel.Invalidate(); };

            chartPanel = new Panel { Location = new Point(20, 194), Size = new Size(538, 314), BackColor = Color.White,
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
            chartPanel.Paint += ChartPaint;
            chartPanel.MouseMove += ChartMouseMove;
            typeof(Panel).GetProperty("DoubleBuffered",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(chartPanel, true, null);
            Controls.Add(chartPanel);

            // legend
            var legUp = new Panel { Location = new Point(24, 520), Size = new Size(12, 12), BackColor = Color.FromArgb(237, 137, 54), Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            var legUpL = new Label { Text = "上传", Location = new Point(40, 516), AutoSize = true, ForeColor = Color.FromArgb(74, 85, 104), Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            var legDown = new Panel { Location = new Point(96, 520), Size = new Size(12, 12), BackColor = Color.FromArgb(66, 153, 225), Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            var legDownL = new Label { Text = "下载", Location = new Point(112, 516), AutoSize = true, ForeColor = Color.FromArgb(74, 85, 104), Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            Controls.Add(legUp); Controls.Add(legUpL); Controls.Add(legDown); Controls.Add(legDownL);

            lblSummary = new Label { Text = "", Location = new Point(160, 516), AutoSize = true,
                ForeColor = Color.FromArgb(113, 128, 150), Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            Controls.Add(lblSummary);

            chkSample = new CheckBox { Text = "本程序运行时采集（兜底）", Location = new Point(20, 548), AutoSize = true,
                ForeColor = Color.FromArgb(74, 85, 104), Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            Controls.Add(chkSample);

            chkAutoStart = new CheckBox { Text = "开机自启动", Location = new Point(190, 548), AutoSize = true,
                ForeColor = Color.FromArgb(74, 85, 104), Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            chkAutoStart.CheckedChanged += ChkAutoStartChanged;
            Controls.Add(chkAutoStart);
            chkAutoStart.Checked = GetAutoStart();   // 读注册表回显（幂等写，无副作用）

            lblTaskStatus = new Label { Text = "", Location = new Point(20, 580), AutoSize = true,
                ForeColor = Color.FromArgb(113, 128, 150), Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            Controls.Add(lblTaskStatus);

            btnInstall = new Button { Text = "安装后台采集任务", Location = new Point(330, 545), Size = new Size(130, 28),
                FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(43, 108, 176), ForeColor = Color.White,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
            btnInstall.FlatAppearance.BorderSize = 0;
            btnInstall.Click += BtnInstallClick;
            Controls.Add(btnInstall);

            btnOpenData = new Button { Text = "数据文件夹", Location = new Point(468, 545), Size = new Size(90, 28),
                FlatStyle = FlatStyle.Flat, BackColor = Color.White, ForeColor = Color.FromArgb(74, 85, 104),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
            btnOpenData.FlatAppearance.BorderColor = Color.FromArgb(203, 213, 224);
            btnOpenData.Click += delegate { ProcessSafeStart(Path.Combine(baseDir, "data")); };
            Controls.Add(btnOpenData);

            btnUninstall = new Button { Text = "卸载后台采集任务", Location = new Point(330, 579), Size = new Size(130, 24),
                FlatStyle = FlatStyle.Flat, BackColor = Color.White, ForeColor = Color.FromArgb(197, 108, 44),
                Font = new Font("Microsoft YaHei UI", 8.25F),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
            btnUninstall.FlatAppearance.BorderColor = Color.FromArgb(221, 190, 169);
            btnUninstall.Click += BtnUninstallClick;
            Controls.Add(btnUninstall);

            // tray icon
            trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("恢复窗口", null, delegate { RestoreWindow(); });
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("退出", null, delegate { allowExit = true; Close(); });
            tray = new NotifyIcon { Icon = appIcon ?? SystemIcons.Application, Text = "网络流量统计工具", ContextMenuStrip = trayMenu, Visible = true };
            tray.DoubleClick += delegate { RestoreWindow(); };

            StartRestoreWatcher();
            EnsureDataFile();
            taskInstalled = TaskInstalled();
            UpdateTaskUI();
            LoadData();
            UpdateStats();
            chartPanel.Invalidate();

            timer = new System.Windows.Forms.Timer { Interval = 1000 };
            timer.Tick += Tick;
            timer.Start();
        }

        Label MakeCard(string title, int x, Color valColor, float vSize)
        {
            var p = new Panel { Location = new Point(x, 72), Size = new Size(130, 90), BackColor = Color.White };
            p.Paint += delegate(object s, PaintEventArgs e) {
                using (var pen = new Pen(Color.FromArgb(226, 232, 240))) e.Graphics.DrawRectangle(pen, 0, 0, p.Width - 1, p.Height - 1);
            };
            var t = new Label { Text = title, Location = new Point(12, 14), AutoSize = true,
                ForeColor = Color.FromArgb(113, 128, 150), Font = new Font("Microsoft YaHei UI", 8.5F) };
            var v = new Label { Text = "0 B", Location = new Point(12, 40), AutoSize = true,
                ForeColor = valColor, Font = new Font("Microsoft YaHei UI", vSize, FontStyle.Bold) };
            p.Controls.Add(t); p.Controls.Add(v);
            Controls.Add(p);
            return v;
        }

        void StartRestoreWatcher()
        {
            if (restoreEvent == null || watcherStarted) return;
            watcherStarted = true;
            ThreadPool.QueueUserWorkItem(delegate {
                while (restoreEvent.WaitOne()) {
                    try {
                        if (IsHandleCreated) BeginInvoke((MethodInvoker)delegate { RestoreWindow(); });
                    } catch { return; }
                }
            });
        }

        void RestoreWindow()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        // /tray 启动：拦截首次显示，窗口不出现只留托盘
        protected override void SetVisibleCore(bool value)
        {
            if (StartHidden && value) {
                value = false;
                if (!IsHandleCreated) CreateHandle();
                StartHidden = false;
            }
            base.SetVisibleCore(value);
        }
        public static bool StartHidden = false;

        // ---------- 开机自启动 (HKCU Run, 无需管理员) ----------
        const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValueName = "NetMon";

        bool GetAutoStart()
        {
            try {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath))
                    return k != null && k.GetValue(RunValueName) != null;
            } catch { return false; }
        }

        bool SetAutoStart(bool on)
        {
            try {
                using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKeyPath)) {
                    if (on) k.SetValue(RunValueName, "\"" + Application.ExecutablePath + "\" /tray");
                    else {
                        if (k.GetValue(RunValueName) != null) k.DeleteValue(RunValueName, false);
                    }
                }
                return true;
            } catch { return false; }
        }

        void ChkAutoStartChanged(object s, EventArgs e)
        {
            bool want = chkAutoStart.Checked;
            if (SetAutoStart(want)) return;
            // 失败：弹提示并回滚勾选状态
            MessageBox.Show("设置开机自启动失败，请尝试以管理员身份运行本程序。", "网络流量统计工具",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            chkAutoStart.CheckedChanged -= ChkAutoStartChanged;
            chkAutoStart.Checked = !want;
            chkAutoStart.CheckedChanged += ChkAutoStartChanged;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!allowExit) {
                e.Cancel = true;
                Hide();
                if (!balloonShown) {
                    tray.ShowBalloonTip(2000, "网络流量统计工具", "程序已最小化到托盘，仍在后台统计流量。右键托盘图标可退出。", ToolTipIcon.Info);
                    balloonShown = true;
                }
                return;
            }
            if (chkSample.Enabled && chkSample.Checked) FallbackWrite();
            tray.Visible = false;
            base.OnFormClosing(e);
        }

        // ---------- data ----------
        void EnsureDataFile()
        {
            try {
                Directory.CreateDirectory(Path.Combine(baseDir, "data"));
                if (!File.Exists(DataFile)) {
                    var root = new Dictionary<string, object>();
                    root["last_sent"] = 0.0; root["last_recv"] = 0.0;
                    root["last_ts"] = "";
                    root["daily"] = new Dictionary<string, object>();
                    var ser = new JavaScriptSerializer();
                    File.WriteAllText(DataFile, ser.Serialize(root), new UTF8Encoding(false));
                }
            } catch { }
        }

        Dictionary<string, object> ReadJson()
        {
            try {
                if (!File.Exists(DataFile)) return null;
                var ser = new JavaScriptSerializer();
                return ser.Deserialize<Dictionary<string, object>>(File.ReadAllText(DataFile, Encoding.UTF8));
            } catch { return null; }
        }

        void LoadData()
        {
            var root = ReadJson();
            var d = new Dictionary<string, double[]>();
            if (root != null) {
                if (root.ContainsKey("last_ts") && root["last_ts"] != null) fileTs = root["last_ts"].ToString();
                var dd = root.ContainsKey("daily") ? root["daily"] as Dictionary<string, object> : null;
                if (dd != null) {
                    foreach (var kv in dd) {
                        var rec = kv.Value as Dictionary<string, object>;
                        if (rec == null) continue;
                        double up = rec.ContainsKey("up") ? Convert.ToDouble(rec["up"]) : 0;
                        double down = rec.ContainsKey("down") ? Convert.ToDouble(rec["down"]) : 0;
                        d[kv.Key] = new double[] { up, down };
                    }
                }
            }
            daily = d;
            liveTodayUp = 0; liveTodayDown = 0;
        }

        void FallbackWrite()
        {
            if (ourUnsavedUp == 0 && ourUnsavedDown == 0) return;
            try {
                Directory.CreateDirectory(Path.GetDirectoryName(DataFile));
                var root = ReadJson();
                if (root == null) {
                    root = new Dictionary<string, object>();
                    root["last_sent"] = 0.0; root["last_recv"] = 0.0;
                }
                var dailyDict = root.ContainsKey("daily") ? root["daily"] as Dictionary<string, object> : null;
                if (dailyDict == null) { dailyDict = new Dictionary<string, object>(); root["daily"] = dailyDict; }
                string today = DateTime.Today.ToString("yyyy-MM-dd");
                double up = 0, down = 0;
                object cur;
                if (dailyDict.TryGetValue(today, out cur)) {
                    var cd = cur as Dictionary<string, object>;
                    if (cd != null) {
                        up = cd.ContainsKey("up") ? Convert.ToDouble(cd["up"]) : 0;
                        down = cd.ContainsKey("down") ? Convert.ToDouble(cd["down"]) : 0;
                    }
                }
                dailyDict[today] = new Dictionary<string, object> {
                    { "up", up + ourUnsavedUp }, { "down", down + ourUnsavedDown }
                };
                root["last_ts"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                var ser = new JavaScriptSerializer();
                File.WriteAllText(DataFile, ser.Serialize(root), new UTF8Encoding(false));
                ourUnsavedUp = 0; ourUnsavedDown = 0;
            } catch { }
        }

        // ---------- live sampling ----------
        void SampleLive()
        {
            double du = 0, dd = 0;
            try {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces()) {
                    try {
                        var st = nic.GetIPv4Statistics();
                        if (st == null) continue;
                        long up = st.BytesSent, down = st.BytesReceived;
                        long[] last;
                        if (lastCounters.TryGetValue(nic.Id, out last)) {
                            long a = up - last[0]; if (a < 0) a = 0;
                            long b = down - last[1]; if (b < 0) b = 0;
                            du += a; dd += b;
                        }
                        lastCounters[nic.Id] = new long[] { up, down };
                    } catch { }
                }
            } catch { }
            speedUp = du; speedDown = dd;
            ourUnsavedUp += du; ourUnsavedDown += dd;
            liveTodayUp += du; liveTodayDown += dd;
        }

        void Tick(object s, EventArgs e)
        {
            tick++;
            SampleLive();
            lblSpeedDown.Text = "↓ " + FmtSpeed(speedDown);
            lblSpeedUp.Text = "↑ " + FmtSpeed(speedUp);
            if (chkSample.Enabled && chkSample.Checked) {
                if (tick % 10 == 0) { FallbackWrite(); LoadData(); UpdateStats(); chartPanel.Invalidate(); return; }
            }
            if (tick % 15 == 0) { LoadData(); UpdateStats(); chartPanel.Invalidate(); }
        }

        void UpdateStats()
        {
            string today = DateTime.Today.ToString("yyyy-MM-dd");
            double tUp = 0, tDown = 0;
            double[] rec;
            if (daily.TryGetValue(today, out rec)) { tUp = rec[0]; tDown = rec[1]; }
            tUp += liveTodayUp; tDown += liveTodayDown;
            lblTodayUp.Text = FmtSize(tUp);
            lblTodayDown.Text = FmtSize(tDown);
            lblTodayTotal.Text = FmtSize(tUp + tDown);

            double w7 = 0, w30 = 0;
            for (int i = 0; i < 30; i++) {
                string d = DateTime.Today.AddDays(-i).ToString("yyyy-MM-dd");
                double[] v;
                if (daily.TryGetValue(d, out v)) {
                    double t = v[0] + v[1];
                    w30 += t;
                    if (i < 7) w7 += t;
                }
            }
            lblSummary.Text = string.Format("近7天合计 {0}   近30天合计 {1}", FmtSize(w7), FmtSize(w30));
            lblUpdated.Text = "数据更新: " + (fileTs == "" ? "-" : fileTs);
        }

        // ---------- scheduled task (schtasks based, self-contained) ----------
        static int RunCmd(string exe, string args)
        {
            try {
                var psi = new System.Diagnostics.ProcessStartInfo(exe, args);
                psi.UseShellExecute = false; psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
                using (var p = System.Diagnostics.Process.Start(psi)) {
                    p.WaitForExit(20000);
                    return p.ExitCode;
                }
            } catch { return -1; }
        }

        bool TaskInstalled()
        {
            return RunCmd("schtasks.exe", "/query /tn " + TaskName) == 0;
        }

        void BtnInstallClick(object s, EventArgs e)
        {
            btnInstall.Text = "安装中..."; btnInstall.Enabled = false;
            string exePath = Application.ExecutablePath;
            // every 1 minute: NetMon.exe /sample  (hidden, samples once and exits)
            int rc = RunCmd("schtasks.exe",
                "/create /f /tn " + TaskName +
                " /tr \"\\\"" + exePath + "\\\" /sample\"" +
                " /sc minute /mo 1");
            taskInstalled = TaskInstalled();
            if (!taskInstalled) {
                MessageBox.Show("安装失败（错误码 " + rc + "）。请尝试右键以管理员身份运行本程序。", "网络流量统计工具",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            UpdateTaskUI();
        }

        void BtnUninstallClick(object s, EventArgs e)
        {
            btnUninstall.Text = "卸载中..."; btnUninstall.Enabled = false;
            RunCmd("schtasks.exe", "/delete /f /tn " + TaskName);
            taskInstalled = TaskInstalled();
            UpdateTaskUI();   // 数据文件不受影响
        }

        void UpdateTaskUI()
        {
            if (taskInstalled) {
                lblTaskStatus.Text = "后台采集任务: 已安装（每分钟自动记录）";
                lblTaskStatus.ForeColor = Color.FromArgb(47, 133, 90);
                btnInstall.Text = "已安装"; btnInstall.Enabled = false;
                btnInstall.BackColor = Color.FromArgb(160, 174, 192);
                btnUninstall.Text = "卸载后台采集任务"; btnUninstall.Enabled = true;
                btnUninstall.ForeColor = Color.FromArgb(197, 108, 44);
                chkSample.Enabled = false; chkSample.Checked = false;
            } else {
                lblTaskStatus.Text = "后台采集任务: 未安装（关闭程序不记录流量）";
                lblTaskStatus.ForeColor = Color.FromArgb(197, 108, 44);
                btnInstall.Text = "安装后台采集任务"; btnInstall.Enabled = true;
                btnInstall.BackColor = Color.FromArgb(43, 108, 176);
                btnUninstall.Text = "卸载后台采集任务"; btnUninstall.Enabled = false;
                btnUninstall.ForeColor = Color.FromArgb(200, 200, 200);
                chkSample.Enabled = true; chkSample.Checked = true;
            }
        }

        void ProcessSafeStart(string target)
        {
            try { System.Diagnostics.Process.Start(target); } catch { }
        }

        // ---------- chart ----------
        class BarInfo { public string Date; public Rectangle Rect; public double Up, Down, Total; }

        double NiceMax(double v)
        {
            if (v <= 0) return 1;
            double pow = Math.Pow(10, Math.Floor(Math.Log10(v)));
            foreach (double m in new double[] { 1, 2, 2.5, 5, 10 }) {
                if (m * pow >= v) return m * pow;
            }
            return 10 * pow;
        }

        string FmtAxis(double bytes)
        {
            if (bytes >= 1073741824.0) return (bytes / 1073741824.0).ToString("0.#") + "G";
            if (bytes >= 1048576.0) return (bytes / 1048576.0).ToString("0.#") + "M";
            if (bytes >= 1024.0) return (bytes / 1024.0).ToString("0.#") + "K";
            return bytes.ToString("0");
        }

        List<KeyValuePair<string, double[]>> GetWindowDays()
        {
            var days = new List<KeyValuePair<string, double[]>>();
            if (rbMonth != null && rbMonth.Checked) {
                int dim = DateTime.DaysInMonth(DateTime.Today.Year, DateTime.Today.Month);
                for (int d = 1; d <= dim; d++) {
                    string key = new DateTime(DateTime.Today.Year, DateTime.Today.Month, d).ToString("yyyy-MM-dd");
                    double[] v;
                    if (!daily.TryGetValue(key, out v)) v = new double[] { 0, 0 };
                    days.Add(new KeyValuePair<string, double[]>(key, v));
                }
            } else {
                for (int i = 29; i >= 0; i--) {
                    string key = DateTime.Today.AddDays(-i).ToString("yyyy-MM-dd");
                    double[] v;
                    if (!daily.TryGetValue(key, out v)) v = new double[] { 0, 0 };
                    days.Add(new KeyValuePair<string, double[]>(key, v));
                }
            }
            return days;
        }

        void ChartPaint(object s, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(Color.White);

            int padL = (int)(50 * dpiScale), padR = (int)(14 * dpiScale), padT = (int)(30 * dpiScale), padB = (int)(26 * dpiScale);
            var area = new Rectangle(padL, padT, chartPanel.Width - padL - padR, chartPanel.Height - padT - padB);

            var days = GetWindowDays();
            double maxV = 1;
            foreach (var k in days) maxV = Math.Max(maxV, k.Value[0] + k.Value[1]);
            double axisMax = NiceMax(maxV);

            using (var gridPen = new Pen(Color.FromArgb(237, 242, 247)))
            using (var tickPen = new Pen(Color.FromArgb(203, 213, 224)))
            using (var axisF = new Font("Microsoft YaHei UI", 7.5F))
            using (var axisB = new SolidBrush(Color.FromArgb(160, 174, 192)))
            using (var upBrush = new SolidBrush(Color.FromArgb(237, 137, 54)))
            using (var downBrush = new SolidBrush(Color.FromArgb(66, 153, 225)))
            using (var lblBrush = new SolidBrush(Color.FromArgb(113, 128, 150)))
            using (var todayBrush = new SolidBrush(Color.FromArgb(43, 108, 176)))
            using (var dateF = new Font("Microsoft YaHei UI", 7F))
            using (var titleF = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold))
            using (var titleB = new SolidBrush(Color.FromArgb(74, 85, 104))) {
                string chartTitle = (rbMonth != null && rbMonth.Checked) ? "当月每日流量" : "近30天每日流量";
                g.DrawString(chartTitle, titleF, titleB, (int)(14 * dpiScale), (int)(8 * dpiScale));
                for (int i = 0; i <= 4; i++) {
                    int y = area.Bottom - (int)(area.Height * i / 4.0);
                    g.DrawLine(gridPen, area.Left, y, area.Right, y);
                    string lbl = FmtAxis(axisMax * i / 4.0);
                    g.DrawString(lbl, axisF, axisB, area.Left - (int)(42 * dpiScale), y - (int)(6 * dpiScale));
                }
                barRects.Clear();
                int n = days.Count;
                double slot = (double)area.Width / n;
                int bw = Math.Max((int)(6 * dpiScale), (int)(slot * 0.6));
                string todayKey = DateTime.Today.ToString("yyyy-MM-dd");
                for (int i = 0; i < n; i++) {
                    var k = days[i];
                    double total = k.Value[0] + k.Value[1];
                    int downH = (int)(area.Height * k.Value[1] / axisMax);
                    int upH = (int)(area.Height * k.Value[0] / axisMax);
                    int x = area.Left + (int)(i * slot + (slot - bw) / 2);
                    if (total > 0) {
                        int downTop = area.Bottom - downH;
                        g.FillRectangle(downBrush, x, downTop, bw, downH);
                        if (upH > 0) g.FillRectangle(upBrush, x, downTop - upH, bw, upH);
                    }
                    // x labels: 1/5/10/15/20/25/30 show numbers, others get a short tick
                    int dayNum;
                    int.TryParse(k.Key.Substring(8), out dayNum);
                    bool showNum = (dayNum == 1 || dayNum == 5 || dayNum == 10 || dayNum == 15 || dayNum == 20 || dayNum == 25 || dayNum == 30);
                    float cx = x + bw / 2f;
                    if (showNum) {
                        string dl = dayNum.ToString();
                        var br = k.Key == todayKey ? todayBrush : lblBrush;
                        var sz = g.MeasureString(dl, dateF);
                        g.DrawString(dl, dateF, br, cx - sz.Width / 2, area.Bottom + (int)(4 * dpiScale));
                    } else {
                        g.DrawLine(tickPen, cx, area.Bottom, cx, area.Bottom + (int)(4 * dpiScale));
                    }
                    barRects.Add(new BarInfo {
                        Date = k.Key, Up = k.Value[0], Down = k.Value[1], Total = total,
                        Rect = new Rectangle(x - (int)(2 * dpiScale), area.Top, bw + (int)(4 * dpiScale), area.Bottom - area.Top)
                    });
                }
            }
        }

        void ChartMouseMove(object s, MouseEventArgs e)
        {
            string txt = null;
            foreach (var b in barRects) {
                if (b.Rect.Contains(e.Location)) {
                    txt = string.Format("{0}\r\n上传: {1}\r\n下载: {2}\r\n合计: {3}",
                        b.Date, FmtSize(b.Up), FmtSize(b.Down), FmtSize(b.Total));
                    break;
                }
            }
            if (txt != lastTip) { tip.SetToolTip(chartPanel, txt ?? ""); lastTip = txt; }
        }

        // ---------- formatting ----------
        static string FmtSize(double bytes)
        {
            if (bytes >= 1099511627776.0) return (bytes / 1099511627776.0).ToString("N2") + " TB";
            if (bytes >= 1073741824.0) return (bytes / 1073741824.0).ToString("N2") + " GB";
            if (bytes >= 1048576.0) return (bytes / 1048576.0).ToString("N2") + " MB";
            if (bytes >= 1024.0) return (bytes / 1024.0).ToString("N1") + " KB";
            return ((long)bytes).ToString() + " B";
        }

        static string FmtSpeed(double bytesPerSec)
        {
            if (bytesPerSec >= 1048576.0) return (bytesPerSec / 1048576.0).ToString("N2") + " MB/s";
            if (bytesPerSec >= 1024.0) return (bytesPerSec / 1024.0).ToString("N1") + " KB/s";
            return ((long)bytesPerSec).ToString() + " B/s";
        }

        // ---------- /sample hidden mode ----------
        static void SampleOnce()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string dataFile = Path.Combine(baseDir, "data", "netmon.json");
            try {
                Directory.CreateDirectory(Path.GetDirectoryName(dataFile));
                // cumulative bytes across all adapters
                double sent = 0, recv = 0;
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces()) {
                    try {
                        var st = nic.GetIPv4Statistics();
                        if (st != null) { sent += st.BytesSent; recv += st.BytesReceived; }
                    } catch { }
                }
                JavaScriptSerializer ser = new JavaScriptSerializer();
                Dictionary<string, object> root = null;
                try {
                    if (File.Exists(dataFile))
                        root = ser.Deserialize<Dictionary<string, object>>(File.ReadAllText(dataFile, Encoding.UTF8));
                } catch { root = null; }
                if (root == null) {
                    root = new Dictionary<string, object>();
                    root["last_sent"] = 0.0; root["last_recv"] = 0.0;
                    root["daily"] = new Dictionary<string, object>();
                }
                double lastSent = root.ContainsKey("last_sent") ? Convert.ToDouble(root["last_sent"]) : 0;
                double lastRecv = root.ContainsKey("last_recv") ? Convert.ToDouble(root["last_recv"]) : 0;
                double dUp = sent - lastSent; if (dUp < 0) dUp = sent;   // counter shrank = reboot/reset
                double dDown = recv - lastRecv; if (dDown < 0) dDown = recv;
                if (dUp > 109951162777600.0) dUp = sent;
                if (dDown > 109951162777600.0) dDown = recv;

                var dailyDict = root.ContainsKey("daily") ? root["daily"] as Dictionary<string, object> : null;
                if (dailyDict == null) { dailyDict = new Dictionary<string, object>(); root["daily"] = dailyDict; }
                string today = DateTime.Today.ToString("yyyy-MM-dd");
                double up = 0, down = 0;
                object cur;
                if (dailyDict.TryGetValue(today, out cur)) {
                    var cd = cur as Dictionary<string, object>;
                    if (cd != null) {
                        up = cd.ContainsKey("up") ? Convert.ToDouble(cd["up"]) : 0;
                        down = cd.ContainsKey("down") ? Convert.ToDouble(cd["down"]) : 0;
                    }
                }
                dailyDict[today] = new Dictionary<string, object> { { "up", up + dUp }, { "down", down + dDown } };
                root["last_sent"] = sent; root["last_recv"] = recv;
                root["last_ts"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                File.WriteAllText(dataFile, ser.Serialize(root), new UTF8Encoding(false));
            } catch { }
        }

        [STAThread]
        static void Main(string[] args)
        {
            if (args != null && args.Length > 0 && (args[0] == "/sample" || args[0] == "-sample")) {
                SampleOnce();
                return;
            }
            if (args != null && args.Length > 0 && (args[0] == "/tray" || args[0] == "-tray")) {
                MainForm.StartHidden = true;   // 开机自启动：直接隐藏到托盘
            }
            try { SetProcessDPIAware(); } catch { }
            // single instance: second launch just brings the running window back
            bool createdNew;
            var mutex = new Mutex(true, "NetMon_SingleInstance_dingqh", out createdNew);
            if (!createdNew) {
                try {
                    EventWaitHandle existing;
                    if (EventWaitHandle.TryOpenExisting("NetMon_RestoreEvent_dingqh", out existing)) {
                        existing.Set(); existing.Dispose();
                    }
                } catch { }
                return;
            }
            var restoreEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "NetMon_RestoreEvent_dingqh");
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try {
                Application.Run(new MainForm(restoreEvent));
            } catch (Exception ex) {
                MessageBox.Show("启动失败: " + ex.Message, "网络流量统计工具", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            try { mutex.ReleaseMutex(); } catch { }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();
    }
}
