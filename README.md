# ES Levels — NinjaTrader 8 Level-Break Trading System

A NinjaTrader 8 system for **ES (E-mini S&P 500)** that computes daily support/resistance
and options-gamma levels in Python, draws them on the chart, and trades a "star" (★)
level-break signal.

There is also a YM (E-mini Dow) sibling of this system. This repo is the ES version.

## What's in here

**Python (levels engine — run these outside NinjaTrader):**

| File | What it does |
|------|--------------|
| `es_full_levels.py` | Fetches ES futures + SPY options, computes session / overnight / prior-week / prior-month pivots and gamma walls, writes `es_levels.csv`. |
| `compute_es_stats.py` | Turns logged level interactions (`es_interactions.csv`) into break/reject/hold probabilities (`es_stats.csv`). |
| `es_levels_loop.py` | Optional: re-runs the levels script on a timer through the session. |
| `run_es_levels.bat` | Optional double-click launcher (runs levels + stats). Portable — no hard-coded paths. |

**NinjaScript (`.cs` — go in NinjaTrader):**

| File | Type | What it does |
|------|------|--------------|
| `ESLevels.cs` | Indicator | Draws the levels + an on-chart dashboard, prints the ★ signal, exposes a `StarSignal` plot. |
| `ESPolaris.cs` | Strategy | Trades the ★ signal (self-contained, with its own dashboard, gates, and risk controls). |
| `ESLevelsLogger.cs` | Indicator | Logs every price/level interaction to `es_interactions.csv` so the stats can build. |

## Requirements

- **NinjaTrader 8**
- **Python 3.10+** with the packages in `requirements.txt`:
  ```
  pip install -r requirements.txt
  ```

## Setup

### 1. Python side

Put the Python files anywhere you like (e.g. a `es_levels` folder). Then run:

```
python es_full_levels.py
```

This writes `es_levels.csv` into your NinjaTrader user folder automatically:
`Documents\NinjaTrader 8\es_levels\es_levels.csv`
(the folder is created on first run — you don't make it yourself).

To also build the probability stats, run:

```
python compute_es_stats.py
```

Or just double-click `run_es_levels.bat`, which runs both.

### 2. NinjaTrader side

1. Copy the indicators into `Documents\NinjaTrader 8\bin\Custom\Indicators\`:
   - `ESLevels.cs`
   - `ESLevelsLogger.cs`
2. Copy the strategy into `Documents\NinjaTrader 8\bin\Custom\Strategies\`:
   - `ESPolaris.cs`
3. Open the NinjaScript Editor and press **F5** to compile.
4. On an ES chart, add:
   - **ESLevels** indicator (levels + dashboard)
   - **ESLevelsLogger** indicator (builds the stats over time)
   - **ESPolaris** strategy (test in **Sim** first)

## How the pieces connect

```
es_full_levels.py  ──writes──►  es_levels.csv  ──read by──►  ESLevels / ESPolaris  (draw + trade)
ESLevelsLogger     ──writes──►  es_interactions.csv  ──read by──►  compute_es_stats.py  ──writes──►  es_stats.csv  ──read by──►  ESLevels (break-odds display)
```

The stats start empty and **build up over live sessions** — the logger only records
interactions while it's running on a live chart. Break-odds show "building" until enough
data accumulates.

## Notes on the levels

- Levels come from **ES futures** (price) and **SPY options** (gamma walls) via `yfinance`.
- The star (★) fires three ways: a level break with the trend, a reversal (price reclaims
  the trend EMA), and a pullback-and-resume continuation. All are tunable in the indicator
  settings.
- Point-based thresholds (touch/break tolerances) are set for the ES price scale; adjust in
  the indicator settings if you prefer.

## Disclaimer

This is trading software provided as-is, for educational purposes. Test in simulation before
using real money. Trading futures involves substantial risk of loss. Nothing here is financial
advice, and no outcome is guaranteed.
