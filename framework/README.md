# LiquidGlass.WinForms

可复用的 Windows x64 桌面折射玻璃窗口组件。原 GPU 显存浮窗和示例程序使用同一个 DLL；组件不依赖显卡名称、显存统计、进程列表或应用配置。

## 使用

运行环境：Windows 10 2004 或更新版本、.NET Framework 4.x、可用的 D3D11 和 DXGI Desktop Duplication 桌面会话。当前验证环境为本机 Windows 桌面；未验证 ARM、HDR、旋转屏幕、远程会话和跨框架集成。桌面捕获不支持时通过 GlassFailed/LastGlassError 报告并定时重试。

引用 `LiquidGlass.WinForms.dll`，应用以 x64 编译。把以下三个文件放在应用 EXE 旁边：

- LiquidGlass.WinForms.dll
- LiquidGlass.Native.dll
- LiquidGlass.hlsl

不需要安装 Vulkan、D3D12 或 WPF。原生 DLL 静态链接 C++ 运行库。预编译程序使用系统 .NET Framework；源码可进一步迁移到现代 .NET，但此包未验证该目标。

```csharp
using System.Drawing;
using LiquidGlass;

class MyWindow : GlassForm
{
    public MyWindow()
    {
        ClientSize = new Size(420,240);
        Glass.Refraction = 19;
        Glass.SampleHz = 30;
    }
    protected override void DrawGlassForeground(Graphics g)
    {
        g.DrawString("我的应用", SystemFonts.DefaultFont, Brushes.Black, 24, 24);
    }
}
```

在 STA 主线程开启 DPI awareness 后，使用 `Application.Run(new MyWindow())`。完整入口、按钮点击和右键关闭见 `samples/GlassDemo.cs`。复用时改示例类即可，不需要显存浮窗源码。

## 接口与约定

- `GlassForm`：无边框窗口，管理承载窗口、拖动、鼠标捕获、前景上传、跨屏重建、隐藏时释放以及显示时重建。
- `DrawGlassForeground(Graphics)`：绘制前景。数据改变后调用 `InvalidateGlass()`；不会每帧重新绘制和上传前景。
- `IsGlassDragArea(Point)`：划分可拖动区域。自绘按钮区域返回 false，用 OnMouseUp 等标准事件处理交互。
- `Glass` / `ApplyGlassOptions()`：调整材质与刷新策略。配置属于每个窗口，不是全局静态值。所有配置、窗口操作和 Dispose 在 UI 线程调用。
- `GlassFailed` / `LastGlassError`：捕获初始化或渲染错误，应用可在事件中提示用户。
- `GlassRenderer`：已有 WinForms 窗口可直接接入的底层组件；窗口须使用 WS_EX_NOREDIRECTIONBITMAP 且不绘制 GDI 背景。传入 HWND 和 GlassOptions，Upload 32 位前景位图；UI 定时调用 Submit/Render，Render 返回 2 时 Dispose 后重建，负结果转换为异常。移动窗口前 PrepareDrag，移动后 Submit(true,true)，松手 Submit(false,true)。构造后必须上传前景。示例的 GlassForm 已封装此流程。
- `Trace`、`Counters`、`Save`、`Compare` 为显式诊断接口；保存和对比会有读回/等待开销，不应放进正常刷新循环。

材质参数：CornerRadius 0..4096，Refraction 0..40，Blur 0..12，Tint 0..1，PredictionMs 0..8。默认预测关闭，保留已经验证稳定的急停行为。

Material（新增，默认 0）：0 旧版浅色玻璃（自适应文字反色，即此前的效果）；1 深色烟熏玻璃；2 浅色磨砂玻璃。1 和 2 不做文字反色，前景自己决定墨色，适合深色/浅色界面。通过 flags 的两个空闲位传给原生层，`GlassRender` 导出签名与 ABI 不变；旧调用方不设置 Material 时效果与之前完全相同。

Fps 10..144；SampleHz=0 为自适应，1..144 为固定背景采样与更新时钟。DragBoost 默认开启，拖动临时跟随屏幕刷新率，松手立即提交结束帧后恢复原频率。关闭 DragBoost 可严格测试指定频率。固定频率下完全不变的画面复用已有表面，不影响下一次变化的采样时机。

