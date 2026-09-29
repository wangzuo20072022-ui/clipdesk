# ClipDesk v0.1.0 · 绿色版发布

1. 运行 `packaging/publish-win-x64.ps1`。
2. 脚本执行 Release 发布并在 `artifacts/` 生成 `ClipDesk-win-x64-v0.1.0.zip`。
3. 解压后双击 `ClipDesk.exe` 即可运行；包为 .NET 8 自包含 x64，不需要管理员权限。

发布包是多文件形式，以保证 WPF 资源和系统互操作兼容。脚本不会发布验证项目、测试、参考资料或开发产物。
