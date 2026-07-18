# Release signing and notarization

The release workflow (`.github/workflows/release.yml`) builds, signs, and
publishes every artifact when a `vX.Y.Z` tag is pushed. Signing is driven
entirely by repository secrets — **every signing step degrades gracefully
when its secrets are absent**, so releases always succeed; they just ship
with weaker trust:

| Secrets configured | macOS result | Windows result |
| --- | --- | --- |
| none | ad-hoc signed; Gatekeeper needs right-click → Open | unsigned; SmartScreen warns |
| macOS cert only | Developer ID + hardened runtime; Gatekeeper still warns (not notarized) | — |
| macOS cert + notarization | notarized and stapled; opens cleanly | — |
| Windows cert | — | Authenticode-signed; SmartScreen warning fades as reputation builds |

## macOS: Developer ID + notarization

Requires a paid Apple Developer Program membership.

1. In Xcode or developer.apple.com, create a **Developer ID Application**
   certificate and export it (with private key) as a `.p12`.
2. Create an **app-specific password** for your Apple ID at
   appleid.apple.com (notarytool cannot use your real password).
3. Find your **Team ID** on the developer.apple.com membership page.

Repository secrets:

| Secret | Value |
| --- | --- |
| `MACOS_CERT_P12_BASE64` | `base64 < certificate.p12` |
| `MACOS_CERT_PASSWORD` | the `.p12` export password |
| `APPLE_ID` | Apple ID email of the account |
| `APPLE_TEAM_ID` | 10-character team ID |
| `APPLE_APP_PASSWORD` | the app-specific password |

The workflow signs the CLI binaries and `.app` bundles with the hardened
runtime and the entitlements in `packaging/macos/entitlements.plist`
(the .NET JIT needs them), then submits to Apple notarization and staples
the tickets to the `.app` bundles. Bare CLI binaries cannot carry a
stapled ticket — Gatekeeper verifies them online, which is normal.

## Windows: Authenticode

Any code-signing certificate works (OV or EV; EV builds SmartScreen
reputation immediately, OV over time). Signing runs on the macOS runner
via `osslsigncode`, so no Windows runner is needed.

| Secret | Value |
| --- | --- |
| `WINDOWS_CERT_PFX_BASE64` | `base64 < certificate.pfx` |
| `WINDOWS_CERT_PASSWORD` | the `.pfx` password |

Note: cloud/HSM-based signing services (e.g. Azure Trusted Signing) need
a different pipeline than a `.pfx` secret; revisit the workflow if the
certificate ever moves to one.

## Flatpak

The `flatpak` job needs no secrets: it builds
`flashkit-md-vX.Y.Z-{x86_64,aarch64}.flatpak` from
`packaging/flatpak/io.github.jfryman.FlashKitMD.yml` after the release is
created (aarch64 natively on GitHub's arm64 runners), uploads them, and
appends their checksums to `SHA256SUMS`. The same
build runs on every push to main (CI `flatpak` job) so manifest breakage
is caught before release day. Submitting the app to Flathub is a separate
manual process (a PR to flathub/flathub referencing this manifest); the
manifest and AppStream metainfo are written to be Flathub-ready.
