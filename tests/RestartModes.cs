using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
internal static class RestartModes
{
    [DllImport("shell32.dll", CharSet=CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string command, out int count);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    [STAThread] private static void Main(string[] args)
    {
        var assembly=Assembly.LoadFrom(args[0]);
        var options=assembly.GetType("GpuVramWidget.Options",true);
        var form=assembly.GetType("GpuVramWidget.WidgetForm",true);
        var stats=assembly.GetType("GpuVramWidget.GpuStats",true);
        var classify=stats.GetMethod("IsDiscreteAdapterName",BindingFlags.NonPublic|BindingFlags.Static);
        foreach(string name in new[]{"NVIDIA GeForce RTX 4080","RTX 4080","AMD Radeon RX 7900 XTX","Intel(R) Arc A770"})
            if(!(bool)classify.Invoke(null,new object[]{name})) throw new Exception("Discrete name missed: "+name);
        foreach(string name in new[]{"AMD Radeon(TM) Graphics","Intel(R) UHD Graphics 770","Parsec Virtual Display Adapter"})
            if((bool)classify.Invoke(null,new object[]{name})) throw new Exception("Wrong discrete classification: "+name);
        var builder=form.GetMethod("RestartArguments",BindingFlags.NonPublic|BindingFlags.Static);
        string font="C:\\Fonts with spaces\\quoted\"name\\";
        options.GetField("FontPath").SetValue(null,font);
        options.GetField("Refraction").SetValue(null,23f);
        options.GetField("Economy").SetValue(null,true);
        options.GetField("SampleHz").SetValue(null,17);
        for(int iteration=0; iteration<20; iteration++)
        {
            bool glass=iteration%2==1;
            bool boost=iteration%3==0;
            options.GetField("DragBoost").SetValue(null,boost);
            options.GetField("SyncDrag").SetValue(null,boost);
            options.GetField("PredictionMs").SetValue(null,boost ? 4f : 0f);
            string command=(string)builder.Invoke(null,new object[]{glass,420,240});
            options.GetField("DragBoost").SetValue(null,!boost);
            options.GetField("SyncDrag").SetValue(null,!boost);
            options.GetField("PredictionMs").SetValue(null,99f);
            int count; IntPtr argv=CommandLineToArgvW("widget.exe "+command,out count);
            if(argv==IntPtr.Zero) throw new Exception("Argument parsing failed");
            var parsed=new string[count];
            try { for(int i=0;i<count;i++) parsed[i]=Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv,i*IntPtr.Size)); }
            finally { LocalFree(argv); }
            options.GetMethod("Parse").Invoke(null,new object[]{parsed});
            if((bool)options.GetField("Glass").GetValue(null)!=glass) throw new Exception("Wrong mode after restart");
            if((string)options.GetField("FontPath").GetValue(null)!=font) throw new Exception("Font argument changed");
            if((float)options.GetField("Refraction").GetValue(null)!=23f) throw new Exception("Material setting lost");
            if(!(bool)options.GetField("Economy").GetValue(null)) throw new Exception("Refresh preference lost");
            if((int)options.GetField("SampleHz").GetValue(null)!=17) throw new Exception("Sampling frequency lost");
            if((bool)options.GetField("DragBoost").GetValue(null)!=boost) throw new Exception("Drag boost preference lost");
            if((bool)options.GetField("SyncDrag").GetValue(null)!=boost) throw new Exception("Synchronized drag preference lost");
            if((float)options.GetField("PredictionMs").GetValue(null)!=(boost ? 4f : 0f)) throw new Exception("Prediction preference lost");
            using(var instance=(Form)Activator.CreateInstance(form,true))
            {
                bool canSwitch=false;
                foreach(ToolStripItem item in instance.ContextMenuStrip.Items)
                    if(item.Text.StartsWith(glass?"重启为纯白":"重启为液态玻璃") && item.Enabled) canSwitch=true;
                if(!canSwitch) throw new Exception("Missing enabled return menu");
                if(!glass)
                {
                    var redraw=form.GetMethod("UpdateLayeredSurface",BindingFlags.Instance|BindingFlags.NonPublic);
                    var paintCounter=form.GetField("_foregroundPaints",BindingFlags.Instance|BindingFlags.NonPublic);
                    redraw.Invoke(instance,null);
                    int before=(int)paintCounter.GetValue(instance);
                    redraw.Invoke(instance,null);
                    if((int)paintCounter.GetValue(instance)!=before) throw new Exception("Unchanged foreground repainted");
                    instance.ClientSize=new System.Drawing.Size(460,260);
                    redraw.Invoke(instance,null);
                    if((int)paintCounter.GetValue(instance)<=before) throw new Exception("Resize did not repaint foreground");
                }
            }
        }
        Console.WriteLine("PASS: performance interval, 20 mode switches, drag boost preferences, menus, quoted paths and foreground invalidation.");
    }
}
