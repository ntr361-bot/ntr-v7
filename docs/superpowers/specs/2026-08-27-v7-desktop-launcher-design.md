# V7 桌面启动器

桌面快捷方式固定启动 V7 主目录的 PowerShell 启动器。启动器检测 V7 源文件是否比 Release 可执行文件新；需要时执行 `dotnet build -c Release`，仅在构建成功后启动 Release 程序。构建失败时保留错误窗口而不运行旧程序。

同时，实际运行的 V7 程序的 AI预测历史只显示 `V6.5 AutoLearning`，不显示后台继续保存的 `V7 AutoLearning`。
