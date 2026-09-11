# Build and test the N.I.N.A. plugin

Run these commands from the repository root:

```powershell
dotnet run --project Chatstronomy.NINA.Tests\Chatstronomy.NINA.Tests.csproj -c Release
./fetch-runtime.ps1
./build-package.ps1
```

`fetch-runtime.ps1` downloads the exact backend release pinned by
`runtime.lock.json`, rejecting identity, protocol, size, or checksum
mismatches, and leaves the signed runtime in `runtime-cache/`.
`build-package.ps1` then copies it into the N.I.N.A. plugin archive — it does
not download anything itself and fails if the runtime is missing. Rust is never
compiled in this repository. Every archive includes the Apache-2.0 license and
third-party notices; archives containing the runtime also include the complete
Liberation Sans SIL Open Font License next to `chatstronomy.exe`.

Three optional environment variables widen the test suite; without them the
process-level runtime, hub, and cross-repo contract checks report `SKIP`
rather than failing, so it is easy to believe you ran more than you did:

```powershell
$env:CHATSTRONOMY_RUNTIME_EXE = "$PWD/runtime-cache/chatstronomy.exe"
$env:CHATSTRONOMY_HUB_EXE = "<path to full chatstronomy backend executable>"
$env:CHATSTRONOMY_CONTRACTS_DIR = "<path to chatstronomy>/contracts"
```

[Back to the README](../README.md#development)
