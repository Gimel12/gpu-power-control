# GPU Power Control

A simple, portable Windows app for choosing NVIDIA GPU power limits. Designed around the **RTX PRO 6000 Blackwell Server Edition** and its configurable power budget, with capability checks for other NVIDIA GPUs.

**[Download the Windows app](https://github.com/Gimel12/gpu-power-control/releases/latest)**

| Mode | Power cap per selected GPU |
| --- | --- |
| High | 550 W |
| Balanced | 450 W |
| Efficient | 350 W |
| Restore Default | Factory default reported by each GPU |

## Use it

Download **GPU-Power-Control.exe**, launch it, approve administrator access, and press **Detect GPUs**. Select the cards you want to change, then click a mode. The app reads the limit back from the NVIDIA driver and reports each GPU's result.

No installer, Python, CUDA Toolkit, or separate .NET installation is required. The self-contained app bundles the .NET desktop runtime; native runtime files are extracted locally on launch. The NVIDIA Windows driver must already be installed. No telemetry or network requests are made by the application.

## Requirements and behavior

- Windows 11 x64 or Windows Server 2022/2025 with Desktop Experience. Server Core and ARM64 builds are not supported. Other Windows versions have not been validated.
- NVIDIA GPU and driver that expose adjustable power limits through `nvidia-smi`; administrator access is required. A matching card name alone does not guarantee driver support.
- Only modes within **every selected GPU's** reported minimum and maximum are enabled. Select individual GPUs for mixed systems.
- No cards are selected automatically. Newly detected cards are never silently added to a selection.
- Live readings update every five seconds. Unavailable readings are shown explicitly. Failed detection clears stale controls.
- Each change rechecks the GPU by its stable UUID, validates its current limits, then verifies the requested limit with a fresh driver query. Lower separately enforced caps are disclosed.
- Changes across multiple GPUs are sequential, not atomic. Individual failures are reported; successful changes are retained. Inspect the results or use Restore Default as needed.
- Power caps do not force a GPU to consume that wattage or promise a particular performance level.
- Settings may reset after restart or driver reload. This app does not install persistence, scheduled tasks, a service, or driver modifications.

NVIDIA documents the power-limit command and its supported-range requirement in the [NVIDIA System Management Interface guide](https://docs.nvidia.com/deploy/nvidia-smi/index.html). The [RTX PRO 6000 Blackwell Server Edition specifications](https://www.nvidia.com/en-us/data-center/rtx-pro-6000-blackwell-server-edition/) describe a configurable power budget up to 600 W.

## Release validation and signing

GitHub Actions builds the Windows executable, runs control-logic tests (including rejected writes and failed verification), and launches the **packaged executable** for a WPF UI smoke test with an isolated demo backend. The workflow produces a rendered UI preview and a test result.

**Physical GPU validation has not been performed.** GitHub's Windows runner does not provide an RTX PRO 6000. A successful simulated test does not prove that a customer's GPU/firmware/driver combination supports changing power limits. The application verifies actual changes on the customer's hardware at use time.

The initial release is **unsigned**. Windows may display an unknown-publisher or SmartScreen warning, and organizational policy may block it. A trusted code-signing certificate is needed for a signed distribution. Release downloads include SHA-256 checksums.

## Build and test

With the .NET 10 SDK:

```powershell
dotnet run --project tests/GpuPower.Tests -c Release
dotnet publish src/GpuPower.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/windows
```

On Windows, launch `GPU-Power-Control.exe --demo` to preview sample cards without reading or changing hardware. `--smoke-test` runs the demo UI checks, saves `smoke-preview.png` and `smoke-result.txt` in the current directory, then exits. The ordinary launch always uses the real NVIDIA backend. Demo mode is prominently labeled.

## Implementation

Native WPF interface with a separate, testable control library. Driver commands use argument arrays, no shell, a 20-second timeout, and trusted Windows system / NVIDIA Program Files locations; the app never searches the current folder or PATH for elevated executables. The three presets and driver-provided factory default are the only UI write paths. It does not change clocks, voltage, fans, or firmware.

MIT licensed. This project is independent and is not affiliated with NVIDIA.
