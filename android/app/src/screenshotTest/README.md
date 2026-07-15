# Compose Preview screenshot tests

This source set holds the JellyFetch dashboard's `@PreviewTest` composables
(`DashboardPreviews.kt`) for the `com.android.compose.screenshot` plugin. They
render the stateless `DashboardScreen` in every explicit state
(loading / empty / error / populated, plus not-configured and stale-with-error)
in **both** the light and dark **fuchsia** theme.

## Tasks

```bash
# Generate/refresh reference PNGs (writes app/src/screenshotTestDebug/reference/):
./gradlew :app:updateDebugScreenshotTest \
    -Pandroid.aapt2FromMavenOverride=/opt/android-sdk/build-tools/36.0.0/aapt2

# Diff current render against the committed references (CI gate):
./gradlew :app:validateDebugScreenshotTest \
    -Pandroid.aapt2FromMavenOverride=/opt/android-sdk/build-tools/36.0.0/aapt2
```

## ⚠ arm64 Linux caveat (this dev host) — render must run on x86-64

The **render** step of these tasks does NOT work on this arm64 dev host, and this
is an **upstream Google gap, not a project bug**:

- The screenshot plugin renders via **layoutlib**, which loads the native libs
  `layoutlib_jni.so` + `libandroid_runtime.so`. Google ships these **x86-64 only**
  (there is no official or community **arm64** layoutlib build — Google issue
  `227219818`, open for years). The exact same wall blocks Paparazzi and
  Roborazzi/RNG; all three JVM host renderers use layoutlib.
- On arm64 the render fails with
  `com.android.tools.idea.layoutlib.RenderingException: The rendering library
  could not be initialized`.

What DOES work on arm64: everything up to the render — the `screenshotTest` source
set **compiles**, the plugin **discovers** these `@PreviewTest` previews, and the
variant **packages**. Verified: `:app:compileDebugScreenshotTestKotlin` is green.

**To actually generate the reference PNGs, run `updateDebugScreenshotTest` on an
x86-64 host** (a normal x86-64 CI runner or dev machine) — the previews and config
here are correct and will render there unchanged. The Android CI workflow
(`android-ci.yml`, x86-64 `ubuntu-latest`) is the intended place to produce these
references — see [Rendering on CI](#rendering-on-ci-x86-64) below.

(The `-Pandroid.aapt2FromMavenOverride` flag above is the arm64-host aapt2
override; on an x86-64 host you omit it — the Maven-bundled aapt2 works there.)

## Rendering on an x86-64 host (the drop-in recipe)

layoutlib works out of the box on x86-64. Two ways to render:

### Rendering on CI (x86-64)

`android-ci.yml` has a **`render-screenshots`** job, `workflow_dispatch`-only
(GitHub → Actions → "Android CI" → Run workflow). It runs on x86-64
`ubuntu-latest`, executes `:app:updateDebugScreenshotTest`, and uploads the PNGs
as the **`jellyfetch-android-screenshots`** artifact (download it from the run
summary to view the rendered fuchsia dashboard). It is manual-only because
rendering is slower than the compile/test path and adds nothing to the PR gate —
the previews already compile on arm64.

### Rendering on a networked x86-64 box / VM (future drop-in)

When an x86-64 machine is available (same Docker network as this dev host, or any
x86-64 Linux box), it becomes a drop-in renderer with no round-trip through CI.

**1. Toolchain**
- **JDK 17** — AGP/Gradle reject the newer JDK line (e.g. 25). Match the app's
  JVM target.
- **Android SDK**: `compileSdk 36` → install `platforms;android-36` and
  `build-tools;36.0.0` via `sdkmanager`. (layoutlib's classpath pulls
  `android-36/android.jar` + `build-tools/36.0.0/core-lambda-stubs.jar`.)

**2. Native deps layoutlib needs (headless graphics/fonts)** — on a bare
Debian/Ubuntu these are the libs a fresh render-init fails without:

```bash
sudo apt-get install -y --no-install-recommends \
    libfreetype6 libfontconfig1 fontconfig fonts-dejavu-core \
    libxext6 libxrender1 libxtst6 libxi6
```

(freetype + fontconfig + a real fallback font, plus the X/AWT shared libs the
JDK's headless AWT still dlopen-links.)

**3. Render command — NO aapt2 override on x86-64:**

```bash
# Generate/refresh the reference PNGs:
./gradlew :app:updateDebugScreenshotTest --stacktrace

# Diff against committed references (the CI-style gate):
./gradlew :app:validateDebugScreenshotTest --stacktrace
```

Omit `-Pandroid.aapt2FromMavenOverride` entirely — that flag is the arm64
dev-host workaround; on x86-64 AGP resolves the correct aapt2 from Maven.

**4. Where the PNGs land** (authoritative — read from the plugin's generated
`build/tmp/updateDebugScreenshotTest/preview_screenshot_config.properties`):

| Output | Path |
|---|---|
| Reference PNGs (what `update` writes; the durable oracle) | `app/src/screenshotTestDebug/reference/` |
| Freshly-rendered PNGs (this run) | `app/build/outputs/screenshotTest-results/preview/debug/rendered/` |
| Diffs (from `validate`) | `app/build/outputs/screenshotTest-results/preview/debug/diffs/` |
