# JellyFetch Android companion

A minimal companion app for the [JellyFetch](../) Jellyfin plugin. It does three things:

- **Catch links and files** shared or opened from other apps — a video URL shared from the
  SVT Play or YouTube app, a link shared from a browser, a tapped `magnet:` link, or an opened
  `.torrent` file — and submit them to your JellyFetch plugin.
- **Settings**: the server URL + API key to reach the plugin.
- **Dashboard**: a live list of downloads with progress, and cancel/retry/remove actions.

Media playback and library browsing are **not** here — the official Jellyfin app does that.

## Setup

1. Install the debug APK (from the `android-latest` GitHub release, or build it yourself — see below).
2. Open the app → menu (⋮) → **Settings**.
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

> A successful build proves the app **compiles** — it does not verify that share/open intents
> resolve from real sender apps. Confirm those on-device: share from SVT Play / YouTube / a
> browser, tap a `magnet:` link, and open a `.torrent` file, and check each lands on the dashboard.
