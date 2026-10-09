# GPU VRAM Liquid Glass

WinForms 显存浮窗，保留原有 D3DKMT 数据采集和交互。玻璃背景由原生 Direct3D 11、DXGI Desktop Duplication、DirectComposition 和自定义 HLSL 实时生成。

## 构建与运行

要求：Windows 10 2004 或更新版本（推荐 Windows 11），支持 D3D11 feature level 11.0 的显卡，.NET Framework 4.8。编译需要 Visual Studio C++ Build Tools、Windows SDK 和系统 .NET Framework C# 编译器；不需要 NuGet。

```powershell
.\build.ps1
.\bin\GpuVramWidget.exe
```

交付时将 `GpuVramWidget.exe`、`LiquidGlass.WinForms.dll`、`LiquidGlass.Native.dll`、`LiquidGlass.hlsl` 放在同一目录。HLSL 在构建时验证，运行时编译，编辑后可通过右键菜单“重新加载玻璃着色器”查看效果。DLL 静态链接 C++ 运行库。

```powershell
# 默认：纯色卡片（不加载着色器，不占用桌面捕获）
.\bin\GpuVramWidget.exe
# 液态玻璃：60 FPS、较强边缘折射、轻微模糊
.\bin\GpuVramWidget.exe --glass --fps 60 --refraction 19 --blur 1.2 --tint 0.035
# 自动保存两张 GPU 渲染预览，并在 300 帧后退出
.\bin\GpuVramWidget.exe --diagnostic
```

原有 `--top`、`--always`、`--interval`、`--w`、`--h`、`--corner`、`--font` 等参数继续有效。`--smooth` 控制经典模式的轮廓；玻璃轮廓目前使用 HLSL 中的四次超椭圆。

右键可切换通透/柔和玻璃、重新加载 shader、保存预览、切换置顶、退出。文字和图例作为预乘 alpha 纹理叠加在玻璃上，不参加折射。

右键选择“重启为纯白低占用模式（无 Shader）”会自动重启到原版白底模式，释放桌面捕获和 D3D11 渲染资源，不启动玻璃刷新计时器。保留字体、显示进程等启动参数和当前窗口尺寸，关闭超采样，采集间隔至少为原版的 2000ms。纯白模式中菜单显示“当前”并禁用重复切换。

纯白模式右键选择“重启为液态玻璃模式（Shader）”即可返回玻璃模式。两种模式始终显示切换入口，当前模式标注“当前”。重启保留当前材质参数，命令行也可用 `--glass` 明确启用玻璃。

## 结构

- `GpuVramWidget.cs`：原有采集、布局、交互；计时器只轮询玻璃状态及运行诊断。
- `GlassRenderer.cs`：显存浮窗的配置适配层。
- `framework/`：独立的 LiquidGlass.WinForms 组件，包含窗口基类、配置和渲染线程；`samples/GlassDemo.cs` 为不依赖显存逻辑的接入示例。
- `native/LiquidGlass.cpp`：设备、桌面捕获、透明交换链、纹理上传和资源释放。
- `native/LiquidGlass.hlsl`：超椭圆轮廓、边缘透镜折射、轻度色散、模糊和方向性高光。

桌面纹理留在 GPU 上，不逐帧读回 CPU。只有每两秒变化的数据前景需要上传；“保存预览”才会读回 GPU。捕获接口自动排除浮窗自身，因此常规录屏或截图中可能看不到它，预览应使用右键菜单保存。

## 当前边界

- 桌面捕获依赖交互桌面、驱动和系统捕获支持；失去捕获时显示占位材质并重试，错误记录在程序目录 `glass.log`。
- 当前选择浮窗所在的主要显示器，跨显示器后重建渲染设备。横跨两个屏幕时尚未拼接两路画面。
- HDR、旋转显示器、远程会话、锁屏恢复和多显卡切换需要分别验证。
- 这是独立制作的 Apple Liquid Glass 风格近似效果，不使用 Apple 私有代码。

已实现两项性能优化：

