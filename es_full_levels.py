"""
ES FULL LEVELS
==============
One-shot pre-market levels tool combining:
- Session levels (pivots, overnight, prior week/month)
- Gamma levels (flip, call/put walls) from SPY options
- Confluence detector (where levels stack up)
- CSV export for NinjaTrader

FIXES IN THIS VERSION
---------------------
1. Spot is anchored to the reliable ES FUTURES price, not the SPY history
   close. On bad mornings dia.history() returned a garbage close (~471 vs a
   real ~527), which made the flip (47080) look "sane" because it was being
   compared against an equally-garbage spot. Now the futures price is the
   reference for BOTH the sanity guard and regime, so junk flips get dropped.
2. The SPY option chain is filtered to strikes within STRIKE_BAND of spot
   before computing GEX. Deep/stale strikes far from price were dragging the
   flip down toward 470; this removes them.
3. Regime PIVOT FALLBACK: when no valid gamma flip survives, regime is
   resolved from price vs the daily Pivot (above = POSITIVE, below = NEGATIVE)
   instead of writing UNKNOWN. This keeps the NinjaTrader stat buckets
   resolving (POS/NEG) on junk-options mornings so break/reject percentages
   still show. The regime source is noted in the console and is not gamma on
   those days.
4. find_flip() never returns None on non-empty data (closest-to-zero fallback),
   0DTE is detected by real days-to-expiry, and Regime/spot/timestamp meta rows
   are written to the CSV for the dashboard + logger.
"""

import argparse
import requests
import csv
import json
import math
import os
import yfinance as yf
import numpy as np
from scipy.stats import norm
from datetime import datetime, date, time, timedelta
from zoneinfo import ZoneInfo

# ==========================================================
#   CONFIG
# ==========================================================
ET = ZoneInfo("America/New_York")
HEADERS = {"User-Agent": "Mozilla/5.0"}
ES_URL = "https://query1.finance.yahoo.com/v8/finance/chart/ES=F"
SPY_TO_ES = 14.5   # SPY ~530 -> ES ~7670; refined from live futures each run
RISK_FREE = 0.043
CONTRACT_SIZE = 100
CONFLUENCE_TOL = 7           # ES-scale (~0.09% of spot, matching YM's 50pt@53000)
MAX_DTE = 60                 # ignore expiries further out than this
GAMMA_SANITY_FRAC = 0.03     # gamma level farther than this fraction of the
                             # FUTURES spot is treated as bad data and dropped
STRIKE_BAND = 0.05           # only use option strikes within +/-5% of spot
SPOT_DISAGREE_FRAC = 0.10    # if DIA history close differs from the futures
                             # reference by more than this, trust the futures

# 252 trading days x 6.5h RTH = 1638 option-trading hours per year. Used to price
# 0DTE by the session time actually left instead of pretending it has a full day.
TRADING_HOURS_PER_YEAR = 1638.0

# A wall is an argmax over strikes. If the runner-up carries at least this share
# of the leader's gamma, the two swap places on noise and the printed level jumps
# between them, so the wall is reported as contested.
WALL_CONTEST_FRAC = 0.20

# Price every strike in an expiry off ONE implied vol instead of each contract's
# own. yfinance's per-contract IV is a derived, delayed field that jitters strike
# to strike between polls, and that jitter was the only thing that could move the
# wall intraday: gamma = OI x BS_gamma(spot, K, T, IV), OI is fixed for the day,
# and 17 live cycles showed the same spot producing different walls, so spot was
# ruled out. One smooth IV per expiry leaves the ranking driven by OI and spot.
# Cost: the volatility smile is discarded, which slightly misprices far-OTM
# gamma. That is a fair trade when what matters is the RELATIVE ranking near the
# money. Set False to go back to per-contract IV.
USE_ATM_IV = True
ATM_IV_STRIKES = 5      # median IV over this many strikes nearest spot, per side

# Yahoo publishes 0.00001 as a PLACEHOLDER implied vol on contracts it has not
# priced — which pre-market is roughly half the near-ATM chain. A median is
# robust to a minority of outliers, not to a majority of placeholders: measured
# 2026-08-21 at 09:09 ET, 52 of 108 near-spot quotes were 0.00001 and the median
# came out at 0.00013, i.e. a 0.013% vol. Gamma is pdf(d1)/(S*sigma*sqrt(T)), so
# a sigma that small makes d1 explode, pdf(d1) underflow, and gamma collapse to
# zero on 51 of 62 strikes — producing confident-looking but meaningless walls.
# Real index IV does not go below a few percent, so anything under this is a
# placeholder and the contract is treated as unpriced.
IV_MIN_PLAUSIBLE = 0.005

# How many strikes to publish per side. Rank 1 is "Call Wall"/"Put Wall"; deeper
# ranks are suffixed. A single argmax hides the fact that the top few strikes are
# often near-tied, which is what made the printed wall look like it teleported.
WALL_LADDER_DEPTH = 3

# Where the NinjaTrader side looks. YMLevels, YMLevelsLogger and any strategy
# reading the same file resolve it from NinjaTrader.Core.Globals.UserDataDir, which is
# NT8's own "Documents\NinjaTrader 8". Keeping the levels inside the NT8 user
# folder means neither side hard-codes a profile path, so the whole thing moves to
# another machine untouched. NT8_DIR overrides, matching nt8bridge.
def _nt8_user_dir():
    env = os.environ.get("NT8_DIR")
    if env:
        return env
    return os.path.join(os.path.expanduser("~"), "Documents", "NinjaTrader 8")

