using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;

namespace 六合分析软件
{
    public partial class Form1 : Form
    {
        Panel menuPanel;
        Panel mainPanel;

        Button btnHome;
        Button btnHistory;
        Button btnZodiacPredict;
        Button btnAIPredictHistory;
        Button btnLive;
        Button btnAnalyze;
        Button btnPredict;
        Button btnCheck;
        Button btnMlBacktest;
        Button btnV7Models;
        Button btnV7History;

        Label titleLabel;
        Label cloudSyncLabel;
        TextBox txtNumber;
        Button btnSaveHistory;
        Label saveStatus;
        RadioButton rbDownload100;
        RadioButton rbDownload300;
        RadioButton rbDownload500;

        // AI 预测缓存（统一使用 AIEngine）
        const int HomePredictionPeriods = 100;
        AIEngine.PredictResult? _cachedPredict;
        bool _cloudSyncRunning;
        bool _predictionLoading;
        System.Windows.Forms.Timer? _cloudSyncTimer;

        public Form1()
        {
            InitializeComponent();
            InitUI();

            // 恢复窗口位置
            this.Load += (s, e) => RestoreWindowPosition();
            this.Shown += async (s, e) => await SyncCloudDataAsync();
            this.FormClosing += (s, e) => SaveWindowPosition();
        }

        private void SaveWindowPosition()
        {
            try
            {
                var bounds = $"{this.Left},{this.Top},{this.Width},{this.Height}";
                System.IO.File.WriteAllText("window.cfg", bounds);
            }
            catch { }
        }

        private void RestoreWindowPosition()
        {
            try
            {
                if (System.IO.File.Exists("window.cfg"))
                {
                    var parts = System.IO.File.ReadAllText("window.cfg").Split(',');
                    if (parts.Length == 4)
                    {
                        this.Left = int.Parse(parts[0]);
                        this.Top = int.Parse(parts[1]);
                        this.Width = int.Parse(parts[2]);
                        this.Height = int.Parse(parts[3]);
                        this.StartPosition = FormStartPosition.Manual;
                    }
                }
            }
            catch { }
        }

        private void InitUI()
        {
            this.Text = "六合智能分析系统 V6.5";
            this.Size = new Size(1000, 650);
            this.StartPosition = FormStartPosition.CenterScreen;

            // 标题
            titleLabel = new Label();
            titleLabel.Text = "六合智能分析系统 V6.5";
            titleLabel.Font = new Font("微软雅黑", 22);
            titleLabel.Location = new Point(300, 20);
            titleLabel.AutoSize = true;

            this.Controls.Add(titleLabel);

            cloudSyncLabel = new Label();
            cloudSyncLabel.Text = "V6云端数据：等待同步";
            cloudSyncLabel.Font = new Font("微软雅黑", 9);
            cloudSyncLabel.ForeColor = Color.DimGray;
            cloudSyncLabel.Location = new Point(302, 62);
            cloudSyncLabel.AutoSize = true;
            this.Controls.Add(cloudSyncLabel);

            _cloudSyncTimer = new System.Windows.Forms.Timer { Interval = 15 * 60 * 1000 };
            _cloudSyncTimer.Tick += async (s, e) => await SyncCloudDataAsync();
            _cloudSyncTimer.Start();

            // 左侧菜单
            menuPanel = new Panel();
            menuPanel.Location = new Point(0, 0);
            menuPanel.Size = new Size(200, 650);
            menuPanel.BackColor = Color.LightGray;
            menuPanel.AutoScroll = true;

            this.Controls.Add(menuPanel);

            btnHome = CreateButton("首页", 80);
            btnHistory = CreateButton("历史数据", 130);
            btnZodiacPredict = CreateButton("⭐ AI生肖预测", 180);
            btnZodiacPredict.BackColor = Color.FromArgb(255, 152, 0);
            btnZodiacPredict.ForeColor = Color.White;
            btnAIPredictHistory = CreateButton("📜 AI预测历史", 230);
            btnLive = CreateButton("📺 开奖直播", 265);
            btnLive.BackColor = Color.FromArgb(220, 50, 50);
            btnLive.ForeColor = Color.White;
            btnAnalyze = CreateButton("📊 数据中心", 320);
            btnPredict = CreateButton("走势预测", 370);
            btnMlBacktest = CreateButton("🧪 ML滚动回测", 440);
            btnV7Models = CreateButton("🧠 智能模型实验", 490);
            btnV7History = CreateButton("📜 智能预测历史", 545);
            btnCheck = CreateButton("📐 自用规律", 600);

            menuPanel.Controls.Add(btnHome);
            menuPanel.Controls.Add(btnHistory);
            menuPanel.Controls.Add(btnZodiacPredict);
            menuPanel.Controls.Add(btnAIPredictHistory);
            menuPanel.Controls.Add(btnLive);
            menuPanel.Controls.Add(btnAnalyze);
            menuPanel.Controls.Add(btnPredict);
            menuPanel.Controls.Add(btnMlBacktest);
            menuPanel.Controls.Add(btnV7Models);
            menuPanel.Controls.Add(btnV7History);
            menuPanel.Controls.Add(btnCheck);

            btnHome.Click += (s, e) => ShowHome();
            btnHistory.Click += BtnHistory_Click;
            btnZodiacPredict.Click += BtnZodiacPredict_Click;
            btnAIPredictHistory.Click += BtnAIPredictHistory_Click;
            btnLive.Click += BtnLive_Click;
            btnAnalyze.Click += BtnAnalyze_Click;
            btnPredict.Click += BtnPredict_Click;
            btnMlBacktest.Click += BtnMlBacktest_Click;
            btnV7Models.Click += BtnV7Models_Click;
            btnV7History.Click += (_, _) => new V7PredictionHistoryForm().ShowDialog(this);
            btnCheck.Click += BtnCheck_Click;

            // 主显示区域
            mainPanel = new Panel();
            mainPanel.Location = new Point(220, 100);
            mainPanel.Size = new Size(700, 450);
            mainPanel.BorderStyle = BorderStyle.FixedSingle;
            mainPanel.AutoScroll = true;

            this.Controls.Add(mainPanel);

            // 窗口大小变化时调整面板
            this.Resize += (s, e) => {
                mainPanel.Width = Math.Max(400, this.ClientSize.Width - 240);
                mainPanel.Height = Math.Max(300, this.ClientSize.Height - 130);
                menuPanel.Height = this.ClientSize.Height;
            };

            ShowHome();
        }