- 没有相关桌面变化、前景更新、鼠标高光变化或窗口/材质变化时，跳过 Draw 和 Present。桌面脏矩形及移动区域不与采样区域相交时，不复制背景。捕获仍按原频率轮询，不改变动画帧率上限。
- 非拖动时使用 CopySubresourceRegion 只复制浮窗附近区域，根据折射、模糊及色散的采样范围预留边距；额外预留 256 像素移动缓存，剩余空间不足 64 像素时重新居中。拖动时临时使用当前输出的整屏缓存，防止快速移动不断越界；松手后缩回局部。shader 使用纹理的真实屏幕坐标。

DXGI 内部仍保留桌面资源，局部复制减少的是本程序额外的背景纹理和复制带宽，并非消除系统整屏捕获开销。诊断日志包含 draws、skipped、cropCopies、copiedPixels 和 cropBytes；这些是操作计数，不是 GPU 占用率或功耗测量。

旧版小缓存测试记录：动态背景随后静止、并在中途移动/缩放浮窗的 240 次检查中，跳过 116 次绘制；默认窗口局部背景纹理约 0.42 MiB。新版移动缓存通常更大，默认窗口远离屏幕边界时约 2.76 MiB，屏幕边缘会裁小。较大的更新范围可能增加背景复制和绘制次数。尚未做新版整机 CPU/GPU/功耗基准。

## 已完成的验证

- x64 C++ DLL、WinForms EXE、VS/PS HLSL 编译通过。
- 在正常交互桌面中，使用 `tests/VisualCheck.cs` 创建独立的动态色条/圆形窗口作为真实捕获源，120/240 帧的捕获状态均为成功，GPU 预览显示不同背景画面，没有浮窗文字的递归残影。
- 诊断过程中移动浮窗并从 380×206 调整为 520×280，交换链与前景纹理更新成功。
- `--classic --diagnostic` 生成原版白底预览并正常退出。
- 受限执行环境会使 Desktop Duplication 返回 `0x80070005`；这是执行环境的桌面访问限制，不能用占位底图当作捕获成功。请从正常用户桌面启动。

未完成性能基准，以及 HDR、多屏拼接、旋转屏幕、锁屏/驱动重置等专项验证。

## 第 3–6 项优化

### 性能模式：手动背景采样频率

玻璃模式右键选择“性能模式：自定义同步刷新率…”，输入 **1–144 Hz**，点击“应用”立即生效，无需重启。“恢复自动”返回原来的效果优先/节能刷新策略。也可通过 `--sample-hz 15` 启用，`--sample-hz 0` 恢复自动。模式切换重启会保留该设置。

手动模式使用渲染线程上的同一个定时节拍控制完整玻璃帧：每次触发依次检查背景、运行 Shader 并 Present。默认开启“拖动时临时提高刷新率（跟随屏幕）”：实际拖动期间，采样与绘制一起提高到屏幕刷新率、设置频率、`--fps` 和 60 中的最大值，松手或失去鼠标捕获后恢复。右键关闭该选项，或使用 `--no-drag-boost`，即可做严格固定频率测试；`--drag-boost` 重新开启。模式切换重启保留该开关。

关闭拖动提速后，文字、鼠标高光、移动和缩放都不会主动越过设置的频率；手动模式关闭自动空闲降频。即使桌面没有新帧，也会用上一张背景执行一次 Shader 和 Present，因此该模式用于性能测试时会比自动模式更耗电。低频下背景、文字和高光都会有相应延迟。VSync 和系统负载可能使实际频率低于目标值，高 Hz 不保证实际达到。

渲染使用单调时钟和高精度可等待计时器，等待显示所花的时间计入当前帧周期，卡顿后不补发旧帧。普通等待方式作为后备。界面线程只提交最新状态及一张待上传的前景，不按鼠标事件堆积渲染任务。拖动提速时先等待上一轮合成更新，再读取位置、采样、绘制；窗体本身也使用鼠标的最新屏幕坐标，避免累加过时事件坐标。

拖动期间整屏背景纹理在 3840×2160 下约 31.6 MiB，且动态背景会增加复制带宽；这是本次为减少高速拖动停顿作出的取舍。关闭拖动提速只限制频率，拖动整屏缓存仍启用，松手后缩回局部。