OUTPUT_DIR = os.path.join(_nt8_user_dir(), "es_levels")

# ==========================================================
#   PART 1 — SESSION LEVELS FROM YM FUTURES
# ==========================================================
def fetch_es_daily():
    r = requests.get(ES_URL, headers=HEADERS,
                     params={"range": "3mo", "interval": "1d"})
    d = r.json()["chart"]["result"][0]
    ts, q = d["timestamp"], d["indicators"]["quote"][0]
    bars = []
    for i in range(len(ts)):
        if None in (q["open"][i], q["high"][i], q["low"][i], q["close"][i]):
            continue
        bars.append({
            "dt": datetime.fromtimestamp(ts[i], tz=ET),
            "open": q["open"][i], "high": q["high"][i],
            "low": q["low"][i], "close": q["close"][i],
        })
    return bars

def fetch_es_intraday():
    r = requests.get(ES_URL, headers=HEADERS,
                     params={"range": "5d", "interval": "5m"})
    d = r.json()["chart"]["result"][0]
    ts, q = d["timestamp"], d["indicators"]["quote"][0]
    price = d["meta"]["regularMarketPrice"]
    bars = []
    for i in range(len(ts)):
        if q["high"][i] is None or q["low"][i] is None:
            continue
        bars.append({
            "dt": datetime.fromtimestamp(ts[i], tz=ET),
            "high": q["high"][i], "low": q["low"][i],
        })
    return bars, price

def compute_pivots(bar):
    h, l, c = bar["high"], bar["low"], bar["close"]
    p = (h + l + c) / 3
    return {
        "R3": round(h + 2 * (p - l)),
        "R2": round(p + (h - l)),
        "R1": round(2 * p - l),
        "Pivot": round(p),
        "Y-Mid": round((h + l) / 2),
        "S1": round(2 * p - h),
        "S2": round(p - (h - l)),
        "S3": round(l - 2 * (h - p)),
    }

def compute_overnight(intra_bars, today):
    start = datetime.combine(today - timedelta(days=1), time(18, 0), tzinfo=ET)
    end = datetime.combine(today, time(9, 30), tzinfo=ET)
    on = [b for b in intra_bars if start <= b["dt"] < end]
    if not on:
        return None
    h = max(b["high"] for b in on)
    l = min(b["low"] for b in on)
    return {"ONH": round(h), "ON-Mid": round((h + l) / 2), "ONL": round(l)}

def compute_prior_week(daily_bars, today):
    this_mon = today - timedelta(days=today.weekday())
    last_mon = this_mon - timedelta(days=7)
    last_fri = last_mon + timedelta(days=4)
    pw = [b for b in daily_bars if last_mon <= b["dt"].date() <= last_fri]
    if not pw:
        return None, last_mon, last_fri
    h = max(b["high"] for b in pw)
    l = min(b["low"] for b in pw)
    return {
        "PWH": round(h), "PW-Mid": round((h + l) / 2),
        "PWC": round(pw[-1]["close"]), "PWL": round(l),
    }, last_mon, last_fri

def compute_prior_month(daily_bars, today):
    first_this = today.replace(day=1)
    last_prior = first_this - timedelta(days=1)
    first_prior = last_prior.replace(day=1)
    pm = [b for b in daily_bars if first_prior <= b["dt"].date() <= last_prior]
    if not pm:
        return None, first_prior, last_prior
    h = max(b["high"] for b in pm)
    l = min(b["low"] for b in pm)
    return {
        "PMH": round(h), "PM-Mid": round((h + l) / 2),
        "PMC": round(pm[-1]["close"]), "PML": round(l),
    }, first_prior, last_prior

# ==========================================================
#   PART 2 — GAMMA LEVELS FROM DIA OPTIONS
# ==========================================================
def bs_gamma(S, K, T, r, sigma):
    if T <= 0 or sigma <= 0:
        return 0.0
    d1 = (np.log(S / K) + (r + 0.5 * sigma ** 2) * T) / (sigma * np.sqrt(T))
    return norm.pdf(d1) / (S * sigma * np.sqrt(T))

def _num(v, default=0.0):
    """Coerce a yfinance cell to a real number.

    yfinance routinely returns NaN for openInterest / impliedVolatility on thin
    strikes. The old code did `row["openInterest"] or 0`, but NaN is TRUTHY, so
    NaN sailed through the `== 0` guard, turned that contract's GEX into NaN, and
    poisoned the whole cumulative sum — every comparison against NaN is False, so
    find_flip() silently fell through to its fallback and returned an arbitrary
    strike. Anything not finite is treated as missing.
    """
    try:
        f = float(v)
    except (TypeError, ValueError):
        return default
    if math.isnan(f) or math.isinf(f):
        return default
    return f

