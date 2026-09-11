# Chatstronomy development plugin repository

Chatstronomy is available in N.I.N.A.'s **official plugin repository**. For the
standard installation, use **Plugins > Available**; no extra repository is needed.

This optional development feed may offer newer reviewed releases before the
official listing catches up. It can sit alongside the official repository, and
both feeds may point to the same version. See the
[latest release](https://github.com/theatrus/chatstronomy-nina-plugin/releases/latest)
for release notes.

Add this base URL to **N.I.N.A. > Options > General > Plugin Repositories**:

```text
https://raw.githubusercontent.com/theatrus/chatstronomy-nina-plugin/main/registry
```

N.I.N.A. appends `/plugins/manifests` itself. After adding the repository, open
**Plugins > Available**, select **Chatstronomy**, install it, and restart
N.I.N.A.

There is no separate beta package channel. Both the release-pinned Rust runtime and
`Chatstronomy.dll` are signed by StackFoundry LLC and verified before the
package is published.
