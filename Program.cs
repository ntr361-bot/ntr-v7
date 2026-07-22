using System;
using System.Windows.Forms;

namespace 六合分析软件
{
    internal static class Program
    {

        [STAThread]
        static void Main(string[] args)
        {

            ApplicationConfiguration.Initialize();


            // 初始化数据库
            DatabaseHelper.InitializeDatabase();

            // 自动备份数据库
            string backupResult = DatabaseBackupService.Backup();
            Console.WriteLine($"[程序] 备份：{backupResult}");

            // 为旧数据补全 SpecialNumber/SpecialZodiac 字段
            int migrated = DatabaseHelper.MigrateOldData();
            if (migrated > 0)
            {
                Console.WriteLine($"[程序] 旧数据迁移完成：{migrated} 条记录已补全特码字段");
            }



            // 启动主界面
            if (Array.Exists(args, a => a == "--v6-report"))
            {
                Console.OutputEncoding = System.Text.Encoding.UTF8;
                Console.WriteLine(V6UpgradeReportService.GenerateReport(500));
                return;
            }

            Application.Run(new Form1());

        }

    }
}
