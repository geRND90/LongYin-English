# LongYin English

English translation mod for **LongYinLiZhiZhuan**, built with MelonLoader and ModsOfLong.

The project combines canonical terminology, contextual replacements, procedural NPC-name handling, UI text normalization, and safeguards for dynamic text. The current data release is **v0.1.8-test46-data**, using the verified compiled TEST44 core.

## Download

For normal installation, download the compiled package:

**[LongYinEnglish v0.1.8-test46-data](https://github.com/geRND90/LongYin-English/releases/download/v0.1.8-test46-data/LongYinEnglish_v0.1.8_TEST46_AUTHORIZED_CORPUS_DATA_WAVE.zip)**

You do not need the source files from the repository.

## Installation

1. Install the required MelonLoader/ModsOfLong setup for the game.
2. Extract the compiled ZIP into the game root directory.
3. Allow Windows to merge the included `Mods` and `UserData` folders.
4. Start the game.

The installed files include:

- `Mods/LongYinEnglish.dll`
- `Mods/ModsOfLong/modLongYinEnglish/`
- `UserData/LongYinEnglish/`

## Repository layout

- `LongYinEnglish/` — C# source project and main translation runtime.
- `UserData/LongYinEnglish/` — canonical dictionaries, aliases, tokens, regex rules, and NPC/building name data.
- `Mods/ModsOfLong/modLongYinEnglish/` — ModsOfLong data component.
- `docs/` — audit and canonical terminology notes.
- `BUILD_TEST.cmd` — Windows build and packaging script.

## Building

Requirements:

- .NET 6 SDK
- `MelonLoader.dll`
- `0Harmony.dll`

Create a local `Refs` directory at the repository root and place the two dependency DLLs there. They are intentionally not included in this repository. Then run:

```bat
BUILD_TEST.cmd
```

The installable output is created in `READY/`.

## Current version

**v0.1.8-test46-data — Authorized Corpus Data Wave**

TEST46 adds 59,486 audited exact fallbacks and 4,387 context-anchored hash-placeholder templates derived, with permission, from DragonHierOverLlm. Existing LongYin-English canonical data always takes precedence, and the verified compiled TEST44 DLL remains unchanged. See [`docs/TEST46_AUDIT.txt`](docs/TEST46_AUDIT.txt) for the complete audit and [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md) for attribution.

## Notes

- ComplexData quest patches remain excluded for stability.
- The UI Stabilizer and Building Actions mods are separate projects and are not included here.
- Translation coverage is still being tested and refined.
