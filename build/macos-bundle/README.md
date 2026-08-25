# macOS bundle + the Velopack packaging plan

`make-bundle.sh` builds `Mainguard.app` (Pro) / `Mainguard Client.app` in the exact layout
Velopack packs on macOS, so the dev bundle and the eventual shipped bundle are one shape.

## Today (dev)

```bash
dotnet build -c Release
bash build/macos-bundle/make-bundle.sh            # Pro head → out/Mainguard.app
bash build/macos-bundle/make-bundle.sh --head client
open build/macos-bundle/out/Mainguard.app
```

Dev mode copies the apphost under the bundle name (a renamed copy sidesteps the macOS
name-pinning SIGKILL — AGENTS.md § Developing on macOS) and symlinks the rest of the build
output beside it: a plain `dotnet build` updates the running app; re-run the script only when
the output gains NEW files. The bundle is what makes macOS treat Mainguard as an app at all —
Dock identity, app-menu name, Notification Center attribution, `mainguard://` registration
(Pro head), login items, and eventually notarization all key off it.

## The Velopack lane (when release packaging lands)

Velopack ships macOS apps as a `.app` in a DMG/portable zip with delta updates, exactly like
the Windows lane in `build/velopack/pack.ps1`. The steps, in order, all mechanical except one:

1. `bash build/macos-bundle/make-bundle.sh --publish` — the self-contained bundle
   (`Contents/MacOS` = the osx-arm64 publish, entry executable = the bundle name).
2. `vpk pack --packId Mainguard --packVersion <v> --packDir out/Mainguard.app --mainExe Mainguard
   --runtime osx-arm64` (plus the `client` channel variant, mirroring pack.ps1's two channels).
3. **Signing + notarization — the one human prerequisite.** Requires an Apple Developer ID
   ($99/yr): `codesign` with the Developer ID Application identity (hardened runtime + the
   dotnet JIT entitlement `com.apple.security.cs.allow-jit`), `notarytool submit`, staple.
   Velopack drives all of it via `--signAppIdentity`/`--notaryProfile` once the credentials
   exist. Without this step the DMG works locally but Gatekeeper blocks every download.
4. Updates: the packaged app checks the release feed exactly as the Windows build does; the
   daemon needs nothing extra — `MacDaemonUpdater` already restarts it from the app payload.

Do NOT hand-maintain a second Info.plist for Velopack: `--packDir` consumes the bundle this
script already writes, so plist changes live in one place.
