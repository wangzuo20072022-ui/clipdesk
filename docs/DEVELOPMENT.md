# 开发与发布

## 目录职责

- `src/ClipDesk/`：唯一的正式 WPF 应用源码。
- `docs/`：用户功能与开发说明。
- `packaging/`：可重复的 Windows x64 发布脚本。
- `verify/`：历史技术验证材料。它不属于最新公开产品树，也不进入发布 ZIP；旧材料在本机 `D:\ClipDesk-local-archive\verify-2026-09-29` 留有归档。
- `reference/`：只读参考资料，不参与构建。

正式代码的 Win32 调用集中在 `NativeMethods.cs`、`ScreenCapture.cs` 和 `CursorHider.cs` 等平台边界；剪贴板历史和方向选择保持为独立逻辑类型，便于后续测试和替换。

## 本地构建

```powershell
dotnet restore src/ClipDesk/ClipDesk.csproj
dotnet build src/ClipDesk/ClipDesk.csproj --configuration Release
```

## 生成绿色 ZIP

```powershell
powershell -ExecutionPolicy Bypass -File packaging/publish-win-x64.ps1
```

脚本只发布 `src/ClipDesk/ClipDesk.csproj`，使用 `win-x64` 和 `SelfContained=true`，不启用 trimming，先生成干净 staging 目录，再生成多文件自包含 ZIP。包内包含 `ClipDesk.exe`、运行所需 .NET 文件、README、MIT 许可证和第三方声明；不包含 `verify/`、测试源码、截图、日志、`bin/obj` 或本机配置。

发布前应检查：

1. `dotnet build` 无警告、无错误；
2. ZIP 中的入口名是 `ClipDesk.exe`，且没有测试/探针目录；
3. 在不同当前工作目录解压后可以双击启动；
4. 实测收缩条、面板、滚轮、`Alt+V`、粘贴和 `Ctrl+Alt+Q`；
5. 视觉验证不能只看编译结果，还要在真实 Windows 桌面观察玻璃和 DPI 行为。

验证探针保留在历史提交中，用于追溯研发过程；它们不随正式版本发布。
