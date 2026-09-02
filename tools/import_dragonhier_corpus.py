#!/usr/bin/env python3
"""Build an audited, low-priority exact-translation wave from DragonHierOverLlm.

The generated records are intended for fallback_exact.tsv. Existing LongYin-English
keys always win. Short names, one-character tokens, conflicting translations and
placeholder templates are deliberately routed away from the automatic exact import.
"""

from __future__ import annotations

import argparse
import collections
import json
import re
from pathlib import Path

import yaml


CJK_RE = re.compile(r"[\u3400-\u9fff]")
DYNAMIC_PLACEHOLDER_RE = re.compile(r"(?:\{[^{}]+\}|⟦[^⟦⟧]+⟧)")
HASH_PLACEHOLDER_RE = re.compile(r"#[^#\r\n]+#")
CJK_RUN_RE = re.compile(r"[\u3400-\u9fff]{3,}")
FOREIGN_SCRIPT_RE = re.compile(
    r"[\u0400-\u052f\u0600-\u06ff\u0750-\u077f\u08a0-\u08ff]"
)
SUSPICIOUS_UNIT_RE = re.compile(
    r"^\s*\d+(?:\.\d+)?\s+(?:two|pairs?|seconds?|day)\s*$", re.IGNORECASE
)

DATA_FILES = (
    "canonical.tsv",
    "fallback_exact.tsv",
    "tokens.tsv",
    "building_names.tsv",
    "npc_names.tsv",
    "npc_surnames.tsv",
    "npc_given_chars.tsv",
)

NAME_SOURCES = {"NameData.csv.yaml", "heroNameParts.txt.yaml"}

# These are established LongYin-English mechanic names that are not all covered by
# the older EnglishPatch compatibility aliases.
CANONICAL_REPLACEMENTS = (
    (re.compile(r"\bTang Clan\b", re.IGNORECASE), "Tang Sect"),
    (re.compile(r"\bFlying Dragon Gate\b", re.IGNORECASE), "Flying Dragon Sect"),
    (re.compile(r"\bSwordforging Manor\b", re.IGNORECASE), "Sword Forging Villa"),
    (re.compile(r"\bYama Hall\b", re.IGNORECASE), "Yama Palace"),
    (re.compile(r"\bGreat Hidden Pavilion\b", re.IGNORECASE), "Dayin Pavilion"),
    (re.compile(r"\bTyrant Blade Sect\b", re.IGNORECASE), "Badao Sect"),
    (re.compile(r"\bDivine Mechanism Sect\b", re.IGNORECASE), "Shenji Sect"),
    (re.compile(r"\bVajrayana Sect\b", re.IGNORECASE), "Vajra Esoteric Sect"),
    (re.compile(r"\bRighteous Alliance Gate\b", re.IGNORECASE), "Alliance of Justice"),
    (re.compile(r"\bSea Sand Gang\b", re.IGNORECASE), "Haisha Gang"),
    (re.compile(r"\bMedicine King Valley\b", re.IGNORECASE), "Yaowang Valley"),
    (re.compile(r"\bBeggar Sect\b", re.IGNORECASE), "Beggars Sect"),
    (re.compile(r"\bHanoi Township\b", re.IGNORECASE), "Henei Township"),
    (re.compile(r"\bQamdo Town\b", re.IGNORECASE), "Changdu Town"),
    (re.compile(r"\bNagqu Township\b", re.IGNORECASE), "Naqu Township"),
    (re.compile(r"\bQinggong\b", re.IGNORECASE), "Movement Art"),
    (re.compile(r"\bNeigong\b", re.IGNORECASE), "Internal Art"),
    (re.compile(r"\bFists and palms\b", re.IGNORECASE), "Fist"),
    (re.compile(r"\bUltimate Technique\b", re.IGNORECASE), "Body Art"),
    (re.compile(r"\bLong weapon\b", re.IGNORECASE), "Polearm"),
    (re.compile(r"\bQi Men\b", re.IGNORECASE), "Qimen"),
)


def unescape_tsv(value: str) -> str:
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


def escape_tsv(value: str) -> str:
    return (
        value.replace("\\", "\\\\")
        .replace("\r", "\\r")
        .replace("\n", "\\n")
        .replace("\t", "\\t")
    )


def load_tsv(path: Path, stop_marker: str | None = None) -> dict[str, str]:
    result: dict[str, str] = {}
    if not path.exists():
        return result
    for line in path.read_text(encoding="utf-8-sig").splitlines():
        if stop_marker and line == stop_marker:
            break
        if not line or line.startswith("#") or "\t" not in line:
            continue
        key, value = line.split("\t", 1)
        result[unescape_tsv(key)] = unescape_tsv(value)
    return result


