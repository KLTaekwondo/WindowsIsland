# 灵动岛 WindowsIsland

一个仿 Apple 灵动岛的 Windows 悬浮小组件：顶部小胶囊，鼠标悬停展开，滚轮进入二级面板。

> 用 WPF / .NET 9 写成，单进程、无 WinForms 依赖、不需要管理员权限。

## 功能

| 区域 | 说明 |
|---|---|
| **收起态** | 胶囊里显示时间；有媒体时左侧三根随音频跳动的均衡器柱，暂停时压平 |
| **展开态** | 时钟卡片 / 媒体卡片（封面、歌名、歌手、播放控制），底部 4px 进度线 |
| **二级面板** | 「通知 / 快捷 / 设置」三页，滚轮翻页，底部滚轮到底再滚一次收起 |
| **通知页** | 读取系统通知列表（可滚动），图标 + 应用名 + 时间 + 标题 + 正文；**点击可精确打开原会话**（读 `wpndatabase.db` 的 toast 激活参数）；空列表显示中央大字 |
| **快捷页** | 自定义启动器磁贴，点「＋」添加程序/快捷方式，右键移除，图标取 Shell 图标并按主色染色 |
| **设置页** | 音量 / 亮度滑条（点击、拖动、滚轮），6 种主题色 + 自选 HSV 取色，玻璃质感染色 |
| **指示** | 左侧卡片环形 CPU / 内存 / 电量（低电量转红）+ 本程序内存 |
| **外观** | 纯黑 / 液态玻璃两套surface，托盘菜单可切换 |
| **托盘** | 显示、悬停展开、始终置顶、液态玻璃、开机自启动、重启、退出 |

## 快捷键

| 组合 | 作用 |
|---|---|
| `Ctrl + Alt + M` | 展开 / 收起二级面板 |
| `Ctrl + Alt + I` | 显示 / 隐藏 |
| `Ctrl + Alt + Q` | 退出 |

## 目录结构

```
WindowsIsland/
├── src/WindowsIsland/          # 工程
│   ├── MainWindow.xaml(.cs)    # 主窗口与全部 UI 逻辑
│   ├── App.xaml(.cs)           # 入口、单实例、软件渲染
│   ├── Interop/                # Win32 / COM 封装（音频、亮度、原生 API）
│   ├── Media/                  # GSMTC 媒体读取、LRC 歌词
│   ├── Notifications/          # 通知读取、toast 激活、wpndatabase 解析
│   ├── Shell/                  # 托盘、自启动、前台窗口监听、系统指标
│   └── UI/                     # 圆角裁剪、取色面板
├── .github/workflows/build.yml # CI：构建 + tag 自动发 Release
├── WindowsIsland.sln
├── README.md / LICENSE / TODO.md
```

## 构建

需要 **.NET 9 SDK** 与 Windows 10 1809+。

```powershell
dotnet build src/WindowsIsland/WindowsIsland.csproj
dotnet run --project src/WindowsIsland/WindowsIsland.csproj
```

发布免安装单文件：

```powershell
dotnet publish src/WindowsIsland/WindowsIsland.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -o dist/single
```

## 技术要点

- 分层透明窗（`WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`），岛外区域返回 `HTTRANSPARENT` 实现鼠标穿透
- 圆角用自定义几何裁剪（`IslandClipConverter`），圆角半径随吸顶状态动态计算（`IslandRadiusConverter`）
- 媒体走 WinRT GSMTC（枚举所有会话、优先正在播放的），音频走 CoreAudio，亮度走 WMI（DDC/CI 在部分内屏不可用）
- 通知激活：解析 `%LOCALAPPDATA%\Microsoft\Windows\Notifications\wpndatabase.db`
  的 `Notification.Payload`（用系统自带 `winsqlite3.dll` 读取），`activationType=protocol` 时直接执行 `launch`
- 软件渲染（`RenderMode.SoftwareOnly`）以降低显存/工作集

## 已知限制

- 只读得到「注册了系统媒体会话（SMTC）」的播放器；部分客户端只报标题不报进度
- 未上报播放位置时按真实时间外推，进度为推算值
- 亮度依赖 WMI；部分外接显示器不支持
- 通知图标：优先应用 logo，取不到时回退到进程 exe 图标

## 许可

[Apache License 2.0](LICENSE)