        private async Task SyncCloudDataAsync()
        {
            if (_cloudSyncRunning) return;
            _cloudSyncRunning = true;
            cloudSyncLabel.Text = "V6云端数据：正在补齐开奖记录并缓存预测档案...";
            cloudSyncLabel.ForeColor = Color.FromArgb(36, 116, 210);
            try
            {
                CloudSyncResult result = await CloudPredictionSyncService.SyncAsync();
                cloudSyncLabel.Text = $"V6云端同步完成：开奖{result.LatestDrawIssue}期，预测档案缓存至{result.LatestPredictionIssue}期（{result.PredictionFileCount}期）";
                cloudSyncLabel.ForeColor = Color.FromArgb(15, 140, 91);
                DatabaseHelper.BatchVerifyAIPredicts();
                AIEngine.InvalidateCache();
                _cachedPredict = null;
                ShowHome();
            }
            catch (Exception cloudError)
            {
                AppLogger.Error("V6云端档案同步失败", cloudError);
                cloudSyncLabel.Text = "V6云端同步失败，已保留电脑现有数据，15分钟后重试";
                cloudSyncLabel.ForeColor = Color.Firebrick;
            }
            finally { _cloudSyncRunning = false; }
        }

        private Button CreateButton(string text, int y)
        {
            Button btn = new Button();
            btn.Text = text;
            btn.Size = new Size(150, 45);
            btn.Location = new Point(25, y);
            btn.Font = new Font("微软雅黑", 12);
            return btn;
        }

        private RadioButton CreateDownloadRangeRadio(string text, int x, int y, int periods, bool isChecked)
        {
            RadioButton radio = new RadioButton();
            radio.Text = text;
            radio.Tag = periods;
            radio.Checked = isChecked;
            radio.Font = new Font("微软雅黑", 10);
            radio.Location = new Point(x, y);
            radio.Size = new Size(95, 26);
            return radio;
        }

        private int GetSelectedDownloadPeriods()
        {
            if (rbDownload100?.Checked == true) return 100;
            if (rbDownload300?.Checked == true) return 300;
            return 500;
        }

        private void ClearMain()
        {
            mainPanel.Controls.Clear();
        }

