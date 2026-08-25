// =====================================================================================
//  ESPolaris — ES level-break ★ strategy (standalone) + self-contained dashboard
// =====================================================================================
//  Trades the SAME signal the ESLevels indicator prints as a ★ (v19 rule): a bar
//  CLOSES through a level from ym_levels.csv with the 50 EMA sloping the same way.
//        break UP   + 50 EMA rising  = LONG
//        break DOWN + 50 EMA falling = SHORT
//  Any level is eligible. No odds / ADX / RSI / lean / conviction gating — pure
//  break-with-trend, exactly like the star on the chart.
//
//  Skeleton is CouncilProAtm with the council/voting engine removed. What's kept:
//  session windows + skip, FixedTicks or ATM order handling, break-even, daily
//  profit/loss risk kill, and the es_levels.csv reader that powers the break.
//
//  DASHBOARD: this build carries its OWN WPF card (the CouncilPro card lived in the
//  indicator, which is why it disappeared when the council was cut). It is drawn by
//  the strategy directly onto the chart — no companion indicator required. Layout:
//    TOP  — status + a BIG day-P&L number + record + position + risk progress bars
//    MID  — the last ★ break + room to the next level either side
//    BTM  — ARM LONG · ARM SHORT · AUTO ARM toggle · CLOSE ALL  (manual control strip)
//  The arm / auto-arm / close-all flags used to live on the cp indicator; ESPolaris
//  now owns them as its own fields, read by OnBarUpdate and set by the buttons.
//
//  Card only appears on a CHART (ChartControl is null in Strategy Analyzer / backtest
//  and everything card-related is null-guarded, so it simply no-ops there).
//
//  No "exit on flat signal": the star is a one-bar PULSE (0 on every non-break bar),
//  so an exit-on-flat would bail the bar after entry. Exits are the fixed stop/target
//  (or the ATM template's brackets), break-even, the session flatten, the daily kill,
//  and CLOSE ALL. An opposite-direction star reverses the position.
//
//  Enums live in the parent NinjaTrader.NinjaScript namespace (not .Strategies) for
//  the same reason CouncilProAtm's do: NT8's import-time code generator emits
//  UNQUALIFIED references to [NinjaScriptProperty] enum types, and a type tucked
//  inside .Strategies fails CS0246 there — surfacing only as "Import failed". Names
//  are prefixed (Star* / ESPolaris*) so they can't collide with CouncilProAtm's Cp*.
// =====================================================================================

#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
#endregion

