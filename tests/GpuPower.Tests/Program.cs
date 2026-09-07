using System.Globalization;
using GpuPower.Core;

const string uuid = "GPU-11111111-1111-1111-1111-111111111111";
static string Csv(string limit = "600", string min = "300", string max = "600", string name = "NVIDIA RTX PRO 6000 Blackwell Server Edition", string id = "GPU-11111111-1111-1111-1111-111111111111", string enforced = "600") =>
    $"0, {id}, {name}, 00000000:21:00.0, {limit}, 600, {min}, {max}, 180.25, 48, {enforced}\n";
static void Check(bool result, string message = "Assertion failed") { if (!result) throw new Exception(message); }
static void Throws(Action action) { try { action(); } catch (InvalidOperationException) { return; } throw new Exception("Expected rejection"); }
var tests = new List<(string Name, Func<Task> Run)>();
void Test(string name, Action run) => tests.Add((name, () => { run(); return Task.CompletedTask; }));
void AsyncTest(string name, Func<Task> run) => tests.Add((name, run));

Test("Parse real NVIDIA CSV and supported ranges", () => { var g = NvidiaService.Parse(Csv()).Single(); Check(g.Name.Contains("6000") && g.Draw == 180.25 && g.Supports(350) && g.Supports(450) && g.Supports(550) && g.Supports(600) && !g.Supports(299) && !g.Supports(601)); });
Test("Unavailable power data disables writes", () => { var g = NvidiaService.Parse(Csv("N/A", "[Not Supported]", "N/A")).Single(); Check(g.Limit is null && !g.Supports(450)); });
Test("Zero, negative and non-finite limits are rejected", () => { foreach (var min in new[] { "0", "-1", "NaN", "Infinity" }) Check(!NvidiaService.Parse(Csv(min: min)).Single().Supports(450)); });
Test("Mixed GPUs retain independent capabilities", () => { var g = NvidiaService.Parse(Csv() + Csv(max: "300", id: "GPU-2222")); Check(g.Count == 2 && g[0].Supports(550) && !g[1].Supports(550)); });
Test("Quoted CSV names and escaped quotes", () => Check(NvidiaService.Parse(Csv(name: "\"NVIDIA, \"\"PRO\"\"\"")).Single().Name == "NVIDIA, \"PRO\""));
Test("Malformed output fails closed", () => { foreach (var input in new[] { "bogus", "0, GPU-1", Csv(name: "\"unclosed"), Csv(id: "unexpected") }) Throws(() => NvidiaService.Parse(input)); });
Test("Duplicate UUID fails closed", () => Throws(() => NvidiaService.Parse(Csv() + Csv())));
Test("Empty inventory is supported", () => Check(NvidiaService.Parse("").Count == 0 && NvidiaService.Parse("No devices were found").Count == 0));
Test("Decimal parsing is independent of Windows locale", () => { var previous = CultureInfo.CurrentCulture; try { CultureInfo.CurrentCulture = new("es-ES"); Check(NvidiaService.Parse(Csv("450.50")).Single().Limit == 450.5); } finally { CultureInfo.CurrentCulture = previous; } });
AsyncTest("Power write targets UUID and verifies read-back", async () => {
    var fake = new FakeRunner(Csv()); var service = new NvidiaService(fake);
    Check((await service.ApplyAsync(uuid, 450)).Success);
    Check(fake.Calls.Count == 3 && fake.Calls[1].SequenceEqual(new[] { "-i", uuid, "-pl", "450" }));
});
AsyncTest("All three presets apply and verify", async () => { foreach (var watt in new[] { 350d, 450d, 550d }) { var fake = new FakeRunner(Csv()); Check((await new NvidiaService(fake).ApplyAsync(uuid, watt)).Success); } });
AsyncTest("Factory reset uses driver default", async () => { var fake = new FakeRunner(Csv("450")); Check((await new NvidiaService(fake).ApplyAsync(uuid, null)).Success); Check(fake.Calls[1][3] == "600"); });
AsyncTest("Out-of-range requests never write", async () => { foreach (var watt in new[] { 299d, 601d, -1, double.NaN }) { var fake = new FakeRunner(Csv()); Check(!(await new NvidiaService(fake).ApplyAsync(uuid, watt)).Success && fake.Calls.Count == 1); } });
AsyncTest("Unavailable limits never write", async () => { var fake = new FakeRunner(Csv(min: "N/A")); Check(!(await new NvidiaService(fake).ApplyAsync(uuid, 450)).Success && fake.Calls.Count == 1); });
AsyncTest("Stale UUID never writes", async () => { var fake = new FakeRunner(Csv()); Check(!(await new NvidiaService(fake).ApplyAsync("GPU-2222", 450)).Success && fake.Calls.Count == 1); });
AsyncTest("Invalid UUID cannot become a command", async () => { var fake = new FakeRunner(Csv()); Check(!(await new NvidiaService(fake).ApplyAsync("-i 0; injected", 450)).Success && fake.Calls.Count == 0); });
AsyncTest("Driver write rejection is surfaced", async () => { var fake = new FakeRunner(Csv()) { RejectWrite = true }; var r = await new NvidiaService(fake).ApplyAsync(uuid, 450); Check(!r.Success && r.Message.Contains("Insufficient Permissions")); });
AsyncTest("Successful exit without changed limit is not success", async () => { var fake = new FakeRunner(Csv()) { IgnoreWrite = true }; Check(!(await new NvidiaService(fake).ApplyAsync(uuid, 450)).Success); });
AsyncTest("Read-back failure is not success", async () => { var fake = new FakeRunner(Csv()) { FailAfterWrite = true }; Check(!(await new NvidiaService(fake).ApplyAsync(uuid, 450)).Success); });
AsyncTest("Lower enforced system cap is disclosed", async () => { var fake = new FakeRunner(Csv(enforced: "350")); var r = await new NvidiaService(fake).ApplyAsync(uuid, 450); Check(r.Success && r.Message.Contains("350 W")); });
AsyncTest("Timeout/error does not claim success", async () => { var fake = new FakeRunner(Csv()) { ThrowWrite = true }; Check(!(await new NvidiaService(fake).ApplyAsync(uuid, 450)).Success); });
AsyncTest("Demo never needs NVIDIA and restores independently", async () => { var demo = new DemoService(); var g = await demo.DetectAsync(); await demo.ApplyAsync(g[0].Uuid, 350); var updated = await demo.DetectAsync(); Check(updated[0].Limit == 350 && updated[1].Limit == 600); await demo.ApplyAsync(g[0].Uuid, null); Check((await demo.DetectAsync())[0].Limit == 600); });

