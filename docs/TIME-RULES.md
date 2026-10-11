# Time-expression rules

How the clock turns the phone's time into Egyptian Arabic speech. The full list of all 720
sentences is in [ALL-TIMES.md](ALL-TIMES.md) (generated from the engine itself).

## Inputs

- **The phone's local time** (Android `Calendar` in the device time zone), truncated to the minute.
  UTC is never used.
- **Settings**: whether to add «صباحًا / مساءً» (off by default) and whether complete recorded
  sentences may replace the composed words (on by default).

## The 12-hour clock

| 24-hour | Spoken hour |
|---|---|
| 00:xx | 12 (الساعة اتناشر) |
| 01:xx – 11:xx | 1 – 11 |
| 12:xx | 12 |
| 13:xx – 23:xx | 1 – 11 |

When a minute is expressed with «إلا» (see below), the **next** hour is spoken, wrapping 12 → 1:
11:45 → «الساعة اتناشر إلا ربع», 12:40 → «الساعة واحدة إلا تلت».

## Minutes

Every sentence is: **hour clip** + **minute clip** (+ **صباحًا/مساءً** when enabled).

| Minute | Relation | Spoken (for 1:mm) |
|---|---|---|
| 00 | — | الساعة واحدة |
| 05 | current hour | الساعة واحدة وخمسة |
| 10 | current hour | الساعة واحدة وعشرة |
| 15 | current hour | الساعة واحدة وربع |
| 20 | current hour | الساعة واحدة وتلت |
| 25 | current hour | الساعة واحدة ونص إلا خمسة |
| 30 | current hour | الساعة واحدة ونص |
| 35 | current hour | الساعة واحدة ونص وخمسة |
| **40** | **next hour** | الساعة اتنين إلا تلت |
| **45** | **next hour** | الساعة اتنين إلا ربع |
| **50** | **next hour** | الساعة اتنين إلا عشرة |
| **55** | **next hour** | الساعة اتنين إلا خمسة |
| any other minute | current hour | the exact number, e.g. الساعة واحدة وأربعة وعشرين دقيقة |

Exact minutes use natural Egyptian counting forms:

- 1 → «ودقيقة», 2 → «ودقيقتين»
- 3–9 → short number + «دقايق»: «وتلات دقايق», «وسبع دقايق»
- 11 and above → number + «دقيقة»: «وحداشر دقيقة», «وتلاتة وأربعين دقيقة»

### Owner decisions (Hassan, October 2026)

These replace the original draft wherever they differ:

1. **1:25 is «واحدة ونص إلا خمسة»**; other non-round minutes are spoken exactly:
   1:24 → «واحدة وأربعة وعشرين دقيقة».
2. **After minute 40, only 40, 45, 50 and 55 use «إلا»** with the next hour. Every other minute
   after 40 stays with the current hour: 1:43 → «الساعة واحدة وتلاتة وأربعين دقيقة».
   (The original draft spoke 41–44, 46–49, 51–54 and 56–59 relative to the next hour.)
3. **«صباحًا / مساءً» is optional and off by default.** When on, it is appended after the time and
   follows the real time: 00:00–11:59 is صباحًا, 12:00–23:59 is مساءً. So 11:40 is
   «الساعة اتناشر إلا تلت صباحًا» and midnight is «الساعة اتناشر صباحًا».
4. **No automatic announcements** in this version; the time is spoken only when asked
   (main button, repeat button or home-screen widget).

## Required acceptance examples

All pass in `TimeSpeechEngineTests.Acceptance_examples_from_the_brief` (in both halves of the day):

| Time | Spoken |
|---|---|
| 1:00 | الساعة واحدة |
| 1:05 | الساعة واحدة وخمسة |
| 1:15 | الساعة واحدة وربع |
| 1:20 | الساعة واحدة وتلت |
| 1:30 | الساعة واحدة ونص |
| 1:35 | الساعة واحدة ونص وخمسة |
| 1:40 | الساعة اتنين إلا تلت |
| 1:45 | الساعة اتنين إلا ربع |
| 1:50 | الساعة اتنين إلا عشرة |
| 1:55 | الساعة اتنين إلا خمسة |
| 11:40 | الساعة اتناشر إلا تلت |
| 11:50 | الساعة اتناشر إلا عشرة |
| 11:55 | الساعة اتناشر إلا خمسة |
| 12:00 | الساعة اتناشر |
| 12:40 | الساعة واحدة إلا تلت |
| 12:50 | الساعة واحدة إلا عشرة |

## Why the clips are cut this way

Each clip is a natural spoken unit, so most announcements join only two recordings:

- `hour.NN` already includes «الساعة» («الساعة واحدة»).
- `minute.NN` already includes its connector («وربع», «إلا تلت», «وتلاتة وأربعين دقيقة»).

Joining at word boundaries that people pause at anyway keeps the result natural, and avoids
gluing a bare «و» onto the next word. The full list is in [RECORDING-CATALOG.md](RECORDING-CATALOG.md).

## Resolution order (what actually plays)

1. If a **complete recorded sentence** exists for this exact 12-hour time (`phrase.HH.MM`) and
   overrides are enabled, play it (followed by صباحًا/مساءً when that is on).
2. Otherwise play the **segments** from the engine: hour clip, minute clip (, day-period clip).
3. If anything needed is missing or damaged, **say nothing misleading**: report which recordings
   are missing, beep and vibrate (or play the owner's recorded error message), and never fall back
   to a synthetic or network voice.

A complete sentence that turns out to be damaged falls back to the segments automatically.

## Changing a word without touching code

All wording lives in `src/VoiceClock.Core/Rules/speech-rules-ar-EG.json`:

```json
"43": { "relation": "past", "text": "وتلاتة وأربعين دقيقة" }
```

- `relation` is `past` (current hour, «و…») or `to` (next hour, «إلا …»).
- `text` is what the setup screen shows the owner to record.

The file is validated at start-up (all 12 hours and 59 minutes present, valid relations). For
example, switching 1:43 back to the «إلا» style is purely a data change:

```json
"43": { "relation": "to", "text": "إلا سبعتاشر دقيقة" }
```

`SpeechRulesTests.A_word_can_be_corrected_in_the_data_without_changing_the_algorithm` proves this.
After editing, re-record the affected clip and run `dotnet run --project tools/GenerateDocs` to
refresh the generated tables.
