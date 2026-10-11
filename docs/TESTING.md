# Testing

## Automated tests

```bash
dotnet test tests/VoiceClock.Core.Tests/VoiceClock.Core.Tests.csproj
```

They test real logic, not just construction, without any Android tools:

| Area | What is checked |
|---|---|
| Time engine | all 16 acceptance examples (morning and evening); the owner's exact-minute rules (1:24, 1:25, 1:43…); every one of the 1,440 minutes of a day against independently computed expectations; 720 distinct sentences per 12-hour cycle; 12 → 1 wrap; midnight and noon; صباحًا/مساءً placement; segment order; determinism; seconds ignored |
| Phrase dictionary | all 12 hours and 59 minutes mapped; only 40/45/50/55 use «إلا»; round-minute wording; Egyptian counting forms; changing a word in data changes speech without code; malformed rules rejected with clear reasons |
| Catalog | 74 clips, ids unique and well-formed; every clip any plan can need exists, and every required clip is actually used |
| Audio | WAV read/write (including stereo, extra chunks, extensible format); truncated/foreign/empty files rejected; 80 Hz low-cut filter (25/50 Hz removed, voice band unchanged, DC removed); rumble before a word trimmed; soft hissing consonants kept at the start and end; isolated clicks ignored; silence trimming keeps every loud sample of the speech; fades; too short/long/silent/quiet/clipped detection; loudness normalization limits; gapless composition with exact gaps; resampling |
| Playback completion | `StreamPlayer` against a simulated Android AudioTrack: typical phones, deep-buffer phones that hold back the last partial period, position reading 0 after stop (the cause of the false "playback failed" in builds 1–4), slow Bluetooth start, output that never starts or stops midway, write errors, focus loss while writing or draining, cancellation, audio shorter than the buffer, a position that never moves |
| Shipped voice | the bundled `voicepack.zip` passes import validation, contains every required clip, covers all 1,440 minutes, and every prepared clip is clean speech without long leading/trailing silence |
| Storage | persistence across restarts; manifest contents; atomic replace (interrupted write leaves the previous recording intact); unrelated clips untouched; deleting/restoring defaults; damaged files detected and the default used instead; corrupt or missing manifest rebuilt; externally deleted files; concurrent saves of different and of the same clip; recordings from older builds cleaned on load without touching the files; bundled voice re-prepared when the processing improves |
| Bundled voice | extracted once per build; re-extracted when the build changes; broken pack reported without crashing |
| Backup/restore | round trip; defaults not duplicated; rejection of non-zips, wrong format, future versions, missing manifest; path traversal, unknown ids, checksum mismatch, duplicates, silent audio and oversized (zip-bomb) entries skipped |
| Settings | defaults; persistence; corruption kept aside and defaults restored; migration from unversioned files; newer files read but not overwritten; clamping |
| Announcements | correct clips and order; missing recordings reported (never "success"); recorded error message only for missing recordings, beep for playback failures; full-sentence priority and fallback; duplicate requests ignored; cancellation; every player failure mapped to a non-success result; repeat replays identical audio; no speech while recording |
| Recording | one session at a time; take trimming; unusable takes cannot be saved; microphone unavailable; cancel releases the microphone; replacing another clip while recording |
| Coverage | all 720 (or 1,440 with صباحًا/مساءً) times checked; one missing minute leaves exactly 12 times uncovered; a missing hour leaves exactly 60; optional sentences never hide a missing word |
| View models | big button, muted volume, setup messages, double taps, navigation, errors become friendly messages; setup progress and completion; backup/restore flows; recorder permission flows (denied, permanently denied → settings); guided "save then next missing" |

## Manual checklist on a phone

Use a real phone; emulators often have no usable microphone.

### First run and recording
- [ ] Install the APK; the app opens on the big amber button without asking for any permission.
- [ ] Press the big button before recording anything: no speech, a short beep and vibration, and a
      note that recordings are missing.
- [ ] Open settings (bottom corner) → «سجّل اللي ناقص». Microphone permission is asked only now.
- [ ] Deny once → message explains the microphone is needed. Press record again → asked again.
- [ ] Deny with "don't ask again" → the app offers «افتح الإعدادات», which opens the app's settings.
- [ ] Record, listen («اسمع التسجيل الجديد»), save; the next missing clip appears automatically.
- [ ] Record silence → «مفيش صوت واضح…», and «احفظ» stays disabled.
- [ ] During a recording press Home → recording stops; reopen → nothing half-saved.

### Speaking the time
- [ ] With every clip recorded, «راجع كل الأوقات والتسجيلات» reports all 720 times fine.
- [ ] Main button speaks the current time; compare with the status-bar clock for a few minutes
      (especially :25, :40, :43, :45, :55 and around 12).
- [ ] After the time is spoken, nothing else is heard and no error message appears (builds 1–4
      wrongly reported "مقدرتش أشغّل الصوت" here).
- [ ] Repeat button replays the same sentence.
- [ ] Tap the main button repeatedly: speech never overlaps or queues.
- [ ] Media volume at zero → the system volume panel appears instead of silent "speech".
- [ ] Turn on «قول صباحًا ومساءً»: completion shows 2 more clips needed; after recording them the
      time ends with صباحًا/مساءً.
- [ ] Record a full sentence for one time (list → «سجّل جملة كاملة للوقت ده»), test that time:
      the sentence plays; «قوله بالقطع بس» plays the word pieces instead.
- [ ] Start a phone call and press the button: nothing is spoken over the call.
- [ ] Play music in another app, then press the button: the music ducks while the time is spoken.

### Widget
- [ ] Long-press the home screen → Widgets → «الساعة الناطقة»; it is added as a 1×1 round button
      (resize works). A widget added by an older build keeps its old size: remove it and add it again.
- [ ] With the app closed (swipe it away from recents), tap the widget: the time is spoken
      without the app opening. A brief notification may flash while speaking.
- [ ] TalkBack on: the widget is announced as «قول الساعة كام».

### Persistence and offline
- [ ] Enable airplane mode; everything above still works.
- [ ] Force-stop the app (Settings → Apps → Force stop), reopen: recordings and settings are intact.
- [ ] Restart the phone: same.
- [ ] Install a newer CI build over the old one: recordings survive.

### Backup and default voice
- [ ] «احفظ نسخة من تسجيلاتي» → choose a folder → file appears there.
- [ ] Delete a recording, «رجّع نسخة احتياطية» → choose the file → it comes back.
- [ ] Choosing a random file → «الملف ده مش نسخة احتياطية صالحة», nothing changes.
- [ ] Build with `voicepack.zip` in `Resources/Raw`: on a fresh install the clock speaks without
      recording; settings show the bundled clip count; re-recording one clip overrides it and
      «رجّع الأساسي» restores it.

### Accessibility
- [ ] With TalkBack, every main-screen button is announced in Arabic and works with double-tap.
- [ ] With the largest font and display size, the setup pages remain usable (they scroll).
