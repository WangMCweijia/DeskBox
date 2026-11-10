#!/usr/bin/env python3
"""DeskBox Todo <-> Dida365 (dida365.com) bidirectional sync agent.

The agent owns all Dida365 API complexity. DeskBox itself only gained a tiny
"todo.sync" signal watcher; it never talks to the network. Changes are merged
into %LocalAppData%/DeskBox/data/widgets/<id>/todo.json and the signal file
makes the running DeskBox rebuild its in-memory Items without a restart.

Only the Python standard library is required.

Usage:
    python dida_sync.py --list-projects          # verify token, list project ids
    python dida_sync.py --once --dry-run         # show a plan, change nothing
    python dida_sync.py --once                   # one sync round
    python dida_sync.py                         # resident loop
"""

from __future__ import annotations

import argparse
import json
import logging
import os
import sys
import time
import uuid
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any
from urllib import error, request

HERE = Path(__file__).resolve().parent
DEFAULT_CONFIG = HERE / "config.json"
DEFAULT_STATE = HERE / "sync-state.json"
LOG = logging.getLogger("dida-sync")

EPOCH = datetime(1970, 1, 1, tzinfo=timezone.utc)


# --------------------------------------------------------------------------
# helpers
# --------------------------------------------------------------------------

def utc_now() -> datetime:
    return datetime.now(timezone.utc)


def iso_now() -> str:
    return utc_now().isoformat()


def parse_dt(value: Any) -> datetime | None:
    if not value or not isinstance(value, str):
        return None
    text = value.strip()
    # Dida365 uses "+0000" (no colon), DeskBox uses "+00:00".
    if len(text) >= 5 and text[-5] in "+-" and text[-3] != ":":
        text = text[:-2] + ":" + text[-2:]
    try:
        parsed = datetime.fromisoformat(text.replace("Z", "+00:00"))
    except ValueError:
        return None
    if parsed.tzinfo is None:
        parsed = parsed.replace(tzinfo=timezone.utc)
    return parsed.astimezone(timezone.utc)


def canon_dt(value: Any) -> str | None:
    parsed = parse_dt(value)
    return parsed.strftime("%Y-%m-%dT%H:%M:%S") if parsed else None


def to_dida_dt(value: Any) -> str | None:
    """DeskBox ISO -> Dida365 'yyyy-MM-dd'T'HH:mm:ss.SSS+0000'."""
    parsed = parse_dt(value)
    return parsed.strftime("%Y-%m-%dT%H:%M:%S.000+0000") if parsed else None


