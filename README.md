# CAUCNet Traffic

CAUCNet Traffic 是一个面向 Windows 的 CAUC 校园网流量助手，用于查看本机实时流量、同步校园网后台数据、查看在线设备，并提供托盘常驻和轻量悬浮流量窗。

## 功能

- 实时显示本机下载/上传速度
- 统计本次打开 APP 后的总流量
- 同步 CAUC Dr.COM 后台的已用流量、可用流量、余额、套餐和计费周期
- 自动复用校园网登录会话，过期后再提醒重新登录
- 查看当前在线设备列表，可一键注销设备
- 今日/本月本机流量统计
- 最近 10 分钟趋势图、最近 7 天流量柱状图
- 托盘常驻，右键菜单显示状态、用量和网速
- 可选任务栏上方迷你流量窗
- 开机自启、阈值提醒、日志导出
- 本地设置和会话数据存放在用户目录，不保存明文密码

## 运行环境

- Windows 10 / Windows 11
- WebView2 Runtime

> Windows 11 通常已经自带 WebView2 Runtime。如果打不开内嵌网页登录页，需要安装 Microsoft Edge WebView2 Runtime。

## 给别人使用时发哪些文件

最简单：只发这个文件即可：

```text
dist\CAUCNetTraffic.exe
```

不要发送这些文件/目录：

```text
dist\CAUCNetTraffic.pdb
dist\CampusNetTraffic.exe.WebView2\
dist\*.xml
```

说明：

- `.pdb` 是调试符号，普通用户不需要。
- `.xml` 是库文档文件，普通用户不需要。
- `CampusNetTraffic.exe.WebView2` 是本机 WebView2 缓存/用户数据目录，不应该发给别人。
- 如果使用 Syncfusion 图表并希望去掉授权提示，可把 `syncfusion-license.txt` 和 exe 放在同一个文件夹；也可以让用户放到 `%LOCALAPPDATA%\CampusNetTraffic\syncfusion-license.txt`。

## 使用方式

1. 运行 `CAUCNetTraffic.exe`
2. 点击“网页登录”
3. 在内嵌页面完成校园网登录
4. 登录成功后点击“同步校园网”
5. 后续打开 APP 会自动复用登录状态，过期后才需要重新登录

## 常用设置

- “开机自动启动”：在设置页打开或关闭
- “显示任务栏上方迷你流量窗”：默认关闭，可在设置页或托盘菜单打开
- “清除校园网登录状态”：会清除本地保存的会话和 WebView cookie
- “导出诊断日志”：出现问题时可导出日志用于排查

## 本地数据位置

程序数据默认保存在：

```text
%LOCALAPPDATA%\CampusNetTraffic
```

其中包括：

- `settings.json`：本地设置
- `traffic.db`：本机流量统计数据库
- `app.log`：运行日志
- `crash.log`：崩溃日志
- `syncfusion-license.txt`：可选 Syncfusion 授权文件

## 开发与发布

构建：

```powershell
dotnet build
```

发布单文件 exe：

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist
```

发布完成后主程序位于：

```text
dist\CAUCNetTraffic.exe
```

## 项目说明

本项目当前主要面向 CAUC 校园网环境，校园网登录和后台同步逻辑针对 `https://www.cauc.edu.cn/Self/dashboard` 实现。
