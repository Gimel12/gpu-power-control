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
                    // Export the entire scrollable interface, independent of the CI desktop size.
                    var page = window.PageContent;
                    var drawing = new DrawingVisual();
                    using (var context = drawing.RenderOpen())
                    {
                        var area = new Rect(0, 0, page.ActualWidth + 72, page.ActualHeight + 52);
                        context.DrawRectangle(window.Background, null, area);
                        context.DrawRectangle(new VisualBrush(page) { Stretch = Stretch.Fill }, null,
                            new Rect(36, 28, page.ActualWidth, page.ActualHeight));
                    }
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(page.ActualWidth + 72), (int)Math.Ceiling(page.ActualHeight + 52), 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(drawing);
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
