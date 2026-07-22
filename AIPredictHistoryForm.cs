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

        public AIPredictHistoryForm()
        {
            InitializeComponent();

            this.Text = "AI预测历史记录 - " + AIEngine.Version;
            this.Size = new Size(1000, 650);
            this.StartPosition = FormStartPosition.CenterParent;
            this.MinimumSize = new Size(1000, 650);

            InitUI();
            LoadData();
        }

        private void InitUI()
        {
            // 顶部工具栏
            Panel topBar = new Panel();
            topBar.Dock = DockStyle.Top;
            topBar.Height = 60;
            topBar.BackColor = Color.FromArgb(30, 30, 46);
            this.Controls.Add(topBar);

            Label title = new Label();
            title.Text = "📜 AI预测历史记录（按期号和分析周期留档）";
            title.Font = new Font("微软雅黑", 16, FontStyle.Bold);
            title.ForeColor = Color.White;
            title.Location = new Point(20, 15);
            title.AutoSize = true;
            topBar.Controls.Add(title);

            // 刷新按钮
            btnRefresh = new Button();
            btnRefresh.Text = "🔄 刷新";
            btnRefresh.Font = new Font("微软雅黑", 10);
            btnRefresh.Size = new Size(80, 30);
            btnRefresh.Location = new Point(650, 15);
            btnRefresh.BackColor = Color.FromArgb(0, 122, 204);
            btnRefresh.ForeColor = Color.White;
            btnRefresh.FlatAppearance.BorderSize = 0;
            btnRefresh.Click += (s, e) => LoadData();
            topBar.Controls.Add(btnRefresh);

            // 验证按钮
            btnVerify = new Button();
            btnVerify.Text = "✅ 验证未开奖";
            btnVerify.Font = new Font("微软雅黑", 10);
            btnVerify.Size = new Size(110, 30);
            btnVerify.Location = new Point(740, 15);
            btnVerify.BackColor = Color.FromArgb(46, 139, 87);
            btnVerify.ForeColor = Color.White;
            btnVerify.FlatAppearance.BorderSize = 0;
            btnVerify.Click += BtnVerify_Click;
            topBar.Controls.Add(btnVerify);

            // 统计标签
            statsLabel = new Label();
            statsLabel.Font = new Font("微软雅黑", 10);
            statsLabel.ForeColor = Color.FromArgb(80, 80, 100);
            statsLabel.Location = new Point(20, 70);
            statsLabel.AutoSize = true;
            statsLabel.MaximumSize = new Size(900, 0);
            this.Controls.Add(statsLabel);

            // 数据表格
            table = new DataGridView();
            table.Location = new Point(20, 100);
            table.Size = new Size(940, 500);
            table.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None; // 手动控制列宽
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
            table.Columns.Add("ModelVersion", "模型");
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
            table.Columns["ModelVersion"].Width = 80;
            table.Columns["PredictTime"].Width = 140;

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

            this.Controls.Add(table);
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

        private void LoadData()
        {
            table.Rows.Clear();

            var records = DatabaseHelper.GetPredictionHistory(100);
            var (total, hits, top6Hits, rate, top6Rate) = DatabaseHelper.GetAIPredictStats();

            int unverified = records.Count(r => r.HitResult == "未开奖" || string.IsNullOrEmpty(r.HitResult));

            if (total > 0)
                statsLabel.Text = $"📊 验证{total}条  |  前3：{hits}次 ({rate:F1}%)  |  前6：{top6Hits}次 ({top6Rate:F1}%)  |  未开奖：{unverified}条";
            else
                statsLabel.Text = $"📊 暂无验证记录（需等待开奖后验证）  |  预测记录：{records.Count} 期";

            foreach (var r in records)
            {
                string hitResult = string.IsNullOrEmpty(r.HitResult) ? "未开奖" : r.HitResult;

                table.Rows.Add(new object[]
                {
                    r.Issue,
                    r.AnalysisPeriods > 0 ? $"{r.AnalysisPeriods}期" : "旧记录",
                    r.PredictZodiac,
                    string.IsNullOrEmpty(r.Top6Zodiac) ? "-" : r.Top6Zodiac,
                    string.IsNullOrEmpty(r.PredictNumber) ? "-" : r.PredictNumber,
                    string.IsNullOrEmpty(r.ActualNumber) ? "?" : r.ActualNumber,
                    string.IsNullOrEmpty(r.ActualZodiac) ? "?" : r.ActualZodiac,
                    hitResult,
                    string.IsNullOrEmpty(r.Top6HitResult) ? "未开奖" : r.Top6HitResult,
                    r.ModelVersion,
                    r.PredictTime
                });
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