        private void ShowHome()
        {
            mainPanel.SuspendLayout();
            ClearMain();
            mainPanel.AutoScrollPosition = Point.Empty;

            int y = 15;

            // ===== 标题 =====
            Label title = new Label();
            title.Text = "六合智能分析系统 V6.5";
            title.Font = new Font("微软雅黑", 18, FontStyle.Bold);
            title.ForeColor = Color.FromArgb(30, 30, 60);
            title.Location = new Point(20, y);
            title.AutoSize = true;
            mainPanel.Controls.Add(title);
            y += 30;

            // ===== 最新开奖 =====
            Panel section1 = CreateSectionPanel(y, 130);
            mainPanel.Controls.Add(section1);

            Label s1Title = new Label();
            s1Title.Text = "🎯 最新开奖";
            s1Title.Font = new Font("微软雅黑", 12, FontStyle.Bold);
            s1Title.ForeColor = Color.FromArgb(0, 122, 204);
            s1Title.Location = new Point(15, 10);
            s1Title.AutoSize = true;
            section1.Controls.Add(s1Title);

            // 使用 GetLatestRecord 获取期号最大的记录
            var latest = DatabaseHelper.GetLatestRecord();
            if (!string.IsNullOrEmpty(latest.Period))
            {
                AddInfoLine(section1, 15, 35, $"期号：{latest.Period}");
                AddInfoLine(section1, 15, 58, $"号码：{latest.Numbers} + {latest.SpecialNumber}");
                AddInfoLine(section1, 15, 81, $"特码：{latest.SpecialNumber}  生肖：{latest.SpecialZodiac}");
                AddInfoLine(section1, 15, 104, $"开奖时间：{latest.OpenTime}");
            }
            else
            {
                AddInfoLine(section1, 15, 35, "暂无开奖数据，请先更新历史数据");
            }

            y += 145;
            // 预测可能包含本地计算和 GPT 网络请求，不能阻塞 UI 线程。
            if (_cachedPredict == null && !_predictionLoading)
            {
                _predictionLoading = true;
                _ = LoadHomePredictionAsync();
            }

            Panel section2 = CreateSectionPanel(y, 385);
            mainPanel.Controls.Add(section2);

            Label s2Title = new Label();
            s2Title.Text = $"🤖 生肖预测（{HomePredictionPeriods}期） - {AIEngine.Version}";
            s2Title.Font = new Font("微软雅黑", 12, FontStyle.Bold);
            s2Title.ForeColor = Color.FromArgb(155, 89, 182);
            s2Title.Location = new Point(15, 10);
            s2Title.AutoSize = true;
            section2.Controls.Add(s2Title);

            // 刷新按钮
            Button btnRefreshAI = new Button();
            btnRefreshAI.Text = "🔄 刷新AI预测";
            btnRefreshAI.Font = new Font("微软雅黑", 10);
            btnRefreshAI.Size = new Size(120, 30);
            btnRefreshAI.Location = new Point(480, 5);
            btnRefreshAI.BackColor = Color.FromArgb(155, 89, 182);
            btnRefreshAI.ForeColor = Color.White;
            btnRefreshAI.FlatAppearance.BorderSize = 0;
            btnRefreshAI.Click += (s, e) => { AIEngine.InvalidateCache(); _cachedPredict = null; ShowHome(); };
            section2.Controls.Add(btnRefreshAI);

            int sy = 40;

            if (_cachedPredict != null && _cachedPredict.AnalysisPeriods >= 10)
            {
                // 预测期号 + 分析周期 + 更新时间
                string predictPeriod = string.IsNullOrEmpty(_cachedPredict.PredictPeriod) ? "?" : _cachedPredict.PredictPeriod;
                AddInfoLine(section2, 15, sy, $"🎯 预测期号：{predictPeriod}  |  分析周期：{_cachedPredict.AnalysisPeriods} 期  |  更新时间：{_cachedPredict.PredictTime:yyyy-MM-dd HH:mm:ss}");
                sy += 22;
                AddInfoLine(section2, 15, sy, $"可信度：{_cachedPredict.Confidence}  |  最佳模型：{_cachedPredict.BestModel}");
                sy += 30;

                var sortedZodiacs = _cachedPredict.AllScores.OrderByDescending(s => s.TotalScore)
                    .Select(s => s.Zodiac).ToList();
                var sevenZodiacs = sortedZodiacs.Take(7).ToList();
                var fiveZodiacs = sevenZodiacs.Take(5).ToList();
                var threeZodiacs = fiveZodiacs.Take(3).ToList();
                var oneZodiac = threeZodiacs.Take(1).ToList();
                var allNumbers = _cachedPredict.RecommendedNumbers;
                var threeNumbers = GetTierNumbers(allNumbers, threeZodiacs, 3);
                var oneNumber = GetTierNumbers(allNumbers, oneZodiac, 1);

                AddPredictionTierRow(section2, sy, "七肖", sevenZodiacs, "⑦码", allNumbers.Take(7), false);
                sy += 62;
                AddPredictionTierRow(section2, sy, "五肖", fiveZodiacs, "⑤码", allNumbers.Take(5), false);
                sy += 62;
                AddPredictionTierRow(section2, sy, "三肖", threeZodiacs, "③码", threeNumbers, true);
                sy += 62;
                AddPredictionTierRow(section2, sy, "一肖", oneZodiac, "①码", oneNumber, true);
            }
            else
            {
                AddInfoLine(section2, 15, sy, _predictionLoading
                    ? "正在后台计算预测，界面不会被网络请求阻塞..."
                    : "数据不足，请先更新历史数据（至少10期）");
            }

            y += 400;

            AddPredictionHistorySection(y);
            y += 385;

            // ===== 数据统计 =====
            Panel section3 = CreateSectionPanel(y, 120);
            mainPanel.Controls.Add(section3);

            int totalRecords = DatabaseHelper.GetHistory().Count;
            var (aiTotal, aiHits, _, aiRate, _) = DatabaseHelper.GetAIPredictStats(HomePredictionPeriods);

            AddInfoLine(section3, 15, 15, $"📊 历史记录：{totalRecords} 期");
            if (aiTotal > 0)
                AddInfoLine(section3, 15, 38, $"🎯 100期模型历史：{aiTotal} 期 | 命中率：{aiRate:F1}%（{aiHits}次）");
            else
                AddInfoLine(section3, 15, 38, "🎯 100期模型历史：暂无验证记录");

            // 系统状态
            string dbFile = DatabaseHelper.DatabasePath;
            string dbStatus = System.IO.File.Exists(dbFile) ? "正常" : "异常";
            long dbSize = System.IO.File.Exists(dbFile) ? new System.IO.FileInfo(dbFile).Length / 1024 : 0;
            string lastSync = DatabaseHelper.GetLatestDate();
            if (string.IsNullOrEmpty(lastSync)) lastSync = "未同步";
            else if (lastSync.Length > 16) lastSync = lastSync.Substring(0, 16);
            string modelVersion = DatabaseHelper.GetCurrentModelVersion();
            var backupStats = DatabaseBackupService.GetBackupStats();

            AddInfoLine(section3, 15, 61, $"💾 数据库：{dbStatus}（{dbSize}KB）| 备份：{backupStats.count}份");
            AddInfoLine(section3, 15, 84, $"🕐 最后同步：{lastSync} | 模型：{modelVersion}");

            y += 135;

            // ===== 分隔线 =====
            Panel divider = new Panel();
            divider.Size = new Size(640, 1);
            divider.BackColor = Color.LightGray;
            divider.Location = new Point(20, y);
            mainPanel.Controls.Add(divider);
            y += 15;

            // ===== 手动录入区 =====
            Panel section4 = CreateSectionPanel(y, 190);
            mainPanel.Controls.Add(section4);

            Label s4Title = new Label();
            s4Title.Text = "📝 手动录入";
            s4Title.Font = new Font("微软雅黑", 12, FontStyle.Bold);
            s4Title.ForeColor = Color.FromArgb(46, 139, 87);
            s4Title.Location = new Point(15, 10);
            s4Title.AutoSize = true;
            section4.Controls.Add(s4Title);

            // 期号输入
            Label lblPeriod = new Label();
            lblPeriod.Text = "期号：";
            lblPeriod.Font = new Font("微软雅黑", 11);
            lblPeriod.Location = new Point(15, 38);
            lblPeriod.AutoSize = true;
            section4.Controls.Add(lblPeriod);

            txtNumber = new TextBox();
            txtNumber.Font = new Font("微软雅黑", 11);
            txtNumber.Location = new Point(70, 34);
            txtNumber.Size = new Size(120, 30);
            txtNumber.PlaceholderText = "例：YYYYNNN";
            section4.Controls.Add(txtNumber);

            // 号码输入
            Label lblNumbers = new Label();
            lblNumbers.Text = "号码：";
            lblNumbers.Font = new Font("微软雅黑", 11);
            lblNumbers.Location = new Point(220, 38);
            lblNumbers.AutoSize = true;
            section4.Controls.Add(lblNumbers);

            TextBox txtNumbers = new TextBox();
            txtNumbers.Name = "txtNumbers";
            txtNumbers.Font = new Font("微软雅黑", 11);
            txtNumbers.Location = new Point(270, 34);
            txtNumbers.Size = new Size(180, 30);
            txtNumbers.PlaceholderText = "例：01 12 25 08 19 33";
            section4.Controls.Add(txtNumbers);

            // 生肖输入
            Label lblShengxiao = new Label();
            lblShengxiao.Text = "生肖：";
            lblShengxiao.Font = new Font("微软雅黑", 11);
            lblShengxiao.Location = new Point(470, 38);
            lblShengxiao.AutoSize = true;
            section4.Controls.Add(lblShengxiao);

            ComboBox cboShengxiao = new ComboBox();
            cboShengxiao.Name = "cboShengxiao";
            cboShengxiao.Font = new Font("微软雅黑", 11);
            cboShengxiao.Location = new Point(515, 34);
            cboShengxiao.Size = new Size(100, 30);
            cboShengxiao.DropDownStyle = ComboBoxStyle.DropDownList;
            cboShengxiao.Items.AddRange(new string[] { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" });
            section4.Controls.Add(cboShengxiao);

            Label lblDownloadRange = new Label();
            lblDownloadRange.Text = "下载范围：";
            lblDownloadRange.Font = new Font("微软雅黑", 10);
            lblDownloadRange.Location = new Point(15, 78);
            lblDownloadRange.AutoSize = true;
            section4.Controls.Add(lblDownloadRange);

            rbDownload100 = CreateDownloadRangeRadio("最近100期", 95, 76, 100, false);
            rbDownload300 = CreateDownloadRangeRadio("最近300期", 195, 76, 300, false);
            rbDownload500 = CreateDownloadRangeRadio("最近500期", 295, 76, 500, true);
            section4.Controls.Add(rbDownload100);
            section4.Controls.Add(rbDownload300);
            section4.Controls.Add(rbDownload500);

            // 保存按钮
            btnSaveHistory = new Button();
            btnSaveHistory.Text = "保存记录";
            btnSaveHistory.Font = new Font("微软雅黑", 11);
            btnSaveHistory.Size = new Size(100, 32);
            btnSaveHistory.Location = new Point(15, 115);
            btnSaveHistory.BackColor = Color.FromArgb(0, 122, 204);
            btnSaveHistory.ForeColor = Color.White;
            btnSaveHistory.FlatAppearance.BorderSize = 0;
            btnSaveHistory.Click += BtnSaveHistory_Click;
            section4.Controls.Add(btnSaveHistory);

            // 更新历史数据按钮
            Button btnUpdateHistory = new Button();
            btnUpdateHistory.Text = "更新历史数据";
            btnUpdateHistory.Font = new Font("微软雅黑", 11);
            btnUpdateHistory.Size = new Size(120, 32);
            btnUpdateHistory.Location = new Point(130, 115);
            btnUpdateHistory.BackColor = Color.FromArgb(46, 139, 87);
            btnUpdateHistory.ForeColor = Color.White;
            btnUpdateHistory.FlatAppearance.BorderSize = 0;
            btnUpdateHistory.Click += BtnUpdateHistory_Click;
            section4.Controls.Add(btnUpdateHistory);

            // 测试抓取按钮
            Button btnTestCrawl = new Button();
            btnTestCrawl.Text = "测试抓取";
            btnTestCrawl.Font = new Font("微软雅黑", 11);
            btnTestCrawl.Size = new Size(90, 32);
            btnTestCrawl.Location = new Point(265, 115);
            btnTestCrawl.BackColor = Color.FromArgb(155, 89, 182);
            btnTestCrawl.ForeColor = Color.White;
            btnTestCrawl.FlatAppearance.BorderSize = 0;
            btnTestCrawl.Click += BtnTestCrawl_Click;
            section4.Controls.Add(btnTestCrawl);

            // 状态提示
            saveStatus = new Label();
            saveStatus.Name = "saveStatus";
            saveStatus.Font = new Font("微软雅黑", 10);
            saveStatus.ForeColor = Color.Green;
            saveStatus.Location = new Point(15, 160);
            saveStatus.AutoSize = true;
            saveStatus.MaximumSize = new Size(580, 0);
            section4.Controls.Add(saveStatus);

            // ===== 开奖直播（底部）=====
            y += 10;
            Panel sectionLive = CreateSectionPanel(y, 60);
            mainPanel.Controls.Add(sectionLive);

            Label liveTitle = new Label();
            liveTitle.Text = "📺 开奖直播";
            liveTitle.Font = new Font("微软雅黑", 12, FontStyle.Bold);
            liveTitle.ForeColor = Color.FromArgb(220, 50, 50);
            liveTitle.Location = new Point(15, 10);
            liveTitle.AutoSize = true;
            sectionLive.Controls.Add(liveTitle);

            Button btnLive = new Button();
            btnLive.Text = "🔴 打开直播间";
            btnLive.Font = new Font("微软雅黑", 11, FontStyle.Bold);
            btnLive.Size = new Size(140, 36);
            btnLive.Location = new Point(150, 14);
            btnLive.BackColor = Color.FromArgb(220, 50, 50);
            btnLive.ForeColor = Color.White;
            btnLive.FlatAppearance.BorderSize = 0;
            btnLive.Click += (s, e) => {
                LiveStreamForm form = new LiveStreamForm();
                form.ShowDialog();
            };
            sectionLive.Controls.Add(btnLive);

            y += 75;
            mainPanel.ResumeLayout(true);
            mainPanel.AutoScrollPosition = Point.Empty;
        }

        // 创建分区面板
        private Panel CreateSectionPanel(int y, int height)
        {
            Panel panel = new Panel();
            panel.Location = new Point(20, y);
            panel.Size = new Size(Math.Max(600, mainPanel.Width - 40), height);
            panel.BackColor = Color.White;
            panel.BorderStyle = BorderStyle.FixedSingle;
            return panel;
        }

        // 添加信息行
        private void AddInfoLine(Panel parent, int x, int y, string text)
        {
            Label label = new Label();
            label.Text = text;
            label.Font = new Font("微软雅黑", 10);
            label.ForeColor = Color.FromArgb(60, 60, 80);
            label.Location = new Point(x, y);
            label.AutoSize = true;
            label.MaximumSize = new Size(600, 0);
            parent.Controls.Add(label);
        }

        private void AddPredictionTierRow(Panel parent, int y, string tierName,
            IEnumerable<string> zodiacs, string numberTitle, IEnumerable<int> numbers, bool emphasize)
        {
            Panel row = new Panel
            {
                Location = new Point(15, y), Size = new Size(630, 56),
                BackColor = Color.FromArgb(18, 18, 22), BorderStyle = BorderStyle.FixedSingle
            };
            const int fontSize = 14;
            TableLayoutPanel layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = Padding.Empty, Padding = new Padding(8, 7, 8, 6)
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 223));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            Label tierNameLabel = CreateTierCell($"{tierName}：", Color.Lime, fontSize);
            Label zodiacLabel = new Label
            {
                Text = string.Join(" ", zodiacs), Font = new Font("微软雅黑", fontSize, FontStyle.Bold),
                ForeColor = Color.FromArgb(255, 65, 55), Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Margin = Padding.Empty
            };
            Label numberTitleLabel = CreateTierCell($"{numberTitle}：", Color.Lime, fontSize);
            Label numberLabel = new Label
            {
                Text = string.Join(" ", numbers.Select(n => n.ToString("D2"))),
                Font = new Font("微软雅黑", fontSize, FontStyle.Bold),
                ForeColor = Color.FromArgb(255, 70, 55), Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Margin = Padding.Empty
            };
            layout.Controls.Add(tierNameLabel, 0, 0);
            layout.Controls.Add(zodiacLabel, 1, 0);
            layout.Controls.Add(numberTitleLabel, 2, 0);
            layout.Controls.Add(numberLabel, 3, 0);
            row.Controls.Add(layout);
            parent.Controls.Add(row);
        }

