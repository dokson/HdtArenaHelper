using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Hearthstone_Deck_Tracker.Controls.Overlay.Arena;

namespace HdtArenaHelper
{
	/// <summary>One offered option's data to render in the overlay.</summary>
	public class OverlayEntry
	{
		/// <summary>Text shown under the plaque (card name, or class name at the hero pick).</summary>
		public string Label { get; }
		public BlendedScore Score { get; }
		/// <summary>Mana cost, shown in the deck-review list to identify the card; -1 = hide.</summary>
		public int Cost { get; }

		/// <summary>
		/// Extra line under the label, in real units rather than the 0-100 blend (the hero pick's
		/// estimated class win-rate). Null = nothing to add.
		/// </summary>
		public string? Note { get; }

		public OverlayEntry(string label, BlendedScore score, int cost = -1, string? note = null)
		{
			Label = label;
			Score = score;
			Cost = cost;
			Note = note;
		}
	}

	/// <summary>
	/// One card on the mulligan screen: a KEEP/TOSS verdict judged against the drafted deck, plus
	/// the deck fact behind it. Deliberately NOT an <see cref="OverlayEntry"/> — the draft plaque
	/// shows a 0-100 blend and this shows a decision, and putting the two in the same badge would
	/// invite reading one as the other.
	/// </summary>
	public class MulliganOverlayEntry
	{
		public string Label { get; }
		public MulliganCardVerdict Verdict { get; }

		public MulliganOverlayEntry(string label, MulliganCardVerdict verdict)
		{
			Label = label;
			Verdict = verdict;
		}
	}

	/// <summary>
	/// Which screen the plaques are being laid out for. Not a cosmetic choice: the draft anchors
	/// are offset to the LEFT because the arena draft screen reserves its right quarter for the
	/// deck list, and an in-game Discover has no such panel — reusing the draft's centre would put
	/// every plaque off to one side.
	/// </summary>
	public enum OverlayLayout
	{
		/// <summary>Arena card draft, and the Underground legendary-group pick.</summary>
		CardDraft,
		/// <summary>Arena hero / class pick: portraits sit wider and higher.</summary>
		HeroPick,
		/// <summary>In-game card choice (Discover): centred on the full screen.</summary>
		InGameChoice,
	}

	/// <summary>
	/// Borderless, click-through, top-most overlay that covers the Hearthstone client and
	/// shows a score plaque above each offered option — for both the card draft and the hero
	/// pick, mirroring HDT's built-in arena overlay.
	///
	/// Geometry is resolution- and DPI-independent: plaques are laid out in a fixed 4:3
	/// <b>design space</b> (<see cref="DesignWidth"/> x <see cref="DesignHeight"/>) inside a
	/// <see cref="Viewbox"/> that uniformly scales to the client — exactly how HDT scales its
	/// own overlay — so resizing the Hearthstone window just rescales everything, no pixel
	/// maths per size. The window itself is kept glued to the client rect (DPI corrected)
	/// each tick, and hidden whenever the client is missing or minimised.
	///
	/// Each plaque is HDT's own pixel-native <see cref="ArenaPlaque"/> control, with a
	/// hand-drawn fallback if that control is ever unavailable.
	/// </summary>
	public class ArenaOverlayWindow : Window
	{
		// Fixed 4:3 design canvas the Viewbox scales to the client (== HS's centred safe area).
		private const double DesignWidth = 1200.0;
		private const double DesignHeight = 900.0;
		// The deck panel occupies the right 1/4.25 of the safe area; picks live in the left 3.25.
		private const double PlayFraction = 3.25 / 4.25;
		// Horizontal spacing of the three options, as a fraction of the play region,
		// live-tuned: hero portraits sit wider; plain cards and legendary-group columns
		// share the same layout in the client, so they share one spread.
		private const double HeroSpreadFraction = 0.29;
		// Hero plaques sit BELOW the portrait and the game's own hero-name banner, the way HDT's
		// own arena overlay places them. Live-tuned twice: at 0.43 they hugged the portrait bottom
		// and our label and win-rate line ran straight through the client's red hero name; 0.62 sat
		// a touch low against the frame below.
		private const double HeroCentreYFraction = 0.60;
		private const double CardSpreadFraction = 0.26;
		// In-game Discover: centred on the WHOLE design space (no deck panel to avoid). Live-tuned:
		// the cards are drawn much larger than draft cards, so the plaques go BELOW them — at 0.34
		// they sat on the art and covered the card text — and the columns are wider apart than the
		// draft's, which put the outer two plaques well inside their cards.
		private const double ChoiceSpreadFraction = 0.27;
		private const double ChoiceCentreYFraction = 0.74;
		// Mulligan: the opening hand sits centred and low, so the numbers go ABOVE the cards.
		// Live-tuned: at 0.30 the stack landed on the cards themselves, hiding the gauge against the
		// card art and covering the mana gems.
		/// <summary>
		/// Horizontal gap between mulligan columns, as a fraction of the design width — and it depends
		/// on the HAND SIZE, because the client fans a smaller hand wider. Both values are live-measured
		/// against card positions in a screenshot: with 3 cards the real gap is ~0.28 and a flat 0.20
		/// left the two outer labels ~0.07-0.10 of the width too far inwards, while with 4 cards 0.20
		/// is right. A two-entry table is COMPLETE rather than a special case: a Hearthstone opening
		/// hand is always 3 cards going first or 4 going second.
		/// </summary>
		private const double MulliganSpreadFractionThree = 0.28;
		private const double MulliganSpreadFraction = 0.20;
		private const double MulliganCentreYFraction = 0.22;

