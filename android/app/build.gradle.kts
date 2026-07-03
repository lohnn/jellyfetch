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

    buildTypes {
        release {
            isMinifyEnabled = false
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlinOptions {
        jvmTarget = "17"
    }
}

dependencies {
    // Deliberately tiny footprint (I-082): framework widgets + org.json +
    // HttpURLConnection + Executors cover everything. The one androidx dep is
    // pull-to-refresh, which the framework does not provide.
    implementation("androidx.swiperefreshlayout:swiperefreshlayout:1.1.0")
}
