#!/usr/bin/env python3
"""Idempotently wire TodoSyncHotReload into upstream TodoWidgetViewModel.cs.

Upstream DeskBox ships as a fast-moving 1.5.x line. We keep every upstream
file byte-for-byte untouched in the fork; CI runs this script before each build
and injects the two calls into the checked-out working tree only. That makes
"Sync fork" from upstream conflict-free and impossible to silently undo.

Handles LF and CRLF (Windows Actions checkouts convert line endings), BOM, and
small upstream formatting changes via anchored regular expressions.

Usage:
    python scripts/apply_sync_patch.py            # apply if missing
    python scripts/apply_sync_patch.py --check   # verify only, exit 1 if missing
"""

from __future__ import annotations

import argparse
import pathlib
import re
import sys

VIEW_MODEL = pathlib.Path("src/DeskBox/ViewModels/TodoWidgetViewModel.cs")
HOT_RELOAD = pathlib.Path("src/DeskBox/ViewModels/TodoSyncHotReload.cs")

CTOR_RE = re.compile(
    r"(?m)^(?P<body>\s*)_localizationService\.LanguageChanged \+= OnLanguageChanged;\n"
    r"(?P<closing>\s*)\}\n"
)
DISPOSE_RE = re.compile(
    r"(?m)^(?P<body>\s*)_localizationService\.LanguageChanged -= OnLanguageChanged;\n"
    r"(?P<closing>\s*)\}\n"
)


def read_text(path: pathlib.Path) -> tuple[str, bool, str]:
    raw = path.read_bytes()
    has_bom = raw.startswith(b"\xef\xbb\xbf")
    decoded = raw.decode("utf-8-sig")
    newline = "\r\n" if "\r\n" in decoded else "\n"
    return decoded.replace("\r\n", "\n"), has_bom, newline


def write_text(path: pathlib.Path, text: str, has_bom: bool, newline: str) -> None:
    if newline == "\r\n":
        text = text.replace("\n", "\r\n")
    data = text.encode("utf-8")
    if has_bom:
        data = b"\xef\xbb\xbf" + data
    path.write_bytes(data)


def inject(match: re.Match[str], call: str) -> str:
    body = match.group("body")
    return (
        f"{body}_localizationService.LanguageChanged "
        f"{'+=' if call == 'AttachSync(_store);' else '-='} OnLanguageChanged;\n"
        f"{body}{call}\n"
        f"{match.group('closing')}}}\n"
    )


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="verify only")
    args = parser.parse_args()

    if not HOT_RELOAD.is_file():
        print(f"MISSING: {HOT_RELOAD}", file=sys.stderr)
        return 2
    if not VIEW_MODEL.is_file():
        print(f"MISSING: {VIEW_MODEL}", file=sys.stderr)
        return 2

    text, has_bom, newline = read_text(VIEW_MODEL)
    need_ctor = "AttachSync(_store);" not in text
    need_dispose = "DetachSync();" not in text

    if not need_ctor and not need_dispose:
        print(f"OK: Todo sync wiring already present ({'CRLF' if newline == chr(13) + chr(10) else 'LF'}).")
        return 0

    if args.check:
        print("MISSING: Todo sync wiring is not present.", file=sys.stderr)
        return 1

    if need_ctor:
        text, count = CTOR_RE.subn(lambda m: inject(m, "AttachSync(_store);"), text, count=1)
        if count == 0:
            print("ERROR: constructor anchor changed upstream; patch by hand.", file=sys.stderr)
            return 3
        print("patched: AttachSync(_store) in constructor")

    if need_dispose:
        text, count = DISPOSE_RE.subn(lambda m: inject(m, "DetachSync();"), text, count=1)
        if count == 0:
            print("ERROR: Dispose anchor changed upstream; patch by hand.", file=sys.stderr)
            return 3
        print("patched: DetachSync() in Dispose")

    write_text(VIEW_MODEL, text, has_bom, newline)
    print("OK: Todo sync wiring applied.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
