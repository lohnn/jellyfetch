# JellyFetch Android companion

A minimal companion app for the [JellyFetch](../) Jellyfin plugin. It does these things:

- **Catch links and files** shared or opened from other apps — a video URL shared from the
  SVT Play or YouTube app, a link shared from a browser, a tapped `magnet:` link, or an opened
  `.torrent` file — and submit them to your JellyFetch plugin.
- **Settings**: the server URL + API key to reach the plugin.
- **Dashboard**: a live list of downloads with progress, and cancel/retry/remove actions.
- **All library items**: browse/search the items JellyFetch has filed, with poster thumbnails,
  and **correct their metadata** (re-match to the right title, or convert an item's type).

Media playback and library browsing (for watching) are **not** here — the official Jellyfin
app does that.

## UI

The app is built with **Jetpack Compose + Material3**. The colour scheme is a fuchsia
(magenta-seeded) tonal palette with full light/dark support; error/warning affordances stay
red/amber (fuchsia never signals "something wrong"). All screens — dashboard, share-confirm,
settings, job detail, all-items, and the metadata-correction sheet — are Compose.

## Setup

1. Install the debug APK (from the `android-latest` GitHub release, or build it yourself — see below).
2. Open the app → tap the **settings** icon in the top bar → **Settings**.
3. Enter your **Server URL** and **API key**, then tap **Test connection**.

### Server URL — include the port

The Server URL must point at your Jellyfin server **including its port**. For a normal direct
(LAN or Tailscale-IP) connection Jellyfin listens on **`:8096`**, so enter something like:

```
http://192.168.1.10:8096
```

> **Why the port matters.** Without the port, the URL usually reaches the Jellyfin **web UI**
> (or a reverse proxy) rather than the plugin's API. The app talks to the JellyFetch REST API
> under `/Jellyfetch/...`, and a port-less URL will typically hit an HTML page instead — which
> is *not* the API. **Test connection** validates that it actually reached the JellyFetch API
> (not just "some server responded"), so a missing port makes the test **fail** with a hint to
> add `:8096`. That failure is intentional — it's catching a real misconfiguration.

Exceptions:
- If your Jellyfin is fronted by a **reverse proxy on HTTPS (443)**, the port is implicit and a
  bare `https://jellyfin.example.com` is correct — as long as the proxy forwards `/Jellyfetch/*`
  to Jellyfin.
- Use whatever custom port you configured if you changed Jellyfin's default.

### API key

Create one in Jellyfin: **Dashboard → API Keys → +**. Paste it into the app's **API key** field.

**Storage posture:** the key is stored in plain `SharedPreferences` (see `Prefs.kt`), not
`EncryptedSharedPreferences`. This is a deliberate trade-off, not an oversight: the one
`androidx.security.crypto` dependency that would provide it pulls in Tink (a sizeable crypto
library) for a threat this app doesn't materially face — a device backup/root/adb-shell attacker
who can read another app's private-storage `SharedPreferences` file typically also has enough
device access to extract the key some other way. Given this app's deliberately tiny dependency
footprint (I-082), that cost isn't justified today. Revisit if the threat model changes (e.g.
shared/managed devices) — `EncryptedSharedPreferences` is a drop-in-ish replacement for `Prefs`'
`SharedPreferences` instance if that day comes. Rotate any API key that has passed through a
shared debugging/session context as a matter of course (W-036).

### Cleartext traffic (`http://`)

The manifest declares `android:usesCleartextTraffic="true"`. This is required, not a leftover
default: most self-hosted Jellyfin servers are reached over a bare LAN or Tailscale IP
(`http://192.168.1.10:8096`) with no TLS in front of them, and the server URL above is arbitrary
runtime user input — the app has no fixed backend domain to pin. Android's usual tightening tool,
a `networkSecurityConfig` domain allowlist, can't express "cleartext to whatever private address
the user just typed in", so it can't narrow this any further without breaking the primary use
case (plain-http LAN servers). If you front your Jellyfin with HTTPS (reverse proxy on 443), the
app works over `https://` exactly as you'd expect — cleartext is *permitted*, not *required*.

## Build

Standard Android debug build:

```bash
./gradlew assembleDebug
```

The APK lands at `app/build/outputs/apk/debug/app-debug.apk`. CI publishes the same artifact to
the `android-latest` GitHub release on every push to `master`.

Toolchain: AGP 9.2 / Kotlin 2.2.10 / Gradle 9.4.1, `compileSdk 36` (Compose links against
API ≥ 35), Compose BOM `2026.06.00` (Material3 1.4.0), JDK **17** (newer JDKs are rejected by
AGP).

### Building on an arm64 Linux host

Google ships `aapt2` for **x86-64 only**, and the Debian arm64 `aapt2` is too old for API 36
(it segfaults on `android-36`'s `android.jar`). On an arm64 build host you must supply a native
arm64 `aapt2` and point the build at it:

```bash
./gradlew assembleDebug \
  -Pandroid.aapt2FromMavenOverride=/opt/android-sdk/build-tools/36.0.0/aapt2
```

The overridden binary is a native arm64 build (e.g. from
[`Commit451/android-arm-build-tools`](https://github.com/Commit451/android-arm-build-tools),
release `platform-tools-36.0.0`) dropped into `$SDK/build-tools/36.0.0/aapt2`. Keep the override
a **command-line flag only** — never commit it to `gradle.properties`, because it points at an
arm64 path that would break x86-64 and CI builds (where stock `aapt2` is correct).

## Screenshot tests (Compose Preview → PNG)

Every screen has `@Preview`/`@PreviewTest` functions (under `app/src/screenshotTest/`) that render
its meaningful states to reference PNGs via the
[`com.android.compose.screenshot`](https://developer.android.com/studio/preview/compose-screenshot-testing)
plugin — no emulator needed. See `app/src/screenshotTest/README.md` for the full recipe.

```bash
./gradlew :app:updateDebugScreenshotTest      # generate/record reference PNGs
./gradlew :app:validateDebugScreenshotTest    # diff against the recorded references
```

> **The render step runs on x86-64 only.** It uses layoutlib, which Google ships as an x86-64
> native library with **no arm64 build** ([issue 227219818](https://issuetracker.google.com/issues/227219818),
> the same wall Paparazzi/Roborazzi hit). On an arm64 dev host the previews *compile*
> (`compileDebugScreenshotTestKotlin` is green) but cannot render — so the PNGs are produced by
> the **`render-screenshots` CI job** (`workflow_dispatch` on an x86-64 runner), which uploads them
> as the `jellyfetch-android-screenshots` artifact.

> A successful build proves the app **compiles**; a passing screenshot proves a composable
> **renders as authored**. Neither proves on-device behaviour — real dark-mode contrast, touch
> feel, window chrome (e.g. the single top bar), that share/open intents resolve from real sender
> apps, or that live-server metadata correction works. Confirm those on a device against a real
> Jellyfin server: share from SVT Play / YouTube / a browser, tap a `magnet:` link, open a
> `.torrent` file, and check each lands on the dashboard.
