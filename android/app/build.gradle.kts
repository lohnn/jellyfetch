plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "se.lohnn.jellyfetch"
    compileSdk = 34

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

    testOptions {
        unitTests {
            isIncludeAndroidResources = true
        }
    }
}

dependencies {
    // Deliberately tiny footprint (I-082): framework widgets + org.json +
    // HttpURLConnection + Executors cover everything. The one androidx dep is
    // pull-to-refresh, which the framework does not provide.
    implementation("androidx.swiperefreshlayout:swiperefreshlayout:1.1.0")

    // Test-only — never ships in the APK, so this doesn't compromise the
    // minimal runtime footprint above. Plain Mockito (no Robolectric): the
    // share/ decision logic (IntentResolver.resolveBlocking) is exercised by
    // mocking the ContentResolver/Intent/Uri seam directly rather than
    // spinning up a shadow Android runtime — these are pure JVM unit tests
    // (testDebugUnitTest), no emulator/instrumentation required.
    testImplementation("junit:junit:4.13.2")
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
}
