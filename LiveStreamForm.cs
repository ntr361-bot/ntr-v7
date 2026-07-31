using System;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;

namespace 六合分析软件
{
    /// <summary>
    /// 开奖直播独立窗口，支持自由缩放和最大化
    /// </summary>
    public partial class LiveStreamForm : Form
    {
        WebView2 webView;
        Panel topBar;
        Label urlLabel;

        public LiveStreamForm()
        {
            InitializeComponent();

            this.Text = "📺 开奖直播";
            this.Size = new Size(1200, 800);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.MinimumSize = new Size(800, 600);
            this.WindowState = FormWindowState.Normal;

            InitUI();
        }

        private void InitUI()
        {
            // 顶部工具栏
            topBar = new Panel();
            topBar.Dock = DockStyle.Top;
            topBar.Height = 40;
            topBar.BackColor = Color.FromArgb(30, 30, 46);
            this.Controls.Add(topBar);

            Label title = new Label();
            title.Text = "📺 开奖直播";
            title.Font = new Font("微软雅黑", 14, FontStyle.Bold);
            title.ForeColor = Color.White;
            title.Location = new Point(15, 8);
            title.AutoSize = true;
            topBar.Controls.Add(title);

            urlLabel = new Label();
            urlLabel.Text = "https://www.00853kkjj.cc/live";
            urlLabel.Font = new Font("微软雅黑", 9);
            urlLabel.ForeColor = Color.FromArgb(180, 180, 200);
            urlLabel.Location = new Point(130, 12);
            urlLabel.AutoSize = true;
            topBar.Controls.Add(urlLabel);

            // 刷新按钮
            Button btnRefresh = new Button();
            btnRefresh.Text = "🔄 刷新";
            btnRefresh.Font = new Font("微软雅黑", 10);
            btnRefresh.Size = new Size(70, 28);
            btnRefresh.Location = new Point(1100, 6);
            btnRefresh.BackColor = Color.FromArgb(0, 122, 204);
            btnRefresh.ForeColor = Color.White;
            btnRefresh.FlatAppearance.BorderSize = 0;
            btnRefresh.Click += (s, e) => Reload();
            topBar.Controls.Add(btnRefresh);

            // WebView2 - 填满剩余空间
            webView = new WebView2();
            webView.Dock = DockStyle.Fill;
            this.Controls.Add(webView);

            // 开始加载
            try
            {
                webView.Source = new Uri("https://www.00853kkjj.cc/live");
            }
            catch
            {
                Label err = new Label();
                err.Text = "无法加载直播页面，请检查网络连接";
                err.Font = new Font("微软雅黑", 14);
                err.ForeColor = Color.Red;
                err.Dock = DockStyle.Fill;
                err.TextAlign = ContentAlignment.MiddleCenter;
                this.Controls.Add(err);
            }
        }

        private void Reload()
        {
            try
            {
                webView.Reload();
            }
            catch { }
        }
    }
}
