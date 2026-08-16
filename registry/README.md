# Chatstronomy development plugin repository

Add this base URL to **N.I.N.A. > Options > General > Plugin Repositories**:

```text
https://raw.githubusercontent.com/theatrus/chatstronomy-nina-plugin/main/registry
```

N.I.N.A. appends `/plugins/manifests` itself. After adding the repository, open
**Plugins > Available**, select **Chatstronomy**, install it, and restart
N.I.N.A.

This repository publishes development packages for testing before they reach
N.I.N.A.'s built-in official plugin repository. The bundled Rust runtime is
release-pinned and signed by StackFoundry LLC; the development
`Chatstronomy.dll` is currently unsigned. Official tagged packages sign and
verify both Windows binaries before packaging.
