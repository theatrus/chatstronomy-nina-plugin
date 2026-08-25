# Chatstronomy for N.I.N.A.

Bridge N.I.N.A. with Discord and Matrix, supporting bot slash commands for
control.

Chatstronomy reads the active N.I.N.A. profile natively. It can start a bundled
local runtime for a private Discord webhook, Discord application, or Matrix
account, or connect outbound to the hosted
[Chatstronomy Hub](https://hub.chatstronomy.com).

## Install

Install **Chatstronomy** from N.I.N.A.'s plugin manager and restart N.I.N.A.
Use the official N.I.N.A. repository when the release is listed there. For
the current release through our development repository, open
**Options → General → Plugin Repositories**, add the
following feed (N.I.N.A. appends `/plugins/manifests`), then install
Chatstronomy from **Plugins → Available**:

```text
https://raw.githubusercontent.com/theatrus/chatstronomy-nina-plugin/main/registry
```

Open **Options → Plugins → Chatstronomy** after restart.

## Modes

- **Hosted Hub** — the recommended centralized path. Pair each N.I.N.A. profile
  with [hub.chatstronomy.com](https://hub.chatstronomy.com). Multiple N.I.N.A.
  systems can share one managed Discord application.
- **Local Discord webhook** — simple notifications using your webhook.
- **Local Discord app / bot** — runs the full bot with your token, channel, and
  slash commands.
- **Local Matrix** — logs in to an HTTPS Matrix homeserver and posts to your
  selected room. Matrix can also accompany either local Discord option.

Local mode starts and stops its signed bundled runtime with N.I.N.A. Credentials
cross only a current-user named pipe and are never placed in command-line
arguments or generated configuration files. Hosted mode uses an outbound TLS
WebSocket and a profile/node-bound credential stored in Windows Credential
Manager.

## Local control consent and hosted privacy

Remote telescope and camera control is **disabled by default in each N.I.N.A.
profile**. The plugin's **Security and privacy** settings provide an overall
control switch and an individual permission for every supported hardware
command; both the overall switch and every command permission start off. Turning
on the overall switch alone does not authorize any action: explicitly select
only the commands an authorized Discord server or locally managed bot should be
allowed to run. Skipping sequence validation requires its own additional local
permission. These N.I.N.A.-side controls are the hardware trust boundary: Hub
roles, channel permissions, and server policies cannot override them.

Observatory position sharing is also disabled by default. With sharing disabled,
the plugin redacts site coordinates, elevation, and location-derived mount
values before sending telemetry. Hardware device identifiers and structured
local filesystem or script paths are always redacted, even when position
sharing is enabled. Images, selected log lines, notifications, user-entered
target names, and ordinary network connection information can still contain
identifying information; review them before enabling forwarding.

Hosted connections transmit only event categories explicitly enabled in the
active N.I.N.A. profile. Turning a category off prevents its underlying events
from reaching the Hub or local bot, including events already buffered before
the setting changed. Turning off images also blocks image history and
thumbnails. Equipment/status queries remain available; raw N.I.N.A. logs remain
opt-in. Before pairing, review the hosted
[privacy policy](https://chatstronomy.com/hub-privacy.html) and
[terms of service](https://chatstronomy.com/hub-terms.html).

## Native data and event controls

The plugin provides bounded native histories and typed command handling for:

- equipment connection and state changes;
- images and larger chat thumbnails;
- autofocus results and charts;
- guider state, dithers, history, and graphs;
- sequence lifecycle, waits, camera cooling, slew, center, and plate-solve
  results;
- Target Scheduler broker events and the active scheduled target name;
- N.I.N.A. popup status notifications;
- N.I.N.A. log events at individually selected levels.

Event families can be enabled per N.I.N.A. profile. Disabled categories never
leave N.I.N.A. over either the hosted WebSocket or the local bot's named pipe;
turning a category off immediately removes its buffered events from subsequent
queries. Images and thumbnails are also withheld when their category is off.
Popup notifications are enabled by default. Raw log levels are opt-in because
logs can be frequent and may include device or filesystem details.

## Development

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

Two optional environment variables widen the test suite; without them the
process-level runtime, hub, and cross-repo contract checks report `SKIP`
rather than failing, so it is easy to believe you ran more than you did:

```powershell
$env:CHATSTRONOMY_RUNTIME_EXE = "$PWD/runtime-cache/chatstronomy.exe"
$env:CHATSTRONOMY_CONTRACTS_DIR = "<path to chatstronomy>/contracts"
```

Author: Yann Ramin. License: Apache-2.0.
