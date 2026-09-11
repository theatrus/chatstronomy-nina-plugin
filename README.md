# Chatstronomy for N.I.N.A.

Bridge N.I.N.A. with Discord and Matrix, supporting bot slash commands for
control.

See image previews, autofocus and guiding graphs, and session updates in chat.
Install the plugin and pair with [Chatstronomy Hub](https://hub.chatstronomy.com)
for the simplest setup, or run your own Discord or Matrix integration locally.

<a id="install"></a>

## Install from N.I.N.A.

Chatstronomy is available in the **official N.I.N.A. plugin repository**.
No extra repository is needed. Requires N.I.N.A. 3.2+ on Windows.

1. Open **Plugins → Available**, install **Chatstronomy**, and restart N.I.N.A.
2. Open **Options → Plugins → Chatstronomy** and review your sharing and
   hardware-control settings.
3. For hosted Discord, get a pairing code from
   [Chatstronomy Hub](https://hub.chatstronomy.com), enter it in the plugin,
   and connect your Discord channel in the Hub.

Read the [Hub privacy policy](https://chatstronomy.com/hub-privacy.html) and
[terms](https://chatstronomy.com/hub-terms.html) before pairing.

### Development version (optional)

Our development repository may offer newer reviewed releases before the official
listing catches up. Add it alongside the official repository under
**Options → General → Plugin Repositories → +**:

```text
https://raw.githubusercontent.com/theatrus/chatstronomy-nina-plugin/main/registry
```

Then install or update Chatstronomy through the plugin manager and restart
N.I.N.A. Both feeds can point to the same version; this is not a separate beta
channel. See the [latest release](https://github.com/theatrus/chatstronomy-nina-plugin/releases/latest)
and [development repository notes](registry/README.md).

## Modes

- **Hosted Hub — recommended:** pair one or more N.I.N.A. computers with the
  managed Discord bot. No observatory ports to open.
- **Local:** use your own Discord bot, Discord webhook, or Matrix account on
  an HTTPS homeserver. Matrix can also accompany either local Discord option.

The plugin manages the bundled local runtime for you. Credentials are stored
in Windows Credential Manager. Slash commands are Discord-only; Matrix and
webhooks provide notifications.

## Local control consent and hosted privacy

Choose which event categories, images, logs, and location data to share in the
plugin. Disabled event categories are not sent to the Hub or local bot;
disabling images also blocks history and thumbnails. Most ordinary events and
images start enabled, so review the settings before connecting.

Hardware commands require **both the local master switch and each command's
permission**. Hub roles cannot override them. Skipping sequence validation
requires separate approval.

See [events, sharing, and privacy](docs/events-and-privacy.md) for defaults,
redaction rules, supported events, and reporting limits.

## Commands while imaging

In Discord, use `/chatstronomy` followed by a subcommand such as `status`,
`last-image`, or `autofocus`.

Autofocus uses the selected N.I.N.A. implementation, including Hocus Focus.
During an advanced sequence, autofocus, filter changes, and target moves queue
through their matching Chatstronomy triggers before a light exposure. Add the
triggers to the enclosing instruction set—or directly to the Target Scheduler
container in its standard workflow. The simple sequencer and parallel
instruction sets do not support queued commands.

Cooling and warming remain available while sequencing. Other hardware commands
have sequence guards; `stop-sequence` stops the active sequence and
`start-sequence` starts the loaded advanced sequence when idle.

See the [command and trigger guide](docs/commands.md) for trigger names,
target selection, cancellation, and local permissions. A queued or accepted
reply is not a completion notice.

## Native data and event controls

- Image previews with capture details.
- Native N.I.N.A. and Hocus Focus autofocus reports, plus guider graphs.
- Target Scheduler targets, sequence progress, waits, cooling, and safety updates.
- Equipment events, optional weather and motion diagnostics, and selected logs.

Event selection is per N.I.N.A. profile. Repeated diagnostics are rate-limited
with omitted-message counts. See the
[event reference](docs/events-and-privacy.md#events-and-report-details) for
supported integrations and their limits.

## Development

This repository builds the C# plugin and packages a signed runtime pinned from
[the backend repository](https://github.com/theatrus/chatstronomy); it does not
compile Rust.

See [build and test instructions](docs/development.md) for packaging, signing,
and full integration-test setup.

Author: Yann Ramin. License: Apache-2.0.