def _t_years(dte, now_et):
    """Time to expiry in years.

    0DTE is NOT a full day. Pricing it as one understates its gamma and, because
    gex_all blends every expiry together, mis-weights the front expiry against
    the back months. For 0DTE use the fraction of today's RTH session that is
    actually left (floored so gamma stays finite after the close).
    """
    if dte > 0:
        return dte / 365.0
    close = now_et.replace(hour=16, minute=0, second=0, microsecond=0)
    secs_left = (close - now_et).total_seconds()
    if secs_left <= 0:
        return 0.5 / TRADING_HOURS_PER_YEAR      # after the close: a token 30 min
    return max(secs_left / 3600.0, 0.1) / TRADING_HOURS_PER_YEAR

def _atm_iv(chain, spot, lo, hi):
    """One representative implied vol for an expiry.

    Median of the implied vols on the ATM_IV_STRIKES strikes nearest spot, taken
    across calls and puts. Median rather than mean so a single junk quote cannot
    drag the reference — which is the whole point, since a junk quote on one
    strike is exactly what was moving the wall.

    Returns None when the expiry has no usable vol, in which case the caller
    falls back to that contract's own IV.
    """
    rows = []
    for frame in (chain.calls, chain.puts):
        for _, r in frame.iterrows():
            K = _num(r["strike"], -1.0)
            if K < lo or K > hi:
                continue
            iv = _num(r["impliedVolatility"])
            # Placeholder vols must be excluded BEFORE the median, not trimmed
            # after it — pre-market they are the majority, so they would carry
            # the median rather than sit in its tail.
            if iv >= IV_MIN_PLAUSIBLE:
                rows.append((abs(K - spot), iv))
    if not rows:
        return None
    rows.sort(key=lambda t: t[0])
    near = [iv for _, iv in rows[:max(1, ATM_IV_STRIKES * 2)]]
    near.sort()
    n = len(near)
    return near[n // 2] if n % 2 else 0.5 * (near[n // 2 - 1] + near[n // 2])


def _bucket():
    # Puts are accumulated as a POSITIVE magnitude and subtracted into `net`, so
    # a put wall can be found on put gamma alone rather than on a net figure that
    # heavy call OI at the same strike can cancel out.
    return {"call": 0.0, "put": 0.0, "net": 0.0}

def compute_gex(ref_spot_spy=None):
    """
    ref_spot_spy : reliable SPY-equivalent spot derived from the ES futures
                   price (es_price / SPY_TO_ES). Used to (a) override a stale
                   dia.history() close and (b) filter option strikes near spot.

    Returns spot, gex_all, gex_0dte, has_true_0dte, spot_was_corrected, diag.

    `diag` counts what the option feed actually delivered so an empty gamma
    section can name its own cause instead of just printing nothing.
    """
    diag = {"strikes_in_band": 0, "contracts_with_oi": 0,
            "contracts_with_iv": 0, "total_oi": 0.0, "expiries_used": 0,
            "contracts_placeholder_iv": 0, "atm_iv": {}}
    dia = yf.Ticker("SPY")
    spot = float(dia.history(period="1d")["Close"].iloc[-1])

    # (1) Correct a stale/garbage DIA close against the futures reference.
    spot_was_corrected = False
    if ref_spot_spy is not None and ref_spot_spy > 0:
        if abs(spot - ref_spot_spy) > SPOT_DISAGREE_FRAC * ref_spot_spy:
            spot = ref_spot_spy
            spot_was_corrected = True

    # (2) Strike band around the (now trustworthy) spot.
    lo = spot * (1 - STRIKE_BAND)
    hi = spot * (1 + STRIKE_BAND)

    all_exp = dia.options
    gex_all = {}
    gex_0dte = {}
    now_et = datetime.now(ET)
    today_d = now_et.date()

    parsed = []
    for exp in all_exp:
        try:
            exp_d = datetime.strptime(exp, "%Y-%m-%d").date()
        except ValueError:
            continue
        dte = (exp_d - today_d).days
        if 0 <= dte <= MAX_DTE:
            parsed.append((exp, dte))
    if not parsed:
        return spot, gex_all, gex_0dte, False, spot_was_corrected, diag

    parsed.sort(key=lambda x: x[1])
    front_exp = parsed[0][0]
    has_true_0dte = (parsed[0][1] == 0)

    for exp, dte in parsed:
        T = _t_years(dte, now_et)
        try:
            chain = dia.option_chain(exp)
        except Exception:
            continue

        diag["expiries_used"] += 1
        is_front = (exp == front_exp)
        ref_iv = _atm_iv(chain, spot, lo, hi) if USE_ATM_IV else None
        if ref_iv:
            diag["atm_iv"][exp] = ref_iv

        for side, frame in (("call", chain.calls), ("put", chain.puts)):
            for _, row in frame.iterrows():
                K = _num(row["strike"], -1.0)
                if K < lo or K > hi:              # strike-band filter
                    continue
                diag["strikes_in_band"] += 1
                oi = _num(row["openInterest"])
                iv = _num(row["impliedVolatility"])
                if oi > 0:
                    diag["contracts_with_oi"] += 1
                    diag["total_oi"] += oi
                if iv > 0:
                    diag["contracts_with_iv"] += 1
                if 0 < iv < IV_MIN_PLAUSIBLE:
                    diag["contracts_placeholder_iv"] += 1
                # An unpriced contract contributes nothing but noise. Without
                # this it contributes a gamma that has underflowed to zero,
                # which silently removes the strike from wall contention.
                if oi <= 0 or iv < IV_MIN_PLAUSIBLE:
                    continue
                # Price off the expiry's ATM vol, not this contract's own, so a
                # jittery per-strike quote cannot reorder the wall ranking. The
                # contract still has to HAVE a vol to qualify above — a strike
                # yfinance never priced is not one we want to weight.
                g = bs_gamma(spot, K, T, RISK_FREE, ref_iv if ref_iv else iv)
                gex = g * oi * CONTRACT_SIZE * spot * spot * 0.01
                if not math.isfinite(gex):
                    continue

                b = gex_all.setdefault(K, _bucket())
                b[side] += gex
                b["net"] += gex if side == "call" else -gex
                if is_front:
                    b0 = gex_0dte.setdefault(K, _bucket())
                    b0[side] += gex
                    b0["net"] += gex if side == "call" else -gex

    return spot, gex_all, gex_0dte, has_true_0dte, spot_was_corrected, diag

def find_flip(book, spot=None):
    """Strike where net dealer gamma changes sign, interpolated, nearest spot.

    Crossings are taken on each strike's OWN net gamma, not on a running
    cumulative sum. The cumulative version cannot work here: strikes are already
    truncated to +/-STRIKE_BAND of spot, so the running total starts from an
    arbitrary zero at the bottom edge of the band. Measured live on 2026-08-20
    that produced exactly ONE crossing, at 502.92 -- the band edge, ~ES
    points below the market -- which the sanity guard then dropped every time.

    Worse, it was self-contradictory: cumulative net gamma at spot was about
    -300M (short gamma), yet a flip *below* spot reads as "positive gamma". The
    per-strike form has no such dependence on where the band begins, and is what
    gex-dashboard's gex_calculator.find_zero_gamma() uses.

    Many strikes flip sign, so nearest-to-spot selection is what keeps a
    deep-OTM crossing on stale open interest from being returned.
    """
    strikes = sorted(book.keys())
    if not strikes:
        return None
    if len(strikes) < 2:
        return float(strikes[0])

    crossings = []
    for i in range(len(strikes) - 1):
        k0, k1 = strikes[i], strikes[i + 1]
        n0, n1 = book[k0]["net"], book[k1]["net"]
        if (n0 >= 0 > n1) or (n0 < 0 <= n1):
            crossings.append(k0 + (k1 - k0) * (-n0 / (n1 - n0)) if n1 != n0 else float(k0))

    if crossings:
        ref = spot if spot else crossings[0]
        return min(crossings, key=lambda x: abs(x - ref))
    # No sign change anywhere: fall back to the strike closest to zero net.
    return float(min(strikes, key=lambda k: abs(book[k]["net"])))

def find_walls(book, spot):
    """Call wall = most call gamma above spot; put wall = most put gamma below.

    Measured on each side's OWN gamma, not on the net. On a net basis a strike
    carrying both heavy calls and heavy puts nets toward zero and drops out of
    the running entirely, so the wall lands on some thinner strike or vanishes.
    """
    above = {k: v["call"] for k, v in book.items() if k > spot and v["call"] > 0}
    below = {k: v["put"] for k, v in book.items() if k < spot and v["put"] > 0}
    cw = max(above, key=above.get) if above else None
    pw = max(below, key=below.get) if below else None
    return cw, pw

def wall_ladder(book, spot, side, depth=WALL_LADDER_DEPTH):
    """The top `depth` strikes by that side's own gamma, ranked, strongest first.

    Returns [(strike, gamma, share_of_leader), ...]. Rank 1 is the wall the old
    single-argmax reported; publishing the runners-up alongside it is what makes
    a near-tie visible instead of showing up as a level that teleports between
    two strikes on consecutive runs.
    """
    if side == "call":
        cand = {k: v["call"] for k, v in book.items() if k > spot and v["call"] > 0}
    else:
        cand = {k: v["put"] for k, v in book.items() if k < spot and v["put"] > 0}
    if not cand:
        return []
    ranked = sorted(cand.items(), key=lambda kv: kv[1], reverse=True)[:max(1, depth)]
    lead = ranked[0][1]
    return [(k, g, (g / lead if lead > 0 else 0.0)) for k, g in ranked]


def wall_contest(book, spot, side, threshold=WALL_CONTEST_FRAC):
    """Is the winning wall strike actually a clear winner?

    The wall is an argmax, so two strikes carrying similar gamma trade places on
    any small IV or spot drift and the printed level teleports by the distance
    between them. Observed live on 2026-08-20: K525 at 70.2M vs K527 at 61.5M —
    only 12% apart but 200 YM points apart — and the put wall oscillated between
    the two across consecutive 5-minute runs.

    Returns (leader, rival, rival_share) when the runner-up is within `threshold`
    of the leader, else None. The level itself is deliberately NOT changed; this
    only reports that it is contested.
    """
    if side == "call":
        cand = {k: v["call"] for k, v in book.items() if k > spot and v["call"] > 0}
    else:
        cand = {k: v["put"] for k, v in book.items() if k < spot and v["put"] > 0}
    if len(cand) < 2:
        return None
    ranked = sorted(cand.items(), key=lambda kv: kv[1], reverse=True)
    (k1, g1), (k2, g2) = ranked[0], ranked[1]
    if g1 <= 0:
        return None
    share = g2 / g1
    return (k1, k2, share) if share >= (1.0 - threshold) else None

def sane_level(val_ym, spot_ym):
    if val_ym is None:
        return False
    return abs(val_ym - spot_ym) <= GAMMA_SANITY_FRAC * spot_ym

# ==========================================================
#   WALL FREEZE — walls are a once-a-day number, flips are not
# ==========================================================
# A wall is an open-interest concentration, and OI is published overnight by the
# OCC and cannot change during the session. Re-deriving it every few minutes and
# weighting it by Black-Scholes gamma at live spot injects a spot-dependence into
# something that is static by nature, which is what makes the printed level step
# around during the day. So the walls are computed ONCE and held for the session,
# the way MenthorQ's getDailyLevels does it.
#
# The flip and the regime are deliberately NOT frozen. Their entire content is
# where price sits relative to the gamma profile *right now*; freezing them would
# throw away the only genuinely intraday-useful part of this.
#
# Note this stores the final YM-converted prices, not DIA strikes. Freezing the
# strike and re-converting each run would let the DIA->YM ratio drift move the
# level, defeating the point.
def _freeze_path():
    return os.path.join(OUTPUT_DIR, "es_walls_frozen.json")


def load_frozen_walls(day):
    """Today's frozen walls, or None if absent/stale/unreadable."""
    try:
        with open(_freeze_path(), encoding="utf-8") as f:
            d = json.load(f)
    except (OSError, ValueError):
        return None
    if d.get("date") != day or not d.get("walls"):
        return None
    return d


def save_frozen_walls(day, walls, contested, es_price, spy_spot):
    payload = {
        "date": day,
        "frozen_at": datetime.now(ET).strftime("%H:%M:%S"),
        "es_price": round(es_price),
        "spy_spot": round(spy_spot, 2),
        "walls": walls,
        "contested": contested or {},
    }
    os.makedirs(OUTPUT_DIR, exist_ok=True)
    tmp = _freeze_path() + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(payload, f, indent=2)
    os.replace(tmp, _freeze_path())
    return payload


# ==========================================================
#   PART 3 — CONFLUENCE DETECTOR
# ==========================================================
def find_confluences(levels, tol=CONFLUENCE_TOL):
    sorted_lv = sorted(levels, key=lambda x: x[1])
    clusters = []
    used = set()
    for i, (n1, v1) in enumerate(sorted_lv):
        if i in used:
            continue
        cluster = [(n1, v1)]
        used.add(i)
        for j in range(i + 1, len(sorted_lv)):
            if j in used:
                continue
            n2, v2 = sorted_lv[j]
            if abs(v2 - v1) <= tol:
                cluster.append((n2, v2))
                used.add(j)
        if len(cluster) >= 2:
            clusters.append(cluster)
    return clusters

# ==========================================================
#   PART 4 — CSV EXPORT FOR NINJATRADER
# ==========================================================
def export_csv(pivots, on, pw, pm, gamma_levels, regime, spy_spot, es_price,
               contested=None):
    os.makedirs(OUTPUT_DIR, exist_ok=True)
    csv_path = os.path.join(OUTPUT_DIR, "es_levels.csv")

    # Write to a temp file in the same folder and os.replace() it into place.
    # NinjaTrader polls this file on a timer and reloads it the moment the mtime
    # changes, so it CAN catch a half-written file — and since YMLevels now
    # erases the line of any level missing from a reload, a truncated read would
    # wipe levels off the chart rather than merely leave them stale. os.replace
    # is atomic on Windows, so a reader sees either the old file or the new one.
    tmp_path = csv_path + ".tmp"
    with open(tmp_path, "w", newline="") as f:
        writer = csv.writer(f)
        writer.writerow(["Name", "Price", "Type", "Color"])

        for name, val in pivots.items():
            if name.startswith("R"):
                color = "Red"
            elif name.startswith("S"):
                color = "Green"
            else:
                color = "Gray"
            writer.writerow([name, val, "Session", color])

        if on:
            writer.writerow(["ONH", on["ONH"], "Overnight", "Orange"])
            writer.writerow(["ONM", on["ON-Mid"], "Overnight", "Orange"])
            writer.writerow(["ONL", on["ONL"], "Overnight", "Orange"])

        if pw:
            writer.writerow(["PWH", pw["PWH"], "PriorWeek", "Blue"])
            writer.writerow(["PWM", pw["PW-Mid"], "PriorWeek", "Blue"])
            writer.writerow(["PWC", pw["PWC"], "PriorWeek", "Blue"])
            writer.writerow(["PWL", pw["PWL"], "PriorWeek", "Blue"])

        if pm:
            writer.writerow(["PMH", pm["PMH"], "PriorMonth", "Purple"])
            writer.writerow(["PMM", pm["PM-Mid"], "PriorMonth", "Purple"])
            writer.writerow(["PMC", pm["PMC"], "PriorMonth", "Purple"])
            writer.writerow(["PML", pm["PML"], "PriorMonth", "Purple"])

        for name, val in gamma_levels.items():
            if "Flip" in name:
                color = "Yellow"
            elif "Call" in name:
                color = "Magenta"
            else:
                color = "Cyan"
            writer.writerow([name, val, "Gamma", color])

        # ---- Meta rows the dashboard + logger read directly ----
        writer.writerow(["Regime", regime, "Meta", "White"])
        writer.writerow(["SPY_Spot", round(spy_spot, 2), "Meta", "White"])
        writer.writerow(["ES_Price", round(es_price), "Meta", "White"])
        writer.writerow(["Updated",
                         datetime.now(ET).strftime("%Y-%m-%d %H:%M:%S"),
                         "Meta", "White"])

        # Contested walls ride as Meta rows: every NinjaScript reader skips
        # Type == "Meta", so these never draw a line or affect the strategy --
        # they are here so the CSV records that a wall was a coin toss.
        for label, rival_px in (contested or {}).items():
            writer.writerow([label.replace(" ", "") + "Rival", rival_px,
                             "Meta", "White"])

    os.replace(tmp_path, csv_path)
    return csv_path

# ==========================================================
#   MAIN
# ==========================================================
def main(refreeze=False):
    now_et = datetime.now(ET)
    today = now_et.date()

    print("\n" + "=" * 60)
    print(f"  ES FULL LEVEL SHEET")
    print(f"  Generated: {now_et.strftime('%Y-%m-%d %I:%M %p')} ET")
    print("=" * 60)

    # ---- Session ----
    print("\n[1/4] Fetching ES futures data...")
    daily = fetch_es_daily()
    intra, es_price = fetch_es_intraday()
    print(f"  Current ES: {es_price:.0f}")

    # Pivots must come from the last COMPLETED session. daily[-2] assumed Yahoo had
    # already emitted a partial bar for today; run pre-market before that bar exists
    # and daily[-2] is the day before yesterday, silently shifting every pivot by a
    # session. Select by date instead of by position.
    prior_sessions = [b for b in daily if b["dt"].date() < today]
    if not prior_sessions:
        raise RuntimeError("No completed ES daily session found in the Yahoo history")
    yesterday = prior_sessions[-1]
    pivots = compute_pivots(yesterday)
    on = compute_overnight(intra, today)
    pw, pw_start, pw_end = compute_prior_week(daily, today)
    pm, pm_start, pm_end = compute_prior_month(daily, today)

    # ---- Gamma ----
    print("\n[2/4] Fetching SPY options chain (may take 20-40s)...")
    ref_dia = es_price / SPY_TO_ES          # rough seed, only to filter strikes
    spy_spot, gex_all, gex_0dte, has_true_0dte, corrected, gdiag = compute_gex(ref_dia)

    # Anchor sanity + regime to the FUTURES price, which we trust.
    spot_ym = es_price

    # LIVE DIA->YM ratio. The fixed 100 multiplier is wrong — the true ratio
    # drifts (dividends, tracking error, futures basis). Using es_price/spy_spot
    # lands every gamma level correctly instead of hundreds of points off.
    if spy_spot and spy_spot > 0:
        spy_to_es = es_price / spy_spot
    else:
        spy_to_es = SPY_TO_ES               # fallback if DIA spot unavailable

    flip_full = find_flip(gex_all, spy_spot)
    flip_0dte = find_flip(gex_0dte, spy_spot)
    cw_full, pw_full_wall = find_walls(gex_all, spy_spot)
    cw_0dte, pw_0dte = find_walls(gex_0dte, spy_spot)
    print(f"  SPY spot: {spy_spot:.2f}  (live ratio {spy_to_es:.2f}, "
          f"{round(spy_spot * spy_to_es)} ES equiv)"
          + ("  [corrected from stale SPY close]" if corrected else ""))
    if not has_true_0dte:
        print("  NOTE: no true 0DTE expiry today; 'Flip 0DTE' uses nearest expiry.")

    # ---- Print sections ----
    print(f"\n--- Yesterday's Pivots ({yesterday['dt'].date()}) ---")
    for k, v in pivots.items():
        print(f"  {k:<8} {v}")

    if on:
        print(f"\n--- Overnight ({on['ONL']}-{on['ONH']}, {on['ONH'] - on['ONL']} pts) ---")
        for k, v in on.items():
            print(f"  {k:<8} {v}")

    if pw:
        print(f"\n--- Prior Week ({pw_start} -> {pw_end}) ---")
        for k, v in pw.items():
            print(f"  {k:<8} {v}")

    if pm:
        print(f"\n--- Prior Month ({pm_start} -> {pm_end}) ---")
        for k, v in pm.items():
            print(f"  {k:<8} {v}")

    print(f"\n--- Gamma Levels (from SPY options) ---")
    gamma_levels = {}

    def add_gamma(name, dia_val, note=""):
        if dia_val is None:
            return
        v = round(dia_val * spy_to_es)
        if not sane_level(v, spot_ym):
            guard = round(GAMMA_SANITY_FRAC * spot_ym)
            print(f"  {name:<17} {v}  [DROPPED: >{guard} pts from spot {spot_ym}]")
            return
        gamma_levels[name] = v
        print(f"  {name:<17} {v}" + (f"   {note}" if note else ""))

    add_gamma("Gamma Flip", flip_full)
    add_gamma("Flip 0DTE", flip_0dte)
    add_gamma("Call Wall", cw_full)
    add_gamma("Call Wall 0DTE", cw_0dte)
    add_gamma("Put Wall", pw_full_wall)
    add_gamma("Put Wall 0DTE", pw_0dte)

    # Ranks 2..N of each wall. Rank 1 is already published above under its plain
    # name, so start at 2 and never re-emit the leader.
    if gex_all:
        for side, label in (("call", "Call Wall"), ("put", "Put Wall")):
            ladder = wall_ladder(gex_all, spy_spot, side)
            for rank, (K, _g, share) in enumerate(ladder[1:], start=2):
                add_gamma(f"{label} {rank}", K, note=f"{share:.0%} of rank 1")

    # Every gamma number is gamma x OPEN INTEREST. If the feed hands back no open
    # interest there is nothing to compute, and an empty section on its own looks
    # identical to "no levels qualified". Say which one it was.
    if not gamma_levels:
        print("  (none)")
        if gdiag["contracts_with_oi"] == 0:
            print()
            print(f"  GAMMA UNAVAILABLE - the options feed returned NO open interest.")
            print(f"    {gdiag['strikes_in_band']} contracts in the +/-{STRIKE_BAND:.0%} "
                  f"strike band across {gdiag['expiries_used']} expiries,")
            print(f"    {gdiag['contracts_with_iv']} had an implied vol, but 0 had open interest.")
            print( "    GEX = gamma x OI, so with no OI there is nothing to compute.")
            print( "    This is a DATA problem, not a settings problem. Regime falls back to the pivot.")
        else:
            print(f"\n  All gamma levels failed the {GAMMA_SANITY_FRAC:.0%}-from-spot sanity guard.")
            print(f"    ({gdiag['contracts_with_oi']} contracts had OI, total {int(gdiag['total_oi']):,})")

    # ---- Regime: SIGN OF TOTAL NET GAMMA, else PIVOT FALLBACK ----
    #
    # This used to be `spot > flip ? POSITIVE : NEGATIVE`. That is unusable here.
    # With ~12 net-gamma sign changes inside the strike band and DIA strikes $1
    # apart (~100 YM points), the nearest crossing to spot is essentially always
    # on the same side of price: over 17 consecutive live runs on 2026-08-20 the
    # flip came in between +67 and +145 YM points ABOVE spot every single time,
    # so the regime printed NEGATIVE 17/17 no matter what the market did. It was
    # a constant, not a reading.
    #
    # Summed net GEX is the quantity the regime is actually asking about — are
    # dealers net long or net short gamma — and it is already computed. It gave
    # the same answer that day (-350.9M => NEGATIVE) by a route that can change.
    net_total = sum(v["net"] for v in gex_all.values()) if gex_all else 0.0

    if gex_all and net_total != 0:
        regime = "POSITIVE" if net_total > 0 else "NEGATIVE"
        tag = "stable/mean-revert" if regime == "POSITIVE" else "volatile/trending"
        print(f"\n  Regime: {regime} ({tag})   [net GEX {net_total:,.0f}]")
    else:
        # No usable gamma flip today (thin/bad options). Fall back to structure:
        # price vs the daily Pivot. Keeps NinjaTrader stat buckets resolving.
        pvt = pivots["Pivot"]
        regime = "POSITIVE" if es_price > pvt else "NEGATIVE"
        print(f"\n  Regime: {regime}   [PIVOT FALLBACK - no gamma data at all; "
              f"price {round(es_price)} vs Pivot {pvt}]")

    # ---- Is either wall a clear winner, or a coin toss between two strikes? ----
    contested = {}
    for side, label in (("call", "Call Wall"), ("put", "Put Wall")):
        c = wall_contest(gex_all, spy_spot, side)
        if not c:
            continue
        leader, rival, share = c
        contested[label] = round(rival * spy_to_es)
        print(f"\n  ! {label} is CONTESTED: {round(leader * spy_to_es)} leads, but "
              f"{round(rival * spy_to_es)} carries {share:.0%} of its gamma.")
        print(f"    They swap on small IV/spot drift, moving the level "
              f"{abs(round((leader - rival) * spy_to_es))} pts. Treat it as a zone.")

    # ---- Walls hold for the session; flip and regime stay live ----
    # A book is only credible if a decent share of its strikes actually carry
    # gamma. When implied vol is unpublished the gamma underflows to zero and the
    # book collapses onto a handful of strikes, which still yields a wall — just
    # a meaningless one. Measured on the bad 2026-08-21 pre-market run: 11 of 62
    # strikes. A healthy RTH book had all 62.
    strikes_with_gamma = sum(1 for v in gex_all.values() if v["call"] or v["put"])
    book_ok = bool(gex_all) and strikes_with_gamma >= max(8, len(gex_all) // 3)
    if gex_all and not book_ok:
        print(f"\n  ! Gamma book looks degenerate: only {strikes_with_gamma} of "
              f"{len(gex_all)} strikes carry any gamma.")
        print( "    Implied vol is probably not published yet. Walls will NOT be pinned.")

    # Never pin before the open. Pre-market the chain is not properly priced on
    # EITHER input: open interest is absent overnight, and the implied vols that
    # do appear are far too low — measured 2026-08-21 at 09:11 ET the ATM vols
    # came out at 0.031 / 0.016 / 0.008 against 0.096-0.128 in the prior RTH
    # session, with the term structure inverted. Those clear the placeholder
    # floor but are not real vols, so a pre-market pin would fix a distorted
    # reading in place for the entire day. Pre-market runs still publish live
    # provisional walls; they just do not become the day's pin.
    rth_open = now_et.replace(hour=9, minute=30, second=0, microsecond=0)
    in_rth = now_et >= rth_open
    gamma_is_credible = book_ok and in_rth
    if book_ok and not in_rth:
        mins = int((rth_open - now_et).total_seconds() // 60)
        print(f"\n  Pre-market: walls shown are PROVISIONAL and are not pinned "
              f"({mins} min to the open).")
        print( "    The chain is not fully priced yet. Re-run after 09:30 ET to set the day's walls.")

    day = today.isoformat()
    flip_levels = {k: v for k, v in gamma_levels.items() if "Flip" in k}
    wall_levels = {k: v for k, v in gamma_levels.items() if "Wall" in k}

    frozen = None if refreeze else load_frozen_walls(day)
    if frozen:
        live_walls = wall_levels
        wall_levels = frozen["walls"]
        contested = frozen.get("contested", {})
        print(f"\n  Walls HELD from {frozen['frozen_at']} ET (ES was {frozen['es_price']}) "
              f"- {len(wall_levels)} levels reused, not recomputed.")
        print( "    Flip and regime above are live. Re-run with --refreeze to redo the walls.")
        # The Gamma Levels block above printed what the CURRENT chain says. Those
        # are not what goes to the CSV once the walls are held, so show the held
        # values and how far the live book has drifted from them.
        drift = [(n, wall_levels[n], live_walls[n])
                 for n in wall_levels if n in live_walls and live_walls[n] != wall_levels[n]]
        print("    held:  " + "  ".join(f"{n} {p}" for n, p in sorted(wall_levels.items())))
        if drift:
            print("    live would now say: "
                  + "  ".join(f"{n} {lv} ({lv - hp:+d})" for n, hp, lv in sorted(drift)))
        else:
            print("    (the live book still agrees with every held level)")
    elif wall_levels and gamma_is_credible:
        # Only ever pin a reading that actually has gamma behind it. Non-empty is
        # NOT sufficient: pre-market, unpriced contracts still carry open
        # interest, so walls get produced from a book where gamma has underflowed
        # on nearly every strike. On 2026-08-21 that pinned a put wall one point
        # below spot and a regime of POSITIVE, against NEGATIVE the day before.
        # A pin is for the whole session, so it has to clear a real bar.
        f = save_frozen_walls(day, wall_levels, contested, es_price, spy_spot)
        print(f"\n  Walls FROZEN for {day} at {f['frozen_at']} ET - {len(wall_levels)} levels.")
        print( "    Later runs today reuse these; flip and regime keep updating.")
    elif not wall_levels:
        print("\n  No walls to pin - no gamma data this run.")
    # The remaining case (walls exist but were not pinned, because the book is
    # degenerate or it is still pre-market) has already explained itself above.

    gamma_levels = dict(flip_levels)
    gamma_levels.update(wall_levels)

    # ---- Confluence ----
    print(f"\n[3/4] Scanning for confluences (within {CONFLUENCE_TOL} pts)...")
    all_levels = []
    for k, v in pivots.items():
        all_levels.append((k, v))
    if on:
        for k, v in on.items():
            all_levels.append((k, v))
    if pw:
        for k, v in pw.items():
            all_levels.append((k, v))
    if pm:
        for k, v in pm.items():
            all_levels.append((k, v))
    for k, v in gamma_levels.items():
        all_levels.append((k, v))

    clusters = find_confluences(all_levels)

    print(f"\n--- CONFLUENCE ZONES ---")
    if clusters:
        for c in clusters:
            avg = round(sum(v for _, v in c) / len(c))
            names = ", ".join(n for n, _ in c)
            distance = avg - round(es_price)
            direction = "above" if distance > 0 else "below"
            print(f"  ES {avg}  ({abs(distance)} pts {direction})  <- {names}")
    else:
        print("  No confluences found within tolerance.")

    # ---- CSV Export ----
    print(f"\n[4/4] Exporting to CSV for NinjaTrader...")
    csv_path = export_csv(pivots, on, pw, pm, gamma_levels,
                          regime, spy_spot, es_price, contested)
    print(f"  Levels exported to: {csv_path}")

    print("\n" + "=" * 60)
    print(f"  Current ES: {es_price:.0f}   Regime: {regime}")
    print("=" * 60 + "\n")


if __name__ == "__main__":
    _ap = argparse.ArgumentParser(description="ES full level sheet")
    _ap.add_argument("--refreeze", action="store_true",
                     help="recompute today's walls and re-pin them, discarding the "
                          "existing freeze (use if the morning run caught bad data)")
    _args, _ = _ap.parse_known_args()

    # Friendly runner for non-technical users running the packaged .exe:
    # never crash-and-vanish. On any failure, show a plain-English message and
    # keep the window open so it can be read.
    try:
        main(refreeze=_args.refreeze)
    except KeyboardInterrupt:
        print("\nCancelled.")
    except Exception as e:
        print("\n" + "=" * 60)
        print("  SOMETHING WENT WRONG")
        print("=" * 60)
        print(f"  {type(e).__name__}: {e}")
        print()
        print("  Common causes:")
        print("   - No internet connection, or Yahoo Finance is unreachable")
        print("   - The market data feed is temporarily down or rate-limited")
        print("   - Options data was empty/unavailable for SPY this morning")
        print()
        print("  Try again in a few minutes. If it keeps failing, check your")
        print("  internet connection first.")
        print("=" * 60)
    try:
        input("\nPress Enter to close...")
    except EOFError:
        pass