        private Label CreateTierCell(string text, Color color, int fontSize)
        {
            return new Label
            {
                Text = text, Font = new Font("微软雅黑", fontSize, FontStyle.Bold), ForeColor = color,
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty
            };
        }

        private void AddPredictionHistorySection(int y)
        {
            Panel section = CreateSectionPanel(y, 370);
            mainPanel.Controls.Add(section);

            Label title = new Label
            {
                Text = $"📜 近10期预测验证记录（{HomePredictionPeriods}期模型）",
                Font = new Font("微软雅黑", 11, FontStyle.Bold),
                ForeColor = Color.FromArgb(80, 80, 110), Location = new Point(12, 8), AutoSize = true
            };
            section.Controls.Add(title);

            Label legend = new Label
            {
                Text = "浅绿=前3命中  浅黄=前6命中  浅红=未命中  浅灰=未开奖",
                Font = new Font("微软雅黑", 9), ForeColor = Color.Gray,
                Location = new Point(12, 32), AutoSize = true
            };
            section.Controls.Add(legend);

            FlowLayoutPanel list = new FlowLayoutPanel
            {
                Location = new Point(10, 56), Size = new Size(638, 302), AutoScroll = true,
                FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Color.WhiteSmoke,
                Padding = new Padding(4)
            };
            section.Controls.Add(list);

            var records = DatabaseHelper.GetPredictionHistory(200)
                .Where(r => r.AnalysisPeriods == HomePredictionPeriods)
                .GroupBy(r => r.Issue)
                .OrderByDescending(g => int.TryParse(g.Key, out int issue) ? issue : 0)
                .Take(10)
                .Select(g => g.OrderByDescending(r => r.Id).First())
                .ToList();

            if (records.Count == 0)
            {
                list.Controls.Add(new Label
                {
                    Text = "暂无预测历史。执行预测后会自动留档，开奖更新后自动验证。",
                    Font = new Font("微软雅黑", 10), ForeColor = Color.Gray,
                    AutoSize = true, Margin = new Padding(10)
                });
                return;
            }

            foreach (var record in records)
                list.Controls.Add(CreatePredictionHistoryCard(record));
        }

