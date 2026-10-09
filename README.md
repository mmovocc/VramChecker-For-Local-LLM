# GPU VRAM Checker（local_llm_vram_console）

Windows 桌面显存浮窗 + 启动控制台：实时显示各进程显存占用（D3DKMT），一键清理占显存的应用、预检 llama-server 配置能否放下、启动并监控本地 LLM。可选液态玻璃背景（D3D11 + DXGI Desktop Duplication + DirectComposition + HLSL）。

当前版本：**0.1.0**（2026-10-08），发布说明见 [docs/RELEASE-NOTES-0.1.0.md](docs/RELEASE-NOTES-0.1.0.md)。

## 构建与运行

要求：Windows 10 2004+（推荐 Windows 11）、D3D11 FL 11.0 显卡、.NET Framework 4.8、Visual Studio C++ Build Tools + Windows SDK。不需要 NuGet。

```powershell
.\build.ps1            # 输出到 bin\，同时运行原生单元测试
.\bin\GpuVramWidget.exe            # 默认：纯色卡片，无 Shader、无桌面捕获
.\bin\GpuVramWidget.exe --glass    # 液态玻璃模式
.\bin\GpuVramWidget.exe --diagnostic
```

运行时需要同目录下：`GpuVramWidget.exe`、`LiquidGlass.WinForms.dll`、`LiquidGlass.Native.dll`、`LiquidGlass.hlsl`。
设置、校准数据和 GPU 偏好快照保存在 `%APPDATA%\GpuVramWidget`。

## 目录结构

| 路径 | 内容 |
| --- | --- |
| `GpuVramWidget.cs` | 入口、命令行参数、D3DKMT 显存采集 |
| `GlassRenderer.cs` | 浮窗到玻璃框架的配置适配层 |
| `ui/` | 点阵风格界面：`WidgetForm`、`LaunchPanel`、`DotUi`、`DotChrome`、`AppMenu` |
| `launcher/` | 清理 / 显存预检 / 估算 / 校准 / 启动 / 监控 llama-server、GPU 偏好 |
| `framework/` | 可复用的 `LiquidGlass.WinForms` 组件（见 [framework/README.md](framework/README.md)） |
| `native/` | `LiquidGlass.Native.dll`（C++）与 `LiquidGlass.hlsl` |
| `samples/` | `GlassDemo.cs`：不依赖显存逻辑的框架接入示例 |
| `tests/` | 单元/集成/截图检查（见 [tests/README.md](tests/README.md)） |
| `docs/` | `HISTORY.md`：完整的迭代与验证记录（原 README）；发布说明 |
| `releases/` | 当前发布包（runtime / source 及 zip；gitignore，二进制作为 GitHub Release 附件上传） |
| `bin/` | 构建输出（gitignore） |
| `_archive/` | 历史迭代版本与测试产物（gitignore，见 [_archive/README.md](_archive/README.md)） |

## 主要功能

- **浮窗**：已用显存点阵环、FREE / SERVER / CTX / MAX CTX 统计、2 分钟显存曲线、每进程占用条；底部 `CLEAN` · `▶ LAUNCH` · `•••`。
- **清理 → 启动 → 监控**：WM_CLOSE 优雅关闭清理名单中的应用，宽限期后强制结束；在 Windows Terminal 标签页中启动 llama-server，轮询 `/health` 至就绪；可恢复被关闭的应用。
- **显存预检**：基于 `llama-fit-params` 与模型文件的免测试估算（`launcher/Estimator.cs`），可选 CALIBRATE 实测校准；状态显示 Fits now / Tight / Would spill。
- **GPU 偏好**：管理 Windows 每应用 GPU 偏好，启动时快照，可一键恢复。
- **材质**：纯色卡片（默认）、深色 / 浅色 / 旧版液态玻璃（`--material dark|light|legacy`）。

各功能的设计细节、性能优化、命令行参数和验证记录见 [docs/HISTORY.md](docs/HISTORY.md)。

## 许可

`ui/` 表现层移植自 karminski 的 dot-matrix-dashboard-ui（CC BY-NC-SA 4.0，非商业、署名、相同方式共享），见 [ui/THIRD-PARTY-NOTICES.md](ui/THIRD-PARTY-NOTICES.md)。
