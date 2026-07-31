using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace 六合分析软件
{
    /// <summary>
    /// 特码规律验证窗体
    /// 生肖表布局 (6行 x 2列):
    ///   鼠  马
    ///   牛  羊
    ///   虎  猴
    ///   兔  鸡
    ///   龙  狗
    ///   蛇  猪
    /// 预测: 计算生肖 + 上行的2个 + 下行的2个(循环) + 同行的另一列 = 6个生肖范围
    /// </summary>
    public partial class ZodiacRuleForm : Form
    {
        DataGridView table;
        Label statusLabel;
        Label summaryLabel;
        Label missAnalysisLabel;
        FlowLayoutPanel reversePanel;
        Label reverseTitleLabel;
        System.Windows.Forms.Timer autoRefreshTimer;
        string lastLoadedPeriod = "";
        int selectedPeriods = 150; // 默认验算今年最新往前150期

        // 生肖表: 6行 x 2列, 行号从0开始
        private static readonly string[,] ZodiacTable = new string[,]
        {
            { "鼠", "马" },
            { "牛", "羊" },
            { "虎", "猴" },
            { "兔", "鸡" },
            { "龙", "狗" },
            { "蛇", "猪" }
        };

        private static readonly Dictionary<string, (int row, int col)> ZodiacPositions = new Dictionary<string, (int, int)>
        {
            { "鼠", (0,0) }, { "马", (0,1) },
            { "牛", (1,0) }, { "羊", (1,1) },
            { "虎", (2,0) }, { "猴", (2,1) },
            { "兔", (3,0) }, { "鸡", (3,1) },
            { "龙", (4,0) }, { "狗", (4,1) },
            { "蛇", (5,0) }, { "猪", (5,1) }
        };

        public ZodiacRuleForm()
        {
            InitializeComponent();
            this.Text = "特码规律验证";
            this.Size = new Size(1050, 680);
            this.StartPosition = FormStartPosition.CenterParent;
            InitUI();
            ValidateData();
            StartAutoRefresh();
            this.Activated += (s, e) => RefreshIfLatestChanged();
            this.FormClosed += (s, e) => autoRefreshTimer?.Stop();
        }

        private void InitUI()
        {
            Label title = new Label();
            title.Text = "特码规律验证";
            title.Font = new Font("微软雅黑", 16, FontStyle.Bold);
            title.Location = new Point(20, 15);
            title.AutoSize = true;
            this.Controls.Add(title);

            Label rule = new Label();
            rule.Text = "规则: 平码首尾尾数和 → 生肖 → 上/下行+平行位 共6个生肖 → 预测下期特码";
            rule.Font = new Font("微软雅黑", 10);
            rule.ForeColor = Color.FromArgb(80, 80, 100);
            rule.Location = new Point(20, 48);
            rule.AutoSize = true;
            this.Controls.Add(rule);

            string tableStr = "生肖表:  鼠马 牛羊 虎猴 兔鸡 龙狗 蛇猪  (6行x2列)";
            Label tbl = new Label();
            tbl.Text = tableStr;
            tbl.Font = new Font("Consolas", 10);
            tbl.ForeColor = Color.FromArgb(100, 100, 120);
            tbl.Location = new Point(20, 72);
            tbl.AutoSize = true;
            this.Controls.Add(tbl);

            summaryLabel = new Label();
            summaryLabel.Font = new Font("微软雅黑", 11, FontStyle.Bold);
            summaryLabel.Location = new Point(20, 95);
            summaryLabel.AutoSize = false;
            summaryLabel.Size = new Size(590, 24);
            this.Controls.Add(summaryLabel);

            statusLabel = new Label();
            statusLabel.Font = new Font("微软雅黑", 9);
            statusLabel.ForeColor = Color.Gray;
            statusLabel.Location = new Point(20, 119);
            statusLabel.AutoSize = false;
            statusLabel.Size = new Size(980, 22);
            this.Controls.Add(statusLabel);

            table = new DataGridView();
            table.Location = new Point(20, 125);
            table.Size = new Size(1000, 500);
            table.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            table.AllowUserToAddRows = false;
            table.RowHeadersVisible = false;
            table.ReadOnly = true;
            table.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            table.DefaultCellStyle.SelectionBackColor = Color.FromArgb(220, 220, 220);
            table.DefaultCellStyle.SelectionForeColor = Color.Black;

            table.Columns.Add("期号", "源期号");
            table.Columns.Add("验证期", "验证期");
            table.Columns.Add("首位", "首位");
            table.Columns.Add("末位", "末位");
            table.Columns.Add("尾数和", "数和");
            table.Columns.Add("号码", "号码");
            table.Columns.Add("生肖", "生肖");
            table.Columns.Add("预测6肖", "预测6个生肖");
            table.Columns.Add("建议方向", "方向");
            table.Columns.Add("建议6肖", "建议6肖");
            table.Columns.Add("实际生肖", "实际");
            table.Columns.Add("结果", "结果");
            table.Columns.Add("建议结果", "建议");

            table.Columns["期号"].Width = 65;
            table.Columns["验证期"].Width = 65;
            table.Columns["首位"].Width = 65;
            table.Columns["末位"].Width = 65;
            table.Columns["尾数和"].Width = 45;
            table.Columns["号码"].Width = 45;
            table.Columns["生肖"].Width = 45;
            table.Columns["预测6肖"].Width = 155;
            table.Columns["建议方向"].Width = 55;
            table.Columns["建议6肖"].Width = 155;
            table.Columns["实际生肖"].Width = 55;
            table.Columns["结果"].Width = 50;
            table.Columns["建议结果"].Width = 50;

            table.CellFormatting += (s, e) =>
            {
                if (table.Columns[e.ColumnIndex]?.Name == "结果" || table.Columns[e.ColumnIndex]?.Name == "建议结果")
                {
                    if (e.Value?.ToString() == "命中")
                    {
                        e.CellStyle.BackColor = table.Columns[e.ColumnIndex]?.Name == "建议结果"
                            ? Color.FromArgb(215, 235, 255) : Color.FromArgb(255, 255, 180);
                        e.CellStyle.ForeColor = Color.FromArgb(0, 140, 0);
                        e.CellStyle.Font = new Font("微软雅黑", 9, FontStyle.Bold);
                    }
                    else
                    {
                        e.CellStyle.BackColor = Color.FromArgb(220, 190, 220);
                        e.CellStyle.ForeColor = Color.FromArgb(180, 50, 50);
                        e.CellStyle.Font = new Font("微软雅黑", 9, FontStyle.Bold);
                    }
                }
            };
            this.Controls.Add(table);
            
            // ===== 最新预测区 =====
            Panel predictPanel = new Panel();
            predictPanel.Location = new Point(20, 145);
            predictPanel.Size = new Size(1000, 40);
            predictPanel.BackColor = Color.FromArgb(255, 248, 220);
            predictPanel.BorderStyle = BorderStyle.FixedSingle;
            this.Controls.Add(predictPanel);

            Label predictPreview = new Label();
            predictPreview.Name = "predictPreview";
            predictPreview.Font = new Font("微软雅黑", 11, FontStyle.Bold);
            predictPreview.ForeColor = Color.FromArgb(200, 100, 0);
            predictPreview.Location = new Point(10, 8);
            predictPreview.AutoSize = true;
            predictPanel.Controls.Add(predictPreview);

            Panel analysisPanel = new Panel();
            analysisPanel.Location = new Point(20, 192);
            analysisPanel.Size = new Size(1000, 136);
            analysisPanel.BackColor = Color.FromArgb(245, 248, 252);
            analysisPanel.BorderStyle = BorderStyle.FixedSingle;
            this.Controls.Add(analysisPanel);

            Label analysisTitle = new Label();
            analysisTitle.Text = "智能分析建议";
            analysisTitle.Font = new Font("微软雅黑", 10, FontStyle.Bold);
            analysisTitle.ForeColor = Color.FromArgb(55, 75, 110);
            analysisTitle.Location = new Point(10, 7);
            analysisTitle.AutoSize = true;
            analysisPanel.Controls.Add(analysisTitle);

            missAnalysisLabel = new Label();
            missAnalysisLabel.Name = "missAnalysisLabel";
            missAnalysisLabel.Font = new Font("微软雅黑", 9);
            missAnalysisLabel.ForeColor = Color.FromArgb(55, 55, 70);
            missAnalysisLabel.Location = new Point(10, 30);
            missAnalysisLabel.Size = new Size(975, 62);
            missAnalysisLabel.AutoEllipsis = true;
            analysisPanel.Controls.Add(missAnalysisLabel);

            reverseTitleLabel = new Label();
            reverseTitleLabel.Text = "反区六肖：";
            reverseTitleLabel.Name = "reverseTitleLabel";
            reverseTitleLabel.Font = new Font("微软雅黑", 10, FontStyle.Bold);
            reverseTitleLabel.ForeColor = Color.FromArgb(190, 50, 50);
            reverseTitleLabel.Location = new Point(10, 92);
            reverseTitleLabel.AutoSize = true;
            analysisPanel.Controls.Add(reverseTitleLabel);

            reversePanel = new FlowLayoutPanel();
            reversePanel.Name = "reversePanel";
            reversePanel.Location = new Point(96, 88);
            reversePanel.Size = new Size(890, 34);
            reversePanel.FlowDirection = FlowDirection.LeftToRight;
            reversePanel.WrapContents = false;
            reversePanel.AutoScroll = true;
            reversePanel.BackColor = Color.Transparent;
            reversePanel.Margin = Padding.Empty;
            reversePanel.Padding = Padding.Empty;
            analysisPanel.Controls.Add(reversePanel);

            table.Location = new Point(20, 338);
            table.Size = new Size(1000, 285);

            Button btnRefresh = new Button();
            btnRefresh.Text = "刷新验证";
            btnRefresh.Size = new Size(90, 28);
            btnRefresh.Location = new Point(940, 15);
            btnRefresh.BackColor = Color.FromArgb(0, 122, 204);
            btnRefresh.ForeColor = Color.White;
            btnRefresh.FlatAppearance.BorderSize = 0;
            btnRefresh.Click += (s, e) => ValidateData();
            this.Controls.Add(btnRefresh);

            // 期数选择按钮
            Label lblPeriods = new Label();
            lblPeriods.Text = "验证期数：";
            lblPeriods.Font = new Font("微软雅黑", 10);
            lblPeriods.ForeColor = Color.FromArgb(60, 60, 80);
            lblPeriods.Location = new Point(640, 18);
            lblPeriods.AutoSize = true;
            this.Controls.Add(lblPeriods);

            int[] periods = { 50, 100, 150, 200 };
            for (int i = 0; i < periods.Length; i++)
            {
                int p = periods[i];
                Button btn = new Button();
                btn.Text = p + "期";
                btn.Size = new Size(60, 28);
                btn.Location = new Point(720 + i * 65, 15);
                btn.Font = new Font("微软雅黑", 9);
                btn.FlatAppearance.BorderSize = 0;
                btn.BackColor = p == selectedPeriods ? Color.FromArgb(0, 122, 204) : Color.FromArgb(60, 60, 80);
                btn.ForeColor = Color.White;
                int selectedP = p;
                btn.Click += (s, e) =>
                {
                    selectedPeriods = selectedP;
                    // 更新按钮样式
                    foreach (Control c in this.Controls)
                    {
                        if (c is Button b && b.Text.EndsWith("期"))
                        {
                            int bp = int.Parse(b.Text.Replace("期", ""));
                            b.BackColor = bp == selectedPeriods ? Color.FromArgb(0, 122, 204) : Color.FromArgb(60, 60, 80);
                        }
                    }
                    ValidateData();
                };
                this.Controls.Add(btn);
            }
        }

        /// <summary>
        /// 获取6个预测生肖: 自身 + 上行的2个 + 下行的2个(循环) + 同行的另一列
        /// </summary>
        private List<string> GetPredictZodiacs(string zodiac)
        {
            var result = new List<string>();
            if (!ZodiacPositions.ContainsKey(zodiac)) return result;

            var (row, col) = ZodiacPositions[zodiac];

            // 自身
            result.Add(zodiac);

            // 上行 (循环: 行0的上行是行5)
            int rowUp = row == 0 ? 5 : row - 1;
            result.Add(ZodiacTable[rowUp, 0]);
            result.Add(ZodiacTable[rowUp, 1]);

            // 下行 (循环: 行5的下行是行0)
            int rowDown = row == 5 ? 0 : row + 1;
            result.Add(ZodiacTable[rowDown, 0]);
            result.Add(ZodiacTable[rowDown, 1]);

            // 同行的另一列
            int otherCol = col == 0 ? 1 : 0;
            string other = ZodiacTable[row, otherCol];
            if (!result.Contains(other)) result.Add(other);

            return result;
        }

        private void ValidateData()
        {
            table.Rows.Clear();
            statusLabel.Text = "正在验证...";

            var records = DatabaseHelper.GetLatestHistory(int.MaxValue);
            if (records.Count < 2) { statusLabel.Text = "数据不足"; return; }
            records = records.OrderByDescending(r => int.TryParse(r.Period, out int p) ? p : 0).ToList();
            lastLoadedPeriod = records[0].Period ?? "";
            string latestYear = GetRecordYear(records[0]);

            // 从数据库构建号码→生肖映射（最新记录优先，确保用最新年份的映射）
            var numToZodiac = new Dictionary<string, string>();
            for (int ri = 0; ri < records.Count; ri++)
            {
                var r = records[ri];
                string sn = r.SpecialNumber ?? "";
                string sz = r.SpecialZodiac ?? "";
                if (!string.IsNullOrEmpty(sn) && !string.IsNullOrEmpty(sz) && !numToZodiac.ContainsKey(sn))
                    numToZodiac[sn] = sz;
            }
            if (numToZodiac.Count == 0) { statusLabel.Text = "数据库无特码生肖数据"; return; }
            // 补全 1-49 所有号码的生肖映射（用网站标准算法兜底）
            string yearPet = GetYearPet(records);
            for (int n = 1; n <= 49; n++)
            {
                string ns = n.ToString("D2");
                if (!numToZodiac.ContainsKey(ns))
                    try { numToZodiac[ns] = DataCrawler.GetShengXiaoByTeMa(ns, yearPet); } catch { }
            }

            int hits = 0, suggestedHits = 0, total = 0;
            var verifyItems = new List<RuleVerifyItem>();

            for (int i = 1; i < records.Count && total < selectedPeriods; i++)
            {
                var cur = records[i];
                var next = records[i - 1]; // 降序: records[i]是源期, records[i-1]是目标期
                if (!string.IsNullOrEmpty(latestYear) && GetRecordYear(cur) != latestYear) break;
                string actualZodiac = next.SpecialZodiac ?? "";
                if (string.IsNullOrEmpty(actualZodiac)) continue;
                

                string rawNums = cur.Numbers ?? ""; if (rawNums.Length >= 14) rawNums = rawNums.Substring(0, 12); string nums = rawNums;
                if (nums.Length < 4) continue;
                if (!int.TryParse(nums.Substring(0, 2), out int fn)) continue;
                if (!int.TryParse(nums.Substring(nums.Length - 2, 2), out int ln)) continue;

                int tailSum = (fn % 10) + (ln % 10);
                int pn = tailSum == 0 ? 49 : tailSum;

                string zodiac = numToZodiac.ContainsKey(pn.ToString("D2")) ? numToZodiac[pn.ToString("D2")] : "";
                if (string.IsNullOrEmpty(zodiac)) continue;

                // 计算6个预测生肖
                var predictList = GetPredictZodiacs(zodiac);
                string predictStr = string.Join(" ", predictList);
                bool hit = predictList.Contains(actualZodiac);
                if (hit) hits++;
                total++;
                verifyItems.Add(new RuleVerifyItem
                {
                    SourcePeriod = cur.Period,
                    TargetPeriod = next.Period,
                    SourceZodiac = zodiac,
                    ActualZodiac = actualZodiac,
                    PredictZodiacs = predictList,
                    Hit = hit,
                    TailSum = tailSum,
                    FirstNumber = fn,
                    LastNumber = ln,
                    PredictNumber = pn.ToString("D2"),
                    PredictText = predictStr
                });
            }

            for (int i = 0; i < verifyItems.Count; i++)
            {
                var item = verifyItems[i];
                var priorItems = verifyItems.Skip(i + 1).Reverse().ToList();
                var suggestion = BuildSuggestion(item.PredictZodiacs, priorItems);
                item.SuggestionMode = suggestion.mode;
                item.SuggestedZodiacs = suggestion.zodiacs;
                item.SuggestedHit = item.SuggestedZodiacs.Contains(item.ActualZodiac);
                if (item.SuggestedHit) suggestedHits++;

                table.Rows.Add(new object[]
                {
                    item.SourcePeriod,
                    item.TargetPeriod,
                    $"{item.FirstNumber}(尾{item.FirstNumber%10})",
                    $"{item.LastNumber}(尾{item.LastNumber%10})",
                    item.TailSum,
                    item.PredictNumber,
                    item.SourceZodiac,
                    item.PredictText,
                    item.SuggestionMode,
                    string.Join(" ", item.SuggestedZodiacs),
                    item.ActualZodiac,
                    item.Hit ? "命中" : "未命中",
                    item.SuggestedHit ? "命中" : "未命中"
                });
            }

            double rate = total > 0 ? (double)hits / total * 100 : 0;
            double suggestedRate = total > 0 ? (double)suggestedHits / total * 100 : 0;
            var followItems = verifyItems.Where(x => x.SuggestionMode == "跟6肖").ToList();
            var reverseItems = verifyItems.Where(x => x.SuggestionMode == "反6肖").ToList();
            string followStats = FormatModeStats(followItems);
            string reverseStats = FormatModeStats(reverseItems);
            string yearText = string.IsNullOrEmpty(latestYear) ? "" : $"{latestYear}年";
            summaryLabel.Text = $"验证结果({yearText}最新往前{selectedPeriods}期): 规律 {hits}/{total}({rate:F1}%)  建议 {suggestedHits}/{total}({suggestedRate:F1}%)";
            statusLabel.Text = $"跟6肖 {followStats}  反6肖 {reverseStats}  |  最新期号: {lastLoadedPeriod}  黄色=规律命中  蓝色=建议命中";
            var latestPredicts = ShowLatestPrediction(records, numToZodiac);
            ShowMissAnalysis(verifyItems, latestPredicts);

            foreach (DataGridViewRow row in table.Rows)
            {
                row.DefaultCellStyle.BackColor = row.Cells["结果"].Value?.ToString() == "命中"
                    ? Color.FromArgb(255, 255, 180) : Color.FromArgb(220, 190, 220);
                row.Cells["建议结果"].Style.BackColor = row.Cells["建议结果"].Value?.ToString() == "命中"
                    ? Color.FromArgb(215, 235, 255) : Color.FromArgb(255, 230, 230);
            }
        }

        private void StartAutoRefresh()
        {
            autoRefreshTimer = new System.Windows.Forms.Timer();
            autoRefreshTimer.Interval = 10000;
            autoRefreshTimer.Tick += (s, e) => RefreshIfLatestChanged();
            autoRefreshTimer.Start();
        }

        private void RefreshIfLatestChanged()
        {
            string latestPeriod = DatabaseHelper.GetLatestPeriod();
            if (!string.IsNullOrEmpty(latestPeriod) && latestPeriod != lastLoadedPeriod)
                ValidateData();
        }

        private void ShowMissAnalysis(List<RuleVerifyItem> items, List<string> latestPredicts)
        {
            if (missAnalysisLabel == null) return;
            if (items.Count == 0)
            {
                missAnalysisLabel.Text = "暂无可分析的验算记录。";
                return;
            }

            int hits = items.Count(x => x.Hit);
            int misses = items.Count - hits;
            double hitRate = (double)hits / items.Count * 100;
            string topMissZodiacs = FormatTop(items.Where(x => !x.Hit)
                .GroupBy(x => x.ActualZodiac)
                .OrderByDescending(g => g.Count())
                .Take(4)
                .Select(g => $"{g.Key}{g.Count()}"));

            string missZones = FormatTop(items.Where(x => !x.Hit)
                .GroupBy(x => GetRelativeZone(x.SourceZodiac, x.ActualZodiac))
                .OrderByDescending(g => g.Count())
                .Take(3)
                .Select(g => $"{g.Key}{g.Count()}"));

            string strongSources = FormatSourceRates(items, true);
            string weakSources = FormatSourceRates(items, false);

            var afterMissItems = new List<RuleVerifyItem>();
            for (int i = 1; i < items.Count; i++)
            {
                if (!items[i].Hit)
                    afterMissItems.Add(items[i - 1]);
            }
            int afterMissHits = afterMissItems.Count(x => x.Hit);
            double afterMissRate = afterMissItems.Count > 0 ? (double)afterMissHits / afterMissItems.Count * 100 : 0;

            int latestMissStreak = 0;
            foreach (var item in items)
            {
                if (item.Hit) break;
                latestMissStreak++;
            }
            int suggestedHits = items.Count(x => x.SuggestedHit);
            double suggestedRate = (double)suggestedHits / items.Count * 100;
            var interval = WilsonInterval(suggestedHits, items.Count);
            string windowTrend = BuildWindowTrend(items);
            string strategyComparison = BuildStrategyComparison(items);
            string advice = BuildIntelligentAdvice(
                suggestedRate, interval.low, interval.high, latestMissStreak, latestPredicts);
            missAnalysisLabel.Text =
                $"基准判断：建议{suggestedHits}/{items.Count}({suggestedRate:F1}%)，随机六码基准50.0%，95%区间{interval.low:F1}%～{interval.high:F1}% | {advice}\r\n" +
                $"多窗口：{windowTrend}\r\n" +
                $"策略比较：{strategyComparison} | 连错后回补{afterMissHits}/{afterMissItems.Count}({afterMissRate:F1}%)，当前连错{latestMissStreak}期\r\n" +
                $"错因画像：未中{misses}期，多落{topMissZodiacs}，位置{missZones} | 源肖强{strongSources}，源肖弱{weakSources}";
            RenderReverseZodiacs(latestPredicts);
        }

        private void RenderReverseZodiacs(List<string> latestPredicts)
        {
            if (reversePanel == null) return;
            reversePanel.SuspendLayout();
            reversePanel.Controls.Clear();

            foreach (string zodiac in GetReverseZodiacs(latestPredicts))
            {
                Label chip = new Label();
                chip.AutoSize = true;
                chip.Font = new Font("微软雅黑", 10, FontStyle.Bold);
                chip.ForeColor = GetZodiacColor(zodiac);
                chip.Margin = new Padding(0, 2, 10, 0);
                chip.Text = zodiac;
                reversePanel.Controls.Add(chip);
            }

            reversePanel.ResumeLayout();
        }

        private static Color GetZodiacColor(string zodiac) => zodiac switch
        {
            "鼠" => Color.FromArgb(0, 128, 0),
            "牛" => Color.FromArgb(0, 102, 204),
            "虎" => Color.FromArgb(204, 102, 0),
            "兔" => Color.FromArgb(153, 51, 153),
            "龙" => Color.FromArgb(220, 20, 60),
            "蛇" => Color.FromArgb(255, 140, 0),
            "马" => Color.FromArgb(0, 139, 139),
            "羊" => Color.FromArgb(128, 0, 128),
            "猴" => Color.FromArgb(46, 139, 87),
            "鸡" => Color.FromArgb(178, 34, 34),
            "狗" => Color.FromArgb(70, 130, 180),
            "猪" => Color.FromArgb(139, 69, 19),
            _ => Color.FromArgb(190, 50, 50)
        };

        private (double low, double high) WilsonInterval(int hits, int total)
        {
            if (total <= 0) return (0, 0);
            const double z = 1.96;
            double p = (double)hits / total;
            double denominator = 1 + z * z / total;
            double center = (p + z * z / (2 * total)) / denominator;
            double margin = z * Math.Sqrt((p * (1 - p) + z * z / (4 * total)) / total) / denominator;
            return (Math.Max(0, (center - margin) * 100), Math.Min(100, (center + margin) * 100));
        }

        private string BuildWindowTrend(List<RuleVerifyItem> items)
        {
            int[] windows = { 50, 100, 150, 200 };
            var parts = new List<string>();
            foreach (int window in windows.Where(window => window <= items.Count || window == windows[0]))
            {
                var sample = items.Take(Math.Min(window, items.Count)).ToList();
                if (sample.Count == 0) continue;
                double ruleRate = (double)sample.Count(x => x.Hit) / sample.Count * 100;
                double adviceRate = (double)sample.Count(x => x.SuggestedHit) / sample.Count * 100;
                parts.Add($"{sample.Count}期 规律{ruleRate:F1}%/建议{adviceRate:F1}%");
            }

            if (parts.Count == 0) return "样本不足";
            int recentCount = Math.Min(50, items.Count);
            double recent = (double)items.Take(recentCount).Count(x => x.SuggestedHit) / recentCount * 100;
            double previous = items.Count >= recentCount * 2
                ? (double)items.Skip(recentCount).Take(recentCount).Count(x => x.SuggestedHit) / recentCount * 100
                : recent;
            string direction = recent >= previous + 5 ? "短期转强"
                : recent <= previous - 5 ? "短期转弱"
                : "短期平稳";
            return string.Join("；", parts) + $"（{direction}）";
        }

        private string BuildStrategyComparison(List<RuleVerifyItem> items)
        {
            var follow = items.Where(x => x.SuggestionMode == "跟6肖").ToList();
            var reverse = items.Where(x => x.SuggestionMode == "反6肖").ToList();
            double followRate = follow.Count > 0 ? (double)follow.Count(x => x.SuggestedHit) / follow.Count * 100 : 0;
            double reverseRate = reverse.Count > 0 ? (double)reverse.Count(x => x.SuggestedHit) / reverse.Count * 100 : 0;
            double gap = Math.Abs(followRate - reverseRate);
            string leader = followRate > reverseRate ? "跟6肖较优" : reverseRate > followRate ? "反6肖较优" : "两者持平";
            string strength = gap >= 10 && Math.Min(follow.Count, reverse.Count) >= 20 ? "差异明显"
                : Math.Min(follow.Count, reverse.Count) < 20 ? "分组样本不足"
                : "差异有限";
            return $"跟6肖{follow.Count}次/{followRate:F1}%，反6肖{reverse.Count}次/{reverseRate:F1}%，{leader}但{strength}";
        }

        private string BuildIntelligentAdvice(double rate, double lowerBound, double upperBound,
            int latestMissStreak, List<string> latestPredicts)
        {
            string reverse = string.Join(" ", GetReverseZodiacs(latestPredicts));
            if (lowerBound > 50 && rate >= 55)
                return $"信号等级：中等偏强；表现高于随机基准，可参考当前动态策略，反区为{reverse}";
            if (upperBound < 50)
                return $"信号等级：明显偏弱；整体低于随机基准，不应机械跟随，反区为{reverse}";
            if (latestMissStreak >= 2)
                return $"信号等级：弱；统计区间仍覆盖随机基准且已连错，不追连败，反区仅作观察{reverse}";
            return $"信号等级：弱；尚未证明优于随机六码，当前结论仅作观察，反区为{reverse}";
        }

        private string FormatTop(IEnumerable<string> parts)
        {
            string text = string.Join("、", parts.Where(x => !string.IsNullOrEmpty(x)));
            return string.IsNullOrEmpty(text) ? "无" : text;
        }

        private string FormatSourceRates(List<RuleVerifyItem> items, bool strong)
        {
            var rates = items.GroupBy(x => x.SourceZodiac)
                .Select(g => new
                {
                    Zodiac = g.Key,
                    Total = g.Count(),
                    Hits = g.Count(x => x.Hit),
                    Rate = (double)g.Count(x => x.Hit) / g.Count() * 100
                })
                .Where(x => x.Total >= 5)
                .OrderBy(x => strong ? -x.Rate : x.Rate)
                .ThenByDescending(x => x.Total)
                .Take(3)
                .Select(x => $"{x.Zodiac}{x.Rate:F0}%");
            return FormatTop(rates);
        }

        private string GetRelativeZone(string sourceZodiac, string actualZodiac)
        {
            if (!ZodiacPositions.ContainsKey(sourceZodiac) || !ZodiacPositions.ContainsKey(actualZodiac))
                return "未知";

            var source = ZodiacPositions[sourceZodiac];
            var actual = ZodiacPositions[actualZodiac];
            int rowDiff = (actual.row - source.row + 6) % 6;
            bool sameCol = actual.col == source.col;

            if (rowDiff == 0) return sameCol ? "本位" : "同行";
            if (rowDiff == 1) return "下行";
            if (rowDiff == 5) return "上行";
            if (rowDiff == 2) return "下隔行";
            if (rowDiff == 4) return "上隔行";
            return sameCol ? "对行同列" : "对行异列";
        }

        private string BuildAdvice(double hitRate, double afterMissRate, int latestMissStreak, List<string> latestPredicts)
        {
            string reverse = string.Join(" ", GetReverseZodiacs(latestPredicts));
            if (latestMissStreak >= 2 && afterMissRate >= hitRate + 5)
                return $"建议: 连错后有回补倾向，优先跟当前6肖，反区(除当前6肖外) {reverse}";
            if (hitRate < 45 || latestMissStreak >= 2)
                return $"建议: 当前6肖偏弱，重点参考反区(除当前6肖外) {reverse}";
            if (latestMissStreak == 0)
                return $"建议: 刚命中，下一期谨慎追，反区(除当前6肖外) {reverse}";
            return $"建议: 跟当前6肖为主，反区(除当前6肖外) {reverse}";
        }

        private string FormatModeStats(List<RuleVerifyItem> items)
        {
            if (items.Count == 0) return "0/0(0.0%)";
            int hits = items.Count(x => x.SuggestedHit);
            return $"{hits}/{items.Count}({(double)hits / items.Count * 100:F1}%)";
        }

        private (string mode, List<string> zodiacs) BuildSuggestion(List<string> predictList, List<RuleVerifyItem> priorItems)
        {
            if (predictList.Count == 0) return ("跟6肖", new List<string>());
            if (priorItems.Count < 20) return ("跟6肖", predictList);

            int hits = priorItems.Count(x => x.Hit);
            double hitRate = (double)hits / priorItems.Count * 100;

            var afterMissItems = new List<RuleVerifyItem>();
            for (int i = 1; i < priorItems.Count; i++)
            {
                if (!priorItems[i - 1].Hit)
                    afterMissItems.Add(priorItems[i]);
            }
            int afterMissHits = afterMissItems.Count(x => x.Hit);
            double afterMissRate = afterMissItems.Count > 0 ? (double)afterMissHits / afterMissItems.Count * 100 : 0;

            int missStreak = 0;
            for (int i = priorItems.Count - 1; i >= 0; i--)
            {
                if (priorItems[i].Hit) break;
                missStreak++;
            }

            if (missStreak >= 2 && afterMissRate >= hitRate + 5)
                return ("跟6肖", predictList);
            if (hitRate < 45 || missStreak >= 2)
                return ("反6肖", GetReverseZodiacs(predictList));
            if (missStreak == 0)
                return ("反6肖", GetReverseZodiacs(predictList));
            return ("跟6肖", predictList);
        }

        private List<string> GetReverseZodiacs(List<string> predictZodiacs)
        {
            var all = ZodiacPositions.Keys.ToList();
            return all.Where(z => !predictZodiacs.Contains(z)).ToList();
        }

        private List<string> ShowLatestPrediction(List<DatabaseHelper.HistoryRecord> records, Dictionary<string, string> numToZodiac)
        {
            var preview = this.Controls.Find("predictPreview", true).FirstOrDefault() as Label;
            var panel = (Panel)preview?.Parent;
            if (preview == null || records.Count < 2) return new List<string>();

            var latest = records[0]; // 最新一期（降序排列）
            string nums = latest.Numbers ?? "";
            // 只取前12位（前6个平码）
            if (nums.Length >= 14) nums = nums.Substring(0, 12);
            if (nums.Length < 4) return new List<string>();

            if (!int.TryParse(nums.Substring(0, 2), out int fn)) return new List<string>();
            if (!int.TryParse(nums.Substring(nums.Length - 2, 2), out int ln)) return new List<string>();

            int tailSum = (fn % 10) + (ln % 10);
            int pn = tailSum == 0 ? 49 : tailSum;
            string zodiac = numToZodiac.ContainsKey(pn.ToString("D2")) ? numToZodiac[pn.ToString("D2")] : "";
            if (string.IsNullOrEmpty(zodiac)) return new List<string>();

            var predicts = GetPredictZodiacs(zodiac);
            int nextPeriod = int.TryParse(latest.Period, out int lp) ? lp + 1 : 0;

            preview.Text = $"⭐ 最新预测: 期号{latest.Period} 首位{fn}(尾{fn%10}) + 末位{ln}(尾{ln%10}) = {tailSum} → {pn:D2}→{zodiac} → 下期({nextPeriod})预测6肖: {string.Join(" ", predicts)}";
            panel.BackColor = Color.FromArgb(255, 248, 220);

            // 自动保存预测记录（不删除旧记录）
            return predicts;
        }

        private string GetYearPet(List<DatabaseHelper.HistoryRecord> records)
        {
            try
            {
                string dateStr = records.LastOrDefault()?.OpenTime;
                if (string.IsNullOrEmpty(dateStr)) dateStr = records.LastOrDefault()?.Date;
                if (!string.IsNullOrEmpty(dateStr) && dateStr.Length >= 4)
                {
                    int year = int.Parse(dateStr.Substring(0, 4));
                    string[] z = { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };
                    int idx = (year - 2020) % 12;
                    if (idx < 0) idx += 12;
                    return z[idx];
                }
            }
            catch { }
            return "马";
        }

        private string GetRecordYear(DatabaseHelper.HistoryRecord record)
        {
            string dateStr = record.OpenTime;
            if (string.IsNullOrEmpty(dateStr)) dateStr = record.Date;
            if (!string.IsNullOrEmpty(dateStr) && dateStr.Length >= 4)
                return dateStr.Substring(0, 4);
            if (!string.IsNullOrEmpty(record.Period) && record.Period.Length >= 4)
                return record.Period.Substring(0, 4);
            return "";
        }

        private class RuleVerifyItem
        {
            public string SourcePeriod { get; set; } = "";
            public string TargetPeriod { get; set; } = "";
            public string SourceZodiac { get; set; } = "";
            public string ActualZodiac { get; set; } = "";
            public List<string> PredictZodiacs { get; set; } = new List<string>();
            public string SuggestionMode { get; set; } = "";
            public List<string> SuggestedZodiacs { get; set; } = new List<string>();
            public bool Hit { get; set; }
            public bool SuggestedHit { get; set; }
            public int TailSum { get; set; }
            public int FirstNumber { get; set; }
            public int LastNumber { get; set; }
            public string PredictNumber { get; set; } = "";
            public string PredictText { get; set; } = "";
        }
    }
}