		// Deck-review panel geometry, as fractions of the CLIENT (it sits on the left edge, outside
		// the centred 4:3 design space). It fills the client height and spaces the rows to fit:
		// there is nothing on that side to align to, so an earlier version's attempt to match the
		// game's own row pitch — which only ever made sense while the panel overlapped that list —
		// is gone along with the compression and centring it needed.
		private const double DeckPanelTopFraction = 0.015;
		// Five times the top margin, and both earlier values were measured too small on a live
		// client: 0.015 left the last row flush against the edge (it read as clipped) and 0.045
		// still overlapped Hearthstone's own bottom bar — the friends button and the clock live
		// there, so this edge is not free space the way the top is.
		private const double DeckPanelBottomFraction = 0.075;
		/// <summary>Floor for a row, below which the 22px badge would clip.</summary>
		private const double MinDeckRowHeight = 24.0;
		/// <summary>
		/// Ceiling for a row. Only bites on an unusually short deck: filling 1000px with 12 rows
		/// would space them like a menu rather than a card list.
		/// </summary>
		private const double MaxDeckRowHeight = 48.0;

		private readonly Canvas _canvas;
		// Window-space (DIP) layer for edge-anchored UI (the deck-review panel), OUTSIDE the centred
		// 4:3 Viewbox so it can reach the true window edge, not the letterboxed inset. A Grid, not a
		// Canvas: WPF alignment re-anchors its child on every resize by itself, where Canvas coords
		// would go stale until the next render call.
		private readonly Grid _cornerLayer;
		private bool _nativePlaqueUnavailable;
		private bool _shownOnce;
		private bool _visible;   // our own show/hide state, so the transition is loggable
		private int _lastLoggedWidth;

		/// <summary>Underground Arena draft — switches the native plaque to its red/gold theme.</summary>
		public bool IsUnderground { get; set; }

		public ArenaOverlayWindow()
		{
			WindowStyle = WindowStyle.None;
			AllowsTransparency = true;
			Background = Brushes.Transparent;
			ShowInTaskbar = false;
			ShowActivated = false; // never steal focus from Hearthstone
			Topmost = true;
			ResizeMode = ResizeMode.NoResize;
			Visibility = Visibility.Collapsed;

			_canvas = new Canvas { Width = DesignWidth, Height = DesignHeight };
			var viewbox = new Viewbox
			{
				Stretch = Stretch.Uniform, // preserve 4:3, centre it -> HS's letterboxed safe area
				HorizontalAlignment = HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Center,
				Child = _canvas
			};
			// Root grid: the plaque Viewbox (centred 4:3) plus a window-filling layer for
			// edge-anchored UI that must reach the true window edge, not the letterbox inset.
			_cornerLayer = new Grid();
			var root = new Grid();
			root.Children.Add(viewbox);
			root.Children.Add(_cornerLayer);
			Content = root;

			Loaded += (_, __) => MakeClickThrough();
		}

		/// <summary>
		/// Show or hide the overlay for this tick and keep it glued to the client. Shown only
		/// when the caller wants it AND the Hearthstone client exists and is not minimised.
		/// </summary>
		public void UpdateVisibility(bool wantVisible)
		{
			var hwnd = FindWindow(null, "Hearthstone");
			var show = wantVisible && hwnd != IntPtr.Zero && !IsIconic(hwnd);
			// Log the transition off OUR OWN flag, not off Visibility: Show() already leaves the
			// window Visible, so a `Visibility != Visible` check never fired on the first show and
			// the log came out with no show/hide lines at all — which is exactly the pair of lines
			// needed to diagnose a "why is it still on screen" report.
			if(show)
			{
				if(!_shownOnce)
				{
					_shownOnce = true;
					Show();
				}
				Reposition(hwnd); // track size/position, incl. resizes
				Visibility = Visibility.Visible;
				if(!_visible)
				{
					_visible = true;
					Log("overlay shown");
				}
			}
			else
			{
				Visibility = Visibility.Collapsed;
				if(_visible)
				{
					_visible = false;
					Log("overlay hidden");
				}
			}
		}

