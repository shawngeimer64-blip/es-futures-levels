"""
compute_stats.py
================
Reads es_interactions.csv (the logger's output), cleans it, and computes
break / reject / hold probabilities per bucket:  (level_type x regime x touch_bucket).

Honest-stats guards built in:
  - De-duplicates repeated log rows (the logger back-processes history on restart,
    which can log the same interaction many times).
  - Reports sample size n for every bucket.
  - Flags buckets with n < MIN_SAMPLES as "insufficient" so the dashboard can
    suppress untrustworthy percentages.

REGIME-AGNOSTIC FALLBACK ("ALL")
--------------------------------
In addition to the per-regime buckets, every interaction is ALSO counted into a
regime-agnostic bucket with regime = "ALL" (level_type x touch_bucket, all
regimes combined). This gives the dashboard a fallback: when a specific
regime bucket (e.g. Session|POS|1) is missing or too thin, it can fall back to
Session|ALL|1, which is backed by the full history. The break-rate signal in
this data is driven far more by touch number than by regime, so the blended
number is meaningful when the regime-specific one isn't available yet.

Output: es_stats.csv  (one row per bucket, including the ALL rows)
"""

import csv
import os
from collections import defaultdict

# ----- config -----
# Read and write in the NT8 user folder, which is where YMLevelsLogger writes
# es_interactions.csv and where ESLevels reads es_stats.csv back from. Resolving
# these beside the script instead — as this originally did — means the stats run
# finds no input and the indicator finds no stats, silently, on both ends.
#
# The helper is duplicated from es_full_levels.py rather than imported so this
# stays stdlib-only: computing stats over a CSV should not require yfinance,
# numpy and scipy to be installed.
def _nt8_user_dir():
    env = os.environ.get("NT8_DIR")
    if env:
        return env
    return os.path.join(os.path.expanduser("~"), "Documents", "NinjaTrader 8")


def _resolve_folder():
    """Prefer the NT8 user folder, but fall back to beside the script.

    If you keep this script inside your ym_levels folder, the original
    beside-the-script behaviour already worked — so don't break it. The NT8
    location wins when it actually holds a log; otherwise a local
    es_interactions.csv is used.
    """
    nt8 = os.path.join(_nt8_user_dir(), "es_levels")
    here = os.path.dirname(os.path.abspath(__file__))
    if os.path.exists(os.path.join(nt8, "es_interactions.csv")):
        return nt8
    if os.path.exists(os.path.join(here, "es_interactions.csv")):
        return here
    return nt8          # neither exists yet; report against the expected location


FOLDER   = _resolve_folder()
IN_PATH  = os.path.join(FOLDER, "es_interactions.csv")
OUT_PATH = os.path.join(FOLDER, "es_stats.csv")

MIN_SAMPLES = 30          # below this, a bucket's % is not trustworthy
TOUCH_BUCKETS = {          # collapse touch_number into buckets
    1: "1",
    2: "2",
    3: "3plus",            # 3 and beyond lumped — later touches behave similarly
}

ALL_REGIME = "ALL"        # label for the regime-agnostic fallback buckets

def touch_bucket(n):
    try:
        n = int(n)
    except:
        return "1"
    if n <= 1: return "1"
    if n == 2: return "2"
    return "3plus"

# ----- load + de-duplicate -----
def load_rows(path):
    if not os.path.exists(path):
        print(f"ERROR: {path} not found.")
        print( "       That file is written by the YMLevelsLogger indicator, which only")
        print( "       logs in real time — it does not back-process history. So it needs")
        print( "       to have been on a live chart while price interacted with a level.")
        return []

    rows = []
    with open(path, "r", newline="", encoding="utf-8", errors="replace") as f:
        reader = csv.DictReader(f)
        for r in reader:
            rows.append(r)
    return rows

def dedupe(rows):
    """
    A real interaction is uniquely identified by:
      timestamp + level_name + level_price + outcome
    The logger can re-emit the same interaction when NinjaTrader reloads and
    back-processes history. Collapsing on this key counts each once.
    """
    seen = set()
    unique = []
    for r in rows:
        key = (
            r.get("timestamp", ""),
            r.get("level_name", ""),
            r.get("level_price", ""),
            r.get("outcome", ""),
            r.get("direction", ""),
        )
        if key in seen:
            continue
        seen.add(key)
        unique.append(r)
    return unique

