# WindowsIsland · 待办

## 已完成（2026-11-17）

- [x] **开机自启动** —— 托盘菜单「开机自启动」，写 `HKCU\...\Run`，无需管理员；
      启动时 `AutoStart.Sync()` 会按当前 exe 路径修复注册表项（换目录也生效）
- [x] **自定义颜色** —— 色块行末尾「＋」，打开 HSV 取色面板（`ColorPickerWindow`）；
      选完立即应用并写入 `AccentColor`
- [x] **通知提醒** —— 新通知到达时岛体左右抖动 + 主题色描边闪一下（`PlayNotificationAlert`）；
      开关存 `NotificationAlert`

## 仍待确认

- 「通知秀动」目前是**视觉**方案（PC 无马达）。如果想要声音提示，
  在 `PlayNotificationAlert()` 里加 `SystemSounds.Notification.Play()` 即可

---

# 交接备忘（踩过的坑）

## 单位问题（最坑）

- `ActualWidth` 是 **DIP**；`PointToScreen()` / `GetCursorPos()` 是**物理像素**
- 150% 缩放下差 1.5 倍 → “点 75% 变 100%”“越拖越偏”
- 光标交互一律用 `PointToScreen` 量边界，见 `MeasureTrack()`

## 鼠标交互

- 窗口是 `WS_EX_NOACTIVATE`，`CaptureMouse()` **静默失败**
- 拖动靠 16ms `DispatcherTimer` 轮询光标，`MouseLeftButtonUp` 收尾
- 必须有看门狗：光标 1.2 秒不动 → 强制结束拖动，否则 `_dragRow` 残留会永久挡住探测

## 通知

- `UserNotificationListener` 无 launch 参数 → 读 `wpndatabase.db` 的 `Notification.Payload`
  - 复制 db + `-wal` + `-shm` 到临时目录（原库被 WPN 占用）
  - `winsqlite3.dll` P/Invoke，无需 NuGet
  - `activationType=protocol` → `Process.Start(launch)`（QQ 可精确进会话）
- 通知中心（Win+N / 点时钟）在后台打不开，别再试

## 编辑工具

- PowerShell 批量替换中文注释**会丢换行** → 用 `edit`/`write` 工具改 C#
- 整段替换用行号切片更可靠

## 其他

- 亮度：DDC/CI 不可用（`0xC0262582`），走 PowerShell CIM
- 音量：CoreAudio，**每次调用新建 COM 对象**（跨线程复用会间歇失败）
- 内存：私有 ~63 MB，工作集 130 MB 左右