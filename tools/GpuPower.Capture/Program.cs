using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GpuPower.App;
using GpuPower.Core;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        Directory.CreateDirectory("captures");
        var app = new GpuPower.App.App(); app.InitializeComponent();
        var window = new MainWindow(new DemoService(), true) { Width = 1100, Height = 900 };
        app.MainWindow = window;
        var exit = 0;
        window.Loaded += async (_, _) =>
        {
            try
            {
                await Capture(window, "01-launch");
                await window.DetectAsync();
                await Capture(window, "02-detected");
                ((CheckBox)window.FindName("SelectAll")).IsChecked = true;
                await Capture(window, "03-selected");
                foreach (var watts in new[] { 550, 450, 350 })
                {
                    await window.ApplyModeAsync(watts);
                    await Capture(window, $"04-mode-{watts}");
                }
                await window.ApplyModeAsync(null);
                await Capture(window, "05-restored");
                File.WriteAllText("captures/PROVENANCE.txt", "Captured from the real WPF application using its isolated DemoService. No physical GPU reads or writes. Release app source unchanged.\n");
            }
            catch (Exception ex) { File.WriteAllText("captures/ERROR.txt", ex.ToString()); exit = 1; }
            app.Shutdown(exit);
        };
        window.Show();
        Dispatcher.Run();
        return exit;
    }

    private static async Task Capture(MainWindow window, string name)
    {
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        var page = (FrameworkElement)window.FindName("PageContent");
        var width = (int)Math.Ceiling(page.ActualWidth + 72);
        var height = (int)Math.Ceiling(page.ActualHeight + 52);
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen())
        {
            dc.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new VisualBrush(page) { Stretch = Stretch.Fill }, null, new Rect(36, 28, page.ActualWidth, page.ActualHeight));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create($"captures/{name}.png")) encoder.Save(file);
        var points = new Dictionary<string, object> { ["canvas"] = new { width, height } };
        foreach (var id in new[] { "DetectButton", "GpuList", "SelectAll", "HighButton", "BalancedButton", "EfficientButton", "RestoreButton", "StatusPanel" })
        {
            var control = (FrameworkElement)window.FindName(id);
            var origin = control.TransformToAncestor(page).Transform(new Point(0, 0));
            points[id] = new { x = origin.X + 36, y = origin.Y + 28, width = control.ActualWidth, height = control.ActualHeight };
        }
        File.WriteAllText($"captures/{name}.json", JsonSerializer.Serialize(points, new JsonSerializerOptions { WriteIndented = true }));
    }
}
