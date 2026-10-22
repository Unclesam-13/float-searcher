plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "com.huaci.ai"
    compileSdk = 35

    defaultConfig {
        applicationId = "com.huaci.ai"
        minSdk = 26          // 系统悬浮窗 TYPE_APPLICATION_OVERLAY 需要 Android 8.0+
        targetSdk = 34
        versionCode = 10
        versionName = "1.5"
        // 手机都是 ARM 芯片；去掉模拟器用的 x86 库，安装包小一半
        ndk { abiFilters += listOf("arm64-v8a", "armeabi-v7a") }
    }

    // 固定签名：每次编译出的新版本都能直接覆盖安装，不用先卸载旧版。
    signingConfigs {
        getByName("debug") {
            storeFile = file("huaci-debug.jks")
            storePassword = "huaci123"
            keyAlias = "huaci"
            keyPassword = "huaci123"
        }
    }

    buildTypes {
        debug {
            signingConfig = signingConfigs.getByName("debug")
        }
        release {
            isMinifyEnabled = false
            signingConfig = signingConfigs.getByName("debug")
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

// 界面只用系统自带控件；文字识别用 Google ML Kit 的离线中文模型（不需要联网、不需要谷歌服务）。
dependencies {
    implementation("com.google.mlkit:text-recognition-chinese:16.0.1")
}
