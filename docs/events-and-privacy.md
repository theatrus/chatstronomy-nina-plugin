# Events, sharing, and privacy

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
Slew diagnostics, rotator-motion diagnostics, weather-change reports, and
high-wind alerts are separate opt-ins and start disabled. Review those settings
before pairing with the Hub or starting a local runtime, and turn off anything
you do not want to leave N.I.N.A. Turning a
category off prevents its underlying events from reaching the Hub or local bot,
including events already buffered before the setting changed. Turning off
images also blocks image history and thumbnails.
Equipment/status queries remain available, although disabling event categories
can reduce the detail available for target, sequence, and equipment tracking.
Raw N.I.N.A. logs are neither read nor sent until you enable at least one log
level. Before pairing, review the hosted
[privacy policy](https://chatstronomy.com/hub-privacy.html) and
[terms of service](https://chatstronomy.com/hub-terms.html).

## Events and report details

The plugin provides bounded native histories and typed command handling for:

- equipment connection and state changes, including dome/shutter activity,
  flat-panel cover, light, and brightness changes, and connection state for
  weather and switch devices;
- images, larger chat thumbnails, and image-save failures;
- autofocus results and charts from the report matching the completed run,
  including every native N.I.N.A. focus and fitting mode plus Hocus Focus fit
  quality, star-count, region, validation, and algorithm details when present;
- guider state, dithers, history, and graphs;
- native safety-monitor connection and safe/unsafe transitions, retained as
  current status while safety delivery remains enabled;
- optional, rate-limited weather-change reports and independent high-wind and
  recovery alerts from N.I.N.A.'s wind-speed or gust readings;
- sequence lifecycle, item failures, and explicit completion outcomes;
- built-in timed, altitude, Moon-altitude, Sun-altitude, horizon, and safety
  waits, plus supported long-running Sequencer+ condition and manual waits;
- camera cooling and warming, optional mount-slew start/end diagnostics,
  center, and plate-solve results;
- optional rotator move start/end diagnostics with sky and mechanical angles
  when N.I.N.A. exposes them;
- Target Scheduler broker events and the active scheduled target name;
- N.I.N.A. popup status notifications;
- N.I.N.A. log events at individually selected levels.

Hocus Focus 4.0.0.13's normal autofocus workflow publishes N.I.N.A.'s full
start/completion lifecycle and is supported. Chatstronomy copies a reviewed set
of result and algorithm fields; raw Hocus settings, paths, device IDs, images,
and star lists stay inside N.I.N.A. Optional fields fall back naturally to the
standard N.I.N.A. report. Hocus's Star Detection Optimizer feedback variants are
not exported in its normal autofocus report. Its Aberration Inspector publishes
only completion and exposes the complete six-region analysis only through an
optional private save folder, so Chatstronomy keeps Inspector reports local
rather than weakening the requirement that autofocus sharing stay enabled for
the whole run. Full optimizer or Inspector feedback requires a future Hocus
Focus event or adapter contract.

Autofocus notifications are tied to a completed run. A delivery receipt stops
that run from being replayed after reconnect; reading its report does not.
Pending notification replay expires after ten minutes or when another focus
run starts, including with older Hub or local-runtime versions that do not
send receipts. The last completed report stays available for status queries.

Event families, images, and popup notifications can be controlled independently
for each N.I.N.A. profile. Slew diagnostics, rotator-motion diagnostics, weather
changes, and high-wind alerts are separate and start disabled; most other event
families start enabled. Disabled categories never leave N.I.N.A. over either the
hosted WebSocket or the local bot's named pipe; turning a category off
immediately removes its buffered events from subsequent queries. Images and
thumbnails are also withheld when their category is off.

N.I.N.A. exposes public completion callbacks for slews and rotator moves, but no
public start callback. When its separate motion switches are enabled,
Chatstronomy detects moving and idle edges from N.I.N.A.'s live
`Slewing` and `IsMoving` state. When both edges are observed, a motion ID pairs
the start and end and the interval spans those observations. Slew events record
a requested target when N.I.N.A. provides one, plus observed moving and idle
RA/Dec positions. State-observed starts and ends also include altitude and
azimuth only while **Share exact observatory coordinates and location-derived
mount position** is enabled.
N.I.N.A. does not expose the requested rotator target at its public start-state
boundary. State-observed rotator starts report available sky and mechanical
angles; a recovered start carries the callback's available logical or mechanical
`From` angle. An
ordinary “ended” event means N.I.N.A. first reported the device idle; it does
not by itself claim that settling or the overall operation succeeded. Delayed native
completion callbacks enrich or deduplicate the same motion without blocking
N.I.N.A.'s equipment callbacks. If a short movement completes between live
state observations, Chatstronomy recovers a paired start and end from N.I.N.A.'s
completion callback, labels it **Recovered after motion began**, and omits a
duration it could not observe. Because that callback has no start timestamp, a
recovered mount start contains callback RA/Dec but no historical altitude or
azimuth; its end is timestamped by the completion callback and uses the
available live idle snapshot. Neither recovery record implies success.

Timed waits use Discord's localized timestamps and relative countdowns. Matrix
shows UTC timestamps and the time remaining when the message was sent. Target
Scheduler waits and sequence time waits are tracked separately; reaching an
estimated wait time does not by itself mean that the wait has finished.

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
Changing any event-delivery selection first closes the current Direct session,
then applies the new selection and reconnects. This prevents an older Hub or
local runtime from turning cached operation state into a final message after
sharing is disabled.
Every raw log level starts off because logs can be frequent and may include
device or filesystem details; logs are not read or sent until a level is
enabled.

Chat forwarding drops repeated diagnostics instead of building a backlog.
Per profile, errors and warnings share a burst allowance of five messages,
replenished at one every 12 seconds. Other log lines and popup notifications
share an allowance of ten, replenished at one every six seconds. Identical
diagnostics are sent at most once a minute, including alternating failures
from different sequence items. Excess messages are discarded before entering
chat history; N.I.N.A.'s own logs are unchanged. Normal equipment events,
safety changes, sequence outcomes, and responses to remote commands remain
available. Existing event and log sharing selections still apply.

The Hub can report how many diagnostics were omitted, even if no further
event arrives. Event-history responses include an additive `ElidedEvents`
array of cumulative `{Event, Level?, Count, Epoch}` counters, including enabled
counter slots with a zero count. Unchanged reads do not drain or duplicate
counts. These contain no suppressed message text.
Counters obey the original event and log-level permissions, clear when sharing
settings change, and start a new epoch after a profile reset. Missing slots
and changed epochs clear pending notices, including messages dropped by the
Hub itself. An empty array means no diagnostic counters are currently shared;
older runtimes can ignore the new envelope field while continuing to read the
unchanged event list.

## Credentials and transport

Local mode starts and stops its signed bundled runtime with N.I.N.A. Local
delivery secrets and hosted connection credentials are stored in Windows
Credential Manager, not in the N.I.N.A. profile. Local credentials cross only a
current-user named pipe and are never placed in command-line arguments or
generated configuration files. Hosted mode uses an outbound TLS WebSocket and a
credential bound to the profile and node.

[Back to the README](../README.md#local-control-consent-and-hosted-privacy)
