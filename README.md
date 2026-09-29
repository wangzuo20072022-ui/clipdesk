# ClipDesk

ClipDesk 是一款常驻 Windows 桌面的剪贴板历史工具。它把最近复制的文本保存在内存中，用轻量的玻璃界面快速取回。

当前版本：**v0.1.0（早期可用版）**

## 下载与启动

从 Releases 下载 `ClipDesk-win-x64-v0.1.0.zip`，解压到任意用户可写目录，然后双击 `ClipDesk.exe`。这是 .NET 8 自包含包，不需要另外安装 .NET，也不需要管理员权限。

ClipDesk 只支持 Windows 11 22H2（build 22621）及以上的 x64 电脑。首次启动后，屏幕顶部会出现一条很窄的收缩条。

## 使用方式

- 鼠标移到收缩条上并停留约 0.35 秒：展开剪贴板面板。
- 面板显示最近 20 条文本记录；点击一条会写回剪贴板，不会替你抢焦点，回到原窗口按 `Ctrl+V` 即可粘贴。
- 按住 `Alt+V`：在鼠标位置打开八方向网格；向某个方向移动后松开，粘贴对应记录；回到中心或按 `Esc` 取消。
- `Ctrl+Alt+G`：打开玻璃材质调节面板。
- `Ctrl+Alt+Q`：退出 ClipDesk。

## 隐私与限制

剪贴板历史只保存在内存中，退出程序后清空，不上传、不共享。材质会根据桌面背景和显示器缩放产生视觉差异。首版已知限制包括混合 DPI 多显示器、远程桌面以及部分旧版 DWM 功能的兼容性尚未全面验证。

## 从源码构建

环境要求：Windows 11 22H2+、.NET 8 SDK（仓库中的 `global.json` 会锁定 SDK 版本）。

```powershell
dotnet build src/ClipDesk/ClipDesk.csproj
dotnet run --project src/ClipDesk/ClipDesk.csproj
powershell -ExecutionPolicy Bypass -File packaging/publish-win-x64.ps1
```

发布脚本会在干净目录生成 Windows x64 自包含 ZIP。发布包只包含正式应用和必要说明，不包含验证探针、测试源码或开发截图。

项目采用 MIT License；第三方移植代码见 [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)。
