plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    // Compose Compiler plugin — version pinned in the root build.gradle.kts to
    // match Kotlin (2.2.10). Applying it is what makes @Composable compile.
    id("org.jetbrains.kotlin.plugin.compose")
    // Screenshot testing plugin — adds the screenshotTest source set + the
    // <variant>UpdateScreenshotTest / <variant>ValidateScreenshotTest tasks.
    id("com.android.compose.screenshot")
}

android {
    namespace = "se.lohnn.jellyfetch"
    // Bumped 34 -> 36 for the Compose migration: AGP 9.2 supports up to API 37,
    // and Compose 1.11.x (the stable line, BOM 2026.06.00 below) links against a
    // compileSdk >= 35. 36 is installed via sdkmanager on this host and is the
    // safe stable choice (Compose 1.12.0+ would force compileSdk 37 — not adopted
    // here). targetSdk stays 34 deliberately: compileSdk is a build/link concern,
    // targetSdk is a runtime-behavior contract we haven't re-tested on device.
    compileSdk = 36

    defaultConfig {
        applicationId = "se.lohnn.jellyfetch"
        minSdk = 24
        targetSdk = 34
        versionCode = 1
        versionName = "0.1"
    }

    // Release signing (W-036 / CI wiring): driven entirely by env vars so no
    // key material or passwords are ever committed. CI (android-ci.yml) decodes
    // the static release keystore to a path OUTSIDE the repo tree and exports
    // these four vars before invoking gradle. Locally, when they're absent
    // (or the keystore file doesn't exist at that path), releaseSigning stays
    // null and the release buildType is left WITHOUT a signingConfig — this
    // must not throw at configuration time, it just means a local
    // assembleRelease produces an unsigned APK, which is fine for local dev.
    val keystorePath = System.getenv("JELLYFETCH_KEYSTORE_PATH")
    val releaseSigning = if (keystorePath != null && file(keystorePath).exists()) {
        signingConfigs.create("release") {
            storeFile = file(keystorePath)
            storePassword = System.getenv("JELLYFETCH_KEYSTORE_PASSWORD")
            keyAlias = System.getenv("JELLYFETCH_KEY_ALIAS")
            keyPassword = System.getenv("JELLYFETCH_KEY_PASSWORD")
        }
    } else {
        null
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            signingConfig = releaseSigning
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlinOptions {
        jvmTarget = "17"
    }

    buildFeatures {
        // Turn on the Compose toolchain for this module (the migration).
        compose = true
    }

    // The screenshot plugin, with this project's android.newDsl=false, reads the
    // enableScreenshotTest opt-in from the module's experimentalProperties (not
    // just the gradle.properties flag). Required for it to register the
    // screenshotTest source set + Update/Validate tasks.
    @Suppress("UnstableApiUsage")
    experimentalProperties["android.experimental.enableScreenshotTest"] = true

    testOptions {
        unitTests {
            isIncludeAndroidResources = true
        }
    }
}

dependencies {
    // Deliberately tiny footprint (I-082): framework widgets + org.json +
    // HttpURLConnection + Executors cover everything. PASS 2 completed the
    // Views->Compose migration and DELETED the last classic-Views screen
    // (AllItems' ListView), so androidx.swiperefreshlayout is gone — Compose's
    // own PullToRefreshBox / LazyColumn provide pull-to-refresh and paging
    // natively, and material-icons-* is deliberately NOT added (text-glyph
    // convention, see ui/Glyphs.kt).

    // --- Jetpack Compose stack (Views->Compose rebuild) ---
    // The BOM pins every Compose artifact to one compatible snapshot; individual
    // Compose deps below carry NO version — the BOM supplies it. 2026.06.00 is the
    // latest STABLE BOM (Compose 1.11.4 / material3 1.4.0) that does not force
    // compileSdk 37 (that arrives with Compose 1.12.0).
    val composeBom = platform("androidx.compose:compose-bom:2026.06.00")
    implementation(composeBom)
    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.ui:ui-tooling-preview")
    implementation("androidx.compose.material3:material3")
    // Compose entry point for an Activity (setContent { }).
    implementation("androidx.activity:activity-compose:1.9.3")
    // viewModel() in composables + collectAsStateWithLifecycle().
    implementation("androidx.lifecycle:lifecycle-viewmodel-compose:2.8.7")
    implementation("androidx.lifecycle:lifecycle-runtime-compose:2.8.7")
    // ViewModel base class (androidx.lifecycle.ViewModel + factory DSL).
    implementation("androidx.lifecycle:lifecycle-viewmodel:2.8.7")
    // I-082: a transitive classpath dep does NOT guarantee COMPILE visibility.
    // The dashboard state-holder uses kotlinx.coroutines Flow as its Compose-
    // observable surface, so declare coroutines-core EXPLICITLY rather than
    // relying on it leaking through lifecycle. (The API transport itself stays
    // callback-based — no coroutines in HttpJellyFetchApi.)
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-core:1.9.0")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.9.0")

    // Tooling: @Preview support in the IDE; the debug-only ui-tooling backend
    // is what the screenshot renderer (and interactive preview) uses to render.
    debugImplementation("androidx.compose.ui:ui-tooling")

    // Test-only — never ships in the APK, so this doesn't compromise the
    // minimal runtime footprint above. Plain Mockito (no Robolectric): the
    // share/ decision logic (IntentResolver.resolveBlocking) is exercised by
    // mocking the ContentResolver/Intent/Uri seam directly rather than
    // spinning up a shadow Android runtime — these are pure JVM unit tests
    // (testDebugUnitTest), no emulator/instrumentation required.
    testImplementation("junit:junit:4.13.2")
    // I-082: declare coroutines EXPLICITLY on the test classpath too (the
    // DashboardViewModel test drives its Flow surface with runBlocking/launch).
    testImplementation("org.jetbrains.kotlinx:kotlinx-coroutines-core:1.9.0")
    testImplementation("org.mockito:mockito-core:5.12.0")
    testImplementation("org.mockito.kotlin:mockito-kotlin:5.4.0")

    // Robolectric is the one deliberate exception to "no shadow Android runtime"
    // above: the dashboard's scroll-back-up bug (SwipeRefreshLayout.canChildScrollUp()
    // resolving the wrong "target" child) lives entirely in real android.view /
    // androidx.swiperefreshlayout object behavior that plain-Mockito JVM tests can't
    // exercise (the compile-time android.jar is stub-only). Robolectric runs the real
    // ViewGroup/SwipeRefreshLayout code against a simulated framework, still on the
    // plain testDebugUnitTest JVM task — no emulator/device required.
    testImplementation("org.robolectric:robolectric:4.16.1")
    // ApplicationProvider only — for DarkModeColorsTest's @Config(qualifiers
    // = "night") resource-resolution sanity check.
    testImplementation("androidx.test:core:1.6.1")

    // --- Compose preview screenshot testing (screenshotTest source set) ---
    // These deps are consumed ONLY by src/screenshotTest and never ship in the
    // APK. ui-tooling provides @Preview; the screenshot-validation library is the
    // annotation surface (@PreviewTest) the plugin scans for. compose-bom pins
    // their versions to the same snapshot as the app deps above.
    screenshotTestImplementation(platform("androidx.compose:compose-bom:2026.06.00"))
    screenshotTestImplementation("androidx.compose.ui:ui-tooling")
    screenshotTestImplementation("androidx.compose.ui:ui-test-manifest")
    // Provides the @PreviewTest annotation (com.android.tools.screenshot) that
    // alpha15 REQUIRES on every screenshot preview — unannotated previews are
    // skipped. Version tracks the screenshot plugin (0.0.1-alpha15).
    screenshotTestImplementation("com.android.tools.screenshot:screenshot-validation-api:0.0.1-alpha15")
}