		/// <summary>Replace the displayed plaques (call on the UI thread). Coords are design-space.</summary>
		public void SetEntries(IReadOnlyList<OverlayEntry> entries, OverlayLayout layout)
		{
			_canvas.Children.Clear();
			_cornerLayer.Children.Clear(); // drop any deck-review panel from the other phase
			if(entries.Count == 0)
				return;

			var best = double.MinValue;
			var anyWinrate = false;
			foreach(var e in entries)
			{
				if(!e.Score.HasData)
					continue;
				// Any empirical win-rate source, with or without a per-card sample size: a class tier
				// is real data backed by a whole bucket, and testing MaxGames instead made this
				// banner fire at the hero pick while three win-rates were on screen.
				if(e.Score.HasWinRateData)
					anyWinrate = true;
				if(e.Score.Value > best)
					best = e.Score.Value;
			}

			// The draft screens keep their right quarter for the deck list, so options centre on the
			// play region; an in-game choice centres on the whole screen.
			var playWidth = layout == OverlayLayout.InGameChoice ? DesignWidth : DesignWidth * PlayFraction;
			var playCentre = playWidth / 2.0;

			// No option is backed by real win-rate data (offline, curl blocked, or the
			// hero pick before the tier list loaded): say so once, centred — otherwise
			// heuristic-only plaques would look authoritative while the real signal is
			// silently missing.
			if(!anyWinrate)
			{
				var note = BuildLabel("win-rate data unavailable — check connection or use Refresh data", dimmed: true);
				note.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
				Canvas.SetLeft(note, playCentre - note.DesiredSize.Width / 2.0);
				Canvas.SetTop(note, DesignHeight * 0.72);
				_canvas.Children.Add(note);
			}
			var spread = playWidth * (layout switch
			{
				OverlayLayout.HeroPick => HeroSpreadFraction,
				OverlayLayout.InGameChoice => ChoiceSpreadFraction,
				_ => CardSpreadFraction,
			});
			// Vertical anchors, live-tuned per screen: hero plaques hug the portrait bottom (which
			// also keeps our labels off the game's own hero names), draft cards sit lower, and an
			// in-game choice sits higher because its cards are drawn larger.
			var centreY = DesignHeight * (layout switch
			{
				OverlayLayout.HeroPick => HeroCentreYFraction,
				OverlayLayout.InGameChoice => ChoiceCentreYFraction,
				_ => 0.55,
			});

			Log($"layout {layout} playW={playWidth:0} centre={playCentre:0} spread={spread:0} centreY={centreY:0}");

			for(var i = 0; i < entries.Count; i++)
			{
				var e = entries[i];
				var isBest = e.Score.HasData && Math.Abs(e.Score.Value - best) < 0.001;
				var plaque = BuildPlaqueElement(e, isBest);

				plaque.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
				var w = plaque.DesiredSize.Width > 0 ? plaque.DesiredSize.Width : 90.0;
				var h = plaque.DesiredSize.Height > 0 ? plaque.DesiredSize.Height : 60.0;

				// Three options packed around the play-region centre (same X in both phases).
				var centreX = playCentre + (i - (entries.Count - 1) / 2.0) * spread;
				var left = centreX - w / 2.0;
				var top = centreY - h / 2.0;
				Canvas.SetLeft(plaque, left);
				Canvas.SetTop(plaque, top);
				_canvas.Children.Add(plaque);

				// Label under the plaque so the score is unambiguously tied to its option.
				// Low-confidence scores (thin or heuristic-only data) are dimmed and
				// starred so they stop looking as authoritative as well-sampled ones.
				var lowConfidence = e.Score.HasData && e.Score.IsLowConfidence;
				var label = BuildLabel(lowConfidence ? e.Label + " *" : e.Label, lowConfidence);
				label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
				Canvas.SetLeft(label, centreX - label.DesiredSize.Width / 2.0);
				Canvas.SetTop(label, top + h + 4.0);
				_canvas.Children.Add(label);

				// Extra lines under the label, stacked: the note (hero pick's estimated class
				// win-rate, in real percentage points) then the synergy reason. Both are optional
				// and in practice never co-occur, but stacking keeps that from mattering.
				var nextY = top + h + 4.0 + label.DesiredSize.Height + 2.0;
				if(e.Note != null)
				{
					var note = BuildReason(e.Note);
					note.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
					Canvas.SetLeft(note, centreX - note.DesiredSize.Width / 2.0);
					Canvas.SetTop(note, nextY);
					_canvas.Children.Add(note);
					nextY += note.DesiredSize.Height + 2.0;
				}

				// The dominant synergy reason, when one fired ("fills the 3-drop gap").
				// Marked (exp.): the synergy rules are unvalidated by design (see AGENTS),
				// and the label keeps that honest at the point of use.
				if(e.Score.SynergyReason != null)
				{
					var reason = BuildReason(e.Score.SynergyReason + " (exp.)");
					reason.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
					Canvas.SetLeft(reason, centreX - reason.DesiredSize.Width / 2.0);
					Canvas.SetTop(reason, nextY);
					_canvas.Children.Add(reason);
				}

				Log($"  plaque[{i}] '{e.Label}' cx={centreX:0} y={top:0} w={w:0} h={h:0} " +
					$"score={(e.Score.HasData ? Math.Round(e.Score.Value).ToString() : "-")}" +
					$"{(lowConfidence ? " lowConf" : "")}" +
					$"{(e.Score.SynergyReason != null ? $" reason='{e.Score.SynergyReason}'" : "")}");
			}
		}


