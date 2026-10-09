# 交互桌面集成验证

在正常 Windows 用户会话执行，勿使用禁止桌面捕获的沙箱。

```powershell
.\build.ps1 -OutputDirectory artifacts\test
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /platform:x64 /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /out:artifacts\test\VisualCheck.exe tests\VisualCheck.cs
.\artifacts\test\VisualCheck.exe
```

测试窗口绘制动态背景并启动浮窗的 `--diagnostic` 模式。第 120 帧保存 380×206 预览，第 160 帧移动并调整窗口为 520×280，第 240 帧保存第二张预览，第 300 帧退出。测试背景随子进程退出而关闭。

背景在自身第 60 次计时后静止，用于验证无变化时跳过渲染。上述“帧”指 UI 状态检查次数，实际 Draw/Present 数量见日志的 draws，渲染线程独立运行；自动模式下 skipped 应大于零。cropBytes 覆盖窗口、采样边距和额外移动缓存，屏幕边缘会裁小。前景采集仍定期更新。

验收：

- `glass.log` 的新记录中两次 `capture status` 都为 `0`。
- 两张 `glass-preview-*.bmp` 的尺寸分别正确，背景随动画变化，没有重复浮窗/文字残影。
- 图例、数值和标题未被折射；两种尺寸下边缘无黑色矩形。
- 右键和拖动可用；勿点击真实进程图例的结束进程操作来做渲染测试。

原版回归：运行 `GpuVramWidget.exe --classic --diagnostic`，检查 `classic-preview.png`，程序自动退出。

预览从 GPU 读取；由于浮窗使用 `WDA_EXCLUDEFROMCAPTURE`，普通录屏可能看不到浮窗，不能以普通截屏缺少浮窗判定渲染失败。

## 拖动刷新回归

执行 `tests/RunWorker.ps1` 可在不采集桌面的情况下测试真实 C# 渲染线程；模拟 DLL 仅写入 `artifacts/worker-test`，不要将它复制到发布目录。测试密集移动通知不能突破固定频率、拖动提速/恢复、前景更新合并、所有原生调用的线程归属、连续重建、退出和异常处理。它不能验证真实桌面画面或测量 GPU 性能。

交互桌面验收：在静态格子背景及播放视频的窗口上分别拖动；选择 15 Hz，开启/关闭“拖动时临时提高刷新率”比较，关闭时仍应保持低频更新，开启时拖动应明显更跟手。松手后应恢复 15 Hz。检查快速长距离移动、屏幕四边、缩放、跨屏重建，以及拖动中失去鼠标捕获后不会一直提速。

## 同步合成集成测试

`CompositionCheck.cs` 与实际应用源码一起编译，使用真实原生 DLL。它自动移动测试窗、切换同步/预测、在拖动中缩放，并检查承载窗口的显示、隐藏和捕获排除。还会进行 12 轮快速移动/停止，检查松手后承载窗口保持可见且区域收紧到浮窗大小并保存 `handoff-*.bmp`。测试约数秒，自动关闭；生成 `composition-latency.csv`、GPU 预览和 `composition-test-result.txt`。GPU 预览验证最终画面有效，但不能取代屏幕逐帧摄影对瞬时闪烁的检查。

```powershell
.\build.ps1 -OutputDirectory artifacts\composition
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /target:winexe /main:CompositionCheck /platform:x64 /codepage:65001 /out:artifacts\composition\CompositionCheck.exe /r:artifacts\composition\LiquidGlass.WinForms.dll /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:System.Management.dll GpuVramWidget.cs GlassRenderer.cs launcher\*.cs ui\*.cs tests\CompositionCheck.cs
.\artifacts\composition\CompositionCheck.exe
```

在正常交互桌面权限下运行。`desktop_ready` 应为 1，同步帧的 `sampled_x/y` 与 `visual_x/y` 一致；应存在有效 GPU 时间戳和预测样本。GPU 预览应包含真实背景，380×206 和 520×280 两种尺寸正常。受限沙箱返回 `0x80070005` 时仍能测试合成接口，但不能作为真实桌面采集验收。

手动比较同步开关、预测开关，尤其检查急停、反向、点击、退出、切换到其他应用和跨屏。CSV 只测软件时序；不要将 GPU 绘制耗时解释为鼠标到屏幕显示的完整延迟。

## 框架与静止绘制回归

`FrameworkCheck.cs` 仅引用 LiquidGlass.WinForms.dll，验证 GlassForm 的创建、前景失效、缩放、设置变化、隐藏/显示重建、释放。`IdleCheck.cs` 仅引用该 DLL，以 30 Hz 在纯色背景窗口前运行，记录静止绘制/跳过/复制计数，并断言变化背景与新前景触发绘制。测试都自动结束，结果写在测试 EXE 同目录。IdleCheck 会短暂铺满主屏幕，测试期间避免移动鼠标；桌面系统更新仍可影响次数。
构建根项目后，用 Framework64 csc 编译这两个文件，指定 /target:winexe /platform:x64 /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:输出目录\LiquidGlass.WinForms.dll，将测试 EXE 放到同一输出目录执行。上一版原生 DLL 与当前 API 兼容，可在独立测试目录替换 DLL 做同程序对比；不要覆盖运行中的用户版本。

## 启动与清理

只读后端检查（解析启动脚本、llama-fit-params 预检、进程归属）。在 PowerShell 中、仓库根目录执行（`$csc` 同上）：

```powershell
& $csc /nologo /target:exe /main:LauncherCheck /platform:x64 /codepage:65001 /out:artifacts\launcher-check\LauncherCheck.exe /r:bin\LiquidGlass.WinForms.dll /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:System.Management.dll GpuVramWidget.cs GlassRenderer.cs launcher\*.cs ui\*.cs tests\LauncherCheck.cs
artifacts\launcher-check\LauncherCheck.exe "P:\Local_LLM\Qwen 3.8\Quick Start"
```

`PanelShot.cs`（`/target:winexe /main:PanelShot`）打开面板约 8 秒并截图：`PanelShot.exe "<out.png>" [firefox,msedge]`，第二个参数只在内存中模拟清理名单，不会保存。
