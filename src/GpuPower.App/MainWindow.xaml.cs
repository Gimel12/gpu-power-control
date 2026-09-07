using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using GpuPower.Core;

namespace GpuPower.App;

public sealed class GpuRow(Gpu gpu, bool selected) : INotifyPropertyChanged
{
    public Gpu Device { get; } = gpu;
    private bool selected = selected;
    public bool Selected { get => selected; set { selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); } }
    public string Name => Device.Name;
    public string SelectionLabel => $"Select GPU {Device.Index}: {Device.Name}";
    public string Identity => $"GPU {Device.Index}  ·  PCI {Device.BusId}";
    public string Limit => Gpu.Watts(Device.Limit);
    public string Draw => Gpu.Watts(Device.Draw);
    public string Temperature => Device.Temperature is double n ? $"{n:0} °C" : "Unavailable";
    public string Range => Device.MinLimit is double min && Device.MaxLimit is double max ? $"{min:0.#}–{max:0.#} W" : "Unavailable";
    public string Capability => Device.MinLimit is null || Device.MaxLimit is null
        ? "Power control is unavailable for this GPU / driver."
        : $"Factory default: {Gpu.Watts(Device.DefaultLimit)}  ·  Enforced limit: {Gpu.Watts(Device.EnforcedLimit)}";
    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class MainWindow : Window
{
    private readonly IGpuService service;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private List<GpuRow> rows = [];
    private bool busy;
    private bool rendering;
    private bool detected;
    public MainWindow(IGpuService service, bool demo)
    {
        InitializeComponent(); this.service = service;
        if (demo) { DemoBanner.Visibility = Visibility.Visible; AccessLabel.Text = "Demo preview"; }
        timer.Tick += async (_, _) => { if (!busy && detected) await DetectAsync(quiet: true); };
        Loaded += (_, _) => timer.Start();
        Closing += (_, e) => { if (busy) { e.Cancel = true; return; } timer.Stop(); };
        UpdateControls();
    }
    private async void Detect_Click(object sender, RoutedEventArgs e) => await DetectAsync();
    public async Task DetectAsync(bool quiet = false)
    {
        if (busy) return;
        SetBusy(true);
        if (!quiet) SetStatus("Detecting GPUs…", "Reading the NVIDIA driver. This can take a few seconds.");
        try
        {
            await RefreshRows(); detected = true;
            if (!quiet) SetStatus(rows.Count > 0 ? "Ready to choose a mode" : "No NVIDIA GPUs found",
                rows.Count > 0 ? "Select one or more GPUs below. A mode is enabled only when every selected GPU supports it." : "Check that the NVIDIA GPU and its Windows driver are installed, then detect again.");
        }
        catch (Exception ex)
        {
            detected = false; rows = []; RenderRows();
            SetStatus("GPU detection unavailable", ex.Message, true);
        }
        finally { SetBusy(false); }
    }
    private async Task RefreshRows()
    {
        var previous = rows.ToDictionary(r => r.Device.Uuid, r => r.Selected);
        var devices = await service.DetectAsync();
        // Explicit selection avoids changing additional cards just because they appeared during a refresh.
        rows = devices.Select(g => new GpuRow(g, previous.GetValueOrDefault(g.Uuid, false))).ToList();
        RenderRows();
    }
    private void RenderRows()
    {
        rendering = true;
        GpuList.ItemsSource = rows;
        EmptyState.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SelectionBar.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        GpuCount.Text = rows.Count == 0 ? "No GPUs detected." : $"{rows.Count} NVIDIA GPU{(rows.Count == 1 ? "" : "s")} detected  ·  Readings refresh every 5 seconds";
        SelectAll.IsChecked = rows.Count > 0 && rows.All(r => r.Selected);
        rendering = false; UpdateControls();
    }
    private void Selection_Changed(object sender, RoutedEventArgs e)
    {
        if (rendering) return;
        // Checked fires before the source binding update on some WPF controls.
        if (sender is CheckBox { DataContext: GpuRow row } checkbox) row.Selected = checkbox.IsChecked == true;
        rendering = true; SelectAll.IsChecked = rows.Count > 0 && rows.All(r => r.Selected); rendering = false;
        UpdateControls();
    }
    private void SelectAll_Changed(object sender, RoutedEventArgs e)
    {
        if (rendering) return;
        rendering = true;
        foreach (var row in rows) row.Selected = SelectAll.IsChecked == true;
        rendering = false; UpdateControls();
    }
    private void UpdateControls()
    {
        if (HighButton is null) return;
        var selected = rows.Where(r => r.Selected).ToArray();
        foreach (var button in new[] { HighButton, BalancedButton, EfficientButton })
        {
            var watts = double.Parse((string)button.Tag, CultureInfo.InvariantCulture);
            button.IsEnabled = !busy && selected.Length > 0 && selected.All(r => r.Device.Supports(watts));
            button.ToolTip = button.IsEnabled ? $"Apply {watts:0} W to {selected.Length} selected GPU(s)" : "Select GPUs that support this power limit.";
        }
        RestoreButton.IsEnabled = !busy && selected.Length > 0 && selected.All(r => r.Device.DefaultLimit is double d && r.Device.Supports(d));
        DetectButton.IsEnabled = !busy; SelectAll.IsEnabled = !busy; GpuList.IsEnabled = !busy;
        var total = selected.All(r => r.Device.Limit is not null) ? $"  ·  Total limit: {selected.Sum(r => r.Device.Limit ?? 0):0.#} W" : "";
        SelectionSummary.Text = $"{selected.Length} selected{(selected.Length > 0 ? total : "")}";
    }
    private void SetBusy(bool value) { busy = value; UpdateControls(); }
    private async void Mode_Click(object sender, RoutedEventArgs e) => await ApplyModeAsync(double.Parse((string)((Button)sender).Tag, CultureInfo.InvariantCulture));
    private async void Restore_Click(object sender, RoutedEventArgs e) => await ApplyModeAsync(null);
    public async Task ApplyModeAsync(double? watts)
    {
        if (busy) return;
        var selected = rows.Where(r => r.Selected).ToArray();
        if (selected.Length == 0) return;
        SetBusy(true);
        var results = new List<ApplyResult>();
        try
        {
            foreach (var row in selected)
            {
                SetStatus("Applying power settings…", $"Updating GPU {row.Device.Index}. Each change is checked against the driver before continuing.");
                results.Add(await service.ApplyAsync(row.Device.Uuid, watts));
            }
            var refreshError = "";
            try { await RefreshRows(); }
            catch (Exception ex) { detected = false; rows = []; RenderRows(); refreshError = "\nLive readings unavailable: " + ex.Message; }
            var success = results.Count(r => r.Success);
            var title = success == results.Count && refreshError == "" ? "Power settings verified" : $"{success} of {results.Count} GPU changes verified — review results";
            SetStatus(title, string.Join("\n", results.Select((r, i) => r.Success ? r.Message : $"GPU {selected[i].Device.Index}: {r.Message}")) + refreshError, success != results.Count || refreshError != "");
        }
        catch (Exception ex) { detected = false; rows = []; RenderRows(); SetStatus("Check current GPU settings", ex.Message + " Detect again before retrying.", true); }
        finally { SetBusy(false); StatusPanel.BringIntoView(); }
    }
    private void SetStatus(string title, string detail, bool error = false)
    {
        StatusTitle.Text = title; StatusDetail.Text = detail;
        StatusPanel.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(error ? "#FFF0DD" : "#E8EEF4"));
    }
    public async Task SmokeTestAsync()
    {
        await DetectAsync();
        if (rows.Count != 2 || HighButton.IsEnabled) throw new InvalidOperationException("Detection or selection safety check failed.");
        SelectAll.IsChecked = true;
        if (!BalancedButton.IsEnabled) throw new InvalidOperationException("Mode controls failed to enable.");
        await ApplyModeAsync(450);
        if (rows.Any(r => r.Device.Limit != 450) || StatusTitle.Text != "Power settings verified") throw new InvalidOperationException("Balanced mode UI verification failed.");
        await ApplyModeAsync(350);
        if (rows.Any(r => r.Device.Limit != 350)) throw new InvalidOperationException("Efficient mode UI verification failed.");
        await ApplyModeAsync(null);
        if (rows.Any(r => r.Device.Limit != 600)) throw new InvalidOperationException("Restore UI verification failed.");
        await ApplyModeAsync(550);
        if (rows.Any(r => r.Device.Limit != 550)) throw new InvalidOperationException("High mode UI verification failed.");
        await ApplyModeAsync(450);
    }
}
