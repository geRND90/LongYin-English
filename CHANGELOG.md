# Changelog

## v0.1.8-test44-data

- Corrected `(Already learned Chapter 一 Repeat)` to the canonical `(Already learned at Layer 1)` format.
- Normalized Chinese numerals 0-10 in context-safe Layer, Chapter, Rank, Round, item, auction, and Level labels.
- Corrected malformed numeric regex outputs without adding unsafe global single-character numeral mappings.
- Processed all 212 unresolved records from Latest(20260902-021331).log.
- Added 24 safe reusable translations, 8 contextual regex rules, and 137 net aliases.
- Expanded procedural NPC names, generated rumors, manuscripts, dialogue, events, construction, rewards, and mixed-output cleanup.
- Preserved the TEST42 compiled core and all established canonical terminology.
- Completed the data audit with no duplicate keys, shadowed fallbacks, duplicate regex groups, invalid TSV rows, exact self-aliases, or English numeric labels retaining Chinese numerals.

Full details are available in docs/TEST44_AUDIT.txt.

## v0.1.8-test43-data

- Processed all 628 unresolved records from Latest(20260902-005115).log.
- Added 196 safe reusable translations and 40 contextual regex rules.
- Expanded procedural NPC romanization with 62 longer names and 19 context-guarded short names.
- Corrected recurring building, commission, trading, crafting, retirement, practice, poison, event, and dialogue text.
- Preserved the TEST42 compiled core and all established canonical terminology.
- Completed the data audit with no duplicate keys, shadowed fallbacks, duplicate regex groups, invalid TSV rows, or self-referential aliases.

Full details are available in docs/TEST43_AUDIT.txt.

## v0.1.8-test42

- Expanded translation coverage using 831 newly captured unresolved records.
- Improved procedural NPC romanization and compound-surname handling.
- Added context-safe Research labels for Eloquence and Strength.
- Added more than 61 procedural NPC display names.
- Expanded narrative, dialogue, mail, meeting, event, crafting, loyalty, and retirement text handling.
- Added canonical translations for additional locations, events, enemies, labels, and poetry.
- Preserved established canonical terms including Tang Sect, Maoshan Sect, Bagua Sect, Superior Martial Arts, and Secret Martial Arts.
- Completed dictionary and regex audits with no duplicate keys, shadowed fallbacks, duplicate regex groups, or self-referential aliases.

Full details are available in [`docs/TEST42_AUDIT.txt`](docs/TEST42_AUDIT.txt).
