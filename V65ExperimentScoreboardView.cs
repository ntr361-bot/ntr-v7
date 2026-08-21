using System.Drawing;
using System.Windows.Forms;

namespace 六合分析软件;

/// <summary>数据中心内的只读实验成绩榜，不触发预测或在线学习。</summary>
public static class V65ExperimentScoreboardView
{
    public static Control Create()
    {
        var panel = new Panel
        {
            Size = new Size(1120, 520),
            BackColor = Color.FromArgb(244, 247, 252),
            BorderStyle = BorderStyle.FixedSingle
        };
        var header = new Panel
        {
            Location = new Point(0, 0), Height = 58, Dock = DockStyle.Top,
            BackColor = Color.FromArgb(22, 58, 103)
        };
        var headerTitle = new Label
        {
            Text = "实验模型表现看板",
            Location = new Point(18, 10), AutoSize = true,
            Font = new Font("微软雅黑", 14, FontStyle.Bold), ForeColor = Color.White
        };
        var headerHint = new Label
        {
            Text = "已开奖记录 · 两组独立统计 · 不改变预测结果",
            Location = new Point(20, 34), AutoSize = true,
            Font = new Font("微软雅黑", 8.5f), ForeColor = Color.FromArgb(190, 216, 246)
        };
        header.Controls.Add(headerTitle);
        header.Controls.Add(headerHint);
        var refresh = new Button
        {
            Text = "刷新成绩",
            Location = new Point(14, 70),
            Size = new Size(106, 34),
            BackColor = Color.FromArgb(29, 123, 202),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("微软雅黑", 9, FontStyle.Bold)
        };
        refresh.FlatAppearance.BorderSize = 0;
        var note = new Label
        {
            Text = "点击每行“查看”可读取该模型最近30条已开奖明细；领先：至少30期，最近50期 TOP6 最好且平均排名最佳。",
            Location = new Point(136, 78),
            AutoSize = true,
            ForeColor = Color.DimGray,
            Font = new Font("微软雅黑", 9)
        };
        var grid = new DataGridView
        {
            Location = new Point(14, 116),
            Size = new Size(1088, 355),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
            ScrollBars = ScrollBars.Vertical,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            ReadOnly = true,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.None,
            EnableHeadersVisualStyles = false,
            GridColor = Color.FromArgb(218, 226, 238),
            RowTemplate = { Height = 31 },
            ColumnHeadersHeight = 34,
            ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(35, 80, 135), ForeColor = Color.White,
                Font = new Font("微软雅黑", 9, FontStyle.Bold), Alignment = DataGridViewContentAlignment.MiddleCenter
            },
            DefaultCellStyle = new DataGridViewCellStyle
            {
                SelectionBackColor = Color.FromArgb(211, 232, 253), SelectionForeColor = Color.FromArgb(20, 48, 85),
                Font = new Font("微软雅黑", 9), Alignment = DataGridViewContentAlignment.MiddleCenter
            }
        };
        grid.Columns.Add("Group", "模型组");
        grid.Columns.Add("Model", "模型");
        grid.Columns.Add(new DataGridViewButtonColumn { Name = "Details", HeaderText = "近30期明细", Text = "查看", UseColumnTextForButtonValue = true, Width = 94 });
        grid.Columns.Add("Samples", "累计期数");
        grid.Columns.Add("Top3", "TOP3");
        grid.Columns.Add("Top6", "TOP6");
        grid.Columns.Add("Rank", "平均排名");
        grid.Columns.Add("Recent20", "近20期 TOP3/TOP6");
        grid.Columns.Add("Recent50", "近50期 TOP3/TOP6");
        grid.Columns.Add("MaxMiss", "最大连续 TOP6 未中");
        grid.Columns.Add("CurrentMiss", "当前连续未中");
        grid.Columns.Add("Status", "状态");
        grid.Columns["Group"].Width = 150;
        grid.Columns["Model"].Width = 150;
        grid.Columns["Samples"].Width = 82;
        grid.Columns["Top3"].Width = 76;
        grid.Columns["Top6"].Width = 76;
        grid.Columns["Rank"].Width = 92;
        grid.Columns["Recent20"].Width = 150;
        grid.Columns["Recent50"].Width = 150;
        grid.Columns["MaxMiss"].Width = 154;
        grid.Columns["CurrentMiss"].Width = 126;
        grid.Columns["Status"].Width = 76;
        var horizontalScroll = new HScrollBar
        {
            Location = new Point(14, 478),
            Size = new Size(1088, 18),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            SmallChange = 40,
            LargeChange = 300,
            Visible = true
        };
        var scrollHint = new Label
        {
            Text = "← 左右拖动滑块查看右侧列：近50期、连续未中、状态 →",
            Location = new Point(128, 506),
            AutoSize = true,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
            ForeColor = Color.FromArgb(67, 102, 143),
            Font = new Font("微软雅黑", 8.5f)
        };
        void RefreshHorizontalScroll()
        {
            int offset = Math.Max(0, grid.Columns.GetColumnsWidth(DataGridViewElementStates.Visible) - grid.DisplayRectangle.Width);
            horizontalScroll.Maximum = Math.Max(0, offset + horizontalScroll.LargeChange - 1);
            horizontalScroll.Value = Math.Min(horizontalScroll.Value, offset);
            horizontalScroll.Enabled = offset > 0;
        }
        horizontalScroll.ValueChanged += (_, _) => grid.HorizontalScrollingOffset = horizontalScroll.Value;
        grid.Resize += (_, _) => RefreshHorizontalScroll();
        grid.CellFormatting += (_, e) =>
        {
            if (e.ColumnIndex == grid.Columns["Status"].Index && e.Value is string status)
            {
                e.CellStyle.Font = new Font(grid.Font, FontStyle.Bold);
                e.CellStyle.BackColor = status switch
                {
                    "领先" => Color.FromArgb(217, 245, 226),
                    "暂停" => Color.FromArgb(255, 224, 226),
                    _ => Color.FromArgb(238, 242, 247)
                };
                e.CellStyle.ForeColor = status switch
                {
                    "领先" => Color.FromArgb(20, 121, 62),
                    "暂停" => Color.FromArgb(190, 45, 55),
                    _ => Color.FromArgb(84, 101, 122)
                };
            }
            if (e.ColumnIndex is 3 or 4 && e.Value is string metric && metric != "0.0%")
            {
                e.CellStyle.Font = new Font(grid.Font, FontStyle.Bold);
                e.CellStyle.ForeColor = Color.FromArgb(20, 105, 180);
            }
        };

