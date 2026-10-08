# ClipDesk 发布流程

## 出安装包（正式渠道）

1. 装 [Inno Setup 6 或 7](https://jrsoftware.org/isdl.php)（免费）。
2. 运行 `packaging/build-installer.ps1`。
3. 脚本做两件事：先把 ClipDesk 自包含发布到 `artifacts/staging-v<版本>/app`，再调 `ISCC.exe` 编译。
4. 产出 `artifacts/ClipDesk-Setup-v<版本>.exe`，把这个文件传到 GitHub Releases。

安装包用 `PrivilegesRequired=lowest`，装到 `%LocalAppData%\Programs\ClipDesk`，**全程不弹 UAC**。

## 发版前要改的地方

版本号散在三个文件里，改的时候三处一起改：

| 文件 | 位置 |
|---|---|
| `src/ClipDesk/ClipDesk.csproj` | `<Version>` |
| `packaging/installer.iss` | `#define AppVersion` |
| `packaging/build-installer.ps1` | `$version` |

`installer.iss` 里的 `[Files]` 段路径带版本号（`staging-v0.1.1\app\*`），也要跟着改。

## 关于代码签名

当前**没有**购买代码签名证书，所以用户首次运行 setup.exe 会看到 Windows 的「已保护你的电脑」提示。这是预期行为，必须在 Release notes 里说明清楚（见 `RELEASE-NOTES-v0.1.1.md` 的对应段落）。
