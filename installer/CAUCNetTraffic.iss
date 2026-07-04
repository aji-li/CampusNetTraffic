#define MyAppName "CAUCNet Traffic"
#ifndef MyAppVersion
#define MyAppVersion "0.0.0"
#endif
#define MyAppPublisher "CAUCNet Traffic"
#define MyAppExeName "CAUCNetTraffic.exe"
#define MyServiceName "CampusNetTrafficTraffic"
#define MyServiceExeName "CampusNetTraffic.TrafficService.exe"
#define MyDriverName "CampusNetTrafficNet"
#define MyDriverFileName "CampusNetTrafficNet.sys"

[Setup]
AppId={{4C8F03D6-8B70-4F5E-A2C1-6A7B0C57F511}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppComments=CAUC 校园网流量助手，支持本机流量监测、校园网后台同步、托盘常驻和在线设备查看。
DefaultDirName={autopf}\CAUCNet Traffic
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
OutputDir=..\release
OutputBaseFilename=CAUCNetTraffic-v{#MyAppVersion}-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\Assets\app.ico
InfoBeforeFile=.\SetupInfo.zh-cn.txt
UninstallDisplayIcon={app}\{#MyAppExeName}
VersionInfoDescription=CAUC 校园网流量助手安装程序
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
CloseApplications=yes
RestartApplications=no
ArchitecturesAllowed=x64compatible

[Languages]
Name: "chinesesimplified"; MessagesFile: ".\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："; Flags: checkedonce

[Files]
Source: "..\dist\CAUCNetTraffic.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\CampusNetTraffic.TrafficService.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\CampusNetTrafficNet.sys"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\Assets\app.ico"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{cmd}"; Parameters: "/C sc stop {#MyDriverName} >nul 2>nul & sc delete {#MyDriverName} >nul 2>nul & exit /b 0"; Flags: runhidden waituntilterminated
Filename: "{cmd}"; Parameters: "/C sc stop {#MyServiceName} >nul 2>nul & sc delete {#MyServiceName} >nul 2>nul & exit /b 0"; Flags: runhidden waituntilterminated
Filename: "{cmd}"; Parameters: "/C if exist ""{app}\{#MyDriverFileName}"" sc create {#MyDriverName} type= kernel binPath= ""{app}\{#MyDriverFileName}"" start= demand DisplayName= ""CampusNetTraffic Network Counter Driver"""; Flags: runhidden waituntilterminated
Filename: "{cmd}"; Parameters: "/C if exist ""{app}\{#MyDriverFileName}"" sc start {#MyDriverName}"; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "create {#MyServiceName} binPath= ""{app}\{#MyServiceExeName}"" start= auto DisplayName= ""CampusNetTraffic Traffic Service"""; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "description {#MyServiceName} ""Provides low-overhead network byte counters for CAUCNet Traffic."""; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "start {#MyServiceName}"; Flags: runhidden waituntilterminated
Filename: "{app}\{#MyAppExeName}"; Description: "启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{cmd}"; Parameters: "/C sc stop {#MyServiceName} >nul 2>nul & sc delete {#MyServiceName} >nul 2>nul & exit /b 0"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveTrafficService"
Filename: "{cmd}"; Parameters: "/C sc stop {#MyDriverName} >nul 2>nul & sc delete {#MyDriverName} >nul 2>nul & exit /b 0"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveNetworkDriver"
