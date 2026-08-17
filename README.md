# Stopwatch Overlay

Windows 桌面悬浮计时器，可始终置顶显示在录屏、直播、演示或全屏应用上方。

![Stopwatch Overlay Controller](controller-window.png)

## 功能

- 四种模式：秒表、时钟、倒计时、时间码。
- 支持多显示器，可显示到单个或全部屏幕。
- 开始计时（按钮或 `Win+F5`）后自动显示悬浮窗。
- 悬浮窗可拖动，并会按显示器记住上次的精确位置。
- 自定义文字颜色、描边、字体、大小与背景透明度。
- 倒计时支持时、分、秒输入、暂停/继续，以及快捷时长按钮；右键快捷按钮可修改其分钟数。
- 支持鼠标穿透、REC 指示器、光环边框、分段计时和全局快捷键。
- 支持 English / 中文 界面切换。
- 设置自动保存至 `%LocalAppData%\StopwatchOverlay\settings.json`，升级或单文件发布后仍会保留。

## 快捷键

| 快捷键 | 操作 |
| --- | --- |
| `Win+F5` | 开始 / 停止 |
| `Win+F6` | 重置 |
| `Win+F7` | 显示 / 隐藏悬浮窗 |
| `Win+F8` | 记录分段时间 |

## 使用方法

1. 运行 `StopwatchOverlay.exe`。
2. 选择计时模式；倒计时可直接输入时、分、秒。
3. 点击开始，悬浮窗会自动显示。
4. 拖动悬浮窗到所需位置；下次开始会恢复该位置。
5. 展开 **Settings / 设置**，选择语言、屏幕、外观和其他选项。
6. 右键倒计时快捷按钮即可编辑对应的快捷分钟数。

## 构建

项目使用 .NET 10 和 WPF。请安装 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)，然后在仓库根目录执行：

```powershell
dotnet build .\StopwatchOverlay\StopwatchOverlay.csproj -c Release
dotnet publish .\StopwatchOverlay\StopwatchOverlay.csproj -c Release
```

发布后的单文件程序位于：

```text
StopwatchOverlay\bin\Release\net10.0-windows\win-x64\publish\StopwatchOverlay.exe
```

运行发布版的电脑需要安装对应的 .NET 10 Desktop Runtime。

## 开发说明

项目结构和贡献说明见 [DEVELOPERS.md](DEVELOPERS.md)。

## 许可证

[MIT](LICENSE)
