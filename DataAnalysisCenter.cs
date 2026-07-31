using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace 六合分析软件
{
    /// <summary>
    /// 数据分析中心 — 号码冷热分析、生肖分析、遗漏分析
    /// </summary>
    public partial class DataAnalysisCenter : Form
    {
        TabControl tabControl;
        ComboBox cboPeriods1, cboPeriods2, cboPeriods3;
        DataGridView gridNumbers, gridZodiacs, gridMissing;
        Label infoLabel1, infoLabel2, infoLabel3;
        Button btnRefresh1, btnRefresh2, btnRefresh3;

        public DataAnalysisCenter()
        {
            InitializeComponent();
            this.Text = "数据分析中心 V2.0";
            this.Size = new Size(1050, 720);
            this.StartPosition = FormStartPosition.CenterParent;
            this.MinimumSize = new Size(900, 600);

            InitUI();
        }

        void InitUI()
        {
            Panel topBar = new Panel { Dock = DockStyle.Top, Height = 50, BackColor = Color.FromArgb(30, 30, 46) };
            Label title = new Label
            {
                Text = "📊 数据分析中心 V2.0",
                Font = new Font("微软雅黑", 16, FontStyle.Bold),
                ForeColor = Color.White,
                Location = new Point(20, 10),
                AutoSize = true
            };
            topBar.Controls.Add(title);
            Controls.Add(topBar);

            tabControl = new TabControl
            {
                Dock = DockStyle.Fill,
                Font = new Font("微软雅黑", 10)
            };
            Controls.Add(tabControl);

            // === Tab 1: 号码冷热分析 ===
            var tab1 = new TabPage("🔥 号码冷热分析");
            BuildTab1(tab1);
            tabControl.TabPages.Add(tab1);

            // === Tab 2: 生肖冷热分析 ===
            var tab2 = new TabPage("🐉 生肖冷热分析");
            BuildTab2(tab2);
            tabControl.TabPages.Add(tab2);

            // === Tab 3: 遗漏分析 ===
            var tab3 = new TabPage("⏳ 遗漏分析");
            BuildTab3(tab3);
            tabControl.TabPages.Add(tab3);

            // 初始加载
            RefreshAll();
        }

        void BuildTab1(TabPage tab)
        {
            cboPeriods1 = new ComboBox
            {
                Location = new Point(15, 15), Size = new Size(120, 28),
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font("微软雅黑", 10)
            };
            cboPeriods1.Items.AddRange(new object[] { "最近50期", "最近100期", "最近300期", "最近500期" });
            cboPeriods1.SelectedIndex = 3;
            cboPeriods1.SelectedIndexChanged += (s, e) => RefreshNumbers();
            tab.Controls.Add(cboPeriods1);

            btnRefresh1 = new Button
            {
                Text = "🔄 刷新", Location = new Point(145, 13), Size = new Size(80, 30),
                BackColor = Color.FromArgb(0, 122, 204), ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat, Font = new Font("微软雅黑", 9)
            };
            btnRefresh1.FlatAppearance.BorderSize = 0;
            btnRefresh1.Click += (s, e) => RefreshNumbers();
            tab.Controls.Add(btnRefresh1);

            infoLabel1 = new Label
            {
                Location = new Point(240, 17), AutoSize = true,
                Font = new Font("微软雅黑", 9), ForeColor = Color.Gray
            };
            tab.Controls.Add(infoLabel1);

            gridNumbers = new DataGridView
            {
                Location = new Point(15, 55), Size = new Size(990, 580),
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                AllowUserToAddRows = false, RowHeadersVisible = false,
                ReadOnly = true, SelectionMode = DataGridViewSelectionMode.FullRowSelect
            };
            gridNumbers.Columns.Add("Number", "号码");
            gridNumbers.Columns.Add("AppearCount", "出现次数");
            gridNumbers.Columns.Add("CurrentMissing", "当前遗漏");
            gridNumbers.Columns.Add("MaxMissing", "最大遗漏");
            gridNumbers.Columns.Add("AvgMissing", "平均遗漏");
            gridNumbers.Columns.Add("Frequency", "频率");
            gridNumbers.Columns.Add("Level", "热度");

            gridNumbers.Columns["Number"].FillWeight = 10;
            gridNumbers.Columns["AppearCount"].FillWeight = 12;
            gridNumbers.Columns["CurrentMissing"].FillWeight = 12;
            gridNumbers.Columns["MaxMissing"].FillWeight = 12;
            gridNumbers.Columns["AvgMissing"].FillWeight = 12;
            gridNumbers.Columns["Frequency"].FillWeight = 10;
            gridNumbers.Columns["Level"].FillWeight = 12;

            gridNumbers.CellFormatting += (s, e) =>
            {
                if (e.ColumnIndex == gridNumbers.Columns["Level"].Index && e.Value != null)
                {
                    switch (e.Value.ToString())
                    {
                        case "热": e.CellStyle.BackColor = Color.FromArgb(255, 200, 200); e.CellStyle.ForeColor = Color.DarkRed; break;
                        case "中": e.CellStyle.BackColor = Color.FromArgb(255, 255, 200); e.CellStyle.ForeColor = Color.DarkOrange; break;
                        case "冷": e.CellStyle.BackColor = Color.FromArgb(200, 220, 255); e.CellStyle.ForeColor = Color.DarkBlue; break;
                    }
                    e.CellStyle.Font = new Font("微软雅黑", 10, FontStyle.Bold);
                    e.CellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
                }
            };

            tab.Controls.Add(gridNumbers);
        }

        void BuildTab2(TabPage tab)
        {
            cboPeriods2 = new ComboBox
            {
                Location = new Point(15, 15), Size = new Size(120, 28),
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font("微软雅黑", 10)
            };
            cboPeriods2.Items.AddRange(new object[] { "最近50期", "最近100期", "最近300期", "最近500期" });
            cboPeriods2.SelectedIndex = 3;
            cboPeriods2.SelectedIndexChanged += (s, e) => RefreshZodiacs();
            tab.Controls.Add(cboPeriods2);

            btnRefresh2 = new Button
            {
                Text = "🔄 刷新", Location = new Point(145, 13), Size = new Size(80, 30),
                BackColor = Color.FromArgb(0, 122, 204), ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat, Font = new Font("微软雅黑", 9)
            };
            btnRefresh2.FlatAppearance.BorderSize = 0;
            btnRefresh2.Click += (s, e) => RefreshZodiacs();
            tab.Controls.Add(btnRefresh2);

            infoLabel2 = new Label
            {
                Location = new Point(240, 17), AutoSize = true,
                Font = new Font("微软雅黑", 9), ForeColor = Color.Gray
            };
            tab.Controls.Add(infoLabel2);

            gridZodiacs = new DataGridView
            {
                Location = new Point(15, 55), Size = new Size(990, 580),
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                AllowUserToAddRows = false, RowHeadersVisible = false,
                ReadOnly = true, SelectionMode = DataGridViewSelectionMode.FullRowSelect
            };
            gridZodiacs.Columns.Add("Zodiac", "生肖");
            gridZodiacs.Columns.Add("AppearCount", "出现次数");
            gridZodiacs.Columns.Add("CurrentMissing", "当前遗漏");
            gridZodiacs.Columns.Add("MaxMissing", "最大遗漏");
            gridZodiacs.Columns.Add("Frequency", "频率");
            gridZodiacs.Columns.Add("Recent10", "近10期");
            gridZodiacs.Columns.Add("Recent30", "近30期");
            gridZodiacs.Columns.Add("Trend", "趋势");

            gridZodiacs.CellFormatting += (s, e) =>
            {
                if (e.ColumnIndex == gridZodiacs.Columns["Trend"].Index && e.Value != null)
                {
                    string v = e.Value.ToString();
                    if (v.Contains("上升")) { e.CellStyle.ForeColor = Color.FromArgb(0, 150, 0); e.CellStyle.Font = new Font("微软雅黑", 10, FontStyle.Bold); }
                    else if (v.Contains("下降")) { e.CellStyle.ForeColor = Color.FromArgb(200, 50, 50); e.CellStyle.Font = new Font("微软雅黑", 10, FontStyle.Bold); }
                    else if (v.Contains("冷")) { e.CellStyle.ForeColor = Color.Gray; }
                }
            };

            tab.Controls.Add(gridZodiacs);
        }

        void BuildTab3(TabPage tab)
        {
            cboPeriods3 = new ComboBox
            {
                Location = new Point(15, 15), Size = new Size(120, 28),
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font("微软雅黑", 10)
            };
            cboPeriods3.Items.AddRange(new object[] { "最近50期", "最近100期", "最近300期", "最近500期" });
            cboPeriods3.SelectedIndex = 3;
            cboPeriods3.SelectedIndexChanged += (s, e) => RefreshMissing();
            tab.Controls.Add(cboPeriods3);

            btnRefresh3 = new Button
            {
                Text = "🔄 刷新", Location = new Point(145, 13), Size = new Size(80, 30),
                BackColor = Color.FromArgb(0, 122, 204), ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat, Font = new Font("微软雅黑", 9)
            };
            btnRefresh3.FlatAppearance.BorderSize = 0;
            btnRefresh3.Click += (s, e) => RefreshMissing();
            tab.Controls.Add(btnRefresh3);

            infoLabel3 = new Label
            {
                Location = new Point(240, 17), AutoSize = true,
                Font = new Font("微软雅黑", 9), ForeColor = Color.Gray
            };
            tab.Controls.Add(infoLabel3);

            // 分割左右：左=号码遗漏，右=生肖遗漏
            var splitPanel = new Panel
            {
                Location = new Point(15, 55), Size = new Size(990, 580)
            };
            tab.Controls.Add(splitPanel);

            // 左边：号码遗漏
            Label numLabel = new Label
            {
                Text = "📌 号码遗漏统计",
                Location = new Point(0, 0), AutoSize = true,
                Font = new Font("微软雅黑", 11, FontStyle.Bold)
            };
            splitPanel.Controls.Add(numLabel);

            gridMissing = new DataGridView
            {
                Location = new Point(0, 30), Size = new Size(485, 530),
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                AllowUserToAddRows = false, RowHeadersVisible = false,
                ReadOnly = true, SelectionMode = DataGridViewSelectionMode.FullRowSelect
            };
            gridMissing.Columns.Add("Item", "号码");
            gridMissing.Columns.Add("CurMiss", "当前遗漏");
            gridMissing.Columns.Add("MaxMiss", "最大遗漏");
            gridMissing.Columns.Add("AvgMiss", "平均遗漏");
            gridMissing.Columns.Add("Appear", "出现次数");
            gridMissing.Columns.Add("Status", "状态");

            gridMissing.Columns["Item"].FillWeight = 10;
            gridMissing.Columns["CurMiss"].FillWeight = 15;
            gridMissing.Columns["MaxMiss"].FillWeight = 15;
            gridMissing.Columns["AvgMiss"].FillWeight = 15;
            gridMissing.Columns["Appear"].FillWeight = 15;
            gridMissing.Columns["Status"].FillWeight = 15;

            gridMissing.CellFormatting += (s, e) =>
            {
                if (e.ColumnIndex == gridMissing.Columns["Status"].Index && e.Value != null)
                {
                    if (e.Value.ToString().Contains("⚠️")) { e.CellStyle.ForeColor = Color.Red; e.CellStyle.Font = new Font("微软雅黑", 9, FontStyle.Bold); }
                }
            };

            splitPanel.Controls.Add(gridMissing);

            // 右边：生肖遗漏
            var gridZodiacMissing = new DataGridView
            {
                Location = new Point(505, 30), Size = new Size(485, 530),
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                AllowUserToAddRows = false, RowHeadersVisible = false,
                ReadOnly = true, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                Name = "gridZodiacMissing"
            };
            gridZodiacMissing.Columns.Add("ItemZ", "生肖");
            gridZodiacMissing.Columns.Add("CurMissZ", "当前遗漏");
            gridZodiacMissing.Columns.Add("MaxMissZ", "最大遗漏");
            gridZodiacMissing.Columns.Add("AvgMissZ", "平均遗漏");
            gridZodiacMissing.Columns.Add("AppearZ", "出现次数");
            gridZodiacMissing.Columns.Add("StatusZ", "状态");

            gridZodiacMissing.CellFormatting += (s, e) =>
            {
                if (gridZodiacMissing.Columns["StatusZ"].Index == e.ColumnIndex && e.Value != null)
                {
                    if (e.Value.ToString().Contains("⚠️")) { e.CellStyle.ForeColor = Color.Red; e.CellStyle.Font = new Font("微软雅黑", 9, FontStyle.Bold); }
                }
            };

            splitPanel.Controls.Add(gridZodiacMissing);

            Label zodiacLabel = new Label
            {
                Text = "🐉 生肖遗漏统计",
                Location = new Point(505, 0), AutoSize = true,
                Font = new Font("微软雅黑", 11, FontStyle.Bold)
            };
            splitPanel.Controls.Add(zodiacLabel);
        }

        void RefreshAll()
        {
            RefreshNumbers();
            RefreshZodiacs();
            RefreshMissing();
        }

        int ParsePeriod(string text) => text switch
        {
            "最近50期" => 50, "最近100期" => 100, "最近300期" => 300, _ => 500
        };

        void RefreshNumbers()
        {
            try
            {
                int periods = ParsePeriod(cboPeriods1?.SelectedItem?.ToString() ?? "");
                var result = NumberStatisticsService.Calculate(periods);
                gridNumbers.Rows.Clear();

                foreach (var s in result.Stats)
                {
                    gridNumbers.Rows.Add(s.Number.ToString("D2"), s.AppearCount, s.CurrentMissing,
                        s.MaxMissing, $"{s.AvgMissing:F1}", $"{s.Frequency:F1}%", s.Level.ToString());
                }

                infoLabel1.Text = $"统计 {result.TotalPeriods} 期 | 热:{result.HotCount} 中:{result.MediumCount} 冷:{result.ColdCount}";
            }
            catch (Exception ex) { infoLabel1.Text = $"加载失败: {ex.Message}"; }
        }

        void RefreshZodiacs()
        {
            try
            {
                int periods = ParsePeriod(cboPeriods2?.SelectedItem?.ToString() ?? "");
                var result = ZodiacStatisticsService.Calculate(periods);
                gridZodiacs.Rows.Clear();

                foreach (var s in result.Stats)
                {
                    gridZodiacs.Rows.Add(s.Zodiac, s.AppearCount, s.CurrentMissing,
                        s.MaxMissing, $"{s.Frequency:F1}%", s.Recent10Count, s.Recent30Count, s.TrendLabel);
                }

                infoLabel2.Text = $"统计 {result.TotalPeriods} 期 | 最热: {result.Stats[0].Zodiac} | 最冷: {result.Stats.Last().Zodiac}";
            }
            catch (Exception ex) { infoLabel2.Text = $"加载失败: {ex.Message}"; }
        }

        void RefreshMissing()
        {
            try
            {
                int periods = ParsePeriod(cboPeriods3?.SelectedItem?.ToString() ?? "");
                var report = MissingNumberService.Calculate(periods);
                gridMissing.Rows.Clear();

                foreach (var n in report.NumberMissings)
                {
                    gridMissing.Rows.Add(n.Item, n.CurrentMissing, n.MaxMissing,
                        $"{n.AvgMissing:F1}", n.TotalAppear, n.HotStatus);
                }

                // 生肖遗漏
                var zGrid = gridMissing.Parent?.Controls.Find("gridZodiacMissing", true).FirstOrDefault() as DataGridView;
                if (zGrid != null)
                {
                    zGrid.Rows.Clear();
                    foreach (var z in report.ZodiacMissings)
                    {
                        zGrid.Rows.Add(z.Item, z.CurrentMissing, z.MaxMissing,
                            $"{z.AvgMissing:F1}", z.TotalAppear, z.HotStatus);
                    }
                }

                string anomalyText = report.Anomalies.Count > 0
                    ? $" | ⚠️ {report.Anomalies.Count} 项异常"
                    : " | ✅ 无异常";
                infoLabel3.Text = $"统计 {report.TotalPeriods} 期{anomalyText}";
            }
            catch (Exception ex) { infoLabel3.Text = $"加载失败: {ex.Message}"; }
        }
    }
}