# ----- aggregate -----
def compute(rows):
    # bucket -> {"BREAK": n, "REJECT": n, "HOLD": n, "TOTAL": n}
    buckets = defaultdict(lambda: {"BREAK": 0, "REJECT": 0, "HOLD": 0, "TOTAL": 0})

    for r in rows:
        ltype  = (r.get("level_type") or "").strip()
        regime = (r.get("regime") or "UNK").strip()
        tb     = touch_bucket(r.get("touch_number"))
        outcome = (r.get("outcome") or "").strip().upper()

        if outcome not in ("BREAK", "REJECT", "HOLD"):
            continue
        if not ltype:
            continue

        # (1) per-regime bucket, exactly as before
        key = (ltype, regime, tb)
        buckets[key][outcome] += 1
        buckets[key]["TOTAL"] += 1

        # (2) regime-agnostic fallback bucket — every interaction counted here too,
        #     under regime = "ALL". Guard against double-counting if any row's
        #     regime column literally says "ALL".
        if regime != ALL_REGIME:
            akey = (ltype, ALL_REGIME, tb)
            buckets[akey][outcome] += 1
            buckets[akey]["TOTAL"] += 1

    return buckets

# ----- write stats -----
def write_stats(buckets, path):
    rows_out = []
    for (ltype, regime, tb), counts in sorted(buckets.items()):
        total = counts["TOTAL"]
        if total == 0:
            continue
        brk = counts["BREAK"]
        rej = counts["REJECT"]
        hld = counts["HOLD"]

        break_pct  = round(100.0 * brk / total, 1)
        reject_pct = round(100.0 * rej / total, 1)
        hold_pct   = round(100.0 * hld / total, 1)

        trustworthy = "yes" if total >= MIN_SAMPLES else "no"

        rows_out.append({
            "level_type": ltype,
            "regime": regime,
            "touch_bucket": tb,
            "n": total,
            "break_pct": break_pct,
            "reject_pct": reject_pct,
            "hold_pct": hold_pct,
            "trustworthy": trustworthy,
        })

    with open(path, "w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=[
            "level_type", "regime", "touch_bucket", "n",
            "break_pct", "reject_pct", "hold_pct", "trustworthy"
        ])
        writer.writeheader()
        for row in rows_out:
            writer.writerow(row)

    return rows_out

# ----- pretty print summary -----
def print_summary(raw_count, unique_count, rows_out):
    print("=" * 70)
    print("  ES LEVEL INTERACTION STATS")
    print("=" * 70)
    print(f"  Raw rows in log:        {raw_count}")
    print(f"  Unique interactions:    {unique_count}")
    print(f"  Duplicates removed:     {raw_count - unique_count}")
    print(f"  Min samples to trust:   {MIN_SAMPLES}")
    print("=" * 70)

    if not rows_out:
        print("  No usable interactions found.")
        print("=" * 70)
        return

    # header
    print(f"  {'Level Type':<12} {'Regime':<6} {'Touch':<6} {'n':>5}  "
          f"{'Break%':>7} {'Rej%':>6} {'Hold%':>6}  Trust")
    print("  " + "-" * 66)

    for r in rows_out:
        flag = "  ok " if r["trustworthy"] == "yes" else "  LOW"
        print(f"  {r['level_type']:<12} {r['regime']:<6} {r['touch_bucket']:<6} "
              f"{r['n']:>5}  {r['break_pct']:>6}% {r['reject_pct']:>5}% "
              f"{r['hold_pct']:>5}% {flag}")

    print("=" * 70)

    # regime coverage callout
    regimes = defaultdict(int)
    for r in rows_out:
        regimes[r["regime"]] += r["n"]
    print("  Sample coverage by regime:")
    for reg, n in sorted(regimes.items(), key=lambda x: -x[1]):
        note = "" if n >= MIN_SAMPLES else "  <- too thin to trust"
        tag = "  (fallback, all regimes combined)" if reg == ALL_REGIME else ""
        print(f"    {reg:<5} {n:>6} samples{note}{tag}")
    print("=" * 70)


def main():
    raw = load_rows(IN_PATH)
    if not raw:
        return
    unique = dedupe(raw)
    buckets = compute(unique)
    rows_out = write_stats(buckets, OUT_PATH)
    print_summary(len(raw), len(unique), rows_out)
    print(f"\n  Stats written to: {OUT_PATH}\n")


if __name__ == "__main__":
    main()