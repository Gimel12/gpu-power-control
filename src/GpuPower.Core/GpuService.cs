using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace GpuPower.Core;

public sealed record Gpu(int Index, string Uuid, string Name, string BusId, double? Limit,
    double? DefaultLimit, double? MinLimit, double? MaxLimit, double? Draw, double? Temperature, double? EnforcedLimit)
{
    public bool Supports(double watts) => double.IsFinite(watts) && watts > 0 &&
        MinLimit is > 0 && MaxLimit >= MinLimit && watts >= MinLimit && watts <= MaxLimit;
    public static string Watts(double? value) => value is null ? "Unavailable" : $"{value:0.#} W";
}

public sealed record CommandResult(int ExitCode, string Output, string Error);
public interface ICommandRunner { Task<CommandResult> RunAsync(IReadOnlyList<string> arguments); }
public interface IGpuService
{
    Task<IReadOnlyList<Gpu>> DetectAsync();
    Task<ApplyResult> ApplyAsync(string uuid, double? watts);
}
public sealed record ApplyResult(string Uuid, bool Success, string Message);

public sealed class NvidiaRunner : ICommandRunner
{
    public static string Locate()
    {
        // Never search the current directory or PATH when running elevated.
        string[] candidates = [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe")];
        return candidates.FirstOrDefault(File.Exists) ?? throw new InvalidOperationException(
            "NVIDIA tools were not found. Install or update the NVIDIA driver, restart Windows, then detect again.");
    }

    public async Task<CommandResult> RunAsync(IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo(Locate()) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System) };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = info };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new InvalidOperationException("NVIDIA took too long to respond. Detect again to check the current GPU settings.");
        }
        return new(process.ExitCode, await stdout, await stderr);
    }
}

public sealed class NvidiaService(ICommandRunner runner) : IGpuService
{
    public const string Query = "--query-gpu=index,uuid,name,pci.bus_id,power.limit,power.default_limit,power.min_limit,power.max_limit,power.draw,temperature.gpu,enforced.power.limit";
    public async Task<IReadOnlyList<Gpu>> DetectAsync()
    {
        var result = await runner.RunAsync([Query, "--format=csv,noheader,nounits"]);
        if (result.ExitCode != 0) throw new InvalidOperationException(Failure(result));
        return Parse(result.Output);
    }

    public async Task<ApplyResult> ApplyAsync(string uuid, double? watts)
    {
        try
        {
            if (!Regex.IsMatch(uuid, "^GPU-[a-fA-F0-9-]+$"))
                throw new InvalidOperationException("Invalid GPU identity. Detect the GPUs again.");
            var gpu = (await DetectAsync()).SingleOrDefault(g => g.Uuid == uuid)
                ?? throw new InvalidOperationException("This GPU is no longer available. Detect again.");
            var target = watts ?? gpu.DefaultLimit;
            if (target is null || !gpu.Supports(target.Value))
                throw new InvalidOperationException("This power setting is outside the GPU's supported range, or its limits are unavailable.");
            var result = await runner.RunAsync(["-i", uuid, "-pl", target.Value.ToString("0.###", CultureInfo.InvariantCulture)]);
            if (result.ExitCode != 0) throw new InvalidOperationException(Failure(result));
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (attempt > 0) await Task.Delay(400);
                var updated = (await DetectAsync()).SingleOrDefault(g => g.Uuid == uuid);
                if (updated?.Limit is double limit && Math.Abs(limit - target.Value) < 0.1)
                {
                    var note = updated.EnforcedLimit is double enforced && enforced + 0.1 < target.Value
                        ? $" A separate hardware or system cap is enforcing {Gpu.Watts(enforced)}." : "";
                    return new(uuid, true, $"GPU {gpu.Index}: {Gpu.Watts(limit)} verified.{note}");
                }
            }
            return new(uuid, false, "NVIDIA accepted the request, but the new limit could not be verified. Detect again before retrying.");
        }
        catch (Exception ex) { return new(uuid, false, ex.Message + " Current settings may have changed; detect again to confirm."); }
    }

    private static string Failure(CommandResult result)
    {
        var detail = string.Join(" ", new[] { result.Error.Trim(), result.Output.Trim() }.Where(s => s.Length > 0));
        if (detail.Length > 800) detail = detail[..800];
        return $"NVIDIA could not complete the request (code {result.ExitCode}). {detail} " +
            "Check that the NVIDIA driver is installed and this GPU supports power control. Administrator access is required to change limits.";
    }

    public static IReadOnlyList<Gpu> Parse(string csv)
    {
        var devices = new List<Gpu>();
        foreach (var line in csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.Equals("No devices were found", StringComparison.OrdinalIgnoreCase)) continue;
            var p = CsvFields(line);
            if (p.Count != 11 || !int.TryParse(p[0], out var index) ||
                !Regex.IsMatch(p[1], "^GPU-[a-fA-F0-9-]+$"))
                throw new InvalidOperationException("NVIDIA returned unexpected GPU data. Update the driver and try again.");
            devices.Add(new(index, p[1], p[2], p[3], Number(p[4]), Number(p[5]), Number(p[6]), Number(p[7]), Number(p[8]), Number(p[9]), Number(p[10])));
        }
        if (devices.Select(g => g.Uuid).Distinct().Count() != devices.Count)
            throw new InvalidOperationException("NVIDIA returned duplicate GPU identities. No changes were made.");
        return devices;
    }

    private static double? Number(string value) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
        && double.IsFinite(n) && n >= 0 ? n : null;
    private static List<string> CsvFields(string line)
    {
        var fields = new List<string>(); var value = new StringBuilder(); var quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"') { value.Append('"'); i++; }
                else quoted = !quoted;
            }
            else if (c == ',' && !quoted) { fields.Add(value.ToString().Trim()); value.Clear(); }
            else value.Append(c);
        }
        if (quoted) throw new InvalidOperationException("NVIDIA returned incomplete GPU data.");
        fields.Add(value.ToString().Trim()); return fields;
    }
}

// Explicit demo mode is isolated from the NVIDIA command runner and never writes to hardware.
public sealed class DemoService : IGpuService
{
    private readonly List<Gpu> devices = [
        new(0, "GPU-11111111-1111-1111-1111-111111111111", "NVIDIA RTX PRO 6000 Blackwell Server Edition", "00000000:21:00.0", 600, 600, 300, 600, 182, 48, 600),
        new(1, "GPU-22222222-2222-2222-2222-222222222222", "NVIDIA RTX PRO 6000 Blackwell Server Edition", "00000000:81:00.0", 600, 600, 300, 600, 164, 45, 600)];
    public Task<IReadOnlyList<Gpu>> DetectAsync() => Task.FromResult<IReadOnlyList<Gpu>>(devices.ToArray());
    public Task<ApplyResult> ApplyAsync(string uuid, double? watts)
    {
        var index = devices.FindIndex(g => g.Uuid == uuid);
        if (index < 0) return Task.FromResult(new ApplyResult(uuid, false, "Demo GPU not found."));
        var target = watts ?? devices[index].DefaultLimit;
        if (target is null || !devices[index].Supports(target.Value)) return Task.FromResult(new ApplyResult(uuid, false, "Unsupported demo limit."));
        devices[index] = devices[index] with { Limit = target, EnforcedLimit = target };
        return Task.FromResult(new ApplyResult(uuid, true, $"GPU {index}: {Gpu.Watts(target)} verified (demo)."));
    }
}
