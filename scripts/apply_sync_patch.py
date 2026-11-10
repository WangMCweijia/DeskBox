#!/usr/bin/env python3
"""Idempotently wire the Todo hot-reload hook and the Dida365 settings UI.

Upstream DeskBox ships as a fast-moving 1.5.x line. We keep every upstream
file byte-for-byte untouched in the fork; CI runs this script before each build
and injects the wiring into the checked-out working tree only. That makes
"Sync fork" from upstream conflict-free and impossible to silently undo.

Handles LF and CRLF (Windows Actions checkouts convert line endings), BOM, and
small upstream formatting changes via anchored lookups.

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
SETTINGS_XAML = pathlib.Path("src/DeskBox/Views/SettingsWindow.xaml")

MARKER_CTOR = "AttachSync(_store);"
MARKER_DISPOSE = "DetachSync();"
MARKER_XAML = 'x:Name="DidaSyncEnabledSwitch"'

CTOR_RE = re.compile(
    r"(?m)^(?P<body>\s*)_localizationService\.LanguageChanged \+= OnLanguageChanged;\n"
    r"(?P<closing>\s*)\}\n"
)
DISPOSE_RE = re.compile(
    r"(?m)^(?P<body>\s*)_localizationService\.LanguageChanged -= OnLanguageChanged;\n"
    r"(?P<closing>\s*)\}\n"
)

# Inserted after the last SettingsExpander of the Todo settings section and
# before the Music section template. Every control is looked up at runtime via
# FindCreatedSectionElement, so no upstream C# file has to change.
DIDA_XAML = """                            <toolkit:SettingsExpander
                                HorizontalContentAlignment="Stretch"
                                IsEnabled="{Binding Enabled}"
                                Loaded="DidaSyncSection_Loaded"
                                Description="把待办与滴答清单双向同步；手机上的改动约 10 秒后出现在这里"
                                Header="滴答清单同步">
                                <toolkit:SettingsExpander.HeaderIcon>
                                    <FontIcon Glyph="&#xE895;" />
                                </toolkit:SettingsExpander.HeaderIcon>
                                <ToggleSwitch x:Name="DidaSyncEnabledSwitch" MinWidth="0" OffContent="" OnContent="" Toggled="DidaSyncEnabled_Toggled" />
                                <toolkit:SettingsExpander.Items>
                                    <toolkit:SettingsCard HorizontalContentAlignment="Stretch" Description="滴答清单网页版 → 设置 → 账户与安全 → API 口令">
                                        <StackPanel Spacing="8">
                                            <TextBox x:Name="DidaTokenBox" PlaceholderText="粘贴 dp_ 开头的 API 口令" />
                                            <StackPanel Orientation="Horizontal" Spacing="8">
                                                <Button x:Name="DidaConnectButton" Content="测试并载入清单" Click="DidaConnect_Click" />
                                                <Button Content="保存" Click="DidaSave_Click" />
                                            </StackPanel>
                                        </StackPanel>
                                    </toolkit:SettingsCard>
                                    <toolkit:SettingsCard HorizontalContentAlignment="Stretch" Description="DeskBox 的待办会落到这个清单">
                                        <ComboBox x:Name="DidaProjectBox" HorizontalAlignment="Stretch" SelectionChanged="DidaProject_SelectionChanged" />
                                    </toolkit:SettingsCard>
                                    <toolkit:SettingsCard HorizontalContentAlignment="Stretch">
                                        <TextBlock x:Name="DidaStatusText" TextWrapping="Wrap" Text="未配置" />
                                    </toolkit:SettingsCard>
                                </toolkit:SettingsExpander.Items>
                            </toolkit:SettingsExpander>
"""


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
        f"{'+=' if call == MARKER_CTOR else '-='} OnLanguageChanged;\n"
        f"{body}{call}\n"
        f"{match.group('closing')}}}\n"
    )


def patch_view_model(check: bool) -> int:
    if not VIEW_MODEL.is_file():
        print(f"MISSING: {VIEW_MODEL}", file=sys.stderr)
        return 2

    text, has_bom, newline = read_text(VIEW_MODEL)
    need_ctor = MARKER_CTOR not in text
    need_dispose = MARKER_DISPOSE not in text

    if not need_ctor and not need_dispose:
        print("OK: Todo sync wiring already present.")
        return 0

    if check:
        print("MISSING: Todo sync wiring is not present.", file=sys.stderr)
        return 1

    if need_ctor:
        text, count = CTOR_RE.subn(lambda m: inject(m, MARKER_CTOR), text, count=1)
        if count == 0:
            print("ERROR: constructor anchor changed upstream; patch by hand.", file=sys.stderr)
            return 3
        print("patched: AttachSync(_store) in constructor")

    if need_dispose:
        text, count = DISPOSE_RE.subn(lambda m: inject(m, MARKER_DISPOSE), text, count=1)
        if count == 0:
            print("ERROR: Dispose anchor changed upstream; patch by hand.", file=sys.stderr)
            return 3
        print("patched: DetachSync() in Dispose")

    write_text(VIEW_MODEL, text, has_bom, newline)
    print("OK: Todo sync wiring applied.")
    return 0


def patch_settings_xaml(check: bool) -> int:
    if not SETTINGS_XAML.is_file():
        print(f"MISSING: {SETTINGS_XAML}", file=sys.stderr)
        return 2

    text, has_bom, newline = read_text(SETTINGS_XAML)
    if MARKER_XAML in text:
        print("OK: Dida365 settings section already present.")
        return 0

    if check:
        print("MISSING: Dida365 settings section is not present.", file=sys.stderr)
        return 1

    marker = '<DataTemplate x:Key="MusicSettingsSectionTemplate"'
    marker_index = text.find(marker)
    if marker_index == -1:
        print("ERROR: MusicSettingsSectionTemplate anchor not found.", file=sys.stderr)
        return 3

    anchor = "</toolkit:SettingsExpander>"
    anchor_index = text.rfind(anchor, 0, marker_index)
    if anchor_index == -1:
        print("ERROR: no Todo SettingsExpander to anchor on.", file=sys.stderr)
        return 3

    insert_at = anchor_index + len(anchor)
    # Keep the newline that already followed the anchor.
    text = text[:insert_at] + "\n" + DIDA_XAML + text[insert_at:]
    write_text(SETTINGS_XAML, text, has_bom, newline)
    print("patched: Dida365 sync section in SettingsWindow.xaml")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="verify only")
    args = parser.parse_args()

    if not HOT_RELOAD.is_file():
        print(f"MISSING: {HOT_RELOAD}", file=sys.stderr)
        return 2

    for step in (patch_view_model, patch_settings_xaml):
        result = step(args.check)
        if result != 0:
            return result

    print("OK: all sync patches applied.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
