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
