using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace 六合分析软件
{
    public partial class TrendPredictionForm : Form
    {
        TabControl tabControl;
        Panel topBar;
        Label dataInfoLabel;
        ComboBox periodSelector;
        DateTime _lastLoadTime;

        private int SelectedPeriods => periodSelector?.SelectedItem is int periods ? periods : 200;

        private readonly bool _openDataCenterTab;

        public TrendPredictionForm(bool openDataCenterTab = false)
        {
            InitializeComponent();
            _openDataCenterTab = openDataCenterTab;
            this.Text = "走势预测 / 数据中心";
            this.Size = new Size(1050, 680);
            this.StartPosition = FormStartPosition.CenterParent;
            InitUI();
        }

        private void InitUI()
        {
            topBar = new Panel();
            topBar.Location = new Point(0, 0);
            topBar.Size = new Size(984, 36);
            topBar.BackColor = Color.FromArgb(30, 30, 46);
            this.Controls.Add(topBar);

            Label title = new Label();
            title.Text = "走势预测 / 数据中心  |  ";
            title.Font = new Font("微软雅黑", 11, FontStyle.Bold);
            title.ForeColor = Color.White;
            title.Location = new Point(10, 8);
            title.AutoSize = true;
            topBar.Controls.Add(title);

            dataInfoLabel = new Label();
            dataInfoLabel.Text = "正在加载...";
            dataInfoLabel.Font = new Font("微软雅黑", 9);
            dataInfoLabel.ForeColor = Color.FromArgb(180, 180, 200);
            dataInfoLabel.Location = new Point(170, 10);
            dataInfoLabel.AutoSize = true;
            topBar.Controls.Add(dataInfoLabel);

            Label periodLabel = new Label();
            periodLabel.Text = "分析期数:";
            periodLabel.Font = new Font("微软雅黑", 9);
            periodLabel.ForeColor = Color.White;
            periodLabel.Location = new Point(680, 10);
            periodLabel.AutoSize = true;
            topBar.Controls.Add(periodLabel);

            periodSelector = new ComboBox();
            periodSelector.DropDownStyle = ComboBoxStyle.DropDownList;
            periodSelector.Font = new Font("微软雅黑", 9);
            periodSelector.Location = new Point(745, 6);
            periodSelector.Size = new Size(90, 25);
            periodSelector.Items.AddRange(new object[] { 50, 100, 200, 300, 500 });
            periodSelector.SelectedItem = 500;
            periodSelector.SelectedIndexChanged += (s, e) => LoadCurrentTab();
            topBar.Controls.Add(periodSelector);

            Button btnRefresh = new Button();
            btnRefresh.Text = "刷新数据";
            btnRefresh.Font = new Font("微软雅黑", 9);
            btnRefresh.Size = new Size(90, 26);
            btnRefresh.Location = new Point(880, 5);
            btnRefresh.BackColor = Color.FromArgb(0, 122, 204);
            btnRefresh.ForeColor = Color.White;
            btnRefresh.FlatAppearance.BorderSize = 0;
            btnRefresh.Click += (s, e) => { RefreshAll(); };
            topBar.Controls.Add(btnRefresh);

            tabControl = new TabControl();
            tabControl.Location = new Point(0, 36);
            tabControl.Size = new Size(984, 576);
            tabControl.Font = new Font("微软雅黑", 10);
            this.Controls.Add(tabControl);

            tabControl.TabPages.Add("阶段趋势", "  阶段趋势  ");
            tabControl.TabPages.Add("遗漏表", "  遗漏表  ");
            tabControl.TabPages.Add("连号分析", "  连号分析  ");
            tabControl.TabPages.Add("号码数据", "  号码数据  ");
            tabControl.TabPages.Add("生肖数据", "  生肖数据  ");
            tabControl.TabPages.Add("数据遗漏", "  数据遗漏  ");

            tabControl.SelectedIndex = 0;
            tabControl.SelectedIndexChanged += (s, e) => { LoadCurrentTab(); UpdateInfo(); };

            LoadCurrentTab();
            UpdateInfo();
        }

        public void RefreshAll()
        {
            UpdateInfo();
            LoadCurrentTab();
        }

        private void UpdateInfo()
        {
            _lastLoadTime = DateTime.Now;
            int count = DatabaseHelper.GetHistory().Count;
            string latest = DatabaseHelper.GetLatestPeriod();
            dataInfoLabel.Text = $"数据: {count} 条  |  最新: {latest}期  |  刷新: {_lastLoadTime:HH:mm:ss}";
        }

        private void LoadCurrentTab()
        {
            if (tabControl.SelectedTab == null) return;
            var page = tabControl.SelectedTab;
            page.Controls.Clear();

            string tabName = page.Text.Trim();
            switch (tabName)
            {
                case "阶段趋势": LoadZodiacTrend(page); break;
                case "遗漏表": LoadOmissionTable(page); break;
                case "连号分析": LoadConsecutiveAnalysis(page); break;
                case "号码数据": LoadNumberDataCenter(page); break;
                case "生肖数据": LoadZodiacDataCenter(page); break;
                case "数据遗漏": LoadMissingDataCenter(page); break;
            }
        }

        private void LoadNumberDataCenter(TabPage page)
        {
            var infoLabel = new Label
            {
                Location = new Point(20, 18),
                AutoSize = true,
                Font = new Font("微软雅黑", 9),
                ForeColor = Color.Gray
            };
            page.Controls.Add(infoLabel);

            var grid = new DataGridView
            {
                Location = new Point(20, 48),
                Size = new Size(Math.Max(780, page.ClientSize.Width - 40), Math.Max(420, page.ClientSize.Height - 70)),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                ReadOnly = true,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                BackgroundColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                ColumnHeadersHeight = 34,
                RowTemplate = { Height = 26 }
            };
            grid.Columns.Add("Number", "号码");
            grid.Columns.Add("AppearCount", "出现次数");
            grid.Columns.Add("CurrentMissing", "当前遗漏");
            grid.Columns.Add("MaxMissing", "最大遗漏");
            grid.Columns.Add("AvgMissing", "平均遗漏");
            grid.Columns.Add("Frequency", "频率");
            grid.Columns.Add("Level", "热度");
            grid.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            grid.CellFormatting += (s, e) =>
            {
                if (e.ColumnIndex == grid.Columns["Level"].Index && e.Value != null)
                {
                    string value = e.Value.ToString() ?? "";
                    if (value == "热")
                    {
                        e.CellStyle.BackColor = Color.FromArgb(255, 225, 225);
                        e.CellStyle.ForeColor = Color.DarkRed;
                    }
                    else if (value == "中")
                    {
                        e.CellStyle.BackColor = Color.FromArgb(255, 248, 210);
                        e.CellStyle.ForeColor = Color.DarkOrange;
                    }
                    else if (value == "冷")
                    {
                        e.CellStyle.BackColor = Color.FromArgb(225, 235, 255);
                        e.CellStyle.ForeColor = Color.DarkBlue;
                    }
                    e.CellStyle.Font = new Font("微软雅黑", 10, FontStyle.Bold);
                }
            };
            page.Controls.Add(grid);

            int periods = SelectedPeriods;
            var result = NumberStatisticsService.Calculate(periods);
            grid.Rows.Clear();
            foreach (var stat in result.Stats)
            {
                grid.Rows.Add(
                    stat.Number.ToString("D2"),
                    stat.AppearCount,
                    stat.CurrentMissing,
                    stat.MaxMissing,
                    stat.AvgMissing.ToString("F1"),
                    $"{stat.Frequency:F1}%",
                    stat.Level.ToString());
            }
            infoLabel.Text = $"号码数据 | 统计 {result.TotalPeriods} 期 | 热 {result.HotCount} | 中 {result.MediumCount} | 冷 {result.ColdCount}";
        }

        private void LoadZodiacDataCenter(TabPage page)
        {
            var infoLabel = new Label
            {
                Location = new Point(20, 18),
                AutoSize = true,
                Font = new Font("微软雅黑", 9),
                ForeColor = Color.Gray
            };
            page.Controls.Add(infoLabel);

            var grid = CreateDataGrid(page, 20, 48, Math.Max(780, page.ClientSize.Width - 40), Math.Max(420, page.ClientSize.Height - 70));
            grid.Columns.Add("Zodiac", "生肖");
            grid.Columns.Add("AppearCount", "出现次数");
            grid.Columns.Add("CurrentMissing", "当前遗漏");
            grid.Columns.Add("MaxMissing", "最大遗漏");
            grid.Columns.Add("Frequency", "频率");
            grid.Columns.Add("Recent10", "近10期");
            grid.Columns.Add("Recent30", "近30期");
            grid.Columns.Add("Trend", "趋势");

            int periods = SelectedPeriods;
            var result = ZodiacStatisticsService.Calculate(periods);
            foreach (var stat in result.Stats)
            {
                grid.Rows.Add(stat.Zodiac, stat.AppearCount, stat.CurrentMissing, stat.MaxMissing,
                    $"{stat.Frequency:F1}%", stat.Recent10Count, stat.Recent30Count, stat.TrendLabel);
            }

            infoLabel.Text = result.Stats.Count > 0
                ? $"生肖数据 | 统计 {result.TotalPeriods} 期 | 最热 {result.Stats[0].Zodiac} | 最冷 {result.Stats.Last().Zodiac}"
                : "生肖数据 | 暂无数据";
        }

        private void LoadMissingDataCenter(TabPage page)
        {
            var infoLabel = new Label
            {
                Location = new Point(20, 18),
                AutoSize = true,
                Font = new Font("微软雅黑", 9),
                ForeColor = Color.Gray
            };
            page.Controls.Add(infoLabel);

            int gridWidth = Math.Max(380, (page.ClientSize.Width - 55) / 2);
            int gridHeight = Math.Max(420, page.ClientSize.Height - 95);
            AddLabel(page, 20, 48, "号码遗漏", Color.FromArgb(80, 80, 100));
            AddLabel(page, 40 + gridWidth, 48, "生肖遗漏", Color.FromArgb(80, 80, 100));

            var numberGrid = CreateDataGrid(page, 20, 78, gridWidth, gridHeight);
            var zodiacGrid = CreateDataGrid(page, 40 + gridWidth, 78, gridWidth, gridHeight);

            foreach (var grid in new[] { numberGrid, zodiacGrid })
            {
                grid.Columns.Add("Item", "项目");
                grid.Columns.Add("CurrentMissing", "当前遗漏");
                grid.Columns.Add("MaxMissing", "最大遗漏");
                grid.Columns.Add("AvgMissing", "平均遗漏");
                grid.Columns.Add("TotalAppear", "出现次数");
                grid.Columns.Add("Status", "状态");
            }

            int periods = SelectedPeriods;
            var report = MissingNumberService.Calculate(periods);
            foreach (var item in report.NumberMissings)
            {
                numberGrid.Rows.Add(item.Item, item.CurrentMissing, item.MaxMissing,
                    item.AvgMissing.ToString("F1"), item.TotalAppear, item.HotStatus);
            }
            foreach (var item in report.ZodiacMissings)
            {
                zodiacGrid.Rows.Add(item.Item, item.CurrentMissing, item.MaxMissing,
                    item.AvgMissing.ToString("F1"), item.TotalAppear, item.HotStatus);
            }

            infoLabel.Text = report.Anomalies.Count > 0
                ? $"遗漏数据 | 统计 {report.TotalPeriods} 期 | 异常 {report.Anomalies.Count} 项"
                : $"遗漏数据 | 统计 {report.TotalPeriods} 期 | 无异常";
        }

        private DataGridView CreateDataGrid(Control parent, int x, int y, int width, int height)
        {
            var grid = new DataGridView
            {
                Location = new Point(x, y),
                Size = new Size(width, height),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                ReadOnly = true,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                BackgroundColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                ColumnHeadersHeight = 34,
                RowTemplate = { Height = 26 }
            };
            grid.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            parent.Controls.Add(grid);
            return grid;
        }

        // ===== 生肖走势 =====
        private void LoadZodiacTrend(TabPage page)
        {
            int periods = SelectedPeriods;
            var records = DatabaseHelper.GetLatestHistory(periods);
            if (records.Count == 0)
            {
                AddLabel(page, 20, 20, "暂无数据", Color.Gray);
                return;
            }

            var zodiacData = records
                .Where(r => !string.IsNullOrEmpty(r.SpecialZodiac))
                .Select(r => r.SpecialZodiac)
                .ToList();
            string[] zodiacs = { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };

            int y = 20;
            AddQualityLabel(page, periods, ref y);
            AddLabel(page, 20, y, "特码生肖阶段趋势（近期10期 对比 前10期）", Color.FromArgb(80, 80, 100));
            y += 28;
            AddLabel(page, 20, y, "趋势按两个相邻阶段的出现次数变化判断，全部数据只使用真实特码生肖。", Color.Gray);
            y += 35;

            DataGridView grid = new DataGridView();
            grid.Location = new Point(20, y);
            grid.Size = new Size(Math.Max(720, page.ClientSize.Width - 40), 365);
            grid.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.ReadOnly = true;
            grid.RowHeadersVisible = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.BackgroundColor = Color.White;
            grid.BorderStyle = BorderStyle.FixedSingle;
            grid.ColumnHeadersHeight = 34;
            grid.RowTemplate.Height = 26;
            grid.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            grid.Columns.Add("Zodiac", "生肖");
            grid.Columns.Add("Recent10", "近期10期");
            grid.Columns.Add("Previous10", "前10期");
            grid.Columns.Add("Change", "增减");
            grid.Columns.Add("PeriodTotal", $"近{periods}期");
            grid.Columns.Add("Missing", "当前遗漏");
            grid.Columns.Add("Trend", "趋势判断");

            foreach (var zodiac in zodiacs)
            {
                int recent10 = zodiacData.Take(10).Count(z => z == zodiac);
                int previous10 = zodiacData.Skip(10).Take(10).Count(z => z == zodiac);
                int periodTotal = zodiacData.Count(z => z == zodiac);
                int missing = zodiacData.TakeWhile(z => z != zodiac).Count();
                int change = recent10 - previous10;
                string trend = change >= 2 ? "明显升温" : change == 1 ? "升温"
                    : change == 0 ? "平稳" : change == -1 ? "降温" : "明显降温";
                string changeText = change > 0 ? $"+{change}" : change.ToString();

                int rowIndex = grid.Rows.Add(zodiac, recent10, previous10, changeText, periodTotal, missing, trend);
                grid.Rows[rowIndex].Cells[0].Style.BackColor = GetZodiacColor(zodiac);
                grid.Rows[rowIndex].Cells[0].Style.ForeColor = Color.White;
                grid.Rows[rowIndex].Cells[0].Style.Font = new Font("微软雅黑", 10, FontStyle.Bold);
                grid.Rows[rowIndex].Cells[6].Style.ForeColor = change > 0 ? Color.Red
                    : change < 0 ? Color.FromArgb(40, 100, 190) : Color.Gray;
            }

            page.Controls.Add(grid);
        }

        // ===== 遗漏表 =====
        private void LoadOmissionTable(TabPage page)
        {
            int periods = SelectedPeriods;
            var records = DatabaseHelper.GetLatestHistory(periods);
            if (records.Count == 0)
            {
                AddLabel(page, 20, 20, "暂无数据", Color.Gray);
                return;
            }

            int y = 20;
            AddQualityLabel(page, periods, ref y);
            AddLabel(page, 20, y, $"特码生肖遗漏表（最近{periods}期）:", Color.FromArgb(80, 80, 100));
            y += 30;

            string[] zodiacs = { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };
            var zodiacData = records.Where(r => !string.IsNullOrEmpty(r.SpecialZodiac)).Select(r => r.SpecialZodiac).ToList();

            AddLabel(page, 20, y, "生肖", Color.Gray);
            AddLabel(page, 80, y, "当前遗漏", Color.Gray);
            AddLabel(page, 180, y, "最大遗漏", Color.Gray);
            AddLabel(page, 280, y, "平均遗漏", Color.Gray);
            AddLabel(page, 380, y, "出现次数", Color.Gray);
            y += 25;

            Panel divider = new Panel();
            divider.Size = new Size(500, 1);
            divider.BackColor = Color.LightGray;
            divider.Location = new Point(20, y);
            page.Controls.Add(divider);
            y += 5;

            foreach (var z in zodiacs)
            {
                int currentOmission = -1;
                for (int i = 0; i < zodiacData.Count; i++)
                    if (zodiacData[i] == z) { currentOmission = i; break; }
                if (currentOmission < 0) currentOmission = zodiacData.Count;

                var intervals = new List<int>();
                int lastPos = -1;
                for (int i = 0; i < zodiacData.Count; i++)
                {
                    if (zodiacData[i] == z)
                    {
                        if (lastPos >= 0) intervals.Add(i - lastPos - 1);
                        lastPos = i;
                    }
                }
                int maxOm = intervals.Count > 0 ? intervals.Max() : zodiacData.Count;
                double avgOm = intervals.Count > 0 ? intervals.Average() : zodiacData.Count;
                int appearCount = zodiacData.Count(zd => zd == z);

                Color c = currentOmission > 20 ? Color.Red : currentOmission > 10 ? Color.Orange : Color.Green;
                AddLabel(page, 20, y, z, Color.Black);
                AddLabel(page, 80, y, currentOmission.ToString(), c);
                AddLabel(page, 180, y, maxOm.ToString(), Color.Gray);
                AddLabel(page, 280, y, avgOm.ToString("F1"), Color.Gray);
                AddLabel(page, 380, y, appearCount.ToString(), Color.Gray);
                y += 25;
            }
        }

        // ===== 连号分析 =====
        private void LoadConsecutiveAnalysis(TabPage page)
        {
            int periods = SelectedPeriods;
            var records = DatabaseHelper.GetLatestHistory(periods);
            if (records.Count < 2)
            {
                AddLabel(page, 20, 20, "数据不足", Color.Gray);
                return;
            }

            int y = 20;
            AddQualityLabel(page, periods, ref y);
            AddLabel(page, 20, y, $"连号分析（最近{periods}期）:", Color.FromArgb(80, 80, 100));
            y += 30;

            string[] zodiacs = { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };
            var zodiacData = records.Where(r => !string.IsNullOrEmpty(r.SpecialZodiac)).Select(r => r.SpecialZodiac).ToList();

            int consecutiveCount = 0;
            for (int i = 0; i < zodiacData.Count - 1; i++)
                if (zodiacData[i] == zodiacData[i + 1]) consecutiveCount++;

            AddLabel(page, 20, y, "连续出现次数: " + consecutiveCount + " 次", Color.Black);
            y += 25;

            y += 10;
            AddLabel(page, 20, y, "生肖转移矩阵（A出现后，B出现的次数）:", Color.FromArgb(80, 80, 100));
            y += 30;

            var transferMatrix = new Dictionary<string, Dictionary<string, int>>();
            foreach (var z in zodiacs) transferMatrix[z] = new Dictionary<string, int>();

            for (int i = 0; i < zodiacData.Count - 1; i++)
            {
                // 数据为最新到最旧，转移关系要按旧期 -> 新期计算。
                string from = zodiacData[i + 1];
                string to = zodiacData[i];
                if (transferMatrix.ContainsKey(from))
                {
                    if (!transferMatrix[from].ContainsKey(to))
                        transferMatrix[from][to] = 0;
                    transferMatrix[from][to]++;
                }
            }

            int maxZ = zodiacs.Length;
            for (int i = 0; i < maxZ; i++)
            {
                string from = zodiacs[i];
                var transfers = transferMatrix[from].OrderByDescending(t => t.Value).Take(3).ToList();

                string text = from + " -> ";
                if (transfers.Count > 0)
                    text += string.Join(", ", transfers.Select(t => t.Key + t.Value + "次"));
                else
                    text += "无后续数据";

                AddLabel(page, 40, y, text, Color.Black);
                y += 22;
            }
        }

        private Color GetZodiacColor(string zodiac)
        {
            var map = new Dictionary<string, Color>
            {
                { "鼠", Color.FromArgb(255, 100, 100) }, { "牛", Color.FromArgb(255, 150, 100) },
                { "虎", Color.FromArgb(255, 200, 100) }, { "兔", Color.FromArgb(255, 250, 100) },
                { "龙", Color.FromArgb(100, 200, 100) }, { "蛇", Color.FromArgb(100, 200, 200) },
                { "马", Color.FromArgb(100, 150, 255) }, { "羊", Color.FromArgb(150, 100, 255) },
                { "猴", Color.FromArgb(200, 100, 255) }, { "鸡", Color.FromArgb(255, 100, 200) },
                { "狗", Color.FromArgb(255, 100, 150) }, { "猪", Color.FromArgb(255, 100, 100) }
            };
            return map.ContainsKey(zodiac) ? map[zodiac] : Color.Gray;
        }

        private void AddQualityLabel(Control page, int periods, ref int y)
        {
            var quality = DataCheckService.CheckSelection(periods);
            AddLabel(page, 20, y, (quality.IsComplete ? "✅ " : "⚠ ") + quality.Summary,
                quality.IsComplete ? Color.FromArgb(0, 130, 70) : Color.FromArgb(210, 100, 0));
            y += 28;
        }

        private void AddLabel(Control parent, int x, int y, string text, Color color)
        {
            Label label = new Label();
            label.Text = text;
            label.Font = new Font("微软雅黑", 10);
            label.ForeColor = color;
            label.Location = new Point(x, y);
            label.AutoSize = true;
            parent.Controls.Add(label);
        }
    }
}
