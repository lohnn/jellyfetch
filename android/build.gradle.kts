// Top-level build file. Plugins are declared here but applied in :app.
plugins {
    id("com.android.application") version "9.2.1" apply false
    id("org.jetbrains.kotlin.android") version "2.2.10" apply false
    // Compose Compiler Gradle plugin — Kotlin 2.0+ manages the Compose compiler
    // in lockstep with the Kotlin compiler, so its version MUST equal the Kotlin
    // version (2.2.10). This replaces the old composeOptions{kotlinCompilerExtensionVersion}.
    id("org.jetbrains.kotlin.plugin.compose") version "2.2.10" apply false
    // Compose Preview screenshot testing (JVM @Preview -> PNG, no emulator).
    // NEW GROUND for the collective: verified plugin id + version against current
    // docs (2026-07). CLI tasks: <variant>UpdateScreenshotTest / ValidateScreenshotTest.
    id("com.android.compose.screenshot") version "0.0.1-alpha15" apply false
}
