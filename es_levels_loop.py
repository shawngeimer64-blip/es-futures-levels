"""Refresh es_levels.csv on a timer while the session is open.

Session levels (pivots, overnight, prior week/month) are fixed for the day, but
the gamma levels move with spot and IV, so the CSV is worth regenerating through
the session. NinjaTrader reloads it on mtime change, so nothing here has to talk
to NT8.

    C:/Python314/python.exe run_levels_loop.py [--interval-sec 300] [--until 16:05]

Writes a full transcript to es_levels_loop.log and one compact status line per
cycle to es_levels_loop.status — read the status file to monitor.
"""
import argparse
import os
import re
import subprocess
import sys
import time
from datetime import datetime
from zoneinfo import ZoneInfo

ET = ZoneInfo("America/New_York")
HERE = os.path.dirname(os.path.abspath(__file__))
SCRIPT = os.path.join(HERE, "es_full_levels.py")


def _esf():
    """Import the generator purely to reuse its OUTPUT_DIR, so the loop can never
    watch a different folder than the one the generator writes to."""
    import importlib.util
    spec = importlib.util.spec_from_file_location("_esf_paths", SCRIPT)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


CSV = os.path.join(_esf().OUTPUT_DIR, "es_levels.csv")
LOG = os.path.join(HERE, "es_levels_loop.log")
STATUS = os.path.join(HERE, "es_levels_loop.status")

GAMMA_NAMES = ("Gamma Flip", "Flip 0DTE", "Call Wall", "Call Wall 0DTE",
               "Put Wall", "Put Wall 0DTE")


def note(line):
    stamp = datetime.now(ET).strftime("%H:%M:%S")
    msg = "%s ET  %s" % (stamp, line)
    print(msg, flush=True)
    with open(STATUS, "a", encoding="utf-8") as f:
        f.write(msg + "\n")


def read_csv_summary():
    """Summarise what the chart will actually see."""
    if not os.path.exists(CSV):
        return None
    rows = [r.split(",") for r in
            open(CSV, encoding="utf-8").read().splitlines()[1:] if r.strip()]
    levels = [r for r in rows if len(r) >= 3 and r[2].strip() != "Meta"]
    meta = {r[0].strip(): r[1].strip() for r in rows
            if len(r) >= 3 and r[2].strip() == "Meta"}
    gamma = {r[0].strip(): r[1].strip() for r in levels if r[2].strip() == "Gamma"}
    return {"levels": len(levels), "gamma": gamma, "meta": meta}


def one_cycle(n):
    started = time.time()
    try:
        p = subprocess.run([sys.executable, SCRIPT], cwd=HERE,
                           stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                           stderr=subprocess.STDOUT, timeout=300)
        out = p.stdout.decode("utf-8", "replace")
    except subprocess.TimeoutExpired:
        note("#%d FAILED - generator exceeded 300s (network hang?)" % n)
        return
    except Exception as e:
        note("#%d FAILED - %s: %s" % (n, type(e).__name__, e))
        return

    with open(LOG, "a", encoding="utf-8") as f:
        f.write("\n%s\n===== cycle %d @ %s ET (%.0fs) =====\n%s\n"
                % ("=" * 70, n, datetime.now(ET).strftime("%H:%M:%S"),
                   time.time() - started, out))

    if p.returncode != 0:
        note("#%d generator exited %d - see the log" % (n, p.returncode))
        return
    if "SOMETHING WENT WRONG" in out:
        why = re.search(r"^\s{2}(\w+Error): (.+)$", out, re.M)
        note("#%d FAILED - %s" % (n, why.group(0).strip() if why else "see the log"))
        return

    s = read_csv_summary()
    if s is None:
        note("#%d ran but no CSV at %s" % (n, CSV))
        return

    es = s["meta"].get("ES_Price", "?")
    regime = s["meta"].get("Regime", "?")
    have = [g for g in GAMMA_NAMES if g in s["gamma"]]

    if not have:
        reason = ("no open interest from the feed"
                  if "returned NO open interest" in out else "all dropped by the sanity guard")
        note("#%d ES %s | regime %s | %d levels | GAMMA MISSING (%s)"
             % (n, es, regime, s["levels"], reason))
        return

    flip = s["gamma"].get("Gamma Flip", "-")
    cw = s["gamma"].get("Call Wall", "-")
    pw = s["gamma"].get("Put Wall", "-")
    extra = ""
    try:
        d = int(flip) - int(es)
        extra = "  (flip %+d)" % d
    except ValueError:
        pass
    note("#%d ES %s | regime %s | %d levels | flip %s  put %s  call %s | %d/6 gamma%s"
         % (n, es, regime, s["levels"], flip, pw, cw, len(have), extra))

    dropped = [g for g in GAMMA_NAMES if g not in s["gamma"]]
    if dropped:
        note("     dropped by sanity guard: %s" % ", ".join(dropped))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--interval-sec", type=int, default=300)
    ap.add_argument("--until", default="16:05",
                    help="stop after this ET wall-clock time (HH:MM)")
    a = ap.parse_args()

    hh, mm = (int(x) for x in a.until.split(":"))
    for f in (LOG, STATUS):
        open(f, "w", encoding="utf-8").close()

    note("loop start - every %ds until %s ET" % (a.interval_sec, a.until))
    n = 0
    while True:
        now = datetime.now(ET)
        if (now.hour, now.minute) >= (hh, mm):
            note("reached %s ET - stopping" % a.until)
            return
        n += 1
        one_cycle(n)
        # Re-check the stop time before sleeping so we never idle past it.
        if (datetime.now(ET).hour, datetime.now(ET).minute) >= (hh, mm):
            note("reached %s ET - stopping" % a.until)
            return
        time.sleep(a.interval_sec)


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        note("stopped by hand")
