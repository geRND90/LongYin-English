# LongYin English

English translation mod for **LongYinLiZhiZhuan**, built with MelonLoader and ModsOfLong.

The project combines canonical terminology, contextual replacements, procedural NPC-name handling, UI text normalization, and safeguards for dynamic text. The current development build is **v0.1.8-test42**.

## Download

For normal installation, download the compiled ZIP from the latest GitHub Release. You do not need the source files from the repository.

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

**v0.1.8-test42 — Latest 831 Canonical Wave**

TEST42 expands coverage from the latest unresolved-text log while preserving the established canonical terminology and the stable TEST38/TEST41 architecture. See [`docs/TEST42_AUDIT.txt`](docs/TEST42_AUDIT.txt) for the detailed audit.

## Notes

- ComplexData quest patches remain excluded for stability.
- The UI Stabilizer and Building Actions mods are separate projects and are not included here.
- Translation coverage is still being tested and refined.

