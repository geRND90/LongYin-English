#!/usr/bin/env python3
"""Structural and canonical audit for the TEST46 data-only corpus wave."""

from __future__ import annotations

import collections
import json
import re
from pathlib import Path


PROJECT = Path(__file__).resolve().parents[1]
DATA = PROJECT / "UserData" / "LongYinEnglish"
FALLBACK = DATA / "fallback_exact.tsv"
MARKER = "# TEST46 authorized DragonHierOverLlm exact corpus wave"
REGEX_MARKER = "# TEST46 authorized DragonHierOverLlm hash-placeholder templates"
CJK_RE = re.compile(r"[\u3400-\u9fff]")
FOREIGN_RE = re.compile(r"[\u0400-\u052f\u0600-\u06ff\u0750-\u077f\u08a0-\u08ff\ufffd]")
HASH_PLACEHOLDER_RE = re.compile(r"#[^#\r\n]+#")
RICH_TAG_RE = re.compile(r"<(/?)(b|color|size)(?:=[^>]*)?>", re.IGNORECASE)

BANNED_EXTERNAL_TERMS = (
    "Tang Clan",
    "Flying Dragon Gate",
    "Swordforging Manor",
    "Yama Hall",
    "Great Hidden Pavilion",
    "Tyrant Blade Sect",
    "Divine Mechanism Sect",
    "Vajrayana Sect",
    "Righteous Alliance Gate",
    "Sea Sand Gang",
    "Medicine King Valley",
    "Qamdo Town",
    "Nagqu Township",
    "Qinggong",
    "Neigong",
    "Fists and palms",
    "Ultimate Technique",
    "Long weapon",
    "Qi Men",
)

SUSPICIOUS_PATTERNS = {
    "numeric_two_pairs_seconds": re.compile(r"\b\d+(?:\.\d+)?\s+(?:two|pairs?|seconds?)\b", re.I),
    "numeric_singular_day": re.compile(r"^\s*(?:0|[2-9]|\d{2,})\s+day\s*$", re.I),
    "known_bad_manual_choice": re.compile(r"\bChoose a potion\b", re.I),
    "known_bad_thief_title": re.compile(r"\bThief and Four Quirks\b", re.I),
}


def unescape(value: str) -> str:
    output: list[str] = []
    i = 0
    while i < len(value):
        if value[i] == "\\" and i + 1 < len(value):
            i += 1
            char = value[i]
            output.append({"n": "\n", "r": "\r", "t": "\t", "\\": "\\"}.get(char, "\\" + char))
        else:
            output.append(value[i])
        i += 1
    return "".join(output)


def read_records(path: Path, expected_columns: int = 2) -> tuple[list[tuple[str, str]], list[int]]:
    records: list[tuple[str, str]] = []
    invalid: list[int] = []
    for number, line in enumerate(path.read_text(encoding="utf-8-sig").splitlines(), start=1):
        if not line or line.startswith("#"):
            continue
        if line.count("\t") != expected_columns - 1:
            invalid.append(number)
            continue
        fields = line.split("\t")
        key, value = fields[0], fields[1]
        records.append((unescape(key), unescape(value)))
    return records, invalid


def duplicate_keys(records: list[tuple[str, str]]) -> list[str]:
    counts = collections.Counter(key for key, _value in records)
    return sorted(key for key, count in counts.items() if count > 1)


def duplicate_regex_pattern_triggers(path: Path) -> list[str]:
    groups: list[str] = []
    for line in path.read_text(encoding="utf-8-sig").splitlines():
        if not line or line.startswith("#"):
            continue
        fields = line.split("\t")
        if len(fields) == 3:
            groups.append(unescape(fields[0]) + "\0" + unescape(fields[2]))
    counts = collections.Counter(groups)
    return sorted(group.replace("\0", " | ") for group, count in counts.items() if count > 1)


def imported_regex_records(path: Path) -> list[tuple[str, str, str]]:
    text = path.read_text(encoding="utf-8-sig")
    if REGEX_MARKER not in text:
        raise RuntimeError("TEST46 regex marker not found")
    section = text.split(REGEX_MARKER, 1)[1]
    records: list[tuple[str, str, str]] = []
    for line in section.splitlines():
        if not line or line.startswith("#"):
            continue
        fields = line.split("\t")
        if len(fields) == 3:
            records.append(tuple(unescape(field) for field in fields))
    return records


