; ClipDesk 安装程序（Inno Setup 6）
;
; 用 ISCC.exe 编译：见 packaging/build-installer.ps1
;
; ── 装到哪里 ──────────────────────────────────────────────────────
;   PrivilegesRequired=lowest → 装进 %LocalAppData%\Programs\ClipDesk
;   **全程不弹 UAC**。程序本身不需要管理员权限（自启开关写的是 HKCU），
;   装到 Program Files 反而会多一次提权弹窗，对用户是纯粹的打扰。

#define AppName        "ClipDesk"
#define AppVersion     "0.1.1"
; 发布者写软件名本身，不写个人姓名 —— 这个字段会显示在
; 「应用和功能」列表和 UAC 提示里，属于对外的公开信息。
#define AppPublisher   "ClipDesk"
#define AppURL         "https://github.com/wangzuo20072022-ui/clipdesk"
#define AppExeName     "ClipDesk.exe"

[Setup]
AppId={{8F3A5C21-7B4E-4D9A-9E62-1C0D5A7B3E44}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}/issues
AppUpdatesURL={#AppURL}/releases
VersionInfoVersion={#AppVersion}

; ★ lowest = 不需要管理员权限，装到用户目录
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=no
AllowNoIcons=yes

OutputDir=..\artifacts
OutputBaseFilename=ClipDesk-Setup-v{#AppVersion}
SetupIconFile=..\src\ClipDesk\Assets\ClipDesk.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName} {#AppVersion}

; LZMA2/max 压得最狠；安装包本来就大（自带 .NET 运行时）
Compression=lzma2/max
SolidCompression=yes

WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; 卸载/安装前若程序在跑，提示用户关闭
CloseApplications=yes
RestartApplications=no

[Languages]
; 简体中文语言包是社区维护的（ChineseSimplified.isl），
; 不在 Inno 自带列表里。没装也不影响编译 —— build-installer.ps1 会
; 自动探测并去掉这一行，退回英文界面。
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[CustomMessages]
english.CreateDesktopIcon=Create a desktop shortcut
english.AutoStart=Start {#AppName} when Windows starts
english.LaunchAfterInstall=Launch {#AppName}
chinesesimplified.CreateDesktopIcon=创建桌面快捷方式
chinesesimplified.AutoStart=开机时自动启动 ClipDesk
chinesesimplified.LaunchAfterInstall=安装完成后启动 ClipDesk

[Tasks]
Name: "autostart"; Description: "{cm:AutoStart}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; staging 目录由 build-installer.ps1 产出，里面是自包含发布的全套文件
Source: "..\artifacts\staging-v0.1.1\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\卸载 {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
; ★ 开机自启写 HKCU，和程序内设置中心的 AutoStart.cs 用同一个位置、同一个值名，
;   这样两边不会打架 —— 用户在设置中心关掉，安装程序这边也是关的。
;   带引号是因为路径里可能有空格。
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: string; ValueName: "ClipDesk"; \
    ValueData: """{app}\{#AppExeName}"""; \
    Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchAfterInstall}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; 卸载前先结束进程，否则文件被占用删不掉
Filename: "{cmd}"; Parameters: "/C taskkill /IM {#AppExeName} /F"; Flags: runhidden; RunOnceId: "KillClipDesk"

[UninstallDelete]
; 用户配置（glass.json / clipdesk.log）**故意不删** ——
; 重装后用户调好的材质还在。想彻底清干净的用户可以自己删
; %LocalAppData%\ClipDesk 目录。
Type: filesandordirs; Name: "{app}"