		/// <summary>
		/// Render the mulligan screen: per card, a KEEP/TOSS call judged against the drafted deck and
		/// the deck fact behind it. Call on the UI thread.
		///
		/// A word and a reason, never a percentage — the reason IS the advice, and it is what lets a
		/// player disagree with us on the spot. Most cards show a dash, which is not a gap: three
		/// confident calls per hand would bury the one worth reading.
		/// </summary>
		public void SetMulligan(IReadOnlyList<MulliganOverlayEntry> entries)
		{
			_canvas.Children.Clear();
			_cornerLayer.Children.Clear();
			if(entries.Count == 0)
				return;

			var spread = DesignWidth * (entries.Count == 3
				? MulliganSpreadFractionThree
				: MulliganSpreadFraction);
			var centreY = DesignHeight * MulliganCentreYFraction;
			var centreX = DesignWidth / 2.0;
			Log($"layout Mulligan cards={entries.Count} centre={centreX:0} spread={spread:0} " +
				$"centreY={centreY:0}");

			for(var i = 0; i < entries.Count; i++)
			{
				var e = entries[i];
				var x = centreX + (i - (entries.Count - 1) / 2.0) * spread;

				// The verdict is a WORD, not a number, and the dash is a real answer: most cards in
				// most hands have nothing decisive said about them, and printing a confident verdict
				// on all three would make the two that matter invisible.
				var word = e.Verdict.Verdict == MulliganVerdict.Keep ? "KEEP"
					: e.Verdict.Verdict == MulliganVerdict.Toss ? "TOSS"
					: "–";
				// Colour carries the verdict faster than the word does, which matters on a screen
				// with a timer running: green and red are read before they are parsed, and the
				// dash stays grey so "no call" cannot be mistaken for either.
				var colour = e.Verdict.Verdict == MulliganVerdict.Keep ? KeepBrush
					: e.Verdict.Verdict == MulliganVerdict.Toss ? TossBrush
					: null;
				var head = BuildLabel(word, dimmed: e.Verdict.Verdict == MulliganVerdict.Situational,
					colour: colour);
				head.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
				Canvas.SetLeft(head, x - head.DesiredSize.Width / 2.0);
				Canvas.SetTop(head, centreY);
				_canvas.Children.Add(head);

				// The reason carries the whole justification, so it is not decoration: "KEEP" alone
				// is an instruction, "KEEP — only 4 early bodies in the deck" is a checkable claim
				// the player can disagree with.
				var detail = e.Verdict.Reason ?? "no clear call from the deck";
				var sub = BuildReason(detail);
				sub.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
				Canvas.SetLeft(sub, x - sub.DesiredSize.Width / 2.0);
				Canvas.SetTop(sub, centreY + head.DesiredSize.Height + 2.0);
				_canvas.Children.Add(sub);

				Log($"  mulligan[{i}] '{e.Label}' x={x:0} verdict={e.Verdict.Verdict} " +
					$"reason='{e.Verdict.Reason}'");
			}
		}


