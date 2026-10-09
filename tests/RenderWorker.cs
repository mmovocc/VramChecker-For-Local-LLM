using System;
using System.Drawing;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using GpuVramWidget;

internal static class RenderWorkerTest
{
    [DllImport("LiquidGlass.Native.dll", CallingConvention=CallingConvention.Cdecl)] static extern int MockValue(int key);
    [DllImport("LiquidGlass.Native.dll", CallingConvention=CallingConvention.Cdecl)] static extern void MockFail();
    [DllImport("LiquidGlass.Native.dll", CallingConvention=CallingConvention.Cdecl)] static extern void MockRefresh(int hz);
    static void Require(bool condition,string message) { if(!condition) throw new Exception(message); }
    static int Observe(GlassRenderer renderer,bool dragging,int milliseconds)
    {
        int start=MockValue(0); var timer=Stopwatch.StartNew();
        while(timer.ElapsedMilliseconds<milliseconds)
        {
            renderer.Submit(dragging,true); // Flood position notifications; never run a UI message loop.
            Thread.Sleep(2);
        }
        return MockValue(0)-start;
    }
    [STAThread] static void Main()
    {
        using(var window=new Form())
        {
            Options.SampleHz=15; Options.DragBoost=false;
            var renderer=new GlassRenderer(window.Handle);
            using(var overlay=new Bitmap(20,20)) for(int i=0;i<20;i++) renderer.Upload(overlay);
            Thread.Sleep(100);
            int fixedFrames=Observe(renderer,true,650);
            Require(fixedFrames>=5 && fixedFrames<=11,"Fixed 15 Hz bypassed or worker did not render: "+fixedFrames);
            Options.DragBoost=true;
            int boostedFrames=Observe(renderer,true,650);
            Require(MockValue(4)==60,"Drag did not request 60 Hz");
            Require(boostedFrames>fixedFrames*1.5 && boostedFrames<=42,"Boost did not increase cadence or exceeded limit: "+boostedFrames);
            renderer.Submit(false,true);
            int restoredFrames=Observe(renderer,false,650);
            Require(MockValue(4)==15 && restoredFrames>=5 && restoredFrames<=11,"Release did not restore fixed rate");
            renderer.Save("unused",false); renderer.Compare("."); renderer.Counters(); renderer.Trace("unused");
            Require(MockValue(5)>0 && MockValue(5)<20,"Overlay updates were not coalesced");
            renderer.Dispose(); renderer.Dispose();
            int after=MockValue(0); Thread.Sleep(100);
            Require(after==MockValue(0),"Frames continued after Dispose");
            Require(MockValue(1)==0,"Native calls escaped the owning render thread");
            for(int i=0;i<10;i++) using(var r=new GlassRenderer(window.Handle)) { r.Submit(true,true); }
            MockRefresh(144);
            using(var r=new GlassRenderer(window.Handle))
            {
                Thread.Sleep(100);
                Observe(r,true,250);
                Require(MockValue(4)==144,"144 Hz monitor drag still capped at 60 Hz");
            }
            MockRefresh(60);
            using(var r=new GlassRenderer(window.Handle))
            {
                MockFail(); Thread.Sleep(200);
                bool failed=false; try { r.Render(); } catch(InvalidOperationException) { failed=true; }
                Require(failed,"Worker exception not surfaced to UI");
                failed=false; try { r.Save("unused",false); } catch(InvalidOperationException) { failed=true; }
                Require(failed,"Diagnostic call after failure did not fail promptly");
            }
            Require(MockValue(2)==MockValue(3),"Native renderer leaked during restart/failure");
            Require(MockValue(1)==0,"Destruction escaped the owning render thread");
            Console.WriteLine("PASS: worker-only native calls; 15 Hz={0}, drag={1}, restored={2} frames/650 ms; coalescing, diagnostics, 10 restarts, shutdown and failure.",fixedFrames,boostedFrames,restoredFrames);
        }
    }
}
