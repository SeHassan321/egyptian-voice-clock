# The default voice (voice pack)

**Current default voice:** Hassan's recordings from 12 October 2026 (73 clips: all 71 required plus
«صباحًا» and «مساءً»), in `src/VoiceClock.App/Resources/Raw/voicepack.zip`. The test
`BundledVoicePackTests` fails the build if this file is ever incomplete or damaged.

The app ships with a default voice recorded by the owner. Anyone can still re-record any clip on
their own phone; their recording takes priority, and «رجّع الأساسي» (restore default) brings the
shipped clip back.

## Make the default voice (first time)

1. Install the app (see [BUILD-AND-RELEASE.md](BUILD-AND-RELEASE.md)); it starts with no voice.
2. Open the settings button (bottom corner) → «سجّل اللي ناقص». The recorder shows each sentence;
   press «ابدأ التسجيل», say it, «وقّف التسجيل», listen, «احفظ». It moves to the next clip
   automatically. 71 clips are required (73 if you want «صباحًا / مساءً» available).
3. Press «راجع كل الأوقات والتسجيلات». It must say all 720 times are fine.
4. Press «ابعت الصوت كامل (عشان ينزل مع التطبيق)». This creates **`voicepack.zip`** (the exact name
   the build expects) and opens the share sheet. Send it to yourself (Drive, e-mail, WhatsApp…).
5. Put the file at `src/VoiceClock.App/Resources/Raw/voicepack.zip`. On GitHub: open that folder →
   *Add file* → *Upload files*. Committing it starts a new build automatically.
6. Install the new build over the old one. The clock now speaks with the bundled voice even on a
   phone that never recorded anything.

> **Privacy:** the repository is public, so a committed `voicepack.zip` (your recorded voice) is
> downloadable by anyone, and so is the APK. Make the repository private in GitHub settings if you
> prefer; the build works the same way.

## Updating the default voice later

Re-record the clips you want to change, then repeat steps 4–6. Phones that recorded their own
version of a clip keep their own recording until they choose «رجّع الأساسي».

## File format

A standard zip file:

```
voicepack.json          manifest
clips/hour.01.wav       one WAV per clip, named by its stable id
clips/minute.43.wav
...
```

`voicepack.json`:

```json
{
  "format": "egyptian-voice-clock/voice-pack",
  "schemaVersion": 1,
  "createdAtUtc": "2026-10-11T12:00:00+00:00",
  "appVersion": "1.0.0 (5)",
  "locale": "ar-EG",
  "clips": [
    { "id": "hour.01", "file": "clips/hour.01.wav", "sha256": "…", "durationMs": 820, "text": "الساعة واحدة" }
  ]
}
```

Audio is mono 16-bit PCM WAV (44.1 kHz when recorded by the app).

When the app prepares the bundled voice (once per app build), each clip goes through the same
cleaning as a new recording: an 80 Hz low-cut filter removes rumble and mains hum, and silence or
noise before and after the voice is trimmed to about 40 ms / 110 ms, keeping soft consonants such as
the «ص» in «صباحًا». Recordings stored on a phone by an older build are cleaned the same way when
they are loaded; the stored files are never modified.

## Validation on import

The same rules apply to the bundled pack and to restoring a backup:

- must be a zip with a `voicepack.json` of the right format and a supported version, or the whole
  file is rejected;
- only ids known to the app are accepted, and only from `clips/<id>.wav` (path tricks are ignored);
- each clip's SHA-256 must match the manifest;
- sizes are capped (8 MB per clip, 300 MB total after decompression) so a malicious zip cannot
  exhaust memory;
- every clip must decode and pass the same checks as a fresh recording (not empty, contains
  speech, not too short or long); bad clips are skipped and reported, good ones are kept.

## Backups

«احفظ نسخة من تسجيلاتي» / «ابعت نسخة من تسجيلاتي» save only the recordings made on this phone,
through the system file picker or share sheet (no storage permission). «رجّع نسخة احتياطية»
restores such a file; clips identical to the shipped default are not duplicated.
