namespace 六合分析软件
{
    partial class HistoryForm
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
            // HistoryForm
            // 
            this.ClientSize = new System.Drawing.Size(800, 500);
            this.Name = "HistoryForm";
            this.Text = "历史开奖数据";
            this.ResumeLayout(false);
        }
    }
}
