# libmpv runtime

Run `Tools/prepare-libmpv.ps1` from the repository root to place the pinned x64
libmpv runtime in this directory before debugging or packaging PlayerPlugin.

The loader accepts `libmpv-2.dll`, `mpv-2.dll`, or `mpv-1.dll`. Native binaries
are deliberately not committed. Version, checksum, source, and release-license
notes are tracked in `THIRD-PARTY-NOTICES.md`.