        private Control CreatePredictionHistoryCard(DatabaseHelper.PredictionRecord record)
        {
            Color background = record.HitResult == "命中" ? Color.FromArgb(224, 247, 232)
                : record.Top6HitResult == "命中" ? Color.FromArgb(255, 248, 214)
                : record.HitResult == "未命中" ? Color.FromArgb(255, 230, 230)
                : Color.FromArgb(238, 240, 244);
            Panel card = new Panel
            {
                Size = new Size(602, 176), BackColor = background,
                BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(2, 2, 2, 8)
            };

            string resultText = record.HitResult == "命中" ? "前3命中"
                : record.Top6HitResult == "命中" ? "前6命中"
                : record.HitResult == "未命中" ? "未命中" : "未开奖";
            AddPredictionHistoryHeader(card, record, resultText);

            var ranked = ParseRankedZodiacs(record);
            var seven = ranked.Take(7).ToList();
            var five = seven.Take(5).ToList();
            var three = record.PredictZodiac.Split(',', StringSplitOptions.RemoveEmptyEntries).Take(3).ToList();
            if (three.Count == 0) three = five.Take(3).ToList();
            var one = three.Take(1).ToList();
            var numbers = record.PredictNumber.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(n => int.TryParse(n, out int value) ? value : 0).Where(n => n > 0).ToList();
            var threeNumbers = GetTierNumbers(numbers, three, 3);
            var oneNumber = GetTierNumbers(numbers, one, 1);

            AddHistoryTierLine(card, 34, "七肖", seven, "⑦码", numbers.Take(7), record.ActualZodiac);
            AddHistoryTierLine(card, 68, "五肖", five, "⑤码", numbers.Take(5), record.ActualZodiac);
            AddHistoryTierLine(card, 102, "三肖", three, "③码", threeNumbers, record.ActualZodiac);
            AddHistoryTierLine(card, 136, "一肖", one, "①码", oneNumber, record.ActualZodiac);
            return card;
        }

