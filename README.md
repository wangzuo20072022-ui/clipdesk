# ClipDesk

ClipDesk 是一款常驻 Windows 桌面的剪贴板历史工具。它把最近复制的文本保存在内存中，用轻量的玻璃界面快速取回。

当前版本：**v0.1.1（安装版）**

## 下载与安装

从 Releases 下载 `ClipDesk-Setup-v0.1.1.exe`，双击安装。安装程序不需要管理员权限，会把 ClipDesk 装到你的用户目录（`%LocalAppData%\Programs\ClipDesk`），并创建开始菜单快捷方式。

> **安装时 Windows 可能弹出蓝色的「Windows 已保护你的电脑」提示。** 这是正常的 —— 本版本没有购买代码签名证书，Windows 对任何未签名的安装包都会给这个提示。点「更多信息」→「仍要运行」即可继续。这不是病毒警告，是"我不认识这个发布者"的意思。

安装过程中可以勾选「开机时自动启动 ClipDesk」。装完后从开始菜单启动，屏幕顶部会出现一条很窄的收缩条，右下角托盘会出现 ClipDesk 图标。

ClipDesk 只支持 Windows 11 22H2（build 22621）及以上的 x64 电脑。

## 使用方式

- **托盘图标**（右下角）：左键或右键单击 → 弹出菜单，可以打开设置中心、开关开机自启、退出程序。
- 鼠标移到收缩条上并停留约 0.35 秒：展开剪贴板面板。
- 面板显示最近 20 条文本记录；点击一条会写回剪贴板，不会替你抢焦点，回到原窗口按 `Ctrl+V` 即可粘贴。
- 按住 `Alt+V`：在鼠标位置打开八方向网格；向某个方向移动后松开，粘贴对应记录；回到中心或按 `Esc` 取消。
- `Ctrl+Alt+G`：打开设置中心（等同托盘菜单里的「设置…」）。
- `Ctrl+Alt+Q`：退出 ClipDesk。

## 设置中心

- **常规**：开机时自动启动 ClipDesk（勾选即写入当前用户的启动项，不需要管理员权限）。
- **玻璃材质**：调节条子、面板、网格的玻璃效果参数，改完点「保存」。

配置存在 `%LocalAppData%\ClipDesk\`，卸载 ClipDesk **不会**删除这个目录，重装后你调好的材质还在。

## 隐私与限制

剪贴板历史只保存在内存中，退出程序后清空，不上传、不共享。材质会根据桌面背景和显示器缩放产生视觉差异。首版已知限制包括混合 DPI 多显示器、远程桌面以及部分旧版 DWM 功能的兼容性尚未全面验证。

## 从源码构建

环境要求：Windows 11 22H2+、.NET 8 SDK（仓库中的 `global.json` 会锁定 SDK 版本）。

```powershell
dotnet build src/ClipDesk/ClipDesk.csproj
dotnet run --project src/ClipDesk/ClipDesk.csproj
powershell -ExecutionPolicy Bypass -File packaging/build-installer.ps1
```

`build-installer.ps1` 会先做自包含发布，再调用 Inno Setup 编译出 `artifacts/ClipDesk-Setup-v<版本>.exe`。需要先装 [Inno Setup 6 或 7](https://jrsoftware.org/isdl.php)（免费）。

发布包只包含正式应用和必要说明，不包含验证探针、测试源码或开发截图。

项目采用 MIT License；第三方移植代码见 [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)。
