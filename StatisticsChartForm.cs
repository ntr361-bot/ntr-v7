using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace 六合分析软件
{
    /// <summary>
    /// 统计图表窗体
    /// </summary>
    public partial class StatisticsChartForm : Form
    {
        TabControl tabControl;
        Panel topBar;
        Label dataInfoLabel;
        ComboBox periodSelector;
        DateTime _lastLoadTime;

        private int SelectedPeriods => periodSelector?.SelectedItem is int periods ? periods : 200;

        public StatisticsChartForm()
        {
            InitializeComponent();
            this.Text = "统计图表模块";
            this.Size = new Size(900, 650);
            this.StartPosition = FormStartPosition.CenterParent;
            InitUI();
        }

        private void InitUI()
        {
            topBar = new Panel();
            topBar.Size = new Size(884, 36);
            topBar.Location = new Point(0, 0);
            topBar.BackColor = Color.FromArgb(30, 30, 46);
            topBar.Padding = new Padding(10, 5, 10, 5);
            this.Controls.Add(topBar);

            Label title = new Label();
            title.Text = "统计图表（数据实时更新）";
            title.Font = new Font("微软雅黑", 11, FontStyle.Bold);
            title.ForeColor = Color.White;
            title.Location = new Point(10, 8);
            title.AutoSize = true;
            topBar.Controls.Add(title);

            dataInfoLabel = new Label();
            dataInfoLabel.Text = "正在加载...";
            dataInfoLabel.Font = new Font("微软雅黑", 9);
            dataInfoLabel.ForeColor = Color.FromArgb(180, 180, 200);
            dataInfoLabel.Location = new Point(300, 10);
            dataInfoLabel.AutoSize = true;
            topBar.Controls.Add(dataInfoLabel);

            Label periodLabel = new Label();
            periodLabel.Text = "统计期数:";
            periodLabel.Font = new Font("微软雅黑", 9);
            periodLabel.ForeColor = Color.White;
            periodLabel.Location = new Point(575, 10);
            periodLabel.AutoSize = true;
            topBar.Controls.Add(periodLabel);

            periodSelector = new ComboBox();
            periodSelector.DropDownStyle = ComboBoxStyle.DropDownList;
            periodSelector.Font = new Font("微软雅黑", 9);
            periodSelector.Location = new Point(640, 6);
            periodSelector.Size = new Size(90, 25);
            periodSelector.Items.AddRange(new object[] { 50, 100, 200 });
            periodSelector.SelectedItem = 200;
            periodSelector.SelectedIndexChanged += (s, e) => LoadCurrentTab();
            topBar.Controls.Add(periodSelector);

            Button btnRefresh = new Button();
            btnRefresh.Text = "刷新数据";
            btnRefresh.Font = new Font("微软雅黑", 9);
            btnRefresh.Size = new Size(90, 26);
            btnRefresh.Location = new Point(780, 5);
            btnRefresh.BackColor = Color.FromArgb(0, 122, 204);
            btnRefresh.ForeColor = Color.White;
            btnRefresh.FlatAppearance.BorderSize = 0;
            btnRefresh.Click += (s, e) => { LoadCurrentTab(); UpdateInfo(); };
            topBar.Controls.Add(btnRefresh);

            tabControl = new TabControl();
            tabControl.Location = new Point(0, 36); tabControl.Size = new Size(884, 576);
            this.Controls.Add(tabControl);

            tabControl.TabPages.Add("生肖统计", "生肖统计");
            tabControl.TabPages.Add("号码统计", "号码统计");
            tabControl.TabPages.Add("趋势对比", "趋势对比");

            tabControl.SelectedIndex = 0;
            tabControl.SelectedIndexChanged += (s, e) => { LoadCurrentTab(); UpdateInfo(); };

            LoadCurrentTab();
            UpdateInfo();
        }

        public void RefreshAll()
        {
            LoadCurrentTab();
            UpdateInfo();
        }

        private void UpdateInfo()
        {
            _lastLoadTime = DateTime.Now;
            int count = DatabaseHelper.GetHistory().Count;
            string latest = DatabaseHelper.GetLatestPeriod();
            dataInfoLabel.Text = $"数据: {count} 条 | 最新: {latest}期 | 刷新: {_lastLoadTime:HH:mm:ss}";
        }

        private void LoadCurrentTab()
        {
            var page = tabControl.SelectedTab;
            page.Controls.Clear();

            // 添加自动滚动面板
            Panel scrollPanel = new Panel();
            scrollPanel.Dock = DockStyle.Fill;
            scrollPanel.AutoScroll = true;
            page.Controls.Add(scrollPanel);

            switch (page.Name)
            {
                case "生肖统计": LoadZodiacStats(scrollPanel); break;
                case "号码统计": LoadNumberStats(scrollPanel); break;
                case "趋势对比": LoadTrendComparison(scrollPanel); break;
            }
        }

        /// <summary>
        /// 生肖统计
        /// </summary>
        private void LoadZodiacStats(Panel page)
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
            AddLabel(page, 20, y, $"🐲 特码生肖统计（最近{periods}期）：", Color.FromArgb(80, 80, 100));
            y += 30;

            string[] zodiacs = { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };
            var zodiacData = records.Where(r => !string.IsNullOrEmpty(r.SpecialZodiac)).Select(r => r.SpecialZodiac).ToList();
            int total = zodiacData.Count;

            foreach (var z in zodiacs)
            {
                int count = zodiacData.Count(zd => zd == z);
                double rate = total > 0 ? (double)count / total * 100 : 0;

                // 条形图
                int barWidth = Math.Max(10, (int)(rate * 4));
                Panel bar = new Panel();
                bar.Size = new Size(barWidth, 22);
                bar.BackColor = GetZodiacColor(z);
                bar.Location = new Point(200, y - 2);
                page.Controls.Add(bar);

                AddLabel(page, 40, y, $"{z}", Color.Black);
                AddLabel(page, 100, y, $"{count}次", Color.Black);
                AddLabel(page, 150, y, $"({rate:F1}%)", Color.Gray);
                y += 28;
            }
        }

        /// <summary>
        /// 号码统计
        /// </summary>
        private void LoadNumberStats(Panel page)
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
            AddLabel(page, 20, y, $"🔢 特码出现次数统计（最近{periods}期）：", Color.FromArgb(80, 80, 100));
            y += 30;

            var allNumbers = records
                .Select(r => int.TryParse(r.SpecialNumber, out int special) ? special : 0)
                .Where(n => n >= 1 && n <= 49)
                .ToList();

            var numCounts = allNumbers.GroupBy(n => n)
                .Select(g => new { Num = g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count)
                .ToList();

            int maxCount = numCounts.Count > 0 ? numCounts.Max(x => x.Count) : 1;

            foreach (var item in numCounts)
            {
                int barWidth = Math.Max(10, (int)((double)item.Count / maxCount * 400));
                Panel bar = new Panel();
                bar.Size = new Size(barWidth, 18);
                bar.BackColor = item.Count > maxCount * 0.8 ? Color.FromArgb(255, 100, 100)
                    : item.Count > maxCount * 0.5 ? Color.FromArgb(255, 200, 100)
                    : Color.FromArgb(100, 200, 100);
                bar.Location = new Point(100, y - 2);
                page.Controls.Add(bar);

                AddLabel(page, 20, y, $"{item.Num:D2}", Color.Black);
                AddLabel(page, 60, y, $"{item.Count}次", Color.Gray);
                y += 22;
            }
        }

        /// <summary>
        /// 趋势对比
        /// </summary>
        private void LoadTrendComparison(Panel page)
        {
            int y = 20;
            AddLabel(page, 20, y, "📈 趋势对比分析：", Color.FromArgb(80, 80, 100));
            y += 30;

            // 对比不同周期的生肖分布
            int[] periods = { 50, 100, 200 };
            string[] zodiacs = { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };

            AddLabel(page, 20, y, "生肖", Color.Gray);
            int x = 80;
            foreach (var p in periods)
            {
                AddLabel(page, x, y, $"{p}期", Color.Gray);
                x += 80;
            }
            y += 25;

            Panel divider = new Panel();
            divider.Size = new Size(400, 1);
            divider.BackColor = Color.LightGray;
            divider.Location = new Point(20, y);
            page.Controls.Add(divider);
            y += 5;

            foreach (var z in zodiacs)
            {
                AddLabel(page, 20, y, z, Color.Black);
                int xPos = 80;
                foreach (var p in periods)
                {
                    var records = DatabaseHelper.GetLatestHistory(p);
                    var zodiacData = records.Where(r => !string.IsNullOrEmpty(r.SpecialZodiac)).Select(r => r.SpecialZodiac).ToList();
                    int count = zodiacData.Count(zd => zd == z);
                    double rate = zodiacData.Count > 0 ? (double)count / zodiacData.Count * 100 : 0;

                    AddLabel(page, xPos, y, $"{rate:F1}%", Color.Black);
                    xPos += 80;
                }
                y += 22;
            }
        }

        private Color GetZodiacColor(string zodiac)
        {
            var colors = new Dictionary<string, Color>
            {
                { "鼠", Color.FromArgb(255, 100, 100) },
                { "牛", Color.FromArgb(255, 150, 100) },
                { "虎", Color.FromArgb(255, 200, 100) },
                { "兔", Color.FromArgb(255, 250, 100) },
                { "龙", Color.FromArgb(100, 200, 100) },
                { "蛇", Color.FromArgb(100, 200, 200) },
                { "马", Color.FromArgb(100, 150, 255) },
                { "羊", Color.FromArgb(150, 100, 255) },
                { "猴", Color.FromArgb(200, 100, 255) },
                { "鸡", Color.FromArgb(255, 100, 200) },
                { "狗", Color.FromArgb(255, 100, 150) },
                { "猪", Color.FromArgb(255, 100, 100) }
            };
            return colors.ContainsKey(zodiac) ? colors[zodiac] : Color.Gray;
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