        private void AddPredictionHistoryHeader(Control parent, DatabaseHelper.PredictionRecord record, string resultText)
        {
            var header = new FlowLayoutPanel
            {
                Location = new Point(8, 4), Size = new Size(585, 26), AutoSize = false,
                FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = Padding.Empty
            };
            header.Controls.Add(CreateHistoryText(
                $"{record.Issue}期  |  {record.AnalysisPeriods}期模型  |  实际：" +
                $"{(string.IsNullOrEmpty(record.ActualNumber) ? "?" : record.ActualNumber)} ",
                Color.FromArgb(55, 55, 70), true));

            bool zodiacHit = record.HitResult == "命中" || record.Top6HitResult == "命中";
            header.Controls.Add(CreateHistoryText(
                string.IsNullOrEmpty(record.ActualZodiac) ? "?" : record.ActualZodiac,
                zodiacHit ? Color.FromArgb(0, 120, 50) : Color.FromArgb(55, 55, 70), zodiacHit));
            header.Controls.Add(CreateHistoryText("  |  ", Color.FromArgb(55, 55, 70), true));

            Color resultColor = record.HitResult == "命中" ? Color.FromArgb(0, 135, 55)
                : record.Top6HitResult == "命中" ? Color.FromArgb(0, 90, 190)
                : record.HitResult == "未命中" ? Color.FromArgb(210, 35, 35)
                : Color.Gray;
            header.Controls.Add(CreateHistoryText(resultText, resultColor, true));
            parent.Controls.Add(header);
        }

        private Label CreateHistoryText(string text, Color color, bool bold)
        {
            return new Label
            {
                Text = text, AutoSize = true, Margin = Padding.Empty,
                Font = new Font("微软雅黑", 10, bold ? FontStyle.Bold : FontStyle.Regular),
                ForeColor = color, TextAlign = ContentAlignment.MiddleLeft
            };
        }

        private List<string> ParseRankedZodiacs(DatabaseHelper.PredictionRecord record)
        {
            var top6 = record.Top6Zodiac.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(zodiac => zodiac.Trim())
                .Where(zodiac => !string.IsNullOrEmpty(zodiac));
            var ranked = record.ScoreDetails.Split('#')[0].Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Split(':')[0].Trim()).Where(z => !string.IsNullOrEmpty(z)).ToList();
            // 前六判定使用 Top6Zodiac，显示也必须以同一顺序为准，避免判定与卡片内容不一致。
            var aligned = top6
                .Concat(ranked)
                .Concat(record.PredictZodiac.Split(',', StringSplitOptions.RemoveEmptyEntries))
                .Distinct().ToList();
            return aligned;
        }

        private void AddHistoryTierLine(Control parent, int y, string tier, IEnumerable<string> zodiacs,
            string numberTier, IEnumerable<int> numbers, string actualZodiac)
        {
            var line = new FlowLayoutPanel
            {
                Location = new Point(10, y), AutoSize = false, Size = new Size(575, 28),
                FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = Padding.Empty
            };
            line.Controls.Add(CreateHistoryText($"{tier}：", Color.FromArgb(80, 55, 55), true));
            foreach (string zodiac in zodiacs)
            {
                bool hit = !string.IsNullOrEmpty(actualZodiac) && zodiac.Trim() == actualZodiac.Trim();
                line.Controls.Add(CreateHistoryText(zodiac + " ",
                    hit ? Color.FromArgb(0, 120, 50) : Color.FromArgb(185, 45, 38), hit));
            }
            line.Controls.Add(CreateHistoryText(
                $"   {numberTier}：{string.Join(" ", numbers.Select(n => n.ToString("D2")))}",
                Color.FromArgb(185, 45, 38), true));
            parent.Controls.Add(line);
        }

        private List<int> GetTierNumbers(IEnumerable<int> candidates, IEnumerable<string> requiredZodiacs, int count)
        {
            var candidateList = candidates.ToList();
            var required = requiredZodiacs.ToList();
            var selected = new List<int>();
            var latest = DatabaseHelper.GetLatestHistory(1).FirstOrDefault();
            string year = latest?.OpenTime?.Length >= 4 ? latest.OpenTime.Substring(0, 4)
                : latest?.Date?.Length >= 4 ? latest.Date.Substring(0, 4) : "";
            string yearPet = string.IsNullOrEmpty(year) ? "" : DatabaseHelper.GetYearPetPublic(year);

            foreach (string zodiac in required)
            {
                int match = candidateList.FirstOrDefault(n => DataCrawler.GetShengXiaoByTeMa(n.ToString("D2"), yearPet) == zodiac);
                if (match > 0 && !selected.Contains(match)) selected.Add(match);
            }
            selected.AddRange(candidateList.Where(n => !selected.Contains(n)).Take(Math.Max(0, count - selected.Count)));
            return selected.Take(count).ToList();
        }

