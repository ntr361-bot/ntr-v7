using System;
using System.Drawing;
using System.Windows.Forms;

namespace 六合分析软件
{
    /// <summary>
    /// 数据检测中心 — 期号连续性、号码有效性、生肖校验
    /// </summary>
    public partial class DataCheckForm : Form
    {
        TextBox reportBox;
        Button btnRun;
        Label statusLabel;

        public DataCheckForm()
        {
            InitializeComponent();
            this.Text = "数据检测中心";
            this.Size = new Size(750, 600);
            this.StartPosition = FormStartPosition.CenterParent;
            InitUI();
        }

        void InitUI()
        {
            Panel topBar = new Panel { Dock = DockStyle.Top, Height = 50, BackColor = Color.FromArgb(30, 30, 46) };
            Label title = new Label
            {
                Text = "🔍 数据检测中心",
                Font = new Font("微软雅黑", 16, FontStyle.Bold),
                ForeColor = Color.White, Location = new Point(20, 10), AutoSize = true
            };
            topBar.Controls.Add(title);
            Controls.Add(topBar);

            btnRun = new Button
            {
                Text = "▶ 执行全面检测",
                Font = new Font("微软雅黑", 11, FontStyle.Bold),
                Size = new Size(160, 40), Location = new Point(20, 65),
                BackColor = Color.FromArgb(0, 122, 204), ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            btnRun.FlatAppearance.BorderSize = 0;
            btnRun.Click += (s, e) => RunCheck();
            Controls.Add(btnRun);

            statusLabel = new Label
            {
                Location = new Point(200, 75), AutoSize = true,
                Font = new Font("微软雅黑", 10), ForeColor = Color.Gray,
                Text = "点击按钮执行数据质量检测"
            };
            Controls.Add(statusLabel);

            reportBox = new TextBox
            {
                Location = new Point(20, 120), Size = new Size(690, 420),
                Multiline = true, ScrollBars = ScrollBars.Vertical,
                Font = new Font("Consolas", 10),
                ReadOnly = true, BackColor = Color.FromArgb(245, 245, 245)
            };
            Controls.Add(reportBox);
        }

        void RunCheck()
        {
            btnRun.Enabled = false;
            statusLabel.Text = "⏳ 检测中...";
            Application.DoEvents();

            try
            {
                reportBox.Text = DataCheckService.GetQualityReport();
                statusLabel.Text = "✅ 检测完成";
            }
            catch (Exception ex)
            {
                reportBox.Text = $"检测失败：{ex.Message}";
                statusLabel.Text = "❌ 检测失败";
            }

            btnRun.Enabled = true;
        }
    }
}
