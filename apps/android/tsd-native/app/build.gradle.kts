plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

fun localSecret(name: String): String? =
    providers.gradleProperty(name)
        .orElse(providers.environmentVariable(name))
        .orNull
        ?.takeIf { it.isNotBlank() }

val releaseStoreFile = localSecret("FLOWSTOCK_TSD_RELEASE_STORE_FILE")
val releaseStorePassword = localSecret("FLOWSTOCK_TSD_RELEASE_STORE_PASSWORD")
val releaseKeyAlias = localSecret("FLOWSTOCK_TSD_RELEASE_KEY_ALIAS")
val releaseKeyPassword = localSecret("FLOWSTOCK_TSD_RELEASE_KEY_PASSWORD")
val releaseSigningConfigured = listOf(
    releaseStoreFile,
    releaseStorePassword,
    releaseKeyAlias,
    releaseKeyPassword,
).all { it != null }

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
    }

    buildFeatures {
        buildConfig = true
    }

    signingConfigs {
        if (releaseSigningConfigured) {
            create("flowstockRelease") {
                storeFile = file(releaseStoreFile!!)
                storePassword = releaseStorePassword
                keyAlias = releaseKeyAlias
                keyPassword = releaseKeyPassword
            }
        }
    }

    buildTypes {
        release {
            if (releaseSigningConfigured) {
                signingConfig = signingConfigs.getByName("flowstockRelease")
            }
        }
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
    testImplementation("org.robolectric:robolectric:4.11.1")
}

val verifyFlowStockReleaseSigning by tasks.registering {
    doLast {
        if (!releaseSigningConfigured) {
            throw GradleException(
                "Release signing is required. Provide FLOWSTOCK_TSD_RELEASE_STORE_FILE, " +
                    "FLOWSTOCK_TSD_RELEASE_STORE_PASSWORD, FLOWSTOCK_TSD_RELEASE_KEY_ALIAS, " +
                    "and FLOWSTOCK_TSD_RELEASE_KEY_PASSWORD as environment variables or Gradle properties.",
            )
        }
    }
}

tasks.matching { it.name == "packageRelease" || it.name == "assembleRelease" }.configureEach {
    dependsOn(verifyFlowStockReleaseSigning)
}