        // 在后台生成首页预测，完成后回到 UI 线程刷新页面。
        private async Task LoadHomePredictionAsync()
        {
            try
            {
                var prediction = await Task.Run(() => AIEngine.Predict(HomePredictionPeriods));
                _cachedPredict = prediction;
                _predictionLoading = false;
                if (!IsDisposed && IsHandleCreated)
                    BeginInvoke(new Action(ShowHome));
            }
            catch (Exception ex)
            {
                _predictionLoading = false;
                AppLogger.Error("后台生成首页预测失败", ex);
            }
        }

        // 保存记录到数据库
        private async void BtnSaveHistory_Click(object sender, EventArgs e)
        {
            // 移除旧状态提示
            var oldStatus = mainPanel.Controls.Find("saveStatus", false);
            foreach (var c in oldStatus) mainPanel.Controls.Remove(c);

            string period = txtNumber?.Text?.Trim();
            string numbers = mainPanel.Controls.Find("txtNumbers", false)
                .FirstOrDefault()?.Text?.Trim();
            string shengxiao = ((ComboBox)mainPanel.Controls.Find("cboShengxiao", false)
                .FirstOrDefault())?.SelectedItem?.ToString();

            if (string.IsNullOrEmpty(period))
            {
                saveStatus.ForeColor = Color.Red;
                saveStatus.Text = "请输入期号";
                return;
            }

            if (string.IsNullOrEmpty(numbers))
            {
                saveStatus.ForeColor = Color.Red;
                saveStatus.Text = "请输入开奖号码";
                return;
            }

            // 写入数据库，自动记录当前日期
            DatabaseHelper.InsertHistory(period, numbers, DateTime.Now.ToString("yyyy-MM-dd"), shengxiao ?? "");

            saveStatus.ForeColor = Color.Green;
            saveStatus.Text = "保存成功！日期：" + DateTime.Now.ToString("yyyy-MM-dd");

            // 清空输入
            txtNumber.Text = "";
            mainPanel.Controls.Find("txtNumbers", false).FirstOrDefault().Text = "";
            ((ComboBox)mainPanel.Controls.Find("cboShengxiao", false).FirstOrDefault()).SelectedIndex = -1;
        }

        // 更新历史数据（从网站抓取）
        private async void BtnUpdateHistory_Click(object sender, EventArgs e)
        {
            // 移除旧状态提示
            var oldStatus = mainPanel.Controls.Find("saveStatus", false);
            foreach (var c in oldStatus) mainPanel.Controls.Remove(c);

            saveStatus = new Label();
            saveStatus.Name = "saveStatus";
            saveStatus.Font = new Font("微软雅黑", 11);
            saveStatus.ForeColor = Color.Blue;
            saveStatus.Location = new Point(380, 325);
            saveStatus.AutoSize = true;
            int downloadPeriods = GetSelectedDownloadPeriods();
            saveStatus.Text = $"正在抓取最近 {downloadPeriods} 期历史数据，可能跨年补抓...";
            mainPanel.Controls.Add(saveStatus);

            // 禁用按钮防止重复点击
            Button btn = (Button)sender;
            btn.Enabled = false;

            try
            {
                var result = await DataCrawler.FetchAndSaveAsync(downloadPeriods);

                if (result.Success)
                {
                    saveStatus.ForeColor = Color.Green;
                    saveStatus.Text = $"{result.Message}";

                    // 数据更新后：先验证AI预测，再重新训练
                    if (result.NewCount > 0)
                    {
                        int verified = DatabaseHelper.BatchVerifyAIPredicts();
                        if (verified > 0)
                        {
                            saveStatus.Text += $"\n已验证 {verified} 条AI预测记录";
                        }
                        AIEngine.Retrain();
                    }
                }
                else
                {
                    saveStatus.ForeColor = Color.Red;
                    saveStatus.Text = result.Message;
                }
            }
            catch (Exception ex)
            {
                saveStatus.ForeColor = Color.Red;
                saveStatus.Text = $"抓取失败：{ex.Message}";
            }
            finally
            {
                btn.Enabled = true;
            }
        }

        // 测试抓取（不保存数据，仅显示诊断信息）
        private async void BtnTestCrawl_Click(object sender, EventArgs e)
        {
            // 移除旧状态提示
            var oldStatus = mainPanel.Controls.Find("saveStatus", false);
            foreach (var c in oldStatus) mainPanel.Controls.Remove(c);

            saveStatus = new Label();
            saveStatus.Name = "saveStatus";
            saveStatus.Font = new Font("微软雅黑", 11);
            saveStatus.ForeColor = Color.Blue;
            saveStatus.Location = new Point(200, 350);
            saveStatus.AutoSize = true;
            saveStatus.Text = "正在测试连接...";
            mainPanel.Controls.Add(saveStatus);

            Button btn = (Button)sender;
            btn.Enabled = false;

            try
            {
                var result = await DataCrawler.FetchAndSaveAsync(isTest: true);

                if (result.Success)
                {
                    saveStatus.ForeColor = Color.Green;
                    saveStatus.Text = $"✅ 连接正常 | 解析到 {result.NewCount} 条记录";
                }
                else
                {
                    saveStatus.ForeColor = Color.Red;
                    saveStatus.Text = $"❌ {result.Message}";
                }
            }
            catch (Exception ex)
            {
                saveStatus.ForeColor = Color.Red;
                saveStatus.Text = $"❌ 测试失败：{ex.Message}";
            }
            finally
            {
                btn.Enabled = true;
            }
        }

        // 历史数据
        private void BtnHistory_Click(object sender, EventArgs e)
        {
            HistoryForm form = new HistoryForm();
            form.ShowDialog();
        }

        // AI生肖预测
        private void BtnZodiacPredict_Click(object sender, EventArgs e)
        {
            ZodiacPredictForm form = new ZodiacPredictForm();
            form.ShowDialog();
        }