def atomic_write_json(path: Path, payload: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_name(f"{path.name}.{uuid.uuid4().hex}.tmp")
    with open(tmp, "w", encoding="utf-8", newline="\n") as handle:
        json.dump(payload, handle, ensure_ascii=False, indent=2)
    for attempt in range(5):
        try:
            os.replace(tmp, path)
            return
        except OSError:
            if attempt == 4:
                raise
            time.sleep(0.15)
    if tmp.exists():
        tmp.unlink()


def new_local_item(task: dict) -> dict:
    due = canon_dt(task.get("dueDate"))
    due_iso = f"{due}+00:00" if due else None
    now = iso_now()
    steps = []
    for index, sub in enumerate(task.get("items") or []):
        steps.append({
            "id": uuid.uuid4().hex,
            "text": sub.get("title") or "",
            "isCompleted": int(sub.get("status", 0)) == 1,
            "sortOrder": index,
        })
    completed_time = canon_dt(task.get("completedTime"))
    return {
        "id": uuid.uuid4().hex,
        "text": task.get("title") or "(untitled)",
        "isCompleted": int(task.get("status", 0)) == 2,
        "isImportant": int(task.get("priority", 0)) >= 5,
        "colorMarker": None,
        "dueDate": due_iso,
        "recurrence": None,
        "steps": steps,
        "notes": task.get("content") or None,
        "attachments": [],
        "completedAt": f"{completed_time}+00:00" if completed_time else None,
        "reminderLastNotifiedAt": None,
        "reminderDismissedForDueDate": None,
        "reminderOffsetMinutes": None,
        "snoozedUntil": None,
        "snoozeLastNotifiedAt": None,
        "recurrenceSeriesId": None,
        "generatedNextItemId": None,
        "sortOrder": 0,
        "createdAt": now,
        "updatedAt": now,
        "deviceId": "dida-sync-agent",
        "isDeleted": False,
    }


def normalize_local(item: dict) -> dict:
    """Only fields that participate in sync; DeskBox churns reminder fields."""
    return {
        "text": item.get("text") or "",
        "isCompleted": bool(item.get("isCompleted")),
        "isImportant": bool(item.get("isImportant")),
        "dueDate": canon_dt(item.get("dueDate")),
        "notes": item.get("notes") or "",
        "steps": [
            [step.get("text") or "", bool(step.get("isCompleted"))]
            for step in (item.get("steps") or [])
        ],
        "isDeleted": bool(item.get("isDeleted")),
    }


def normalize_remote(task: dict) -> dict:
    return {
        "title": task.get("title") or "",
        "status": int(task.get("status", 0)),
        "priority": int(task.get("priority", 0)),
        "dueDate": canon_dt(task.get("dueDate")),
        "content": task.get("content") or "",
        "items": [
            [sub.get("title") or "", int(sub.get("status", 0)) == 1]
            for sub in (task.get("items") or [])
        ],
    }


def local_to_remote_body(item: dict, project_id: str, task_id: str | None = None) -> dict:
    body: dict[str, Any] = {
        "projectId": project_id,
        "title": item.get("text") or "(untitled)",
        "content": item.get("notes") or "",
        "priority": 5 if item.get("isImportant") else 0,
        "timeZone": "Asia/Shanghai",
    }
    if task_id:
        body["id"] = task_id
    due = to_dida_dt(item.get("dueDate"))
    if due:
        body["dueDate"] = due
        body["isAllDay"] = False
    steps = item.get("steps") or []
    if steps:
        body["kind"] = "CHECKLIST"
        body["items"] = [
            {
                "title": step.get("text") or "",
                "status": 1 if step.get("isCompleted") else 0,
                "sortOrder": index,
            }
            for index, step in enumerate(steps)
        ]
    return body


def remote_to_local_patch(task: dict) -> dict:
    due = canon_dt(task.get("dueDate"))
    completed = canon_dt(task.get("completedTime"))
    return {
        "text": task.get("title") or "(untitled)",
        "isCompleted": int(task.get("status", 0)) == 2,
        "isImportant": int(task.get("priority", 0)) >= 5,
        "dueDate": f"{due}+00:00" if due else None,
        "notes": task.get("content") or None,
        "completedAt": f"{completed}+00:00" if completed else None,
        "steps": [
            {
                "id": uuid.uuid4().hex,
                "text": sub.get("title") or "",
                "isCompleted": int(sub.get("status", 0)) == 1,
                "sortOrder": index,
            }
            for index, sub in enumerate(task.get("items") or [])
        ],
        "updatedAt": iso_now(),
    }


# --------------------------------------------------------------------------
# Dida365 Open API client
# --------------------------------------------------------------------------

class TokenError(RuntimeError):
    pass


class DidaClient:
    def __init__(self, base_url: str, token: str):
        self.base = base_url.rstrip("/")
        self.token = token

    def _request(self, method: str, path: str, body: Any = None, retry: int = 0) -> Any:
        data = json.dumps(body, ensure_ascii=False).encode("utf-8") if body is not None else None
        req = request.Request(
            self.base + path,
            data=data,
            method=method,
            headers={
                "Authorization": f"Bearer {self.token}",
                "Content-Type": "application/json",
                "Accept": "application/json",
                "User-Agent": "DeskBox-DidaSync/1.0",
            },
        )
        try:
            with request.urlopen(req, timeout=25) as response:
                raw = response.read().decode("utf-8")
            return json.loads(raw) if raw.strip() else None
        except error.HTTPError as exc:
            text = exc.read().decode("utf-8", "replace")
            if exc.code == 401:
                raise TokenError("Dida365 token rejected (401). Generate a new API token.") from exc
            if exc.code == 500 and "exceed_query_limit" in text:
                LOG.warning("Rate limited (exceed_query_limit); cooling down 61s.")
                if retry < 1:
                    time.sleep(61)
                    return self._request(method, path, body, retry + 1)
            if exc.code in (429, 500, 502, 503, 504) and retry < 2:
                time.sleep(2 ** retry * 2)
                return self._request(method, path, body, retry + 1)
            raise RuntimeError(f"HTTP {exc.code} {method} {path}: {text[:300]}") from exc
        except error.URLError as exc:
            if retry < 2:
                time.sleep(2 ** retry * 2)
                return self._request(method, path, body, retry + 1)
            raise RuntimeError(f"Network error {method} {path}: {exc}") from exc

    def list_projects(self) -> list[dict]:
        return self._request("GET", "/project") or []

    def project_data(self, project_id: str) -> dict:
        return self._request("GET", f"/project/{project_id}/data") or {}

    def completed(self, project_id: str, start: datetime, end: datetime) -> list[dict]:
        fmt = "%Y-%m-%dT%H:%M:%S.000+0000"
        body = {
            "projectIds": [project_id],
            "startDate": start.strftime(fmt),
            "endDate": end.strftime(fmt),
        }
        return self._request("POST", "/task/completed", body) or []

    def create_task(self, body: dict) -> dict:
        return self._request("POST", "/task", body) or {}

    def update_task(self, task_id: str, body: dict) -> dict:
        return self._request("POST", f"/task/{task_id}", body) or {}

    def complete_task(self, project_id: str, task_id: str) -> None:
        self._request("POST", f"/project/{project_id}/task/{task_id}/complete")


# --------------------------------------------------------------------------
# sync agent
# --------------------------------------------------------------------------

class SyncAgent:
    def __init__(self, config: dict, state_path: Path, dry_run: bool):
        self.cfg = config
        self.client = DidaClient(config["dida_base_url"], config["dida_token"])
        self.project_id = config["dida_project_id"]
        self.todo_path = Path(config["todo_json"])
        self.signal_path = self.todo_path.with_name("todo.sync")
        self.state_path = state_path
        self.dry_run = dry_run
        self.poll = int(config.get("poll_seconds", 12))
        self.lookback = int(config.get("completed_lookback_days", 30))
        self.state = self._load_state()

    def _load_state(self) -> dict:
        if self.state_path.is_file():
            try:
                with open(self.state_path, "r", encoding="utf-8") as handle:
                    loaded = json.load(handle)
                if loaded.get("project_id") == self.project_id:
                    return loaded
                LOG.warning("State belongs to another project; starting fresh.")
            except (OSError, json.JSONDecodeError) as exc:
                LOG.warning("Ignoring unreadable state: %s", exc)
        return {"version": 1, "project_id": self.project_id, "by_local": {}}

    def _save_state(self) -> None:
        if self.dry_run:
            return
        atomic_write_json(self.state_path, self.state)

    def read_local(self) -> dict:
        if not self.todo_path.is_file():
            return {"version": 3, "items": []}
        for attempt in range(5):
            try:
                with open(self.todo_path, "r", encoding="utf-8-sig") as handle:
                    doc = json.load(handle)
                doc["items"] = doc.get("items") or []
                return doc
            except (OSError, json.JSONDecodeError):
                if attempt == 4:
                    raise
                time.sleep(0.12)

    def write_local(self, doc: dict) -> None:
        # Re-read just before committing: a DeskBox SaveAsync during this round
        # must not be clobbered. The newer side wins per item id.
        current = self.read_local()
        merged = {item["id"]: item for item in doc["items"] if item.get("id")}
        for item in current["items"]:
            key = item.get("id")
            if not key:
                continue
            known = merged.get(key)
            if known is None:
                merged[key] = item
            elif (parse_dt(item.get("updatedAt")) or EPOCH) > (parse_dt(known.get("updatedAt")) or EPOCH):
                merged[key] = item
        doc["items"] = list(merged.values())
        doc["items"].sort(key=lambda item: item.get("sortOrder", 0))
        atomic_write_json(self.todo_path, doc)
        self._write_signal()

    def _write_signal(self) -> None:
        atomic_write_json(self.signal_path, {"ts": time.time_ns(), "source": "dida-sync"})

    def fetch_remote(self) -> dict[str, dict]:
        data = self.client.project_data(self.project_id)
        tasks: dict[str, dict] = {}
        for task in data.get("tasks") or []:
            if task.get("id"):
                tasks[task["id"]] = task
        end = utc_now() + timedelta(days=1)
        start = utc_now() - timedelta(days=self.lookback)
        for task in self.client.completed(self.project_id, start, end):
            if task.get("id"):
                tasks[task["id"]] = task
        return tasks

    def sync_once(self) -> None:
        local_doc = self.read_local()
        local_items = [item for item in local_doc["items"] if item.get("id")]
        by_id = {item["id"]: item for item in local_items}
        planned = {key: dict(item) for key, item in by_id.items()}
        mapping: dict[str, dict] = self.state["by_local"]

        remote = self.fetch_remote()
        remote_owner = {entry["remote_id"]: lid for lid, entry in mapping.items() if entry.get("remote_id")}
        pushed_ids: set[str] = set()
        local_dirty = False

        # ---- 1. local -> remote -----------------------------------------
        for lid, item in planned.items():
            baseline = mapping.get(lid, {}).get("local")
            if baseline is None:
                if item.get("isDeleted"):
                    continue  # a tombstone from a previous run is not a new task
                body = local_to_remote_body(item, self.project_id)
                LOG.info("CREATE remote <- local: %s", item.get("text"))
                if self.dry_run:
                    tid = f"dry:{lid}"
                else:
                    created = self.client.create_task(body)
                    tid = created.get("id")
                    if not tid:
                        raise RuntimeError(f"Create returned no task id: {created}")
                mapping[lid] = {"remote_id": tid, "local": normalize_local(item), "remote": {}}
                remote_owner[tid] = lid
                if not self.dry_run:
                    pushed_ids.add(tid)
                continue

            if normalize_local(item) == baseline:
                continue

            tid = mapping[lid].get("remote_id")
            if not tid or tid not in remote:
                LOG.warning("Remote task %s missing; skipping push for %s", tid, item.get("text"))
                continue

            # Two-sided edit: last writer wins by timestamp.
            local_time = parse_dt(item.get("updatedAt")) or EPOCH
            remote_task = remote[tid]
            remote_time = parse_dt(remote_task.get("modifiedTime") or remote_task.get("completedTime")) or EPOCH
            remote_changed = normalize_remote(remote_task) != mapping[lid].get("remote")
            if remote_changed and remote_time > local_time:
                LOG.info("Conflict: remote newer, deferring %s", item.get("text"))
                continue

            LOG.info("UPDATE remote <- local: %s", item.get("text"))
            if not self.dry_run:
                body = local_to_remote_body(item, self.project_id, tid)
                self.client.update_task(tid, body)
                was_completed = bool(baseline.get("isCompleted"))
                now_completed = bool(item.get("isCompleted"))
                if now_completed and not was_completed:
                    self.client.complete_task(self.project_id, tid)
                elif was_completed and not now_completed:
                    # Reopen is not a documented endpoint. Best effort.
                    self.client.update_task(tid, {"id": tid, "projectId": self.project_id, "status": 0})
                    LOG.warning("Reopen used status:0; verify in Dida365 if it did not take effect.")
                pushed_ids.add(tid)

        # ---- 2. remote -> local -----------------------------------------
        if pushed_ids and not self.dry_run:
            remote = self.fetch_remote()  # refresh baselines after our own writes

        for tid, task in remote.items():
            lid = remote_owner.get(tid)
            if lid is None:
                item = new_local_item(task)
                planned[item["id"]] = item
                mapping[item["id"]] = {
                    "remote_id": tid,
                    "local": normalize_local(item),
                    "remote": normalize_remote(task),
                }
                remote_owner[tid] = item["id"]
                local_dirty = True
                LOG.info("CREATE local <- remote: %s", task.get("title"))
                continue
            if tid in pushed_ids:
                continue  # our own write echoed back; do not loop
            previous = mapping[lid].get("remote")
            if previous == normalize_remote(task):
                continue
            patch = remote_to_local_patch(task)
            existing_steps = [
                [step.get("text") or "", bool(step.get("isCompleted"))]
                for step in (planned[lid].get("steps") or [])
            ]
            same_steps = existing_steps == [[step["text"], step["isCompleted"]] for step in patch["steps"]]
            other_fields_changed = any(
                planned[lid].get(key) != value
                for key, value in patch.items()
                if key not in ("updatedAt", "steps")
            )
            if other_fields_changed or not same_steps:
                LOG.info("UPDATE local <- remote: %s", task.get("title"))
                if same_steps:
                    # Keep the current step ids so the detail view does not
                    # rebuild unchanged checklist rows.
                    patch["steps"] = planned[lid].get("steps") or []
                planned[lid].update(patch)
                local_dirty = True

        # A task can vanish because it was deleted OR because its completedTime
        # aged out of the lookback window. Dida365 has no tombstones, so this
        # is ambiguous: default to "never delete locally". Opt in explicitly.
        if self.cfg.get("remote_delete_to_tombstone", False):
            for lid, entry in list(mapping.items()):
                tid = entry.get("remote_id")
                if tid and tid not in remote and entry.get("remote", {}).get("status") == 0:
                    if lid in planned and not planned[lid].get("isDeleted"):
                        LOG.info("Remote deletion -> local tombstone: %s", planned[lid].get("text"))
                        planned[lid]["isDeleted"] = True
                        planned[lid]["updatedAt"] = iso_now()
                        local_dirty = True
                    mapping.pop(lid, None)

        # ---- 3. commit ---------------------------------------------------
        if local_dirty:
            local_doc["items"] = list(planned.values())
            if self.dry_run:
                LOG.info("DRY RUN: would write %d items to %s", len(planned), self.todo_path)
            else:
                self.write_local(local_doc)
                LOG.info("Wrote todo.json and todo.sync signal (%d items).", len(planned))

        for lid, entry in list(mapping.items()):
            item = planned.get(lid)
            if item is None:
                continue
            entry["local"] = normalize_local(item)
            tid = entry.get("remote_id")
            if tid and tid in remote:
                entry["remote"] = normalize_remote(remote[tid])
        self._save_state()

    def run(self) -> None:
        while True:
            try:
                self.sync_once()
            except TokenError as exc:
                LOG.error("%s Stopping.", exc)
                raise SystemExit(2)
            except Exception as exc:  # keep the resident loop alive
                LOG.exception("Sync round failed: %s", exc)
            time.sleep(self.poll)


def load_config(path: Path) -> dict:
    if not path.is_file():
        raise SystemExit(f"Config not found: {path}\nCopy config.example.json and fill dida_token.")
    with open(path, "r", encoding="utf-8-sig") as handle:
        cfg = json.load(handle)
    for key in ("dida_token", "dida_project_id", "todo_json"):
        if not cfg.get(key) or str(cfg[key]).startswith("<"):
            raise SystemExit(f"Config field '{key}' is not filled in: {path}")
    cfg.setdefault("dida_base_url", "https://api.dida365.com/open/v1")
    cfg.setdefault("poll_seconds", 12)
    cfg.setdefault("completed_lookback_days", 30)
    return cfg


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", type=Path, default=DEFAULT_CONFIG)
    parser.add_argument("--state", type=Path, default=DEFAULT_STATE)
    parser.add_argument("--once", action="store_true", help="run one round and exit")
    parser.add_argument("--dry-run", action="store_true", help="no file or remote writes")
    parser.add_argument("--list-projects", action="store_true", help="list Dida365 projects and exit")
    parser.add_argument("--log-level", default="INFO")
    args = parser.parse_args()

    logging.basicConfig(
        level=getattr(logging, args.log_level.upper(), logging.INFO),
        format="%(asctime)s %(levelname)-7s %(message)s",
        handlers=[logging.StreamHandler(sys.stdout), logging.FileHandler(HERE / "dida_sync.log", encoding="utf-8")],
    )

    if args.list_projects:
        with open(args.config, "r", encoding="utf-8-sig") as handle:
            cfg = json.load(handle)
        client = DidaClient(cfg.get("dida_base_url", "https://api.dida365.com/open/v1"), cfg["dida_token"])
        for project in client.list_projects():
            print(f"{project.get('id')}\t{project.get('name')}")
        return 0

    cfg = load_config(args.config)
    agent = SyncAgent(cfg, args.state, args.dry_run)
    if args.once or args.dry_run:
        agent.sync_once()
    else:
        LOG.info("Resident sync started: poll=%ss project=%s", agent.poll, agent.project_id)
        agent.run()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