        void ShowDetails(IEnumerable<string> modelNames)
        {
            string[] selectedModels = modelNames.Distinct().ToArray();
            var detailForm = new Form
            {
                Text = selectedModels.Length == 1 ? $"{selectedModels[0]} - 最新预测与近30期成绩" : "模型 - 最新预测与近30期成绩",
                Size = new Size(1180, 620),
                StartPosition = FormStartPosition.CenterParent,
                BackColor = Color.White,
                MinimizeBox = false,
                MaximizeBox = true
            };
            var detailGrid = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                BackgroundColor = Color.White,
                EnableHeadersVisualStyles = false,
                ColumnHeadersHeight = 34,
                ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
                {
                    BackColor = Color.FromArgb(35, 80, 135), ForeColor = Color.White,
                    Font = new Font("微软雅黑", 9, FontStyle.Bold), Alignment = DataGridViewContentAlignment.MiddleCenter
                },
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Font = new Font("微软雅黑", 9), Alignment = DataGridViewContentAlignment.MiddleCenter
                }
            };
            detailGrid.Columns.Add("Model", "模型");
            detailGrid.Columns.Add("Issue", "期号");
            detailGrid.Columns.Add("Top3", "预测TOP3");
            detailGrid.Columns.Add("Top6", "预测TOP6");
            detailGrid.Columns.Add("Actual", "实际生肖");
            detailGrid.Columns.Add("Rank", "实际排名");
            detailGrid.Columns.Add("Top3Hit", "TOP3结果");
            detailGrid.Columns.Add("Top6Hit", "TOP6结果");
            detailGrid.Columns.Add("Time", "预测时间");
            detailGrid.Columns.Add("Source", "来源");
            detailGrid.Columns["Model"].Width = 150;
            detailGrid.Columns["Issue"].Width = 88;
            detailGrid.Columns["Top3"].Width = 150;
            detailGrid.Columns["Top6"].Width = 205;
            detailGrid.Columns["Actual"].Width = 82;
            detailGrid.Columns["Rank"].Width = 82;
            detailGrid.Columns["Top3Hit"].Width = 90;
            detailGrid.Columns["Top6Hit"].Width = 90;
            detailGrid.Columns["Time"].Width = 150;
            detailGrid.Columns["Source"].Width = 110;

            V65ExperimentScoreboardDetailRow[] details = selectedModels
                .SelectMany(model => V65ExperimentScoreboardService.LoadScorecardDetails(model))
                .OrderByDescending(detail => long.TryParse(detail.Issue, out long issue) ? issue : long.MinValue)
                .ThenBy(detail => detail.ModelName)
                .ToArray();
            foreach (V65ExperimentScoreboardDetailRow detail in details)
            {
                int index = detailGrid.Rows.Add(detail.ModelName, detail.Issue,
                    string.IsNullOrWhiteSpace(detail.Top3Zodiac) ? "-" : detail.Top3Zodiac,
                    string.IsNullOrWhiteSpace(detail.Top6Zodiac) ? "-" : detail.Top6Zodiac,
                    detail.IsVerified ? detail.ActualZodiac : "未开奖", detail.IsVerified ? detail.ActualRank : "-",
                    detail.IsVerified ? (detail.Top3Hit ? "命中" : "未命中") : "未开奖",
                    detail.IsVerified ? (detail.Top6Hit ? "命中" : "未命中") : "未开奖",
                    detail.PredictTime, string.IsNullOrWhiteSpace(detail.Source) ? "-" : detail.Source);
                if (!detail.IsVerified)
                {
                    detailGrid.Rows[index].DefaultCellStyle.BackColor = Color.FromArgb(255, 248, 214);
                    detailGrid.Rows[index].Cells["Top3Hit"].Style.ForeColor = Color.FromArgb(150, 102, 0);
                    detailGrid.Rows[index].Cells["Top6Hit"].Style.ForeColor = Color.FromArgb(150, 102, 0);
                }
                else
                {
                    detailGrid.Rows[index].Cells["Top3Hit"].Style.ForeColor = detail.Top3Hit ? Color.ForestGreen : Color.Firebrick;
                    detailGrid.Rows[index].Cells["Top6Hit"].Style.ForeColor = detail.Top6Hit ? Color.ForestGreen : Color.Firebrick;
                }
            }

            if (details.Length == 0)
            {
                detailForm.Controls.Add(new Label
                {
                    Text = "暂无已开奖记录",
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Font = new Font("微软雅黑", 14, FontStyle.Bold),
                    ForeColor = Color.DimGray
                });
            }
            else detailForm.Controls.Add(detailGrid);
            detailForm.ShowDialog(panel.FindForm());
        }

        void LoadRows()
        {
            headerHint.Text = $"已开奖记录 · 两组独立统计 · V6.5自动学习：{V65ExperimentScoreboardService.LoadAutoLearningState()}";
            grid.Rows.Clear();
            foreach (V65ExperimentScoreboardRow row in V65ExperimentScoreboardService.Load())
            {
                int index = grid.Rows.Add(row.Group, row.ModelName, "查看", row.Samples, row.Top3HitRate.ToString("P1"),
                    row.Top6HitRate.ToString("P1"), row.Samples == 0 ? "-" : row.AverageRank.ToString("F2"),
                    $"{row.Recent20Top3HitRate:P1} / {row.Recent20Top6HitRate:P1}",
                    $"{row.Recent50Top3HitRate:P1} / {row.Recent50Top6HitRate:P1}",
                    row.MaximumTop6Misses, row.CurrentTop6Misses, row.Status);
                DataGridViewRow gridRow = grid.Rows[index];
                gridRow.Tag = row.ModelName;
                bool v65 = row.Group == "V6.5四模型实验";
                gridRow.DefaultCellStyle.BackColor = v65 ? Color.FromArgb(249, 252, 255) : Color.FromArgb(253, 251, 255);
                gridRow.Cells["Group"].Style.BackColor = v65 ? Color.FromArgb(236, 246, 255) : Color.FromArgb(246, 239, 255);
                gridRow.Cells["Group"].Style.ForeColor = v65 ? Color.FromArgb(35, 84, 136) : Color.FromArgb(93, 67, 132);
                gridRow.Cells["Group"].Style.Font = new Font(grid.Font, FontStyle.Bold);
                gridRow.Cells["Model"].Style.Font = new Font(grid.Font, FontStyle.Bold);
                gridRow.Cells["Model"].Style.ForeColor = Color.FromArgb(28, 43, 62);
            }
        }

        refresh.Click += (_, _) => LoadRows();
        grid.CellContentClick += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0 || grid.Columns[e.ColumnIndex].Name != "Details") return;
            if (grid.Rows[e.RowIndex].Tag is string modelName) ShowDetails(new[] { modelName });
        };
        panel.Controls.Add(header);
        panel.Controls.Add(refresh);
        panel.Controls.Add(note);
        panel.Controls.Add(grid);
        panel.Controls.Add(horizontalScroll);
        panel.Controls.Add(scrollHint);
        LoadRows();
        RefreshHorizontalScroll();
        return panel;
    }
}
