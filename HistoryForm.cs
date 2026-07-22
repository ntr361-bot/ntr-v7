using System;
using System.Drawing;
using System.Windows.Forms;

namespace 六合分析软件
{
    public partial class HistoryForm : Form
    {
        DataGridView table;
        Button btnRefresh;
        Button btnDelete;
        Button btnUpdate;
        Label statusLabel;
        ComboBox cboExportPeriods;

        public HistoryForm()
        {
            InitializeComponent();

            this.Text = "历史开奖数据";
            this.Size = new Size(1000, 600);

            this.FormClosing += (s, e) =>
            {
                try
                {
                    System.IO.File.WriteAllText("history_window.cfg",
                        $"{this.Left},{this.Top},{this.Width},{this.Height}");
                }
                catch { }
            };
            try
            {
                if (System.IO.File.Exists("history_window.cfg"))
                {
                    var parts = System.IO.File.ReadAllText("history_window.cfg").Split(',');
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

            Label title = new Label();
            title.Text = "历史开奖数据";
            title.Font = new Font("微软雅黑", 20);
            title.Location = new Point(30, 20);
            title.AutoSize = true;
            this.Controls.Add(title);

            statusLabel = new Label();
            statusLabel.Name = "statusLabel";
            statusLabel.Font = new Font("微软雅黑", 10);
            statusLabel.ForeColor = Color.Gray;
            statusLabel.Location = new Point(30, 55);
            statusLabel.AutoSize = true;
            statusLabel.MaximumSize = new Size(700, 0);
            this.Controls.Add(statusLabel);

            btnUpdate = new Button();
            btnUpdate.Text = "更新最新";
            btnUpdate.Size = new Size(95, 30);
            btnUpdate.Location = new Point(170, 20);
            btnUpdate.BackColor = Color.FromArgb(0, 122, 204);
            btnUpdate.ForeColor = Color.White;
            btnUpdate.Click += BtnUpdate_Click;
            this.Controls.Add(btnUpdate);

            Button btnRepair = new Button();
            btnRepair.Text = "修复全部";
            btnRepair.Size = new Size(105, 30);
            btnRepair.Location = new Point(270, 20);
            btnRepair.BackColor = Color.FromArgb(200, 80, 30);
            btnRepair.ForeColor = Color.White;
            btnRepair.Click += BtnRepair_Click;
            this.Controls.Add(btnRepair);

            btnDelete = new Button();
            btnDelete.Text = "删除选中";
            btnDelete.Size = new Size(75, 30);
            btnDelete.Location = new Point(380, 20);
            btnDelete.Click += BtnDelete_Click;
            this.Controls.Add(btnDelete);

            Button btnExport = new Button();
            btnExport.Text = "导出CSV";
            btnExport.Size = new Size(90, 30);
            btnExport.Location = new Point(460, 20);
            btnExport.BackColor = Color.FromArgb(46, 139, 87);
            btnExport.ForeColor = Color.White;
            btnExport.FlatStyle = FlatStyle.Flat;
            btnExport.FlatAppearance.BorderSize = 0;
            btnExport.Font = new Font("微软雅黑", 9);
            btnExport.Click += BtnExport_Click;
            this.Controls.Add(btnExport);

            btnRefresh = new Button();
            btnRefresh.Text = "刷新";
            btnRefresh.Size = new Size(60, 30);
            btnRefresh.Location = new Point(555, 20);
            btnRefresh.Click += BtnRefresh_Click;
            this.Controls.Add(btnRefresh);

            Label exportRangeLabel = new Label();
            exportRangeLabel.Text = "导出范围:";
            exportRangeLabel.Font = new Font("微软雅黑", 10);
            exportRangeLabel.Location = new Point(630, 25);
            exportRangeLabel.AutoSize = true;
            exportRangeLabel.ForeColor = Color.Gray;
            this.Controls.Add(exportRangeLabel);

            cboExportPeriods = new ComboBox();
            cboExportPeriods.Font = new Font("微软雅黑", 9);
            cboExportPeriods.Location = new Point(700, 21);
            cboExportPeriods.Size = new Size(110, 25);
            cboExportPeriods.DropDownStyle = ComboBoxStyle.DropDownList;
            cboExportPeriods.Items.AddRange(new object[] { "最近100期", "最近300期", "最近500期" });
            cboExportPeriods.SelectedIndex = 2;
            this.Controls.Add(cboExportPeriods);

            Label searchLabel = new Label();
            searchLabel.Text = "搜索:";
            searchLabel.Font = new Font("微软雅黑", 10);
            searchLabel.Location = new Point(30, 78);
            searchLabel.AutoSize = true;
            searchLabel.ForeColor = Color.Gray;
            this.Controls.Add(searchLabel);

            TextBox txtSearch = new TextBox();
            txtSearch.Name = "txtSearch";
            txtSearch.Font = new Font("微软雅黑", 10);
            txtSearch.Location = new Point(70, 76);
            txtSearch.Size = new Size(120, 23);
            txtSearch.TextChanged += (s, e) => FilterTable();
            this.Controls.Add(txtSearch);

            // 数据表格 - 显示 6个平码 + 特码 + 特码生肖
            table = new DataGridView();
            table.Location = new Point(30, 108);
            table.Size = new Size(920, 440);
            table.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            table.AllowUserToAddRows = false;
            table.RowHeadersVisible = false;
            table.ReadOnly = true;
            table.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            table.DefaultCellStyle.SelectionBackColor = Color.FromArgb(220, 220, 220);
            table.DefaultCellStyle.SelectionForeColor = Color.Black;

            table.Columns.Add("Id", "ID");
            table.Columns.Add("期号", "期号");
            table.Columns.Add("平码", "平码(6个)");      // 前6个号码
            table.Columns.Add("特码", "特码");             // 第7个号码
            table.Columns.Add("特码生肖", "特码生肖");
            table.Columns.Add("开奖时间", "开奖时间");
            table.Columns.Add("日期", "日期");
            table.Columns.Add("生肖", "生肖");

            table.Columns["Id"].Visible = false;
            table.Columns["生肖"].Visible = false;
            table.Columns["日期"].Visible = false;

            // 设置列宽
            table.Columns["期号"].Width = 100;
            table.Columns["平码"].Width = 260;
            table.Columns["特码"].Width = 60;
            table.Columns["特码生肖"].Width = 80;
            table.Columns["开奖时间"].Width = 160;

            // 平码列隐藏，点击展开按钮再看
            table.CellFormatting += Table_CellFormatting;

            this.Controls.Add(table);

            LoadData();
            AutoCheckAndFetch();
        }

        private void LoadData()
        {
            // 查找搜索框
            string keyword = "";
            var searchBox = this.Controls.Find("txtSearch", false);
            if (searchBox.Length > 0 && searchBox[0] is TextBox tb)
                keyword = tb.Text.Trim();

            table.Rows.Clear();

            var records = DatabaseHelper.GetLatestHistory(200);
            foreach (var r in records)
            {
                // 显示六合号码 + 特码
                string numbers6 = r.Numbers;  // 前6个号码
                string specialNum = r.SpecialNumber; // 第7个号码（特码）

                // 如果 database 里的 Numbers 字段已经包含了特码，提取前6个
                if (string.IsNullOrEmpty(numbers6))
                {
                    numbers6 = r.Numbers ?? "";
                }

                // 格式化显示：数字之间加空格
                string formattedNumbers = FormatNumbers(numbers6);

                table.Rows.Add(new object[]
                {
                    r.Id,
                    r.Period,
                    formattedNumbers,
                    specialNum,
                    r.SpecialZodiac,
                    r.OpenTime,
                    r.Date,
                    r.ShengXiao
                });
            }

            int total = DatabaseHelper.GetHistory().Count;
            statusLabel.Text = $"共 {total} 条记录，显示最近 {records.Count} 条";
        }

        // 导出 CSV - 包含 6个平码 + 特码 + 生肖
        private void BtnExport_Click(object sender, EventArgs e)
        {
            try
            {
                int periods = GetSelectedExportPeriods();
                string keyword = "";
                var searchBox = this.Controls.Find("txtSearch", false);
                if (searchBox.Length > 0 && searchBox[0] is TextBox tb)
                    keyword = tb.Text.Trim();

                var exportRecords = DatabaseHelper.GetLatestHistory(periods);
                if (!string.IsNullOrEmpty(keyword))
                {
                    exportRecords = exportRecords
                        .Where(r => (r.Period ?? "").Contains(keyword)
                            || FormatNumbers(r.Numbers ?? "").Contains(keyword)
                            || (r.SpecialNumber ?? "").Contains(keyword)
                            || (r.SpecialZodiac ?? "").Contains(keyword)
                            || (r.OpenTime ?? "").Contains(keyword))
                        .ToList();
                }

                using (SaveFileDialog sfd = new SaveFileDialog())
                {
                    sfd.Filter = "CSV文件|*.csv";
                    sfd.FileName = $"历史数据_最近{periods}期_{DateTime.Now:yyyyMMdd}.csv";
                    if (sfd.ShowDialog() != DialogResult.OK) return;

                    using (var writer = new System.IO.StreamWriter(sfd.FileName, false, System.Text.Encoding.UTF8))
                    {
                        writer.Write("\uFEFF");
                        writer.WriteLine("期号,六码(平码),特码,特码生肖,开奖时间");

                        foreach (var record in exportRecords)
                        {
                            writer.WriteLine($"{EscapeCsv(record.Period)},{EscapeCsv(FormatNumbers(record.Numbers))},{EscapeCsv(record.SpecialNumber)},{EscapeCsv(record.SpecialZodiac)},{EscapeCsv(record.OpenTime)}");
                        }
                    }
                    SetStatus($"已导出最近 {periods} 期，共 {exportRecords.Count} 条：{sfd.FileName}", Color.Green);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"导出失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private int GetSelectedExportPeriods()
        {
            string selected = cboExportPeriods?.SelectedItem?.ToString() ?? "";
            if (selected.Contains("100")) return 100;
            if (selected.Contains("300")) return 300;
            return 500;
        }

        private string EscapeCsv(string value)
        {
            value = value ?? "";
            if (value.Contains(",") || value.Contains("\"") || value.Contains("\r") || value.Contains("\n"))
                return $"\"{value.Replace("\"", "\"\"")}\"";
            return value;
        }

        private void Table_CellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (table.Columns[e.ColumnIndex]?.Name == "特码" && e.Value != null)
            {
                string val = e.Value.ToString();
                if (int.TryParse(val, out int num))
                {
                    // 红波: 1,2,7,8,12,13,18,19,23,24,29,30,34,35,40,45,46
                    // 蓝波: 3,4,9,10,14,15,20,25,26,31,36,37,41,42,47,48
                    // 绿波: 5,6,11,16,17,21,22,27,28,32,33,38,39,43,44,49
                    int[] red = { 1,2,7,8,12,13,18,19,23,24,29,30,34,35,40,45,46 };
                    int[] blue = { 3,4,9,10,14,15,20,25,26,31,36,37,41,42,47,48 };

                    if (red.Contains(num))
                        e.CellStyle.BackColor = Color.FromArgb(255, 68, 68);
                    else if (blue.Contains(num))
                        e.CellStyle.BackColor = Color.FromArgb(68, 136, 255);
                    else
                        e.CellStyle.BackColor = Color.FromArgb(68, 187, 68);

                    e.CellStyle.ForeColor = Color.White;

                    e.CellStyle.Font = new Font("微软雅黑", 11, FontStyle.Bold);
                }
            }
        }

        // 格式化号码：每两位加空格
        private string FormatNumbers(string raw)
        {
            if (string.IsNullOrEmpty(raw) || raw.Length < 2)
                return raw ?? "";

            var parts = new System.Collections.Generic.List<string>();
            for (int i = 0; i < raw.Length; i += 2)
            {
                if (i + 1 < raw.Length)
                    parts.Add(raw.Substring(i, 2));
                else
                    parts.Add(raw.Substring(i, 1));
            }
            return string.Join(" ", parts);
        }

        private void FilterTable()
        {
            var searchBox = this.Controls.Find("txtSearch", false);
            if (searchBox.Length == 0) return;
            string keyword = ((TextBox)searchBox[0]).Text.Trim();

            foreach (DataGridViewRow row in table.Rows)
            {
                if (keyword == "")
                {
                    row.Visible = true;
                    continue;
                }
                bool show = false;
                foreach (DataGridViewCell cell in row.Cells)
                {
                    if (cell.Value != null && cell.Value.ToString().Contains(keyword))
                    {
                        show = true;
                        break;
                    }
                }
                row.Visible = show;
            }

            int visibleCount = 0;
            foreach (DataGridViewRow row in table.Rows)
                if (row.Visible) visibleCount++;
            statusLabel.Text = $"{visibleCount} 条记录";
        }

        private void SetStatus(string msg, Color color)
        {
            statusLabel.Text = msg;
            statusLabel.ForeColor = color;
        }

        private async void BtnUpdate_Click(object sender, EventArgs e)
        {
            btnUpdate.Enabled = false;
            SetStatus("正在更新数据...", Color.Gray);

            try
            {
                var result = await DataCrawler.FetchAndSaveAsync();
                if (result.Success)
                {
                    if (result.NewCount > 0)
                        DatabaseHelper.BatchVerifyAIPredicts();
                    SetStatus(result.Message, Color.Green);
                }
                else
                {
                    SetStatus($"更新失败: {result.Message}", Color.Red);
                }
            }
            catch (Exception ex)
            {
                SetStatus($"更新异常: {ex.Message}", Color.Red);
            }
            finally
            {
                btnUpdate.Enabled = true;
                LoadData();
            }
        }

        private void BtnDelete_Click(object sender, EventArgs e)
        {
            if (table.SelectedRows.Count == 0)
            {
                SetStatus("请先选中要删除的行", Color.Red);
                return;
            }

            var confirm = MessageBox.Show("确定要删除选中的记录吗？", "确认删除",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes) return;

            int deleted = 0;
            foreach (DataGridViewRow row in table.SelectedRows)
            {
                int id = (int)row.Cells["Id"].Value;
                DatabaseHelper.DeleteHistory(id);
                deleted++;
            }

            LoadData();
            SetStatus($"已删除 {deleted} 条记录", Color.Green);
        }

        private void BtnRefresh_Click(object sender, EventArgs e)
        {
            LoadData();
            SetStatus("已刷新", Color.Gray);
        }

        private void BtnRepair_Click(object sender, EventArgs e)
        {
            SetStatus("正在修复数据...", Color.Gray);
            Application.DoEvents();

            try
            {
                int repaired = DatabaseHelper.MigrateOldData();
                if (repaired > 0)
                    SetStatus($"修复完成：{repaired} 条记录已补全", Color.Green);
                else
                    SetStatus("无需修复，所有数据完整", Color.Gray);
                LoadData();
            }
            catch (Exception ex)
            {
                SetStatus($"修复失败：{ex.Message}", Color.Red);
            }
        }

        private async void AutoCheckAndFetch()
        {
            try
            {
                string latestPeriod = DatabaseHelper.GetLatestPeriod();
                if (string.IsNullOrEmpty(latestPeriod))
                {
                    SetStatus("数据库为空，正在自动抓取...", Color.Orange);
                    var result = await DataCrawler.FetchAndSaveAsync();
                    if (result.Success)
                    {
                        if (result.NewCount > 0)
                            DatabaseHelper.BatchVerifyAIPredicts();
                        SetStatus($"自动抓取：{result.Message}", Color.Green);
                        LoadData();
                    }
                    return;
                }
            }
            catch { }
        }
    }
}
