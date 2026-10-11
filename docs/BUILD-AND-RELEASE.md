# Build, install and release

## Option A — GitHub builds it for you (recommended)

Every push to `main` runs [`.github/workflows/android.yml`](../.github/workflows/android.yml):

1. **Core tests** — all unit tests with xUnit.
2. **Android** — installs the MAUI Android workload and Android SDK, builds a signed **APK** and
   **AAB**, uploads them as a run artifact, and publishes them to a release named `latest`.

On the phone, open this link and tap the APK to install or update:

```
https://github.com/SeHassan321/egyptian-voice-clock/releases/download/latest/EgyptianVoiceClock.apk
```

The first time, Android asks to allow installs from your browser or file manager
(*Install unknown apps*). Allow it for that app only.

### Signing in CI

Updates only install over the previous version when both are signed with the same key.

- **Without any setup**, the first CI run generates a key and keeps it in the GitHub Actions cache
  (never in the repository). Later runs reuse it, so each new APK installs over the previous one.
  GitHub evicts caches that are unused for 7 days; after a long pause a new key may be generated,
  and the phone then needs one uninstall/reinstall (export a backup first).
- **For a permanent key (and Google Play)**, add these repository secrets
  (*Settings → Secrets and variables → Actions*). They then take priority over the cached key:

| Secret | Value |
|---|---|
| `ANDROID_KEYSTORE_BASE64` | the keystore file, base64-encoded (`base64 -w0 release.keystore`) |
| `ANDROID_KEYSTORE_PASSWORD` | keystore password |
| `ANDROID_KEY_ALIAS` | key alias (e.g. `voiceclock`) |
| `ANDROID_KEY_PASSWORD` | key password (may equal the keystore password) |

Create the keystore once on any computer with a JDK and keep a safe offline copy:

```bash
keytool -genkeypair -v -keystore release.keystore -storetype PKCS12 -alias voiceclock \
  -keyalg RSA -keysize 2048 -validity 10000
```

## Option B — build on your own computer

### Prerequisites

| Tool | Version | Notes |
|---|---|---|
| .NET SDK | 10.0 (latest patch) | https://dotnet.microsoft.com/download |
| MAUI Android workload | matches the SDK | `dotnet workload install maui-android` |
| JDK | 21 (17 also works) | Microsoft Build of OpenJDK recommended |
| Android SDK | platform API 36, build-tools, platform-tools | installed by the command below |

Check what is installed:

```bash
dotnet --list-sdks
dotnet workload list
java -version
```

Install everything for this project (Windows, macOS or Linux):

```bash
dotnet workload install maui-android
dotnet build src/VoiceClock.App/VoiceClock.App.csproj -t:InstallAndroidDependencies -f net10.0-android \
  -p:AndroidSdkDirectory=<android-sdk-path> -p:JavaSdkDirectory=<jdk-path> -p:AcceptAndroidSDKLicenses=True
```

Typical paths: Windows `%LOCALAPPDATA%\Android\Sdk`, macOS `~/Library/Android/sdk`, Linux
`~/Android/Sdk`. Visual Studio 2026 with the *.NET MAUI* workload installs all of this for you.

### Run the tests

```bash
dotnet test tests/VoiceClock.Core.Tests/VoiceClock.Core.Tests.csproj
```

The tests need only the .NET SDK (no Android tools).

### Debug APK

```bash
dotnet build src/VoiceClock.App/VoiceClock.App.csproj -f net10.0-android -c Debug
```

Output: `src/VoiceClock.App/bin/Debug/net10.0-android/com.egyptianvoiceclock.app-Signed.apk`
(signed with your machine's debug key).

### Install on a physical phone

1. On the phone: *Settings → About phone* → tap *Build number* 7 times; then
   *Settings → Developer options* → enable *USB debugging*.
2. Connect by USB and accept the "Allow USB debugging" prompt.
3. Check and install:

```bash
adb devices
adb install -r src/VoiceClock.App/bin/Debug/net10.0-android/com.egyptianvoiceclock.app-Signed.apk
```

Or build, deploy and launch in one step: `dotnet build src/VoiceClock.App -f net10.0-android -t:Run`.
`adb` is in `<android-sdk>/platform-tools`.

### Signed release APK and AAB

Nothing secret is stored in the project; pass the key at build time. The `env:` prefix makes the
build read passwords from environment variables instead of the command line:

```bash
export ANDROID_KEYSTORE_PASSWORD='…'
export ANDROID_KEY_PASSWORD='…'
dotnet publish src/VoiceClock.App/VoiceClock.App.csproj -f net10.0-android -c Release \
  -p:ApplicationVersion=2 \
  -p:AndroidSigningKeyStore=/full/path/release.keystore \
  -p:AndroidSigningKeyAlias=voiceclock \
  -p:AndroidSigningStorePass=env:ANDROID_KEYSTORE_PASSWORD \
  -p:AndroidSigningKeyPass=env:ANDROID_KEY_PASSWORD \
  -o artifacts/publish
```

Outputs in `artifacts/publish`:

- `com.egyptianvoiceclock.app-Signed.apk` — install directly on phones.
- `com.egyptianvoiceclock.app-Signed.aab` — upload to Google Play (enroll in *Play App Signing*;
  your keystore then acts as the upload key).

Increase `ApplicationVersion` (an integer) for every release; Android refuses to install an update
with the same or a lower number. `ApplicationDisplayVersion` (e.g. `1.0.0`) is what users see.

## Project facts

| Item | Value |
|---|---|
| Application id | `com.egyptianvoiceclock.app` |
| Target framework | `net10.0-android` (Android 16, API 36) |
| Minimum Android | 8.0 (API 26): notification channels, foreground-service start and audio-focus requests |
| Permissions | microphone (asked only when recording), vibrate, foreground service (widget speech) |
| Not requested | internet, storage, location, contacts, phone state |
| Release packaging | trimmed, AAB + APK, arm64-v8a only (all phones since about 2017; Debug builds also include x86_64 for the emulator) |

## Verified environments

- **Core library and all unit tests**: built and run with .NET SDK 10.0.112 on Ubuntu 24.04, and on
  GitHub's `ubuntu-24.04` runner with the latest .NET 10 SDK and real xUnit.
- **Android app**: built by GitHub Actions on `ubuntu-24.04` (JDK 21, MAUI Android workload,
  Android SDK API 36). The original development sandbox had no access to NuGet or Google's
  Android SDK servers, which is why the Android build runs in CI.
