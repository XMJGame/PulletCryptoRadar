"""Read-only SQLite/gzip/JSON audit. Does not repair, delete or rewrite account data."""
import argparse
import collections
import datetime
import gzip
import hashlib
import io
import json
from pathlib import Path
import sqlite3


def audit(directory, output):
    result = {"checked_at_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
              "read_only": True, "databases": [], "errors": []}
    for filename in ("PulletCryptoRadar.db", "PulletCryptoRadar.Market.db", "PulletCryptoRadar.Forward.db"):
        path = Path(directory).resolve() / filename
        if not path.exists():
            continue
        entry = {"name": filename, "tables": [], "quick_check": None}
        with sqlite3.connect(path.as_uri() + "?mode=ro", uri=True) as conn:
            conn.execute("PRAGMA query_only=ON")
            entry["quick_check"] = [r[0] for r in conn.execute("PRAGMA quick_check")]
            tables = [r[0] for r in conn.execute("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'")]
            for table in tables:
                quoted = '"' + table.replace('"', '""') + '"'
                if "Payload" not in {r[1] for r in conn.execute(f"PRAGMA table_info({quoted})")}:
                    continue
                stats = {"table": table, "rows": 0, "gzip_rows": 0, "max_expanded_bytes": 0, "invalid": 0}
                for rowid, payload in conn.execute(f"SELECT rowid,Payload FROM {quoted}"):
                    stats["rows"] += 1
                    try:
                        if isinstance(payload, bytes) and payload[:2] == b"\x1f\x8b":
                            stats["gzip_rows"] += 1
                            with gzip.GzipFile(fileobj=io.BytesIO(payload)) as stream:
                                data = stream.read(64 * 1024 * 1024 + 1)
                            if len(data) > 64 * 1024 * 1024:
                                raise ValueError("expanded record exceeds 64MiB audit limit")
                            stats["max_expanded_bytes"] = max(stats["max_expanded_bytes"], len(data))
                        else:
                            data = payload
                        json.loads(data)
                    except (OSError, EOFError, ValueError, TypeError) as exc:
                        stats["invalid"] += 1
                        raw = payload.encode() if isinstance(payload, str) else payload
                        result["errors"].append({"database": filename, "table": table, "rowid": rowid,
                                                 "sha256": hashlib.sha256(raw).hexdigest(), "error_type": type(exc).__name__})
                entry["tables"].append(stats)
                if stats["gzip_rows"]:
                    print(f"{filename}/{table}: {stats['rows']} rows, {stats['invalid']} invalid", flush=True)
        result["databases"].append(entry)
    result["passed"] = not result["errors"] and all(d["quick_check"] == ["ok"] for d in result["databases"])
    Path(output).write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({"passed": result["passed"], "databases": len(result["databases"]),
                      "rows": sum(t["rows"] for d in result["databases"] for t in d["tables"]),
                      "errors": len(result["errors"])}), flush=True)
    return result["passed"]


if __name__ == "__main__":
    p = argparse.ArgumentParser()
    p.add_argument("--data", required=True)
    p.add_argument("--output", required=True)
    args = p.parse_args()
    raise SystemExit(0 if audit(args.data, args.output) else 1)
