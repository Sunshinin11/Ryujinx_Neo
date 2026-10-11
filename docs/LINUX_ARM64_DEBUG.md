# Linux ARM64 Debug

Run `bash ./build-linux-arm64-debug.sh` from a Linux environment with the .NET
SDK selected by `global.json`, `file`, `readelf` (binutils), and `tar` installed.
The script cleans and publishes `src/Ryujinx/Ryujinx.csproj` for `linux-arm64`.
GitHub Actions runs it on `ubuntu-24.04-arm` on pushes to `neo-v2-fase1` and on
manual dispatch, then runs the executable's headless help command.

Download the Actions artifact **Ryujinx-Neo-linux-arm64-Debug**. It contains
`Ryujinx-Neo-linux-arm64-Debug.tar.gz`; extract it and run
`neo-linux-arm64-debug/Ryujinx` (or `Ryujinx.sh`). The tar archive preserves
executable permissions. `BUILD-INFO.txt` identifies the source commit and SDK.

This is a self-contained .NET Debug build with separate assemblies and portable
PDBs. Single-file publishing, trimming, and ReadyToRun are disabled. Keep the
PDBs alongside the assemblies for useful source locations in stack traces.
The build verifies the apphost and runtime are ELF AArch64 and checks that the
main, Common, and Vulkan assemblies and PDBs exist before packaging.

A successful build and headless help check do not validate Vulkan rendering or
game compatibility on the Odin 2. The target OS still needs the emulator's
native graphics, windowing, and audio dependencies.

## Missing diagnostics investigation

- `98e23e65e0e1859f67bdbc7ef31fab3f9ae82f23` explicitly excluded
  `src/Ryujinx.Common/Diagnostics/` in `.gitignore`.
- `e564bb0603a70f7dc515076f4cf07dec777b1485` introduced
  `LogicalSyncWaitTelemetry.cs` and `VulkanSubmissionWorker.cs` with references
  to that namespace, but did not include its implementation.
- None of the available branches, tags, PRs, or reachable Git history contain
  these diagnostics, under either their expected paths or type names. The
  structural analysis document describes a port from Gezinjinx and references
  commit `9cdac144`, which is not available in this repository.
- The project reference chain already exists:
  Vulkan -> GAL -> Shader -> Common. Common uses the SDK's default source
  inclusion. Neither a renamed namespace nor a missing project reference was
  found.

The four files in `Ryujinx.Common/Diagnostics` are **new implementations of the
API used by this branch**, not a cherry-pick or a recovered copy of Gezinjinx.
The unavailable implementation's defaults and log format cannot be verified.
They provide real sampled counters, host progress, submission boundaries,
state transitions, and fault details. No Vulkan call sites or queue/sync
behavior are changed, and the erroneous ignore rule has been removed.

Detailed diagnostics are opt-in at process startup:

```sh
RYUJINX_DETAILED_TELEMETRY=1 ./Ryujinx
```

`true` (case-insensitive) is also accepted. With the variable unset, detailed
sampling is disabled. Submission fault logging remains active, subject to the
normal logger configuration. Existing baseline and submission telemetry is
unchanged. Counters aggregate host worker activity; the last observed host
submit/present is not proof of GPU completion, and timeline identities from
different queues must not be compared. Sampling runs when the worker progresses,
so it is not a background hang detector.
