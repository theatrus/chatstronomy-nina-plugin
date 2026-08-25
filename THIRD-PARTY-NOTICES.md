# Third-party notices

The Chatstronomy N.I.N.A. plugin is licensed under Apache-2.0.

The build references the N.I.N.A. plugin API through the `NINA.Plugin` NuGet
package. N.I.N.A. is distributed by its respective authors under the license in
the upstream N.I.N.A. repository.

Release archives may bundle the lean `chatstronomy` Windows runtime from
[`theatrus/chatstronomy`](https://github.com/theatrus/chatstronomy). That runtime
is an Apache-2.0 Rust application and includes its own transitive open-source
dependencies. The exact backend release and SHA-256 hashes are recorded in
`runtime.lock.json` and the downloaded `chatstronomy-runtime-manifest.json`.

The bundled runtime embeds Liberation Sans when rendering autofocus and guider
charts. Liberation Sans includes digitized data copyright (c) 2010 Google
Corporation and copyright (c) 2012 Red Hat, Inc., and is distributed under the
SIL Open Font License, Version 1.1. The complete copyright notice and license
are included beside the runtime at `runtime/LiberationSans-LICENSE`; the source
copy is maintained at `licenses/LiberationSans-LICENSE`.

Development may use AI-assisted tools. All changes remain subject to human
review, repository tests, and the same licensing and contribution requirements
as manually authored changes.