		/// <summary>
		/// The run screen's deck description: what the deck DOES, in counts. Client-relative and
		/// top-LEFT, which is empty on both run screens (normal Arena's "Ready Up" and the Underground
		/// hub) — the bottom of that screen is taken by the hero portrait, the game's own curve widget
		/// and Play, and the centre by the reward banner.
		///
		/// Placement is a first guess and MUST be checked live: the coords are logged so a screenshot
		/// can correct them, which is the only way overlay geometry has ever been fixed in this project.
		/// </summary>
		public void SetRunSummary(DeckMechanics mechanics, string classLabel, int wins, int losses)
		{
			_canvas.Children.Clear();
			_cornerLayer.Children.Clear();

			var list = new StackPanel();
			list.Children.Add(new TextBlock
			{
				Text = $"{classLabel}   {wins}-{losses}",
				FontSize = 15,
				FontWeight = FontWeights.Bold,
				Foreground = Brushes.White,
				Margin = new Thickness(0, 0, 0, 4)
			});

			// The curve first, because it is the one line that reads as a shape rather than a number, and
			// over ALL cards: the client draws its own all-cards curve a few centimetres below this panel,
			// so a minions-only count here reads as a miscount. Live, exactly that happened — a 6-cost
			// SPELL appeared on the game's curve and not on ours. The SCORE still reasons about bodies
			// only (see DeckMechanics.MinionCurve); the two answer different questions.
			var curve = new System.Text.StringBuilder();
			for(var i = 0; i < mechanics.FullCurve.Count; i++)
			{
				if(curve.Length > 0)
					curve.Append("  ");
				curve.Append(i == mechanics.FullCurve.Count - 1 ? "7+" : (i + 1).ToString())
					.Append(':').Append(mechanics.FullCurve[i]);
			}
			list.Children.Add(BuildSummaryRow("curve", curve.ToString()));
			list.Children.Add(BuildSummaryRow("bodies",
				$"{mechanics.Minions} minions, {mechanics.Weapons + mechanics.Locations} weapons/locations"));
			list.Children.Add(BuildSummaryRow("removal",
				$"{mechanics.HardRemoval} hard, {mechanics.DamageCards} damage"));
			list.Children.Add(BuildSummaryRow("reach", $"{mechanics.Aoe} AoE, {mechanics.Draw} draw"));
			if(mechanics.Profile.Length > 0)
			{
				// The profile and the slot it is thinnest in, together on purpose: the profile is a mean
				// and a mean hides structure, so "midrange, thin at 3" says more than either half does.
				var shape = mechanics.ThinnestSlot < 0
					? $"{mechanics.Profile} ({mechanics.AverageCost:0.0})"
					: $"{mechanics.Profile} ({mechanics.AverageCost:0.0}), thin at "
						+ MetadataSynergyEngine.BucketLabel(mechanics.ThinnestSlot);
				list.Children.Add(BuildSummaryRow("shape", shape));
			}

			var panel = new Border
			{
				Background = new SolidColorBrush(Color.FromArgb(210, 12, 12, 14)),
				CornerRadius = new CornerRadius(8),
				Padding = new Thickness(12, 6, 14, 8),
				Child = list,
				Effect = new System.Windows.Media.Effects.DropShadowEffect
				{
					Color = Colors.Black,
					BlurRadius = 8,
					ShadowDepth = 0,
					Opacity = 0.8
				},
				HorizontalAlignment = HorizontalAlignment.Left,
				VerticalAlignment = VerticalAlignment.Top,
				Margin = new Thickness(RunSummaryLeft * ActualWidth, RunSummaryTop * ActualHeight, 0, 0)
			};
			_cornerLayer.Children.Add(panel);
			Log($"layout RunSummary left={RunSummaryLeft * ActualWidth:0} top={RunSummaryTop * ActualHeight:0} "
				+ $"(client {ActualWidth:0}x{ActualHeight:0}) curve=[{curve}]");
		}

		// Fractions of the client, not absolute pixels, so a resize re-anchors by itself. First guess:
		// clear of the reward banner on both run screens. Re-check on a live client before trusting.
		private const double RunSummaryLeft = 0.02;
		private const double RunSummaryTop = 0.06;

		private static StackPanel BuildSummaryRow(string label, string value)
		{
			var row = new StackPanel { Orientation = Orientation.Horizontal };
			row.Children.Add(new TextBlock
			{
				Text = label,
				FontSize = 13,
				Width = 62,
				Foreground = Brushes.Silver
			});
			row.Children.Add(new TextBlock
			{
				Text = value,
				FontSize = 13,
				Foreground = Brushes.White
			});
			return row;
		}

