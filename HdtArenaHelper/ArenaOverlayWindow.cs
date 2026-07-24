using System;
using System.Collections.Generic;
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
		public OverlayEntry(string label, BlendedScore score)
		{
			Label = label;
			Score = score;
		}
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
		// The three options (heroes OR cards) sit at the same horizontal positions, packed
		// tighter than an even 3-way split of the play region. Tuned against a live hero pick.
		private const double OptionSpreadFraction = 0.28;

		private readonly Canvas _canvas;
		private bool _nativePlaqueUnavailable;
		private bool _shownOnce;
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
			Content = new Viewbox
			{
				Stretch = Stretch.Uniform, // preserve 4:3, centre it -> HS's letterboxed safe area
				HorizontalAlignment = HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Center,
				Child = _canvas
			};

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
			if(show)
			{
				if(!_shownOnce)
				{
					_shownOnce = true;
					Show();
				}
				Reposition(hwnd); // track size/position, incl. resizes
				if(Visibility != Visibility.Visible)
				{
					Visibility = Visibility.Visible;
					Log("overlay shown");
				}
			}
			else if(Visibility != Visibility.Collapsed)
			{
				Visibility = Visibility.Collapsed;
				Log("overlay hidden");
			}
		}

		/// <summary>Replace the displayed plaques (call on the UI thread). Coords are design-space.</summary>
		public void SetEntries(IReadOnlyList<OverlayEntry> entries, bool isHeroPick)
		{
			_canvas.Children.Clear();
			if(entries.Count == 0)
				return;

			var best = double.MinValue;
			foreach(var e in entries)
				if(e.Score.HasData && e.Score.Value > best)
					best = e.Score.Value;

			var playWidth = DesignWidth * PlayFraction;
			var playCentre = playWidth / 2.0;
			var spread = playWidth * OptionSpreadFraction;
			// Same 3 horizontal positions in both phases; only the vertical anchor differs
			// (hero portraits vs the lower card row). Card Y still to be tuned on a live draft.
			var centreY = isHeroPick ? DesignHeight * 0.44 : DesignHeight * 0.55;

			Log($"layout heroPick={isHeroPick} playW={playWidth:0} centre={playCentre:0} spread={spread:0} centreY={centreY:0}");

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
				var label = BuildLabel(e.Label);
				label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
				Canvas.SetLeft(label, centreX - label.DesiredSize.Width / 2.0);
				Canvas.SetTop(label, top + h + 4.0);
				_canvas.Children.Add(label);

				Log($"  plaque[{i}] '{e.Label}' cx={centreX:0} y={top:0} w={w:0} h={h:0} " +
					$"score={(e.Score.HasData ? Math.Round(e.Score.Value).ToString() : "-")}");
			}
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

		private static FrameworkElement BuildLabel(string text)
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
					Foreground = Brushes.White,
					TextAlignment = TextAlignment.Center
				},
				Effect = new System.Windows.Media.Effects.DropShadowEffect
				{
					Color = Colors.Black, BlurRadius = 6, ShadowDepth = 0, Opacity = 0.85
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
					Color = Colors.Black, BlurRadius = 8, ShadowDepth = 0, Opacity = 0.7
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
