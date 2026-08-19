using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace 六合分析软件
{
    /// <summary>
    /// AI 预测历史窗体
    /// </summary>
    public partial class AIPredictHistoryForm : Form
    {
        DataGridView table;
        Label statsLabel;
        Button btnRefresh;
        Button btnVerify;
        private readonly bool newModelOnly;
        private static readonly Font NumberHitFont = new Font("微软雅黑", 10, FontStyle.Bold);
        private const float WaveHitFontSize = 12f;

        public static float GetWaveHitFontSize(bool isHit) => isHit ? WaveHitFontSize : 9f;

        public static string GetWaveColorForNumber(string number)
        {
            return V65MappingService.GetWaveColor(number);
        }

        public static Color GetWaveColorForDisplay(string waveColor) => waveColor switch
        {
            "红" => Color.FromArgb(220, 30, 30),
            "蓝" => Color.FromArgb(30, 90, 210),
            "绿" => Color.FromArgb(0, 150, 70),
            _ => Color.FromArgb(30, 30, 30)
        };

        public static bool ShouldEmphasizeWave(string actualNumber, string predictedWave) =>
            !string.IsNullOrWhiteSpace(actualNumber) &&
            string.Equals(ColorEngine.ColorOf(actualNumber), predictedWave, StringComparison.Ordinal);

        public static Color GetWaveTextColor(string waveColor, bool isHit) =>
            isHit ? GetWaveColorForDisplay(waveColor) : Color.Black;

        public AIPredictHistoryForm() : this(false)
        {
        }

        protected AIPredictHistoryForm(bool newModelOnly)
        {
            this.newModelOnly = newModelOnly;
            InitializeComponent();

            this.Text = newModelOnly ? "智能预测历史记录" : "AI预测历史记录 - " + AIEngine.Version;
            this.Size = new Size(1000, 650);
            this.StartPosition = FormStartPosition.CenterParent;
            this.MinimumSize = new Size(1000, 650);
            this.WindowState = FormWindowState.Maximized;

            InitUI();
            LoadData();
        }

        private void InitUI()
        {
            TableLayoutPanel rootLayout = new TableLayoutPanel();
            rootLayout.Dock = DockStyle.Fill;
            rootLayout.Margin = new Padding(0);
            rootLayout.Padding = new Padding(0);
            rootLayout.ColumnCount = 1;
            rootLayout.RowCount = 2;
            rootLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
            rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            this.Controls.Add(rootLayout);

            // 顶部工具栏
            Panel topBar = new Panel();
            topBar.Dock = DockStyle.Fill;
            topBar.Margin = new Padding(0);
            topBar.BackColor = Color.FromArgb(30, 30, 46);
            rootLayout.Controls.Add(topBar, 0, 0);

            Label title = new Label();
            title.Text = newModelOnly
                ? "📜 智能预测历史记录（按期号和模型留档）"
                : "📜 AI预测历史记录（按期号和分析周期留档）";
            title.Font = new Font("微软雅黑", 16, FontStyle.Bold);
            title.ForeColor = Color.White;
            title.Dock = DockStyle.Fill;
            title.Padding = new Padding(20, 0, 0, 0);
            title.TextAlign = ContentAlignment.MiddleLeft;
            title.AutoEllipsis = true;
            topBar.Controls.Add(title);

            FlowLayoutPanel toolPanel = new FlowLayoutPanel();
            toolPanel.Dock = DockStyle.Right;
            toolPanel.Width = newModelOnly ? 350 : 230;
            toolPanel.Padding = new Padding(0, 15, 10, 0);
            toolPanel.WrapContents = false;
            toolPanel.FlowDirection = FlowDirection.LeftToRight;
            toolPanel.BackColor = topBar.BackColor;
            topBar.Controls.Add(toolPanel);

            // 刷新按钮
            btnRefresh = new Button();
            btnRefresh.Text = "🔄 刷新";
            btnRefresh.Font = new Font("微软雅黑", 10);
            btnRefresh.Size = new Size(80, 30);
            btnRefresh.Margin = new Padding(0, 0, 10, 0);
            btnRefresh.BackColor = Color.FromArgb(0, 122, 204);
            btnRefresh.ForeColor = Color.White;
            btnRefresh.FlatAppearance.BorderSize = 0;
            btnRefresh.Click += (s, e) => LoadData();
            toolPanel.Controls.Add(btnRefresh);

            // 验证按钮
            btnVerify = new Button();
            btnVerify.Text = "✅ 验证未开奖";
            btnVerify.Font = new Font("微软雅黑", 10);
            btnVerify.Size = new Size(110, 30);
            btnVerify.Margin = new Padding(0);
            btnVerify.BackColor = Color.FromArgb(46, 139, 87);
            btnVerify.ForeColor = Color.White;
            btnVerify.FlatAppearance.BorderSize = 0;
            btnVerify.Click += BtnVerify_Click;
            toolPanel.Controls.Add(btnVerify);

            if (newModelOnly)
            {
                var btnLearningReport = new Button
                {
                    Name = "btnLearningReport",
                    Text = "学习报告",
                    Font = new Font("微软雅黑", 10),
                    Size = new Size(100, 30),
                    Margin = new Padding(10, 0, 0, 0),
                    BackColor = Color.FromArgb(118, 78, 180),
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat
                };
                btnLearningReport.FlatAppearance.BorderSize = 0;
                btnLearningReport.Click += (_, _) =>
                {
                    using var report = new AutoLearningReportForm();
                    report.ShowDialog(this);
                };
                toolPanel.Controls.Add(btnLearningReport);
            }

            TableLayoutPanel content = new TableLayoutPanel();
            content.Dock = DockStyle.Fill;
            content.Padding = new Padding(10);
            content.ColumnCount = 1;
            content.RowCount = 2;
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            rootLayout.Controls.Add(content, 0, 1);

            // 统计标签
            statsLabel = new Label();
            statsLabel.Font = new Font("微软雅黑", 10);
            statsLabel.ForeColor = Color.FromArgb(80, 80, 100);
            statsLabel.Dock = DockStyle.Fill;
            statsLabel.TextAlign = ContentAlignment.MiddleLeft;
            statsLabel.AutoEllipsis = true;
            content.Controls.Add(statsLabel, 0, 0);

            // 数据表格
            table = new DataGridView();
            table.Dock = DockStyle.Fill;
            table.Margin = new Padding(0);
            table.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            table.AllowUserToAddRows = false;
            table.RowHeadersVisible = false;
            table.ReadOnly = true;
            table.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            table.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
            table.ScrollBars = ScrollBars.Both; // 支持横向和纵向滚动

            // 新列定义：期号 | 推荐生肖 | 推荐号码 | 实际特码 | 实际生肖 | 结果
            table.Columns.Add("Issue", "期号");
            table.Columns.Add("AnalysisPeriods", "分析期数");
            table.Columns.Add("PredictZodiac", "推荐生肖");
            table.Columns.Add("Top6Zodiac", "前6生肖");
            table.Columns.Add("PredictNumber", "模型重点号码");
            table.Columns.Add("ActualNumber", "实际特码");
            table.Columns.Add("ActualZodiac", "实际生肖");
            table.Columns.Add("HitResult", "前3结果");
            table.Columns.Add("Top6HitResult", "前6结果");
            if (newModelOnly)
                table.Columns.Add("ReviewDetails", "错因复盘/学习状态");
            if (newModelOnly)
                table.Columns.Add("ColorPrediction", "波色预测");
            table.Columns.Add("ModelVersion", "模型");
            table.Columns.Add("PredictionSource", "来源");
            table.Columns.Add("PredictTime", "预测时间");

            // 设置列宽（像素）
            table.Columns["Issue"].Width = 80;
            table.Columns["AnalysisPeriods"].Width = 70;
            table.Columns["PredictZodiac"].Width = 100;
            table.Columns["Top6Zodiac"].Width = 130;
            table.Columns["PredictNumber"].Width = 150;
            table.Columns["ActualNumber"].Width = 60;
            table.Columns["ActualZodiac"].Width = 60;
            table.Columns["HitResult"].Width = 70;
            table.Columns["Top6HitResult"].Width = 70;
            if (newModelOnly)
            {
                table.Columns["ReviewDetails"].MinimumWidth = 420;
                table.Columns["ReviewDetails"].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
                table.Columns["ReviewDetails"].DefaultCellStyle.WrapMode = DataGridViewTriState.True;
                table.Columns["ColorPrediction"].Width = 190;
                table.Columns["ColorPrediction"].DefaultCellStyle.WrapMode = DataGridViewTriState.True;
                table.Columns["ColorPrediction"].DefaultCellStyle.Font = new Font("微软雅黑", 10, FontStyle.Regular);
            }
            table.Columns["ModelVersion"].Width = 90;
            table.Columns["PredictionSource"].Width = 90;
            // Keep the history table readable at any maximized window size. The
            // previous fixed-width layout left the unused client area blank.
            if (newModelOnly)
            {
                table.Columns["PredictTime"].Width = 180;
            }
            else
            {
                table.Columns["PredictTime"].MinimumWidth = 180;
                table.Columns["PredictTime"].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            }

            // 号码列自动换行
            table.Columns["PredictNumber"].DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            table.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;

            table.Columns["HitResult"].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            table.Columns["Top6HitResult"].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            table.Columns["PredictZodiac"].DefaultCellStyle.ForeColor = Color.FromArgb(30, 30, 60);
            table.Columns["Top6Zodiac"].DefaultCellStyle.ForeColor = Color.FromArgb(30, 30, 60);
            table.Columns["PredictZodiac"].DefaultCellStyle.Font = new Font("微软雅黑", 10, FontStyle.Bold);
            table.Columns["Top6Zodiac"].DefaultCellStyle.Font = new Font("微软雅黑", 10, FontStyle.Bold);

            table.CellFormatting += Table_CellFormatting;
            table.CellPainting += Table_CellPainting;

            content.Controls.Add(table, 0, 1);
        }

        private void Table_CellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if ((e.ColumnIndex == table.Columns["HitResult"].Index ||
                 e.ColumnIndex == table.Columns["Top6HitResult"].Index) && e.Value != null)
            {
                if (e.Value.ToString() == "命中")
                {
                    e.CellStyle.ForeColor = Color.FromArgb(0, 150, 0);
                    e.CellStyle.Font = new Font("微软雅黑", 10, FontStyle.Bold);
                }
                else if (e.Value.ToString() == "未命中")
                {
                    e.CellStyle.ForeColor = Color.FromArgb(200, 50, 50);
                    e.CellStyle.Font = new Font("微软雅黑", 10, FontStyle.Bold);
                }
                else
                {
                    e.CellStyle.ForeColor = Color.Gray;
                }
            }
        }

        private void Table_CellPainting(object? sender, DataGridViewCellPaintingEventArgs e)
        {
            if (newModelOnly && e.RowIndex >= 0 &&
                table.Columns.Contains("ColorPrediction") &&
                e.ColumnIndex == table.Columns["ColorPrediction"].Index)
            {
                PaintColorPredictionCell(e);
                return;
            }

            if (e.RowIndex < 0 || e.ColumnIndex != table.Columns["PredictNumber"].Index)
                return;

            string text = Convert.ToString(e.FormattedValue) ?? string.Empty;
            string actualText = Convert.ToString(table.Rows[e.RowIndex].Cells["ActualNumber"].Value) ?? string.Empty;
            if (!int.TryParse(actualText, out int actualNumber))
                return;

            char[] separators = { ',', '，', '、', ' ', ';', '；' };
            string[] numbers = text.Split(separators, StringSplitOptions.RemoveEmptyEntries);
            bool numberHit = numbers.Any(value =>
                int.TryParse(value.Trim(), out int number) && number == actualNumber);
            if (!numberHit)
                return;

            e.PaintBackground(e.CellBounds, true);
            e.Paint(e.CellBounds, DataGridViewPaintParts.Border);

            bool selected = (e.State & DataGridViewElementStates.Selected) != 0;
            Color normalColor = selected ? e.CellStyle.SelectionForeColor : e.CellStyle.ForeColor;
            Color hitColor = selected ? Color.Yellow : Color.FromArgb(0, 105, 45);
            Font normalFont = e.CellStyle.Font ?? table.Font;
            TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
            int left = e.CellBounds.Left + 3;
            int right = e.CellBounds.Right - 3;
            int x = left;
            int y = e.CellBounds.Top + 2;
            int lineHeight = Math.Max(
                TextRenderer.MeasureText("00", normalFont, Size.Empty, flags).Height,
                TextRenderer.MeasureText("00", NumberHitFont, Size.Empty, flags).Height);

            for (int i = 0; i < numbers.Length; i++)
            {
                string numberText = numbers[i].Trim();
                string prefix = i == 0 ? string.Empty : ",";
                bool isHit = int.TryParse(numberText, out int number) && number == actualNumber;
                Font numberFont = isHit ? NumberHitFont : normalFont;
                int prefixWidth = TextRenderer.MeasureText(prefix, normalFont, Size.Empty, flags).Width;
                int numberWidth = TextRenderer.MeasureText(numberText, numberFont, Size.Empty, flags).Width;

                if (x > left && x + prefixWidth + numberWidth > right)
                {
                    x = left;
                    y += lineHeight;
                    prefix = string.Empty;
                    prefixWidth = 0;
                }

                if (prefix.Length > 0)
                {
                    TextRenderer.DrawText(e.Graphics, prefix, normalFont, new Point(x, y), normalColor, flags);
                    x += prefixWidth;
                }

                TextRenderer.DrawText(
                    e.Graphics,
                    numberText,
                    numberFont,
                    new Point(x, y),
                    isHit ? hitColor : normalColor,
                    flags);
                x += numberWidth;
            }

            e.Handled = true;
        }

        private void PaintColorPredictionCell(DataGridViewCellPaintingEventArgs e)
        {
            string text = Convert.ToString(e.FormattedValue) ?? string.Empty;
            const string mainPrefix = "主：";
            const string defensePrefix = "防：";
            int mainStart = text.IndexOf(mainPrefix, StringComparison.Ordinal);
            int defenseStart = text.IndexOf(defensePrefix, StringComparison.Ordinal);
            if (mainStart < 0 || defenseStart < 0 ||
                mainStart + mainPrefix.Length >= text.Length ||
                defenseStart + defensePrefix.Length >= text.Length)
                return;

            string mainColor = text.Substring(mainStart + mainPrefix.Length, 1);
            string defenseColor = text.Substring(defenseStart + defensePrefix.Length, 1);
            string actualNumber = Convert.ToString(table.Rows[e.RowIndex].Cells["ActualNumber"].Value) ?? string.Empty;
            bool mainHit = ShouldEmphasizeWave(actualNumber, mainColor);
            bool defenseHit = ShouldEmphasizeWave(actualNumber, defenseColor);

            e.PaintBackground(e.CellBounds, true);
            e.Paint(e.CellBounds, DataGridViewPaintParts.Border);

            Font labelFont = e.CellStyle.Font ?? table.Font;
            using Font normalColorFont = new Font(labelFont.FontFamily, 10, FontStyle.Regular);
            using Font hitColorFont = new Font(labelFont.FontFamily, 12, FontStyle.Bold);
            TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix |
                                    TextFormatFlags.VerticalCenter;
            int x = e.CellBounds.Left + 5;
            int y = e.CellBounds.Top;
            int height = e.CellBounds.Height;

            Color mainDisplayColor = GetWaveTextColor(mainColor, mainHit);
            Color defenseDisplayColor = GetWaveTextColor(defenseColor, defenseHit);
            DrawPart(mainPrefix, labelFont, mainDisplayColor);
            DrawPart(mainColor, mainHit ? hitColorFont : normalColorFont, mainDisplayColor);
            DrawPart("　", labelFont, Color.Black);
            DrawPart(defensePrefix, labelFont, defenseDisplayColor);
            DrawPart(defenseColor, defenseHit ? hitColorFont : normalColorFont, defenseDisplayColor);
            e.Handled = true;

            void DrawPart(string part, Font font, Color color)
            {
                Size size = TextRenderer.MeasureText(part, font, Size.Empty, flags);
                TextRenderer.DrawText(e.Graphics, part, font,
                    new Rectangle(x, y, size.Width, height), color, flags);
                x += size.Width;
            }
        }

        private void LoadData()
        {
            table.Rows.Clear();

            Color[] issueColors =
            {
                Color.FromArgb(255, 235, 130),
                Color.FromArgb(184, 222, 255),
                Color.FromArgb(190, 235, 190),
                Color.FromArgb(246, 195, 214),
                Color.FromArgb(218, 201, 247),
                Color.FromArgb(255, 207, 160)
            };
            var issueColorIndexes = new System.Collections.Generic.Dictionary<string, int>(StringComparer.Ordinal);
            int nextIssueColor = 0;

            var records = newModelOnly
                ? V7PredictionHistoryService.GetHistory(500)
                : DatabaseHelper.GetPredictionHistory(int.MaxValue)
                    .Where(r => !r.ModelVersion.StartsWith("V7", StringComparison.OrdinalIgnoreCase) &&
                                V7PredictionHistoryService.IsV65DisplayedModel(r.ModelVersion, r.AnalysisPeriods))
                    .Take(100)
                    .ToList();
            var verified = records.Where(r => !string.IsNullOrEmpty(r.ActualZodiac)).ToList();
            int total = verified.Count;
            int hits = verified.Count(r => r.HitResult == "命中");
            int top6Hits = verified.Count(r => r.Top6HitResult == "命中");
            double rate = total == 0 ? 0 : hits * 100.0 / total;
            double top6Rate = total == 0 ? 0 : top6Hits * 100.0 / total;

            var colorByIssue = records
                .Select(r => new { r.Issue, Color = V7PredictionHistoryService.ExtractColorPrediction(r.ScoreDetails) })
                .Where(x => x.Color != "-")
                .GroupBy(x => x.Issue)
                .ToDictionary(g => g.Key, g => g.First().Color);

            int unverified = records.Count(r => r.HitResult == "未开奖" || string.IsNullOrEmpty(r.HitResult));

            if (total > 0)
                statsLabel.Text = $"📊 验证{total}条  |  前3：{hits}次 ({rate:F1}%)  |  前6：{top6Hits}次 ({top6Rate:F1}%)  |  未开奖：{unverified}条";
            else
                statsLabel.Text = $"📊 暂无验证记录（需等待开奖后验证）  |  预测记录：{records.Count} 期";

            foreach (var r in records)
            {
                string hitResult = string.IsNullOrEmpty(r.HitResult) ? "未开奖" : r.HitResult;

                var rowValues = new System.Collections.Generic.List<object>
                {
                    r.Issue,
                    V7PredictionHistoryService.FormatAnalysisLabel(r.AnalysisPeriods, r.ModelVersion),
                    r.PredictZodiac,
                    string.IsNullOrEmpty(r.Top6Zodiac) ? "-" : r.Top6Zodiac,
                    string.IsNullOrEmpty(r.PredictNumber) ? "-" : r.PredictNumber,
                    string.IsNullOrEmpty(r.ActualNumber) ? "?" : r.ActualNumber,
                    string.IsNullOrEmpty(r.ActualZodiac) ? "?" : r.ActualZodiac,
                    hitResult,
                    string.IsNullOrEmpty(r.Top6HitResult) ? "未开奖" : r.Top6HitResult
                };
                if (newModelOnly)
                    rowValues.Add(!string.IsNullOrEmpty(r.ReviewDetails) ? r.ReviewDetails : r.LearningDetails);
                if (newModelOnly)
                    rowValues.Add(colorByIssue.TryGetValue(r.Issue, out string? color) ? color : "-");
                rowValues.Add(newModelOnly ? V7PredictionHistoryService.FormatModelName(r.ModelVersion) : r.ModelVersion);
                rowValues.Add(r.PredictionSource);
                rowValues.Add(r.PredictTime);
                int rowIndex = table.Rows.Add(rowValues.ToArray());

                string issueKey = Convert.ToString(r.Issue) ?? string.Empty;
                if (!issueColorIndexes.TryGetValue(issueKey, out int colorIndex))
                {
                    colorIndex = nextIssueColor++ % issueColors.Length;
                    issueColorIndexes[issueKey] = colorIndex;
                }
                table.Rows[rowIndex].DefaultCellStyle.BackColor = issueColors[colorIndex];
                table.Rows[rowIndex].Cells["Issue"].Style.BackColor = ControlPaint.Dark(issueColors[colorIndex], 0.08f);
                table.Rows[rowIndex].Cells["Issue"].Style.Font = new Font("微软雅黑", 9, FontStyle.Bold);
            }

            for (int rowIndex = 0; rowIndex < table.Rows.Count - 1; rowIndex++)
            {
                string currentIssue = Convert.ToString(table.Rows[rowIndex].Cells["Issue"].Value) ?? string.Empty;
                string nextIssue = Convert.ToString(table.Rows[rowIndex + 1].Cells["Issue"].Value) ?? string.Empty;
                if (!string.Equals(currentIssue, nextIssue, StringComparison.Ordinal))
                    table.Rows[rowIndex].DividerHeight = 3;
            }

            if (records.Count == 0)
                statsLabel.Text = "📊 暂无预测记录。请先进行AI预测，记录会自动保存。";
        }

        private void BtnVerify_Click(object sender, EventArgs e)
        {
            btnVerify.Enabled = false;
            btnVerify.Text = "⏳ 验证中...";
            Application.DoEvents();

            try
            {
                int verified = DatabaseHelper.BatchVerifyAIPredicts();
                if (verified > 0)
                {
                    MessageBox.Show($"验证完成！共验证 {verified} 条记录。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    MessageBox.Show("没有需要验证的记录。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"验证失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                btnVerify.Enabled = true;
                btnVerify.Text = "✅ 验证未开奖";
                LoadData();
            }
        }
    }
}