def load_regex_groups(path: Path, stop_marker: str) -> set[tuple[str, str]]:
    groups: set[tuple[str, str]] = set()
    if not path.exists():
        return groups
    for line in path.read_text(encoding="utf-8-sig").splitlines():
        if line == stop_marker:
            break
        if not line or line.startswith("#"):
            continue
        fields = line.split("\t")
        if len(fields) == 3:
            groups.add((unescape_tsv(fields[0]), unescape_tsv(fields[2])))
    return groups


def normalize_translation(text: str) -> str:
    normalized = text.strip()
    for pattern, target in CANONICAL_REPLACEMENTS:
        normalized = pattern.sub(target, normalized)
    return normalized


def placeholder_signature(text: str) -> collections.Counter[str]:
    return collections.Counter(HASH_PLACEHOLDER_RE.findall(text))


def select_trigger(raw: str, character_frequency: collections.Counter[str]) -> str | None:
    literal = HASH_PLACEHOLDER_RE.sub("", raw)
    choices: list[tuple[int, int, str]] = []
    for match in CJK_RUN_RE.finditer(literal):
        run = match.group(0)
        for index, char in enumerate(run):
            candidate = run[index : index + 12]
            if len(candidate) < 3:
                continue
            choices.append((character_frequency[char], -len(candidate), candidate))
    return min(choices)[2] if choices else None


def build_hash_template_rule(
    raw: str, translated: str, character_frequency: collections.Counter[str]
) -> tuple[str, str, str] | None:
    raw_matches = list(HASH_PLACEHOLDER_RE.finditer(raw))
    translated_matches = list(HASH_PLACEHOLDER_RE.finditer(translated))
    if not raw_matches or placeholder_signature(raw) != placeholder_signature(translated):
        return None

    groups_by_token: dict[str, collections.deque[str]] = collections.defaultdict(collections.deque)
    pattern_parts = ["^"]
    cursor = 0
    for number, match in enumerate(raw_matches, start=1):
        pattern_parts.append(re.escape(raw[cursor : match.start()]))
        group = f"p{number}"
        groups_by_token[match.group(0)].append(group)
        pattern_parts.append(f"(?<{group}>[\\s\\S]+?)")
        cursor = match.end()
    pattern_parts.append(re.escape(raw[cursor:]))
    pattern_parts.append("$")

    replacement_parts: list[str] = []
    cursor = 0
    for match in translated_matches:
        replacement_parts.append(translated[cursor : match.start()].replace("$", "$$"))
        groups = groups_by_token[match.group(0)]
        if not groups:
            return None
        replacement_parts.append("${" + groups.popleft() + "}")
        cursor = match.end()
    replacement_parts.append(translated[cursor:].replace("$", "$$"))

    trigger = select_trigger(raw, character_frequency)
    if not trigger:
        return None
    return "".join(pattern_parts), "".join(replacement_parts), trigger


