namespace 六合分析软件
{
    partial class StatisticsChartForm
    {
        private System.ComponentModel.IContainer components = null;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            this.SuspendLayout();
            // 
            // StatisticsChartForm
            // 
            this.ClientSize = new System.Drawing.Size(900, 650);
            this.Name = "StatisticsChartForm";
            this.Text = "统计图表模块";
            this.ResumeLayout(false);
        }
    }
}