def imported_records() -> list[tuple[str, str]]:
    text = FALLBACK.read_text(encoding="utf-8-sig")
    if MARKER not in text:
        raise RuntimeError("TEST46 marker not found")
    section = text.split(MARKER, 1)[1]
    records: list[tuple[str, str]] = []
    for line in section.splitlines():
        if not line or line.startswith("#") or "\t" not in line:
            continue
        key, value = line.split("\t", 1)
        records.append((unescape(key), unescape(value)))
    return records


def main() -> int:
    filenames = (
        "canonical.tsv",
        "fallback_exact.tsv",
        "tokens.tsv",
        "aliases.tsv",
        "regex.tsv",
        "building_names.tsv",
        "npc_names.tsv",
        "npc_surnames.tsv",
        "npc_given_chars.tsv",
    )
    loaded: dict[str, list[tuple[str, str]]] = {}
    invalid_columns: dict[str, list[int]] = {}
    duplicates: dict[str, list[str]] = {}
    for filename in filenames:
        records, invalid = read_records(DATA / filename, 3 if filename == "regex.tsv" else 2)
        loaded[filename] = records
        invalid_columns[filename] = invalid
        duplicates[filename] = [] if filename == "regex.tsv" else duplicate_keys(records)

    duplicate_regex = duplicate_regex_pattern_triggers(DATA / "regex.tsv")

    imported = imported_records()
    imported_regex = imported_regex_records(DATA / "regex.tsv")
    imported_keys = {key for key, _value in imported}
    canonical_keys = {key for key, _value in loaded["canonical.tsv"]}
    token_keys = {key for key, _value in loaded["tokens.tsv"]}
    fallback_keys = {key for key, _value in loaded["fallback_exact.tsv"]}

    banned_counts = {
        term: sum(
            len(re.findall(r"\b" + re.escape(term) + r"\b", value, flags=re.IGNORECASE))
            for _key, value in imported
        )
        for term in BANNED_EXTERNAL_TERMS
    }
    suspicious: dict[str, list[dict[str, str]]] = {}
    for name, pattern in SUSPICIOUS_PATTERNS.items():
        matches = [
            {"raw": key, "translated": value}
            for key, value in imported
            if pattern.search(value)
        ]
        suspicious[name] = matches[:30]

    aliases = loaded["aliases.tsv"]
    alias_self = sorted(key for key, value in aliases if key == value)
    imported_placeholder_mismatches = sum(
        collections.Counter(HASH_PLACEHOLDER_RE.findall(key))
        != collections.Counter(HASH_PLACEHOLDER_RE.findall(value))
        for key, value in imported
    )
    imported_rich_tag_mismatches = sum(
        collections.Counter((slash.lower(), name.lower()) for slash, name in RICH_TAG_RE.findall(key))
        != collections.Counter((slash.lower(), name.lower()) for slash, name in RICH_TAG_RE.findall(value))
        for key, value in imported
    )

    regex_compile_failures: list[dict[str, str]] = []
    regex_group_failures: list[dict[str, str]] = []
    regex_synthetic_match_failures: list[dict[str, str]] = []
    trigger_buckets: collections.Counter[str] = collections.Counter()
    for pattern, replacement, trigger in imported_regex:
        trigger_buckets[trigger[0] if trigger else ""] += 1
        python_pattern = re.sub(r"\(\?<([A-Za-z][A-Za-z0-9_]*)>", r"(?P<\1>", pattern)
        try:
            compiled = re.compile(python_pattern)
        except re.error as error:
            regex_compile_failures.append({"pattern": pattern, "error": str(error)})
            continue
        replacement_groups = set(re.findall(r"\$\{([A-Za-z][A-Za-z0-9_]*)\}", replacement))
        if not replacement_groups.issubset(compiled.groupindex):
            regex_group_failures.append(
                {
                    "pattern": pattern,
                    "replacement": replacement,
                    "missing": sorted(replacement_groups - set(compiled.groupindex)),
                }
            )
        synthetic_pattern = re.sub(
            r"\(\?<([A-Za-z][A-Za-z0-9_]*)>\[\\s\\S\]\+\?\)",
            "TestValue",
            pattern[1:-1],
        )
        synthetic_chars: list[str] = []
        index = 0
        while index < len(synthetic_pattern):
            if synthetic_pattern[index] == "\\" and index + 1 < len(synthetic_pattern):
                index += 1
            synthetic_chars.append(synthetic_pattern[index])
            index += 1
        synthetic = "".join(synthetic_chars)
        if not compiled.fullmatch(synthetic):
            regex_synthetic_match_failures.append({"pattern": pattern, "synthetic": synthetic})

    report = {
        "release": "v0.1.8-test46-data",
        "scope": "data-only; compiled TEST44 core unchanged",
        "record_counts": {filename: len(records) for filename, records in loaded.items()},
        "test46_imported_exact_fallbacks": len(imported),
        "test46_imported_unique_keys": len(imported_keys),
        "test46_imported_hash_placeholder_regex": len(imported_regex),
        "test46_regex_compile_failures": regex_compile_failures[:30],
        "test46_regex_replacement_group_failures": regex_group_failures[:30],
        "test46_regex_synthetic_match_failures": regex_synthetic_match_failures[:30],
        "test46_regex_empty_or_short_triggers": sum(len(trigger) < 3 for _pattern, _replacement, trigger in imported_regex),
        "test46_regex_trigger_bucket_count": len(trigger_buckets),
        "test46_regex_largest_trigger_bucket": max(trigger_buckets.values(), default=0),
        "test46_file_size_bytes": FALLBACK.stat().st_size,
        "invalid_tsv_columns": {name: lines for name, lines in invalid_columns.items() if lines},
        "duplicate_keys": {name: keys[:30] for name, keys in duplicates.items() if keys},
        "duplicate_regex_pattern_triggers": duplicate_regex[:30],
        "fallback_shadowed_by_canonical": len(fallback_keys & canonical_keys),
        "imported_shadowed_by_canonical": len(imported_keys & canonical_keys),
        "imported_shadowed_by_tokens": len(imported_keys & token_keys),
        "imported_keys_below_three_cjk": sum(len(CJK_RE.findall(key)) < 3 for key, _value in imported),
        "imported_outputs_with_cjk": sum(bool(CJK_RE.search(value)) for _key, value in imported),
        "imported_outputs_with_foreign_script_or_replacement": sum(
            bool(FOREIGN_RE.search(value)) for _key, value in imported
        ),
        "empty_imported_keys_or_values": sum(not key or not value for key, value in imported),
        "imported_hash_placeholder_mismatches": imported_placeholder_mismatches,
        "imported_rich_text_tag_mismatches": imported_rich_tag_mismatches,
        "banned_external_term_counts": banned_counts,
        "suspicious_translation_samples": suspicious,
        "self_referential_aliases": alias_self,
        "provenance_notice_present": (PROJECT / "THIRD_PARTY_NOTICES.md").exists(),
        "source_commit_recorded": "b2cbaf32d0c9907f78576bc6340f44afb90af249"
        in (PROJECT / "THIRD_PARTY_NOTICES.md").read_text(encoding="utf-8"),
    }

    failures = []
    zero_fields = (
        "fallback_shadowed_by_canonical",
        "imported_shadowed_by_canonical",
        "imported_shadowed_by_tokens",
        "imported_keys_below_three_cjk",
        "imported_outputs_with_cjk",
        "imported_outputs_with_foreign_script_or_replacement",
        "empty_imported_keys_or_values",
        "imported_hash_placeholder_mismatches",
        "imported_rich_text_tag_mismatches",
    )
    for field in zero_fields:
        if report[field] != 0:
            failures.append(field)
    if report["invalid_tsv_columns"]:
        failures.append("invalid_tsv_columns")
    if report["duplicate_keys"]:
        failures.append("duplicate_keys")
    if report["duplicate_regex_pattern_triggers"]:
        failures.append("duplicate_regex_pattern_triggers")
    if report["test46_regex_compile_failures"]:
        failures.append("test46_regex_compile_failures")
    if report["test46_regex_replacement_group_failures"]:
        failures.append("test46_regex_replacement_group_failures")
    if report["test46_regex_synthetic_match_failures"]:
        failures.append("test46_regex_synthetic_match_failures")
    if report["test46_regex_empty_or_short_triggers"]:
        failures.append("test46_regex_empty_or_short_triggers")
    if report["test46_regex_largest_trigger_bucket"] > 50:
        failures.append("test46_regex_largest_trigger_bucket")
    if any(banned_counts.values()):
        failures.append("banned_external_term_counts")
    if alias_self:
        failures.append("self_referential_aliases")
    if any(suspicious.values()):
        failures.append("suspicious_translation_samples")
    if not report["provenance_notice_present"] or not report["source_commit_recorded"]:
        failures.append("provenance")

    report["failures"] = failures
    report["status"] = "PASS" if not failures else "FAIL"
    path = PROJECT / "docs" / "TEST46_AUDIT.json"
    path.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, ensure_ascii=False, indent=2))
    return 0 if not failures else 1


if __name__ == "__main__":
    raise SystemExit(main())
