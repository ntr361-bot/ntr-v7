using System.Drawing;
using System.Windows.Forms;

namespace 六合分析软件;

/// <summary>Only reads the completed historical experiment report. Does not instantiate a learner.</summary>
public sealed class IndependentLearningHistoryForm : Form
{
    public IndependentLearningHistoryForm(string dataRoot)
    {
        Text = "独立学习历史实验 · 每期学习成果";
        Size = new Size(1250, 720);
        MinimumSize = new Size(800, 480);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.White;
        var summary = new Label { Dock = DockStyle.Top, Height = 78, Padding = new Padding(15),
            Font = new Font("微软雅黑", 10), ForeColor = Color.FromArgb(28,55,88) };
        var grid = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
            AllowUserToDeleteRows = false, RowHeadersVisible = false, BackgroundColor = Color.White,
            ScrollBars = ScrollBars.Both, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect, EnableHeadersVisualStyles = false,
            ColumnHeadersHeight = 36, Font = new Font("微软雅黑", 9) };
        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(30,66,109);
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(213,232,249);
        grid.DefaultCellStyle.SelectionForeColor = Color.Black;
        grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(244,248,253);
        string[] titles = {"期号","实际生肖","预测Top3","预测Top6","实际名次","Top3结果","Top6结果",
            "50期权重：前→后","100期权重：前→后","全历史权重：前→后","快照来源","快照生成时间","来源证据"};
        int[] widths = {90,80,130,205,85,85,85,180,180,180,220,220,480};
        for(int i=0;i<titles.Length;i++) { grid.Columns.Add("c"+i,titles[i]);grid.Columns[i].Width=widths[i]; }
        Controls.Add(grid);
        Controls.Add(summary);
        try
        {
            var report = IndependentLearningHistoricalTraining.LoadReport(dataRoot);
            if(report is null) { summary.Text = "尚未完成历史实验。";return; }
            int n=report.Entries.Length;
            summary.Text = $"历史实验预训练 · {n}期 · Top3 {report.Entries.Count(e=>e.Top3Hit)}/{n} · Top6 {report.Entries.Count(e=>e.Top6Hit)}/{n}\n权重变化用于下一期。此处成绩为历史顺序实验，非留出集成绩。左右滚动可查看权重和来源。";
            foreach(var e in report.Entries.OrderByDescending(e=>e.Issue))
            {
                string Change(string key) => $"{e.BeforeWeights[key]:P3} → {e.AfterWeights[key]:P3}";
                int row=grid.Rows.Add(e.Issue,e.ActualZodiac,string.Join(",",e.Top3),string.Join(",",e.Top6),e.ActualRank,
                    e.Top3Hit?"命中":"未命中",e.Top6Hit?"命中":"未命中",Change("v65-50"),Change("v65-100"),Change("v65-all"),
                    e.EvidenceSource,e.SourceGeneratedAt.ToString("yyyy-MM-dd HH:mm:ss zzz"),e.EvidenceReference);
                grid.Rows[row].Cells[5].Style.ForeColor=e.Top3Hit?Color.ForestGreen:Color.Firebrick;
                grid.Rows[row].Cells[6].Style.ForeColor=e.Top6Hit?Color.ForestGreen:Color.Firebrick;
            }
        }
        catch(Exception ex) { summary.Text="历史实验报告读取失败："+ex.Message; }
    }
}
