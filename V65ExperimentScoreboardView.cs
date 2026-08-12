using System.Drawing;
using System.Windows.Forms;

namespace 六合分析软件;

/// <summary>数据中心内的只读实验成绩榜，不触发预测或在线学习。</summary>
public static class V65ExperimentScoreboardView
{
    public static Control Create()
    {
        var panel = new Panel { BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
        var refresh = new Button
        {
            Text = "刷新成绩",
            Location = new Point(14, 12),
            Size = new Size(100, 32),
            BackColor = Color.FromArgb(0, 122, 204),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        refresh.FlatAppearance.BorderSize = 0;
        var note = new Label
        {
            Text = "领先：至少30期，最近50期 TOP6 最好且平均排名最佳；暂停：连续8期 TOP6 未中；其余为观察。",
            Location = new Point(130, 19),
            AutoSize = true,
            ForeColor = Color.DimGray,
            Font = new Font("微软雅黑", 9)
        };
        var grid = new DataGridView
        {
            Location = new Point(14, 55),
            Size = new Size(1088, 445),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            ReadOnly = true,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            BackgroundColor = Color.White
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
        grid.Columns["Group"].FillWeight = 15;
        grid.Columns["Model"].FillWeight = 15;
        grid.Columns["Samples"].FillWeight = 9;
        grid.Columns["Top3"].FillWeight = 8;
        grid.Columns["Top6"].FillWeight = 8;
        grid.Columns["Rank"].FillWeight = 9;
        grid.Columns["Recent20"].FillWeight = 16;
        grid.Columns["Recent50"].FillWeight = 16;
        grid.Columns["MaxMiss"].FillWeight = 13;
        grid.Columns["CurrentMiss"].FillWeight = 11;
        grid.Columns["Status"].FillWeight = 8;
        grid.CellFormatting += (_, e) =>
        {
            if (e.ColumnIndex != grid.Columns["Status"].Index || e.Value is not string status) return;
            e.CellStyle.Font = new Font(grid.Font, FontStyle.Bold);
            e.CellStyle.ForeColor = status switch
            {
                "领先" => Color.DarkGreen,
                "暂停" => Color.DarkRed,
                _ => Color.DimGray
            };
        };

        void LoadRows()
        {
            grid.Rows.Clear();
            foreach (V65ExperimentScoreboardRow row in V65ExperimentScoreboardService.Load())
            {
                grid.Rows.Add(row.Group, row.ModelName, row.Samples, row.Top3HitRate.ToString("P1"),
                    row.Top6HitRate.ToString("P1"), row.Samples == 0 ? "-" : row.AverageRank.ToString("F2"),
                    $"{row.Recent20Top3HitRate:P1} / {row.Recent20Top6HitRate:P1}",
                    $"{row.Recent50Top3HitRate:P1} / {row.Recent50Top6HitRate:P1}",
                    row.MaximumTop6Misses, row.CurrentTop6Misses, row.Status);
            }
        }

        refresh.Click += (_, _) => LoadRows();
        panel.Controls.Add(refresh);
        panel.Controls.Add(note);
        panel.Controls.Add(grid);
        LoadRows();
        return panel;
    }
}
