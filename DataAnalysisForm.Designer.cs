namespace 六合分析软件
{
    partial class DataAnalysisForm
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
            // DataAnalysisForm
            // 
            this.ClientSize = new System.Drawing.Size(900, 650);
            this.Name = "DataAnalysisForm";
            this.Text = "数据分析模块";
            this.ResumeLayout(false);
        }
    }
}