		public void SetDeckReview(IReadOnlyList<OverlayEntry> ranked,
			IReadOnlyList<OverlayEntry>? fullDeck = null)
		{
			_canvas.Children.Clear();        // no plaques while reviewing the deck
			_cornerLayer.Children.Clear();
			if(ranked.Count == 0)
				return;

			// Cut rank, not just "is a cut": the shade grades from red (worst card in the deck)
			// through orange to yellow (the marginal candidate), so the panel says which cuts are
			// clear and which are close. `ranked` arrives sorted weakest-first, so index IS severity.
			var cutRank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
			for(var i = 0; i < ranked.Count; i++)
				cutRank[ranked[i].Label] = i;
			var ordered = (fullDeck ?? ranked)
				.OrderBy(e => e.Cost)
				.ThenBy(e => e.Label, StringComparer.OrdinalIgnoreCase)
				.ToList();

			// No title: the game's own "Your Deck" header sits right beside it, so ours only added
			// width and a second heading saying the same thing.
			var list = new StackPanel();

			// Fill the client height: the rows share whatever space there is, so the panel reads as
			// one full-height column whatever the deck size. Clamped at both ends — a row may not
			// shrink under the badge, nor stretch into a menu on a short deck — so a deck outside
			// that range simply does not fill the height rather than rendering badly.
			var height = ActualHeight > 0 ? ActualHeight : DesignHeight;
			var available = height * (1.0 - DeckPanelTopFraction - DeckPanelBottomFraction);
			var rowHeight = Math.Max(MinDeckRowHeight,
				Math.Min(MaxDeckRowHeight, available / Math.Max(1, ordered.Count)));
			var top = height * DeckPanelTopFraction;

			var manaBlue = new SolidColorBrush(Color.FromRgb(90, 165, 245));
			foreach(var e in ordered)
			{
				var lowConfidence = e.Score.HasData && e.Score.IsLowConfidence;
				var isCut = cutRank.TryGetValue(e.Label, out var rank);
				var row = new StackPanel
				{
					Orientation = Orientation.Horizontal,
					Height = rowHeight,
					VerticalAlignment = VerticalAlignment.Center
				};
				row.Children.Add(BuildScoreBadge(e,
					isCut ? CutSeverityColor(rank, ranked.Count) : (Color?)null));
				// Mana cost (blue, like the game) so each row is identifiable at a glance.
				if(e.Cost >= 0)
					row.Children.Add(new TextBlock
					{
						Text = e.Cost.ToString(),
						FontSize = 15,
						FontWeight = FontWeights.Bold,
						Width = 20,
						TextAlignment = TextAlignment.Center,
						VerticalAlignment = VerticalAlignment.Center,
						Foreground = manaBlue,
						Margin = new Thickness(8, 0, 0, 0)
					});
				row.Children.Add(new TextBlock
				{
					Text = lowConfidence ? e.Label + " *" : e.Label,
					FontSize = 15,
					Margin = new Thickness(8, 0, 0, 0),
					VerticalAlignment = VerticalAlignment.Center,
					Foreground = isCut ? Brushes.White : (lowConfidence ? Brushes.Silver : Brushes.White),
					Opacity = lowConfidence && !isCut ? 0.8 : 1.0
				});
				list.Children.Add(row);
			}

			var panel = new Border
			{
				Background = new SolidColorBrush(Color.FromArgb(210, 12, 12, 14)),
				CornerRadius = new CornerRadius(8),
				Padding = new Thickness(12, 4, 14, 6),
				Child = list,
				Effect = new System.Windows.Media.Effects.DropShadowEffect
				{
					Color = Colors.Black,
					BlurRadius = 8,
					ShadowDepth = 0,
					Opacity = 0.8
				},
				// Against the LEFT edge of the client, opposite the game's own "Your Deck" list:
				// the row heights already match that list, so sitting on top of it added nothing
				// and hid its card art. The trade is that a wide panel can clip the leftmost of
				// the discard columns — keep it narrow. Alignment plus a fractional top margin,
				// not absolute coordinates, so a client resize re-anchors it by itself.
				HorizontalAlignment = HorizontalAlignment.Left,
				VerticalAlignment = VerticalAlignment.Top,
				Margin = new Thickness(0, top, 0, 0)
			};
			_cornerLayer.Children.Add(panel);
			Log($"deck-review panel: {ordered.Count} deck rows, {ranked.Count} flagged as cuts, " +
				$"top={top:0} rowHeight={rowHeight:0.0}");
		}

		/// <summary>
		/// Severity shade for cut candidate <paramref name="rank"/> of <paramref name="total"/>,
		/// weakest first: deep red for the clearest cut, through orange, to yellow for the marginal
		/// one. Interpolated rather than a fixed palette so it reads the same for any candidate
		/// count, and hue-only — the badge stays legible at every step.
		/// </summary>
		private static Color CutSeverityColor(int rank, int total)
		{
			var t = total <= 1 ? 0.0 : Math.Min(1.0, Math.Max(0.0, rank / (double)(total - 1)));
			var worst = Color.FromRgb(0xf8, 0x2a, 0x1e);   // red
			var mildest = Color.FromRgb(0xf2, 0xc0, 0x2c); // yellow
			return Color.FromRgb(
				(byte)Math.Round(worst.R + (mildest.R - worst.R) * t),
				(byte)Math.Round(worst.G + (mildest.G - worst.G) * t),
				(byte)Math.Round(worst.B + (mildest.B - worst.B) * t));
		}