显卡名称修复：备用识别逻辑现在使用完整型号判断独显，再精简标题。旧逻辑去掉了 `NVIDIA GeForce` 后又靠 `GeForce` 判断，可能错误显示核显。已在本机枚举验证标题为 `RTX 4080`；显存采集和负责桌面输出的渲染适配器选择逻辑未改动。

### 同步拖动、预测与延迟记录

默认开启右键“拖动时同步画面与位置”。原 WinForms 窗口继续处理输入；同步模式下，玻璃始终通过一个固定于当前屏幕的透明承载窗口显示。窗口大小的 DirectComposition Surface 与显示偏移使用同一取样坐标，在同一次 Commit 中更新。松手时保持相同的承载窗口和表面，最终位置显示完成后，将承载窗口区域收紧到浮窗大小；仅关闭同步选项时恢复原窗口显示。承载窗口虽然覆盖当前屏幕，Shader 仍只绘制玻璃本身的尺寸。两个窗口都排除自身捕获。事务机制见 [DirectComposition Commit](https://learn.microsoft.com/en-us/windows/win32/api/dcomp/nf-dcomp-idcompositiondevice-commit)。

松手黑闪修复（persistent-host）：取消松手时的表面归属切换与承载窗口隐藏，始终保留同一个显示窗口。停止后等待最终非预测位置完成合成，再收紧窗口区域；拖动前扩大区域，避免裁剪移动中的玻璃。空闲的原窗口使用显式透明表面，承载窗口不执行 GDI 背景擦除。全屏背景缓存延后至少 250 ms 再缩小，避免与松手集中在同一帧。正常桌面下 12 轮快速移动/松手回归通过；最终预览有效，但瞬间黑闪仍需实际拖动观察。

关闭此开关，或使用 `--no-sync-drag`，可与原窗口直接显示方式比较；`--sync-drag` 重新开启。同步方式的取舍是整块玻璃以渲染节奏跟随鼠标，尤其固定低频模式下会显得不跟手，但位置与内部背景不会分别更新。跨屏仍重建当前输出，尚未拼接多屏。

拖动提速开启时，渲染线程等待上一轮合成更新，再读取位置与绘制，取消额外的完整帧周期等待；关闭提速后的固定频率测试继续由指定频率限制。结束一次提速拖动时先立即提交结束帧，再恢复低频，避免最终位置等到下一次低频刷新才显示。

“轻度位置预测”默认关闭，仅同步拖动时生效。开启后使用 4ms 预测，位移上限 12 像素；停顿、方向反转、过旧样本或异常大跳跃会撤掉补偿。可用 `--prediction-ms 0..8` 调整，0 表示关闭。它可能在急停时轻微过冲，适合与关闭状态比较。同步、提速和预测设置均随模式重启保留。

右键“记录接下来 30 秒的延迟数据”会在程序目录生成 `glass-latency-日期时间.csv`。到时自动保存，提前退出或重新开始记录也会保存当前数据。诊断启用时才创建 GPU 查询和记录缓冲，结束后释放。`wait_ms` 是显示队列等待；`capture_cpu_ms` 是采集与纹理操作的 CPU 提交时间；`draw_cpu_ms` 是绘制命令的 CPU 时间；`submit_cpu_ms` 是提交阶段的 CPU 时间；`gpu_draw_ms` 是非阻塞 GPU 时间戳测得的绘制及合成表面复制时间，不包含此前提交的整屏背景复制。GPU 查询不可用或尚未完成时为 -1。

`desktop_frame_age_ms` 表示最新取得的桌面图像时间戳距当前的时间；静态画面可以长时间不更新，不能把该值直接当成可见 lag。`desktop_ready=0` 表示未取得桌面，不能用于评估真实背景延迟。flags 的位 1/2/4 分别表示拖动、同步选项、显示节奏提速。`sampled_x/y` 与 `visual_x/y` 在同步帧中使用同一坐标，后者是提交的位置；`hwnd_after_x/y` 只是提交后读取的窗口位置。这些数据均不是鼠标到屏幕发光的端到端延迟，后者需要外部高速摄影等测量。

拖动仍受桌面采集和显示合成的固有延迟限制，不保证零输入延迟。

新版验证：`tests/RunWorker.ps1` 使用隔离的模拟渲染 DLL 验证固定 15 Hz、提速/恢复、144 Hz 屏幕目标、更新合并、线程归属、重建和异常退出；不代表真实 GPU 性能。构建自动测试缓存和预测的边界。2026-09-08 在正常桌面权限下运行 `tests/CompositionCheck.cs`：271 条记录全部取得桌面，193 个同步帧的取样/提交坐标一致，完成拖动中 380×206 → 520×280 缩放、预测开关、切回原显示方式、承载窗口隐藏和退出保存。GPU 预览检查通过。尚未测量端到端输入延迟或整机功耗。

- 自适应刷新：默认效果优先，动态上限仍为 `--fps`（默认 60）。连续 9 次检查无变化后，检查间隔降至 100ms；检测到变化后恢复。右键“节能刷新”或 `--economy` 将非交互动态画面限制为约 30 FPS，拖动/缩放/鼠标交互恢复较高频率。静止转动态可能有最多约 100ms 的发现延迟。
- 几何缓存：两张半精度 GPU 纹理缓存法线、距离、单位折射量及高光系数，仅尺寸、圆角或着色器重建时更新。默认尺寸新增约 1.2 MiB 显存，换取减少每帧复杂计算；鼠标高光依然实时。
- 采样优化：模糊由 9 次取样近似为 5 次；模糊接近零时直接取样；远离边缘时合并极小色散偏移的取样。保留 `PSReference` 用于同一桌面纹理/前景/参数下对照。首次对照 RGB 平均绝对差异约 0.206/255，99 分位为 5/255；此结果只代表该测试画面。
- 采集与前景：显式释放 Process 对象，清理已退出 PID 缓存；访问失败退避 30 秒，零占用进程每 4 秒重新发现（比原有 30 秒更及时，但可能增加零占用进程查询量）。总容量不可用才重枚举 GPU。缓存进程分组、文字测量和 GDI 路径；显示文字及四分之一像素精度的进度条位置均未变时，跳过前景绘制/上传，点击用的进程数据仍更新。

已通过编译、20 次模式切换及设置保留测试、前景跳过/尺寸失效测试。首轮真实桌面测试中，窗口缩放一次时几何缓存仅生成两次，并完成原版/优化 shader 同帧对比。最后一次桌面复测被用户 Esc 中止，未继续操作桌面。尚未测量优化后的整机功耗及 CPU/GPU 百分比，不能将取样减少比例当成整机性能提升。

## 启动与清理：为本地 LLM 腾出显存（2026-10-07）

浮窗底部新增操作条：**清理** · **▶ 启动 ‹配置› ▾** · **•••**。完整流程变为一次点击：关闭清理名单中的应用 → 等待显存回收 → 启动 llama-server → 监控 `/health` 直到就绪。状态行显示「加载模型中 · N 秒」「就绪 · :8080 · ctx 110,592」，停止后可一键恢复被关闭的应用。

- **启动配置**就是现有的 llama-server `.bat/.cmd` 脚本，无需改写。在「•••」>「启动与清理…」中添加脚本文件夹；▾ 切换配置。启动时解析脚本中的命令，在 **Windows Terminal 的新标签页**（`wt.exe new-tab`，标题为 `llama-server · 配置名`）中用 PowerShell 运行 llama-server（已安装 PowerShell 7 时用 `pwsh`，否则用系统自带的 Windows PowerShell）；`wt.exe` 无法启动时退回独立的 PowerShell 窗口，状态行会注明。脚本通过 `-EncodedCommand` 传入，不写脚本文件，也不受执行策略影响。勾选「以最大可用 ctx 启动」时，清理后重新计算，用 `--ctx-size` 覆盖脚本中的值。含环境变量等无法解析的脚本，仍交给 cmd 原样运行。从浮窗停止服务时，先在临时目录写一个停止标记再结束 llama-server，脚本见到标记即正常退出，标签页/窗口随之关闭；服务自行退出（如启动报错）时则保留标签页，显示退出码并等待回车。只处理浮窗自己启动的服务所在的控制台，手动打开的终端不受影响。
- **显存预检**：所需显存 = 权重与缓冲 + 草稿/投影 + ctx ÷ (每 MiB 的 token 数)。其中两项由同目录的 `llama-fit-params.exe` 测得（只读取元数据，两次探测约 2 秒）：线性斜率，以及「权重与缓冲」这一固定开销。可用显存取自浮窗自己的 D3DKMT 统计：总量 − 当前占用 + 清理名单应用的占用 + 运行中的 llama-server（重新启动时会先停掉它），再留 384 MB 余量（`HeadroomMiB`）。不使用 fit-params 报告的空闲显存，因为 WDDM 下 CUDA 的空闲值不扣除其他进程，在 llama-server 运行时会严重失真。DFlash 草稿模型与 mmproj 按文件大小估算，标注「估算」。实测 vision 配置在 ctx 110,592 时预测 15,079 MiB，运行中的服务实际约 15,135 MiB。
- **清理**：先向应用窗口发送 WM_CLOSE（浏览器可保存会话），宽限期（默认 5 秒）后强制结束。系统、外壳、驱动进程以及 llama-server 本身受保护，不能加入名单。被关闭应用的路径保存在设置中，可「恢复应用」。
- **进程菜单**：左/右键点击图例中的进程 → 加入/移出清理名单（自动归到所属应用，如 steamwebhelper → steam；按安装目录判断，不会把从 Everything 启动的浏览器算到 Everything 名下）、**GPU 偏好**（写入与「Windows 设置 > 图形」相同的 `HKCU\Software\Microsoft\DirectX\UserGpuPreferences`，设为核显后该应用重启起不再占用独显显存）、打开文件位置、结束进程。名单中的应用在图例中带橙色圆环。
- **菜单整理**：顶层只保留任务入口；玻璃材质与刷新选项移入「外观与渲染」，着色器重载、延迟记录、预览移入「诊断」。

设置保存在 `%APPDATA%\GpuVramWidget\settings.ini`。新代码位于 `launcher/`，依赖 `System.Management`（读取外部启动的 llama-server 命令行）。framework/ 与 native/ 已同步 release-framework 的优化（AcquireNextFrame 0x887A0001 与 DwmFlush 失败改为原地恢复、着色器字节码缓存等）。

验证：`tests/LauncherCheck.cs`（只读）在本机解析 5 个 Qwen 3.8 启动脚本、ctx 替换、fit 预检（free 15074 MiB，约 47.9 token/MiB，训练上限 262,144）、进程到应用的归属与 GPU 偏好读取，全部通过；`tests/PanelShot.cs` 截取面板。经典与玻璃模式诊断预览均正常绘制操作条。清理与实际启动会关闭应用/占用显存，未在自动测试中执行，需在桌面上实际试用。

## Dot-matrix UI (2026-10-07)

The whole interface was rebuilt in the [dot-matrix dashboard style](https://github.com/karminski/karminski-design-skills) (CC BY-NC-SA 4.0, see `ui/THIRD-PARTY-NOTICES.md`), in English:

- **Widget** — one card: `01 RTX 4080 [16 GB VRAM]`, a glowing status line, a dot ring of used VRAM with a dot-matrix readout, a FREE / SERVER / CTX / MAX CTX stat grid, a 2-minute VRAM sparkline, one dot-bar row per GPU process on a shared scale (orange dot = on the clean list, accent bar = llama-server), and pill actions `CLEAN` · `▶ LAUNCH profile ▾` · `•••`. The status line says whether the selected profile *fits now*, *fits after clean*, or is *over budget*.
- **Thresholds are drawn**: the widget ring's amber tick is where the selected profile stops fitting; the console's pre-flight ring (VRAM the profile needs) lights amber past what is free now and red past what is free after cleaning.
- **Launch console** (`•••` › Launch console) — owner-drawn, two columns: 01 PROFILES, 02 PRE-FLIGHT (need ring, ctx stat grid, "VRAM after launch" plan bar), 03 CLEAN LIST (grace pills, toggle rows with dot bars, iGPU/dGPU tags), a LAUNCH AT MAX CTX switch and RESTORE / CLEAN ONLY / CLEAN & LAUNCH. Esc closes; drag by the header.
- **GPU preferences** (console › 03 › `GPU PREFS` tab) — every app that has a Windows per-app GPU preference (Settings › Display › Graphics), running or not, including Store apps and missing executables, with `DEFAULT` / `IGPU` / `DGPU` pills per row. At startup the widget snapshots all preferences (also written to `%APPDATA%\GpuVramWidget\gpu-preferences-startup.txt`); rows that differ get an amber dot and a "was …" note, `RESTORE STARTUP` puts every changed app back after a confirmation, and right-click reverts a single row. Only the `GpuPreference` entry is written; other flags in the same value (AutoHDR etc.) are kept. Changes apply when the app restarts. The snapshot is retaken every time the widget starts.
- **Healthy context** — the launch script's own `--ctx-size` is the healthy baseline (tuned by hand); launches always use it and nothing raises it automatically. Beside it the widget/console show reference limits: a *memory limit* (no spill into system RAM; 512 MB headroom) and a *speed limit* (decode still ≥ `MinDecodeTps`, default 30 t/s, with the context completely full); the status line warns when the baseline would spill (red), only eats into the safety margins (amber "Tight"), or decodes below the threshold when full. A running server whose GPU allocations exceed 1 GB of system RAM is flagged as spilling.
  - **Test-free estimate** (default, `launcher/Estimator.cs`): `VRAM = fit-params fixed + draft/projector + ctx × fit per-token × 1.075`, decode `ms/token = (main GB × 2.22 + filled K × KV MiB/1K × 0.0056) ÷ speculative speedup` (DFlash 1.69). DFlash drafts take 2.15× their file, projectors 1.17×; the draft type comes from its GGUF header. Coefficients were measured on this RTX 4080 (2026-10-07) on an IQ3_XXS+DFlash2 and an IQ3_S+mmproj profile, natural coding output; estimates add a 512 MB margin.
  - **CALIBRATE** (console › 02, ~2–4 min, whole GPU): runs the profile in a hidden llama-server on 127.0.0.1 at 16K and a large ctx, measuring VRAM, spill and decode at three fill depths; results in `%APPDATA%\GpuVramWidget\calibration.ini`, invalidated when model/draft/binary/arguments change. Optional — delete the file to go back to estimates.
  - Measured on IQ3_XXS+DFlash2: 12,377 MB + 23.0 MB per 1K ctx; spill starts near 196K (prefill 1,365 → 141 t/s, decode 87 → 32 t/s); with natural output decode stays ~50 t/s at 110,592 full. IQ3_S vision (no draft): 41 t/s empty, ~27 t/s at 110,592 full.
- **Menus and dialogs** use the same palette and mono type (`DotChrome`): Material, Rendering and Diagnostics submenus; confirmations and the sampling-rate picker are dot-style dialogs.
- **Materials** (`•••` › Material, or the console's material pill): *Solid card* (the startup default since `v2026.10.07-…-solid`: no shader, no desktop capture), *Dark smoked glass* (refraction darkened toward #141313 with the top-left ember glow; the default glass material), *Light glass* (warm frosted, dark ink) and *Legacy glass* (the previous shader unchanged, including adaptive text). Choosing a glass material from the solid card restarts with `--glass`; a plain start returns to the solid card. The card palette follows the material (dark or light). The shader receives the material through two spare flag bits, so the native API is unchanged. `--material dark|light|legacy` overrides it for one run.
- Type: Cascadia Mono (Consolas fallback; CJK falls back to Microsoft YaHei UI); `--font` still overrides.

Structure: `launcher/` holds the clean / fit / launch / monitor backend, `ui/` the presentation (`DotUi` kit, `DotChrome`, `WidgetForm`, `LaunchPanel`, `AppMenu`). `GpuVramWidget.cs` keeps the entry point, options and D3DKMT sampling.

## 可复用框架（2026-09-08）

已将稳定的持久承载窗口方案提取为 `LiquidGlass.WinForms.dll`。根构建同时生成显存浮窗和 `GlassDemo.exe`。详见 [框架接入说明](framework/README.md)。独立源码、示例和运行文件位于 `release-framework`。固定采样下跳过重复绘制，保持原采样频率；只在必要时更新合成绑定/偏移，减少托管分配。相同短时测试旧版绘制 65 次、新版 33 次，不能直接推导整机 GPU 占用或功耗降幅。