def classify_rejection(raw: str, translated: str, origins: set[str], translations: set[str]) -> str | None:
    if not raw.strip() or not translated.strip():
        return "blank"
    if raw.startswith("#"):
        # The current TSV loader treats every physical line beginning with # as a comment.
        return "comment_key"
    if len(translations) != 1:
        return "external_conflict"
    if origins & NAME_SOURCES:
        return "name_source"
    if len(CJK_RE.findall(raw)) < 3:
        return "short_source"
    if DYNAMIC_PLACEHOLDER_RE.search(raw):
        return "template"
    if CJK_RE.search(translated):
        return "translated_cjk"
    if FOREIGN_SCRIPT_RE.search(translated):
        return "foreign_script"
    if SUSPICIOUS_UNIT_RE.fullmatch(translated):
        return "suspicious_unit"
    if placeholder_signature(raw) != placeholder_signature(translated):
        return "placeholder_mismatch"
    if raw.strip() == translated.strip():
        return "identical"
    return None


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--project", type=Path, required=True)
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--apply", action="store_true")
    parser.add_argument("--report", type=Path)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    project = args.project.resolve()
    source = args.source.resolve()
    data_dir = project / "UserData" / "LongYinEnglish"
    converted_dir = source / "Files" / "Converted"

    occupied: dict[str, str] = {}
    marker = "# TEST46 authorized DragonHierOverLlm exact corpus wave"
    for filename in DATA_FILES:
        occupied.update(
            load_tsv(data_dir / filename, marker if filename == "fallback_exact.tsv" else None)
        )

    values: dict[str, collections.Counter[str]] = collections.defaultdict(collections.Counter)
    origins: dict[str, set[str]] = collections.defaultdict(set)
    occurrences: collections.Counter[str] = collections.Counter()
    canonicalized_sources: set[str] = set()

    for path in sorted(converted_dir.glob("*.yaml")):
        payload = yaml.safe_load(path.read_text(encoding="utf-8-sig")) or []
        if not isinstance(payload, list):
            continue
        for item in payload:
            if not isinstance(item, dict):
                continue
            for split in item.get("splits") or []:
                if not isinstance(split, dict):
                    continue
                raw = str(split.get("text") or "")
                translated = str(split.get("translated") or "")
                if not CJK_RE.search(raw):
                    continue
                normalized = normalize_translation(translated)
                if normalized != translated.strip():
                    canonicalized_sources.add(raw)
                values[raw][normalized] += 1
                origins[raw].add(path.name)
                occurrences[raw] += 1

    accepted: dict[str, str] = {}
    rejected = collections.Counter()
    by_source = collections.Counter()
    for raw in sorted(values):
        translations = set(values[raw])
        preferred = values[raw].most_common(1)[0][0]
        if raw in occupied:
            rejected["already_covered"] += 1
            continue
        reason = classify_rejection(raw, preferred, origins[raw], translations)
        if reason:
            rejected[reason] += 1
            continue
        accepted[raw] = preferred
        for origin in origins[raw]:
            by_source[origin] += 1

    hash_template_inputs: list[tuple[str, str]] = []
    for raw in sorted(values):
        if not HASH_PLACEHOLDER_RE.search(raw) or DYNAMIC_PLACEHOLDER_RE.search(raw):
            continue
        translations = set(values[raw])
        if len(translations) != 1 or origins[raw] & NAME_SOURCES:
            continue
        translated = next(iter(translations))
        if CJK_RE.search(translated) or FOREIGN_SCRIPT_RE.search(translated):
            continue
        if placeholder_signature(raw) != placeholder_signature(translated):
            continue
        if len(CJK_RE.findall(HASH_PLACEHOLDER_RE.sub("", raw))) < 3:
            continue
        hash_template_inputs.append((raw, translated))

    character_frequency: collections.Counter[str] = collections.Counter()
    for raw, _translated in hash_template_inputs:
        character_frequency.update(CJK_RE.findall(HASH_PLACEHOLDER_RE.sub("", raw)))

    hash_template_rules: list[tuple[str, str, str]] = []
    regex_marker = "# TEST46 authorized DragonHierOverLlm hash-placeholder templates"
    seen_rule_groups = load_regex_groups(data_dir / "regex.tsv", regex_marker)
    for raw, translated in hash_template_inputs:
        rule = build_hash_template_rule(raw, translated, character_frequency)
        if not rule:
            continue
        pattern, replacement, trigger = rule
        group = (pattern, trigger)
        if group in seen_rule_groups:
            continue
        seen_rule_groups.add(group)
        hash_template_rules.append((pattern, replacement, trigger))

    report = {
        "source_repository": "https://github.com/joshfreitas1984/DragonHierOverLlm",
        "source_commit": "b2cbaf32d0c9907f78576bc6340f44afb90af249",
        "source_yaml_files": len(list(converted_dir.glob("*.yaml"))),
        "unique_external_cjk_fragments": len(values),
        "accepted_exact_fallbacks": len(accepted),
        "hash_placeholder_regex_rules": len(hash_template_rules),
        "canonicalized_during_import": len(set(accepted) & canonicalized_sources),
        "rejected": dict(sorted(rejected.items())),
        "accepted_by_source": dict(by_source.most_common()),
        "largest_accepted_samples": [
            {"raw": raw, "translated": accepted[raw], "origins": sorted(origins[raw])}
            for raw in sorted(accepted, key=lambda value: (-len(value), value))[:20]
        ],
    }

    report_path = (args.report or project / "docs" / "TEST46_IMPORT_REPORT.json").resolve()
    report_path.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    if args.apply:
        fallback_path = data_dir / "fallback_exact.tsv"
        existing = fallback_path.read_text(encoding="utf-8-sig")
        base = existing.split(marker, 1)[0].rstrip() + "\n"
        with fallback_path.open("w", encoding="utf-8", newline="\n") as handle:
            handle.write(base)
            handle.write("\n# TEST46 authorized DragonHierOverLlm exact corpus wave\n")
            handle.write("# Source commit: b2cbaf32d0c9907f78576bc6340f44afb90af249\n")
            for raw, translated in accepted.items():
                handle.write(f"{escape_tsv(raw)}\t{escape_tsv(translated)}\n")

        regex_path = data_dir / "regex.tsv"
        existing_regex = regex_path.read_text(encoding="utf-8-sig")
        regex_base = existing_regex.split(regex_marker, 1)[0].rstrip() + "\n"
        with regex_path.open("w", encoding="utf-8", newline="\n") as handle:
            handle.write(regex_base)
            handle.write("\n" + regex_marker + "\n")
            handle.write("# Source commit: b2cbaf32d0c9907f78576bc6340f44afb90af249\n")
            for pattern, replacement, trigger in sorted(hash_template_rules, key=lambda rule: (rule[2], rule[0])):
                handle.write(
                    f"{escape_tsv(pattern)}\t{escape_tsv(replacement)}\t{escape_tsv(trigger)}\n"
                )

    print(json.dumps(report, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
