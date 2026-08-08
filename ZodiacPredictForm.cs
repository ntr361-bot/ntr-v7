using System;
using System.Drawing;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace 六合分析软件
{
    /// <summary>
    /// AI特码生肖预测窗体 V3.0
    /// </summary>
    public partial class ZodiacPredictForm : Form
    {
        Panel topBar;
        ComboBox cboPeriods;
        Button btnPredict;
        Button btnBacktest;
        Panel scrollPanel;
        Panel resultPanel;
        Panel backtestPanel;
        AIEngine.PredictResult? lastResult;
        Dictionary<int, AIEngine.PredictResult> lastPeriodResults = new();

        public ZodiacPredictForm()
        {
            InitializeComponent();

            this.Text = AISettings.ModelVersion;
            this.Size = new Size(1100, 760);
            this.StartPosition = FormStartPosition.CenterParent;
            this.MinimumSize = new Size(1000, 700);
            this.FormBorderStyle = FormBorderStyle.Sizable;
            this.MinimizeBox = true;
            this.MaximizeBox = true;

            InitUI();
        }

        private void InitUI()
        {
            TableLayoutPanel rootLayout = new TableLayoutPanel();
            rootLayout.Dock = DockStyle.Fill;
            rootLayout.ColumnCount = 1;
            rootLayout.RowCount = 2;
            rootLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 110));
            rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            this.Controls.Add(rootLayout);

            topBar = new Panel();
            topBar.Dock = DockStyle.Fill;
            topBar.Margin = Padding.Empty;
            topBar.BackColor = Color.FromArgb(30, 30, 46);
            rootLayout.Controls.Add(topBar, 0, 0);

            Label title = new Label();
            title.Text = "🎯 " + AISettings.ModelVersion;
            title.Font = new Font("微软雅黑", 18, FontStyle.Bold);
            title.ForeColor = Color.White;
            title.Location = new Point(20, 15);
            title.AutoSize = true;
            topBar.Controls.Add(title);

            Label lblPeriods = new Label();
            lblPeriods.Text = "分析期数：";
            lblPeriods.Font = new Font("微软雅黑", 11);
            lblPeriods.ForeColor = Color.White;
            lblPeriods.Location = new Point(350, 22);
            lblPeriods.AutoSize = true;
            topBar.Controls.Add(lblPeriods);

            cboPeriods = new ComboBox();
            cboPeriods.Font = new Font("微软雅黑", 11);
            cboPeriods.Location = new Point(450, 18);
            cboPeriods.Size = new Size(140, 30);
            cboPeriods.DropDownStyle = ComboBoxStyle.DropDownList;
            cboPeriods.Items.AddRange(new string[] { "50期", "100期", "全部历史" });
            cboPeriods.SelectedIndex = 2;
            topBar.Controls.Add(cboPeriods);

            btnPredict = new Button();
            btnPredict.Text = "🔮 开始预测";
            btnPredict.Font = new Font("微软雅黑", 11, FontStyle.Bold);
            btnPredict.Size = new Size(130, 40);
            btnPredict.Location = new Point(610, 15);
            btnPredict.BackColor = Color.FromArgb(0, 122, 204);
            btnPredict.ForeColor = Color.White;
            btnPredict.FlatAppearance.BorderSize = 0;
            btnPredict.Click += BtnPredict_Click;
            topBar.Controls.Add(btnPredict);

            btnBacktest = new Button();
            btnBacktest.Text = "📊 回测验证";
            btnBacktest.Font = new Font("微软雅黑", 11, FontStyle.Bold);
            btnBacktest.Size = new Size(130, 40);
            btnBacktest.Location = new Point(750, 15);
            btnBacktest.BackColor = Color.FromArgb(46, 139, 87);
            btnBacktest.ForeColor = Color.White;
            btnBacktest.FlatAppearance.BorderSize = 0;
            btnBacktest.Click += BtnBacktest_Click;
            topBar.Controls.Add(btnBacktest);

            Button btnCompare = CreateToolButton("多周期对比", 350, 65, Color.FromArgb(110, 80, 180));
            btnCompare.Click += (s, e) => ShowMultiPeriodComparison();
            topBar.Controls.Add(btnCompare);

            Button btnExport = CreateToolButton("导出报告", 490, 65, Color.FromArgb(180, 110, 30));
            btnExport.Click += (s, e) => ExportCurrentReport();
            topBar.Controls.Add(btnExport);

            Button btnHelp = CreateToolButton("统计口径", 630, 65, Color.FromArgb(90, 100, 110));
            btnHelp.Click += (s, e) => MessageBox.Show(
                "本模块只使用真实开奖记录中的特码生肖（SpecialZodiac）。\n\n" +
                "50/100期表示从最新一期向前取对应数量的有效记录，6个平码不参与预测、趋势或验证。\n" +
                "预测会按目标期号和分析周期自动留档，开奖后用实际特码生肖验证。",
                "统计口径", MessageBoxButtons.OK, MessageBoxIcon.Information);
            topBar.Controls.Add(btnHelp);

            FlowLayoutPanel verticalContent = new FlowLayoutPanel();
            scrollPanel = verticalContent;
            scrollPanel.Dock = DockStyle.Fill;
            scrollPanel.AutoScroll = true;
            scrollPanel.Padding = new Padding(20);
            scrollPanel.Margin = Padding.Empty;
            verticalContent.FlowDirection = FlowDirection.TopDown;
            verticalContent.WrapContents = false;
            rootLayout.Controls.Add(scrollPanel, 0, 1);

            resultPanel = new Panel();
            resultPanel.Margin = Padding.Empty;
            resultPanel.Height = 350;
            resultPanel.Padding = new Padding(10);
            scrollPanel.Controls.Add(resultPanel);

            backtestPanel = new Panel();
            backtestPanel.Margin = Padding.Empty;
            backtestPanel.Height = 280;
            backtestPanel.Padding = new Padding(10);
            backtestPanel.Visible = false;
            scrollPanel.Controls.Add(backtestPanel);

            void ResizeContentPanels()
            {
                int width = Math.Max(900, verticalContent.ClientSize.Width - verticalContent.Padding.Horizontal - 22);
                resultPanel.Width = width;
                backtestPanel.Width = width;
            }
            verticalContent.Resize += (s, e) => ResizeContentPanels();
            ResizeContentPanels();

            ShowWelcome(resultPanel);
            cboPeriods.SelectedIndexChanged += CboPeriods_SelectedIndexChanged;
        }

        private void ShowWelcome(Panel panel)
        {
            panel.Controls.Clear();
            Label welcome = new Label();
            welcome.Text = "选择要查看的分析期数，点击「开始预测」会同步刷新全部周期";
            welcome.Font = new Font("微软雅黑", 13);
            welcome.ForeColor = Color.Gray;
            welcome.Location = new Point(30, 30);
            welcome.AutoSize = true;
            panel.Controls.Add(welcome);

            Label hint = new Label();
            hint.Text = "💡 每次会刷新50期、100期和全部历史，并分别保存到预测历史记录\n当前下拉框只决定页面显示哪一套结果；预测只使用真实特码生肖，不使用平码数据";
            hint.Font = new Font("微软雅黑", 10);
            hint.ForeColor = Color.Gray;
            hint.Location = new Point(30, 70);
            hint.AutoSize = true;
            panel.Controls.Add(hint);
        }

        private int GetPeriodCount()
        {
            switch (cboPeriods.SelectedIndex)
            {
                case 0: return 50;
                case 1: return 100;
                case 2: return AISettings.AllHistoryModeValue;
                default: return AISettings.AllHistoryModeValue;
            }
        }

        private void CboPeriods_SelectedIndexChanged(object? sender, EventArgs e)
        {
            if (!lastPeriodResults.TryGetValue(GetPeriodCount(), out var result)) return;

            lastResult = result;
            RenderPredictResult(result);
        }

        private async void BtnPredict_Click(object sender, EventArgs e)
        {
            int selectedPeriod = GetPeriodCount();
            btnPredict.Enabled = false;
            cboPeriods.Enabled = false;
            btnPredict.Text = "⏳ 刷新全部...";

            try
            {
                var results = await Task.Run(AIEngine.RefreshAllPeriodPredictions);
                lastPeriodResults = results;
                lastResult = results[selectedPeriod];
                RenderPredictResult(lastResult);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"刷新全部周期预测失败：{ex.Message}", "预测失败",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                btnPredict.Enabled = true;
                cboPeriods.Enabled = true;
                btnPredict.Text = "🔮 开始预测";
            }
        }

        private void RenderPredictResult(AIEngine.PredictResult result)
        {
            scrollPanel.AutoScrollPosition = Point.Empty;
            resultPanel.Controls.Clear();

            Label infoLabel = new Label();
            infoLabel.Text = $"📋 分析期数：{result.AnalysisPeriods} 期  |  预测时间：{result.PredictTime:yyyy-MM-dd HH:mm:ss}  |  可信度：{result.Confidence}  |  最佳模型：{result.BestModel}";
            infoLabel.Font = new Font("微软雅黑", 11);
            infoLabel.ForeColor = Color.FromArgb(80, 80, 100);
            infoLabel.Location = new Point(20, 10);
            infoLabel.AutoSize = true;
            infoLabel.MaximumSize = new Size(800, 0);
            resultPanel.Controls.Add(infoLabel);

            var quality = DataCheckService.CheckSelection(GetPeriodCount());
            Label qualityLabel = new Label();
            qualityLabel.Text = (quality.IsComplete ? "✅ " : "⚠ ") + quality.Summary + $"  |  最新：{quality.LatestPeriod}期";
            qualityLabel.Font = new Font("微软雅黑", 10, FontStyle.Bold);
            qualityLabel.ForeColor = quality.IsComplete ? Color.FromArgb(0, 130, 70) : Color.FromArgb(210, 100, 0);
            qualityLabel.Location = new Point(20, 60);
            qualityLabel.AutoSize = true;
            resultPanel.Controls.Add(qualityLabel);

            Label numberLabel = new Label();
            numberLabel.Text = result.RecommendedNumbers.Count > 0
                ? "🎯 模型重点号码：" + string.Join("  ", result.RecommendedNumbers.Select(n => n.ToString("D2")))
                : "🎯 模型重点号码：数据不足，暂未生成";
            numberLabel.Font = new Font("微软雅黑", 11, FontStyle.Bold);
            numberLabel.ForeColor = Color.FromArgb(180, 70, 20);
            numberLabel.Location = new Point(20, 86);
            numberLabel.AutoSize = true;
            resultPanel.Controls.Add(numberLabel);

            // 模型参数显示
            Label modelLabel = new Label();
            modelLabel.Text = $"🤖 当前模型：{AISettings.ModelVersion}  |  训练数据：{result.AnalysisPeriods}期  |  评分方式：{AISettings.AnalysisMethod}";
            modelLabel.Font = new Font("微软雅黑", 10);
            modelLabel.ForeColor = Color.FromArgb(100, 100, 120);
            modelLabel.Location = new Point(20, 35);
            modelLabel.AutoSize = true;
            modelLabel.MaximumSize = new Size(800, 0);
            resultPanel.Controls.Add(modelLabel);

            int y = 120;

            // 第一梯队
            Label tier1Title = new Label();
            tier1Title.Text = "🏆 第一梯队（重点预测）";
            tier1Title.Font = new Font("微软雅黑", 14, FontStyle.Bold);
            tier1Title.ForeColor = Color.FromArgb(200, 50, 50);
            tier1Title.Location = new Point(20, y);
            tier1Title.AutoSize = true;
            resultPanel.Controls.Add(tier1Title);

            y += 35;
            var sorted = result.AllScores.OrderByDescending(s => s.TotalScore).ToList();
            for (int i = 0; i < 3 && i < sorted.Count; i++)
            {
                Panel card = CreateZodiacCard(sorted[i].Zodiac, $"{sorted[i].TotalScore:F0}分", i + 1,
                    Color.FromArgb(255, 235, 235), Color.FromArgb(200, 50, 50));
                card.Location = new Point(20 + i * 180, y);
                resultPanel.Controls.Add(card);
            }

            y += 90;

            // 第二梯队
            Label tier2Title = new Label();
            tier2Title.Text = "前6生肖（第4至6名）";
            tier2Title.Font = new Font("微软雅黑", 14, FontStyle.Bold);
            tier2Title.ForeColor = Color.FromArgb(200, 50, 50);
            tier2Title.Location = new Point(20, y);
            tier2Title.AutoSize = true;
            resultPanel.Controls.Add(tier2Title);

            y += 35;
            for (int i = 3; i < 6 && i < sorted.Count; i++)
            {
                Panel card = CreateZodiacCard(sorted[i].Zodiac, $"{sorted[i].TotalScore:F0}分", i + 1,
                    Color.FromArgb(255, 235, 235), Color.FromArgb(200, 50, 50));
                card.Location = new Point(20 + (i - 3) * 180, y);
                resultPanel.Controls.Add(card);
            }

            y += 90;

            // 淘汰
            Label elimLabel = new Label();
            elimLabel.Text = "❌ 淘汰：";
            elimLabel.Font = new Font("微软雅黑", 12, FontStyle.Bold);
            elimLabel.ForeColor = Color.Gray;
            elimLabel.Location = new Point(20, y);
            elimLabel.AutoSize = true;
            resultPanel.Controls.Add(elimLabel);
            y += 25;

            string elimText = string.Join("  ", result.Bottom3.Select(z => $"[{z}]"));
            Label elimValue = new Label();
            elimValue.Text = elimText;
            elimValue.Font = new Font("微软雅黑", 11);
            elimValue.ForeColor = Color.Gray;
            elimValue.Location = new Point(100, y - 22);
            elimValue.AutoSize = true;
            resultPanel.Controls.Add(elimValue);
            y += 30;

            // 完整评分表
            Label tableTitle = new Label();
            tableTitle.Text = "📊 完整评分表";
            tableTitle.Font = new Font("微软雅黑", 12, FontStyle.Bold);
            tableTitle.ForeColor = Color.FromArgb(80, 80, 100);
            tableTitle.Location = new Point(20, y);
            tableTitle.AutoSize = true;
            resultPanel.Controls.Add(tableTitle);
            y += 25;

            DataGridView scoreGrid = new DataGridView();
            scoreGrid.Location = new Point(20, y);
            scoreGrid.Size = new Size(Math.Max(760, resultPanel.ClientSize.Width - 40), 315);
            scoreGrid.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            scoreGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            scoreGrid.AllowUserToAddRows = false;
            scoreGrid.AllowUserToDeleteRows = false;
            scoreGrid.AllowUserToResizeRows = false;
            scoreGrid.ReadOnly = true;
            scoreGrid.RowHeadersVisible = false;
            scoreGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            scoreGrid.BackgroundColor = Color.White;
            scoreGrid.BorderStyle = BorderStyle.FixedSingle;
            scoreGrid.ColumnHeadersHeight = 32;
            scoreGrid.RowTemplate.Height = 23;
            scoreGrid.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            scoreGrid.Columns.Add("Zodiac", "生肖");
            scoreGrid.Columns.Add("Total", "综合分");
            scoreGrid.Columns.Add("Frequency", "频率分");
            scoreGrid.Columns.Add("Trend", "走势分");
            scoreGrid.Columns.Add("Omission", "遗漏分");
            scoreGrid.Columns.Add("HotCold", "冷热分");
            scoreGrid.Columns.Add("Period", "周期分");
            scoreGrid.Columns.Add("Relation", "关联分");
            scoreGrid.Columns.Add("Count", "次数");
            scoreGrid.CellDoubleClick += (s, e) =>
            {
                if (e.RowIndex < 0 || e.RowIndex >= sorted.Count) return;
                ShowZodiacExplanation(sorted[e.RowIndex], result.AnalysisPeriods);
            };

            foreach (var s in sorted)
            {
                scoreGrid.Rows.Add(s.Zodiac, $"{s.TotalScore:F1}", $"{s.FrequencyScore:F1}",
                    $"{s.RecentTrendScore:F1}", $"{s.OmissionScore:F1}", $"{s.HotColdScore:F1}",
                    $"{s.PeriodPatternScore:F1}", $"{s.ConsecutiveScore:F1}", s.TotalAppear);
            }

            resultPanel.Controls.Add(scoreGrid);
            y += scoreGrid.Height;

            resultPanel.Height = y + 30;
        }

        private Panel CreateZodiacCard(string zodiac, string score, int rank, Color bgColor, Color borderColor)
        {
            Panel card = new Panel();
            card.Size = new Size(150, 80);
            card.BackColor = bgColor;
            card.BorderStyle = BorderStyle.FixedSingle;

            Label rankLabel = new Label();
            rankLabel.Text = $"第{rank}名";
            rankLabel.Font = new Font("微软雅黑", 9);
            rankLabel.ForeColor = Color.Gray;
            rankLabel.Location = new Point(10, 8);
            rankLabel.AutoSize = true;
            card.Controls.Add(rankLabel);

            Label zodiacLabel = new Label();
            zodiacLabel.Text = zodiac;
            zodiacLabel.Font = new Font("微软雅黑", 28, FontStyle.Bold);
            zodiacLabel.ForeColor = borderColor;
            zodiacLabel.Location = new Point(10, 30);
            zodiacLabel.AutoSize = true;
            card.Controls.Add(zodiacLabel);

            if (!string.IsNullOrEmpty(score))
            {
                Label scoreLabel = new Label();
                scoreLabel.Text = score;
                scoreLabel.Font = new Font("微软雅黑", 11);
                scoreLabel.ForeColor = Color.FromArgb(100, 100, 100);
                scoreLabel.Location = new Point(80, 50);
                scoreLabel.AutoSize = true;
                card.Controls.Add(scoreLabel);
            }

            return card;
        }

        private Button CreateToolButton(string text, int x, int y, Color color)
        {
            return new Button
            {
                Text = text,
                Font = new Font("微软雅黑", 10, FontStyle.Bold),
                Size = new Size(125, 32),
                Location = new Point(x, y),
                BackColor = color,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
        }

        private void ShowZodiacExplanation(ZodiacPredictEngineV2.ZodiacScoreV2 score, int periods)
        {
            var history = DatabaseHelper.GetLatestHistory(periods)
                .Where(h => !string.IsNullOrEmpty(h.SpecialZodiac)).Select(h => h.SpecialZodiac).ToList();
            int recent10 = history.Take(10).Count(z => z == score.Zodiac);
            int previous10 = history.Skip(10).Take(10).Count(z => z == score.Zodiac);
            int missing = history.TakeWhile(z => z != score.Zodiac).Count();
            string reason = $"生肖：{score.Zodiac}\n分析范围：最近{periods}期真实特码生肖\n\n" +
                $"近期10期：{recent10}次\n前10期：{previous10}次\n当前遗漏：{missing}期\n总出现：{score.TotalAppear}次\n\n" +
                $"综合分：{score.TotalScore:F1}\n频率分：{score.FrequencyScore:F1}\n走势分：{score.RecentTrendScore:F1}\n" +
                $"遗漏分：{score.OmissionScore:F1}\n冷热分：{score.HotColdScore:F1}\n周期分：{score.PeriodPatternScore:F1}\n关联分：{score.ConsecutiveScore:F1}";
            MessageBox.Show(reason, $"{score.Zodiac} - 预测理由", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void ShowMultiPeriodComparison()
        {
            int[] periods = { 50, 100, AISettings.AllHistoryModeValue };
            var results = periods.ToDictionary(p => p, p => AIEngine.GenerateForAutomation(p));
            Form dialog = new Form { Text = "50/100/全部历史多周期预测对比", Size = new Size(1000, 570), StartPosition = FormStartPosition.CenterParent };
            DataGridView grid = new DataGridView
            {
                Dock = DockStyle.Fill, ReadOnly = true, RowHeadersVisible = false,
                AllowUserToAddRows = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                BackgroundColor = Color.White
            };
            grid.Columns.Add("Zodiac", "生肖");
            foreach (int p in periods)
            {
                string label = AISettings.GetPeriodLabel(p);
                grid.Columns.Add($"R{p}", $"{label}排名");
                grid.Columns.Add($"S{p}", $"{label}得分");
            }
            grid.Columns.Add("Conclusion", "综合结论");
            string[] zodiacs = { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };
            foreach (string z in zodiacs)
            {
                var values = new List<object> { z };
                var ranks = new List<int>();
                foreach (int p in periods)
                {
                    var sorted = results[p].AllScores.OrderByDescending(s => s.TotalScore).ToList();
                    int rank = sorted.FindIndex(s => s.Zodiac == z) + 1;
                    ranks.Add(rank);
                    values.Add(rank); values.Add($"{sorted[rank - 1].TotalScore:F1}");
                }
                string conclusion = ranks.All(r => r <= 3) ? "三周期共同关注"
                    : ranks.Count(r => r <= 6) >= 2 ? "多周期关注"
                    : ranks[0] <= 3 && ranks[2] > 6 ? "短期升温" : "一般观察";
                values.Add(conclusion);
                grid.Rows.Add(values.ToArray());
            }
            dialog.Controls.Add(grid);
            dialog.ShowDialog(this);
        }

        private void ExportCurrentReport()
        {
            if (lastResult == null)
            {
                MessageBox.Show("请先执行一次预测。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using SaveFileDialog dialog = new SaveFileDialog
            {
                Filter = "CSV文件（Excel可打开）|*.csv|文本报告|*.txt",
                FileName = $"特码生肖预测_{lastResult.PredictPeriod}_{lastResult.AnalysisPeriods}期.csv"
            };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            var sb = new StringBuilder();
            sb.AppendLine("目标期号,分析期数,预测时间,前3生肖,前6生肖,重点号码,数据口径");
            sb.AppendLine($"{lastResult.PredictPeriod},{lastResult.AnalysisPeriods},{lastResult.PredictTime:yyyy-MM-dd HH:mm:ss},\"{string.Join(',', lastResult.Top3)}\",\"{string.Join(',', lastResult.Top6)}\",\"{string.Join(',', lastResult.RecommendedNumbers.Select(n => n.ToString("D2")))}\",仅真实特码生肖");
            sb.AppendLine();
            sb.AppendLine("生肖,综合分,频率分,走势分,遗漏分,冷热分,周期分,关联分,次数");
            foreach (var s in lastResult.AllScores.OrderByDescending(s => s.TotalScore))
                sb.AppendLine($"{s.Zodiac},{s.TotalScore:F1},{s.FrequencyScore:F1},{s.RecentTrendScore:F1},{s.OmissionScore:F1},{s.HotColdScore:F1},{s.PeriodPatternScore:F1},{s.ConsecutiveScore:F1},{s.TotalAppear}");
            File.WriteAllText(dialog.FileName, sb.ToString(), new UTF8Encoding(true));
            MessageBox.Show("报告已导出。", "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void BtnBacktest_Click(object sender, EventArgs e)
        {
            btnBacktest.Enabled = false;
            btnBacktest.Text = "⏳ 回测中...";
            Application.DoEvents();

            try
            {
                RenderMultiPeriodBacktest();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"回测失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                btnBacktest.Enabled = true;
                btnBacktest.Text = "📊 回测验证";
            }
        }

        private void RenderMultiPeriodBacktest()
        {
            backtestPanel.Controls.Clear();
            int[] periods = { 50, 100 };
            var reports = periods.Select(p => AIBacktestV2.Run(trainPeriods: p, testCount: 30)).ToList();

            Label title = new Label
            {
                Text = "📊 50/100期回测对比（训练区间与验证区间严格分离）",
                Font = new Font("微软雅黑", 13, FontStyle.Bold),
                ForeColor = Color.FromArgb(46, 139, 87),
                Location = new Point(20, 10), AutoSize = true
            };
            backtestPanel.Controls.Add(title);

            DataGridView grid = new DataGridView
            {
                Location = new Point(20, 50), Size = new Size(Math.Max(760, backtestPanel.ClientSize.Width - 40), 150),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                ReadOnly = true, RowHeadersVisible = false, AllowUserToAddRows = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = Color.White
            };
            grid.Columns.Add("Periods", "训练期数"); grid.Columns.Add("Tests", "验证次数");
            grid.Columns.Add("Top3", "前3命中率"); grid.Columns.Add("Top6", "前6命中率");
            grid.Columns.Add("HitStreak", "最大连中"); grid.Columns.Add("MissStreak", "最大连失");
            grid.Columns.Add("Model", "最佳模型");
            foreach (var report in reports)
                grid.Rows.Add(report.TrainPeriods, report.TotalTests, $"{report.Top3HitRate:F1}%",
                    $"{report.Top6HitRate:F1}%", report.MaxConsecutiveHits, report.MaxConsecutiveMiss, report.BestModel);
            backtestPanel.Controls.Add(grid);

            Label note = new Label
            {
                Text = "说明：每次只使用目标开奖之前的真实特码生肖训练，再与下一期实际特码生肖比较，不使用未来数据。",
                Font = new Font("微软雅黑", 10), ForeColor = Color.Gray,
                Location = new Point(20, 215), AutoSize = true
            };
            backtestPanel.Controls.Add(note);
            backtestPanel.Height = 270;
            backtestPanel.Visible = true;
        }

        private void RenderBacktestResult(AIBacktestV2.BacktestReportV2 report)
        {
            backtestPanel.Controls.Clear();

            if (report.TotalTests == 0)
            {
                Label noData = new Label();
                noData.Text = "数据不足，无法进行回测。请确保数据库中有足够多的历史记录（至少200期）。";
                noData.Font = new Font("微软雅黑", 12);
                noData.ForeColor = Color.Red;
                noData.Location = new Point(20, 20);
                noData.AutoSize = true;
                backtestPanel.Controls.Add(noData);
                backtestPanel.Visible = true;
                backtestPanel.Height = 80;
                return;
            }

            Label title = new Label();
            title.Text = "📊 AI预测回测验证 V2";
            title.Font = new Font("微软雅黑", 14, FontStyle.Bold);
            title.ForeColor = Color.FromArgb(46, 139, 87);
            title.Location = new Point(20, 10);
            title.AutoSize = true;
            backtestPanel.Controls.Add(title);

            Label testLabel = new Label();
            testLabel.Text = $"测试次数：{report.TotalTests} 次  |  训练期数：{report.TrainPeriods} 期  |  最佳模型：{report.BestModel}";
            testLabel.Font = new Font("微软雅黑", 11);
            testLabel.ForeColor = Color.FromArgb(80, 80, 100);
            testLabel.Location = new Point(20, 45);
            testLabel.AutoSize = true;
            testLabel.MaximumSize = new Size(700, 0);
            backtestPanel.Controls.Add(testLabel);

            int y = 75;

            Panel card3 = new Panel();
            card3.Size = new Size(200, 80);
            card3.Location = new Point(20, y);
            card3.BackColor = Color.FromArgb(235, 255, 235);
            card3.BorderStyle = BorderStyle.FixedSingle;

            Label label3 = new Label();
            label3.Text = "预测3生肖命中率";
            label3.Font = new Font("微软雅黑", 11);
            label3.ForeColor = Color.Gray;
            label3.Location = new Point(10, 10);
            label3.AutoSize = true;
            card3.Controls.Add(label3);

            Label value3 = new Label();
            value3.Text = $"{report.Top3HitRate:F1}%";
            value3.Font = new Font("微软雅黑", 24, FontStyle.Bold);
            value3.ForeColor = Color.FromArgb(0, 150, 0);
            value3.Location = new Point(50, 35);
            value3.AutoSize = true;
            card3.Controls.Add(value3);
            backtestPanel.Controls.Add(card3);

            Panel card6 = new Panel();
            card6.Size = new Size(200, 80);
            card6.Location = new Point(240, y);
            card6.BackColor = Color.FromArgb(235, 245, 255);
            card6.BorderStyle = BorderStyle.FixedSingle;

            Label label6 = new Label();
            label6.Text = "预测6生肖命中率";
            label6.Font = new Font("微软雅黑", 11);
            label6.ForeColor = Color.Gray;
            label6.Location = new Point(10, 10);
            label6.AutoSize = true;
            card6.Controls.Add(label6);

            Label value6 = new Label();
            value6.Text = $"{report.Top6HitRate:F1}%";
            value6.Font = new Font("微软雅黑", 24, FontStyle.Bold);
            value6.ForeColor = Color.FromArgb(0, 100, 200);
            value6.Location = new Point(50, 35);
            value6.AutoSize = true;
            card6.Controls.Add(value6);
            backtestPanel.Controls.Add(card6);

            y += 100;
            Label detailTitle = new Label();
            detailTitle.Text = $"📝 回测详情（最近10次）  |  最大连续命中：{report.MaxConsecutiveHits} 次  |  最大连续未中：{report.MaxConsecutiveMiss} 次";
            detailTitle.Font = new Font("微软雅黑", 11, FontStyle.Bold);
            detailTitle.ForeColor = Color.FromArgb(80, 80, 100);
            detailTitle.Location = new Point(20, y);
            detailTitle.AutoSize = true;
            detailTitle.MaximumSize = new Size(700, 0);
            backtestPanel.Controls.Add(detailTitle);

            y += 25;

            var recentDetails = report.Records.Take(10).ToList();
            foreach (var detail in recentDetails)
            {
                string hitMark3 = detail.Top3Hit ? "✅" : "❌";
                string hitMark6 = detail.Top6Hit ? "✅" : "❌";
                string detailText = $"#{detail.TestIndex}  实际:{detail.ActualZodiac}  3肖:{hitMark3}  6肖:{hitMark6}  预测3肖:[{string.Join(",", detail.PredictedTop3)}]";

                Label detailLabel = new Label();
                detailLabel.Text = detailText;
                detailLabel.Font = new Font("Consolas", 9);
                detailLabel.ForeColor = detail.Top3Hit ? Color.FromArgb(0, 120, 0) : Color.FromArgb(150, 50, 50);
                detailLabel.Location = new Point(20, y);
                detailLabel.AutoSize = true;
                backtestPanel.Controls.Add(detailLabel);
                y += 20;
            }

            backtestPanel.Height = y + 30;
            backtestPanel.Visible = true;
        }
    }
}
