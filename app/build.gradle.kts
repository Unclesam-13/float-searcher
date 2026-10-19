plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "com.huaci.ai"
    compileSdk = 34

    defaultConfig {
        applicationId = "com.huaci.ai"
        minSdk = 26          // 系统悬浮窗 TYPE_APPLICATION_OVERLAY 需要 Android 8.0+
        targetSdk = 34
        versionCode = 3
        versionName = "1.2"
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

// 不依赖任何 AndroidX 库：只用系统自带的 View、HttpURLConnection 和 org.json，安装包很小。
dependencies {}