		/// <summary>
		/// HDT's arena score badge, reproduced: a rounded plate, dark by default. When the card is a
		/// suggested cut, <paramref name="cutAccent"/> is its severity colour and tints both border
		/// and plate. Same shape and palette as HDT's premium helper so the overlay reads as native,
		/// but driven entirely by our own blend.
		/// </summary>
		private static Border BuildScoreBadge(OverlayEntry entry, Color? cutAccent)
		{
			var hasData = entry.Score.HasData;
			var accent = cutAccent ?? Color.FromRgb(0x13, 0x17, 0x1A);
			// A dark wash of the accent, so a yellow-flagged row is still a dark plate with a yellow
			// edge rather than a bright block that outshouts the score itself.
			var plate = cutAccent == null
				? Color.FromRgb(0x23, 0x27, 0x2A)
				: Color.FromRgb((byte)(accent.R * 0.32), (byte)(accent.G * 0.32), (byte)(accent.B * 0.32));
			return new Border
			{
				Width = 32,
				Height = 22,
				CornerRadius = new CornerRadius(3),
				BorderThickness = new Thickness(1),
				Background = new SolidColorBrush(plate),
				BorderBrush = new SolidColorBrush(accent),
				Child = new TextBlock
				{
					Text = hasData ? Math.Round(entry.Score.Value).ToString("0") : "–",
					FontSize = 14,
					FontWeight = FontWeights.Bold,
					HorizontalAlignment = HorizontalAlignment.Center,
					VerticalAlignment = VerticalAlignment.Center,
					Foreground = new SolidColorBrush(cutAccent != null
						? Colors.White
						: Color.FromArgb(0xbf, 0xff, 0xff, 0xff))
				}
			};
		}

		/// <summary>
		/// HDT's pixel-native arena score plaque when hostable (it is at runtime), otherwise
		/// the hand-drawn fallback. No-data options use the fallback ("—").
		/// </summary>
		private FrameworkElement BuildPlaqueElement(OverlayEntry e, bool isBest)
		{
			if(e.Score.HasData)
			{
				var seed = e.Label.GetHashCode();
				var native = TryBuildNative(() => new ArenaPlaqueViewModel(
					Math.Round(e.Score.Value).ToString("0"), PlaqueTier.FromScore(e.Score.Value), seed, IsUnderground));
				if(native != null)
					return native;
			}
			return BuildPlaque(e, isBest);
		}

		private FrameworkElement? TryBuildNative(Func<ArenaPlaqueViewModel> makeViewModel)
		{
			if(_nativePlaqueUnavailable)
				return null;
			try
			{
				return new ArenaPlaque { DataContext = makeViewModel() };
			}
			catch(Exception ex)
			{
				// Bind once and stop retrying so we don't throw per option every draft.
				_nativePlaqueUnavailable = true;
				Log($"native ArenaPlaque unavailable, using hand-drawn plaques: {ex.Message}");
				return null;
			}
		}

		// Not pure green and red: on the mulligan's warm background a saturated pair vibrates and
		// reads as an error state. These are lifted toward the pastel end so they stay legible over
		// card art without shouting.
		private static readonly Brush KeepBrush = new SolidColorBrush(Color.FromRgb(126, 217, 87));
		private static readonly Brush TossBrush = new SolidColorBrush(Color.FromRgb(240, 106, 106));

		private static FrameworkElement BuildLabel(string text, bool dimmed = false, Brush? colour = null)
			=> new Border
			{
				Background = new SolidColorBrush(Color.FromArgb(200, 12, 12, 14)),
				CornerRadius = new CornerRadius(4),
				Padding = new Thickness(8, 1, 8, 2),
				Child = new TextBlock
				{
					Text = text,
					FontSize = 15,
					FontWeight = FontWeights.Bold,
					Foreground = colour ?? (dimmed ? Brushes.Silver : Brushes.White),
					TextAlignment = TextAlignment.Center
				},
				Opacity = dimmed ? 0.8 : 1.0,
				Effect = new System.Windows.Media.Effects.DropShadowEffect
				{
					Color = Colors.Black,
					BlurRadius = 6,
					ShadowDepth = 0,
					Opacity = 0.85
				}
			};

		// Smaller, quieter line under the label: the synergy "why", not a second score.
		private static FrameworkElement BuildReason(string text)
			=> new Border
			{
				Background = new SolidColorBrush(Color.FromArgb(170, 12, 12, 14)),
				CornerRadius = new CornerRadius(4),
				Padding = new Thickness(6, 0, 6, 1),
				Child = new TextBlock
				{
					Text = text,
					FontSize = 11,
					Foreground = Brushes.LightGray,
					TextAlignment = TextAlignment.Center
				}
			};

