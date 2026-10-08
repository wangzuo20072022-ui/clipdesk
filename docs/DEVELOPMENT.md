# 开发与发布

## 目录职责

- `src/ClipDesk/`：唯一的正式 WPF 应用源码。
- `docs/`：用户功能与开发说明。
- `packaging/`：可重复的 Windows x64 发布脚本与安装脚本。
- `verify/`：历史技术验证材料，**已从公开树移除**，归档在本机 `D:\ClipDesk-local-archive\verify-2026-09-29`，历史提交中仍可追溯。
- `reference/`、`tests/`：只在本地存在，不入库。

正式代码的 Win32 调用集中在 `NativeMethods.cs`、`ScreenCapture.cs` 和 `CursorHider.cs` 等平台边界；剪贴板历史和方向选择保持为独立逻辑类型，便于后续测试和替换。

## 本地构建

```powershell
dotnet restore src/ClipDesk/ClipDesk.csproj
dotnet build src/ClipDesk/ClipDesk.csproj --configuration Release
```

## 出安装包（正式渠道）

```powershell
powershell -ExecutionPolicy Bypass -File packaging/build-installer.ps1
```

需要先装 [Inno Setup 6 或 7](https://jrsoftware.org/isdl.php)（免费）。脚本先做自包含发布到 `artifacts/staging-v<版本>/app`，再调 `ISCC.exe` 编译出 `artifacts/ClipDesk-Setup-v<版本>.exe`。

安装包用 `PrivilegesRequired=lowest`，装到 `%LocalAppData%\Programs\ClipDesk`，全程不弹 UAC。包内包含 `ClipDesk.exe`、运行所需 .NET 文件、README、MIT 许可证和第三方声明；不包含 `verify/`、测试源码、截图、日志、`bin/obj` 或本机配置。

## 出绿色 ZIP（历史渠道，v0.1.0 用）

```powershell
powershell -ExecutionPolicy Bypass -File packaging/publish-win-x64.ps1
```

v0.1.1 起不再使用这条渠道，脚本保留仅作参考。

## 发版前检查

1. `dotnet build` 无警告、无错误；
2. 安装包里入口是 `ClipDesk.exe`，且没有测试/探针目录；
3. 真实安装一次：开始菜单出现、托盘图标出现、设置中心可开、卸载后目录和 Run 键都清干净；
4. 实测收缩条、面板、滚轮、`Alt+V`、粘贴、托盘菜单和 `Ctrl+Alt+Q`；
5. 视觉验证不能只看编译结果，还要在真实 Windows 桌面观察玻璃和 DPI 行为。

验证探针保留在历史提交中，用于追溯研发过程；它们不随正式版本发布。
