# Setup Guide — paste this to your AI assistant

**How to use this file:** open Claude (or any AI chat assistant), paste this entire file
in, and say *"help me set this up, I'm a beginner."* The assistant will walk you through it
one step at a time. You can ask it to slow down, explain any term, or troubleshoot errors as
you go.

---

## Instructions for the AI assistant

You are helping a **beginner** install the "ES Levels" trading system for NinjaTrader 8.
Assume they have never used a command line, GitHub, or NinjaTrader's code editor. Go **one
step at a time**, wait for them to confirm each step worked before moving on, and offer to
explain anything. Never assume a step succeeded — ask them what they saw. If they hit an
error, help them read it and fix it before continuing. Be patient and encouraging.

Here is everything you need to know about the system and the correct install order.

### What the system is

A NinjaTrader 8 system for **ES (E-mini S&P 500)** futures. It has two halves:

1. **Python scripts** that fetch market data and compute price levels (support/resistance
   pivots + options-gamma walls), saving them to a CSV file.
2. **NinjaTrader indicators + a strategy** that read that CSV, draw the levels on the chart,
   and trade a "star" (★) level-break signal.

### The 10 files in the repo

**Python (data engine):**
- `es_full_levels.py` — computes the levels, writes `es_levels.csv`
- `compute_es_stats.py` — computes break/reject probabilities from logged interactions
- `es_levels_loop.py` — optional: auto-reruns the levels on a timer
- `run_es_levels.bat` — optional double-click launcher

**NinjaScript (`.cs` — go into NinjaTrader):**
- `ESLevels.cs` — indicator: draws levels + dashboard
- `ESPolaris.cs` — strategy: trades the ★ signal
- `ESLevelsLogger.cs` — indicator: logs level interactions so stats can build

**Repo files:** `README.md`, `requirements.txt`, `.gitignore`

### Prerequisites — check these FIRST, before anything else

Walk the user through confirming each of these before installing. If one is missing, help
them install it.

1. **NinjaTrader 8** is installed and they can open a chart.
2. **Python 3.10 or newer** is installed. Check by having them open Command Prompt
   (Windows key → type `cmd` → Enter) and run:
   ```
   python --version
   ```
   If that shows an error or a version below 3.10, help them install Python from python.org.
   **Important:** during install they must tick "Add Python to PATH."
3. They have the 10 files downloaded (from GitHub: green "Code" button → "Download ZIP" →
   unzip it).

### Install order — guide them through these in sequence

**STEP 1 — Install the Python packages.**
Have them open Command Prompt, navigate to the folder with the files, and run:
```
pip install -r requirements.txt
```
This installs `requests`, `yfinance`, `numpy`, `scipy`. Confirm it finishes without red
errors. (If `pip` isn't recognized, try `python -m pip install -r requirements.txt`.)

**STEP 2 — Run the levels script once to test it.**
In the same Command Prompt (still in the files folder):
```
python es_full_levels.py
```
It should print a level sheet ending with a line like
`Levels exported to: ...NinjaTrader 8\es_levels\es_levels.csv`.
That line means it worked AND it auto-created the output folder. If they get an error,
help them read it — common causes are no internet, or a package didn't install in step 1.

**STEP 3 — Install the NinjaTrader indicators + strategy.**
The three `.cs` files go into NinjaTrader's code folders:
- `ESLevels.cs` and `ESLevelsLogger.cs` → `Documents\NinjaTrader 8\bin\Custom\Indicators\`
- `ESPolaris.cs` → `Documents\NinjaTrader 8\bin\Custom\Strategies\`

Then in NinjaTrader: open the **NinjaScript Editor** (New menu → NinjaScript Editor), press
**F5** to compile, and check the **Errors** tab at the bottom. If it's empty, they compiled.
If there are red errors, have them copy the error text and you help fix it.

**STEP 4 — Put everything on an ES chart.**
Open an ES futures chart, then:
- Right-click → Indicators → add **ESLevels** (draws levels + dashboard)
- Right-click → Indicators → add **ESLevelsLogger** (builds the stats over time)
- Right-click → Strategies → add **ESPolaris**

**STEP 5 — CRITICAL SAFETY STEP.**
Tell them clearly: **run ESPolaris in Simulation mode first, not with real money.** They
should watch it for several sessions to confirm the signals behave sensibly before ever
enabling it on a live account. Trading futures can lose money fast. Make sure they
understand this before they finish.

### Ongoing use (mention once they're set up)

Each trading day they run `python es_full_levels.py` (or double-click `run_es_levels.bat`)
to refresh the levels. The chart updates automatically.

The probability stats start **empty** and build up over live sessions — the logger only
records interactions while it's running on a live chart. The break-odds on the dashboard
will show "building" until enough data accumulates. That's normal.

### Tone reminder for the assistant

This person is a beginner and may feel out of their depth. Keep each reply short and focused
on the current step. Celebrate small wins. Always confirm the previous step worked before
moving on. When in doubt, ask them to paste a screenshot or the exact text they see.