int failed = 0;
foreach (var test in tests) { try { await test.Run(); Console.WriteLine($"PASS {test.Name}"); } catch (Exception e) { failed++; Console.WriteLine($"FAIL {test.Name}: {e}"); } }
Console.WriteLine($"{tests.Count - failed}/{tests.Count} tests passed.");
return failed == 0 ? 0 : 1;

sealed class FakeRunner(string data) : ICommandRunner
{
    public List<string[]> Calls { get; } = [];
    public bool RejectWrite { get; init; }
    public bool IgnoreWrite { get; init; }
    public bool FailAfterWrite { get; init; }
    public bool ThrowWrite { get; init; }
    private bool written;
    public Task<CommandResult> RunAsync(IReadOnlyList<string> arguments)
    {
        Calls.Add(arguments.ToArray());
        if (arguments[0] == "-i")
        {
            if (ThrowWrite) throw new InvalidOperationException("NVIDIA timed out");
            if (RejectWrite) return Task.FromResult(new CommandResult(4, "", "Insufficient Permissions"));
            written = true;
            if (!IgnoreWrite) { var parts = data.Split(','); parts[4] = " " + arguments[3]; data = string.Join(',', parts); }
            return Task.FromResult(new CommandResult(0, "Power limit set", ""));
        }
        if (written && FailAfterWrite) return Task.FromResult(new CommandResult(9, "", "GPU lost"));
        return Task.FromResult(new CommandResult(0, data, ""));
    }
}