		private static Border BuildPlaque(OverlayEntry e, bool isBest)
		{
			var accent = e.Score.HasData ? TierColor(e.Score.Value) : Colors.Gray;

			var stack = new StackPanel { Margin = new Thickness(6, 4, 6, 4) };
			stack.Children.Add(new TextBlock
			{
				Text = e.Score.HasData ? Math.Round(e.Score.Value).ToString("0") : "—",
				FontSize = 34,
				FontWeight = FontWeights.Bold,
				Foreground = new SolidColorBrush(accent),
				HorizontalAlignment = HorizontalAlignment.Center
			});
			var detail = e.Score.HasData ? string.Join("  ", ComponentLabels(e.Score)) : "no data";
			stack.Children.Add(new TextBlock
			{
				Text = detail,
				FontSize = 11,
				Foreground = Brushes.DarkGray,
				TextAlignment = TextAlignment.Center,
				HorizontalAlignment = HorizontalAlignment.Center
			});

			return new Border
			{
				Background = new SolidColorBrush(Color.FromArgb(220, 18, 18, 22)),
				BorderBrush = new SolidColorBrush(isBest ? Colors.LightGreen : accent),
				BorderThickness = new Thickness(isBest ? 2.5 : 1.0),
				CornerRadius = new CornerRadius(10),
				Padding = new Thickness(4, 2, 4, 2),
				Child = stack,
				Effect = new System.Windows.Media.Effects.DropShadowEffect
				{
					Color = Colors.Black,
					BlurRadius = 8,
					ShadowDepth = 0,
					Opacity = 0.7
				}
			};
		}

		// Green (strong) -> amber (average) -> red (weak), on the 0-100 blend scale.
		private static Color TierColor(double score)
		{
			if(score >= 65) return Color.FromRgb(120, 220, 120);
			if(score >= 50) return Color.FromRgb(210, 210, 120);
			if(score >= 40) return Color.FromRgb(225, 175, 110);
			return Color.FromRgb(225, 120, 120);
		}

		private static IEnumerable<string> ComponentLabels(BlendedScore score)
		{
			foreach(var c in score.Components)
				yield return $"{c.SourceName} {Math.Round(c.NormalizedScore)}";
			if(Math.Abs(score.SynergyBonus) >= 0.5)
				yield return (score.SynergyBonus > 0 ? "+" : "") + Math.Round(score.SynergyBonus) + " syn";
		}

		private static void Log(string msg)
			=> Hearthstone_Deck_Tracker.Utility.Logging.Log.Info($"[ArenaHelper] {msg}");

		/// <summary>
		/// Size/position the window over the Hearthstone client rect. Win32 reports device
		/// pixels; WPF wants DIPs, so divide by the client's DPI scale.
		/// </summary>
		private void Reposition(IntPtr hwnd)
		{
			if(hwnd == IntPtr.Zero || !GetClientRect(hwnd, out var rect))
				return;
			var topLeft = new POINT { X = rect.Left, Y = rect.Top };
			ClientToScreen(hwnd, ref topLeft);

			var scale = DpiScaleFor(hwnd);
			Left = topLeft.X / scale;
			Top = topLeft.Y / scale;
			var w = Math.Max(1, rect.Right - rect.Left);
			var h = Math.Max(1, rect.Bottom - rect.Top);
			Width = w / scale;
			Height = h / scale;

			if(w != _lastLoggedWidth)
			{
				_lastLoggedWidth = w;
				Log($"client rect {w}x{h}px scale={scale:0.00} -> window {Width:0}x{Height:0} at ({Left:0},{Top:0})");
			}
		}

		private static double DpiScaleFor(IntPtr hwnd)
		{
			try
			{
				var dpi = GetDpiForWindow(hwnd);
				return dpi >= 48 ? dpi / 96.0 : 1.0;
			}
			catch
			{
				return 1.0; // GetDpiForWindow is Win10 1607+; assume 100% otherwise
			}
		}

		#region interop
		private void MakeClickThrough()
		{
			var hwnd = new WindowInteropHelper(this).Handle;
			var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
			SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
		}

		private const int GWL_EXSTYLE = -20;
		private const int WS_EX_TRANSPARENT = 0x20;
		private const int WS_EX_TOOLWINDOW = 0x80;
		private const int WS_EX_NOACTIVATE = 0x08000000;

		[StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
		[StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

		[DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
		private static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);
		[DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
		[DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);
		[DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
		[DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hWnd);
		[DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
		[DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
		#endregion
	}
}