这是桌面玻璃渲染组件和自绘窗口基类，不是完整 UI 控件库。普通 WinForms 子控件不会自动画进玻璃前景，不应直接在 GlassForm 内放入 TextBox/Button 并期待正常叠合。需要输入框、列表、键盘导航和无障碍支持时，要继续增加控件布局/输入层。当前每个玻璃窗口独立持有设备、采集器和渲染线程；大量窗口不共享桌面采集。WPF、WinUI、Qt 尚无适配器。

## 稳定显示的机制

1. DXGI Desktop Duplication 获取屏幕；D3D11 在 GPU 中复制可用背景，应用前景单独上传。
2. HLSL 计算圆角、折射、模糊和边缘高光；圆角几何缓存只在尺寸或半径改变时重建。
3. 固定屏幕位置的透明承载窗口显示 DirectComposition Surface。采样坐标和显示偏移使用同一位置，同次 Commit 提交。
4. 同步模式拖动、静止使用同一个承载窗口和表面。松手不切换显示归属、不隐藏承载窗口，等待最终未预测位置显示后再收紧区域。
5. 拖动用全屏背景缓存，停止至少 250 ms 后才收缩。前景窗口不擦除 GDI 背景，空视觉对象使用显式透明表面。

两个窗口都使用 WDA_EXCLUDEFROMCAPTURE 排除自身，防止无限递归采集。因此录屏和远程软件通常看不到玻璃窗口；这是当前方式的限制。桌面捕获与显示合成本身仍有延迟，框架不保证零延迟。

## 本次优化与验证

固定采样模式在桌面、前景、参数和位置均未改变时，跳过重复 Shader、表面复制和 Commit；保持采样时钟。只在表面重建/显示方式变化时重新绑定内容，只在位置变化时设置合成偏移；托管配置比较避免装箱，成功帧不再反复转换状态字符串。

2026-09-08，本机同一 30 Hz 测试程序、约 2 秒静止区间：上一版原生 DLL 绘制 65 次，优化版绘制 33 次、跳过 33 次；两次均复制背景 33 次。此数字证明减少了重复绘制，并不等于整机 GPU 使用率或功耗降低 49%。系统仍报告部分背景更新，因此不是理想的完全静止场景。变化背景、前景更新验证通过。

另通过：12 轮快速移动/松手、同步开关、缩放、捕获排除、预测；独立 GlassForm 创建/隐藏/显示/重建/销毁；渲染线程归属、固定刷新、提速恢复、合并上传、异常处理；显存浮窗的 20 次模式参数切换回归。瞬时闪烁的最终体验仍以实际桌面使用为准。

2026-10-07 维护优化（接口与 ABI 不变）：着色器字节码按进程缓存，隐藏/显示、跨屏重建与多窗口不再重复 O3 编译；DwmFlush 失败（如 0x80263001，锁屏/模式切换）只作为节拍等待失败，不再销毁设备；AcquireNextFrame 的 DXGI_ERROR_INVALID_CALL（睡眠唤醒后出现，见 glass.log）与 ReleaseFrame 失败改为只重建桌面复制会话，原因写入 Status；GlassForm 前景位图直接移交渲染线程，少一次整图复制；原生错误文本按系统代码页转换；build.ps1 把中间文件放到 bin/obj。已验证 /W4 无警告构建与示例启动；拖动、睡眠唤醒路径需在实际桌面复测。

2026-10-07 同步 GpuVramWidget v2026.10.07-dotmatrix-gpuprefs-terminal-solid：GlassOptions 新增 Material，原生层与 HLSL 新增深色烟熏/浅色磨砂两种材质，其余框架代码不变；GlassDemo 行为不变。`runtime/` 中的 GpuVramWidget 为点阵风格新版（英文界面，默认以纯色卡片启动，`--glass` 或菜单 Material 切换玻璃），其界面移植自 CC BY-NC-SA 4.0 设计，见 `runtime/THIRD-PARTY-NOTICES.md`。

## 从源码构建

安装 Visual Studio C++ Build Tools（含 Windows SDK），在本目录执行 `./build.ps1`，输出到 `bin`（中间文件在 `bin/obj`）。独立源码包包含本目录的 C#、native 和 samples，构建不需要原显存项目。项目仓库内也可运行根目录 build.ps1，同时构建显存浮窗、框架及示例。
