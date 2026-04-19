plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.android)
    alias(libs.plugins.kotlin.compose)
    alias(libs.plugins.kotlin.serialization)
    alias(libs.plugins.kover)
}

android {
    namespace = "com.mtschoen.windowstream.viewer"
    compileSdk = 36

    defaultConfig {
        applicationId = "com.mtschoen.windowstream.viewer"
        minSdk = 34
        targetSdk = 36
        versionCode = 1
        versionName = "0.1.0"
        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions {
        jvmTarget = "17"
    }
    buildFeatures {
        compose = true
    }
    testOptions {
        unitTests.all { it.useJUnitPlatform() }
    }
}

dependencies {
    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.lifecycle.runtime.ktx)
    implementation(libs.androidx.activity.compose)
    implementation(platform(libs.androidx.compose.bom))
    implementation(libs.androidx.compose.ui)
    implementation(libs.androidx.compose.material3)
    implementation(libs.androidx.xr.scenecore)
    implementation(libs.androidx.xr.compose)
    implementation(libs.kotlinx.coroutines.core)
    implementation(libs.kotlinx.coroutines.android)
    implementation(libs.kotlinx.serialization.json)

    testImplementation(libs.junit.jupiter)
    testImplementation(libs.kotlinx.coroutines.test)
    testImplementation(libs.mockk)

    androidTestImplementation(libs.junit.jupiter)
    androidTestImplementation(libs.kotlinx.coroutines.test)
}

kover {
    reports {
        filters {
            excludes {
                // Lifecycle entry points are not unit-testable on the JVM.
                classes(
                    "com.mtschoen.windowstream.viewer.app.WindowStreamViewerApplication",
                    "com.mtschoen.windowstream.viewer.app.MainActivity"
                )
            }
        }
        verify {
            rule {
                @Suppress("UnstableApiUsage")
                minBound(100, kotlinx.kover.gradle.plugin.dsl.CoverageUnit.LINE)
                @Suppress("UnstableApiUsage")
                minBound(100, kotlinx.kover.gradle.plugin.dsl.CoverageUnit.BRANCH)
            }
        }
    }
}
