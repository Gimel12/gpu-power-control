using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GpuPower.Core;

namespace GpuPower.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool smoke = e.Args.Contains("--smoke-test");
        bool demo = smoke || e.Args.Contains("--demo");
        var window = new MainWindow(demo ? new DemoService() : new NvidiaService(new NvidiaRunner()), demo);
        MainWindow = window;
        if (smoke)
        {
            window.Height = 1160;
            window.Loaded += async (_, _) =>
            {
                try
                {
                    await window.SmokeTestAsync();
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    window.UpdateLayout();
                    var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(window);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var file = File.Create("smoke-preview.png")) encoder.Save(file);
                    File.WriteAllText("smoke-result.txt", "PASS: detect, explicit selection, 550W, 450W, 350W, factory restore, and verified UI results. Demo backend only; no hardware validation.");
                    Shutdown(0);
                }
                catch (Exception ex) { File.WriteAllText("smoke-result.txt", ex.ToString()); Shutdown(1); }
            };
        }
        window.Show();
    }
}
