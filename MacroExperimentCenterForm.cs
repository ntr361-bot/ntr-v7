using System.Drawing;
using System.Windows.Forms;
using 六合分析软件.MacroReasoning;

namespace 六合分析软件;

/// <summary>Research-only UI. It is intentionally not registered in the production navigation.</summary>
public sealed class MacroExperimentCenterForm : Form
{
    private readonly MacroExperimentCenterModel model;
    private readonly DataGridView grid=new(){Dock=DockStyle.Top,Height=220,ReadOnly=true,AutoGenerateColumns=true,AllowUserToAddRows=false};
    private readonly NumericUpDown issue=new(){Minimum=1,Maximum=9999999,Width=110};
    private readonly TextBox question=new(){Width=420,Text="为什么没有调权"};
    private readonly TextBox answer=new(){Dock=DockStyle.Fill,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical};
    public MacroExperimentCenterForm(MacroExperimentCenterModel model)
    {
        this.model=model; Text="Macro 实验中心 · 问模型（旁路）"; Width=900;Height=620;StartPosition=FormStartPosition.CenterParent;
        var ask=new Button{Text="查询冻结审计",AutoSize=true}; ask.Click+=(_,_)=>Ask();
        var refresh=new Button{Text="刷新实验",AutoSize=true}; refresh.Click+=(_,_)=>RefreshRows();
        var bar=new FlowLayoutPanel{Dock=DockStyle.Top,Height=46,Padding=new Padding(8)};
        bar.Controls.AddRange([new Label{Text="期号",AutoSize=true,Padding=new Padding(0,7,0,0)},issue,
            new Label{Text="问题",AutoSize=true,Padding=new Padding(8,7,0,0)},question,ask,refresh]);
        Controls.Add(answer);Controls.Add(bar);Controls.Add(grid); RefreshRows();
    }
    private void RefreshRows()=>grid.DataSource=model.List().Select(x=>new{x.ExperimentId,x.Phase,Mode=x.Mode.ToString(),x.RegisteredAt,x.ProductionEnabled}).ToList();
    private void Ask()
    {
        string? experiment=grid.CurrentRow?.Cells[nameof(MacroExperimentRegistration.ExperimentId)].Value?.ToString()
            ??model.List().FirstOrDefault()?.ExperimentId;
        answer.Text=experiment is null?"暂无实验记录。":model.Ask(experiment,(long)issue.Value,question.Text);
    }
}
