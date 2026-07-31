using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace 六合分析软件
{
    public partial class DataAnalysisForm : Form
    {
        TabControl tabControl;
        Panel topBar;
        Label dataInfoLabel;
        DateTime _lastLoadTime;

        public DataAnalysisForm()
        {
            InitializeComponent();
            this.Text = "数据分析模块";
            this.Size = new Size(900, 650);
            this.StartPosition = FormStartPosition.CenterParent;
            InitUI();
        }

        private void InitUI()
        {
            topBar = new Panel();
            topBar.Location = new Point(0, 0);
            topBar.Size = new Size(884, 36);
            topBar.BackColor = Color.FromArgb(30, 30, 46);
            this.Controls.Add(topBar);

            Label title = new Label();
            title.Text = "数据分析  |  ";
            title.Font = new Font("微软雅黑", 11, FontStyle.Bold);
            title.ForeColor = Color.White;
            title.Location = new Point(10, 8);
            title.AutoSize = true;
            topBar.Controls.Add(title);

            dataInfoLabel = new Label();
            dataInfoLabel.Text = "正在加载...";
            dataInfoLabel.Font = new Font("微软雅黑", 9);
            dataInfoLabel.ForeColor = Color.FromArgb(180, 180, 200);
            dataInfoLabel.Location = new Point(120, 10);
            dataInfoLabel.AutoSize = true;
            topBar.Controls.Add(dataInfoLabel);

            Button btnRefresh = new Button();
            btnRefresh.Text = "刷新数据";
            btnRefresh.Font = new Font("微软雅黑", 9);
            btnRefresh.Size = new Size(90, 26);
            btnRefresh.Location = new Point(780, 5);
            btnRefresh.BackColor = Color.FromArgb(0, 122, 204);
            btnRefresh.ForeColor = Color.White;
            btnRefresh.FlatAppearance.BorderSize = 0;
            btnRefresh.Click += (s, e) => { RefreshAll(); };
            topBar.Controls.Add(btnRefresh);

            tabControl = new TabControl();
            tabControl.Location = new Point(0, 36);
            tabControl.Size = new Size(884, 576);
            tabControl.Font = new Font("微软雅黑", 10);
            this.Controls.Add(tabControl);

            tabControl.TabPages.Add("基本统计", "  基本统计  ");
            tabControl.TabPages.Add("冷热分析", "  冷热分析  ");
            tabControl.TabPages.Add("遗漏分析", "  遗漏分析  ");
            tabControl.TabPages.Add("生肖分布", "  生肖分布  ");

            tabControl.SelectedIndex = 0;
            tabControl.SelectedIndexChanged += (s, e) => RefreshAll();

            UpdateInfo();
            LoadCurrentTab();
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
                case "基本统计": LoadBasicStats(page); break;
                case "冷热分析": LoadHotColdAnalysis(page); break;
                case "遗漏分析": LoadOmissionAnalysis(page); break;
                case "生肖分布": LoadZodiacDistribution(page); break;
            }
        }

        private void LoadBasicStats(TabPage page)
        {
            var records = DatabaseHelper.GetLatestHistory(200);
            if (records.Count == 0) { AddLabel(page, 20, 20, "暂无数据", Color.Gray); return; }
            int y = 20;
            AddLabel(page, 20, y, "分析期数: " + records.Count + " 期", Color.FromArgb(80, 80, 100));
            y += 30;
            int odd = 0, even = 0, big = 0, small = 0, sum = 0, max = 0, min = 99;
            var all = new List<int>();
            foreach (var r in records)
            {
                if (string.IsNullOrEmpty(r.Numbers)) continue;
                string[] ns = r.Numbers.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var n in ns)
                {
                    if (int.TryParse(n, out int num) && num > 0 && num <= 49)
                    {
                        all.Add(num);
                        if (num % 2 == 1) odd++; else even++;
                        if (num >= 25) big++; else small++;
                        sum += num;
                        if (num > max) max = num;
                        if (num < min) min = num;
                    }
                }
            }
            int span = max > 0 && min < 99 ? max - min : 0;
            double avg = all.Count > 0 ? (double)sum / all.Count : 0;
            AddLabel(page, 20, y, "奇偶比:", Color.FromArgb(80, 80, 100));
            AddLabel(page, 120, y, $"奇 {odd} ({(double)odd / Math.Max(odd + even, 1) * 100:F1}%)  |  偶 {even} ({(double)even / Math.Max(odd + even, 1) * 100:F1}%)", Color.Black);
            y += 25;
            AddLabel(page, 20, y, "大小比:", Color.FromArgb(80, 80, 100));
            AddLabel(page, 120, y, $"大 {big} ({(double)big / Math.Max(big + small, 1) * 100:F1}%)  |  小 {small} ({(double)small / Math.Max(big + small, 1) * 100:F1}%)", Color.Black);
            y += 25;
            AddLabel(page, 20, y, "和值:", Color.FromArgb(80, 80, 100));
            AddLabel(page, 120, y, $"总和 {sum}  |  平均 {avg:F1}", Color.Black);
            y += 25;
            AddLabel(page, 20, y, "跨度:", Color.FromArgb(80, 80, 100));
            AddLabel(page, 120, y, $"最大 {max} - 最小 {min} = {span}", Color.Black);
            y += 40;
            AddLabel(page, 20, y, "号码出现次数 TOP10:", Color.FromArgb(80, 80, 100));
            y += 25;
            var top10 = all.GroupBy(n => n).Select(g => new { Num = g.Key, Cnt = g.Count() }).OrderByDescending(x => x.Cnt).Take(10).ToList();
            foreach (var item in top10)
            {
                AddLabel(page, 40, y, $"{item.Num:D2} 出现 {item.Cnt} 次 {new string('#', item.Cnt)}", Color.Black);
                y += 20;
            }
        }

        private void LoadHotColdAnalysis(TabPage page)
        {
            var engine = new ZodiacPredictEngineV2();
            int y = 20;
            AddLabel(page, 20, y, "热门生肖（按出现频率）:", Color.FromArgb(200, 50, 50));
            y += 30;
            var hot = engine.GetHotZodiacs(200);
            foreach (var z in hot) { AddLabel(page, 40, y, $"{z.Zodiac} {z.Count}次 ({z.Rate:F1}%) {new string('#', z.Count)}", Color.Black); y += 22; }
            y += 20;
            AddLabel(page, 20, y, "冷门生肖（按出现频率）:", Color.FromArgb(50, 100, 200));
            y += 30;
            var cold = engine.GetColdZodiacs(200);
            foreach (var z in cold) { AddLabel(page, 40, y, $"{z.Zodiac} {z.Count}次 ({z.Rate:F1}%) {new string('.', z.Count)}", Color.Gray); y += 22; }
        }

        private void LoadOmissionAnalysis(TabPage page)
        {
            var records = DatabaseHelper.GetLatestHistory(200);
            if (records.Count == 0) { AddLabel(page, 20, 20, "暂无数据", Color.Gray); return; }
            int y = 20;
            AddLabel(page, 20, y, "特码生肖遗漏分析（最近200期）:", Color.FromArgb(80, 80, 100));
            y += 30;
            string[] zs = { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };
            var zd = records.Where(r => !string.IsNullOrEmpty(r.SpecialZodiac)).Select(r => r.SpecialZodiac).ToList();
            foreach (var z in zs)
            {
                int last = -1;
                for (int i = 0; i < zd.Count; i++) { if (zd[i] == z) { last = i; break; } }
                int cur = last < 0 ? zd.Count : last;
                int max = 0, ls = -1;
                for (int i = 0; i < zd.Count; i++) { if (zd[i] == z) { if (ls >= 0) { int g = i - ls; if (g > max) max = g; } ls = i; } }
                if (ls >= 0 && zd.Count - ls > max) max = zd.Count - ls;
                Color c = cur > 20 ? Color.Red : cur > 10 ? Color.Orange : Color.Green;
                AddLabel(page, 40, y, $"{z}  当前遗漏: {cur}期  最大遗漏: {max}期", c);
                y += 22;
            }
        }

        private void LoadZodiacDistribution(TabPage page)
        {
            var records = DatabaseHelper.GetLatestHistory(200);
            if (records.Count == 0) { AddLabel(page, 20, 20, "暂无数据", Color.Gray); return; }
            int y = 20;
            AddLabel(page, 20, y, "特码生肖分布（最近200期）:", Color.FromArgb(80, 80, 100));
            y += 30;
            string[] zs = { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };
            var zd = records.Where(r => !string.IsNullOrEmpty(r.SpecialZodiac)).Select(r => r.SpecialZodiac).ToList();
            int tot = zd.Count;
            var colors = new Dictionary<string, Color> {
                { "鼠", Color.FromArgb(255,100,100) }, { "牛", Color.FromArgb(255,150,100) },
                { "虎", Color.FromArgb(255,200,100) }, { "兔", Color.FromArgb(255,250,100) },
                { "龙", Color.FromArgb(100,200,100) }, { "蛇", Color.FromArgb(100,200,200) },
                { "马", Color.FromArgb(100,150,255) }, { "羊", Color.FromArgb(150,100,255) },
                { "猴", Color.FromArgb(200,100,255) }, { "鸡", Color.FromArgb(255,100,200) },
                { "狗", Color.FromArgb(255,100,150) }, { "猪", Color.FromArgb(255,100,100) } };
            foreach (var z in zs)
            {
                int cnt = zd.Count(zd2 => zd2 == z);
                double rt = tot > 0 ? (double)cnt / tot * 100 : 0;
                Panel bar = new Panel();
                bar.Size = new Size((int)(rt * 3), 20);
                bar.BackColor = colors.ContainsKey(z) ? colors[z] : Color.Gray;
                bar.Location = new Point(150, y - 2);
                page.Controls.Add(bar);
                AddLabel(page, 40, y, $"{z} {cnt}次 ({rt:F1}%)", Color.Black);
                y += 25;
            }
        }

        private void AddLabel(Control parent, int x, int y, string text, Color color)
        {
            parent.Controls.Add(new Label { Text = text, Font = new Font("微软雅黑", 10), ForeColor = color, Location = new Point(x, y), AutoSize = true });
        }
    }
}
