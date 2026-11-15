#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
using NinjaTrader.NinjaScript.Indicators;
using SharpDX;
using SharpDX.Direct2D1;
#endregion

// â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
// NinjaGexSignal â€” confluence scorer on top of the Ninja-nator gamma relay
//
// Combines, once per closed bar:
//   1. Gamma regime      spot vs Zero Gamma (long-gamma range / short-gamma trend)
//   2. Wall proximity    price at Call Wall / Put Wall / Long / Short Gamma line
//   3. Structure         1DTE-or-0DTE wall agrees with the 90-day OI wall, and
//                        the ETF overlay (ES_SPY / NQ_QQQ) wall sits at the same level
//   4. Price action      rejection wick or acceptance close at the level, VWAP side
//   5. Volume / flow     volume spike vs 20-bar average, bar delta (live only)
//   6. Cheat-sheet rules Major+/Major- (net GEX sign) x Long/Short Gamma touch
//
// Output: an on-chart panel (regime, nearest wall, factor breakdown, 0-100
// score, direction), an arrow + NT8 Alert when the score crosses the
// threshold at a level. No orders are placed. Same relay auth as NinjaGex
// (user's own Ninja-nator key, POST https://zbyte.ai/gex2). Same rules as the
// sibling files: no external JSON library, SharpDX only in OnRender, no
// StrokeStyle in OnRender, no [NinjaScriptProperty] on custom enums.
// Enums NinjaGexSymbol / NinjaGexAggregation come from NinjaGexTracks.cs.
// â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

namespace NinjaTrader.NinjaScript.Indicators
{
	public class NinjaGexSignal : Indicator
	{
		private class Snap
		{
			public Dictionary<string, double> Majors = new Dictionary<string, double>();
			public double NetGexVol;
			public DateTime FetchedUtc;
			// [strike, vol gex, oi gex] per strike (live snapshots only)
			public List<double[]> Profile = new List<double[]>();
			public double EmUpper = double.NaN, EmLower = double.NaN, MaxPain = double.NaN;
		}

		private class Factor
		{
			public string Name;
			public double Long;     // 0..1 support for a long
			public double Short;    // 0..1 support for a short
			public string Note;
		}

		private class Level
		{
			public string Name;
			public double Price;
			public bool IsSupport;   // put-side level
			public bool IsWall;      // call/put wall (vs gamma line)
			public bool IsLongGamma; // cheat-sheet: cyan line
			public bool IsShortGamma;// cheat-sheet: blue line
			public double Strength = 1.0;   // walls 1.0, G1 0.8, G2 0.65, G3 0.5, EM 0.7
		}

		private class HistPt { public DateTime Time; public Snap S; }

		private class Setup
		{
			public string Dir;               // LONG / SHORT
			public int SignalBar, TriggerBar = -1, ExitBar = -1;
			public DateTime SignalTime, TriggerTime, ExitTime;
			public string LevelName, TargetName, Reason = "";
			public string Why = "", TargetWhy = "";   // human-readable: top factors behind the signal / how the target was chosen
			public double Score, SigHigh, SigLow, Trigger, Stop, StopInit, Target, Entry, Exit, MaxFav, MaxAdv;
			public bool Active, Closed;
			public bool LiveSignal;         // signal bar scored in real time (false = created from a history replay after a reload)
			public bool Shadow;              // diamond-entry twin: entered at the signal bar close, tracked to a separate file
			public string TargetLevel = "";  // relay level the target follows (Zero Gamma, walls, gamma lines, EM); "" = static target
			public double TargetOffset;      // front-run distance kept between the target and its level
			public int Seq;                  // unique per setup; the strategy uses it to tell setups apart
			public int Qty = 1;
			public double Risk { get { return Math.Abs(Entry - StopInit); } }
			public double Pts(double px) { return Dir == "LONG" ? px - Entry : Entry - px; }
		}
		private Setup setup;                                   // pending or active
		private Setup shadow;                                  // diamond-entry twin of the current setup
		private int setupSeq;
		private readonly List<double[]> migHist = new List<double[]>();   // per scored bar: time ticks, Zero Gamma, call wall, put wall
		private string migAdvice = "";                                    // panel advice line from the wall-migration rule
		private readonly List<Setup> closedSetups = new List<Setup>();
		private double dayPts, dayDollars; private int dayTrades, dayWins;
		private DateTime sessStartTime = DateTime.MinValue;   // first bar time of the current session (for the CSV tally reload)
		private string tradeFile;
		private const string BuiltInKey = "";   // paste YOUR Ninja-nator key in the indicator properties (01. Account & Data)
		// project folder (the NT8 Documents\export folder was used until 2026-09-15)
		private static string DefaultExportDir { get { return System.IO.Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "export"); } }   // Documents\NinjaTrader 8\export

		// â”€â”€ relay state â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
		private Snap primary, overlay, structural;
		// per-minute archive (chart-local time, ascending) used to score historical bars
		private readonly List<HistPt> histPrimary = new List<HistPt>(), histOverlay = new List<HistPt>(), histStructural = new List<HistPt>();
		private static readonly Regex PointRx = new Regex("\\{[^{}]*\"t\"\\s*:\\s*(\\d+)[^{}]*\\}", RegexOptions.Compiled);
		private static readonly Regex FieldRx = new Regex(
			"\"(zg|zgv|mpv|mnv|mpo|mno|mlg|msg|spot|net_gex_vol)\"\\s*:\\s*(-?[0-9.]+|null)", RegexOptions.Compiled);
		private readonly object dataLock = new object();
		private DispatcherTimer timer;
		private System.Timers.Timer plainTimer;
		private bool fetchInFlight;
		private string upstreamSymbol, overlaySymbol, snaptypeToken;
		private string lastProblem = "";

		private static readonly Regex MajorRx = new Regex(
			"\"(spot|zero_gamma|zero_gamma_vol|major_pos_vol|major_neg_vol|major_pos_oi|major_neg_oi|major_long_gamma|major_short_gamma)\"\\s*:\\s*(-?[0-9.]+|null)",
			RegexOptions.Compiled);
		private static readonly Regex NetRx = new Regex(
			"\"net_gex_vol\"\\s*:\\s*(-?[0-9.]+|null)", RegexOptions.Compiled);
		private static readonly Regex ProfileRx = new Regex(
			"\"profile\"\\s*:\\s*\\[\\[(.*?)\\]\\]", RegexOptions.Compiled | RegexOptions.Singleline);
		private static readonly Regex DerivedRx = new Regex(
			"\"(max_pain|em_upper|em_lower)\"\\s*:\\s*(-?[0-9.]+|null)", RegexOptions.Compiled);

		// â”€â”€ bar state â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
		private ATR atr;
		private SMA volSma;
		private SMA atrSma;                 // long average of ATR: how hot is the tape compared with its own normal
		private double volRatio = 1;        // ATR now / its 100-bar average
		private bool UsePtz { get { return LevelSet != NinjaGexSigLevelSet.GexOnly; } }
		private string FileTag { get { return LevelSet == NinjaGexSigLevelSet.PtzOnly ? "_PTZ" : ""; } }   // a PTZ-only chart writes its own files
		private double vwapPv, vwapV, sessionVwap;
		private double liveDelta, lastBarDelta;
		// per-price session tape: price -> [bid-hit volume, ask-hit volume]  (live only)
		private readonly Dictionary<double, double[]> tape = new Dictionary<double, double[]>();
		private readonly object tapeLock = new object();
		// big prints: [price, signed size, bar index]  (live only, last 500)
		private readonly List<double[]> bigPrints = new List<double[]>();
		private int lastSignalBar = -1000;
		private string lastSignalDir = "";   // cooldown applies to a repeat in this direction only
		private string lastSignalText = "";
		private List<Factor> lastFactors = new List<Factor>();
		private double lastScoreLong, lastScoreShort;
		private string lastDirection = "None";
		private Level lastLevel;
		private string lastRegime = "";
		private DateTime lastStatusWrite = DateTime.MinValue;
		private DateTime aliveAt = DateTime.MinValue;   // live status file throttle
		private string lastTarget = "";
		private double locPos = 0.5, locFromHigh, locFromLow;
		private double lastTradePx;     // newest Last print (OnMarketData) / bar close, for render-time P&L
		private int lastBarIdx;         // newest CurrentBar seen on the data thread
		private double sessOpen, sessHigh, sessLow, prevHigh = double.NaN, prevLow = double.NaN, prevClose = double.NaN, lastClose;
		// intraday session ranges (chart time = ET): Asia 18:00-03:00, London 03:00-09:30, NY 09:30-16:00 (previous NY session is the level)
		private double asiaH, asiaL, lonH, lonL, nyH, nyL, nyCurH, nyCurL;
		private int lastSessWin = -1;
		private List<KeyValuePair<string, double>> lastSessLevels = new List<KeyValuePair<string, double>>();
		private DateTime lastExportedBar = DateTime.MinValue;
		private readonly HashSet<DateTime> exportedTimes = new HashSet<DateTime>();
		// draggable panel state (device pixels, chart-panel relative)
		private RectangleF panelRect;
		private bool panelDragging, panelCollapsed, mouseHooked;
		private float dragDx, dragDy;
		private bool lastMagnet;
		private List<double[]> lastGLevels = new List<double[]>();
		// PropTraderz daily plan levels (recorder\ptz_plan.py writes <export>\ptz_levels.csv)
		private class PtzLevel { public string Date, Name, Kind; public double P1, P2; public double Mid { get { return P2 > 0 ? (P1 + P2) / 2 : P1; } } }
		private List<PtzLevel> ptzAll = new List<PtzLevel>();
		private List<PtzLevel> lastPtz = new List<PtzLevel>();
		private DateTime ptzFileTime = DateTime.MinValue;
		// news lock: export/news.csv (chart time, written by recorder/trade_watch.py from the ForexFactory calendar)
		private class NewsEvt { public DateTime T; public string Impact, Title; }
		private List<NewsEvt> newsAll = new List<NewsEvt>();
		private DateTime newsFileTime = DateTime.MinValue;
		private string newsLockText = "";
		private Series<double> scoreSeries;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = "Confluence score + setup alerts from Ninja-nator gamma levels, price action, volume and the cheat-sheet rules.";
				Name = "NinjaGexSignal";
				Calculate = Calculate.OnEachTick;   // scoring stays on closed bars; triggers/exits are tick-level (set OnBarClose to get the old behaviour)
				IsOverlay = true;
				DisplayInDataBox = false;
				PaintPriceMarkers = false;
				IsSuspendedWhileInactive = false;

				RelayUrl = "https://zbyte.ai/gex2";
				LicenseKey = BuiltInKey;   // default = the user's own relay key, so a fresh instance works without pasting
				SymbolChoice = NinjaGexSymbol.Auto;
				Aggregation = NinjaGexAggregation.ZeroDTE;
				UseEtfOverlay = true;
				UseStructural = true;
				UseOverlayLevels = true;
				RefreshSeconds = 5;
				ZgSource = NinjaGexSigZgSource.OpenInterestPlusVolume;
				HistoryDays = 1;

				LevelToleranceTicks = 8;
				LevelToleranceAtr = 0.25;
				WallAgreeAtr = 0.75;
				StopBufferAtr = 0.05;
				AtrPeriod = 14;
				WallAgreePoints = 6;
				VolumeSpikeMult = 1.5;
				WickRatio = 0.5;
				GLevelCount = 3;
				ShowGLevels = true;
				ClrGLevel = System.Windows.Media.Brushes.Gold;
				MagnetTicks = 6;
				AutoGoldFull = true;
				UsePtzLevels = true;
				LevelSet = NinjaGexSigLevelSet.GexOnly;
				HighVolRatio = 1.5;
				ShowPtzLevels = true;
				UseSessionLevels = true;
				ShowSessionLevels = true;
				ForceOnEachTick = true;
				ShowRLines = true;
				ShowSetups = true;
				LevelByTouch = true;
				PtzEntries = true;

				WRegime = 20; WWall = 20; WStructure = 10; WPriceAction = 20; WVolume = 10; WCheatSheet = 20; WFlow = 20;
				FlowMinVolume = 50;
				WLocation = 15;
				LocationFilter = true;
				UsePriorDay = true;
				LocationLookbackBars = 40;

				ShowArrows = true;
				AlertsOn = true;
				SignalThreshold = 50;
				CooldownBars = 5;
				ClrLongSignal = System.Windows.Media.Brushes.Lime;
				ClrShortSignal = System.Windows.Media.Brushes.Red;

				ShowPanel = true;
				PanelCorner = NinjaGexCorner.TopRight;
				PanelX = 10;
				PanelY = 30;
				PanelTextSize = 12;
				PanelDraggable = true;

				ExportEnabled = true;
				ExportFolder = "";

