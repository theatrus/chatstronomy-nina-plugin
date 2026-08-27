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
  systems can share one managed Discord application. Hosted delivery currently
  supports Discord; Matrix is available in local mode.
- **Local Discord webhook** — simple notifications using your webhook.
- **Local Discord app / bot** — runs the full bot with your token, channel, and
  slash commands.
- **Local Matrix** — logs in to an HTTPS Matrix homeserver and posts to your
  selected room. Matrix can also accompany either local Discord option.

Local mode starts and stops its signed bundled runtime with N.I.N.A. Local
delivery secrets and hosted connection credentials are stored in Windows
Credential Manager, not in the N.I.N.A. profile. Local credentials cross only a
current-user named pipe and are never placed in command-line arguments or
generated configuration files. Hosted mode uses an outbound TLS WebSocket and a
credential bound to the profile and node.

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
identifying information. Enabled sequence sharing can also include user-authored
annotation and message text. Failure summaries can contain sanitized N.I.N.A.
operational error text; local path-shaped strings are redacted before
transmission. Review these choices before enabling forwarding.

Most event categories, image sharing, and popup notifications start enabled.
Weather-change reports and high-wind alerts are separate opt-ins and start
disabled. Review those settings before pairing with the Hub or starting a local
runtime, and turn off anything you do not want to leave N.I.N.A. Turning a
category off prevents its underlying events from reaching the Hub or local bot,
including events already buffered before the setting changed. Turning off
images also blocks image history and thumbnails.
Equipment/status queries remain available, although disabling event categories
can reduce the detail available for target, sequence, and equipment tracking.
Raw N.I.N.A. logs are neither read nor sent until you enable at least one log
level. Before pairing, review the hosted
[privacy policy](https://chatstronomy.com/hub-privacy.html) and
[terms of service](https://chatstronomy.com/hub-terms.html).

## Native data and event controls

The plugin provides bounded native histories and typed command handling for:

- equipment connection and state changes, including dome/shutter activity,
  flat-panel cover, light, and brightness changes, and connection state for
  weather and switch devices;
- images, larger chat thumbnails, and image-save failures;
- autofocus results and charts from the report matching the completed run;
- guider state, dithers, history, and graphs;
- native safety-monitor connection and safe/unsafe transitions, retained as
  current status while safety delivery remains enabled;
- optional, rate-limited weather-change reports and independent high-wind and
  recovery alerts from N.I.N.A.'s wind-speed or gust readings;
- sequence lifecycle, item failures, and explicit completion outcomes;
- built-in timed, altitude, Moon-altitude, Sun-altitude, horizon, and safety
  waits, plus supported long-running Sequencer+ condition and manual waits;
- camera cooling and warming, mount-slew completion, center, and plate-solve
  results;
- Target Scheduler broker events and the active scheduled target name;
- N.I.N.A. popup status notifications;
- N.I.N.A. log events at individually selected levels.

Event families, images, and popup notifications can be controlled independently
for each N.I.N.A. profile. Weather changes and high-wind alerts are separate and
start disabled; most other event families start enabled. Disabled categories
never leave N.I.N.A. over either the hosted WebSocket or the local bot's named
pipe; turning a category off immediately removes its buffered events from
subsequent queries. Images and thumbnails are also withheld when their category
is off.
Once N.I.N.A. accepts a locally permitted command, its terminal failure is
always delivered as part of that command exchange; optional event switches do
not hide the outcome. Safety-monitor transitions have their own event switch; a
safety wait is sent only when both sequence and safety delivery are enabled.
The dedicated **Observatory and flat panel** switch covers dome/shutter actions
and flat cover, light, and brightness changes. Their connection events, along
with weather-station and switch-device connection state, use **Equipment
connections**. Structured weather measurements remain private unless
**Meaningful weather changes** or **High-wind alerts** is enabled. General
weather reports group significant changes and send
at most once every five minutes, except rain onset; they can include available
temperature, dew point, humidity, pressure, cloud, rain, wind, sky, and seeing
measurements. High-wind-only mode sends only wind speed, gust, and the local
threshold in m/s, plus alert/recovery state. An active alert may be resent after
a station reconnect or threshold change to synchronize status without another
user-facing high-wind notification. Missing readings never count as recovery;
observed wind must cross the hysteresis boundary. Weather-station names, device IDs,
drivers, and raw N.I.N.A. objects are never included. Weather reports are
informational and can be delayed, unavailable, or inaccurate; they do not
replace N.I.N.A.'s safety monitor, local automation, or physical interlocks.
Switch values and LiveStack data are not captured. Enabled popup notifications
and opt-in raw N.I.N.A. logs remain unstructured text and may contain
operational details. Sequencer+
condition expressions and free-form pause reasons remain inside N.I.N.A.
Changing mount, sequence, or safety delivery first closes the current Direct
session, then applies the new selection and reconnects. This prevents an older
Hub or local runtime from turning cached operation state into a final message
after sharing is disabled.
Every raw log level starts off because logs can be frequent and may include
device or filesystem details; logs are not read or sent until a level is
enabled.

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

Three optional environment variables widen the test suite; without them the
process-level runtime, hub, and cross-repo contract checks report `SKIP`
rather than failing, so it is easy to believe you ran more than you did:

```powershell
$env:CHATSTRONOMY_RUNTIME_EXE = "$PWD/runtime-cache/chatstronomy.exe"
$env:CHATSTRONOMY_HUB_EXE = "<path to full chatstronomy backend executable>"
$env:CHATSTRONOMY_CONTRACTS_DIR = "<path to chatstronomy>/contracts"
```

Author: Yann Ramin. License: Apache-2.0.