        // AI预测历史
        private void BtnAIPredictHistory_Click(object sender, EventArgs e)
        {
            AIPredictHistoryForm form = new AIPredictHistoryForm();
            form.ShowDialog();
        }

        // 开奖直播
        private void BtnLive_Click(object sender, EventArgs e)
        {
            LiveStreamForm form = new LiveStreamForm();
            form.ShowDialog();
        }

        // 分析
        private void BtnAnalyze_Click(object sender, EventArgs e)
        {
            Form form = CreateReservedDataCenterForm();
            form.ShowDialog();
        }

        private Form CreateReservedDataCenterForm()
        {
            Form form = new Form();
            form.Text = "V6.5 数据中心 - 实验成绩榜";
            form.Size = new Size(1200, 720);
            form.StartPosition = FormStartPosition.CenterParent;
            form.BackColor = Color.White;

            Label title = new Label();
            title.Text = "实验成绩榜";
            title.Font = new Font("微软雅黑", 18, FontStyle.Bold);
            title.ForeColor = Color.FromArgb(30, 30, 60);
            title.Location = new Point(30, 28);
            title.AutoSize = true;
            form.Controls.Add(title);

            Label hint = new Label();
            hint.Text = "只统计已开奖记录：V6.5 四模型与智能预测模型分组展示，互不混算。";
            hint.Font = new Font("微软雅黑", 11);
            hint.ForeColor = Color.Gray;
            hint.Location = new Point(32, 80);
            hint.AutoSize = true;
            hint.MaximumSize = new Size(800, 0);
            form.Controls.Add(hint);

            Control scoreboard = V65ExperimentScoreboardView.Create();
            scoreboard.Location = new Point(30, 120);
            scoreboard.Size = new Size(1120, 520);
            scoreboard.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            form.Controls.Add(scoreboard);

            return form;
        }

        // 预测
        private void BtnPredict_Click(object sender, EventArgs e)
        {
            TrendPredictionForm form = new TrendPredictionForm();
            form.ShowDialog();
        }

        // ML特征实验：只使用目标期之前的数据，不替换现有V6.5预测结果
        private async void BtnMlBacktest_Click(object sender, EventArgs e)
        {
            btnMlBacktest.Enabled = false;
            try
            {
                var history = DatabaseHelper.GetLatestHistory(int.MaxValue);
                if (history.Count < 10)
                {
                    MessageBox.Show("历史数据不足，至少需要10期后才能进行滚动回测。", "ML滚动回测", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                var result = await Task.Run(() =>
                {
                    var lgb = MachineLearningPredictionService.RollingBacktest(history, 50, 30, MlModelKind.LightGbmStyle);
                    var xgb = MachineLearningPredictionService.RollingBacktest(history, 50, 30, MlModelKind.XgBoostStyle);
                    var lgbPath = MachineLearningPredictionService.SaveReport(lgb);
                    var xgbPath = MachineLearningPredictionService.SaveReport(xgb);
                    var top = MachineLearningPredictionService.Predict(history, 30, MlModelKind.LightGbmStyle).Take(6)
                        .Select(x => $"{x.Zodiac} {x.Probability:P1}");
                    return $"LightGBM-style：TOP3 {lgb.Top3HitRate:P1}，TOP6 {lgb.Top6HitRate:P1}，最大连续错 {lgb.MaximumConsecutiveMisses}\n" +
                           $"XGBoost-style：TOP3 {xgb.Top3HitRate:P1}，TOP6 {xgb.Top6HitRate:P1}，最大连续错 {xgb.MaximumConsecutiveMisses}\n\n" +
                           $"当前生肖概率（TOP6）：{string.Join("、", top)}\n" +
                           $"回测记录：{lgbPath}\n{xgbPath}\n\n" +
                           "说明：该实验模型不写入或覆盖V6.5正式预测；回测每期只读取更早的历史记录。";
                });
                MessageBox.Show(result, "V6.5 ML滚动回测", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"ML回测失败：{ex.Message}", "V6.5 ML滚动回测", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { btnMlBacktest.Enabled = true; }
        }

        private async void BtnV7Models_Click(object sender, EventArgs e)
        {
            btnV7Models.Enabled = false;
            try
            {
                var history = DatabaseHelper.GetLatestHistory(int.MaxValue);
                var text = await Task.Run(() =>
                {
                    string targetPeriod = GetNextPredictionPeriod(history);
                    var color = ColorEngine.Predict(history);
                    var optimized = AutoOptimizeEngine.Optimize(history, 30);
                    var engines = new[] { V7Engine.Predict(history) };
                    var report = AIReportEngine.Generate(history, engines, color: color);
                    V7PredictionHistoryService.SaveAll(targetPeriod, history);
                    return $"预测期号：{targetPeriod}\n" +
                           $"波色：排除 {color.Excluded} / 主 {color.Main} / 防 {color.Defense}\n" +
                           $"最优权重：{optimized.Best?.Name ?? "无"}（TOP6 {optimized.Best?.Top6HitRate:P1}）\n\n" +
                           report.Text;
                });
                MessageBox.Show(text, "智能模型实验", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) { MessageBox.Show($"智能模型运行失败：{ex.Message}", "智能模型实验", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            finally { btnV7Models.Enabled = true; }
        }

        private static string GetNextPredictionPeriod(IReadOnlyList<DatabaseHelper.HistoryRecord> history)
        {
            var latest = history.Select(x => x.Period).Where(x => int.TryParse(x, out _))
                .Select(int.Parse).DefaultIfEmpty(0).Max();
            return latest > 0 ? (latest + 1).ToString() : "下一期";
        }

        // 自用规律
        private void BtnCheck_Click(object sender, EventArgs e)
        {
            ZodiacRuleForm form = new ZodiacRuleForm();
            form.ShowDialog();
        }
    }
}
