import java.util.Properties

plugins {
    id("com.android.application")
}

val signing = rootProject.file("keystore.properties").takeIf { it.exists() }?.let { f ->
    Properties().apply { f.inputStream().use { load(it) } }
}

android {
    namespace = "dev.pentastic"
    compileSdk = 37

    defaultConfig {
        applicationId = "dev.pentastic"
        minSdk = 30
        targetSdk = 37
        versionCode = 3
        versionName = "1.0"
    }
    signingConfigs {
        if (signing != null) create("release") {
            storeFile = rootProject.file(signing.getProperty("storeFile"))
            storePassword = signing.getProperty("storePassword")
            keyAlias = signing.getProperty("keyAlias")
            keyPassword = signing.getProperty("keyPassword")
        }
    }
    buildTypes {
        release {
            signingConfig = signingConfigs.findByName("release")
        }
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
}

dependencies {
    testImplementation("junit:junit:4.13.2")
}
