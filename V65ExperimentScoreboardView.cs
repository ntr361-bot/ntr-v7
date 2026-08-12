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
            Text = "领先：至少30期，最近50期 TOP6 最好且平均排名最佳；暂停：连续8期 TOP6 未中；其余为观察。",
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

        void LoadRows()
        {
            grid.Rows.Clear();
            foreach (V65ExperimentScoreboardRow row in V65ExperimentScoreboardService.Load())
            {
                int index = grid.Rows.Add(row.Group, row.ModelName, row.Samples, row.Top3HitRate.ToString("P1"),
                    row.Top6HitRate.ToString("P1"), row.Samples == 0 ? "-" : row.AverageRank.ToString("F2"),
                    $"{row.Recent20Top3HitRate:P1} / {row.Recent20Top6HitRate:P1}",
                    $"{row.Recent50Top3HitRate:P1} / {row.Recent50Top6HitRate:P1}",
                    row.MaximumTop6Misses, row.CurrentTop6Misses, row.Status);
                DataGridViewRow gridRow = grid.Rows[index];
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