				EntryOnBreak = true;
				RiskDollars = 200;
				ZgRule = NinjaGexSigZgRule.Flip;
				PreferZgTargetLongGamma = true;
				UseReclaimRule = false;
				UseRoomFilter = true;
				UseWallMigration = true; MigrationAtr = 1.0; MigrationMinutes = 60;
				TargetOffsetTicks = 2; TargetOffsetAtr = 0.05; FollowTargetLevel = true;
				LockAtR = 1.5; LockToR = 0.5;
				RoomMinR = 0.5;   // v43: tested on 86 recorded trades - 0.8R blocked winners, 0.5R blocks only the genuinely no-room ones
				TrackDiamondEntries = true;
				UseSessionFilter = true;
				UseNewsLock = true;
				NewsLockMinutes = 10;
				SessionStart = 930;
				SessionEnd = 1600;
				TriggerBars = 3;
				StopBufferTicks = 2;
				MinRewardR = 1.0;
				RiskRewardIfNoLevel = 2.0;
				StopMode = NinjaGexSigStopMode.BreakevenAt1R;
				BreakevenPlusTicks = 4;
				TrailAtr = 1.0;
				RequireCandleAgreement = true;
				MaxBarsInTrade = 20;
				MaxRewardR = 3.0;
				BigPrintSize = 25;
			}
			else if (State == State.Configure)
			{
				// chart instances saved with Calculate=OnBarClose silently lose the tick engine (no intra-bar triggers, no live status
				// file). Force tick mode here so the saved instance property cannot switch it off.
				if (ForceOnEachTick) Calculate = Calculate.OnEachTick;
				scoreSeries = new Series<double>(this, MaximumBarsLookBack.Infinite);
				// Plots for strategies / execution tools. Transparent so they do not clutter the price panel;
				// Strategy Builder and most auto-traders can still read them by name.
				AddPlot(System.Windows.Media.Brushes.Transparent, "Signal");      // +1 long entry bar, -1 short entry bar, else 0
				AddPlot(System.Windows.Media.Brushes.Transparent, "SetupDir");    // +1 / -1 while a setup is pending or a trade is open
				AddPlot(System.Windows.Media.Brushes.Transparent, "StopPrice");   // current stop of the pending/active setup (0 = none)
				AddPlot(System.Windows.Media.Brushes.Transparent, "TargetPrice"); // current target (0 = none)
				AddPlot(System.Windows.Media.Brushes.Transparent, "NetScore");    // score_long - score_short
			}
			else if (State == State.DataLoaded)
			{
				atr = ATR(Math.Max(2, AtrPeriod));
				volSma = SMA(Volume, 20);
				atrSma = SMA(atr, 100);
				upstreamSymbol = ResolveSymbol();
				overlaySymbol = OverlayFor(upstreamSymbol);
				snaptypeToken = Aggregation == NinjaGexAggregation.OneDTE ? "one"
				              : Aggregation == NinjaGexAggregation.Full90d ? "full" : "0dte";
				// Gold: the GLD 0DTE chain is thin/empty most days (video: turn 0DTE off on gold), use the 90-day chain.
				if (AutoGoldFull && upstreamSymbol == "GC" && snaptypeToken == "0dte") snaptypeToken = "full";
				try { LoadPtz(); } catch (Exception ex) { lastProblem = "ptz: " + ex.Message; }
				try { LoadNews(); } catch (Exception ex) { lastProblem = "news: " + ex.Message; }
				Print(string.Format("NinjaGexSignal: {0} ({1}) overlay={2} structural={3} via {4}",
					upstreamSymbol, snaptypeToken, overlaySymbol ?? "none", UseStructural, RelayUrl));
				// Startup marker: proves the export folder is writable and which
				// build is running, independent of whether any bar gets scored.
				if (ExportEnabled)
				{
					try
					{
						string dirPath = string.IsNullOrWhiteSpace(ExportFolder) ? DefaultExportDir : ExportFolder;
						System.IO.Directory.CreateDirectory(dirPath);
						System.IO.File.AppendAllText(System.IO.Path.Combine(dirPath, "ngs_" + upstreamSymbol + "_started.txt"),
							DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " build v44 2026-09-18  bars=" + (BarsArray != null && BarsArray.Length > 0 && BarsArray[0] != null ? BarsArray[0].Count : -1) + "\r\n");
					}
					catch (Exception ex) { lastProblem = "export folder: " + ex.Message; }
				}
				// Blocking history load so historical bars can be scored. A few
				// requests, 15 s timeout each; failures just leave history empty.
				if (!string.IsNullOrWhiteSpace(LicenseKey))
				{
					try { LoadHistory(); }
					catch (Exception ex) { lastProblem = "history: " + ex.Message; }
				}
			}
			else if (State == State.Realtime)
			{
				try { LoadDayTallyFromCsv(); } catch (Exception ex) { lastProblem = "tally reload: " + ex.Message; }
			}
			else if (State == State.Historical)
			{
				if (ChartControl == null)
				{
					// hosted by a strategy (no chart control): poll with a plain timer instead of the dispatcher one
					int secs = Math.Max(10, RefreshSeconds)   /* relay load: levels move slowly, 10 s is plenty */;
					plainTimer = new System.Timers.Timer(secs * 1000 + new Random().Next(0, 900));
					plainTimer.Elapsed += (s2, e2) => { try { if (State != State.Terminated) StartFetch(); } catch (Exception) { } };
					plainTimer.AutoReset = true;
					plainTimer.Start();
					StartFetch();
					return;
				}
				ChartControl.Dispatcher.InvokeAsync(() =>
				{
					int seconds = Math.Max(10, RefreshSeconds)   /* relay load: levels move slowly, 10 s is plenty */;
					timer = new DispatcherTimer();
					timer.Interval = TimeSpan.FromMilliseconds(seconds * 1000 + new Random().Next(0, 900));
					timer.Tick += (s, e) =>
					{
						try
						{
							if (State == State.Terminated || ChartControl == null) { try { timer.Stop(); } catch (Exception) { } return; }
							StartFetch();
						}
						catch (Exception) { }
					};
					timer.Start();
					StartFetch();
					if (PanelDraggable && !mouseHooked)
					{
						ChartControl.MouseLeftButtonDown += OnPanelMouseDown;
						ChartControl.MouseMove += OnPanelMouseMove;
						ChartControl.MouseLeftButtonUp += OnPanelMouseUp;
						mouseHooked = true;
					}
				});
			}
			else if (State == State.Terminated)
			{
				try { if (timer != null) timer.Stop(); } catch (Exception) { }
				timer = null;
				try { if (plainTimer != null) { plainTimer.Stop(); plainTimer.Dispose(); } } catch (Exception) { }
				plainTimer = null;
				if (mouseHooked && ChartControl != null)
				{
					try
					{
						ChartControl.Dispatcher.InvokeAsync(() =>
						{
							ChartControl.MouseLeftButtonDown -= OnPanelMouseDown;
							ChartControl.MouseMove -= OnPanelMouseMove;
							ChartControl.MouseLeftButtonUp -= OnPanelMouseUp;
						});
					}
					catch (Exception) { }
					mouseHooked = false;
				}
			}
		}

		private string ResolveSymbol()
		{
			if (SymbolChoice != NinjaGexSymbol.Auto) return SymbolChoice.ToString();
			string root = Instrument != null && Instrument.MasterInstrument != null ? Instrument.MasterInstrument.Name.ToUpperInvariant() : "";
			if (root.StartsWith("MES") || root.StartsWith("ES")) return "ES";
			if (root.StartsWith("MNQ") || root.StartsWith("NQ")) return "NQ";
			if (root.StartsWith("MGC") || root.StartsWith("GC")) return "GC";
			if (root.StartsWith("MYM") || root.StartsWith("YM")) return "YM";
			if (root.StartsWith("M2K") || root.StartsWith("RTY")) return "RTY";
			return root;
		}

		// ETF-based second opinion for each futures root (relay pairs).
		private static string OverlayFor(string sym)
		{
			switch (sym)
			{
				case "ES": return "ES_SPY";
				case "NQ": return "NQ_QQQ";
				case "YM": return "YM_DIA";
				case "RTY": return "RTY_IWM";
				case "GC": return "GC_GLD";
				default: return null;
			}
		}

		// PropTraderz plan file: date,symbol,name,price1,price2,kind,... (kind: long/short/pivot_bull/pivot_bear/strength/weakness)
		private void LoadPtz()
		{
			if (!UsePtz || upstreamSymbol == null) return;
			string dirPath = string.IsNullOrWhiteSpace(ExportFolder) ? DefaultExportDir : ExportFolder;
			string file = System.IO.Path.Combine(dirPath, "ptz_levels.csv");
			if (!System.IO.File.Exists(file)) return;
			DateTime mt = System.IO.File.GetLastWriteTime(file);
			if (mt == ptzFileTime) return;
			var rows = new List<PtzLevel>();
			var inv = CultureInfo.InvariantCulture;
			foreach (string line in System.IO.File.ReadAllLines(file))
			{
				string[] c = line.Split(',');
				if (c.Length < 6 || c[1] != upstreamSymbol) continue;
				double p1, p2;
				if (!double.TryParse(c[3], NumberStyles.Any, inv, out p1)) continue;
				if (!double.TryParse(c[4], NumberStyles.Any, inv, out p2)) p2 = 0;
				string kind = c[5];
				string label = kind == "long" ? "PTZ Long" : kind == "short" ? "PTZ Short" : kind == "pivot_bull" ? "PTZ Bull Pivot"
				             : kind == "pivot_bear" ? "PTZ Bear Pivot" : kind == "strength" ? "PTZ Strength" : kind == "weakness" ? "PTZ Weakness" : "PTZ " + kind;
				rows.Add(new PtzLevel { Date = c[0], Name = label, Kind = kind, P1 = p1, P2 = p2 });
			}
			lock (dataLock) { ptzAll = rows; ptzFileTime = mt; }
		}

		// â”€â”€ fetching â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
		private void LoadNews()
		{
			if (!UseNewsLock) return;
			string dirPath = string.IsNullOrWhiteSpace(ExportFolder) ? DefaultExportDir : ExportFolder;
			string file = System.IO.Path.Combine(dirPath, "news.csv");
			if (!System.IO.File.Exists(file)) return;
			DateTime mt = System.IO.File.GetLastWriteTime(file);
			if (mt == newsFileTime) return;
			var rows = new List<NewsEvt>();
			foreach (string line in System.IO.File.ReadAllLines(file))
			{
				string[] c = line.Split(',');
				DateTime t;
				if (c.Length < 3 || !DateTime.TryParseExact(c[0], "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out t)) continue;
				rows.Add(new NewsEvt { T = t, Impact = c[1], Title = c[2] });
			}
			lock (dataLock) { newsAll = rows; newsFileTime = mt; }
		}

		private void StartFetch()
		{
			if (fetchInFlight) return;
			fetchInFlight = true;
			Task.Run(() =>
			{
				try
				{
					try { LoadPtz(); } catch (Exception) { }
					try { LoadNews(); } catch (Exception) { }
					if (string.IsNullOrWhiteSpace(LicenseKey)) { lastProblem = "license key is empty -- set it in the indicator properties"; return; }
					Snap p = Fetch(upstreamSymbol, snaptypeToken);
					Snap o = UseEtfOverlay && overlaySymbol != null ? Fetch(overlaySymbol, snaptypeToken) : null;
					Snap f = UseStructural && snaptypeToken != "full" ? Fetch(upstreamSymbol, "full") : null;
					lock (dataLock)
					{
						if (p != null) primary = p;
						if (o != null) overlay = o;
						if (f != null) structural = f;
						else if (snaptypeToken == "full" && p != null) structural = p;
					}
					if (p != null) lastProblem = "";
				}
				catch (Exception ex) { lastProblem = ex.Message; }
				finally { fetchInFlight = false; try { if (ChartControl != null) ForceRefresh(); } catch (Exception) { } }
			});
		}

		private Snap Fetch(string symbol, string snaptype)
		{
			string body = PostJson("{\"key\":\"" + JsonEscape(LicenseKey) + "\",\"symbol\":\"" + symbol +
				"\",\"mode\":\"snapshot\",\"snaptype\":\"" + snaptype + "\",\"greek\":\"gex\"}");
			if (string.IsNullOrEmpty(body) || body.IndexOf("\"majors\"", StringComparison.Ordinal) < 0) return null;
			var s = new Snap { FetchedUtc = DateTime.UtcNow };
			foreach (Match m in MajorRx.Matches(body))
			{
				if (m.Groups[2].Value == "null") continue;
				double v;
				if (double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
					s.Majors[m.Groups[1].Value] = v;
			}
			Match n = NetRx.Match(body);
			if (n.Success && n.Groups[1].Value != "null")
				double.TryParse(n.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out s.NetGexVol);
			Match pm = ProfileRx.Match(body);
			if (pm.Success)
			{
				foreach (string entry in pm.Groups[1].Value.Split(new[] { "],[" }, StringSplitOptions.None))
				{
					string[] cols = entry.Split(',');
					if (cols.Length < 3) continue;
					double k, v, oi;
					if (double.TryParse(cols[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out k)
						&& double.TryParse(cols[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v))
					{
						if (!double.TryParse(cols[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out oi)) oi = 0;
						s.Profile.Add(new[] { k, v, oi });
					}
				}
			}
			foreach (Match m in DerivedRx.Matches(body))
			{
				if (m.Groups[2].Value == "null") continue;
				double v;
				if (!double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) continue;
				if (m.Groups[1].Value == "max_pain") s.MaxPain = v;
				else if (m.Groups[1].Value == "em_upper") s.EmUpper = v;
				else s.EmLower = v;
			}
			return s;
		}

		// G-levels (video: "G1..G3"): strikes inside the expected-move range with
		// the largest gamma weight (|vol gex| + |oi gex|), strongest first.
		private static List<double[]> GLevels(Snap s, double spot, double atr, int count)
		{
			var outList = new List<double[]>();   // [strike, weight]
			if (s == null || s.Profile.Count == 0) return outList;
			double lo = !double.IsNaN(s.EmLower) ? s.EmLower : spot - 3 * atr;
			double hi = !double.IsNaN(s.EmUpper) ? s.EmUpper : spot + 3 * atr;
			if (hi - lo < atr) { lo = spot - 3 * atr; hi = spot + 3 * atr; }   // EM collapses after the close
			double maxOi = 0, maxVol = 0;
			foreach (double[] r in s.Profile) { maxVol = Math.Max(maxVol, Math.Abs(r[1])); maxOi = Math.Max(maxOi, Math.Abs(r[2])); }
			if (maxVol <= 0 && maxOi <= 0) return outList;
			foreach (double[] r in s.Profile)
			{
				if (r[0] < lo || r[0] > hi) continue;
				double w = (maxVol > 0 ? Math.Abs(r[1]) / maxVol : 0) + (maxOi > 0 ? Math.Abs(r[2]) / maxOi : 0);
				if (w > 0) outList.Add(new[] { r[0], w });
			}
			outList.Sort((a, b) => b[1].CompareTo(a[1]));
			if (outList.Count > count) outList.RemoveRange(count, outList.Count - count);
			return outList;
		}

		// â”€â”€ history (relay per-minute archive) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
		private void LoadHistory()
		{
			string daysBody = PostJson("{\"key\":\"" + JsonEscape(LicenseKey) + "\",\"symbol\":\"" + upstreamSymbol +
				"\",\"mode\":\"days\",\"days\":" + Math.Max(1, HistoryDays + 1) + "}");
			var days = new List<string>();
			foreach (Match d in Regex.Matches(daysBody ?? "", "\"(\\d{4}-\\d{2}-\\d{2})\"")) days.Add(d.Groups[1].Value);
			days = days.Distinct().OrderByDescending(x => x).Take(Math.Max(1, HistoryDays + 1)).OrderBy(x => x).ToList();
			if (days.Count == 0) { Print("NinjaGexSignal: relay returned no history days"); return; }
			foreach (string day in days)
			{
				AppendHistory(histPrimary, upstreamSymbol, snaptypeToken, day);
				if (UseEtfOverlay && overlaySymbol != null) AppendHistory(histOverlay, overlaySymbol, snaptypeToken, day);
				if (UseStructural && snaptypeToken != "full") AppendHistory(histStructural, upstreamSymbol, "full", day);
			}
			Print(string.Format("NinjaGexSignal: history loaded -- primary {0}, overlay {1}, structural {2} points over {3} day(s)",
				histPrimary.Count, histOverlay.Count, histStructural.Count, days.Count));
		}

		private void AppendHistory(List<HistPt> target, string symbol, string snaptype, string day)
		{
			string body = PostJson("{\"key\":\"" + JsonEscape(LicenseKey) + "\",\"symbol\":\"" + symbol +
				"\",\"mode\":\"history\",\"snaptype\":\"" + snaptype + "\",\"day\":\"" + day + "\"}");
			if (string.IsNullOrEmpty(body) || body.IndexOf("\"history\"", StringComparison.Ordinal) < 0) return;
			TimeZoneInfo tz = Core.Globals.GeneralOptions.TimeZoneInfo;
			foreach (Match m in PointRx.Matches(body))
			{
				long t;
				if (!long.TryParse(m.Groups[1].Value, out t)) continue;
				var s = new Snap();
				foreach (Match f in FieldRx.Matches(m.Value))
				{
					if (f.Groups[2].Value == "null") continue;
					double v;
					if (!double.TryParse(f.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) continue;
					switch (f.Groups[1].Value)
					{
						case "zg": s.Majors["zero_gamma"] = v; break;
						case "zgv": s.Majors["zero_gamma_vol"] = v; break;
						case "mpv": s.Majors["major_pos_vol"] = v; break;
						case "mnv": s.Majors["major_neg_vol"] = v; break;
						case "mpo": s.Majors["major_pos_oi"] = v; break;
						case "mno": s.Majors["major_neg_oi"] = v; break;
						case "mlg": s.Majors["major_long_gamma"] = v; break;
						case "msg": s.Majors["major_short_gamma"] = v; break;
						case "spot": s.Majors["spot"] = v; break;
						case "net_gex_vol": s.NetGexVol = v; break;
					}
				}
				// The pusher's first point of a day can carry a half-scale wall
				// (seen 2026-09-09: 3182 on ES); drop points whose walls are far off spot.
				double spot, mpv;
				if (s.Majors.TryGetValue("spot", out spot) && s.Majors.TryGetValue("major_pos_vol", out mpv) && spot > 0 && Math.Abs(mpv - spot) > spot * 0.2)
					continue;
				DateTime local = TimeZoneInfo.ConvertTimeFromUtc(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(t), tz);
				target.Add(new HistPt { Time = local, S = s });
			}
			target.Sort((a, b) => a.Time.CompareTo(b.Time));
		}

		// Last archive point at or before t (chart-local), or null.
		private static Snap LookupAt(List<HistPt> list, DateTime t)
		{
			if (list == null || list.Count == 0) return null;
			int lo = 0, hi = list.Count - 1, best = -1;
			while (lo <= hi)
			{
				int mid = (lo + hi) / 2;
				if (list[mid].Time <= t) { best = mid; lo = mid + 1; } else hi = mid - 1;
			}
			if (best < 0) return null;
			// don't use a point older than 30 minutes (overnight gap)
			return (t - list[best].Time).TotalMinutes <= 30 ? list[best].S : null;
		}

		// Licence seat id, byte-identical to Pavi's NinjaGex / NinjaGexTracks (repo commit 59b6618, 2026-09-17): a one-way hash of
		// this PC's Windows MachineGuid. All scripts on one PC must send the SAME id or they would lock each other out of the key.
		private static string machineId;
		private static string MachineId()
		{
			if (machineId != null) return machineId;
			try
			{
				string raw = null;
				try
				{
					using (Microsoft.Win32.RegistryKey hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64))
					using (Microsoft.Win32.RegistryKey k = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography"))
						raw = k != null ? k.GetValue("MachineGuid") as string : null;
				}
				catch (Exception) { }
				if (string.IsNullOrWhiteSpace(raw)) raw = Environment.MachineName + "|" + Environment.UserName;
				using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
				{
					byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes("ninjagex-seat|" + raw.Trim().ToLowerInvariant()));
					StringBuilder sb = new StringBuilder(32);
					for (int i = 0; i < 16; i++) sb.Append(h[i].ToString("x2"));
					machineId = sb.ToString();
				}
			}
			catch (Exception) { machineId = ""; }
			return machineId;
		}

		private string PostJson(string json)
		{
			string mid = MachineId();
			if (mid.Length > 0 && json.StartsWith("{")) json = "{\"mid\":\"" + mid + "\"," + json.Substring(1);
			ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
			HttpWebRequest req = (HttpWebRequest)WebRequest.Create(RelayUrl);
			req.Method = "POST"; req.ContentType = "application/json"; req.Timeout = 15000; req.UserAgent = "NinjaGexSignal/1.0";
			byte[] payload = Encoding.UTF8.GetBytes(json);
			req.ContentLength = payload.Length;
			using (Stream st = req.GetRequestStream()) st.Write(payload, 0, payload.Length);
			try
			{
				using (WebResponse resp = req.GetResponse())
				using (StreamReader r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
					return r.ReadToEnd();
			}
			catch (WebException ex)
			{
				HttpWebResponse hr = ex.Response as HttpWebResponse;
				if (hr != null)
				{
					int code = (int)hr.StatusCode;
					lastProblem = code == 401 ? "license key rejected" : code == 409 ? "license key in use elsewhere" : code == 403 ? "machine limit: key already used on 2 PCs in the last 24h" : code == 426 ? "indicator update required by the relay" : "relay error " + code;
					hr.Close();
					// self-heal: an instance saved with an old key (typically the copy a strategy hosts) falls back to the built-in key;
					// the next refresh (a few seconds later) then succeeds and clears the message
					if (code == 401 && LicenseKey != BuiltInKey) { LicenseKey = BuiltInKey; lastProblem = "old license key rejected -> switched to the built-in key"; }
				}
				else lastProblem = ex.Message;
				return "";
			}
		}

		private static string JsonEscape(string s) { return (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\""); }

		private double Zg(Snap s)
		{
			double v;
			if (ZgSource == NinjaGexSigZgSource.OpenInterestPlusVolume && s.Majors.TryGetValue("zero_gamma_vol", out v) && v > 0) return v;
			if (s.Majors.TryGetValue("zero_gamma", out v) && v > 0) return v;
			if (s.Majors.TryGetValue("zero_gamma_vol", out v) && v > 0) return v;
			return double.NaN;
		}

		// â”€â”€ live delta (real-time only) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
		protected override void OnMarketData(MarketDataEventArgs e)
		{
			if (e.MarketDataType != MarketDataType.Last || e.Volume <= 0) return;
			bool buy = e.Ask > 0 && e.Price >= e.Ask, sell = e.Bid > 0 && e.Price <= e.Bid;
			if (!buy && !sell) return;
			liveDelta += buy ? e.Volume : -e.Volume;
			double px = Instrument.MasterInstrument.RoundToTickSize(e.Price);
			lock (tapeLock)
			{
				double[] t;
				if (!tape.TryGetValue(px, out t)) { t = new double[2]; tape[px] = t; }
				t[buy ? 1 : 0] += e.Volume;
				if (e.Volume >= BigPrintSize)
				{
					bigPrints.Add(new[] { px, buy ? e.Volume : -e.Volume, CurrentBar });
					if (bigPrints.Count > 500) bigPrints.RemoveAt(0);
				}
			}
		}

		// Net big-print flow inside +/- zone around a level over the last N bars: (+buys, -sells, count).
		private void BigPrintFlow(double level, double zone, int bars, out double net, out int count)
		{
			net = 0; count = 0;
			lock (tapeLock)
				foreach (double[] bp in bigPrints)
					if (bp[2] >= CurrentBar - bars && Math.Abs(bp[0] - level) <= zone) { net += bp[1]; count++; }
		}

		// Delta and volume traded inside +/- zone around a price (session tape).
		private void ZoneFlow(double level, double zone, out double delta, out double vol)
		{
			delta = 0; vol = 0;
			lock (tapeLock)
				foreach (var kv in tape)
					if (Math.Abs(kv.Key - level) <= zone) { delta += kv.Value[1] - kv.Value[0]; vol += kv.Value[0] + kv.Value[1]; }
		}

		// â”€â”€ scoring â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
		// sk = offset of the bar being scored: 0 = the bar OnBarUpdate is on (historical / OnBarClose),
		// 1 = the bar that just closed when running OnEachTick (first tick of the new bar).
		private int sk;
		private bool tickMode { get { return State == State.Realtime && Calculate != Calculate.OnBarClose; } }

		protected override void OnBarUpdate()
		{
			lastBarIdx = CurrentBar;
			aliveAt = DateTime.Now;      // heartbeat: a strategy drops its reference when this goes stale (chart reloaded)
			lastTradePx = Close[0];   // tick mode: every tick; bar-close mode: the bar's close
			// never let an exception in the scorer escape: NT8 disables the whole script (and a hosting strategy) on the first one
			try
			{
				if (tickMode)
				{
					if (IsFirstTickOfBar && CurrentBar >= 1) { sk = 1; ScoreBar(); }
					sk = 0;
					IntraBar();
					if (State == State.Realtime) WriteStatus();
					return;
				}
				sk = 0;
				if (State == State.Historical && Time[0] > NinjaTrader.Core.Globals.Now) return;   // forming bar at load: wait for its real close
				ScoreBar();
				if (State == State.Realtime) WriteStatus();
			}
			catch (Exception ex)
			{
				sk = 0;
				lastProblem = "bar update: " + ex.Message;
				try { Print("NinjaGexSignal " + upstreamSymbol + " OnBarUpdate error: " + ex); } catch (Exception) { }
			}
		}

		// Live status file for the trade watcher (recorder/trade_watch.py) and for anyone reading the state without the chart:
		// <export>/ngs_<SYM>_<TF>_status.json, rewritten at most every 5 s while the chart is live.
		private void WriteStatus()
		{
			if (!ExportEnabled) return;
			DateTime now = DateTime.Now;
			if ((now - lastStatusWrite).TotalSeconds < 5) return;
			lastStatusWrite = now;
			try
			{
				if (exportFile == null) ExportInit();
				var inv = CultureInfo.InvariantCulture;
				Setup su = setup; Snap p; Level lvl; string regime, dir; double sL, sS, zg;
				lock (dataLock) { p = primary; lvl = lastLevel; regime = lastRegime; dir = lastDirection; sL = lastScoreLong; sS = lastScoreShort; }
				zg = p != null ? Zg(p) : double.NaN;
				double px = lastTradePx, pv = Instrument.MasterInstrument.PointValue;
				var sb = new StringBuilder("{");
				sb.Append("\"time\":\"").Append(Time[0].ToString("yyyy-MM-dd HH:mm:ss", inv)).Append("\",");
				sb.Append("\"written\":\"").Append(now.ToString("yyyy-MM-dd HH:mm:ss", inv)).Append("\",");
				sb.Append("\"symbol\":\"").Append(upstreamSymbol).Append("\",\"price\":").Append(px.ToString("0.##", inv)).Append(',');
				sb.Append("\"regime\":\"").Append(regime).Append("\",\"zero_gamma\":").Append(double.IsNaN(zg) ? "null" : zg.ToString("0.##", inv)).Append(',');
				sb.Append("\"net_gex_vol\":").Append(p != null ? p.NetGexVol.ToString("0", inv) : "null").Append(',');
				sb.Append("\"nearest\":\"").Append(lvl != null ? lvl.Name : "").Append("\",\"nearest_price\":").Append(lvl != null ? lvl.Price.ToString("0.##", inv) : "null").Append(',');
				sb.Append("\"score_long\":").Append(sL.ToString("0.#", inv)).Append(",\"score_short\":").Append(sS.ToString("0.#", inv)).Append(",\"direction\":\"").Append(dir).Append("\",");
				sb.Append("\"bar_delta\":").Append(liveDelta.ToString("0", inv)).Append(',');
				sb.Append("\"news_lock\":\"").Append(newsLockText.Replace((char)34, (char)32)).Append("\",");
				sb.Append("\"vol_ratio\":").Append(volRatio.ToString("0.00", inv)).Append(',');
				sb.Append("\"advice\":\"").Append(migAdvice.Replace((char)34, (char)32)).Append("\",");
				if (su != null && !su.Closed)
				{
					double pts = su.Active ? su.Pts(px) : 0;
					sb.Append("\"setup\":{");
					sb.Append("\"seq\":").Append(su.Seq).Append(',');
					sb.Append("\"dir\":\"").Append(su.Dir).Append("\",\"state\":\"").Append(su.Active ? "in_trade" : "pending").Append("\",");
					sb.Append("\"signal_time\":\"").Append(su.SignalTime.ToString("yyyy-MM-dd HH:mm:ss", inv)).Append("\",\"live_signal\":").Append(su.LiveSignal ? "true" : "false").Append(',');
					sb.Append("\"level\":\"").Append(su.LevelName).Append("\",\"score\":").Append(su.Score.ToString("0", inv)).Append(',');
					sb.Append("\"trigger\":").Append(su.Trigger.ToString("0.##", inv)).Append(",\"entry\":").Append(su.Active ? su.Entry.ToString("0.##", inv) : "null").Append(',');
					sb.Append("\"stop\":").Append(su.Stop.ToString("0.##", inv)).Append(",\"stop_init\":").Append(su.StopInit.ToString("0.##", inv)).Append(',');
					sb.Append("\"target\":").Append(su.Target.ToString("0.##", inv)).Append(",\"target_name\":\"").Append(su.TargetName).Append("\",");
					sb.Append("\"qty\":").Append(su.Qty).Append(",\"risk_pts\":").Append(su.Risk.ToString("0.##", inv)).Append(',');
					sb.Append("\"pts\":").Append(pts.ToString("0.##", inv)).Append(",\"usd\":").Append((pts * su.Qty * pv).ToString("0", inv)).Append(',');
					sb.Append("\"r\":").Append(su.Risk > 0 ? (pts / su.Risk).ToString("0.##", inv) : "0").Append(',');
					sb.Append("\"max_fav\":").Append(su.MaxFav.ToString("0.##", inv)).Append(",\"max_adv\":").Append(su.MaxAdv.ToString("0.##", inv)).Append(',');
					sb.Append("\"bars_in\":").Append(su.Active ? Math.Max(0, CurrentBar - su.TriggerBar) : 0).Append(',');
					sb.Append("\"why\":\"").Append(su.Why.Replace((char)34, (char)39)).Append("\",\"target_why\":\"").Append(su.TargetWhy.Replace((char)34, (char)39)).Append("\"}");
				}
				else sb.Append("\"setup\":null");
				sb.Append('}');
				string file = exportFile.Replace(".csv", "_status.json");
				System.IO.File.WriteAllText(file + ".tmp", sb.ToString(), Encoding.UTF8);
				if (System.IO.File.Exists(file)) System.IO.File.Delete(file);
				System.IO.File.Move(file + ".tmp", file);
			}
			catch (Exception ex) { lastProblem = "status: " + ex.Message; }
		}

		// Tick-by-tick trigger / stop / target handling of the pending or active setup (live, OnEachTick only).
		private void IntraBar()
		{
			double tick = Instrument.MasterInstrument.TickSize;
			IntraOne(shadow, tick);
			Setup su = setup;
			if (su == null || su.Closed)
			{
				Values[0][0] = 0; Values[1][0] = 0; Values[2][0] = 0; Values[3][0] = 0; Values[4][0] = lastScoreLong - lastScoreShort;
				return;
			}
			IntraOne(su, tick);
			bool isLong = su.Dir == "LONG";
			bool live = !su.Closed;
			Values[0][0] = live && su.Active && su.TriggerBar == CurrentBar ? (isLong ? 1 : -1) : 0;
			Values[1][0] = live ? (isLong ? 1 : -1) : 0;
			Values[2][0] = live ? su.Stop : 0;
			Values[3][0] = live ? su.Target : 0;
			Values[4][0] = lastScoreLong - lastScoreShort;
		}

		private void IntraOne(Setup su, double tick)
		{
			if (su == null || su.Closed) return;
			double px = Close[0];
			bool isLong = su.Dir == "LONG";
			if (!su.Active)
			{
				bool invalid = isLong ? px <= su.Stop : px >= su.Stop;
				bool hit = isLong ? px >= su.Trigger : px <= su.Trigger;
				if (invalid) CloseSetup(su, double.NaN, "cancelled: invalidated before trigger");
				else if (hit)
				{
					su.Active = true; su.TriggerBar = CurrentBar; su.TriggerTime = Time[0];
					// fill at the trigger unless price jumped through it by more than 2 ticks (then the jump price)
					su.Entry = isLong ? Math.Max(su.Trigger, px - 2 * tick > su.Trigger ? px : su.Trigger) : Math.Min(su.Trigger, px + 2 * tick < su.Trigger ? px : su.Trigger);
					if (ShowArrows && !su.Shadow) DrawEntryArrow(su, tick);
					if (AlertsOn && !su.Shadow)
						try { Alert("NGStrig" + CurrentBar, Priority.High, string.Format("NinjaGexSignal {0} {1} TRIGGERED @ {2:N2}  stop {3:N2}  target {4} {5:N2}", upstreamSymbol, su.Dir, su.Entry, su.Stop, su.TargetName, su.Target),
							NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert1.wav", 10, System.Windows.Media.Brushes.Black, isLong ? ClrLongSignal : ClrShortSignal); }
						catch (Exception) { }
					// a strategy-hosted copy has no chart: an unguarded ForceRefresh threw NullReference on the trigger tick and NT8 disabled the strategy
					try { if (ChartControl != null) ForceRefresh(); } catch (Exception) { }
				}
				else if (CurrentBar - su.SignalBar >= Math.Max(1, TriggerBars) && IsFirstTickOfBar) CloseSetup(su, double.NaN, "cancelled: no trigger");
			}
			else
			{
				double fav = isLong ? px - su.Entry : su.Entry - px;
				double adv = -fav;
				su.MaxFav = Math.Max(su.MaxFav, fav); su.MaxAdv = Math.Max(su.MaxAdv, adv);
				bool stopHit = isLong ? px <= su.Stop : px >= su.Stop;
				bool tgtHit = isLong ? px >= su.Target : px <= su.Target;
				if (stopHit) CloseSetup(su, su.Stop, su.Stop == su.StopInit ? "stop" : "trail stop");
				else if (tgtHit) CloseSetup(su, su.Target, "target " + su.TargetName);
				else if (CurrentBar - su.TriggerBar >= Math.Max(1, MaxBarsInTrade) && IsFirstTickOfBar) CloseSetup(su, px, "time exit");
				else
				{
					double r = su.Risk > 0 ? su.Risk : tick;
					// profit lock: a trade that has been well past 1R must not come back to nothing (12 of 75 recorded trades did)
					if (LockAtR > 0 && su.MaxFav >= LockAtR * r) { double lk = isLong ? su.Entry + LockToR * r : su.Entry - LockToR * r; su.Stop = isLong ? Math.Max(su.Stop, lk) : Math.Min(su.Stop, lk); }
					if (StopMode == NinjaGexSigStopMode.BreakevenAt1R && su.MaxFav >= r)
					{
						double be = isLong ? su.Entry + BreakevenPlusTicks * tick : su.Entry - BreakevenPlusTicks * tick;
						su.Stop = isLong ? Math.Max(su.Stop, be) : Math.Min(su.Stop, be);
					}
					else if (StopMode == NinjaGexSigStopMode.AtrTrail)
					{
						double trail = Math.Max(0.25, TrailAtr) * (atr[0] > 0 ? atr[0] : r);
						if (isLong) su.Stop = Math.Max(su.Stop, px - trail); else su.Stop = Math.Min(su.Stop, px + trail);
					}
				}
			}
		}

		private static bool IsRelayLevel(string name)
		{
			return name == "Zero Gamma" || name.EndsWith("Call Wall") || name.EndsWith("Put Wall") || name.EndsWith("Call Wall OI") || name.EndsWith("Put Wall OI")
				|| name.EndsWith("Long Gamma") || name.EndsWith("Short Gamma") || name == "EM+" || name == "EM-";
		}

		// A target named after a relay level moves with that level (NQ 09-17: target "Zero Gamma 29,662" while Zero Gamma itself had
		// climbed to 29,710 and price sat on it at +27 pts; the frozen target was 48 pts lower, under the put wall).
		private void FollowTarget(Setup su, List<Level> levels, double price, double tick)
		{
			if (!FollowTargetLevel || su == null || su.Closed || string.IsNullOrEmpty(su.TargetLevel)) return;
			Level lv = null;
			foreach (Level l in levels) if (l.Name == su.TargetLevel) { lv = l; break; }
			if (lv == null) return;
			bool isLong = su.Dir == "LONG";
			double refPx = su.Active ? su.Entry : su.Trigger, risk = Math.Abs(refPx - su.StopInit);
			double nt = isLong ? lv.Price - su.TargetOffset : lv.Price + su.TargetOffset;
			double cap = isLong ? refPx + risk * MaxRewardR : refPx - risk * MaxRewardR;
			nt = isLong ? Math.Min(nt, cap) : Math.Max(nt, cap);
			if (Math.Abs(nt - su.Target) < 2 * tick) return;
			bool reached = isLong ? nt <= price : nt >= price;
			if (reached)
			{
				// the level came to price: the trade has arrived where it was going
				if (su.Active && su.Pts(price) > 0) CloseSetup(su, price, "target " + su.TargetName + " (level moved to price)");
				return;
			}
			if (su.TargetWhy.Length < 400) su.TargetWhy += string.Format(" | {0:HH:mm} {1} moved {2:N2} -> {3:N2}", Time[sk], su.TargetLevel, su.Target, nt);
			su.Target = nt; lastStatusWrite = DateTime.MinValue;
		}

		private void ScoreBar()
		{
			if ((CurrentBar - sk) < 25) { scoreSeries[sk] = 0; return; }

			// session VWAP
			if (Bars.IsFirstBarOfSessionByIndex(CurrentBar - sk))
			{
				vwapPv = 0; vwapV = 0; lock (tapeLock) tape.Clear();
				if (sessHigh > 0) { prevHigh = sessHigh; prevLow = sessLow; prevClose = lastClose; }
				sessOpen = Open[sk]; sessHigh = High[sk]; sessLow = Low[sk]; sessStartTime = Time[sk];
				dayPts = 0; dayDollars = 0; dayTrades = 0; dayWins = 0;
			}
			sessHigh = Math.Max(sessHigh, High[sk]); sessLow = sessLow > 0 ? Math.Min(sessLow, Low[sk]) : Low[sk]; lastClose = Close[sk];
			if (UseSessionLevels)
			{
				// bar time is the bar's close; a 10-min bar stamped 18:10 is the first Asia bar
				int hm = Time[sk].Hour * 100 + Time[sk].Minute;
				int win = hm > 1800 || hm <= 300 ? 0 : hm <= 930 ? 1 : hm <= 1600 ? 2 : 3;   // 3 = 16:00-18:00 gap, tracked nowhere
				if (win != lastSessWin)
				{
					if (win == 0) { asiaH = High[sk]; asiaL = Low[sk]; }
					else if (win == 1) { lonH = High[sk]; lonL = Low[sk]; }
					else if (win == 2) { if (nyCurH > 0) { nyH = nyCurH; nyL = nyCurL; } nyCurH = High[sk]; nyCurL = Low[sk]; }
					lastSessWin = win;
				}
				if (win == 0) { asiaH = Math.Max(asiaH, High[sk]); asiaL = Math.Min(asiaL, Low[sk]); }
				else if (win == 1) { lonH = Math.Max(lonH, High[sk]); lonL = Math.Min(lonL, Low[sk]); }
				else if (win == 2) { nyCurH = Math.Max(nyCurH, High[sk]); nyCurL = Math.Min(nyCurL, Low[sk]); }
			}
			double typ = (High[sk] + Low[sk] + Close[sk]) / 3.0;
			vwapPv += typ * Volume[sk]; vwapV += Volume[sk];
			sessionVwap = vwapV > 0 ? vwapPv / vwapV : Close[sk];

			// bar delta from the live accumulator, then reset for the next bar
			lastBarDelta = State == State.Realtime ? liveDelta : 0;
			liveDelta = 0;

			Snap p, o, f;
			if (State == State.Historical)
			{
				// Historical bars: levels as they were at that minute, from the archive.
				p = LookupAt(histPrimary, Time[sk]);
				o = LookupAt(histOverlay, Time[sk]);
				f = LookupAt(histStructural, Time[sk]);
			}
			else
				lock (dataLock) { p = primary; o = overlay; f = structural; }
			if (p == null) { scoreSeries[sk] = 0; return; }

			double tick = Instrument.MasterInstrument.TickSize;
			double range = Math.Max(tick, High[sk] - Low[sk]);
			double atrV = atr[sk] > 0 ? atr[sk] : range;
			// ATR-relative tolerance so the same settings work on ES (ATR ~8) and NQ (ATR ~45); ticks act as the floor
			double tol = Math.Max(Math.Max(1, LevelToleranceTicks) * tick, LevelToleranceAtr * atrV);
			double agreePts = Math.Max(WallAgreePoints, WallAgreeAtr * atrV);
			double zg = Zg(p);
			double price = Close[sk];
			volRatio = atrSma != null && atrSma[sk] > 0 ? atrV / atrSma[sk] : 1;

			// 1. regime
			var factors = new List<Factor>();
			string regime = double.IsNaN(zg) ? "unknown" : price > zg ? "LONG gamma (range)" : "SHORT gamma (trend)";
			bool shortGammaRegime = !double.IsNaN(zg) && price < zg;

			// candidate levels from the primary snapshot
			var levels = new List<Level>();
			AddLevel(levels, p, "major_pos_vol", "Call Wall", false, true, false, false);
			AddLevel(levels, p, "major_neg_vol", "Put Wall", true, true, false, false);
			AddLevel(levels, p, "major_pos_oi", "Call Wall OI", false, true, false, false);
			AddLevel(levels, p, "major_neg_oi", "Put Wall OI", true, true, false, false);
			AddLevel(levels, p, "major_long_gamma", "Long Gamma", true, false, true, false);
			AddLevel(levels, p, "major_short_gamma", "Short Gamma", false, false, false, true);
			if (!double.IsNaN(zg)) levels.Add(new Level { Name = "Zero Gamma", Price = zg, IsSupport = price >= zg });
			if (o != null && UseOverlayLevels)
			{
				string tagO = overlaySymbol != null && overlaySymbol.Contains("_") ? overlaySymbol.Substring(overlaySymbol.IndexOf('_') + 1) : "ETF";
				double ov;
				if (o.Majors.TryGetValue("major_pos_vol", out ov) && ov > 0) levels.Add(new Level { Name = tagO + " Call Wall", Price = ov, IsSupport = false, IsWall = true, Strength = 0.8 });
				if (o.Majors.TryGetValue("major_neg_vol", out ov) && ov > 0) levels.Add(new Level { Name = tagO + " Put Wall", Price = ov, IsSupport = true, IsWall = true, Strength = 0.8 });
				if (o.Majors.TryGetValue("major_long_gamma", out ov) && ov > 0) levels.Add(new Level { Name = tagO + " Long Gamma", Price = ov, IsSupport = true, IsLongGamma = true, Strength = 0.7 });
				if (o.Majors.TryGetValue("major_short_gamma", out ov) && ov > 0) levels.Add(new Level { Name = tagO + " Short Gamma", Price = ov, IsSupport = false, IsShortGamma = true, Strength = 0.7 });
			}
			// Video additions: G1..G3 inside the expected move (live snapshots carry the profile), EM+/EM- as fade levels.
			var gl = GLevels(p, price, atrV, Math.Max(0, Math.Min(5, GLevelCount)));
			// after the close the 0DTE profile is expired/empty: fall back to the 90-day profile so the G lines stay on the chart
			if (gl.Count == 0 && f != null) gl = GLevels(f, price, atrV, Math.Max(0, Math.Min(5, GLevelCount)));
			double[] gStrength = { 0.8, 0.65, 0.5, 0.4, 0.3 };
			for (int i = 0; i < gl.Count; i++)
				levels.Add(new Level { Name = "G" + (i + 1), Price = gl[i][0], IsSupport = gl[i][0] <= price, Strength = gStrength[i] });
			if (!double.IsNaN(p.EmUpper) && p.EmUpper > price) levels.Add(new Level { Name = "EM+", Price = p.EmUpper, IsSupport = false, Strength = 0.7 });
			if (!double.IsNaN(p.EmLower) && p.EmLower < price) levels.Add(new Level { Name = "EM-", Price = p.EmLower, IsSupport = true, Strength = 0.7 });
			if (UsePriorDay && !double.IsNaN(prevHigh))
			{
				levels.Add(new Level { Name = "PDH", Price = prevHigh, IsSupport = prevHigh <= price, Strength = 0.6 });
				levels.Add(new Level { Name = "PDL", Price = prevLow, IsSupport = prevLow <= price, Strength = 0.6 });
				levels.Add(new Level { Name = "PDC", Price = prevClose, IsSupport = prevClose <= price, Strength = 0.4 });
			}
			if (UseSessionLevels)
			{
				var sl = new List<KeyValuePair<string, double>>();
				if (asiaH > 0) { sl.Add(new KeyValuePair<string, double>("Asia High", asiaH)); sl.Add(new KeyValuePair<string, double>("Asia Low", asiaL)); }
				if (lonH > 0) { sl.Add(new KeyValuePair<string, double>("London High", lonH)); sl.Add(new KeyValuePair<string, double>("London Low", lonL)); }
				if (nyH > 0) { sl.Add(new KeyValuePair<string, double>("NY High", nyH)); sl.Add(new KeyValuePair<string, double>("NY Low", nyL)); }
				foreach (var kv in sl)
					levels.Add(new Level { Name = kv.Key, Price = kv.Value, IsSupport = kv.Value <= price, Strength = kv.Key.StartsWith("NY") ? 0.6 : 0.5 });
				lock (dataLock) { lastSessLevels = sl; }
			}
			if (UsePtz)
			{
				// Globex bars after 18:00 belong to the next plan day; the plan is posted ~08:00 ET so the evening uses the last one
				// the plan for the next trading day is published the evening before (~19:30 ET): from 18:00 on use it when it is already in the file
				string day = Time[sk].ToString("yyyy-MM-dd");
				if (Time[sk].Hour >= 18)
				{
					string nextDay = Time[sk].Date.AddDays(Time[sk].DayOfWeek == DayOfWeek.Friday ? 3 : 1).ToString("yyyy-MM-dd");
					bool haveNext; lock (dataLock) { haveNext = ptzAll.Any(r => r.Date == nextDay); }
					if (haveNext) day = nextDay;
				}
				List<PtzLevel> rows;
				lock (dataLock) { rows = ptzAll.Where(r => r.Date == day).ToList(); if (rows.Count == 0 && ptzAll.Count > 0) { string last = ptzAll.Max(r => r.Date); if (string.CompareOrdinal(last, day) < 0 && (Time[sk] - DateTime.ParseExact(last, "yyyy-MM-dd", CultureInfo.InvariantCulture)).TotalDays <= 4) rows = ptzAll.Where(r => r.Date == last).ToList(); } }
				foreach (PtzLevel r in rows)
				{
					// zones: the edge nearer to price is what gets touched first (mid only when price is inside the band)
					double px = r.Mid;
					if (r.P2 > 0)
					{
						double lo = Math.Min(r.P1, r.P2), hi = Math.Max(r.P1, r.P2);
						px = price > hi ? hi : price < lo ? lo : r.Mid;
					}
					bool sup = r.Kind == "long" || r.Kind == "pivot_bull" && px <= price || r.Kind == "pivot_bear" && px <= price || (r.Kind == "strength" || r.Kind == "weakness") && px <= price;
					double st = r.Kind.StartsWith("pivot") ? 0.7 : r.Kind == "long" || r.Kind == "short" ? 0.6 : 0.5;
					levels.Add(new Level { Name = r.Name, Price = px, IsSupport = sup, Strength = st });
				}
				lock (dataLock) { lastPtz = rows; }
			}
			if (LevelSet == NinjaGexSigLevelSet.PtzOnly) levels.RemoveAll(l => !l.Name.StartsWith("PTZ "));
			lock (dataLock) { lastGLevels = gl; }
			// Wall migration: when Zero Gamma or the call wall has climbed >= MigrationAtr ATR over the last MigrationMinutes the whole
			// box is moving up and shorts at resistance fade a trend (mirror: Zero Gamma / put wall falling blocks longs at support).
			bool migBlockShort = false, migBlockLong = false; string migNote = "";
			if (UseWallMigration && !double.IsNaN(zg))
			{
				double cwN = double.NaN, pwN = double.NaN, tv, zgMig = zg;
				if (p.Majors.TryGetValue("major_pos_vol", out tv) && tv > 0) cwN = tv;
				if (p.Majors.TryGetValue("major_neg_vol", out tv) && tv > 0) pwN = tv;
				if (migHist.Count > 0)
				{
					double[] last = migHist[migHist.Count - 1];
					bool roll = Math.Abs(zg - last[1]) > 6 * atrV || (!double.IsNaN(cwN) && !double.IsNaN(last[2]) && Math.Abs(cwN - last[2]) > 8 * atrV);
					if (roll || Time[sk].Ticks <= last[0]) migHist.Clear();   // new option chain / session, or a replay restart
				}
				migHist.Add(new double[] { Time[sk].Ticks, zg, cwN, pwN });
				if (migHist.Count > 80) migHist.RemoveAt(0);
				// In the first half hour of the cash session the volume walls hop between strikes hundreds of points apart
				// (NQ put wall 09-18: 29,491 -> 29,786 -> 29,543 -> 29,694 in 40 min). Readings before 10:00 are not used,
				// and both ends of the comparison are the MEDIAN of three neighbouring readings, not single points.
				var usable = new List<double[]>();
				foreach (double[] m in migHist)
				{
					DateTime mt = new DateTime((long)m[0]);
					int hm = mt.Hour * 100 + mt.Minute;
					if (hm >= 930 && hm < 1000) continue;
					usable.Add(m);
				}
				double[] old = null;
				int oldIdx = -1;
				for (int q = 0; q < usable.Count; q++)
				{
					double mins = (Time[sk] - new DateTime((long)usable[q][0])).TotalMinutes;
					if (mins < MigrationMinutes - 1) break;
					if (mins <= 2 * MigrationMinutes) oldIdx = q;
				}
				if (oldIdx >= 0 && usable.Count - 1 - oldIdx >= 3)
				{
					Func<int, int, double> med = (center, col) =>
					{
						var v = new List<double>();
						for (int q = Math.Max(0, center - 1); q <= Math.Min(usable.Count - 1, center + 1); q++) if (!double.IsNaN(usable[q][col])) v.Add(usable[q][col]);
						if (v.Count == 0) return double.NaN;
						v.Sort(); return v[v.Count / 2];
					};
					int nowIdx = usable.Count - 1;
					old = new double[] { usable[oldIdx][0], med(oldIdx, 1), med(oldIdx, 2), med(oldIdx, 3) };
					zgMig = med(nowIdx, 1); cwN = med(nowIdx, 2); pwN = med(nowIdx, 3);
				}
				// the rule was calibrated on 10-minute bars: express the drift in 10-minute-equivalent ATRs so a 5-minute chart
				// (ATR about 0.7x) does not call 4 points of drift "1 ATR"
				double atrRef = atrV;
				if (BarsPeriod != null && BarsPeriod.BarsPeriodType == BarsPeriodType.Minute && BarsPeriod.Value > 0) atrRef = atrV * Math.Sqrt(10.0 / BarsPeriod.Value);
				if (old != null && atrRef > 0)
				{
					double dZ = zgMig - old[1], dC = double.IsNaN(cwN) || double.IsNaN(old[2]) ? 0 : cwN - old[2], dP = double.IsNaN(pwN) || double.IsNaN(old[3]) ? 0 : pwN - old[3];
					double up = Math.Max(dZ, dC) / atrRef, down = Math.Min(dZ, dP) / atrRef;
					// the wall and Zero Gamma must not contradict each other (NQ 09-18: ZG +5 while the put wall read -95)
					migBlockShort = up >= MigrationAtr && dZ >= 0 && dC >= 0;
					migBlockLong = down <= -MigrationAtr && dZ <= 0 && dP <= 0;
					if (migBlockShort) migNote = string.Format("walls rising {0:0.0} ATR / {1} min (ZG {2:+0;-0}, call wall {3:+0;-0}): shorts at resistance OFF", up, MigrationMinutes, dZ, dC);
					else if (migBlockLong) migNote = string.Format("walls falling {0:0.0} ATR / {1} min (ZG {2:+0;-0}, put wall {3:+0;-0}): longs at support OFF", -down, MigrationMinutes, dZ, dP);
				}
			}
			migAdvice = migNote.Length > 0 ? migNote + " (strong reversal bars still allowed)" : "";
			bool magnet = !double.IsNaN(zg) && Math.Abs(price - zg) <= Math.Max(1, MagnetTicks) * tick;

			// nearest level and whether the bar touched it
			Level near = null; double nearDist = double.MaxValue;
			foreach (Level l in levels)
			{
				double d = Math.Abs(price - l.Price);
				if (d < nearDist) { nearDist = d; near = l; }
			}
			// Touch selection (switch "Pick the level the bar touched"): a reversal bar that tags a support at its low and closes
			// far above it used to be assigned to whatever level was nearest its CLOSE (often one it never reached) and scored
			// "not at a level". Now: bullish bar -> the support inside the bar's range nearest the low, with the close back above
			// it; bearish bar -> the resistance nearest the high with the close back below it. Falls back to nearest-to-close.
			bool touchPick = false;
			if (LevelByTouch && range > 0)
			{
				bool bullBar = Close[sk] >= Low[sk] + 0.5 * range, bearBar = Close[sk] <= High[sk] - 0.5 * range;
				Level pick = null; double pickDist = double.MaxValue;
				foreach (Level l in levels)
				{
					if (l.Price < Low[sk] - tol || l.Price > High[sk] + tol) continue;
					if (bullBar && l.IsSupport && Close[sk] > l.Price)
					{
						double d = Math.Abs(l.Price - Low[sk]);
						if (d < pickDist || (Math.Abs(d - pickDist) < tick && pick != null && l.Strength > pick.Strength)) { pickDist = d; pick = l; }
					}
					else if (bearBar && !l.IsSupport && Close[sk] < l.Price)
					{
						double d = Math.Abs(High[sk] - l.Price);
						if (d < pickDist || (Math.Abs(d - pickDist) < tick && pick != null && l.Strength > pick.Strength)) { pickDist = d; pick = l; }
					}
				}
				// only override when the close-based pick is not already a touched level on the right side
				bool nearTouched = near != null && Low[sk] - tol <= near.Price && High[sk] + tol >= near.Price;
				if (pick != null && (!nearTouched || pick == near || (bullBar && !near.IsSupport) || (bearBar && near.IsSupport)))
				{
					near = pick; nearDist = pickDist; touchPick = true;   // proximity is measured from the bar's extreme, not the close
				}
			}
			bool touched = near != null && Low[sk] - tol <= near.Price && High[sk] + tol >= near.Price;
			bool atLevel = near != null && (touched || nearDist <= tol);

			// direction candidates: fade at a wall/gamma line, or continuation on a break in short-gamma regime
			double fadeLong = 0, fadeShort = 0;
			if (atLevel)
			{
				if (near.IsSupport) fadeLong = 1; else fadeShort = 1;
			}
			// Acceptance through a level, in either direction, by this bar or the
			// previous one: break of resistance / reclaim of support = long,
			// loss of support / rejection back under resistance = short.
			bool brokeUp = near != null && Close[sk] > near.Price + tol && Math.Min(Open[sk], Close[sk + 1]) <= near.Price + tol;
			bool brokeDown = near != null && Close[sk] < near.Price - tol && Math.Max(Open[sk], Close[sk + 1]) >= near.Price - tol;

			// F1 regime: long-gamma favours fades, short-gamma favours continuation on breaks
			var fReg = new Factor { Name = "Regime" };
			if (!double.IsNaN(zg))
			{
				if (!shortGammaRegime) { fReg.Long = fadeLong; fReg.Short = fadeShort; fReg.Note = "range: fade walls"; }
				else
				{
					fReg.Long = brokeUp ? 1 : fadeLong * 0.4;
					fReg.Short = brokeDown ? 1 : fadeShort * 0.4;
					fReg.Note = brokeUp || brokeDown ? "trend: break continuation" : "trend: fades weak";
				}
				if (magnet)
				{
					// HVL magnet: price glued to Zero Gamma chops; continuation setups are suspect here.
					fReg.Long *= 0.3; fReg.Short *= 0.3;
					fReg.Note = "ZG magnet: expect chop" + (fReg.Note != null ? " (" + fReg.Note + ")" : "");
				}
			}
			factors.Add(fReg);

			// F2 wall proximity (distance in ATR)
			var fWall = new Factor { Name = "At level" };
			if (near != null)
			{
				double prox = Math.Max(0, 1 - nearDist / Math.Max(tol, atrV)) * near.Strength;
				fWall.Long = near.IsSupport ? prox : 0;
				fWall.Short = near.IsSupport ? 0 : prox;
				if (brokeUp) { fWall.Long = Math.Max(fWall.Long, 0.7); fWall.Short = 0; }
				if (brokeDown) { fWall.Short = Math.Max(fWall.Short, 0.7); fWall.Long = 0; }
				fWall.Note = string.Format("{0} {1:N2} ({2:+0.00;-0.00} pts)", near.Name, near.Price, price - near.Price);
			}
			factors.Add(fWall);

			// F3 structure: 90-day OI wall and ETF overlay wall agree with the touched level
			var fStr = new Factor { Name = "Structure" };
			if (near != null)
			{
				double agree = 0; var notes = new List<string>();
				if (f != null)
				{
					double fv;
					string key = near.IsSupport ? "major_neg_oi" : "major_pos_oi";
					if (f.Majors.TryGetValue(key, out fv) && Math.Abs(fv - near.Price) <= agreePts) { agree += 0.5; notes.Add("90d OI " + fv.ToString("N0")); }
				}
				if (o != null)
				{
					double ov;
					string key = near.IsSupport ? "major_neg_vol" : "major_pos_vol";
					if (o.Majors.TryGetValue(key, out ov) && Math.Abs(ov - near.Price) <= agreePts) { agree += 0.5; notes.Add((overlaySymbol ?? "ETF") + " " + ov.ToString("N0")); }
				}
				fStr.Long = near.IsSupport ? agree : 0;
				fStr.Short = near.IsSupport ? 0 : agree;
				fStr.Note = notes.Count > 0 ? string.Join(", ", notes) : "no agreement";
			}
			factors.Add(fStr);

			// F4 price action: rejection wick at the level, acceptance close, VWAP side
			var fPa = new Factor { Name = "Price action" };
			{
				double upperWick = High[sk] - Math.Max(Open[sk], Close[sk]);
				double lowerWick = Math.Min(Open[sk], Close[sk]) - Low[sk];
				// rejection: long wick through/into the level and a close back within tolerance of it (a hammer closing a point under the wall still counts)
				bool rejectLow = atLevel && near.IsSupport && lowerWick >= WickRatio * range && Close[sk] >= near.Price - tol && Low[sk] <= near.Price + tol;
				bool rejectHigh = atLevel && !near.IsSupport && upperWick >= WickRatio * range && Close[sk] <= near.Price + tol && High[sk] >= near.Price - tol;
				// a strong rejection wick (>= 70% of the bar) is worth more than the base 0.6
				double rejW = Math.Max(lowerWick, upperWick) / range >= 0.7 ? 0.8 : 0.6;
				double l = 0, s = 0; var notes = new List<string>();
				if (rejectLow) { l += rejW; notes.Add(rejW > 0.6 ? "strong rejection wick" : "rejection wick"); }
				if (rejectHigh) { s += rejW; notes.Add(rejW > 0.6 ? "strong rejection wick" : "rejection wick"); }
				if (brokeUp) { l += 0.6; notes.Add("accepted above"); }
				if (brokeDown) { s += 0.6; notes.Add("accepted below"); }
				if (touchPick && !rejectLow && !rejectHigh && range >= 0.6 * atrV)
				{
					// body reversal: tagged the level at one end, closed in the outer quarter at the other end
					if (near.IsSupport && Close[sk] >= High[sk] - 0.25 * range) { l += 0.6; notes.Add("reversal bar off " + near.Name); }
					if (!near.IsSupport && Close[sk] <= Low[sk] + 0.25 * range) { s += 0.6; notes.Add("reversal bar off " + near.Name); }
				}
				if (price > sessionVwap) { l += 0.4; notes.Add("above VWAP"); } else { s += 0.4; notes.Add("below VWAP"); }
				fPa.Long = Math.Min(1, l); fPa.Short = Math.Min(1, s); fPa.Note = string.Join(", ", notes);
			}
			factors.Add(fPa);

			// F5 volume / flow
			var fVol = new Factor { Name = "Volume/flow" };
			{
				double avg = volSma[sk + 1] > 0 ? volSma[sk + 1] : Volume[sk];
				double spike = avg > 0 ? Volume[sk] / avg : 1;
				double v = spike >= VolumeSpikeMult ? 0.6 : spike >= 1.0 ? 0.3 : 0;
				double l = v, s = v; var notes = new List<string>();
				notes.Add(string.Format("vol x{0:0.0}", spike));
				if (lastBarDelta != 0)
				{
					if (lastBarDelta > 0) { l += 0.4; notes.Add("delta +" + lastBarDelta.ToString("N0")); }
					else { s += 0.4; notes.Add("delta " + lastBarDelta.ToString("N0")); }
				}
				fVol.Long = Math.Min(1, l); fVol.Short = Math.Min(1, s); fVol.Note = string.Join(", ", notes);
			}
			factors.Add(fVol);

			// F6 cheat-sheet rules: Major+/- (net GEX sign) x Long/Short Gamma touch
			var fCs = new Factor { Name = "Cheat sheet" };
			{
				bool majorPos = p.NetGexVol > 0;
				if (atLevel && near.IsLongGamma) { fCs.Long = 1; fCs.Note = majorPos ? "Major+ & Long Gamma: long (trend)" : "Major- & Long Gamma: long (reversal)"; }
				else if (atLevel && near.IsShortGamma) { fCs.Short = 1; fCs.Note = majorPos ? "Major+ & Short Gamma: short (reversal)" : "Major- & Short Gamma: short (trend)"; }
				else fCs.Note = majorPos ? "Major+ (net GEX > 0)" : "Major- (net GEX < 0)";
			}
			factors.Add(fCs);

			// F7 flow at the level: who is winning the tape inside the level zone (live only)
			var fFlow = new Factor { Name = "Flow@level" };
			if (near != null && State == State.Realtime)
			{
				double zd, zv;
				ZoneFlow(near.Price, tol * 2, out zd, out zv);
				if (zv >= FlowMinVolume)
				{
					double share = zd / zv;                       // -1..+1
					bool holdingAbove = Close[sk] >= near.Price;   // price holding on/over the level
					bool holdingBelow = Close[sk] <= near.Price;
					// buyers winning at support, or sellers hitting it while price holds (absorption) -> long
					fFlow.Long = share > 0 ? Math.Min(1, share * 1.5) : (holdingAbove && near.IsSupport ? Math.Min(0.7, -share) : 0);
					// sellers winning at resistance, or buyers hitting it while price holds under (absorption) -> short
					fFlow.Short = share < 0 ? Math.Min(1, -share * 1.5) : (holdingBelow && !near.IsSupport ? Math.Min(0.7, share) : 0);
					string tag = share > 0.15 ? "buyers winning" : share < -0.15 ? "sellers winning" : "balanced";
					if (fFlow.Long >= 0.4 && share < 0) tag = "sell absorption";
					if (fFlow.Short >= 0.4 && share > 0) tag = "buy absorption";
					// big prints at the level in the last 3 bars tilt the factor by up to 0.3
					double bpNet; int bpN;
					BigPrintFlow(near.Price, tol * 2, 3, out bpNet, out bpN);
					if (bpN > 0)
					{
						double tilt = Math.Min(0.3, Math.Abs(bpNet) / Math.Max(BigPrintSize * 4, 1) * 0.3);
						if (bpNet > 0) fFlow.Long = Math.Min(1, fFlow.Long + tilt); else fFlow.Short = Math.Min(1, fFlow.Short + tilt);
						tag += string.Format(", {0} big {1} {2:0}", bpN, bpNet > 0 ? "buys" : "sells", Math.Abs(bpNet));
					}
					fFlow.Note = string.Format("delta {0:+0;-0} / vol {1:0}  {2}", zd, zv, tag);
				}
				else fFlow.Note = "not enough volume at level yet";
			}
			else fFlow.Note = State == State.Realtime ? "no level" : "live only";
			factors.Add(fFlow);

			// F8 location: where price sits between Put Wall and Call Wall, and how far the
			// session has already travelled. Low in the range after a drop -> longs; high after a rally -> shorts.
			var fLoc = new Factor { Name = "Location" };
			{
				double pw, cw;
				bool havePw = p.Majors.TryGetValue("major_neg_vol", out pw) && pw > 0;
				bool haveCw = p.Majors.TryGetValue("major_pos_vol", out cw) && cw > 0;
				double pos = 0.5;
				if (havePw && haveCw && cw > pw) pos = Math.Max(0, Math.Min(1, (price - pw) / (cw - pw)));
				// extension measured against the recent swing (last N bars), not the whole Globex session
				int lb = Math.Min((CurrentBar - sk), Math.Max(5, LocationLookbackBars));
				double swingHigh = MAX(High, lb)[sk], swingLow = MIN(Low, lb)[sk];
				double fromHigh = (swingHigh - price) / atrV;   // ATRs below the recent high
				double fromLow = (price - swingLow) / atrV;     // ATRs above the recent low
				fLoc.Long = Math.Min(1, (1 - pos) + (fromHigh >= 2 ? 0.2 : 0));
				fLoc.Short = Math.Min(1, pos + (fromLow >= 2 ? 0.2 : 0));
				locPos = pos; locFromHigh = fromHigh; locFromLow = fromLow;
				fLoc.Note = string.Format("{0:0}% of wall range, {1:0.0} ATR off {2}-bar high, {3:0.0} ATR off low", pos * 100, fromHigh, lb, fromLow);
			}
			factors.Add(fLoc);

			// Reclaim rule (PropTraderz "long if reclaim and hold" / "short below after fail"): the bar closes through a plan
			// idea level with the prior close on the other side and the close in the outer half of the bar. Counts as a
			// strong acceptance at that level and, because the plan author already placed it in context, bypasses the
			// location filter. Off by default until the records say it earns.
			bool reclaimLong = false, reclaimShort = false;
			if (UseReclaimRule && (CurrentBar - sk) >= 1)
			{
				double prevC = Close[sk + 1];
				foreach (Level l in levels)
				{
					if (!l.Name.StartsWith("PTZ ")) continue;
					bool up = l.Name == "PTZ Long" && prevC < l.Price && Close[sk] > l.Price && Close[sk] >= Low[sk] + 0.5 * range && !(reclaimLong);
					bool dn = l.Name == "PTZ Short" && prevC > l.Price && Close[sk] < l.Price && Close[sk] <= High[sk] - 0.5 * range && !(reclaimShort);
					if (!up && !dn) continue;
					if (up) reclaimLong = true; else reclaimShort = true;
					near = l; nearDist = Math.Abs(price - l.Price); atLevel = true;
					fWall.Long = up ? Math.Max(fWall.Long, 0.8) : fWall.Long; fWall.Short = dn ? Math.Max(fWall.Short, 0.8) : fWall.Short;
					fWall.Note = (up ? "reclaim of " : "loss of ") + l.Name + " " + l.Price.ToString("N2");
					if (up) fPa.Long = Math.Max(fPa.Long, 0.6); else fPa.Short = Math.Max(fPa.Short, 0.6);
					fPa.Note = (string.IsNullOrEmpty(fPa.Note) ? "" : fPa.Note + ", ") + "plan-level reclaim";
				}
			}

			// weighted score per direction
			double[] w = { WRegime, WWall, WStructure, WPriceAction, WVolume, WCheatSheet, WFlow, WLocation };
			// flow only exists live: on historical bars drop its weight so past and live scores stay comparable
			if (State != State.Realtime) w[6] = 0;
			double wsum = w.Sum(); if (wsum <= 0) wsum = 1;
			double scoreL = 0, scoreS = 0;
			for (int i = 0; i < factors.Count && i < w.Length; i++) { scoreL += w[i] * factors[i].Long; scoreS += w[i] * factors[i].Short; }
			scoreL = 100 * scoreL / wsum; scoreS = 100 * scoreS / wsum;

			string dir = "None"; double best = 0;
			if (atLevel || brokeUp || brokeDown)
			{
				// position in the wall range decides; a 2-ATR extension only widens the allowed zone a little.
				// A break bypasses location only when it is a real break: short-gamma regime, bar range >= 0.8 ATR,
				// close in the outer quarter of the bar. Weak pokes through a level at the wrong end of the range do not.
				bool strongBar = range >= 0.8 * atrV;
				bool strongUp = brokeUp && shortGammaRegime && strongBar && Close[sk] >= High[sk] - 0.25 * range;
				bool strongDown = brokeDown && shortGammaRegime && strongBar && Close[sk] <= Low[sk] + 0.25 * range;
				bool longOk = !LocationFilter || strongUp || reclaimLong || locPos <= 0.5 || (locFromHigh >= 2 && locPos <= 0.7);
				bool shortOk = !LocationFilter || strongDown || reclaimShort || locPos >= 0.5 || (locFromLow >= 2 && locPos >= 0.3);
				if (scoreL >= scoreS && scoreL >= SignalThreshold && longOk) { dir = "LONG"; best = scoreL; }
				else if (scoreS >= SignalThreshold && shortOk && (scoreS > scoreL || !longOk)) { dir = "SHORT"; best = scoreS; }
			}

			// Level-to-level target: the next level beyond price in the signal's direction.
			string target = "";
			if (dir != "None")
			{
				Level tgt = null;
				foreach (Level l in levels)
				{
					if (near != null && l == near) continue;
					if (dir == "LONG" && l.Price > price + tol && (tgt == null || l.Price < tgt.Price)) tgt = l;
					if (dir == "SHORT" && l.Price < price - tol && (tgt == null || l.Price > tgt.Price)) tgt = l;
				}
				// long gamma: price is pulled back to Zero Gamma, so prefer it over closer minor levels when it lies in the trade's direction
				if (PreferZgTargetLongGamma && !shortGammaRegime && !double.IsNaN(zg) && (near == null || near.Name != "Zero Gamma")
					&& ((dir == "LONG" && zg > price + tol) || (dir == "SHORT" && zg < price - tol)))
					tgt = new Level { Name = "Zero Gamma", Price = zg };
				if (tgt != null) target = string.Format("{0} {1:N2} ({2:+0.00;-0.00})", tgt.Name, tgt.Price, tgt.Price - price);
			}

			lock (dataLock)
			{
				lastFactors = factors; lastScoreLong = scoreL; lastScoreShort = scoreS;
				lastDirection = dir; lastLevel = near; lastRegime = regime; lastTarget = target; lastMagnet = magnet;
			}

			if (ExportEnabled)
			{
				try
				{
					if (exportFile == null) ExportInit();
					if (exportedTimes.Contains(Time[sk])) goto exportDone;
					exportedTimes.Add(Time[sk]);
					double zd = 0, zv = 0;
					if (near != null && State == State.Realtime) ZoneFlow(near.Price, tol * 2, out zd, out zv);
					ExportRow(near, zg, regime, atLevel, brokeUp, brokeDown, factors, lastBarDelta, zd, zv, scoreL, scoreS, dir, atrV, sessionVwap, p.NetGexVol);
				}
				catch (Exception ex) { lastProblem = "export: " + ex.Message; }
			}
			exportDone:
			scoreSeries[sk] = dir == "LONG" ? scoreL : dir == "SHORT" ? -scoreS : 0;

			// â”€â”€ setup / trade engine â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
			int triggeredBefore = setup != null && setup.Active ? setup.TriggerBar : -1;
			FollowTarget(setup, levels, price, tick);
			FollowTarget(shadow, levels, price, tick);
			if (!tickMode) ManageSetup(tick);
			bool candleOk = !RequireCandleAgreement || range <= 0 ||
				(dir == "LONG" ? (Close[sk] - Low[sk]) / range >= 0.5 : (High[sk] - Close[sk]) / range >= 0.5);
			// Zero Gamma rejection: in short-gamma regime an open trade that runs into the ZG trail and gets rejected
			// on the bar close is closed (Exit) or closed and flipped into a pending setup the other way (Flip).
			if (ZgRule != NinjaGexSigZgRule.Off && !double.IsNaN(zg) && shortGammaRegime && setup != null && setup.Active && !setup.Closed)
			{
				bool longRejected = setup.Dir == "LONG" && High[sk] >= zg - tol && Close[sk] < zg - 0.5 * tol && Close[sk] < High[sk] - 0.3 * range;
				bool shortRejected = setup.Dir == "SHORT" && Low[sk] <= zg + tol && Close[sk] > zg + 0.5 * tol && Close[sk] > Low[sk] + 0.3 * range;
				if (longRejected || shortRejected)
				{
					string oldDir = setup.Dir;
					CloseSetup(setup, Close[sk], "ZG rejection " + (ZgRule == NinjaGexSigZgRule.Flip ? "flip" : "exit"));
					if (ZgRule == NinjaGexSigZgRule.Flip)
					{
						dir = oldDir == "LONG" ? "SHORT" : "LONG";
						best = Math.Max(best, SignalThreshold);
						near = new Level { Name = "Zero Gamma", Price = zg, IsSupport = dir == "LONG" };
						lastSignalBar = -1000;   // let the flip through the cooldown
					}
				}
			}
			// a qualified signal in the opposite direction closes an open trade / replaces a pending setup
			if (dir != "None" && candleOk && setup != null && !setup.Closed && setup.Dir != dir)
				CloseSetup(setup, setup.Active ? Close[sk] : double.NaN, setup.Active ? "reversed" : "replaced");
			int hhmm = Time[sk].Hour * 100 + Time[sk].Minute;
			bool inSession = !UseSessionFilter || (SessionStart <= SessionEnd ? (hhmm >= SessionStart && hhmm < SessionEnd) : (hhmm >= SessionStart || hhmm < SessionEnd));
			if (!inSession) dir = "None";
			// news lock: no new setups from NewsLockMinutes before to NewsLockMinutes after a High-impact USD event (chart time)
			string newsLock = "";
			if (UseNewsLock)
			{
				List<NewsEvt> ne; lock (dataLock) { ne = newsAll; }
				foreach (NewsEvt e in ne)
					if (e.Impact == "High" && Math.Abs((Time[sk] - e.T).TotalMinutes) <= NewsLockMinutes) { newsLock = e.Title + " " + e.T.ToString("HH:mm"); break; }
				if (newsLock.Length > 0 && dir != "None") { dir = "None"; }
			}
			newsLockText = newsLock;
			// PropTraderz levels can be limited to confluence / targets only (no entries fired AT them)
			if (dir != "None" && !PtzEntries && near != null && near.Name.StartsWith("PTZ ")) dir = "None";
			int sinceSignal = (CurrentBar - sk) - lastSignalBar;
			bool cooled = sinceSignal >= Math.Max(1, CooldownBars) || (dir != lastSignalDir && sinceSignal >= 1);
			if (dir != "None" && candleOk && (setup == null || setup.Closed) && cooled)
			{
				lastSignalBar = (CurrentBar - sk); lastSignalDir = dir;
				var su = new Setup { Dir = dir, SignalBar = (CurrentBar - sk), SignalTime = Time[sk], Score = best, LevelName = near != null ? near.Name : "",
					SigHigh = High[sk], SigLow = Low[sk], LiveSignal = State == State.Realtime };
				double buf = Math.Max(Math.Max(0, StopBufferTicks) * tick, StopBufferAtr * atrV);
				if (dir == "LONG") { su.Trigger = High[sk] + tick; su.Stop = Low[sk] - buf; }
				else { su.Trigger = Low[sk] - tick; su.Stop = High[sk] + buf; }
				su.StopInit = su.Stop;
				// target: next level in direction at least MinRewardR risks away, else an R multiple
				double riskPts = Math.Abs(su.Trigger - su.Stop);
				// Room filter: the first strong level in the trade's direction is an obstacle, not just "a target that is too close".
				// If it sits closer than RoomMinR risks from the trigger, the trade has no room (short into a put wall 3 pts below,
				// long one point under a plan short level) and is skipped. Logged as a setup not taken, with the distance.
				if (UseWallMigration && ((dir == "SHORT" && migBlockShort) || (dir == "LONG" && migBlockLong)))
				{
					// exception: a strong reversal bar off the level (>= 0.6 ATR, close in the outer third in the trade's direction) -
					// without it the rule would have blocked the two best longs on record (put-wall reversals after the FOMC flush)
					bool strongReversal = range >= 0.6 * atrV && (dir == "LONG" ? Close[sk] >= High[sk] - range / 3.0 : Close[sk] <= Low[sk] + range / 3.0);
					if (!strongReversal)
					{
						su.LevelName = near != null ? near.Name : ""; su.Target = su.Trigger; su.TargetName = ""; su.Why = regime + "; at " + su.LevelName; su.TargetWhy = migNote;
						CloseSetup(su, Close[sk], "skipped: " + migNote);
						lastSignalText = string.Format("{0} {1:0} @ {2} {3} SKIPPED: {4}", dir, best, su.LevelName, Time[sk].ToString("HH:mm"), migNote);
						lastSignalBar = -1000;
						goto setupDone;
					}
				}
				string roomNote = "";
				if (riskPts > 0)
				{
					Level block = null; double blockDist = double.MaxValue;
					foreach (Level l in levels)
					{
						// 09-18: with ~25 levels on the chart something is always within 0.8R; G-levels, OI walls, prior-day, session and
						// plan marks blocked four good setups in one morning. Only structural gamma levels are obstacles.
						if (l == near || !IsRelayLevel(l.Name) || l.Name.EndsWith(" OI")) continue;
						double d = dir == "LONG" ? l.Price - su.Trigger : su.Trigger - l.Price;
						if (d <= 0 || d >= blockDist) continue;
						blockDist = d; block = l;
					}
					if (block != null)
					{
						roomNote = string.Format("room {0:0.0}R to {1} {2:N2}", blockDist / riskPts, block.Name, block.Price);
						if (UseRoomFilter && blockDist < RoomMinR * riskPts)
						{
							su.LevelName = near != null ? near.Name : ""; su.Target = block.Price; su.TargetName = block.Name;
							su.Why = regime + "; at " + su.LevelName; su.TargetWhy = roomNote;
							CloseSetup(su, Close[sk], string.Format("skipped: no room - {0} only {1:0.00} pts away = {2:0.0}R (need {3:0.0}R)", block.Name, blockDist, blockDist / riskPts, RoomMinR));
							lastSignalText = string.Format("{0} {1:0} @ {2} {3} SKIPPED: no room, {4} {5:0.00} pts away ({6:0.0}R)", dir, best, su.LevelName, Time[sk].ToString("HH:mm"), block.Name, blockDist, blockDist / riskPts);
							lastSignalBar = -1000;   // a skipped setup is not a trade: no cooldown
							goto setupDone;
						}
					}
				}
				// position size from fixed dollar risk: contracts = risk$ / (stop points x $ per point), at least 1
				double ptVal = Instrument.MasterInstrument.PointValue;
				su.Qty = RiskDollars > 0 && riskPts > 0 && ptVal > 0 ? Math.Max(1, (int)Math.Floor(RiskDollars / (riskPts * ptVal))) : 1;
				Level tl = null;
				foreach (Level l in levels)
				{
					if (near != null && l == near) continue;
					if (dir == "LONG" && l.Price > su.Trigger + riskPts * MinRewardR && (tl == null || l.Price < tl.Price)) tl = l;
					if (dir == "SHORT" && l.Price < su.Trigger - riskPts * MinRewardR && (tl == null || l.Price > tl.Price)) tl = l;
				}
				if (PreferZgTargetLongGamma && !shortGammaRegime && !double.IsNaN(zg) && (near == null || near.Name != "Zero Gamma")
					&& ((dir == "LONG" && zg > su.Trigger + riskPts * MinRewardR) || (dir == "SHORT" && zg < su.Trigger - riskPts * MinRewardR)))
				{
					su.Target = zg; su.TargetName = "Zero Gamma";
					su.TargetWhy = string.Format("long gamma: Zero Gamma {0:N2} preferred as the magnet = {1:0.0}R{2}", zg, Math.Abs(zg - su.Trigger) / Math.Max(tick, riskPts), tl != null ? " (next level was " + tl.Name + " " + tl.Price.ToString("N2") + ")" : "");
				}
				else if (tl != null) { su.Target = tl.Price; su.TargetName = tl.Name; su.TargetWhy = string.Format("next level {0} {1:N2} = {2:0.0}R (first level at least {3}R away)", tl.Name, tl.Price, Math.Abs(tl.Price - su.Trigger) / Math.Max(tick, riskPts), MinRewardR); }
				else { su.Target = dir == "LONG" ? su.Trigger + riskPts * RiskRewardIfNoLevel : su.Trigger - riskPts * RiskRewardIfNoLevel; su.TargetName = RiskRewardIfNoLevel + "R"; su.TargetWhy = string.Format("no level at least {0}R away in that direction -> fixed {1}R", MinRewardR, RiskRewardIfNoLevel); }
				// never let a far level hold the trade hostage: cap the target at MaxRewardR risks
				double capPx = dir == "LONG" ? su.Trigger + riskPts * MaxRewardR : su.Trigger - riskPts * MaxRewardR;
				if ((dir == "LONG" && su.Target > capPx) || (dir == "SHORT" && su.Target < capPx)) { su.TargetWhy = string.Format("{0} {1:N2} is beyond {2}R -> capped at {2}R", su.TargetName, su.Target, MaxRewardR); su.Target = capPx; su.TargetName = MaxRewardR + "R cap"; }
				// levels get front-run: a target ON the line needs price to trade through it (ES 09-17 missed G2 by one tick), so sit a little inside
				bool levelTarget = !su.TargetName.EndsWith("R") && !su.TargetName.EndsWith("R cap");
				if (levelTarget)
				{
					double tOff = Math.Max(Math.Max(0, TargetOffsetTicks) * tick, Math.Max(0, TargetOffsetAtr) * atrV);
					if (tOff > 0 && Math.Abs(su.Target - su.Trigger) > 3 * tOff)
					{
						su.Target = dir == "LONG" ? su.Target - tOff : su.Target + tOff; su.TargetOffset = tOff;
						su.TargetWhy += string.Format(" (front-run {0:0.00})", tOff);
					}
					if (FollowTargetLevel && IsRelayLevel(su.TargetName)) su.TargetLevel = su.TargetName;
				}
				su.Seq = ++setupSeq; lastStatusWrite = DateTime.MinValue;
				// why: the factors that carried the score, strongest first
				{
					var parts = new List<KeyValuePair<double, string>>();
					foreach (Factor fw in factors)
					{
						double v = dir == "LONG" ? fw.Long : fw.Short;
						if (v >= 0.3) parts.Add(new KeyValuePair<double, string>(v, fw.Name + " " + v.ToString("0.0", CultureInfo.InvariantCulture) + (string.IsNullOrEmpty(fw.Note) ? "" : " (" + fw.Note + ")")));
					}
					parts.Sort((a, b) => b.Key.CompareTo(a.Key));
					var sb = new StringBuilder(regime);
					if (near != null) sb.Append("; at ").Append(near.Name).Append(" ").Append(near.Price.ToString("N2"));
					for (int i = 0; i < parts.Count && i < 4; i++) sb.Append("; ").Append(parts[i].Value);
					if (roomNote.Length > 0) sb.Append("; ").Append(roomNote);
					sb.Append("; vol x").Append(volRatio.ToString("0.0", CultureInfo.InvariantCulture));
					su.Why = sb.ToString().Replace(',', ' ').Replace((char)13, ' ').Replace((char)10, ' ');
				}
				if (!EntryOnBreak) { su.Active = true; su.Entry = Close[sk]; su.TriggerBar = (CurrentBar - sk); su.TriggerTime = Time[sk]; su.Trigger = Close[sk]; }
				setup = su;
				if (EntryOnBreak && TrackDiamondEntries)
				{
					// twin trade entered at the diamond (signal bar close), same stop/target, tracked to *_trades_diamond.csv
					double dRisk = Math.Abs(Close[sk] - su.Stop);
					shadow = new Setup { Dir = dir, Shadow = true, Active = true, LiveSignal = su.LiveSignal, TargetLevel = su.TargetLevel, TargetOffset = su.TargetOffset, SignalBar = su.SignalBar, SignalTime = su.SignalTime, Score = best, LevelName = su.LevelName,
						SigHigh = su.SigHigh, SigLow = su.SigLow, Trigger = Close[sk], Entry = Close[sk], TriggerBar = su.SignalBar, TriggerTime = su.SignalTime,
						Stop = su.Stop, StopInit = su.Stop, Target = su.Target, TargetName = su.TargetName, Why = su.Why, TargetWhy = su.TargetWhy,
						Qty = RiskDollars > 0 && dRisk > 0 && ptVal > 0 ? Math.Max(1, (int)Math.Floor(RiskDollars / (dRisk * ptVal))) : 1 };
				}
				lastSignalText = string.Format("{0} {1:0} @ {2} {3:N2}  {4}  trig {5:N2} stop {6:N2} tgt {7} {8:N2}", dir, best, su.LevelName, price, Time[sk].ToString("HH:mm"),
					su.Trigger, su.Stop, su.TargetName, su.Target);
				if (!EntryOnBreak && ShowArrows) DrawEntryArrow(su, tick);
				if (AlertsOn && State == State.Realtime)
				{
					try
					{
						Alert("NGSsetup" + (CurrentBar - sk), Priority.Medium, "NinjaGexSignal " + upstreamSymbol + " SETUP " + lastSignalText,
							NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert2.wav", 10, System.Windows.Media.Brushes.Black, dir == "LONG" ? ClrLongSignal : ClrShortSignal);
					}
					catch (Exception ex) { lastProblem = "alert: " + ex.Message; }
				}
			}
			setupDone:

			// plots
			bool entryBar = setup != null && setup.Active && setup.TriggerBar == (CurrentBar - sk) && triggeredBefore != (CurrentBar - sk);
			Values[0][sk] = entryBar ? (setup.Dir == "LONG" ? 1 : -1) : 0;
			Values[1][sk] = setup != null && !setup.Closed ? (setup.Dir == "LONG" ? 1 : -1) : 0;
			Values[2][sk] = setup != null && !setup.Closed ? setup.Stop : 0;
			Values[3][sk] = setup != null && !setup.Closed ? setup.Target : 0;
			Values[4][sk] = scoreL - scoreS;
		}

		private void DrawEntryArrow(Setup su, double tick)
		{
			string tag = "NGS" + su.TriggerBar;
			int ago = Math.Max(0, CurrentBar - su.TriggerBar);
			if (su.Dir == "LONG") Draw.ArrowUp(this, tag, false, ago, Low[ago] - 2 * tick, ClrLongSignal);
			else Draw.ArrowDown(this, tag, false, ago, High[ago] + 2 * tick, ClrShortSignal);
		}

		// Advance the pending/active setup by one closed bar.
		private void ManageSetup(double tick)
		{
			ManageOne(setup, tick);
			ManageOne(shadow, tick);
		}

		private void ManageOne(Setup su, double tick)
		{
			if (su == null || su.Closed) return;
			bool isLong = su.Dir == "LONG";
			if (!su.Active)
			{
				bool invalid = isLong ? Low[sk] <= su.Stop : High[sk] >= su.Stop;
				bool hit = isLong ? High[sk] >= su.Trigger : Low[sk] <= su.Trigger;
				if (hit && !invalid)
				{
					su.Active = true; su.TriggerBar = (CurrentBar - sk); su.TriggerTime = Time[sk];
					su.Entry = isLong ? Math.Max(su.Trigger, Open[sk]) : Math.Min(su.Trigger, Open[sk]);   // a gap through the trigger fills at the open
					if (ShowArrows && !su.Shadow) DrawEntryArrow(su, tick);
					if (AlertsOn && State == State.Realtime && !su.Shadow)
						try { Alert("NGStrig" + (CurrentBar - sk), Priority.High, string.Format("NinjaGexSignal {0} {1} TRIGGERED @ {2:N2}  stop {3:N2}  target {4} {5:N2}", upstreamSymbol, su.Dir, su.Entry, su.Stop, su.TargetName, su.Target),
							NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert1.wav", 10, System.Windows.Media.Brushes.Black, isLong ? ClrLongSignal : ClrShortSignal); }
						catch (Exception) { }
					if (isLong ? Low[sk] <= su.Stop : High[sk] >= su.Stop) { CloseSetup(su, su.Stop, "stop (trigger bar)"); return; }
				}
				else if (invalid) CloseSetup(su, double.NaN, "cancelled: invalidated before trigger");
				else if ((CurrentBar - sk) - su.SignalBar >= Math.Max(1, TriggerBars)) CloseSetup(su, double.NaN, "cancelled: no trigger");
				return;
			}
			double fav = isLong ? High[sk] - su.Entry : su.Entry - Low[sk];
			double adv = isLong ? su.Entry - Low[sk] : High[sk] - su.Entry;
			su.MaxFav = Math.Max(su.MaxFav, fav); su.MaxAdv = Math.Max(su.MaxAdv, adv);
			bool stopHit = isLong ? Low[sk] <= su.Stop : High[sk] >= su.Stop;
			bool tgtHit = isLong ? High[sk] >= su.Target : Low[sk] <= su.Target;
			if (stopHit && tgtHit) { CloseSetup(su, su.Stop, "stop (both hit)"); return; }
			if (stopHit) { CloseSetup(su, su.Stop, su.Stop == su.StopInit ? "stop" : "trail stop"); return; }
			if (tgtHit) { CloseSetup(su, su.Target, "target " + su.TargetName); return; }
			if ((CurrentBar - sk) - su.TriggerBar >= Math.Max(1, MaxBarsInTrade)) { CloseSetup(su, Close[sk], "time exit"); return; }
			double r = su.Risk > 0 ? su.Risk : tick;
			// profit lock: a trade that has been well past 1R must not come back to nothing (12 of 75 recorded trades did)
			if (LockAtR > 0 && su.MaxFav >= LockAtR * r) { double lk = isLong ? su.Entry + LockToR * r : su.Entry - LockToR * r; su.Stop = isLong ? Math.Max(su.Stop, lk) : Math.Min(su.Stop, lk); }
			if (StopMode == NinjaGexSigStopMode.BreakevenAt1R && su.MaxFav >= r)
			{
				double be = isLong ? su.Entry + BreakevenPlusTicks * tick : su.Entry - BreakevenPlusTicks * tick;
				su.Stop = isLong ? Math.Max(su.Stop, be) : Math.Min(su.Stop, be);
			}
			else if (StopMode == NinjaGexSigStopMode.AtrTrail)
			{
				double trail = Math.Max(0.25, TrailAtr) * (atr[sk] > 0 ? atr[sk] : r);
				if (isLong) su.Stop = Math.Max(su.Stop, Close[sk] - trail); else su.Stop = Math.Min(su.Stop, Close[sk] + trail);
			}
		}

		private void CloseSetup(Setup su, double exitPx, string reason)
		{
			lastStatusWrite = DateTime.MinValue;   // state change: the next WriteStatus goes out immediately
			su.Closed = true; su.Reason = reason; su.ExitBar = (CurrentBar - sk); su.ExitTime = Time[sk]; su.Exit = exitPx;
			if (su.Shadow)
			{
				try { ExportTrade(su, su.Active ? su.Pts(exitPx) : 0); } catch (Exception ex) { lastProblem = "shadow export: " + ex.Message; }
				return;
			}
			if (!su.Active)
			{
				// never triggered: no trade happened, so the next qualifying bar (same direction too) may set up again
				if (su == setup) lastSignalBar = -1000;
				lock (dataLock) { closedSetups.Add(su); if (closedSetups.Count > 200) closedSetups.RemoveAt(0); }
				try { ExportTrade(su, 0); } catch (Exception ex) { lastProblem = "trade export: " + ex.Message; }
				return;
			}
			if (su.Active)
			{
				double pts = su.Pts(exitPx);
				lock (dataLock) { closedSetups.Add(su); if (closedSetups.Count > 200) closedSetups.RemoveAt(0); }
				// the day tally is the REAL book: only trades whose signal bar was scored live (replays after a reload go to the file as "hist")
				if (su.LiveSignal) { dayPts += pts; dayDollars += pts * su.Qty * Instrument.MasterInstrument.PointValue; dayTrades++; if (pts > 0) dayWins++; }
				try { ExportTrade(su, pts); } catch (Exception ex) { lastProblem = "trade export: " + ex.Message; }
				if (AlertsOn && State == State.Realtime)
					try { Alert("NGSexit" + (CurrentBar - sk), Priority.Medium, string.Format("NinjaGexSignal {0} {1} EXIT {2:N2}  {3:+0.00;-0.00} pts  ({4})", upstreamSymbol, su.Dir, exitPx, pts, reason),
						NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert3.wav", 10, System.Windows.Media.Brushes.Black, pts >= 0 ? ClrLongSignal : ClrShortSignal); }
					catch (Exception) { }
			}
		}

		// After a reload the panel's "Trades today" would only show the bar-close replay. The trades CSV keeps what
		// the live instance actually recorded, so rebuild the tally from it (live rows preferred over hist rows).
		private void LoadDayTallyFromCsv()
		{
			if (!ExportEnabled) return;
			if (exportFile == null) ExportInit();
			string dirPath = string.IsNullOrWhiteSpace(ExportFolder) ? DefaultExportDir : ExportFolder;
			string tf = BarsPeriod != null ? BarsPeriod.Value + BarsPeriod.BarsPeriodType.ToString() : "bars";
			string file = System.IO.Path.Combine(dirPath, "ngs_" + upstreamSymbol + "_" + tf + FileTag + "_trades.csv");
			if (!System.IO.File.Exists(file) || sessStartTime == DateTime.MinValue) return;
			var inv = CultureInfo.InvariantCulture;
			double pv = Instrument.MasterInstrument.PointValue;
			var live = new List<double[]>(); var hist = new List<double[]>();
			foreach (string line in System.IO.File.ReadLines(file))
			{
				string[] c = line.Split(',');
				if (c.Length < 14 || c[0].Length != 19 || string.IsNullOrEmpty(c[13])) continue;
				DateTime xt; double pts, qty = 1, dollars = double.NaN;
				if (!DateTime.TryParseExact(c[11], "yyyy-MM-dd HH:mm:ss", inv, DateTimeStyles.None, out xt) || xt < sessStartTime) continue;
				if (!double.TryParse(c[13], NumberStyles.Any, inv, out pts)) continue;
				if (c.Length > 19) double.TryParse(c[19], NumberStyles.Any, inv, out qty);
				if (c.Length > 20) double.TryParse(c[20], NumberStyles.Any, inv, out dollars);
				if (double.IsNaN(dollars)) dollars = pts * Math.Max(1, qty) * pv;
				if (c[2] == "live") live.Add(new[] { pts, dollars }); else hist.Add(new[] { pts, dollars });
			}
			var rows = live;   // live-signalled trades only; replayed ones never count toward the day
			dayPts = 0; dayDollars = 0; dayTrades = 0; dayWins = 0;
			foreach (double[] r in rows) { dayPts += r[0]; dayDollars += r[1]; dayTrades++; if (r[0] > 0) dayWins++; }
		}

		private readonly HashSet<string> exportedTrades = new HashSet<string>();
		private void ExportTrade(Setup su, double pts)
		{
			if (!ExportEnabled) return;
			if (exportFile == null) ExportInit();
			var inv = CultureInfo.InvariantCulture;
			if (tradeFile == null)
			{
				tradeFile = exportFile.Replace(".csv", "_trades.csv");
				try
				{
					foreach (string fpath in new[] { tradeFile, tradeFile.Replace("_trades.csv", "_trades_diamond.csv") })
						if (System.IO.File.Exists(fpath))
							foreach (string line in System.IO.File.ReadLines(fpath))
							{
								string[] c = line.Split(',');
								if (c.Length > 3 && c[0].Length == 19) exportedTrades.Add(c[0] + "|" + c[3] + (fpath == tradeFile ? "" : "|D"));
							}
				}
				catch (Exception) { }
			}
			string key = su.SignalTime.ToString("yyyy-MM-dd HH:mm:ss", inv) + "|" + su.Dir + (su.Shadow ? "|D" : "");
			if (exportedTrades.Contains(key)) return;
			exportedTrades.Add(key);
			bool taken = su.Active;
			string file = su.Shadow ? tradeFile.Replace("_trades.csv", "_trades_diamond.csv") : tradeFile;
			lock (exportLock)
			{
				bool fresh = !System.IO.File.Exists(file) || new System.IO.FileInfo(file).Length == 0;
				using (var w = new System.IO.StreamWriter(file, true, Encoding.UTF8))
				{
					if (fresh) w.WriteLine("signal_time,symbol,state,dir,level,score,trigger_time,entry,stop_init,target,target_name,exit_time,exit,result_pts,result_r,bars_held,max_fav,max_adv,reason,qty,result_usd,why,target_why");
					w.WriteLine(string.Join(",", su.SignalTime.ToString("yyyy-MM-dd HH:mm:ss", inv), upstreamSymbol, su.LiveSignal && State == State.Realtime ? "live" : "hist", su.Dir, su.LevelName.Replace(' ', '_'),
						su.Score.ToString("0", inv), taken ? su.TriggerTime.ToString("yyyy-MM-dd HH:mm:ss", inv) : "", taken ? su.Entry.ToString("0.##", inv) : su.Trigger.ToString("0.##", inv), su.StopInit.ToString("0.##", inv),
						su.Target.ToString("0.##", inv), su.TargetName.Replace(' ', '_'), su.ExitTime.ToString("yyyy-MM-dd HH:mm:ss", inv), taken ? su.Exit.ToString("0.##", inv) : "",
						taken ? pts.ToString("0.##", inv) : "", taken && su.Risk > 0 ? (pts / su.Risk).ToString("0.##", inv) : "", taken ? (su.ExitBar - su.TriggerBar).ToString(inv) : "",
						su.MaxFav.ToString("0.##", inv), su.MaxAdv.ToString("0.##", inv), su.Reason.Replace(',', ';'),
						su.Qty.ToString(inv), taken ? (pts * su.Qty * Instrument.MasterInstrument.PointValue).ToString("0", inv) : "",
						su.Why.Replace(',', ' '), su.TargetWhy.Replace(',', ' ')));
				}
			}
		}

		// â”€â”€ export (one CSV row per closed bar, for the Python recorder) â”€â”€â”€
		private static readonly object exportLock = new object();
		private string exportFile;
		private int exportRows;
		private const string ExportHeader =
			"time,symbol,state,bar,open,high,low,close,volume,atr,vwap,regime,zero_gamma,net_gex_vol,level,level_price,dist,at_level,broke_up,broke_down," +
			"f_regime_l,f_regime_s,f_level_l,f_level_s,f_struct_l,f_struct_s,f_pa_l,f_pa_s,f_vol_l,f_vol_s,f_cheat_l,f_cheat_s,f_flow_l,f_flow_s,f_loc_l,f_loc_s," +
			"bar_delta,zone_delta,zone_vol,score_long,score_short,direction";

		private void ExportInit()
		{
			string dirPath = string.IsNullOrWhiteSpace(ExportFolder) ? DefaultExportDir : ExportFolder;
			System.IO.Directory.CreateDirectory(dirPath);
			string tf = BarsPeriod != null ? BarsPeriod.Value + BarsPeriod.BarsPeriodType.ToString() : "bars";
			exportFile = System.IO.Path.Combine(dirPath, "ngs_" + upstreamSymbol + "_" + tf + FileTag + ".csv");
			// resume after the newest row already in the file, so a chart reload does not re-append history
			try
			{
				if (System.IO.File.Exists(exportFile))
				{
					foreach (string line in System.IO.File.ReadLines(exportFile))
					{
						int c = line.IndexOf(',');
						DateTime t;
						if (c == 19 && DateTime.TryParseExact(line.Substring(0, 19), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out t)) exportedTimes.Add(t);
					}
				}
			}
			catch (Exception) { }
		}

		private void ExportRow(Level near, double zg, string regime, bool atLevel, bool brokeUp, bool brokeDown, List<Factor> factors,
			double barDelta, double zoneDelta, double zoneVol, double scoreL, double scoreS, string dir, double atrV, double vwap, double netGex)
		{
			if (exportFile == null) ExportInit();
			var inv = CultureInfo.InvariantCulture;
			var sb = new StringBuilder();
			sb.Append(Time[sk].ToString("yyyy-MM-dd HH:mm:ss", inv)).Append(',').Append(upstreamSymbol).Append(',').Append(State == State.Realtime ? "live" : "hist").Append(',')
			  .Append((CurrentBar - sk)).Append(',').Append(Open[sk].ToString(inv)).Append(',').Append(High[sk].ToString(inv)).Append(',').Append(Low[sk].ToString(inv)).Append(',')
			  .Append(Close[sk].ToString(inv)).Append(',').Append(Volume[sk].ToString(inv)).Append(',').Append(atrV.ToString("0.####", inv)).Append(',').Append(vwap.ToString("0.##", inv)).Append(',')
			  .Append(regime.StartsWith("LONG") ? "long" : regime.StartsWith("SHORT") ? "short" : "na").Append(',')
			  .Append(double.IsNaN(zg) ? "" : zg.ToString("0.##", inv)).Append(',').Append(netGex.ToString("0", inv)).Append(',')
			  .Append(near != null ? near.Name.Replace(' ', '_') : "").Append(',').Append(near != null ? near.Price.ToString("0.##", inv) : "").Append(',')
			  .Append(near != null ? (Close[sk] - near.Price).ToString("0.##", inv) : "").Append(',')
			  .Append(atLevel ? 1 : 0).Append(',').Append(brokeUp ? 1 : 0).Append(',').Append(brokeDown ? 1 : 0);
			for (int i = 0; i < 8; i++)
			{
				double l = i < factors.Count ? factors[i].Long : 0, sh = i < factors.Count ? factors[i].Short : 0;
				sb.Append(',').Append(l.ToString("0.##", inv)).Append(',').Append(sh.ToString("0.##", inv));
			}
			sb.Append(',').Append(barDelta.ToString("0", inv)).Append(',').Append(zoneDelta.ToString("0", inv)).Append(',').Append(zoneVol.ToString("0", inv)).Append(',')
			  .Append(scoreL.ToString("0.#", inv)).Append(',').Append(scoreS.ToString("0.#", inv)).Append(',').Append(dir);
			lock (exportLock)
			{
				bool fresh = !System.IO.File.Exists(exportFile) || new System.IO.FileInfo(exportFile).Length == 0;
				using (var w = new System.IO.StreamWriter(exportFile, true, Encoding.UTF8))
				{
					if (fresh) w.WriteLine(ExportHeader);
					w.WriteLine(sb.ToString());
				}
				exportRows++;
			}
			if (exportRows == 1) Print("NinjaGexSignal: exporting to " + exportFile);
		}

		private static void AddLevel(List<Level> list, Snap s, string key, string name, bool support, bool wall, bool lg, bool sg)
		{
			double v;
			if (s.Majors.TryGetValue(key, out v) && v > 0)
				list.Add(new Level { Name = name, Price = v, IsSupport = support, IsWall = wall, IsLongGamma = lg, IsShortGamma = sg });
		}

		// â”€â”€ rendering â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
		protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			base.OnRender(chartControl, chartScale);
			List<Factor> factors; double sL, sS; string dir, regime; Level lvl; Snap p;
			lock (dataLock) { factors = lastFactors; sL = lastScoreLong; sS = lastScoreShort; dir = lastDirection; regime = lastRegime; lvl = lastLevel; p = primary; }

			if (ShowSetups)
			{
				try { DrawSetups(chartControl, chartScale); }
				catch (Exception ex) { lastProblem = "render setups: " + ex.Message; }
			}
			if (ShowGLevels)
			{
				try { DrawGLevels(chartScale); }
				catch (Exception ex) { lastProblem = "render G: " + ex.Message; }
			}
			if (UseSessionLevels && ShowSessionLevels)
			{
				try { DrawSessionLevels(chartScale); }
				catch (Exception ex) { lastProblem = "render sessions: " + ex.Message; }
			}
			if (UsePtz && ShowPtzLevels)
			{
				try { DrawPtzLevels(chartScale); }
				catch (Exception ex) { lastProblem = "render PTZ: " + ex.Message; }
			}
			if (ShowPanel)
			{
				try { DrawPanel(factors, sL, sS, dir, regime, lvl, p); }
				catch (Exception ex) { lastProblem = "render: " + ex.Message; }
			}

			string status = !string.IsNullOrEmpty(lastProblem) ? lastProblem : (p == null ? "waiting for data" : null);
			if (status != null)
			{
				try
				{
					using (var fmt = new SharpDX.DirectWrite.TextFormat(Core.Globals.DirectWriteFactory, "Arial", 12))
					using (var errBrush = System.Windows.Media.Brushes.OrangeRed.ToDxBrush(RenderTarget))
						RenderTarget.DrawText("NinjaGexSignal: " + status, fmt,
							new RectangleF(ChartPanel.X + 10, ChartPanel.Y + ChartPanel.H - 60, 600, 20), errBrush);
				}
				catch (Exception) { }
			}
		}

		// â”€â”€ draggable / collapsible panel â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
		private bool MouseInPanel(System.Windows.Input.MouseEventArgs e, out float x, out float y)
		{
			x = y = 0;
			if (ChartControl == null || ChartPanel == null) return false;
			System.Windows.Point pt = e.GetPosition(ChartControl);
			x = ChartingExtensions.ConvertToHorizontalPixels(pt.X, ChartControl.PresentationSource);
			y = ChartingExtensions.ConvertToVerticalPixels(pt.Y, ChartControl.PresentationSource);
			return panelRect.Width > 0 && x >= panelRect.X && x <= panelRect.X + panelRect.Width && y >= panelRect.Y && y <= panelRect.Y + panelRect.Height;
		}

		private void OnPanelMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
		{
			float x, y;
			if (!ShowPanel || !MouseInPanel(e, out x, out y)) return;
			if (e.ClickCount >= 2) { panelCollapsed = !panelCollapsed; e.Handled = true; ForceRefresh(); return; }
			// switch to absolute top-left placement so the offsets mean "where I dropped it"
			PanelCorner = NinjaGexCorner.TopLeft;
			PanelX = (int)(panelRect.X - ChartPanel.X); PanelY = (int)(panelRect.Y - ChartPanel.Y);
			dragDx = x - panelRect.X; dragDy = y - panelRect.Y;
			panelDragging = true; e.Handled = true;
		}

		private void OnPanelMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
		{
			if (!panelDragging) return;
			float x, y; MouseInPanel(e, out x, out y);
			PanelX = (int)Math.Max(0, x - dragDx - ChartPanel.X);
			PanelY = (int)Math.Max(0, y - dragDy - ChartPanel.Y);
			ForceRefresh();
		}

		private void OnPanelMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
		{
			if (!panelDragging) return;
			panelDragging = false; e.Handled = true; ForceRefresh();
		}

		private void DrawPanel(List<Factor> factors, double sL, double sS, string dir, string regime, Level lvl, Snap p)
		{
			var lines = new List<KeyValuePair<string, System.Windows.Media.Brush>>();
			var white = System.Windows.Media.Brushes.White;
			var gray = System.Windows.Media.Brushes.LightGray;
			lines.Add(new KeyValuePair<string, System.Windows.Media.Brush>(upstreamSymbol + "  " + Aggregation + "  " + (p != null ? "age " + (DateTime.UtcNow - p.FetchedUtc).TotalSeconds.ToString("0") + "s" : "no data"), white));
			lines.Add(new KeyValuePair<string, System.Windows.Media.Brush>("Regime: " + regime, regime.StartsWith("LONG") ? System.Windows.Media.Brushes.Cyan : regime.StartsWith("SHORT") ? System.Windows.Media.Brushes.MediumPurple : gray));
			if (p != null)
			{
				double zg = Zg(p);
				if (!double.IsNaN(zg)) lines.Add(new KeyValuePair<string, System.Windows.Media.Brush>("Zero Gamma " + zg.ToString("N2") + "  net GEX " + Compact(p.NetGexVol), gray));
			}
			if (lvl != null) lines.Add(new KeyValuePair<string, System.Windows.Media.Brush>("Nearest: " + lvl.Name + " " + lvl.Price.ToString("N2") + (lastMagnet ? "   [ZG MAGNET]" : ""), lastMagnet ? System.Windows.Media.Brushes.Yellow : gray));
			List<double[]> gls; lock (dataLock) { gls = lastGLevels; }
			if (gls != null && gls.Count > 0)
				lines.Add(new KeyValuePair<string, System.Windows.Media.Brush>("G-levels: " + string.Join("  ", gls.Select((g, i) => "G" + (i + 1) + " " + g[0].ToString("N0"))), gray));
			if (!string.IsNullOrEmpty(lastTarget)) lines.Add(new KeyValuePair<string, System.Windows.Media.Brush>("Target: " + lastTarget, white));
			foreach (Factor f in factors)
				lines.Add(new KeyValuePair<string, System.Windows.Media.Brush>(string.Format("{0,-12} L {1:0.0}  S {2:0.0}  {3}", f.Name, f.Long, f.Short, f.Note ?? ""), gray));
			lines.Add(new KeyValuePair<string, System.Windows.Media.Brush>(string.Format("Score  LONG {0:0}   SHORT {1:0}   -> {2}", sL, sS, dir),
				dir == "LONG" ? ClrLongSignal : dir == "SHORT" ? ClrShortSignal : white));
			if (volRatio >= HighVolRatio) lines.Add(new KeyValuePair<string, System.Windows.Media.Brush>(string.Format("VOLATILITY: ATR {0:0.0}x its normal - walls break more easily, fades are weaker (info only)", volRatio), System.Windows.Media.Brushes.Orange));
			if (!string.IsNullOrEmpty(migAdvice)) lines.Add(new KeyValuePair<string, System.Windows.Media.Brush>("ADVICE: " + migAdvice, System.Windows.Media.Brushes.Orange));
			if (!string.IsNullOrEmpty(newsLockText)) lines.Add(new KeyValuePair<string, System.Windows.Media.Brush>("NEWS LOCK: no new setups around " + newsLockText, System.Windows.Media.Brushes.Orange));
			if (!string.IsNullOrEmpty(lastSignalText)) lines.Add(new KeyValuePair<string, System.Windows.Media.Brush>("Last: " + lastSignalText, white));
			Setup su = setup;
			if (su != null && !su.Closed)
			{
				var c = su.Dir == "LONG" ? ClrLongSignal : ClrShortSignal;
				if (!su.Active)
				{
					double pvS = Instrument.MasterInstrument.PointValue;
					double riskUsd = Math.Abs(su.Trigger - su.Stop) * su.Qty * pvS, rewardUsd = Math.Abs(su.Target - su.Trigger) * su.Qty * pvS;
					lines.Add(new KeyValuePair<string, System.Windows.Media.Brush>(string.Format("SETUP {0} x{6}: buy/sell stop {1:N2}  stop {2:N2}  target {3} {4:N2}  ({5} bars left)",
						su.Dir, su.Trigger, su.Stop, su.TargetName, su.Target, Math.Max(0, TriggerBars - (lastBarIdx - su.SignalBar)), su.Qty), c));
					lines.Add(new KeyValuePair<string, System.Windows.Media.Brush>(string.Format("   if stopped -{0:$#,0} ({1:0.00} pts)   if target +{2:$#,0} ({3:0.00} pts)   {4:0.0}R",
						riskUsd, Math.Abs(su.Trigger - su.Stop), rewardUsd, Math.Abs(su.Target - su.Trigger), riskUsd > 0 ? rewardUsd / riskUsd : 0), c));
				}
				else
				{
					double lastPx = lastTradePx > 0 ? lastTradePx : su.Entry;
					double now = su.Pts(lastPx);
					double pv = Instrument.MasterInstrument.PointValue;
					lines.Add(new KeyValuePair<string, System.Windows.Media.Brush>(string.Format("TRADE {0} x{7} from {1:N2}: {2:+0.00;-0.00} pts = {8:+$#,0;-$#,0} ({3:+0.0;-0.0}R)  stop {4:N2}{5}  target {6:N2}",
						su.Dir, su.Entry, now, su.Risk > 0 ? now / su.Risk : 0, su.Stop, su.Stop != su.StopInit ? " (moved)" : "", su.Target, su.Qty, now * su.Qty * pv), c));
					lines.Add(new KeyValuePair<string, System.Windows.Media.Brush>(string.Format("   best +{0:0.00} pts (+{1:$#,0})   worst -{2:0.00} pts (-{3:$#,0})   {4} bars in",
						su.MaxFav, su.MaxFav * su.Qty * pv, su.MaxAdv, su.MaxAdv * su.Qty * pv, Math.Max(0, lastBarIdx - su.TriggerBar)), c));
					double atStop = su.Pts(su.Stop) * su.Qty * pv, atTarget = su.Pts(su.Target) * su.Qty * pv;
					lines.Add(new KeyValuePair<string, System.Windows.Media.Brush>(string.Format("   if stopped {0:+$#,0;-$#,0} ({1:+0.00;-0.00} pts)   if target {2:+$#,0;-$#,0} ({3:+0.00;-0.00} pts)",
						atStop, su.Pts(su.Stop), atTarget, su.Pts(su.Target)), c));
				}
			}
			if (dayTrades > 0)
				lines.Add(new KeyValuePair<string, System.Windows.Media.Brush>(string.Format("Trades today (live only): {0}  wins {1}  net {2:+0.00;-0.00} pts = {3:+$#,0;-$#,0}", dayTrades, dayWins, dayPts, dayDollars), dayPts >= 0 ? ClrLongSignal : ClrShortSignal));
			lines.Add(new KeyValuePair<string, System.Windows.Media.Brush>(
				ExportEnabled ? string.Format("Export: {0} rows -> {1}", exportRows, exportFile ?? "(not started)") : "Export: off", gray));

			if (panelCollapsed)
			{
				var scoreLine = lines.FirstOrDefault(k => k.Key.StartsWith("Score"));
				lines = new List<KeyValuePair<string, System.Windows.Media.Brush>> { lines[0], scoreLine.Key != null ? scoreLine : lines[0] };
				lines[0] = new KeyValuePair<string, System.Windows.Media.Brush>(lines[0].Key + "   (double-click to expand)", lines[0].Value);
			}
			float fontSize = Math.Max(8f, (float)PanelTextSize);
			float rowH = fontSize + 5f;
			using (var fmt = new SharpDX.DirectWrite.TextFormat(Core.Globals.DirectWriteFactory, "Consolas", fontSize))
			{
				float maxW = 0;
				foreach (var kv in lines)
					using (var lay = new SharpDX.DirectWrite.TextLayout(Core.Globals.DirectWriteFactory, kv.Key, fmt, 900, rowH))
						maxW = Math.Max(maxW, lay.Metrics.Width);
				float boxW = maxW + 16, boxH = lines.Count * rowH + 10;
				bool leftSide = PanelCorner == NinjaGexCorner.TopLeft || PanelCorner == NinjaGexCorner.BottomLeft;
				bool topSide = PanelCorner == NinjaGexCorner.TopLeft || PanelCorner == NinjaGexCorner.TopRight;
				float bx = leftSide ? ChartPanel.X + PanelX : ChartPanel.X + ChartPanel.W - boxW - PanelX;
				float by = topSide ? ChartPanel.Y + PanelY : ChartPanel.Y + ChartPanel.H - boxH - PanelY;
				// keep the whole box inside the chart panel, whatever the offsets say
				bx = Math.Max(ChartPanel.X, Math.Min(bx, ChartPanel.X + ChartPanel.W - boxW));
				by = Math.Max(ChartPanel.Y, Math.Min(by, ChartPanel.Y + ChartPanel.H - boxH));
				panelRect = new RectangleF(bx, by, boxW, boxH);
				using (var bg = System.Windows.Media.Brushes.Black.ToDxBrush(RenderTarget))
				using (var edge = (panelDragging ? System.Windows.Media.Brushes.Yellow : System.Windows.Media.Brushes.DimGray).ToDxBrush(RenderTarget))
				{
					bg.Opacity = 0.75f;
					RenderTarget.FillRectangle(panelRect, bg);
					RenderTarget.DrawRectangle(panelRect, edge, 1f);
					float y = by + 5;
					foreach (var kv in lines)
					{
						using (var br = (kv.Value ?? white).ToDxBrush(RenderTarget))
							RenderTarget.DrawText(kv.Key, fmt, new RectangleF(bx + 8, y, boxW - 8, rowH), br);
						y += rowH;
					}
				}
			}
		}

		// Entry / stop / target lines for the live setup and the recent closed trades.
		private void DrawSetups(ChartControl chartControl, ChartScale chartScale)
		{
			var list = new List<Setup>();
			List<Setup> all;
			lock (dataLock) { all = new List<Setup>(closedSetups); if (setup != null) all.Add(setup); list.AddRange(closedSetups.Where(x => x.Active).Skip(Math.Max(0, closedSetups.Count(x => x.Active) - 12))); if (setup != null) list.Add(setup); }
			if (all.Count == 0 || ChartBars == null) return;
			// signal-bar markers: diamond for every setup; gray with an x when it never triggered
			using (var fmtM = new SharpDX.DirectWrite.TextFormat(Core.Globals.DirectWriteFactory, "Arial", SharpDX.DirectWrite.FontWeight.Bold, SharpDX.DirectWrite.FontStyle.Normal, 9f))
			using (var gray = System.Windows.Media.Brushes.Silver.ToDxBrush(RenderTarget))
			using (var lg = (ClrLongSignal ?? System.Windows.Media.Brushes.Lime).ToDxBrush(RenderTarget))
			using (var sh = (ClrShortSignal ?? System.Windows.Media.Brushes.Red).ToDxBrush(RenderTarget))
			{
				double tk = Instrument.MasterInstrument.TickSize;
				foreach (Setup su in all)
				{
					if (su.SignalBar < ChartBars.FromIndex || su.SignalBar > ChartBars.ToIndex) continue;
					float x = chartControl.GetXByBarIndex(ChartBars, su.SignalBar);
					bool isLong = su.Dir == "LONG";
					float y = chartScale.GetYByValue(isLong ? su.SigLow - 3 * tk : su.SigHigh + 3 * tk) + (isLong ? 6f : -6f);
					bool cancelled = su.Closed && !su.Active;
					var br = cancelled ? gray : (isLong ? lg : sh);
					br.Opacity = cancelled ? 0.7f : 1f;
					var d = new SharpDX.Vector2[] { new Vector2(x, y - 5), new Vector2(x + 5, y), new Vector2(x, y + 5), new Vector2(x - 5, y) };
					for (int i = 0; i < 4; i++) RenderTarget.DrawLine(d[i], d[(i + 1) % 4], br, 1.2f);
					if (cancelled) RenderTarget.DrawText("x", fmtM, new RectangleF(x - 3, y - 7, 10, 12), gray);
					else if (!su.Active) RenderTarget.DrawText("?", fmtM, new RectangleF(x - 3, y - 7, 10, 12), br);
				}
			}
			using (var g = System.Windows.Media.Brushes.Gray.ToDxBrush(RenderTarget))
			using (var r = (ClrShortSignal ?? System.Windows.Media.Brushes.Red).ToDxBrush(RenderTarget))
			using (var gr = (ClrLongSignal ?? System.Windows.Media.Brushes.Lime).ToDxBrush(RenderTarget))
			using (var fmt = new SharpDX.DirectWrite.TextFormat(Core.Globals.DirectWriteFactory, "Arial", 9f))
			{
				foreach (Setup su in list)
				{
					int b0 = su.Active ? su.TriggerBar : su.SignalBar;
					int b1 = su.Closed ? su.ExitBar : ChartBars.ToIndex;
					if (b1 < ChartBars.FromIndex || b0 > ChartBars.ToIndex) continue;
					float x0 = chartControl.GetXByBarIndex(ChartBars, Math.Max(b0, ChartBars.FromIndex));
					float x1 = chartControl.GetXByBarIndex(ChartBars, Math.Min(b1, ChartBars.ToIndex)) + (su.Closed ? 0 : 40);
					float yE = chartScale.GetYByValue(su.Active ? su.Entry : su.Trigger), yS = chartScale.GetYByValue(su.Stop), yT = chartScale.GetYByValue(su.Target);
					g.Opacity = su.Closed ? 0.35f : 0.9f; r.Opacity = g.Opacity; gr.Opacity = g.Opacity;
					for (float x = x0; x < x1; x += 6f) RenderTarget.DrawLine(new Vector2(x, yE), new Vector2(Math.Min(x + 3f, x1), yE), g, 1f);
					RenderTarget.DrawLine(new Vector2(x0, yS), new Vector2(x1, yS), r, 1f);
					RenderTarget.DrawLine(new Vector2(x0, yT), new Vector2(x1, yT), gr, 1f);
					if (!su.Closed && su.Active && su.Risk > 0 && ShowRLines)
					{
						// 1R and 2R marks from the entry in the trade's direction (thin dotted, labelled), so breakeven and scale-out points are visible
						for (int k = 1; k <= 2; k++)
						{
							double rp = su.Dir == "LONG" ? su.Entry + k * su.Risk : su.Entry - k * su.Risk;
							float yR = chartScale.GetYByValue(rp);
							if (yR < ChartPanel.Y || yR > ChartPanel.Y + ChartPanel.H) continue;
							for (float x = x0; x < x1; x += 8f) RenderTarget.DrawLine(new Vector2(x, yR), new Vector2(Math.Min(x + 2f, x1), yR), gr, 1f);
							RenderTarget.DrawText(k + "R " + rp.ToString("N2"), fmt, new RectangleF(x1 + 2, yR - 6, 90, 12), gr);
						}
					}
					if (!su.Closed)
					{
						RenderTarget.DrawText((su.Active ? "entry " : "trigger ") + (su.Active ? su.Entry : su.Trigger).ToString("N2"), fmt, new RectangleF(x1 + 2, yE - 6, 90, 12), g);
						RenderTarget.DrawText("stop " + su.Stop.ToString("N2"), fmt, new RectangleF(x1 + 2, yS - 6, 90, 12), r);
						RenderTarget.DrawText("tgt " + su.Target.ToString("N2"), fmt, new RectangleF(x1 + 2, yT - 6, 90, 12), gr);
					}
					else if (su.Active)
					{
						double pts = su.Pts(su.Exit);
						using (var tb = (pts >= 0 ? ClrLongSignal : ClrShortSignal).ToDxBrush(RenderTarget))
							RenderTarget.DrawText(pts.ToString("+0.00;-0.00"), fmt, new RectangleF(x1 - 30, chartScale.GetYByValue(su.Exit) - 14, 60, 12), tb);
					}
				}
			}
		}

		// Thin dotted G1..Gn lines (manual dashes: StrokeStyle throws inside OnRender).
		private void DrawGLevels(ChartScale chartScale)
		{
			List<double[]> gls; lock (dataLock) { gls = lastGLevels; }
			if (gls == null || gls.Count == 0) return;
			float x1 = ChartPanel.X, x2 = ChartPanel.X + ChartPanel.W;
			using (var br = (ClrGLevel ?? System.Windows.Media.Brushes.Gold).ToDxBrush(RenderTarget))
			using (var fmt = new SharpDX.DirectWrite.TextFormat(Core.Globals.DirectWriteFactory, "Arial", 10f))
			{
				for (int i = 0; i < gls.Count; i++)
				{
					float y = chartScale.GetYByValue(gls[i][0]);
					if (y < ChartPanel.Y || y > ChartPanel.Y + ChartPanel.H) continue;
					br.Opacity = (float)(0.9 - 0.15 * i);
					for (float x = x1; x < x2; x += 7f)
						RenderTarget.DrawLine(new Vector2(x, y), new Vector2(Math.Min(x + 3f, x2), y), br, 1f);
					RenderTarget.DrawText("G" + (i + 1) + " " + gls[i][0].ToString("N0"), fmt, new RectangleF(x2 - 90, y - 13, 88, 12), br);
				}
			}
		}

		// PropTraderz plan levels: zones as translucent bands, single levels as dashed lines, labels on the left.
		// Asia / London / previous-NY session highs and lows: fine dotted lines, label at the right edge.
		private void DrawSessionLevels(ChartScale chartScale)
		{
			List<KeyValuePair<string, double>> sl; lock (dataLock) { sl = lastSessLevels; }
			if (sl == null || sl.Count == 0) return;
			float x1 = ChartPanel.X, x2 = ChartPanel.X + ChartPanel.W;
			using (var fmt = new SharpDX.DirectWrite.TextFormat(Core.Globals.DirectWriteFactory, "Arial", 9f))
			{
				foreach (var kv in sl)
				{
					Color4 c = kv.Key.StartsWith("Asia") ? new Color4(0.55f, 0.75f, 1f, 1f) : kv.Key.StartsWith("London") ? new Color4(0.6f, 1f, 0.6f, 1f) : new Color4(0.9f, 0.9f, 0.9f, 1f);
					float y = chartScale.GetYByValue(kv.Value);
					if (y < ChartPanel.Y || y > ChartPanel.Y + ChartPanel.H) continue;
					using (var br = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new Color4(c.Red, c.Green, c.Blue, 0.55f)))
					{
						for (float x = x1; x < x2; x += 6f)
							RenderTarget.DrawLine(new Vector2(x, y), new Vector2(Math.Min(x + 2f, x2), y), br, 1f);
						RenderTarget.DrawText(kv.Key + " " + kv.Value.ToString("N2"), fmt, new RectangleF(x2 - 430, y - 11, 130, 11), br);
					}
				}
			}
		}

		private void DrawPtzLevels(ChartScale chartScale)
		{
			List<PtzLevel> rows; lock (dataLock) { rows = lastPtz; }
			if (rows == null || rows.Count == 0) return;
			float x1 = ChartPanel.X, x2 = ChartPanel.X + ChartPanel.W;
			using (var fmt = new SharpDX.DirectWrite.TextFormat(Core.Globals.DirectWriteFactory, "Arial", 9f))
			{
				foreach (PtzLevel r in rows)
				{
					// explicit alpha in the colour: brush.Opacity is not honoured by every render path
					Color4 c = r.Kind == "long" ? new Color4(0f, 1f, 1f, 1f) : r.Kind == "short" ? new Color4(1f, 0.55f, 0f, 1f)
					         : r.Kind.StartsWith("pivot") ? new Color4(1f, 1f, 0f, 1f) : new Color4(1f, 0f, 1f, 1f);
					float yA = chartScale.GetYByValue(r.P1), yB = r.P2 > 0 ? chartScale.GetYByValue(r.P2) : yA;
					float top = Math.Min(yA, yB), bot = Math.Max(yA, yB);
					if (bot < ChartPanel.Y || top > ChartPanel.Y + ChartPanel.H) continue;
					if (r.P2 > 0)
					{
						using (var fill = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new Color4(c.Red, c.Green, c.Blue, 0.10f)))
							RenderTarget.FillRectangle(new RectangleF(x1, top, x2 - x1, Math.Max(1f, bot - top)), fill);
						using (var edge = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new Color4(c.Red, c.Green, c.Blue, 0.45f)))
						{
							RenderTarget.DrawLine(new Vector2(x1, top), new Vector2(x2, top), edge, 1f);
							RenderTarget.DrawLine(new Vector2(x1, bot), new Vector2(x2, bot), edge, 1f);
						}
					}
					else
					{
						using (var ln = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new Color4(c.Red, c.Green, c.Blue, 0.6f)))
							for (float x = x1; x < x2; x += 9f)
								RenderTarget.DrawLine(new Vector2(x, yA), new Vector2(Math.Min(x + 5f, x2), yA), ln, 1f);
					}
					using (var tb = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new Color4(c.Red, c.Green, c.Blue, 0.9f)))
					{
						string txt = "PTZ " + r.Name.Substring(4) + " " + (r.P2 > 0 ? r.P1.ToString("N0") + "-" + r.P2.ToString("N0") : r.P1.ToString("N0"));
						RenderTarget.DrawText(txt, fmt, new RectangleF(x2 - 300, top - 11, 200, 11), tb);
					}
				}
			}
		}

		private static string Compact(double v)
		{
			double a = Math.Abs(v); string sign = v >= 0 ? "+" : "-";
			if (a >= 1e6) return sign + (a / 1e6).ToString("0.#") + "M";
			if (a >= 1e3) return sign + (a / 1e3).ToString("0.#") + "K";
			return sign + a.ToString("0.#");
		}

		// â”€â”€ public series for strategies â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
		[Browsable(false), XmlIgnore]
		public Series<double> Score { get { return scoreSeries; } }
		[Browsable(false), XmlIgnore] public Series<double> Signal { get { return Values[0]; } }
		/// <summary>Current setup as the strategy needs it. State: 0 none, 1 pending, 2 in trade. Dir: +1 long, -1 short.</summary>
		/// <summary>Last time this instance processed a tick. A chart reload leaves the old instance in memory; it stops ticking.</summary>
		[Browsable(false), XmlIgnore] public DateTime AliveAt { get { return aliveAt; } }
		public void SnapshotSetup(out int seq, out int state, out int direction, out bool live, out double trigger, out double entry, out double stop, out double target, out string level)
		{
			Setup su = setup;
			if (su == null || su.Closed) { seq = su == null ? 0 : su.Seq; state = 0; direction = 0; live = false; trigger = entry = stop = target = 0; level = ""; return; }
			seq = su.Seq; state = su.Active ? 2 : 1; direction = su.Dir == "LONG" ? 1 : -1; live = su.LiveSignal;
			trigger = su.Trigger; entry = su.Entry; stop = su.Stop; target = su.Target; level = su.LevelName ?? "";
		}
		/// <summary>True when the current setup's signal bar was scored live (not rebuilt from history after a reload / strategy enable).</summary>
		[Browsable(false), XmlIgnore] public bool SetupIsLive { get { Setup su = setup; return su != null && !su.Closed && su.LiveSignal; } }
		[Browsable(false), XmlIgnore] public Series<double> SetupDir { get { return Values[1]; } }
		[Browsable(false), XmlIgnore] public Series<double> StopPrice { get { return Values[2]; } }
		[Browsable(false), XmlIgnore] public Series<double> TargetPrice { get { return Values[3]; } }
		[Browsable(false), XmlIgnore] public Series<double> NetScore { get { return Values[4]; } }

		// â”€â”€ properties â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
		[Display(Name = "Relay URL", Order = 0, GroupName = "01. Account & Data")] public string RelayUrl { get; set; }
		[Display(Name = "License key", Order = 1, GroupName = "01. Account & Data")] public string LicenseKey { get; set; }
		[Display(Name = "Symbol", Order = 2, GroupName = "01. Account & Data")] public NinjaGexSymbol SymbolChoice { get; set; }
		[Display(Name = "Aggregation", Order = 3, GroupName = "01. Account & Data")] public NinjaGexAggregation Aggregation { get; set; }
		[Display(Name = "Use ETF overlay (ES_SPY / NQ_QQQ)", Order = 4, GroupName = "01. Account & Data")] public bool UseEtfOverlay { get; set; }
		[Display(Name = "Use 90-day OI structure", Order = 5, GroupName = "01. Account & Data")] public bool UseStructural { get; set; }
		[Display(Name = "Overlay walls are levels too (SPY/QQQ walls can fire)", Order = 51, GroupName = "01. Account & Data")] public bool UseOverlayLevels { get; set; }
		[Range(2, 300), Display(Name = "Refresh (seconds)", Order = 6, GroupName = "01. Account & Data")] public int RefreshSeconds { get; set; }
		[Display(Name = "Zero Gamma weighting", Order = 7, GroupName = "01. Account & Data")] public NinjaGexSigZgSource ZgSource { get; set; }
		[Range(0, 30), Display(Name = "History days (past sessions to score)", Order = 8, GroupName = "01. Account & Data")] public int HistoryDays { get; set; }

		[Range(1, 100), Display(Name = "Level tolerance (ticks)", Order = 1, GroupName = "02. Level Detection")] public int LevelToleranceTicks { get; set; }
		[Range(2, 100), Display(Name = "ATR period", Order = 2, GroupName = "02. Level Detection")] public int AtrPeriod { get; set; }
		[Range(0, 2), Display(Name = "Level tolerance (x ATR, 0 = ticks only)", Description = "Tolerance = max(ticks, this x ATR). 0.25 ATR = 2 pts on ES, 11 pts on NQ.", Order = 21, GroupName = "02. Level Detection")] public double LevelToleranceAtr { get; set; }
		[Range(0, 3), Display(Name = "Wall agreement (x ATR, 0 = points only)", Order = 22, GroupName = "02. Level Detection")] public double WallAgreeAtr { get; set; }
		[Range(0, 1), Display(Name = "Stop buffer (x ATR, 0 = ticks only)", Order = 23, GroupName = "02. Level Detection")] public double StopBufferAtr { get; set; }
		[Range(0.25, 200), Display(Name = "Wall agreement (points)", Order = 3, GroupName = "02. Level Detection")] public double WallAgreePoints { get; set; }
		[Range(1, 10), Display(Name = "Volume spike multiple", Order = 4, GroupName = "02. Level Detection")] public double VolumeSpikeMult { get; set; }
		[Range(0.1, 0.95), Display(Name = "Rejection wick ratio", Order = 5, GroupName = "02. Level Detection")] public double WickRatio { get; set; }
		[Range(0, 5), Display(Name = "G-levels to use (0 = off)", Description = "Strongest strikes inside the expected move, ranked by gamma weight (video's G1..G3).", Order = 6, GroupName = "02. Level Detection")] public int GLevelCount { get; set; }
		[Display(Name = "Draw G-level lines", Order = 7, GroupName = "02. Level Detection")] public bool ShowGLevels { get; set; }
		[XmlIgnore, Display(Name = "G-level color", Order = 8, GroupName = "02. Level Detection")] public System.Windows.Media.Brush ClrGLevel { get; set; }
		[Browsable(false)] public string ClrGLevelS { get { return Serialize.BrushToString(ClrGLevel); } set { ClrGLevel = Serialize.StringToBrush(value); } }
		[Range(1, 100), Display(Name = "Zero Gamma magnet zone (ticks)", Description = "Within this distance of Zero Gamma continuation scores are cut to 30% (chop zone).", Order = 9, GroupName = "02. Level Detection")] public int MagnetTicks { get; set; }
		[Display(Name = "Gold: use 90-day chain for 0DTE", Order = 10, GroupName = "02. Level Detection")] public bool AutoGoldFull { get; set; }
		[Display(Name = "Level set for this chart", Description = "GexOnly: gamma, session and prior-day levels (PropTraderz levels are neither drawn nor scored). PtzOnly: only the PropTraderz plan levels can fire setups; this chart then writes its own files with _PTZ in the name. Both: the old mixed behaviour.", Order = 10, GroupName = "02. Level Detection")] public NinjaGexSigLevelSet LevelSet { get; set; }
		[Range(1, 10), Display(Name = "High-volatility notice at ATR ratio", Description = "ATR now divided by its 100-bar average. Shown on the panel, written to the why column and the status file. Information only - it blocks nothing yet.", Order = 20, GroupName = "02. Level Detection")] public double HighVolRatio { get; set; }
		[Browsable(false), Display(Name = "Use PropTraderz plan levels", Description = "Reads <export>\\ptz_levels.csv written by recorder\\ptz_plan.py (NQ and ES daily plan: long/short ideas, bull/bear pivots, strength/weakness).", Order = 11, GroupName = "02. Level Detection")] public bool UsePtzLevels { get; set; }
		[Display(Name = "Draw PropTraderz levels", Order = 12, GroupName = "02. Level Detection")] public bool ShowPtzLevels { get; set; }
		[Display(Name = "Allow entries at PropTraderz levels", Description = "ON: a PTZ level can be the level a setup fires from. OFF: PTZ levels are drawn, count as targets and confluence, but no setup is created AT them (gamma, session and prior-day levels only).", Order = 18, GroupName = "02. Level Detection")] public bool PtzEntries { get; set; }
		[Display(Name = "Use session high/low levels", Description = "Asia 18:00-03:00 and London 03:00-09:30 of the current overnight, plus the previous NY 09:30-16:00 session (chart time).", Order = 13, GroupName = "02. Level Detection")] public bool UseSessionLevels { get; set; }
		[Display(Name = "Draw session high/low lines", Order = 14, GroupName = "02. Level Detection")] public bool ShowSessionLevels { get; set; }
		[Display(Name = "Draw 1R / 2R lines on open trades", Order = 16, GroupName = "02. Level Detection")] public bool ShowRLines { get; set; }
		[Display(Name = "Draw setups (diamonds, entry / stop / target lines)", Description = "A strategy switches this off on the copy it hosts, otherwise the chart shows every setup twice.", Order = 19, GroupName = "02. Level Detection")] public bool ShowSetups { get; set; }
		[Display(Name = "Pick the level the bar touched (not nearest to close)", Description = "ON: a bar that tags a support at its low and closes back above it is scored at that support (mirror for resistance), even if it closes nearer another level. OFF: old behaviour, the level nearest the close.", Order = 17, GroupName = "02. Level Detection")] public bool LevelByTouch { get; set; }
		[Display(Name = "Force Calculate = On each tick", Description = "Overrides the instance's Calculate setting so intra-bar triggers and the live status file always run.", Order = 15, GroupName = "02. Level Detection")] public bool ForceOnEachTick { get; set; }

		[Range(0, 100), Display(Name = "Regime", Order = 1, GroupName = "03. Weights")] public double WRegime { get; set; }
		[Range(0, 100), Display(Name = "At level", Order = 2, GroupName = "03. Weights")] public double WWall { get; set; }
		[Range(0, 100), Display(Name = "Structure", Order = 3, GroupName = "03. Weights")] public double WStructure { get; set; }
		[Range(0, 100), Display(Name = "Price action", Order = 4, GroupName = "03. Weights")] public double WPriceAction { get; set; }
		[Range(0, 100), Display(Name = "Volume / flow", Order = 5, GroupName = "03. Weights")] public double WVolume { get; set; }
		[Range(0, 100), Display(Name = "Cheat sheet", Order = 6, GroupName = "03. Weights")] public double WCheatSheet { get; set; }
		[Range(0, 100), Display(Name = "Flow at level (delta, live only)", Order = 7, GroupName = "03. Weights")] public double WFlow { get; set; }
		[Range(1, 100000), Display(Name = "Flow: min volume at level", Order = 8, GroupName = "03. Weights")] public double FlowMinVolume { get; set; }
		[Range(0, 100), Display(Name = "Location (range position + day extension)", Order = 9, GroupName = "03. Weights")] public double WLocation { get; set; }
		[Display(Name = "Location filter (longs low in range, shorts high)", Description = "Blocks a LONG unless price is in the lower part of the Put-Wall..Call-Wall range or 2+ ATR off the session high (mirror for SHORT). Breaks bypass it.", Order = 10, GroupName = "03. Weights")] public bool LocationFilter { get; set; }
		[Display(Name = "Use prior-day high/low/close as levels", Order = 11, GroupName = "03. Weights")] public bool UsePriorDay { get; set; }
		[Range(5, 500), Display(Name = "Location: extension lookback (bars)", Order = 12, GroupName = "03. Weights")] public int LocationLookbackBars { get; set; }

		[Display(Name = "Show arrows", Order = 1, GroupName = "04. Signals")] public bool ShowArrows { get; set; }
		[Display(Name = "NT8 alerts", Order = 2, GroupName = "04. Signals")] public bool AlertsOn { get; set; }
		[Range(1, 100), Display(Name = "Signal threshold (0-100)", Order = 3, GroupName = "04. Signals")] public double SignalThreshold { get; set; }
		[Range(1, 200), Display(Name = "Cooldown (bars)", Order = 4, GroupName = "04. Signals")] public int CooldownBars { get; set; }
		[XmlIgnore, Display(Name = "Long color", Order = 5, GroupName = "04. Signals")] public System.Windows.Media.Brush ClrLongSignal { get; set; }
		[Browsable(false)] public string ClrLongSignalS { get { return Serialize.BrushToString(ClrLongSignal); } set { ClrLongSignal = Serialize.StringToBrush(value); } }
		[XmlIgnore, Display(Name = "Short color", Order = 6, GroupName = "04. Signals")] public System.Windows.Media.Brush ClrShortSignal { get; set; }
		[Browsable(false)] public string ClrShortSignalS { get { return Serialize.BrushToString(ClrShortSignal); } set { ClrShortSignal = Serialize.StringToBrush(value); } }

		[Display(Name = "Show panel", Order = 1, GroupName = "05. Panel")] public bool ShowPanel { get; set; }
		[Display(Name = "Corner", Order = 2, GroupName = "05. Panel")] public NinjaGexCorner PanelCorner { get; set; }
		[Range(0, 3000), Display(Name = "X offset", Order = 3, GroupName = "05. Panel")] public int PanelX { get; set; }
		[Range(0, 3000), Display(Name = "Y offset", Order = 4, GroupName = "05. Panel")] public int PanelY { get; set; }
		[Range(8, 24), Display(Name = "Text size", Order = 5, GroupName = "05. Panel")] public double PanelTextSize { get; set; }
		[Display(Name = "Draggable (drag to move, double-click to collapse)", Order = 6, GroupName = "05. Panel")] public bool PanelDraggable { get; set; }

		[Display(Name = "Entry on break of signal candle", Description = "Signal creates a pending setup; the arrow and entry come when a later bar trades through the signal bar's high (long) or low (short).", Order = 1, GroupName = "07. Setup & Trade")] public bool EntryOnBreak { get; set; }
		[Range(1, 50), Display(Name = "Bars allowed to trigger", Order = 2, GroupName = "07. Setup & Trade")] public int TriggerBars { get; set; }
		[Range(0, 100), Display(Name = "Stop buffer beyond signal candle (ticks)", Order = 3, GroupName = "07. Setup & Trade")] public int StopBufferTicks { get; set; }
		[Range(0.5, 5), Display(Name = "Min reward for a level target (R)", Description = "Next level must be at least this many risks away to be the target; otherwise the R-multiple target is used.", Order = 4, GroupName = "07. Setup & Trade")] public double MinRewardR { get; set; }
		[Range(0.5, 10), Display(Name = "Target when no level (R multiple)", Order = 5, GroupName = "07. Setup & Trade")] public double RiskRewardIfNoLevel { get; set; }
		[Display(Name = "Stop mode", Order = 6, GroupName = "07. Setup & Trade")] public NinjaGexSigStopMode StopMode { get; set; }
		[Range(0, 100), Display(Name = "Breakeven plus (ticks)", Description = "When the trade reaches 1R the stop goes to entry plus this many ticks in your favour, so a tagged stop still covers commission. 4 ticks = 1 point on ES/NQ.", Order = 61, GroupName = "07. Setup & Trade")] public int BreakevenPlusTicks { get; set; }
		[Range(0.25, 5), Display(Name = "ATR trail distance (x ATR)", Order = 7, GroupName = "07. Setup & Trade")] public double TrailAtr { get; set; }
		[Display(Name = "Signal candle must close with the trade", Description = "Long only if the signal bar closes in its upper half; short only if in its lower half.", Order = 8, GroupName = "07. Setup & Trade")] public bool RequireCandleAgreement { get; set; }
		[Range(1, 1000), Display(Name = "Max bars in trade", Order = 9, GroupName = "07. Setup & Trade")] public int MaxBarsInTrade { get; set; }
		[Display(Name = "Only take setups inside session window", Order = -2, GroupName = "07. Setup & Trade")] public bool UseSessionFilter { get; set; }
		[Display(Name = "News lock (no new setups around High-impact USD news)", Description = "Reads <export>/news.csv written by recorder/trade_watch.py (ForexFactory calendar, chart time). Open trades are still managed.", Order = -3, GroupName = "07. Setup & Trade")] public bool UseNewsLock { get; set; }
		[Range(0, 120), Display(Name = "News lock minutes (before and after)", Order = -2, GroupName = "07. Setup & Trade")] public int NewsLockMinutes { get; set; }
		[Range(0, 2359), Display(Name = "Session start (HHMM, chart time)", Order = -1, GroupName = "07. Setup & Trade")] public int SessionStart { get; set; }
		[Range(0, 2359), Display(Name = "Session end (HHMM, chart time)", Order = 0, GroupName = "07. Setup & Trade")] public int SessionEnd { get; set; }
		[Display(Name = "Zero Gamma rule for open trades (short gamma)", Description = "Off / Exit: close the trade on a rejection bar at Zero Gamma / Flip: close it and set up the opposite trade from that bar.", Order = 12, GroupName = "07. Setup & Trade")] public NinjaGexSigZgRule ZgRule { get; set; }
		[Display(Name = "Prefer Zero Gamma as target in long gamma", Description = "In long-gamma regime the target becomes Zero Gamma when it lies in the trade's direction at least MinRewardR away (still capped at MaxRewardR).", Order = 14, GroupName = "07. Setup & Trade")] public bool PreferZgTargetLongGamma { get; set; }
		[Display(Name = "Room filter (skip trades with an obstacle too close)", Description = "Before a setup is created: the first strong level (walls, gamma lines, Zero Gamma, G1/G2, EM, PDH/PDL, NY high/low, PropTraderz) in the trade's direction must be at least 'Room needed' risks away from the trigger. Skipped setups are logged with the distance.", Order = 16, GroupName = "07. Setup & Trade")] public bool UseRoomFilter { get; set; }
		[Range(0.1, 5), Display(Name = "Room needed (R to the first obstacle)", Order = 17, GroupName = "07. Setup & Trade")] public double RoomMinR { get; set; }
		[Display(Name = "Wall migration rule (automatic)", Description = "Blocks shorts at resistance while Zero Gamma / the call wall climb, longs at support while Zero Gamma / the put wall fall. The panel shows an ADVICE line while it is active. Strong reversal bars are still allowed.", Order = 18, GroupName = "07. Setup & Trade")] public bool UseWallMigration { get; set; }
		[Range(0.2, 10), Display(Name = "Wall migration: ATRs of drift", Order = 19, GroupName = "07. Setup & Trade")] public double MigrationAtr { get; set; }
		[Range(10, 240), Display(Name = "Wall migration: minutes looked back", Order = 20, GroupName = "07. Setup & Trade")] public int MigrationMinutes { get; set; }
		[Range(0, 40), Display(Name = "Target front-run (ticks inside the level)", Order = 21, GroupName = "07. Setup & Trade")] public int TargetOffsetTicks { get; set; }
		[Range(0, 1), Display(Name = "Target front-run (ATR, larger of the two wins)", Order = 22, GroupName = "07. Setup & Trade")] public double TargetOffsetAtr { get; set; }
		[Display(Name = "Targets follow their gamma level", Description = "A target named after a relay level (Zero Gamma, walls, gamma lines, EM) moves when that level moves; closes the trade when the level comes to price.", Order = 23, GroupName = "07. Setup & Trade")] public bool FollowTargetLevel { get; set; }
		[Range(0, 10), Display(Name = "Profit lock: once best reaches (R, 0 = off)", Order = 24, GroupName = "07. Setup & Trade")] public double LockAtR { get; set; }
		[Range(0, 5), Display(Name = "Profit lock: stop goes to (R)", Order = 25, GroupName = "07. Setup & Trade")] public double LockToR { get; set; }
		[Display(Name = "Reclaim rule on PropTraderz levels (experimental)", Description = "A close through a PTZ Long / PTZ Short idea level with the prior close on the other side counts as a strong acceptance and bypasses the location filter.", Order = 15, GroupName = "07. Setup & Trade")] public bool UseReclaimRule { get; set; }
		[Display(Name = "Track diamond entries (twin trade at signal close)", Description = "Every setup also runs a twin entered at the signal candle close with the same stop and target, logged to *_trades_diamond.csv. No arrows, no alerts.", Order = 13, GroupName = "07. Setup & Trade")] public bool TrackDiamondEntries { get; set; }
		[Range(0, 100000), Display(Name = "Risk per trade ($, 0 = 1 contract)", Description = "Contracts = risk / (stop points x $ per point). ES $50/pt, NQ $20, MES $5, MNQ $2. Panel and trades file then show dollars.", Order = 0, GroupName = "07. Setup & Trade")] public double RiskDollars { get; set; }
		[Range(1, 10), Display(Name = "Max target distance (R)", Description = "A level target further than this is replaced by the R cap, so one trade cannot block signals for hours.", Order = 10, GroupName = "07. Setup & Trade")] public double MaxRewardR { get; set; }
		[Range(1, 100000), Display(Name = "Big print size (contracts) for Flow@level", Order = 11, GroupName = "07. Setup & Trade")] public double BigPrintSize { get; set; }

		[Display(Name = "Export rows to CSV", Description = "One row per closed bar to <folder>\\ngs_<symbol>.csv for the Python recorder.", Order = 1, GroupName = "06. Export")] public bool ExportEnabled { get; set; }
		[Display(Name = "Export folder (blank = Documents\\NinjaTrader 8\\export)", Order = 2, GroupName = "06. Export")] public string ExportFolder { get; set; }
	}

	public enum NinjaGexSigZgSource { OpenInterestOnly, OpenInterestPlusVolume }
	public enum NinjaGexSigStopMode { Fixed, BreakevenAt1R, AtrTrail }
	public enum NinjaGexSigZgRule { Off, Exit, Flip }
	public enum NinjaGexSigLevelSet { GexOnly, PtzOnly, Both }
}
