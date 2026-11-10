#!/usr/bin/env python3
"""Idempotently wire TodoSyncHotReload into upstream TodoWidgetViewModel.cs.

Upstream DeskBox ships as a fast-moving 1.5.x line. The only edits we make to
an upstream-owned file are two calls in the constructor and Dispose. Keeping
them in this script means "Sync fork" from upstream can never silently drop
the wiring: CI runs this script before every build, and a developer can run it
locally after a rebase.

Usage:
    python scripts/apply_sync_patch.py            # apply if missing
    python scripts/apply_sync_patch.py --check   # verify only, exit 1 if missing
"""

from __future__ import annotations

import argparse
import pathlib
import sys

VIEW_MODEL = pathlib.Path("src/DeskBox/ViewModels/TodoWidgetViewModel.cs")
HOT_RELOAD = pathlib.Path("src/DeskBox/ViewModels/TodoSyncHotReload.cs")

CTOR_OLD = """        _localizationService.LanguageChanged += OnLanguageChanged;
    }
"""
CTOR_NEW = """        _localizationService.LanguageChanged += OnLanguageChanged;
        AttachSync(_store);
    }
"""

DISPOSE_OLD = """        _isDisposed = true;
        _localizationService.LanguageChanged -= OnLanguageChanged;
    }
"""
DISPOSE_NEW = """        _isDisposed = true;
        _localizationService.LanguageChanged -= OnLanguageChanged;
        DetachSync();
    }
"""


def read_text(path: pathlib.Path) -> tuple[str, bool]:
    raw = path.read_bytes()
    has_bom = raw.startswith(b"\xef\xbb\xbf")
    return raw.decode("utf-8-sig"), has_bom


def write_text(path: pathlib.Path, text: str, has_bom: bool) -> None:
    data = text.encode("utf-8")
    if has_bom:
        data = b"\xef\xbb\xbf" + data
    path.write_bytes(data)


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

    text, has_bom = read_text(VIEW_MODEL)
    need_ctor = "AttachSync(_store);" not in text
    need_dispose = "DetachSync();" not in text

    if not need_ctor and not need_dispose:
        print("OK: Todo sync wiring already present.")
        return 0

    if args.check:
        print("MISSING: Todo sync wiring is not present.", file=sys.stderr)
        return 1

    if need_ctor:
        if CTOR_OLD not in text:
            print("ERROR: constructor anchor changed upstream; patch by hand.", file=sys.stderr)
            return 3
        text = text.replace(CTOR_OLD, CTOR_NEW, 1)
        print("patched: AttachSync(_store) in constructor")

    if need_dispose:
        if DISPOSE_OLD not in text:
            print("ERROR: Dispose anchor changed upstream; patch by hand.", file=sys.stderr)
            return 3
        text = text.replace(DISPOSE_OLD, DISPOSE_NEW, 1)
        print("patched: DetachSync() in Dispose")

    write_text(VIEW_MODEL, text, has_bom)
    print("OK: Todo sync wiring applied.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
