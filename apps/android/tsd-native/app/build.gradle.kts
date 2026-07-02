plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

fun String.asBuildConfigString(): String =
    "\"" + replace("\\", "\\\\").replace("\"", "\\\"") + "\""

val defaultTsdUrl = providers
    .gradleProperty("flowstockTsdUrl")
    .orElse("https://flowstock.invalid/tsd/")

android {
    namespace = "ru.flowstock.tsd"
    compileSdk = 36
    buildToolsVersion = "36.1.0"

    defaultConfig {
        applicationId = "ru.flowstock.tsd"
        minSdk = 24
        targetSdk = 36
        versionCode = 1
        versionName = "0.1.0-poc"

        buildConfigField("String", "DEFAULT_TSD_URL", defaultTsdUrl.get().asBuildConfigString())
    }

    buildFeatures {
        buildConfig = true
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlin {
        compilerOptions {
            jvmTarget.set(org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_17)
        }
    }

    testOptions {
        unitTests.isReturnDefaultValues = true
    }
}

dependencies {
    testImplementation("junit:junit:4.13.2")
    testImplementation("org.json:json:20250517")
}