namespace NinjaTrader.NinjaScript
{
	public enum ESStarOrderMode { Atm, FixedTicks }
	public enum ESStarHistMode  { FullHistoricalProcessing, SignalWarmUpOnly }
	public enum ESPolarisCorner { TopLeft, TopRight, BottomLeft, BottomRight }
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public class ESPolaris : Strategy
	{
		private string   atmId    = string.Empty;
		private string   atmOrder = string.Empty;
		private DateTime sessionDate = Core.Globals.MinDate;

		private double freshBaseline = 0;
		private bool   freshCaptured = false;
		private bool   dayKilled = false;
		private int    prevSig = 0;
		private bool   killedProfit = false;
		private bool   beMoved = false;   // break-even: stop already moved to BE for the current position

		// ---- signal source: SELF-CONTAINED, and an EXACT copy of the ESLevels indicator's
		// star rule. The indicator's ★ is a BAR-CLOSE signal (Close[2]->Close[1], evaluated
		// on IsFirstTickOfBar, 50 EMA read at [1]/[1+lookback]) — NOT intrabar. ComputeStar
		// reproduces that math exactly, so ESPolaris fires on the same bars the ★ prints, with
		// no phantom intrabar entries. Everything is in this one file; no indicator instance.
		private class YmLevel { public string Name; public double Price; }
		private List<YmLevel> ymLevels = new List<YmLevel>();
		private DateTime lastLevelCheck = Core.Globals.MinDate;
		private DateTime lastLevelWrite = Core.Globals.MinDate;
		private int      levelRoomUpTicks   = int.MaxValue;
		private int      levelRoomDownTicks = int.MaxValue;
		private string   nearUpName = "", nearDnName = "";
		private int      levelBreakDir = 0;    // +1/-1 on the bar a level closes through w/ EMA agreeing, else 0
		private EMA      lvlEma;                // 50 EMA (flow-bypass slope); built in DataLoaded
		private EMA      trendEma;              // v30 — faster star trend gate (default 21)
		// v31 — continuation-star pullback state
		private bool     contArmed = false;
		private int      contLastBar = -1000;
		private int      lastAnyStarBar = -1000; // v33 — global spacing across all star types
		// de-dupe a given level+side within a bar-window so one break doesn't re-star every bar
		private Dictionary<string, int> lastBreakBar = new Dictionary<string, int>();

		// ---- order-flow bridge (flow_state.csv, written by AlightenFootprintOrderFlow) ----
		// Same file + convention YMLevels reads: each signal +1 bull / -1 bear / 0 none.
		// The star is blocked when flow CONTRADICTS the break. Contradiction-only,
		// fail-open (off or no file = no block).
		private int flowAbsorp, flowExh, flowStopVol, flowFade, flowDiverg;
		private bool flowValid;
		private DateTime lastFlowCheck = Core.Globals.MinDate;

		// last ★ (for the dashboard) — most recent star computed, entered or not
		private string   lastStarText = "";
		private int      lastStarSign = 0;
		private int      lastStarBar  = -1;


		// ---- manual control flags (backed by the card's buttons; owned here now) ----
		private volatile bool uiArmLong    = false;
		private volatile bool uiArmShort   = false;
		private volatile bool uiAutoArm    = true;   // ON by default = trade every qualifying ★
		private volatile bool uiCloseAllReq = false;

		// hide the long parameter label at the top-left of the chart (keeps the name in pickers)
		public override string DisplayName => State == State.SetDefaults ? "ESPolaris" : string.Empty;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Name							= "ESPolaris";
				Description						= "ES level-break ★ strategy — SELF-CONTAINED: reads es_levels.csv and computes the v19 star (level break + 50 EMA slope) on each tick, with its own dashboard. No indicator required.";
				Calculate						= Calculate.OnEachTick;   // match the indicator's star cadence — catch wick-throughs live
				EntriesPerDirection				= 1;
				EntryHandling					= EntryHandling.UniqueEntries;
				IsExitOnSessionCloseStrategy	= true;
				ExitOnSessionCloseSeconds		= 30;
				IsInstantiatedOnEachOptimizationIteration = true;
				BarsRequiredToTrade				= 65;

				Contracts	= 1;  TradeLongs = true;  TradeShorts = true;
				StarEmaPeriod = 50;  StarSlopeLookback = 3;  StarCooldownBars = 12;
				StarTrendEmaPeriod = 21;  StarUsePriceReclaim = true;
				UseContinuationStar = true;  ContinuationCooldownBars = 8;
				ContinuationPullbackTicks = 12;
				MinBarsBetweenStars = 5;

				OrderMode	= ESStarOrderMode.FixedTicks;
				AtmTemplate	= string.Empty;
				StopTicks	= 40;  TargetTicks = 80;
				EnableBreakEven = false; BreakEvenTriggerTicks = 40; BreakEvenOffsetTicks = 2;

				EnableTF1 = false; StartTime1 = T("09:30"); EndTime1 = T("11:30"); FlattenTF1 = false;
				EnableTF2 = false; StartTime2 = T("13:00"); EndTime2 = T("15:00"); FlattenTF2 = false;
				EnableTF3 = false; StartTime3 = T("00:00"); EndTime3 = T("00:00"); FlattenTF3 = false;
				EnableSkipWindow = false; SkipStartTime = T("11:45"); SkipEndTime = T("13:00");

				// Globals.UserDataDir = NT8's own "Documents\NinjaTrader 8", resolved
				// per-machine, so this travels to another box without remapping.
				YmLevelsCsvPath     = System.IO.Path.Combine(
					NinjaTrader.Core.Globals.UserDataDir, "es_levels", "es_levels.csv");
				LevelRefreshSeconds = 30;
				SRMinRoomTicks      = 0;   // 0 = off. >0 = skip a break with less room than this to the NEXT level ahead.
				UseFlowFilter       = false; // OFF by default — clean star. Flow filter opt-in only.
				FlowContraVotesToBlock = 1; // v28 — 2 signals in the vote, 1 is the sensible default
				FlowUseAbsorption     = true;
				FlowUseExhaustion     = true;
				FlowUseStoppingVol    = false;
				FlowUseFadingMomentum = false;
				FlowUseDivergence     = false;
				FlowTrendBypassTicks  = 20; // v29 — strong with-trend breaks skip the flow filter

				StartFreshOnEnable		= false;
				UseUnrealizedPnl		= true;
				EnableDailyProfitTarget	= false;  DailyProfitTarget = 500;
				EnableDailyLossLimit	= false;  DailyLossLimit    = 500;

				HistoricalMode = ESStarHistMode.FullHistoricalProcessing;
				EnableDebug = false;

				// dashboard
				ShowDashboard = true;
				CardCorner    = ESPolarisCorner.TopLeft;
				CardMarginX   = 12;
				CardMarginY   = 12;
				CardWidth     = 300;
				CardHeight    = 430;
			}
			else if (State == State.Configure)
			{
				if (OrderMode == ESStarOrderMode.FixedTicks)
				{
					SetStopLoss(CalculationMode.Ticks, StopTicks);
					SetProfitTarget(CalculationMode.Ticks, TargetTicks);
				}
			}
			else if (State == State.DataLoaded)
			{
				// Self-contained: build the 50 EMA that gates the star. No indicator
				// instance, no external file to patch. Levels come from ym_levels.csv
				// (loaded in OnBarUpdate); the star is computed here in ESPolaris.
				lvlEma = EMA(StarEmaPeriod);
				trendEma = EMA(StarTrendEmaPeriod < 2 ? 2 : StarTrendEmaPeriod);
			}
			else if (State == State.Historical)
			{
				if (ShowDashboard && ChartControl != null)
					ChartControl.Dispatcher.InvokeAsync(() => TryInjectCard());
			}
			else if (State == State.Terminated)
			{
				if (ChartControl != null)
					ChartControl.Dispatcher.InvokeAsync(() => TryRemoveCard());
			}
		}

		private static DateTime T(string hhmm) => DateTime.Parse(hhmm, CultureInfo.InvariantCulture);
		private static int TimeInt(DateTime d) => d.Hour * 10000 + d.Minute * 100 + d.Second;

		private bool InWindow(bool enabled, DateTime start, DateTime end, int now)
		{
			if (!enabled) return false;
			int s = TimeInt(start), e = TimeInt(end);
			return s <= e ? (now >= s && now <= e) : (now >= s || now <= e);
		}

		private double DayRealized(out int w, out int l)
		{
			double sum = 0; w = 0; l = 0;
			if (SystemPerformance != null && SystemPerformance.AllTrades != null)
				foreach (Trade t in SystemPerformance.AllTrades)
					if (t.Exit != null && t.Exit.Time.Date == sessionDate.Date)
					{ double p = t.ProfitCurrency; sum += p; if (p >= 0) w++; else l++; }
			return sum;
		}

		private double OpenPnl()
		{
			if (OrderMode == ESStarOrderMode.Atm)
				return (State == State.Realtime && atmId.Length > 0) ? GetAtmStrategyUnrealizedProfitLoss(atmId) : 0;
			return Position.MarketPosition != MarketPosition.Flat
				? Position.GetUnrealizedProfitLoss(PerformanceUnit.Currency, Close[0]) : 0;
		}

		// ---- YM levels (read the same ym_levels.csv that the ESLevels indicator uses) ----
		private void MaybeReloadLevels()
		{
			if ((DateTime.Now - lastLevelCheck).TotalSeconds < LevelRefreshSeconds && ymLevels.Count > 0) return;
			lastLevelCheck = DateTime.Now;
			try
			{
				if (!System.IO.File.Exists(YmLevelsCsvPath)) return;
				DateTime wt = System.IO.File.GetLastWriteTime(YmLevelsCsvPath);
				if (wt == lastLevelWrite && ymLevels.Count > 0) return;
				lastLevelWrite = wt;
				LoadYmLevels();
			}
			catch { }
		}

		private void LoadYmLevels()
		{
			var list = new List<YmLevel>();
			try
			{
				string[] lines = System.IO.File.ReadAllLines(YmLevelsCsvPath);
				for (int i = 1; i < lines.Length; i++)          // skip header
				{
					string line = (lines[i] ?? "").Trim();
					if (line.Length == 0) continue;
					string[] p = line.Split(',');
					if (p.Length < 4) continue;
					if (p[2].Trim() == "Meta") continue;         // Regime/Updated/etc. are not tradeable levels
					double price;
					if (!double.TryParse(p[1], NumberStyles.Any, CultureInfo.InvariantCulture, out price)) continue;
					if (price <= 0) continue;
					list.Add(new YmLevel { Name = p[0].Trim(), Price = price });
				}
				ymLevels = list;
				if (EnableDebug) Print($"[{Name}] loaded {ymLevels.Count} YM levels from {YmLevelsCsvPath}");
			}
			catch (Exception ex) { if (EnableDebug) Print($"[{Name}] YM levels load error: {ex.Message}"); }
		}

		// Room = ticks to the nearest real level in each direction (int.MaxValue = no level that side = unlimited room).
		private void RefreshLevelRoom()
		{
			levelRoomUpTicks = int.MaxValue; levelRoomDownTicks = int.MaxValue;
			nearUpName = ""; nearDnName = "";
			if (ymLevels.Count == 0 || TickSize <= 0) return;
			double c = Close[0];
			double bestUp = double.NaN, bestDn = double.NaN;
			foreach (YmLevel lv in ymLevels)
			{
				if (lv.Price > c)      { if (double.IsNaN(bestUp) || lv.Price < bestUp) { bestUp = lv.Price; nearUpName = lv.Name; } }
				else if (lv.Price < c) { if (double.IsNaN(bestDn) || lv.Price > bestDn) { bestDn = lv.Price; nearDnName = lv.Name; } }
			}
			if (!double.IsNaN(bestUp)) levelRoomUpTicks   = Math.Max(0, (int)((bestUp - c) / TickSize));
			if (!double.IsNaN(bestDn)) levelRoomDownTicks = Math.Max(0, (int)((c - bestDn) / TickSize));
		}

		// SELF-CONTAINED star — an EXACT copy of the ESLevels indicator's DetectLevelBreaks
		// rule, so ESPolaris fires on precisely the bars the ★ prints (no phantoms).
		// The indicator's star is a BAR-CLOSE signal, NOT intrabar:
		//   • evaluated once per bar, on IsFirstTickOfBar (i.e. the first tick of the NEW
		//     bar, judging the bar that JUST closed)
		//   • crossing tested on CLOSED bars: Close[2] (prior) -> Close[1] (just closed)
		//   • 50 EMA read at [1] and [1+lookback] (also on closed bars)
		//   • 50 EMA slope must agree with the break direction
		//   • per level+side cooldown so one level doesn't re-star every bar
		// This matches the chart ★ bar-for-bar. It does mean entries land on the close of
		// the breaking bar (the first tick of the next bar), not mid-bar — that's inherent
		// to the ★ being a close-through, which is what you asked to match.
		// v25 — read flow_state.csv (footprint bridge). +1 bull / -1 bear / 0 none each.
		private void LoadFlowState()
		{
			flowValid = false;
			flowAbsorp = flowExh = flowStopVol = flowFade = flowDiverg = 0;
			try
			{
				string dir = System.IO.Path.GetDirectoryName(YmLevelsCsvPath);
				if (string.IsNullOrEmpty(dir)) return;
				string fp = System.IO.Path.Combine(dir, "flow_state.csv");
				if (!System.IO.File.Exists(fp)) return;
				foreach (string line in System.IO.File.ReadAllLines(fp))
				{
					string[] p = line.Split(',');
					if (p.Length < 2) continue;
					string k = p[0].Trim().ToLowerInvariant();
					int v; if (!int.TryParse(p[1].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out v)) continue;
					if (k == "absorption") flowAbsorp = v;
					else if (k == "exhaustion") flowExh = v;
					else if (k == "stoppingvolume") flowStopVol = v;
					else if (k == "fadingmomentum") flowFade = v;
					else if (k == "divergence") flowDiverg = v;
				}
				flowValid = true;
			}
			catch { flowValid = false; }
		}

		// v25 — does order flow CONTRADICT a break in direction d (+1 long / -1 short)?
		// absorption/exhaustion/stopvol/fade/divergence == -d = the break is failing.
		// Contradiction-only; neutral flow passes. Fail-open when off / no file loaded.
		private bool FlowContradicts(int d)
		{
			if (!UseFlowFilter || !flowValid || d == 0) return false;
			int opp = -d;
			int contra = 0;
			if (FlowUseAbsorption     && flowAbsorp  == opp) contra++;
			if (FlowUseExhaustion     && flowExh     == opp) contra++;
			if (FlowUseStoppingVol    && flowStopVol == opp) contra++;
			if (FlowUseFadingMomentum && flowFade    == opp) contra++;
			if (FlowUseDivergence     && flowDiverg  == opp) contra++;
			int need = FlowContraVotesToBlock < 1 ? 1 : FlowContraVotesToBlock;
			return contra >= need;   // v28 — only the enabled signals vote (default absorp+exh)
		}

		private void ComputeStar()
		{
			levelBreakDir = 0;
			if (ymLevels.Count == 0 || lvlEma == null || trendEma == null) return;
			if (CurrentBar < Math.Max(StarEmaPeriod, StarTrendEmaPeriod) + StarSlopeLookback + 1) return;
			if (!IsFirstTickOfBar) return;               // evaluate once, on the just-closed bar

			// v30 — flow-bypass still reads the 50 EMA (lvlEma); the star TREND GATE now
			// uses the faster trendEma so it catches reversals the slow 50 misses.
			double ema0 = lvlEma[1];                     // 50 EMA (for flow bypass slope)
			double emaN = lvlEma[1 + StarSlopeLookback];
			double tEma0 = trendEma[1];                  // fast trend EMA (star gate)
			double tEmaN = trendEma[1 + StarSlopeLookback];
			int emaDir = tEma0 > tEmaN ? 1 : (tEma0 < tEmaN ? -1 : 0);

			double cPrev = Close[2];                     // bar before the one that just closed
			double cNow  = Close[1];                     // the bar that just closed

			foreach (YmLevel lv in ymLevels)
			{
				bool brokeUp   = cPrev <= lv.Price && cNow > lv.Price;
				bool brokeDown = cPrev >= lv.Price && cNow < lv.Price;
				if (!brokeUp && !brokeDown) continue;

				int d = brokeUp ? 1 : -1;
				bool slopeAgrees = (d == emaDir);
				bool reclaim = StarUsePriceReclaim &&
					((d > 0 && cNow > tEma0) || (d < 0 && cNow < tEma0));
				if (!slopeAgrees && !reclaim) continue;  // trend gate: slope OR price-reclaim

				string key = lv.Name + (d > 0 ? "U" : "D");
				int lb;
				if (lastBreakBar.TryGetValue(key, out lb) && (CurrentBar - lb) < StarCooldownBars) continue;

				// v25 — ORDER-FLOW GATE (same as YMLevels): block the star if footprint
				// flow contradicts the break. Contradiction-only, fail-open. Doesn't
				// stamp cooldown so the level can still star later if flow clears.
				// v29 — WITH-TREND BYPASS: skip the flow filter entirely when the 50 EMA
				// is strongly sloped (established trend); flow only vets weak-trend breaks.
				double slopeTicks = TickSize > 0 ? Math.Abs(ema0 - emaN) / TickSize : 0;
				bool strongTrend = FlowTrendBypassTicks > 0 && slopeTicks >= FlowTrendBypassTicks;
				if (!strongTrend && FlowContradicts(d)) continue;

				// v33 — global star spacing: no new star of any type within MinBarsBetweenStars
				if (MinBarsBetweenStars > 0 && (CurrentBar - lastAnyStarBar) < MinBarsBetweenStars) continue;

				lastBreakBar[key] = CurrentBar;
				lastAnyStarBar = CurrentBar;

				levelBreakDir = d;
				bool isReversal = !slopeAgrees && reclaim;
				lastStarText = (d > 0 ? "\u25B2 LONG" : "\u25BC SHORT") + (isReversal ? " reversal at " : " break ") + lv.Name + " " + lv.Price.ToString("0")
							 + " \u00b7 trend " + (emaDir > 0 ? "up" : "down");
				lastStarSign = d;
				lastStarBar  = CurrentBar;

				if (EnableDebug)
					Print($"[{Name}] {Time[0]:HH:mm:ss} \u2605 dir={d} break {lv.Name} {lv.Price:0} \u00b7 trend {(emaDir > 0 ? "up" : "down")}");

				return;                                  // one star per bar is enough
			}

			// v31 — CONTINUATION STAR (pullback-and-resume), same as YMLevels. Runs only
			// if the level-break loop above didn't fire (no return hit). Arms on a pullback
			// to the trend EMA, fires when price closes back in the trend direction.
			if (UseContinuationStar && emaDir != 0)
			{
				double band = ContinuationPullbackTicks * TickSize;
				bool pulledBack = (emaDir > 0 && Low[1] <= tEma0 + band)
							   || (emaDir < 0 && High[1] >= tEma0 - band);
				if (pulledBack) contArmed = true;

				bool resume = contArmed &&
					((emaDir > 0 && cNow > cPrev) || (emaDir < 0 && cNow < cPrev));
				bool contCool = (CurrentBar - contLastBar) >= ContinuationCooldownBars;
				bool globalOk = MinBarsBetweenStars <= 0 || (CurrentBar - lastAnyStarBar) >= MinBarsBetweenStars;

				if (resume && contCool && globalOk)
				{
					contArmed = false;
					contLastBar = CurrentBar;
					lastAnyStarBar = CurrentBar;
					levelBreakDir = emaDir;
					lastStarText = (emaDir > 0 ? "\u25B2 LONG" : "\u25BC SHORT") + " pullback continuation \u00b7 trend " + (emaDir > 0 ? "up" : "down");
					lastStarSign = emaDir;
					lastStarBar  = CurrentBar;
					if (EnableDebug)
						Print($"[{Name}] {Time[0]:HH:mm:ss} \u2605 CONT dir={emaDir} close {cNow:0} reclaimed trendEMA {tEma0:0}");
				}
			}
			else contArmed = false;
		}

		protected override void OnBarUpdate()
		{
			if (BarsInProgress != 0) return;
			if (CurrentBar < BarsRequiredToTrade) return;

			if (Time[0].Date != sessionDate.Date)
			{ sessionDate = Time[0].Date; dayKilled = false; freshCaptured = false; freshBaseline = 0; prevSig = 0; }

			MaybeReloadLevels();   // refresh ym_levels.csv if it changed (throttled)
			if (UseFlowFilter && (DateTime.Now - lastFlowCheck).TotalSeconds >= 2)
			{ lastFlowCheck = DateTime.Now; LoadFlowState(); }
			RefreshLevelRoom();    // recompute room-to-nearest-real-level (dashboard + optional filter)
			ComputeStar();         // self-contained on-tick ★ detection -> levelBreakDir

			if (HistoricalMode == ESStarHistMode.SignalWarmUpOnly && State == State.Historical)
			{ Snap(0, 0, 0, 0, false, 0); return; }   // warm up bars/EMA only; no historical entries

			// ---- risk ----
			int w, l;
			double dayReal = DayRealized(out w, out l);
			if (StartFreshOnEnable && State == State.Realtime && !freshCaptured)
			{ freshBaseline = dayReal; freshCaptured = true; }
			double risk = dayReal - (StartFreshOnEnable ? freshBaseline : 0);
			if (UseUnrealizedPnl) risk += OpenPnl();

			if (!dayKilled)
			{
				if (EnableDailyProfitTarget && risk >= DailyProfitTarget) { dayKilled = true; killedProfit = true; }
				if (EnableDailyLossLimit    && risk <= -DailyLossLimit)    { dayKilled = true; killedProfit = false; }
			}
			if (dayKilled) { FlattenAll(); Snap(0, dayReal, w, l, true, risk); return; }

			// ---- manual CLOSE ALL (card button) ----
			if (uiCloseAllReq)
			{ uiCloseAllReq = false; FlattenAll(); Snap(0, dayReal, w, l, false, risk); return; }

			ManageBreakEven();   // move the stop to break-even once the position runs far enough (FixedTicks only)

			// ---- session ----
			int now = ToTime(Time[0]);
			bool anyWindowEnabled = EnableTF1 || EnableTF2 || EnableTF3;
			bool inWindow = InWindow(EnableTF1, StartTime1, EndTime1, now)
						 || InWindow(EnableTF2, StartTime2, EndTime2, now)
						 || InWindow(EnableTF3, StartTime3, EndTime3, now);
			bool inSkip   = EnableSkipWindow && InWindow(true, SkipStartTime, SkipEndTime, now);
			bool entriesAllowed = (!anyWindowEnabled || inWindow) && !inSkip;

			bool flattenNow = anyWindowEnabled && !inWindow &&
				((EnableTF1 && FlattenTF1) || (EnableTF2 && FlattenTF2) || (EnableTF3 && FlattenTF3));
			if (flattenNow) { FlattenAll(); Snap(0, dayReal, w, l, false, risk); return; }

			// ---- signal ----
			// levelBreakDir is set by ComputeStar ONLY on the tick a level is crossed
			// (0 on every other tick), and a per-level+side cooldown stops re-fires, so
			// running this on each tick can't double-enter. Re-entry rule (your choice):
			// a fresh entry fires when a star occurs AND we're not already in that side.
			//   • same-direction star while holding -> ignored (still in position)
			//   • same-direction star after an exit -> fires (we're flat again)
			//   • opposite star while holding        -> reverses (ManageNative/ManageAtm
			//     close the other side first)
			// ENTRY RULE: a star only ever OPENS a position, and only from FLAT.
			//   • star while flat            -> enter
			//   • star (either dir) while in -> ignored; the trade exits ONLY via stop /
			//                                    target / break-even / session flatten /
			//                                    daily kill / CLOSE ALL. No flipping.
			int dir = levelBreakDir;                 // +1 long / -1 short / 0 none
			bool starThisBar = dir != 0;
			bool repeat = dir != 0 && dir == prevSig;
			prevSig = dir;

			bool flatNow = (OrderMode == ESStarOrderMode.Atm)
				? AtmPosition() == MarketPosition.Flat
				: Position.MarketPosition == MarketPosition.Flat;
			bool fresh = starThisBar && flatNow;     // flat-only: no reversal, no stacking

			// ---- manual arm / auto-arm (card buttons) ----
			bool armedThisDir = (dir > 0 && uiArmLong) || (dir < 0 && uiArmShort);
			bool uiAllows = uiAutoArm || armedThisDir;

			bool srBlocked = SRMinRoomTicks > 0 &&
					((dir > 0 && levelRoomUpTicks < SRMinRoomTicks) || (dir < 0 && levelRoomDownTicks < SRMinRoomTicks));
			bool canEnter = entriesAllowed && fresh && !srBlocked && uiAllows;

			if (armedThisDir && canEnter)
			{ if (dir > 0) uiArmLong = false; else uiArmShort = false; }   // consume the one-shot manual arm

			if (EnableDebug && fresh && srBlocked)
				Print($"[{Name}] {Time[0]:HH:mm} entry blocked — no room (up {levelRoomUpTicks}t / down {levelRoomDownTicks}t, need {SRMinRoomTicks}t · next {(dir > 0 ? nearUpName : nearDnName)})");
			if (EnableDebug && fresh && !uiAllows)
				Print($"[{Name}] {Time[0]:HH:mm} entry blocked — not armed (auto off, no manual arm for dir {dir})");
			if (EnableDebug && starThisBar && !flatNow)
				Print($"[{Name}] {Time[0]:HH:mm} ★ dir={dir} ignored — already in a trade (exit via stop/target/session only)");
			if (EnableDebug && fresh)
				Print($"[{Name}] {Time[0]:HH:mm} {(repeat ? "REPEAT" : "NEW")} ★ dir={dir} flat={flatNow} allowed={entriesAllowed}");

			if (OrderMode == ESStarOrderMode.Atm)
			{ if (State == State.Realtime) ManageAtm(dir, canEnter); }
			else ManageNative(dir, canEnter);

			Snap(dir, dayReal, w, l, false, risk);
		}

		private void ManageNative(int dir, bool canEnter)
		{
			// canEnter already requires flat, so these only ever OPEN — never flip.
			if (Position.MarketPosition != MarketPosition.Flat) return;
			if (dir > 0 && TradeLongs && canEnter)
				EnterLong(Contracts, "STARlong");
			else if (dir < 0 && TradeShorts && canEnter)
				EnterShort(Contracts, "STARshort");
		}

		private MarketPosition AtmPosition()
		{
			if (State != State.Realtime || atmId.Length == 0) return MarketPosition.Flat;
			return GetAtmStrategyMarketPosition(atmId);
		}

		private void ManageAtm(int dir, bool canEnter)
		{
			if (atmId.Length > 0 && atmOrder.Length == 0 && AtmPosition() == MarketPosition.Flat)
				atmId = string.Empty;
			// Flat-only: a star never flips an ATM position. Only open when there's no
			// live ATM strategy running (canEnter already required flat).
			if (AtmPosition() != MarketPosition.Flat || atmId.Length > 0) return;
			if (dir > 0 && TradeLongs && canEnter)       CreateAtm(true);
			else if (dir < 0 && TradeShorts && canEnter) CreateAtm(false);
		}

		private void CreateAtm(bool isLong)
		{
			if (string.IsNullOrEmpty(AtmTemplate))
			{ Print(Name + ": no ATM template selected — pick one, or use FixedTicks."); return; }
			atmOrder = GetAtmStrategyUniqueId();
			string newAtm = GetAtmStrategyUniqueId();
			AtmStrategyCreate(isLong ? OrderAction.Buy : OrderAction.SellShort, OrderType.Market,
				0, 0, TimeInForce.Day, atmOrder, AtmTemplate, newAtm,
				(errorCode, callbackId) =>
				{ if (callbackId == atmOrder) { atmId = (errorCode == ErrorCode.NoError) ? newAtm : string.Empty; atmOrder = string.Empty; } });
		}

		// Break-even (FixedTicks mode only — Atm mode's stop lives in its ATM template).
		private void ManageBreakEven()
		{
			if (!EnableBreakEven || OrderMode != ESStarOrderMode.FixedTicks) return;

			if (Position.MarketPosition == MarketPosition.Flat)
			{
				if (beMoved) { SetStopLoss(CalculationMode.Ticks, StopTicks); beMoved = false; }
				return;
			}
			if (beMoved) return;

			double entry = Position.AveragePrice;
			double trig  = BreakEvenTriggerTicks * TickSize;
			bool reached = Position.MarketPosition == MarketPosition.Long
				? High[0] >= entry + trig
				: Low[0]  <= entry - trig;
			if (!reached) return;

			double bePx = Position.MarketPosition == MarketPosition.Long
				? entry + BreakEvenOffsetTicks * TickSize
				: entry - BreakEvenOffsetTicks * TickSize;
			SetStopLoss(CalculationMode.Price, bePx);
			beMoved = true;
			if (EnableDebug)
				Print($"[{Name}] {Time[0]:HH:mm} break-even: stop \u2192 {bePx:0.##} (+{BreakEvenOffsetTicks}t from entry {entry:0.##})");
		}

		private void FlattenAll()
		{
			if (OrderMode == ESStarOrderMode.Atm)
			{
				if (State == State.Realtime && atmId.Length > 0 && AtmPosition() != MarketPosition.Flat)
					AtmStrategyClose(atmId);
				atmId = string.Empty;
			}
			else if (Position.MarketPosition == MarketPosition.Long)  ExitLong();
			else if (Position.MarketPosition == MarketPosition.Short) ExitShort();
		}

		// =====================================================================================
		//  DASHBOARD  (self-contained WPF card injected onto the chart)
		// =====================================================================================
		#region Dashboard snapshot (strategy thread -> UI thread)
		private string rStatus = "NO TRADE";
		private string rPos    = "Flat";
		private double rDayPnl  = 0;
		private int    rW = 0, rL = 0;
		private double rProfitFrac = -1, rLossFrac = -1;
		private bool   rKilled = false, rKilledProfit = false;
		private string rStarText = "", rRoomText = "", rLevelsText = "";
		private int    rStarSign = 0;

		private void Snap(int dir, double dayReal, int w, int l, bool killed, double risk)
		{
			rKilled = killed; rKilledProfit = killedProfit;
			rStatus = killed ? (killedProfit ? "TARGET HIT" : "LOSS LIMIT")
					: (dir > 0 ? "LONG" : dir < 0 ? "SHORT" : "NO TRADE");
			rPos = (OrderMode == ESStarOrderMode.Atm)
				? AtmPosition().ToString()
				: (Position.MarketPosition == MarketPosition.Flat ? "Flat"
					: Position.MarketPosition + " " + Position.Quantity + " @ " + Position.AveragePrice.ToString("0.##"));
			rDayPnl = dayReal; rW = w; rL = l;
			rProfitFrac = (EnableDailyProfitTarget && DailyProfitTarget > 0) ? Math.Max(0, Math.Min(1, risk / DailyProfitTarget)) : -1;
			rLossFrac   = (EnableDailyLossLimit   && DailyLossLimit   > 0) ? Math.Max(0, Math.Min(1, -risk / DailyLossLimit)) : -1;

			// last ★ text fades to "waiting" after the cooldown window
			if (lastStarBar >= 0 && (CurrentBar - lastStarBar) <= 40) { rStarText = lastStarText; rStarSign = lastStarSign; }
			else { rStarText = "no ★ yet — waiting for a level break"; rStarSign = 0; }

			string up = levelRoomUpTicks   == int.MaxValue ? "—" : levelRoomUpTicks   + "t";
			string dn = levelRoomDownTicks == int.MaxValue ? "—" : levelRoomDownTicks + "t";
			rRoomText = "Room  ▲ " + (nearUpName.Length > 0 ? nearUpName + " " : "") + up
					  + "   ▼ " + (nearDnName.Length > 0 ? nearDnName + " " : "") + dn;
			rLevelsText = ymLevels.Count + " levels loaded";

			if (ShowDashboard) UpdateCard();
		}
		#endregion

		#region Card fields
		private Grid   chartGrid;
		private Border card;
		private Grid   cardShell;
		private ScrollViewer contentScroll;
		private TextBlock tbTitle, tbInstr, tbStatus, tbDayPnl, tbRecord, tbPos;
		private TextBlock tbStar, tbRoom, tbLevels;
		private Border statusRow, pnlRow, starRow;
		private Grid   profitBarGrid, lossBarGrid;
		private System.Windows.Shapes.Rectangle profitFill, lossFill;
		private TextBlock tbProfitLbl, tbLossLbl;
		private Button btnArmLong, btnArmShort, btnAuto, btnCloseAll;
		private Border gripNW, gripNE, gripSW, gripSE;

		private bool     injected, absolutePlaced;
		private DateTime lastUiUpdate = DateTime.MinValue;
		private const int UiThrottleMs = 150;

		private bool      dragging; private Point dragStart; private Thickness dragOrigMargin;
		private bool      resizing; private int resizeCorner; private Point resizeStart;
		private double    resizeOrigW, resizeOrigH; private Thickness resizeOrigMargin;

		private static Brush Frz(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
		private static readonly Brush CardBg     = Frz(Color.FromArgb(235, 20, 22, 28));
		private static readonly Brush TextDim    = Frz(Color.FromRgb(150, 150, 156));
		private static readonly Brush RowBorder  = Frz(Color.FromRgb(120, 120, 126));
		private static readonly Brush RowText    = Frz(Color.FromRgb(214, 214, 218));
		private static readonly Brush RowFill    = Frz(Color.FromArgb(28, 255, 255, 255));
		private static readonly Brush NeutralAcc = Frz(Color.FromRgb(120, 120, 126));
		private static readonly Brush BarTrack   = Frz(Color.FromArgb(60, 255, 255, 255));
		private static readonly Brush BullBrush  = Frz(Color.FromRgb(0, 200, 100));
		private static readonly Brush BearBrush  = Frz(Color.FromRgb(230, 70, 70));
		private static readonly Brush GripBrush  = Frz(Color.FromRgb(78, 205, 196));
		private static readonly Brush BtnLongBg   = Frz(Color.FromArgb(46, 0, 200, 100));
		private static readonly Brush BtnShortBg  = Frz(Color.FromArgb(46, 230, 70, 70));
		private static readonly Brush BtnArmedLong = Frz(Color.FromArgb(150, 0, 200, 100));
		private static readonly Brush BtnArmedShort= Frz(Color.FromArgb(150, 230, 70, 70));
		private static readonly Brush BtnAutoOn    = Frz(Color.FromArgb(120, 78, 205, 196));
		private static readonly Brush BtnAutoOff   = Frz(Color.FromArgb(40, 150, 150, 156));
		private static readonly Brush BtnCloseBg   = Frz(Color.FromArgb(70, 200, 40, 40));
		#endregion

		#region Card build / inject / remove
		private void TryInjectCard()
		{
			try
			{
				if (injected || ChartControl == null) return;
				chartGrid = ChartControl.Parent as Grid;
				if (chartGrid == null)
				{
					DependencyObject p = ChartControl.Parent;
					while (p != null && !(p is Grid))
						p = LogicalTreeHelper.GetParent(p) ?? VisualTreeHelper.GetParent(p);
					chartGrid = p as Grid;
				}
				if (chartGrid == null) return;

				card = BuildCard();
				ApplyCornerPlacement();
				Grid.SetRowSpan(card, Math.Max(1, chartGrid.RowDefinitions.Count));
				Grid.SetColumnSpan(card, Math.Max(1, chartGrid.ColumnDefinitions.Count));
				System.Windows.Controls.Panel.SetZIndex(card, 1000);
				chartGrid.Children.Add(card);
				injected = true;
			}
			catch (Exception ex) { Print(Name + ": inject error " + ex.Message); }
		}

		private void TryRemoveCard()
		{
			try
			{
				if (card != null && chartGrid != null && chartGrid.Children.Contains(card))
					chartGrid.Children.Remove(card);
				card = null; chartGrid = null; injected = false; absolutePlaced = false;
			}
			catch (Exception ex) { Print(Name + ": remove error " + ex.Message); }
		}

		private Border BuildCard()
		{
			FontFamily fam = new FontFamily("Segoe UI");
			StackPanel stack = new StackPanel();

			tbTitle = new TextBlock { Text = "ES POLARIS  \u2605", Foreground = Brushes.White, FontFamily = fam,
				FontSize = 13, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center };
			stack.Children.Add(tbTitle);

			tbInstr = new TextBlock { Text = "Instrument: " + (Instrument != null ? Instrument.MasterInstrument.Name : "\u2014"),
				Foreground = TextDim, FontFamily = fam, FontSize = 10,
				HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 4) };
			stack.Children.Add(tbInstr);

			// ---- TOP: status ----
			tbStatus = new TextBlock { Text = "NO TRADE", Foreground = RowText, FontFamily = fam,
				FontSize = 15, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center,
				TextAlignment = System.Windows.TextAlignment.Center };
			statusRow = MakeRow(tbStatus, RowFill, RowBorder);
			stack.Children.Add(statusRow);

			// ---- TOP: BIG P&L ----
			TextBlock pnlHdr = new TextBlock { Text = "TODAY", Foreground = TextDim, FontFamily = fam,
				FontSize = 10, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
			tbDayPnl = new TextBlock { Text = "$0", Foreground = RowText, FontFamily = fam,
				FontSize = 30, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center,
				TextAlignment = System.Windows.TextAlignment.Center };
			tbRecord = new TextBlock { Text = "0W / 0L", Foreground = TextDim, FontFamily = fam,
				FontSize = 11, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
			StackPanel pnlStack = new StackPanel();
			pnlStack.Children.Add(pnlHdr); pnlStack.Children.Add(tbDayPnl); pnlStack.Children.Add(tbRecord);
			pnlRow = new Border { Background = RowFill, BorderBrush = RowBorder, BorderThickness = new Thickness(1.6),
				CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 6, 6, 6),
				Margin = new Thickness(0, 4, 0, 2), Child = pnlStack };
			stack.Children.Add(pnlRow);

			tbPos = new TextBlock { Text = "Flat", Foreground = RowText, FontFamily = fam,
				FontSize = 12, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center,
				Margin = new Thickness(0, 3, 0, 2) };
			stack.Children.Add(tbPos);

			// ---- risk progress bars ----
			tbProfitLbl = new TextBlock { Text = "", Foreground = TextDim, FontFamily = fam, FontSize = 9.5 };
			profitBarGrid = MakeBar(out profitFill, BullBrush);
			tbLossLbl = new TextBlock { Text = "", Foreground = TextDim, FontFamily = fam, FontSize = 9.5, Margin = new Thickness(0, 3, 0, 0) };
			lossBarGrid = MakeBar(out lossFill, BearBrush);
			stack.Children.Add(tbProfitLbl); stack.Children.Add(profitBarGrid);
			stack.Children.Add(tbLossLbl);   stack.Children.Add(lossBarGrid);

			// ---- MID: last ★ + room ----
			tbStar = new TextBlock { Text = "", Foreground = RowText, FontFamily = fam, FontSize = 11.5,
				FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center,
				TextAlignment = System.Windows.TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
			tbRoom = new TextBlock { Text = "", Foreground = TextDim, FontFamily = fam, FontSize = 10.5,
				HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = System.Windows.TextAlignment.Center,
				Margin = new Thickness(0, 2, 0, 0) };
			tbLevels = new TextBlock { Text = "", Foreground = TextDim, FontFamily = fam, FontSize = 9.5,
				HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 1, 0, 0) };
			StackPanel starStack = new StackPanel();
			starStack.Children.Add(tbStar); starStack.Children.Add(tbRoom); starStack.Children.Add(tbLevels);
			starRow = new Border { Background = RowFill, BorderBrush = RowBorder, BorderThickness = new Thickness(1.4),
				CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 5, 6, 5),
				Margin = new Thickness(0, 6, 0, 2), Child = starStack };
			stack.Children.Add(starRow);

			// ---- BOTTOM: manual control strip ----
			Grid armGrid = new Grid { Margin = new Thickness(0, 8, 0, 0) };
			armGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
			armGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
			armGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
			btnArmLong  = MakeButton("ARM LONG",  BtnLongBg,  BullBrush);
			btnArmShort = MakeButton("ARM SHORT", BtnShortBg, BearBrush);
			btnArmLong.Click  += (s, e) => { uiArmLong  = !uiArmLong;  };
			btnArmShort.Click += (s, e) => { uiArmShort = !uiArmShort; };
			Grid.SetColumn(btnArmLong, 0); Grid.SetColumn(btnArmShort, 2);
			armGrid.Children.Add(btnArmLong); armGrid.Children.Add(btnArmShort);
			stack.Children.Add(armGrid);

			btnAuto = MakeButton("AUTO ARM: ON", BtnAutoOn, GripBrush);
			btnAuto.Margin = new Thickness(0, 6, 0, 0);
			btnAuto.Click += (s, e) => { uiAutoArm = !uiAutoArm; };
			stack.Children.Add(btnAuto);

			btnCloseAll = MakeButton("CLOSE ALL", BtnCloseBg, BearBrush);
			btnCloseAll.Margin = new Thickness(0, 6, 0, 0);
			btnCloseAll.Click += (s, e) => { uiCloseAllReq = true; };
			stack.Children.Add(btnCloseAll);

			contentScroll = new ScrollViewer { Content = stack,
				VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
				HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
				HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
				Padding = new Thickness(0, 0, 4, 0) };

			cardShell = new Grid();
			cardShell.Children.Add(contentScroll);
			gripNW = MakeGrip(Cursors.SizeNWSE); gripNW.HorizontalAlignment = HorizontalAlignment.Left;  gripNW.VerticalAlignment = VerticalAlignment.Top;
			gripNE = MakeGrip(Cursors.SizeNESW); gripNE.HorizontalAlignment = HorizontalAlignment.Right; gripNE.VerticalAlignment = VerticalAlignment.Top;
			gripSW = MakeGrip(Cursors.SizeNESW); gripSW.HorizontalAlignment = HorizontalAlignment.Left;  gripSW.VerticalAlignment = VerticalAlignment.Bottom;
			gripSE = MakeGrip(Cursors.SizeNWSE); gripSE.HorizontalAlignment = HorizontalAlignment.Right; gripSE.VerticalAlignment = VerticalAlignment.Bottom;
			HookGrip(gripNW, 0); HookGrip(gripNE, 1); HookGrip(gripSW, 2); HookGrip(gripSE, 3);
			cardShell.Children.Add(gripNW); cardShell.Children.Add(gripNE); cardShell.Children.Add(gripSW); cardShell.Children.Add(gripSE);

			Border border = new Border {
				Width = Math.Max(240, CardWidth), Height = Math.Max(320, CardHeight),
				Background = CardBg, BorderBrush = RowBorder, BorderThickness = new Thickness(1.5),
				CornerRadius = new CornerRadius(10), Padding = new Thickness(6, 5, 6, 5),
				SnapsToDevicePixels = true, UseLayoutRounding = true, Cursor = Cursors.SizeAll,
				ToolTip = "Drag body to move · Drag corner to resize", Child = cardShell };
			border.MouseLeftButtonDown += OnCardDown;
			border.MouseMove           += OnCardMove;
			border.MouseLeftButtonUp   += OnCardUp;
			return border;
		}

		private Grid MakeBar(out System.Windows.Shapes.Rectangle fill, Brush fillBrush)
		{
			Grid g = new Grid { Height = 8, Margin = new Thickness(0, 1, 0, 1), Visibility = Visibility.Collapsed };
			g.Children.Add(new System.Windows.Shapes.Rectangle { Fill = BarTrack, RadiusX = 3, RadiusY = 3 });
			fill = new System.Windows.Shapes.Rectangle { Fill = fillBrush, RadiusX = 3, RadiusY = 3,
				HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
			g.Children.Add(fill);
			return g;
		}

		private Button MakeButton(string text, Brush bg, Brush accent)
		{
			return new Button {
				Content = text, FontFamily = new FontFamily("Segoe UI"), FontSize = 11.5, FontWeight = FontWeights.Bold,
				Foreground = Brushes.White, Background = bg, BorderBrush = accent, BorderThickness = new Thickness(1.3),
				Padding = new Thickness(4, 6, 4, 6), Cursor = Cursors.Hand, HorizontalContentAlignment = HorizontalAlignment.Center };
		}

		private Border MakeGrip(Cursor c)
		{
			return new Border { Width = 14, Height = 14, Background = GripBrush, CornerRadius = new CornerRadius(3),
				Opacity = 0.55, Cursor = c, Margin = new Thickness(-3), BorderBrush = RowText, BorderThickness = new Thickness(1) };
		}
		private void HookGrip(Border g, int corner)
		{
			g.MouseLeftButtonDown += (s, e) => StartResize(corner, e);
			g.MouseMove           += OnResizeMove;
			g.MouseLeftButtonUp   += OnResizeUp;
			g.MouseEnter          += (s, e) => g.Opacity = 0.9;
			g.MouseLeave          += (s, e) => { if (!resizing) g.Opacity = 0.55; };
		}

		private static Border MakeRow(TextBlock content, Brush fill, Brush border)
		{
			return new Border { Background = fill, BorderBrush = border, BorderThickness = new Thickness(1.4),
				CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 4, 6, 4),
				Margin = new Thickness(0, 4, 0, 2), Child = content };
		}

		private static Brush Tint(Brush src, byte alpha)
		{
			SolidColorBrush s = src as SolidColorBrush;
			if (s == null) return RowFill;
			SolidColorBrush b = new SolidColorBrush(Color.FromArgb(alpha, s.Color.R, s.Color.G, s.Color.B));
			b.Freeze(); return b;
		}
		#endregion

		#region UpdateCard
		private void UpdateCard()
		{
			if (!injected || card == null || ChartControl == null) return;
			if ((DateTime.UtcNow - lastUiUpdate).TotalMilliseconds < UiThrottleMs) return;
			lastUiUpdate = DateTime.UtcNow;

			// snapshot locals
			string status = rStatus, pos = rPos, starText = rStarText, roomText = rRoomText, levelsText = rLevelsText;
			double pnl = rDayPnl; int w = rW, l = rL; double pf = rProfitFrac, lf = rLossFrac;
			bool killed = rKilled, killedProfit = rKilledProfit; int starSign = rStarSign;
			bool armLong = uiArmLong, armShort = uiArmShort, auto = uiAutoArm;

			try
			{
				ChartControl.Dispatcher.InvokeAsync(() =>
				{
					try
					{
						if (card == null) return;

						Brush statusAcc = killed ? (killedProfit ? BullBrush : BearBrush)
							: (status == "LONG" ? BullBrush : status == "SHORT" ? BearBrush : NeutralAcc);
						tbStatus.Text = status; tbStatus.Foreground = statusAcc;
						statusRow.BorderBrush = statusAcc; statusRow.Background = Tint(statusAcc, 34);
						card.BorderBrush = statusAcc;

						Brush pnlAcc = pnl > 0 ? BullBrush : (pnl < 0 ? BearBrush : RowText);
						tbDayPnl.Text = (pnl < 0 ? "-$" : "$") + Math.Abs(pnl).ToString("0");
						tbDayPnl.Foreground = pnlAcc;
						pnlRow.BorderBrush = pnlAcc;
						tbRecord.Text = w + "W / " + l + "L";
						tbPos.Text = pos;
						tbPos.Foreground = pos.StartsWith("Long") ? BullBrush : (pos.StartsWith("Short") ? BearBrush : RowText);

						if (pf >= 0) { profitBarGrid.Visibility = Visibility.Visible; tbProfitLbl.Visibility = Visibility.Visible;
							tbProfitLbl.Text = "Profit target " + (pf * 100).ToString("0") + "%"; SetBar(profitFill, profitBarGrid, pf); }
						else { profitBarGrid.Visibility = Visibility.Collapsed; tbProfitLbl.Visibility = Visibility.Collapsed; }

						if (lf >= 0) { lossBarGrid.Visibility = Visibility.Visible; tbLossLbl.Visibility = Visibility.Visible;
							tbLossLbl.Text = "Loss limit " + (lf * 100).ToString("0") + "%"; SetBar(lossFill, lossBarGrid, lf); }
						else { lossBarGrid.Visibility = Visibility.Collapsed; tbLossLbl.Visibility = Visibility.Collapsed; }

						tbStar.Text = starText;
						tbStar.Foreground = starSign > 0 ? BullBrush : (starSign < 0 ? BearBrush : TextDim);
						tbRoom.Text = roomText;
						tbLevels.Text = levelsText;

						// button states reflect the arm flags
						btnArmLong.Background  = armLong  ? BtnArmedLong  : BtnLongBg;
						btnArmLong.Content     = armLong  ? "ARMED LONG"  : "ARM LONG";
						btnArmShort.Background = armShort ? BtnArmedShort : BtnShortBg;
						btnArmShort.Content    = armShort ? "ARMED SHORT" : "ARM SHORT";
						btnAuto.Background     = auto ? BtnAutoOn : BtnAutoOff;
						btnAuto.Content        = auto ? "AUTO ARM: ON" : "AUTO ARM: OFF";
					}
					catch { }
				});
			}
			catch { }
		}

		private static void SetBar(System.Windows.Shapes.Rectangle fill, Grid track, double frac)
		{
			double w = track.ActualWidth; if (w < 1) w = 260;
			fill.Width = Math.Max(0, Math.Min(1, frac)) * w;
		}
		#endregion

		#region Corner + drag + resize
		private void ApplyCornerPlacement()
		{
			if (card == null) return;
			bool left = CardCorner == ESPolarisCorner.TopLeft || CardCorner == ESPolarisCorner.BottomLeft;
			bool top  = CardCorner == ESPolarisCorner.TopLeft || CardCorner == ESPolarisCorner.TopRight;
			card.HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Right;
			card.VerticalAlignment   = top  ? VerticalAlignment.Top    : VerticalAlignment.Bottom;
			card.Margin = new Thickness(left ? CardMarginX : 0, top ? CardMarginY : 0, left ? 0 : CardMarginX, top ? 0 : CardMarginY);
			absolutePlaced = false;
		}

		private void EnsureAbsolutePlacement()
		{
			if (absolutePlaced || card == null || chartGrid == null) return;
			double leftPx = card.HorizontalAlignment == HorizontalAlignment.Left ? card.Margin.Left
						  : chartGrid.ActualWidth  - card.ActualWidth  - card.Margin.Right;
			double topPx  = card.VerticalAlignment == VerticalAlignment.Top ? card.Margin.Top
						  : chartGrid.ActualHeight - card.ActualHeight - card.Margin.Bottom;
			card.HorizontalAlignment = HorizontalAlignment.Left; card.VerticalAlignment = VerticalAlignment.Top;
			card.Margin = new Thickness(Math.Max(0, leftPx), Math.Max(0, topPx), 0, 0);
			absolutePlaced = true;
		}

		// Don't start a drag when the press lands on a button (or its inner parts).
		private static bool FromButton(object src)
		{
			DependencyObject d = src as DependencyObject;
			while (d != null)
			{
				if (d is ButtonBase) return true;
				DependencyObject next = null;
				try { next = VisualTreeHelper.GetParent(d); } catch { }
				if (next == null) next = LogicalTreeHelper.GetParent(d);
				d = next;
			}
			return false;
		}

		private void OnCardDown(object s, MouseButtonEventArgs e)
		{
			if (resizing || chartGrid == null) return;
			if (FromButton(e.OriginalSource)) return;
			EnsureAbsolutePlacement();
			dragging = true; dragStart = e.GetPosition(chartGrid); dragOrigMargin = card.Margin;
			card.CaptureMouse(); e.Handled = true;
		}
		private void OnCardMove(object s, MouseEventArgs e)
		{
			if (!dragging) return;
			Point p = e.GetPosition(chartGrid);
			double nl = dragOrigMargin.Left + (p.X - dragStart.X), nt = dragOrigMargin.Top + (p.Y - dragStart.Y);
			double maxL = Math.Max(0, chartGrid.ActualWidth - card.ActualWidth), maxT = Math.Max(0, chartGrid.ActualHeight - card.ActualHeight);
			card.Margin = new Thickness(Math.Min(Math.Max(0, nl), maxL), Math.Min(Math.Max(0, nt), maxT), 0, 0);
			e.Handled = true;
		}
		private void OnCardUp(object s, MouseButtonEventArgs e)
		{
			if (!dragging) return;
			dragging = false; card.ReleaseMouseCapture(); PersistLayout(); e.Handled = true;
		}

		private void StartResize(int corner, MouseButtonEventArgs e)
		{
			if (chartGrid == null || card == null) return;
			EnsureAbsolutePlacement();
			resizing = true; resizeCorner = corner; resizeStart = e.GetPosition(chartGrid);
			resizeOrigW = card.ActualWidth; resizeOrigH = card.ActualHeight; resizeOrigMargin = card.Margin;
			Border b = e.Source as Border; if (b != null) { b.CaptureMouse(); b.Opacity = 0.95; }
			e.Handled = true;
		}
		private void OnResizeMove(object s, MouseEventArgs e)
		{
			if (!resizing || card == null || chartGrid == null) return;
			Point p = e.GetPosition(chartGrid);
			double dx = p.X - resizeStart.X, dy = p.Y - resizeStart.Y;
			double newW = resizeOrigW, newH = resizeOrigH, newL = resizeOrigMargin.Left, newT = resizeOrigMargin.Top;
			switch (resizeCorner)
			{
				case 0: newW = resizeOrigW - dx; newH = resizeOrigH - dy; newL += dx; newT += dy; break;
				case 1: newW = resizeOrigW + dx; newH = resizeOrigH - dy; newT += dy;             break;
				case 2: newW = resizeOrigW - dx; newH = resizeOrigH + dy; newL += dx;             break;
				case 3: newW = resizeOrigW + dx; newH = resizeOrigH + dy;                          break;
			}
			const double MIN_W = 240, MIN_H = 300;
			if (newW < MIN_W) { if (resizeCorner == 0 || resizeCorner == 2) newL -= (MIN_W - newW); newW = MIN_W; }
			if (newH < MIN_H) { if (resizeCorner == 0 || resizeCorner == 1) newT -= (MIN_H - newH); newH = MIN_H; }
			if (newL < 0) newL = 0; if (newT < 0) newT = 0;
			if (newL + newW > chartGrid.ActualWidth)  newW = chartGrid.ActualWidth  - newL;
			if (newT + newH > chartGrid.ActualHeight) newH = chartGrid.ActualHeight - newT;
			card.Width = newW; card.Height = newH; card.Margin = new Thickness(newL, newT, 0, 0);
			e.Handled = true;
		}
		private void OnResizeUp(object s, MouseButtonEventArgs e)
		{
			if (!resizing) return;
			resizing = false; Border b = s as Border; if (b != null) { b.ReleaseMouseCapture(); b.Opacity = 0.55; }
			PersistLayout(); e.Handled = true;
		}

		private void PersistLayout()
		{
			if (card == null || chartGrid == null) return;
			try
			{
				int w = (int)Math.Round(card.Width  > 0 ? card.Width  : card.ActualWidth);
				int h = (int)Math.Round(card.Height > 0 ? card.Height : card.ActualHeight);
				CardWidth  = Math.Max(240, Math.Min(1200, w));
				CardHeight = Math.Max(320, Math.Min(1600, h));
				CardCorner  = ESPolarisCorner.TopLeft;
				CardMarginX = (int)Math.Max(0, Math.Min(10000, Math.Round(card.Margin.Left)));
				CardMarginY = (int)Math.Max(0, Math.Min(10000, Math.Round(card.Margin.Top)));
			}
			catch { }
		}
		#endregion

		#region Properties - 1. Trade
		[NinjaScriptProperty] [Range(1, 100)] [Display(Name = "Contracts",    GroupName = "1. Trade", Order = 0)] public int  Contracts   { get; set; }
		[NinjaScriptProperty]                 [Display(Name = "Trade longs",  GroupName = "1. Trade", Order = 1)] public bool TradeLongs  { get; set; }
		[NinjaScriptProperty]                 [Display(Name = "Trade shorts", GroupName = "1. Trade", Order = 2)] public bool TradeShorts { get; set; }
		[NinjaScriptProperty] [Range(2, 200)] [Display(Name = "50 EMA period (star gate)",  GroupName = "1. Trade", Order = 3)] public int StarEmaPeriod { get; set; }
		[NinjaScriptProperty] [Range(2, 200)] [Display(Name = "Star trend EMA period (21=reversals, 50=continuation)", GroupName = "1. Trade", Order = 6)] public int StarTrendEmaPeriod { get; set; }
		[NinjaScriptProperty] [Display(Name = "Star: allow price-reclaim of trend EMA", GroupName = "1. Trade", Order = 7)] public bool StarUsePriceReclaim { get; set; }
		[NinjaScriptProperty] [Display(Name = "Continuation stars (pullback-and-resume)", GroupName = "1. Trade", Order = 8)] public bool UseContinuationStar { get; set; }
		[NinjaScriptProperty] [Range(1, 200)] [Display(Name = "Continuation cooldown (bars)", GroupName = "1. Trade", Order = 9)] public int ContinuationCooldownBars { get; set; }
		[NinjaScriptProperty] [Range(1, 200)] [Display(Name = "Continuation pullback band (ticks near EMA)", GroupName = "1. Trade", Order = 10)] public int ContinuationPullbackTicks { get; set; }
		[NinjaScriptProperty] [Range(0, 500)] [Display(Name = "Min bars between ANY stars (0=off)", GroupName = "1. Trade", Order = 11)] public int MinBarsBetweenStars { get; set; }
		[NinjaScriptProperty] [Range(1, 20)]  [Display(Name = "EMA slope lookback (bars)",  GroupName = "1. Trade", Order = 4)] public int StarSlopeLookback { get; set; }
		[NinjaScriptProperty] [Range(1, 200)] [Display(Name = "Star cooldown (bars/level/side)", GroupName = "1. Trade", Order = 5)] public int StarCooldownBars { get; set; }
		#endregion

		#region Properties - 2. Order Management
		[NinjaScriptProperty] [Display(Name = "Order mode", GroupName = "2. Order Management", Order = 0)] public ESStarOrderMode OrderMode { get; set; }
		[NinjaScriptProperty] [TypeConverter(typeof(FriendlyAtmConverter))] [Display(Name = "ATM template (Atm mode)", GroupName = "2. Order Management", Order = 1)] public string AtmTemplate { get; set; }
		[NinjaScriptProperty] [Range(1, 4000)] [Display(Name = "Stop ticks (FixedTicks)",   GroupName = "2. Order Management", Order = 2)] public int  StopTicks   { get; set; }
		[NinjaScriptProperty] [Range(1, 8000)] [Display(Name = "Target ticks (FixedTicks)", GroupName = "2. Order Management", Order = 3)] public int  TargetTicks { get; set; }
		[NinjaScriptProperty]                  [Display(Name = "Enable break-even (FixedTicks)",       GroupName = "2. Order Management", Order = 4)] public bool EnableBreakEven { get; set; }
		[NinjaScriptProperty] [Range(1, 4000)] [Display(Name = "Break-even trigger (ticks in profit)", GroupName = "2. Order Management", Order = 5)] public int  BreakEvenTriggerTicks { get; set; }
		[NinjaScriptProperty] [Range(0, 1000)] [Display(Name = "Break-even offset (ticks past entry)", GroupName = "2. Order Management", Order = 6)] public int  BreakEvenOffsetTicks { get; set; }
		#endregion

		#region Properties - 3. Session Parameters
		[NinjaScriptProperty] [Display(Name = "Enable TF 1", GroupName = "3. Session Parameters", Order = 0)] public bool EnableTF1 { get; set; }
		[NinjaScriptProperty] [PropertyEditor("NinjaTrader.Gui.Tools.TimeEditorKey")] [Display(Name = "Start Time 1", GroupName = "3. Session Parameters", Order = 1)] public DateTime StartTime1 { get; set; }
		[NinjaScriptProperty] [PropertyEditor("NinjaTrader.Gui.Tools.TimeEditorKey")] [Display(Name = "End Time 1",   GroupName = "3. Session Parameters", Order = 2)] public DateTime EndTime1   { get; set; }
		[NinjaScriptProperty] [Display(Name = "Flatten at End TF 1", GroupName = "3. Session Parameters", Order = 3)] public bool FlattenTF1 { get; set; }
		[NinjaScriptProperty] [Display(Name = "Enable TF 2", GroupName = "3. Session Parameters", Order = 4)] public bool EnableTF2 { get; set; }
		[NinjaScriptProperty] [PropertyEditor("NinjaTrader.Gui.Tools.TimeEditorKey")] [Display(Name = "Start Time 2", GroupName = "3. Session Parameters", Order = 5)] public DateTime StartTime2 { get; set; }
		[NinjaScriptProperty] [PropertyEditor("NinjaTrader.Gui.Tools.TimeEditorKey")] [Display(Name = "End Time 2",   GroupName = "3. Session Parameters", Order = 6)] public DateTime EndTime2   { get; set; }
		[NinjaScriptProperty] [Display(Name = "Flatten at End TF 2", GroupName = "3. Session Parameters", Order = 7)] public bool FlattenTF2 { get; set; }
		[NinjaScriptProperty] [Display(Name = "Enable TF 3", GroupName = "3. Session Parameters", Order = 8)] public bool EnableTF3 { get; set; }
		[NinjaScriptProperty] [PropertyEditor("NinjaTrader.Gui.Tools.TimeEditorKey")] [Display(Name = "Start Time 3", GroupName = "3. Session Parameters", Order = 9)]  public DateTime StartTime3 { get; set; }
		[NinjaScriptProperty] [PropertyEditor("NinjaTrader.Gui.Tools.TimeEditorKey")] [Display(Name = "End Time 3",   GroupName = "3. Session Parameters", Order = 10)] public DateTime EndTime3   { get; set; }
		[NinjaScriptProperty] [Display(Name = "Flatten at End TF 3", GroupName = "3. Session Parameters", Order = 11)] public bool FlattenTF3 { get; set; }
		[NinjaScriptProperty] [Display(Name = "Enable Skip Window", GroupName = "3. Session Parameters", Order = 12)] public bool EnableSkipWindow { get; set; }
		[NinjaScriptProperty] [PropertyEditor("NinjaTrader.Gui.Tools.TimeEditorKey")] [Display(Name = "Skip Start Time", GroupName = "3. Session Parameters", Order = 13)] public DateTime SkipStartTime { get; set; }
		[NinjaScriptProperty] [PropertyEditor("NinjaTrader.Gui.Tools.TimeEditorKey")] [Display(Name = "Skip End Time",   GroupName = "3. Session Parameters", Order = 14)] public DateTime SkipEndTime   { get; set; }
		#endregion

		#region Properties - 4. Levels
		[NinjaScriptProperty] [Display(Name = "YM levels CSV path", GroupName = "4. Levels", Order = 0)] public string YmLevelsCsvPath { get; set; }
		[NinjaScriptProperty] [Range(5, 600)] [Display(Name = "ES levels refresh (sec)", GroupName = "4. Levels", Order = 1)] public int LevelRefreshSeconds { get; set; }
		[NinjaScriptProperty] [Range(0, 4000)] [Display(Name = "Min room to next level (ticks, 0=off)", GroupName = "4. Levels", Order = 2)] public int SRMinRoomTicks { get; set; }
		[NinjaScriptProperty] [Display(Name = "Use Order-Flow Filter (block \u2605 when flow contradicts)", GroupName = "4. Levels", Order = 3)] public bool UseFlowFilter { get; set; }
		[NinjaScriptProperty] [Range(1, 5)] [Display(Name = "Flow Contra Votes to Block \u2605 (1=strict\u20265=lax)", GroupName = "4. Levels", Order = 4)] public int FlowContraVotesToBlock { get; set; }
		[NinjaScriptProperty] [Display(Name = "Flow: use Absorption",      GroupName = "4. Levels", Order = 5)] public bool FlowUseAbsorption { get; set; }
		[NinjaScriptProperty] [Display(Name = "Flow: use Exhaustion",      GroupName = "4. Levels", Order = 6)] public bool FlowUseExhaustion { get; set; }
		[NinjaScriptProperty] [Display(Name = "Flow: use Stopping Volume", GroupName = "4. Levels", Order = 7)] public bool FlowUseStoppingVol { get; set; }
		[NinjaScriptProperty] [Display(Name = "Flow: use Fading Momentum", GroupName = "4. Levels", Order = 8)] public bool FlowUseFadingMomentum { get; set; }
		[NinjaScriptProperty] [Display(Name = "Flow: use Divergence",      GroupName = "4. Levels", Order = 9)] public bool FlowUseDivergence { get; set; }
		[NinjaScriptProperty] [Range(0, 500)] [Display(Name = "Flow: skip filter if EMA slope >= (ticks, 0=always)", GroupName = "4. Levels", Order = 10)] public int FlowTrendBypassTicks { get; set; }
		#endregion

		#region Properties - 5. Risk Management
		[NinjaScriptProperty] [Display(Name = "Start Fresh On Enable", GroupName = "5. Risk Management", Order = 0)] public bool StartFreshOnEnable { get; set; }
		[NinjaScriptProperty] [Display(Name = "Use Unrealized PNL",    GroupName = "5. Risk Management", Order = 1)] public bool UseUnrealizedPnl   { get; set; }
		[NinjaScriptProperty] [Display(Name = "Enable Daily Profit Target", GroupName = "5. Risk Management", Order = 2)] public bool EnableDailyProfitTarget { get; set; }
		[NinjaScriptProperty] [Range(1, 1000000)] [Display(Name = "Daily Profit Target ($)", GroupName = "5. Risk Management", Order = 3)] public double DailyProfitTarget { get; set; }
		[NinjaScriptProperty] [Display(Name = "Enable Daily Loss Limit", GroupName = "5. Risk Management", Order = 4)] public bool EnableDailyLossLimit { get; set; }
		[NinjaScriptProperty] [Range(1, 1000000)] [Display(Name = "Daily Loss Limit ($)", GroupName = "5. Risk Management", Order = 5)] public double DailyLossLimit { get; set; }
		#endregion

		#region Properties - 6. Performance / Historical
		[NinjaScriptProperty] [Display(Name = "Historical processing mode", GroupName = "6. Performance / Historical", Order = 0)] public ESStarHistMode HistoricalMode { get; set; }
		[NinjaScriptProperty] [Display(Name = "Enable debug",               GroupName = "6. Performance / Historical", Order = 1)] public bool EnableDebug { get; set; }
		#endregion

		#region Properties - 7. Dashboard
		[NinjaScriptProperty] [Display(Name = "Show dashboard card", GroupName = "7. Dashboard", Order = 0)] public bool ShowDashboard { get; set; }
		[Display(Name = "Card corner", GroupName = "7. Dashboard", Order = 1)] public ESPolarisCorner CardCorner { get; set; }
		[NinjaScriptProperty] [Range(0, 10000)] [Display(Name = "Card margin X", GroupName = "7. Dashboard", Order = 2)] public int CardMarginX { get; set; }
		[NinjaScriptProperty] [Range(0, 10000)] [Display(Name = "Card margin Y", GroupName = "7. Dashboard", Order = 3)] public int CardMarginY { get; set; }
		[NinjaScriptProperty] [Range(240, 1200)] [Display(Name = "Card width", GroupName = "7. Dashboard", Order = 4)] public int CardWidth { get; set; }
		[NinjaScriptProperty] [Range(320, 1600)] [Display(Name = "Card height", GroupName = "7. Dashboard", Order = 5)] public int CardHeight { get; set; }
		#endregion

		public class FriendlyAtmConverter : TypeConverter
		{
			public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext context)
			{
				List<string> values = new List<string>();
				string atmDir = System.IO.Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "templates", "AtmStrategy");
				if (System.IO.Directory.Exists(atmDir))
					foreach (string atm in System.IO.Directory.GetFiles(atmDir, "*.xml"))
						values.Add(System.IO.Path.GetFileNameWithoutExtension(atm));
				return new StandardValuesCollection(values);
			}
			public override object ConvertFrom(ITypeDescriptorContext context, CultureInfo culture, object value) => value?.ToString() ?? string.Empty;
			public override object ConvertTo(ITypeDescriptorContext context, CultureInfo culture, object value, Type destinationType) => value;
			public override bool CanConvertFrom(ITypeDescriptorContext context, Type sourceType) => true;
			public override bool CanConvertTo(ITypeDescriptorContext context, Type destinationType) => true;
			public override bool GetStandardValuesExclusive(ITypeDescriptorContext context) => true;
			public override bool GetStandardValuesSupported(ITypeDescriptorContext context) => true;
		}
	}
}
