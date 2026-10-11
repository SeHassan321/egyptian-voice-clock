# Egyptian Voice Clock · الساعة الناطقة

An offline Android clock that **speaks the time in Egyptian Arabic, in a real person's recorded voice**,
for people who cannot read numbers or digital clocks. One big button (or a home-screen widget)
says «الساعة اتنين إلا ربع»; no reading is needed.

## بالعربي باختصار

- **التنزيل على الموبايل:** افتح اللينك ده من الموبايل ونزّل الملف وافتحه:
  https://github.com/SeHassan321/egyptian-voice-clock/releases/download/latest/EgyptianVoiceClock.apk
- **الصوت:** صوت حسن نازل مع التطبيق (`src/VoiceClock.App/Resources/Raw/voicepack.zip`)، فالساعة بتتكلم
  من أول ما تتثبّت. أي حد يقدر يعيد تسجيل أي جملة من الإعدادات، و«رجّع الأساسي» يرجّع صوت حسن.
- **تحديث الصوت الأساسي:** سجّل الجمل اللي عايز تغيّرها، ومن الإعدادات ← «ابعت الصوت كامل»، وارفع
  `voicepack.zip` الجديد مكان القديم في نفس الفولدر.
- **الويدجت:** دوسة طويلة على الشاشة الرئيسية ← Widgets ← «الساعة الناطقة» (مقاسه 1×1 وينفع يتكبّر).
  دوسة واحدة عليه تقول الساعة.
- **مثال لطريقة النطق:** 1:25 «واحدة ونص إلا خمسة»، 1:43 «واحدة وتلاتة وأربعين دقيقة»،
  1:45 «اتنين إلا ربع». الجدول الكامل في [docs/ALL-TIMES.md](docs/ALL-TIMES.md).

## Features

- **Voice-first home screen**: a very large "say the time" button, repeat, media volume, setup.
  No digits on screen, TalkBack labels in Arabic, large touch targets, high contrast.
- **Home-screen widget** (1×1, resizable) that speaks the time with one tap, without opening the app.
- **Natural Egyptian Arabic** driven by an editable phrase dictionary; every one of the 720 times is
  defined and tested ([rules](docs/TIME-RULES.md), [all times](docs/ALL-TIMES.md)).
- **Hybrid speech**: reusable recorded pieces, plus optional complete recorded sentences for any time
  that should sound more natural; a missing recording is reported, never faked.
- **Guided recording** of 71 short clips with validation (silence, too short/long, too quiet,
  distorted), a voice low-cut filter (removes rumble and hum), silence trimming that keeps soft
  consonants, preview, and "next missing clip" flow
  ([catalog](docs/RECORDING-CATALOG.md)).
- **Hassan's voice ships in the APK** as the default (all 73 clips), overridable per clip on each phone
  ([voice pack](docs/VOICE-PACK.md)).
- **Backup and restore** through the system file picker or share sheet (no storage permission).
- **Fully offline**: no internet permission, no account, no cloud, no text-to-speech service.
- Optional «صباحًا / مساءً», adjustable pause between words, loudness evening, error beep/vibration.

## Repository layout

```
src/VoiceClock.Core/          platform-independent logic (no package dependencies)
  Rules/speech-rules-ar-EG.json   Egyptian Arabic phrase dictionary (edit words here)
  Time/        TimeSpeechEngine: time → ordered clips (pure and deterministic)
  Catalog/     RecordingCatalog: every clip the rules can need
  Audio/       WAV codec, clip validation and trimming, loudness, gapless composition
  Storage/     VoiceAssetRepository (atomic files, manifest, bundled voice, backup/restore)
  Settings/    versioned JSON settings with migration and corruption recovery
  Playback/    AnnouncementService, AudioSessionGate (one audio activity at a time)
  Recording/   RecordingService (microphone takes, preview, save)
  Validation/  CoverageValidator (all 720 times), SetupStatusCalculator
  ViewModels/  MVVM view models for all screens (tested without Android)
  Resources/   Strings.resx (Arabic UI text, localization-ready)
src/VoiceClock.App/           .NET MAUI Android app
  Pages/       Main, Setup, Recordings checklist, Recorder (XAML)
  Platforms/Android/  AudioRecord/AudioTrack audio, volume, haptics, document pickers, widget
  Resources/Raw/      put voicepack.zip here to ship the default voice
tests/VoiceClock.Core.Tests/  xUnit tests for the core and view models
tools/GenerateDocs/           regenerates docs/ALL-TIMES.md and docs/RECORDING-CATALOG.md
docs/                         rules, catalog, voice pack, build/release, testing
```

## How an announcement works

1. `ILocalClock` reads the phone's local hour and minute.
2. `TimeSpeechEngine.BuildPlan` turns it into clip ids, e.g. `hour.02` + `minute.45`.
3. `AudioPlanResolver` picks a complete recorded sentence if one exists, else the segments, else
   reports exactly what is missing.
4. `VoiceAssetRepository` loads each clip (own recording first, then the bundled default).
5. `AnnouncementComposer` joins them into one buffer with short, configurable pauses and evens out
   their loudness.
6. `AndroidAudioOutput` plays it through `AudioTrack` with transient audio focus and refuses during
   calls; `StreamPlayer` decides success or failure (output error, never started, stopped consuming),
   tested against simulated phones with real AudioTrack quirks.

## Building

- Easiest: push to `main` and GitHub Actions builds the signed APK/AAB and publishes them to the
  `latest` release.
- Locally: .NET 10 SDK + `dotnet workload install maui-android`. Full steps, debug/release builds,
  signing and device installation: [docs/BUILD-AND-RELEASE.md](docs/BUILD-AND-RELEASE.md).

```bash
dotnet test tests/VoiceClock.Core.Tests/VoiceClock.Core.Tests.csproj     # needs only the .NET SDK
dotnet build src/VoiceClock.App/VoiceClock.App.csproj -f net10.0-android # needs the Android workload
```

## Testing

Automated coverage and a manual phone checklist (offline use, persistence, widget, permissions,
calls, backups, TalkBack): [docs/TESTING.md](docs/TESTING.md).

## Design decisions

- **WAV/PCM instead of AAC** for recordings: Android records and plays it identically everywhere,
  and it allows exact validation, safe silence trimming and gap-free joining (an announcement is a
  single continuous stream). The full voice is about 8 MB.
- **Clips include their connector** («وربع», «إلا تلت»), and hour clips include «الساعة», so most
  announcements join just two recordings at natural pause points.
- **No playback-speed setting**: speeding up recorded speech without artifacts is not possible with
  the platform player, so it is omitted rather than distorting the voice.
- **No automatic announcements** in this version (owner's decision); the time is spoken on request.
- **Own MVVM helpers instead of a toolkit**: view models live in the core library and are unit-tested
  without Android.
