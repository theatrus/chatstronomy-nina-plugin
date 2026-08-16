# Chatstronomy development plugin repository

Add this base URL to **N.I.N.A. > Options > General > Plugin Repositories**:

```text
https://raw.githubusercontent.com/theatrus/chatstronomy-nina-plugin/main/registry
```

N.I.N.A. appends `/plugins/manifests` itself. After adding the repository, open
**Plugins > Available**, select **Chatstronomy**, install it, and restart
N.I.N.A.

This development repository publishes the current reviewed release before it
reaches N.I.N.A.'s built-in official plugin repository. It does not use a
separate beta package channel. Both the release-pinned Rust runtime and
`Chatstronomy.dll` are signed by StackFoundry LLC and verified before the
package is published.
