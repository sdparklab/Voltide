using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Voltide.Core;

namespace Voltide.UI
{
    public class MainForm : Form
    {
        // Artboard units. Everything below is multiplied by _scale for the display DPI,
        // so the panel physically measures the same on a 100% and a 150% screen.
        private const int DesignCardW = 220;
        private const int DesignCardH = 300;
        private const int DesignCardGap = BackgroundRenderer.CardGap;
        private const int DesignCardTop = BackgroundRenderer.CardTop;

        /// <summary>How many cards fit across the panel. Extra devices scroll.</summary>
        private const int VisibleSlots = 3;

        /// <summary>Seconds each page lingers before the carousel advances.</summary>
        private const int CarouselMs = 9000;

        private float _scale = 1f;
        private int CardW, CardH, CardGap, CardTop;
        private int PanelW, PanelH;
        private int SlotPitch;          // CardW + CardGap, the distance one notch of scroll moves

        private int _slot;              // scroll position in notches, 0 = first page
        private int _slotMax;
        // Animation timing, all in seconds so the result is identical whether the
        // timer fires at 60, 45 or 20 fps.
        private const float RefillSeconds = 1.05f;   // empty -> real level
        private const float FadeSeconds = 0.26f;     // card appearing
        private const float LeaveSeconds = 0.20f;    // card disappearing
        private const float CalmSeconds = 1.30f;     // excited ripple calms back down
        private const float WaveSpeed = 1.9f;        // rad/s while responding, ~3.3 s
        private const float IdleWaveSpeed = 0.85f;   // rad/s at rest, ~7.4 s per wave
        private const float IdleAmp = 0.42f;         // resting amplitude vs. full
        private const float SlideStiffness = 13f;    // how fast cards glide to a new slot

        // Frame pacing. 8 ms tracks the 120 Hz panel; the idle wave stays at 10 fps.
        // The fast path only runs for a second or two at a time (a page slide, a
        // refresh, a level change), so the higher rate costs nothing while the panel
        // sits still or hides in the tray.
        private const int FastInterval = 8;
        private const int DotsInterval = 33;         // empty-state chase, 30 fps is plenty
        private const int IdleInterval = 100;

        // Empty state: six dots chasing left to right under the hint text.
        private const int DotsCount = 6;
        private const float DotsSeconds = 1.35f;     // one full round of the chase
        private static readonly Color EmptyTextColor = Color.FromArgb(172, 172, 184);
        private static readonly Color DotsColor = Color.FromArgb(210, 210, 222);

        // Set to false to stop the liquid at rest (idle CPU would then be a flat 0%).
        // Not a const: that would make the "stop the timer" branch unreachable code.
        private static readonly bool IdleWave = true;

        private static readonly Color BgColor = Color.FromArgb(20, 20, 20);

        // icon motion
        private const float SpinSeconds = 0.62f;     // one full turn per refresh
        private const float DenySeconds = 0.42f;     // "not yet" shake
        private const float HintSeconds = 1.9f;      // how long the hint text lives
        private const long RefreshCooldownMs = 5000;
        private const float HoverSeconds = 0.13f;
        private const float PressSeconds = 0.09f;
        private const float PressScale = 0.86f;

        private readonly DeviceManager _manager;
        private Thread _frameThread;
        private readonly ManualResetEvent _stopFrame = new ManualResetEvent(false);
        private int _framePending;
        private volatile int _rate = 100;
        private readonly Stopwatch _clock = Stopwatch.StartNew();


        private readonly Dictionary<string, CardAnim> _anim = new Dictionary<string, CardAnim>();
        private readonly List<string> _order = new List<string>();   // visible cards, left to right

        private long _lastTickMs;
        private float _phase;
        private float _energy;          // 1 = liquid is disturbed, 0 = dead calm
        private float _dotsPhase;       // 0..1, drives the empty-state chase
        private bool _timerResRaised;

        private bool _dragging;
        private Point _dragStart;

        // header icons
        private bool _hotRefresh, _hotClose;
        private bool _pressRefresh, _pressClose;
        private float _refreshHot, _closeHot;       // 0..1 eased
        private float _refreshPress, _closePress;   // 0..1 eased
        private bool _spinning;
        private float _spinT;                       // 0..1
        private bool _denying;
        private float _denyT;
        private bool _hintOn;
        private float _hintT;
        private long _lastRefreshAt = -999999;

        public event EventHandler RefreshRequested;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        // WinForms timers inherit the ~15.6 ms system tick. While an animation runs we
        // ask for 1 ms resolution so a 15 ms timer actually ticks every 15 ms
        // (measured 42 fps before, 64 fps after). It is released the moment the
        // animation ends, so an idle panel does not pin the system timer.
        [DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint ms);

        [DllImport("winmm.dll")]
        private static extern uint timeEndPeriod(uint ms);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        /// <summary>Private message the frame clock posts to wake the UI thread.</summary>
        private const int WmFrame = 0x800A;

        /// <summary>Per-device animation state. One object instead of four parallel maps.</summary>
        private class CardAnim
        {
            public string Id;
            public DeviceSnapshot Snap;

            public float Shown;         // displayed percent, may lag Target
            public float Target;
            public float Opacity = 1f;
            public float Refill;        // 0..1
            public bool Refilling;

            public float X;             // current left edge, animated
            public float TargetX;
            public bool Placed;         // false until the first layout pass
            public bool Leaving;        // device went away, fading out

            // 1 = the device is answering, dims toward OfflineDim when it stops.
            public float Dim = 1f;
        }

        /// <summary>How far a card fades when its device stops answering.</summary>
        private const float OfflineDim = 0.78f;

        public MainForm(DeviceManager manager)
        {
            _manager = manager;

            // The whole panel is laid out in artboard units and multiplied by this.
            using (Graphics probe = Graphics.FromHwnd(IntPtr.Zero))
            {
                _scale = Math.Max(1f, probe.DpiX / 96f);
            }

            CardW = (int)Math.Round(DesignCardW * _scale);
            CardH = (int)Math.Round(DesignCardH * _scale);
            CardGap = (int)Math.Round(DesignCardGap * _scale);
            CardTop = (int)Math.Round(DesignCardTop * _scale);
            SlotPitch = CardW + CardGap;
            PanelW = (int)Math.Round(BackgroundRenderer.DesignW * _scale);
            PanelH = (int)Math.Round(BackgroundRenderer.DesignH * _scale);

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(PanelW, PanelH);
            BackColor = BgColor;
            ShowInTaskbar = false;
            DoubleBuffered = true;
            Text = "Voltide";

            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);

            // No timer here: the frame clock is a pool timer created on first use,
            // because WM_TIMER cannot go faster than ~64 Hz on this machine.

            // The timer is NOT started here. It runs only while something is
            // actually moving, which is what keeps a visible panel at 0% CPU.
        }

        // ---------------------------------------------------------------- api

        public void ShowPanel()
        {
            Show();
            Activate();
            BringToFront();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            RequestRefresh();
        }

        /// <summary>Last level this card showed, for the tray menu's sleeping rows.</summary>
        public int? LastPercent(string id)
        {
            CardAnim a;
            if (_anim.TryGetValue(id, out a) && a.Target > 0.5f)
            {
                return (int)Math.Round(a.Target);
            }
            return null;
        }

        public void RequestRefresh()
        {
            // Fire the fill replay immediately, not when the snapshot lands. The
            // device read takes a second or more (a dormant mouse has to time out
            // first), and deferring the replay meant a click could spin the icon and
            // then show nothing at all when the read was skipped as redundant.
            _lastRefreshAt = _clock.ElapsedMilliseconds;
            ReplayFill();
            SpinRefresh();
            if (RefreshRequested != null) RefreshRequested(this, EventArgs.Empty);
        }

        /// <summary>
        /// The refresh button, with a cooldown. Polling a Lightspeed receiver too
        /// often makes it stop answering for a while, so a hammered refresh button
        /// would literally put the cards to sleep. Refused presses get a shake and a
        /// hint instead of being ignored.
        /// </summary>
        private void TryRefresh()
        {
            long now = _clock.ElapsedMilliseconds;
            long since = now - _lastRefreshAt;

            if (since < RefreshCooldownMs)
            {
                DenyRefresh();
                return;
            }

            RequestRefresh();
        }

        private void DenyRefresh()
        {
            _denyT = 0f;
            _denying = true;
            _hintT = 0f;
            _hintOn = true;
            StartAnimating();
        }

        /// <summary>One full clockwise turn of the refresh icon.</summary>
        public void SpinRefresh()
        {
            _spinning = true;
            _spinT = 0f;
            StartAnimating();
        }

        /// <summary>Drains every card and lets the liquid rise again.</summary>
        private void ReplayFill()
        {
            foreach (CardAnim a in _anim.Values)
            {
                if (a.Leaving) continue;
                a.Shown = 0f;
                a.Refill = 0f;
                a.Refilling = true;
            }

            _energy = 1f;
            StartAnimating();
        }

        /// <summary>Disturbs the liquid so it ripples for a moment, then settles.</summary>
        public void Wake()
        {
            _energy = 1f;
            StartAnimating();
        }

        private void StartAnimating()
        {
            if (!Visible) return;
            _lastTickMs = _clock.ElapsedMilliseconds;

            if (!_timerResRaised)
            {
                timeBeginPeriod(1);
                _timerResRaised = true;
            }

            if (_frameThread == null || !_frameThread.IsAlive)
            {
                _stopFrame.Reset();
                _frameThread = new Thread(FrameLoop);
                _frameThread.IsBackground = true;
                _frameThread.Name = "voltide-frame-clock";
                _frameThread.Start();
            }
        }

        /// <summary>
        /// The frame clock. Both WinForms' timer (WM_TIMER, coalesced at ~15.6 ms) and
        /// System.Threading.Timer (pool queue, measured at ~15.4 ms here) cap the panel
        /// at 62 fps no matter what interval is asked for. A thread that sleeps in
        /// 8 ms slices actually delivers it — Sleep is accurate once the system timer
        /// resolution is 1 ms, which StartAnimating requests.
        /// </summary>
        private void FrameLoop()
        {
            while (true)
            {
                int wait = _rate;
                if (wait < 1) wait = 1;

                if (_stopFrame.WaitOne(wait)) return;

                // At most one frame in flight, so a slow paint cannot build a backlog.
                if (Interlocked.Exchange(ref _framePending, 1) == 1) continue;

                try
                {
                    if (IsHandleCreated)
                        PostMessage(Handle, WmFrame, IntPtr.Zero, IntPtr.Zero);
                    else
                        Interlocked.Exchange(ref _framePending, 0);
                }
                catch
                {
                    Interlocked.Exchange(ref _framePending, 0);
                }
            }
        }

        private void SetRate(int interval)
        {
            _rate = interval;

            bool hiRes = interval <= DotsInterval;
            if (hiRes && !_timerResRaised)
            {
                timeBeginPeriod(1);
                _timerResRaised = true;
            }
            else if (!hiRes && _timerResRaised)
            {
                timeEndPeriod(1);
                _timerResRaised = false;
            }
        }

        private void StopAnimating()
        {
            _stopFrame.Set();

            if (_timerResRaised)
            {
                timeEndPeriod(1);
                _timerResRaised = false;
            }
        }

        public void UpdateSnapshot(List<DeviceSnapshot> snaps)
        {
            List<DeviceSnapshot> visible = new List<DeviceSnapshot>();
            if (snaps != null)
            {
                foreach (DeviceSnapshot s in snaps)
                {
                    if (s == null || s.Info == null || s.Reading == null) continue;
                    // Asleep is not gone: the dongle is still plugged in, the device
                    // just stopped answering. Keep the card and its last value.
                    if (!s.Reading.Online && !s.Reading.Sleeping) continue;
                    visible.Add(s);
                }
            }

            // A Logitech mouse plugged in by cable enumerates a second time (its own
            // wired collection next to the receiver), so the same mouse would get two
            // cards. Collapse pairs whose brand+model match when only one is live.
            List<DeviceSnapshot> merged = CollapseTransports(visible);

            HashSet<string> seen = new HashSet<string>();
            foreach (DeviceSnapshot s in merged)
            {
                string id = s.Info.Id;
                seen.Add(id);

                CardAnim a = null;
                _anim.TryGetValue(id, out a);

                float target;
                if (s.Reading.Percent.HasValue)
                {
                    target = s.Reading.Percent.Value;
                }
                else if (a != null)
                {
                    // No fresh reading. Keep the level where it is, but do NOT write it
                    // back into Percent: that made a switched-off mouse sit on "90%"
                    // forever with nothing on the card saying the number was stale.
                    // The card shows "--", dims, and draws no liquid instead.
                    target = a.Target;
                }
                else if (s.Reading.Sleeping)
                {
                    // first sight of a device that is already asleep: show the card
                    // empty, its text reads "--" and the status says 设备休眠
                    target = 0f;
                }
                else
                {
                    continue;   // nothing to show yet
                }

                bool wasLevel = a != null && HasLevel(a.Snap);
                bool nowLevel = HasLevel(s);
                bool nowOnline = s.Reading.Online;

                if (a == null)
                {
                    // fresh card: fade in from nothing, level rises from the bottom
                    a = new CardAnim();
                    a.Id = id;
                    a.Target = target;
                    a.Shown = 0f;
                    a.Opacity = 0f;
                    a.Refill = 0f;
                    a.Refilling = true;
                    _anim[id] = a;
                    _order.Add(id);
                    _energy = 1f;
                }
                else if (Math.Abs(a.Target - target) > 0.01f)
                {
                    // real change: glide to it, do not replay the fill
                    a.Target = target;
                    _energy = 1f;
                }

                a.Snap = s;

                // First reading for this card (device woke up, or a value finally
                // arrived): pour the liquid in rather than snapping to a level.
                if (nowLevel && !wasLevel && a.Target > 0.5f)
                {
                    a.Refilling = true;
                    a.Refill = 0f;
                    a.Shown = 0f;
                    _energy = 1f;
                }

                if (a.Leaving)
                {
                    // reconnected while its card was still fading out
                    a.Leaving = false;
                    a.Opacity = Math.Max(a.Opacity, 0.08f);
                    if (!_order.Contains(id)) _order.Add(id);
                    _energy = 1f;
                }
            }

            // Anything that vanished fades out in place instead of blinking away.
            foreach (CardAnim a in _anim.Values)
            {
                if (a.Leaving || seen.Contains(a.Id)) continue;
                a.Leaving = true;
                a.TargetX = a.X;
                _order.Remove(a.Id);
                _energy = 1f;
            }

            SortOrder();
            FitToCards();
            RelayoutCards();

            StartAnimating();
            Invalidate();
        }

        /// <summary>
        /// Wired and wireless transports of the same device collapse into one card.
        /// Two genuinely identical mice stay as two cards because both are live.
        /// </summary>
        internal static List<DeviceSnapshot> CollapseTransports(List<DeviceSnapshot> input)
        {
            List<DeviceSnapshot> result = new List<DeviceSnapshot>();
            Dictionary<string, int> slot = new Dictionary<string, int>();

            foreach (DeviceSnapshot s in input)
            {
                // A paired child has its own identity even when the same model
                // on another receiver is awake and this one is asleep.
                if (s.Info.ReceiverId != null)
                {
                    result.Add(s);
                    continue;
                }

                string key = s.Info.Brand + "|" + s.Info.Model;

                int index;
                if (!slot.TryGetValue(key, out index))
                {
                    slot[key] = result.Count;
                    result.Add(s);
                    continue;
                }

                DeviceSnapshot kept = result[index];

                if (s.Reading.Online && !kept.Reading.Online)
                {
                    result[index] = s;          // the live transport wins
                }
                else if (!s.Reading.Online && kept.Reading.Online)
                {
                    // keep the live one
                }
                else
                {
                    result.Add(s);              // same state: a real second device
                }
            }

            return result;
        }

        /// <summary>
        /// The panel is a fixed three-slot window. Extra devices do not widen it —
        /// they scroll in one slot at a time from the right, while the leftmost card
        /// slides out under the vignette.
        /// </summary>
        private void FitToCards()
        {
            if (Height != PanelH) Height = PanelH;
            if (Width == PanelW) return;

            int centre = Left + Width / 2;
            Width = PanelW;
            Left = centre - PanelW / 2;
        }

        private void RelayoutCards()
        {
            List<string> ids = VisibleOrder();
            int n = ids.Count;

            _slotMax = Math.Max(0, n - VisibleSlots);
            if (_slot > _slotMax) _slot = _slotMax;
            if (_slot < 0) _slot = 0;

            int slots = Math.Min(n, VisibleSlots);
            int totalW = slots * CardW + Math.Max(0, slots - 1) * CardGap;
            int startX = (Width - totalW) / 2;

            for (int i = 0; i < n; i++)
            {
                CardAnim a;
                if (!_anim.TryGetValue(ids[i], out a)) continue;

                a.TargetX = startX + (i - _slot) * SlotPitch;
                if (!a.Placed)
                {
                    a.X = a.TargetX;
                    a.Placed = true;
                }
            }
        }

        /// <summary>Card ids in display order: live devices first, sleeping ones last.</summary>
        private List<string> VisibleOrder()
        {
            List<string> live = new List<string>();
            List<string> asleep = new List<string>();

            foreach (string id in _order)
            {
                CardAnim a;
                if (!_anim.TryGetValue(id, out a) || a.Leaving) continue;

                bool online = a.Snap != null && a.Snap.Reading != null && a.Snap.Reading.Online;
                if (online) live.Add(id);
                else asleep.Add(id);
            }

            live.AddRange(asleep);
            return live;
        }

        /// <summary>
        /// Rebuilds the display order in three tiers, each keeping arrival order:
        ///   1. reporting a level
        ///   3. asleep / unreachable
        /// Tiers 2 and 3 both sink to the right; a device that is awake but has not
        /// answered yet still outranks one that is asleep, because it is about to
        /// resolve on its own.
        /// Takes a copy first: VisibleOrder walks _order, so clearing it before
        /// reading would empty the list.
        /// </summary>
        private void SortOrder()
        {
            List<string> source = new List<string>(_order);
            List<string> live = new List<string>();
            List<string> asleep = new List<string>();

            foreach (string id in source)
            {
                CardAnim a;
                if (!_anim.TryGetValue(id, out a) || a.Leaving) continue;

                if (a.Snap == null || a.Snap.Reading == null) { asleep.Add(id); continue; }

                DeviceReading r = a.Snap.Reading;
                if (r.Online && r.Percent.HasValue) live.Add(id);
                else asleep.Add(id);
            }

            _order.Clear();
            _order.AddRange(live);
            _order.AddRange(asleep);
        }

        /// <summary>Moves the strip one slot; stops at either end.</summary>
        private void AdvancePage(int direction, bool wrap)
        {
            if (_slotMax <= 0) return;

            int next = _slot + direction;
            if (next > _slotMax) next = _slotMax;
            if (next < 0) next = 0;
            if (next == _slot) return;

            _slot = next;
            RelayoutCards();
            StartAnimating();
        }

        // ----------------------------------------------------------- animation

        /// <summary>Runs on the UI thread, once per frame message.</summary>
        private void OnTick()
        {
            Interlocked.Exchange(ref _framePending, 0);
            long now = _clock.ElapsedMilliseconds;
            float dt = (now - _lastTickMs) / 1000f;
            _lastTickMs = now;

            if (dt <= 0f) dt = 0.015f;
            if (dt > 0.12f) dt = 0.12f;   // never jump after a stall

            bool cardMotion;
            bool fast = Advance(dt, out cardMotion);
            bool dots = ShowDots;

            // Only card motion and the header's own animations need the fast clock.
            // The hint text just fades in and out, so it rides the 30 fps rate.
            int rate;
            if (cardMotion || _denying || _spinning) rate = FastInterval;
            else if (dots || _hintOn) rate = DotsInterval;
            else rate = IdleInterval;
            SetRate(rate);

            if (fast)
            {
                Invalidate();
            }
            else if (dots)
            {
                // Empty state: only the dot strip moves.
                _dotsPhase += dt / DotsSeconds;
                if (_dotsPhase >= 1f) _dotsPhase -= 1f;
                Invalidate(DotsBounds);
            }
            else if (IdleWave)
            {
                // Only the strip around each water line changes at rest, so the panel
                // repaints those few rows instead of the whole 700x470 window.
                // An empty result means every visible card is full (or there are none),
                // in which case a resting surface moves nothing at all.
                Rectangle band = SurfaceBands();
                if (!band.IsEmpty) Invalidate(band);
            }
            else
            {
                StopAnimating();
                Invalidate();
            }
        }

        /// <summary>Union of the thin strips where a resting water line can move.</summary>
        private Rectangle SurfaceBands()
        {
            int startY = CardTop;
            int amp = (int)(CardH * 0.024f) + 3;

            Rectangle band = Rectangle.Empty;
            foreach (CardAnim a in _anim.Values)
            {
                if (a.Leaving || a.Opacity < 1f) continue;

                // No liquid is drawn without a level, so the surface never moves.
                if (!HasLevel(a.Snap)) continue;

                float level = a.Shown / 100f;
                if (level >= 0.995f) continue;   // surface sits above the card top

                int surface = startY + (int)(CardH * (1f - level));
                Rectangle r = new Rectangle((int)Math.Round(a.X), surface - amp, CardW, amp * 2);
                band = band.IsEmpty ? r : Rectangle.Union(band, r);
            }

            return band;
        }

        /// <summary>Returns true while something needs the 60 fps path.</summary>
        private bool Advance(float dt, out bool cardMotion)
        {
            bool fast = false;
            List<string> dead = null;

            // Refresh-radio fade. Driven by the main clock like everything else, so
            // the panel only ever has one timer running.
            for (int i = 0; i < _rateT.Length; i++)
            {
                float target = RefreshOptions[i] == _refreshSeconds ? 1f : 0f;
                float delta = target - _rateT[i];

                if (Math.Abs(delta) < 0.02f)
                {
                    _rateT[i] = target;
                    continue;
                }

                // Same speed as the old 16 ms tick: 0.28 per frame at 60 fps.
                _rateT[i] += delta * Math.Min(1f, 0.28f * (dt / 0.016f));
                fast = true;
            }

            foreach (CardAnim a in _anim.Values)
            {
                if (a.Leaving)
                {
                    a.Opacity -= dt / LeaveSeconds;
                    if (a.Opacity <= 0f)
                    {
                        if (dead == null) dead = new List<string>();
                        dead.Add(a.Id);
                        a.Opacity = 0f;
                    }
                    fast = true;
                    continue;
                }

                if (a.Refilling)
                {
                    a.Refill += dt / RefillSeconds;
                    if (a.Refill >= 1f)
                    {
                        a.Refill = 1f;
                        a.Refilling = false;
                    }

                    // ease-out cubic reads as liquid settling, not as math
                    float t = a.Refill;
                    float e = 1f - (1f - t) * (1f - t) * (1f - t);
                    a.Shown = a.Target * e;
                    fast = true;
                }
                else
                {
                    float d = a.Target - a.Shown;
                    if (Math.Abs(d) > 0.04f)
                    {
                        a.Shown += d * (1f - (float)Math.Exp(-11f * dt));
                        if (Math.Abs(a.Target - a.Shown) < 0.04f) a.Shown = a.Target;
                        fast = true;
                    }
                }

                if (a.Opacity < 1f)
                {
                    a.Opacity += dt / FadeSeconds;
                    if (a.Opacity > 1f) a.Opacity = 1f;
                    fast = true;
                }

                // Dim whenever there is no live number to show: asleep, or awake but
                // still waiting for its first reading. Both sit on the right and both
                // read as "nothing to report", so they should look the same.
                bool live = HasLevel(a.Snap);
                float dimTarget = live ? 1f : OfflineDim;
                if (Math.Abs(a.Dim - dimTarget) > 0.002f)
                {
                    a.Dim += (dimTarget - a.Dim) * (1f - (float)Math.Exp(-13f * dt));
                    if (Math.Abs(a.Dim - dimTarget) < 0.002f) a.Dim = dimTarget;
                    fast = true;
                }

                // slide into the new slot when the row re-centres
                float dx = a.TargetX - a.X;
                if (Math.Abs(dx) > 0.35f)
                {
                    a.X += dx * (1f - (float)Math.Exp(-SlideStiffness * dt));
                    if (Math.Abs(a.TargetX - a.X) < 0.35f) a.X = a.TargetX;
                    fast = true;
                }
                else if (a.X != a.TargetX)
                {
                    a.X = a.TargetX;
                    fast = true;
                }
            }

            if (dead != null)
            {
                foreach (string id in dead)
                {
                    _anim.Remove(id);
                    _order.Remove(id);
                }
            }

            // Header icons ease on their own clock; they must never stir the liquid.
            bool uiFast = false;
            uiFast |= EaseTo(ref _refreshHot, _hotRefresh ? 1f : 0f, dt / HoverSeconds);
            uiFast |= EaseTo(ref _closeHot, _hotClose ? 1f : 0f, dt / HoverSeconds);
            uiFast |= EaseTo(ref _refreshPress, _pressRefresh ? 1f : 0f, dt / PressSeconds);
            uiFast |= EaseTo(ref _closePress, _pressClose ? 1f : 0f, dt / PressSeconds);

            if (_spinning)
            {
                _spinT += dt / SpinSeconds;
                if (_spinT >= 1f) { _spinT = 0f; _spinning = false; }
                uiFast = true;
            }

            if (_denying)
            {
                _denyT += dt / DenySeconds;
                if (_denyT >= 1f) { _denyT = 0f; _denying = false; }
                uiFast = true;
            }

            if (_hintOn)
            {
                _hintT += dt;
                if (_hintT >= HintSeconds) { _hintT = 0f; _hintOn = false; }
                uiFast = true;
            }

            // Only card motion excites the liquid; repainting may also be driven by icons.
            if (fast)
            {
                _energy = 1f;
            }
            else if (_energy > 0f)
            {
                _energy -= dt / CalmSeconds;
                if (_energy < 0f) _energy = 0f;
                fast = true;
            }

            // Always advance the phase: at rest the surface keeps a slow, shallow wave.
            float speed = IdleWaveSpeed + (WaveSpeed - IdleWaveSpeed) * _energy;
            _phase += dt * speed;
            if (_phase > 6.283185f) _phase -= 6.283185f;

            cardMotion = fast;
            return fast || uiFast;
        }

        /// <summary>Moves a value toward its target, returns true while it is still moving.</summary>
        private static bool EaseTo(ref float value, float target, float step)
        {
            if (value == target) return false;
            if (value < target) value = Math.Min(target, value + step);
            else value = Math.Max(target, value - step);
            return true;
        }

        /// <summary>Finishes every transition instantly (used when hiding the panel).</summary>
        private void SnapToRest()
        {
            List<string> dead = null;

            foreach (CardAnim a in _anim.Values)
            {
                if (a.Leaving)
                {
                    if (dead == null) dead = new List<string>();
                    dead.Add(a.Id);
                    continue;
                }

                a.Shown = a.Target;
                a.Refilling = false;
                a.Opacity = 1f;
                a.X = a.TargetX;
                a.Dim = (a.Snap != null && a.Snap.Reading != null && a.Snap.Reading.Online)
                    ? 1f : OfflineDim;
            }

            if (dead != null)
                foreach (string id in dead) { _anim.Remove(id); _order.Remove(id); }

            _energy = 0f;
        }

        // ----------------------------------------------------------- painting

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.HighQuality;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            e.Graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            // Layer order as designed: gradient, cards, vignette, then the wordmark
            // and header icons. The vignette sits ON TOP of the cards so a card
            // sliding out of the three visible slots dissolves into the panel edge
            // instead of being clipped by the window frame.
            Bitmap backdrop = BackgroundRenderer.GetBackdrop(Width, Height);
            Blit(e.Graphics, backdrop, new Rectangle(0, 0, Width, Height), 255);

            DrawCards(e.Graphics);

            BackgroundRenderer.DrawVignette(e.Graphics, Width, Height);

            Bitmap wordmark = BackgroundRenderer.GetWordmark(Width, Height);
            Blit(e.Graphics, wordmark, new Rectangle(0, 0, wordmark.Width, wordmark.Height), 255);

            DrawIcons(e.Graphics);
            DrawHint(e.Graphics);

            if (!ShowDots) DrawRefreshPicker(e.Graphics);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // The frame thread is a background thread, so it dies with the
                // process; just make sure it stops posting before the handle goes.
                try { StopAnimating(); } catch { }
            }
            base.Dispose(disposing);
        }

        /// <summary>
        /// One wheel notch = one slot. Handled at the window level because the panel
        /// has no focusable child controls, so WM_MOUSEWHEEL never reaches a control.
        /// </summary>
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmFrame) { OnTick(); return; }

            if (m.Msg == 0x020A)   // WM_MOUSEWHEEL
            {
                int delta = (short)(m.WParam.ToInt64() >> 16);
                if (delta != 0) AdvancePage(delta < 0 ? 1 : -1, false);
                return;
            }
            base.WndProc(ref m);
        }

        private void DrawIcons(Graphics g)
        {
            // The glyph's arrow points anticlockwise, so the spin follows it.
            float spin = _spinning ? -360f * EaseOutCubic(_spinT) : 0f;

            if (_denying)
            {
                // Refused refresh: a quick anticlockwise nudge, overshoot clockwise,
                // then settle. Two swings, damping to zero.
                float swing = (float)Math.Sin(_denyT * Math.PI * 4f) * 13f * (1f - _denyT);
                spin -= swing;
            }

            BackgroundRenderer.DrawIcons(
                g, Width, _scale,
                spin,
                _refreshHot,
                1f - (1f - PressScale) * _refreshPress,
                _closeHot,
                1f - (1f - PressScale) * _closePress);
        }

        /// <summary>
        /// "Wait a moment" hint, centred in the band between the bottom of the
        /// wordmark and the top of the cards.
        /// </summary>
        private void DrawHint(Graphics g)
        {
            if (!_hintOn) return;

            float a;
            if (_hintT < 0.14f) a = _hintT / 0.14f;
            else if (_hintT > HintSeconds - 0.34f) a = (HintSeconds - _hintT) / 0.34f;
            else a = 1f;
            if (a <= 0f) return;
            if (a > 1f) a = 1f;

            // Same face, size and colour as the empty-state hint lines, so the two
            // pieces of copy read as one system.
            Font font = FontManager.Get(FontKind.Chinese, 15f * _scale);
            float lineH = font.GetHeight(g);

            float bandTop = 66f * _scale;         // below the wordmark
            float bandBottom = CardTop;           // above the first card
            float top = (bandTop + bandBottom) / 2f - lineH / 2f;

            using (SolidBrush brush = new SolidBrush(
                       Color.FromArgb((int)(EmptyTextColor.A * a),
                                      EmptyTextColor.R,
                                      EmptyTextColor.G,
                                      EmptyTextColor.B)))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                sf.FormatFlags = StringFormatFlags.NoWrap;
                g.DrawString("刷新过于频繁，请稍候", font, brush,
                    new RectangleF(0f, top, Width, lineH), sf);
            }
        }

        private static float EaseOutCubic(float t)
        {
            if (t <= 0f) return 0f;
            if (t >= 1f) return 1f;
            float inv = 1f - t;
            return 1f - inv * inv * inv;
        }

        private void DrawCards(Graphics g)
        {
            int startY = CardTop;

            bool any = false;
            foreach (CardAnim a in _anim.Values)
            {
                if (a.Opacity > 0.01f) { any = true; break; }
            }

            if (!any)
            {
                DrawEmptyState(g);
                return;
            }

            // Ghosts first: while a leaving card fades, the live cards slide over it.
            foreach (CardAnim a in _anim.Values)
            {
                if (!a.Leaving || a.Opacity <= 0.01f) continue;
                DrawOneCard(g, new Rectangle((int)Math.Round(a.X), startY, CardW, CardH), a);
            }

            foreach (CardAnim a in _anim.Values)
            {
                if (a.Leaving || a.Opacity <= 0.01f) continue;
                DrawOneCard(g, new Rectangle((int)Math.Round(a.X), startY, CardW, CardH), a);
            }
        }

        /// <summary>
        /// Empty state: two lines of 汉仪旗黑 with the same tight leading the cards use,
        /// and a six-dot chase underneath that reads as "still looking".
        /// </summary>
        private void DrawEmptyState(Graphics g)
        {
            Font font = FontManager.Get(FontKind.Chinese, 15f * _scale);
            float lineH = font.GetHeight(g);
            float leading = 2f * _scale;          // gap between the two lines
            float dotsArea = 46f * _scale;        // room between the last line and the dots

            // The whole group (two lines + dots) is centred in the panel, both axes.
            float totalH = lineH * 2f + leading + dotsArea;
            float top = (Height - totalH) / 2f;

            using (SolidBrush brush = new SolidBrush(EmptyTextColor))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Near;
                sf.FormatFlags = StringFormatFlags.NoWrap;

                g.DrawString("没有检测到受支持的设备", font, brush,
                    new RectangleF(0f, top, Width, lineH), sf);

                g.DrawString("请确认接收器已插入，设备已开机", font, brush,
                    new RectangleF(0f, top + lineH + leading, Width, lineH), sf);
            }

            DrawDots(g, top + lineH * 2f + leading, dotsArea);
            DrawCredit(g);
        }

        /// <summary>
        /// Designer credit, only ever visible in the empty state — once a device shows
        /// up the panel belongs to the data, not to the author.
        /// </summary>
        private void DrawCredit(Graphics g)
        {
            Font font = FontManager.Get(FontKind.MontserratMedium, 14f * 4f / 3f * _scale);
            float lineH = font.GetHeight(g);
            float y = Height - 46f * _scale - lineH;

            using (SolidBrush brush = new SolidBrush(Color.FromArgb(200, 200, 200)))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Near;
                sf.FormatFlags = StringFormatFlags.NoWrap;

                g.DrawString("Designed by Joko", font, brush,
                    new RectangleF(0f, y, Width, lineH), sf);
            }
        }

        // ------------------------------------------------------- refresh rate

        internal static readonly int[] RefreshOptions = new int[] { 15, 30, 90 };

        private static readonly float[] RadioCx = { 389.75f, 458.74f, 527.73f };
        private static readonly float[] RadioTx = { 401.51f, 470.5f, 539.49f };

        public event EventHandler RefreshRateChanged;

        /// <summary>Seconds between polls while the panel is open.</summary>
        public int RefreshSeconds
        {
            get { return _refreshSeconds; }
            set
            {
                if (value != 15 && value != 30 && value != 90) value = 90;
                if (_refreshSeconds == value) return;

                _refreshSeconds = value;

                // Before the panel is on screen there is no frame clock to animate on,
                // so snap — otherwise the radios would show the previous choice until
                // the first repaint.
                if (!Visible)
                {
                    for (int i = 0; i < _rateT.Length; i++)
                    {
                        _rateT[i] = RefreshOptions[i] == value ? 1f : 0f;
                    }
                }
                else
                {
                    StartAnimating();
                }
            }
        }

        private int _refreshSeconds = 90;
        private readonly float[] _rateT = new float[] { 0f, 0f, 1f };   // 90s is the default

        /// <summary>
        /// Fades the selected radio instead of snapping it: the ring cools to green
        /// and the inner dot grows in. Both values ride the same 0..1 progress.
        /// </summary>
        /// <summary>
        /// 15s / 30s / 90s radios along the bottom edge. Only drawn when cards are on
        /// screen — in the empty state that strip carries the designer credit instead.
        /// </summary>
        private void DrawRefreshPicker(Graphics g)
        {
            float s = _scale;
            Font font = FontManager.Get(FontKind.MontserratMedium, 15f * s);
            float baseline = 528.29f * s;
            float cy = 522.98f * s;
            float ringR = 5.31f * s;
            float dotR = 2.9f * s;

            using (StringFormat sf = new StringFormat(StringFormat.GenericTypographic))
            {
                sf.FormatFlags |= StringFormatFlags.NoWrap;

                for (int i = 0; i < RefreshOptions.Length; i++)
                {
                    float t = _rateT[i];
                    float eased = t * t * (3f - 2f * t);   // smoothstep

                    Color idle = Color.FromArgb(135, 135, 135);
                    Color color = Mix(idle, CardRenderer.Accent, eased);

                    float cx = RadioCx[i] * s;
                    using (Pen ring = new Pen(color, Math.Max(1f, 1f * s)))
                    {
                        g.DrawEllipse(ring, cx - ringR, cy - ringR, ringR * 2f, ringR * 2f);
                    }

                    float r = dotR * eased;
                    if (r > 0.2f)
                    {
                        using (SolidBrush dot = new SolidBrush(color))
                        {
                            g.FillEllipse(dot, cx - r, cy - r, r * 2f, r * 2f);
                        }
                    }

                    using (SolidBrush text = new SolidBrush(color))
                    {
                        g.DrawString(RefreshOptions[i] + "s", font, text,
                                     RadioTx[i] * s, baseline - FontManager.Ascent(font), sf);
                    }
                }
            }
        }

        /// <summary>Linear colour interpolation; t is clamped by the caller.</summary>
        private static Color Mix(Color a, Color b, float t)
        {
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        /// <summary>Hit test for the three radios. Returns -1 when the click missed.</summary>
        private int RefreshHit(int x, int y)
        {
            float s = _scale;
            if (y < 505f * s || y > 545f * s) return -1;

            for (int i = 0; i < RefreshOptions.Length; i++)
            {
                float left = (RadioCx[i] - 8f) * s;
                float right = (RadioTx[i] + 34f) * s;
                if (x >= left && x <= right) return i;
            }

            return -1;
        }

        /// <summary>
        /// The bounding box the dots can ever occupy, including the hop. Repainting
        /// only this strip keeps the empty state at a fraction of a percent of a core
        /// instead of redrawing a 941x300 band 30 times a second.
        /// </summary>
        private Rectangle DotsBounds
        {
            get
            {
                int w = (int)(86f * _scale);
                int h = (int)(52f * _scale);
                int cy = _dotsRestY > 0 ? (int)(_dotsRestY - 20f * _scale)
                                        : CardTop + CardH / 2;
                return new Rectangle(Width / 2 - w / 2, cy, w, h);
            }
        }

        private float _dotsRestY;

        /// <summary>
        /// Six dots hop up in sequence and settle back, driven by _dotsPhase.
        /// Each dot is only airborne for the first half of its own cycle, which is
        /// what makes them read as a chase rather than a pulsing row.
        /// </summary>
        private void DrawDots(Graphics g, float top, float height)
        {
            float radius = 3f * _scale;
            float step = 12f * _scale;
            float total = (DotsCount - 1) * step;
            float x0 = Width / 2f - total / 2f;
            float restY = top + height - radius - 3f * _scale;
            float liftPx = 8f * _scale;
            _dotsRestY = restY;

            using (SolidBrush brush = new SolidBrush(DotsColor))
            {
                for (int i = 0; i < DotsCount; i++)
                {
                    float t = _dotsPhase - i * 0.075f;
                    if (t < 0f) t += 1f;

                    float lift = t < 0.5f ? (float)Math.Sin(t / 0.5f * Math.PI) : 0f;
                    float cy = restY - lift * liftPx;

                    // dim while resting, bright at the top of the hop
                    int alpha = 78 + (int)(125f * lift);
                    brush.Color = Color.FromArgb(alpha, DotsColor);

                    g.FillEllipse(brush, x0 + i * step - radius, cy - radius,
                                  radius * 2f, radius * 2f);
                }
            }
        }

        /// <summary>True when nothing is on screen but the empty-state hint.</summary>
        private bool ShowDots
        {
            get
            {
                foreach (CardAnim a in _anim.Values)
                {
                    if (a.Opacity > 0.01f) return false;
                }
                return true;
            }
        }

        /// <summary>
        /// Three pre-baked layers per card, composited in order:
        /// card body+grid+empty bolt, the liquid, then the type.
        /// </summary>
        private void DrawOneCard(Graphics g, Rectangle card, CardAnim a)
        {
            int alpha = (int)(255f * a.Opacity * a.Dim);
            if (alpha <= 2) return;

            Blit(g, CardRenderer.GetUnder(card), card, alpha);

            // A leaving card has already been marked offline, but its ghost must keep
            // showing the level it had when it vanished. A card that is awake but has
            // no level yet ("waiting for a reading") shows no liquid at all: there is
            // nothing to fill it to, and a leftover level would read as real.
            bool hadLiquid = a.Leaving
                ? (a.Snap != null && a.Snap.Reading != null && a.Snap.Reading.Online)
                : HasLevel(a.Snap);

            if (hadLiquid)
            {
                float amp = IdleAmp + (1f - IdleAmp) * _energy;
                CardRenderer.DrawLiquid(g, card, a.Shown, _phase, amp, a.Target);
            }

            // Type sits on top of the liquid, so it is a separate layer.
            Blit(g, CardRenderer.GetTextLayer(card, a.Snap, a.Target), card, alpha);
        }

        /// <summary>True when the card has an actual percentage to draw liquid for.</summary>
        private static bool HasLevel(DeviceSnapshot s)
        {
            return s != null && s.Reading != null && s.Reading.Online && s.Reading.Percent.HasValue;
        }

        private static void Blit(Graphics g, Bitmap bmp, Rectangle dest, int alpha)
        {
            InterpolationMode oldInterp = g.InterpolationMode;
            PixelOffsetMode oldOffset = g.PixelOffsetMode;

            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;

            if (alpha >= 255)
            {
                g.DrawImage(bmp, dest, 0, 0, bmp.Width, bmp.Height, GraphicsUnit.Pixel);
            }
            else
            {
                using (ImageAttributes ia = new ImageAttributes())
                {
                    ColorMatrix cm = new ColorMatrix();
                    cm.Matrix33 = alpha / 255f;
                    ia.SetColorMatrix(cm, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
                    g.DrawImage(bmp, dest, 0, 0, bmp.Width, bmp.Height, GraphicsUnit.Pixel, ia);
                }
            }

            g.InterpolationMode = oldInterp;
            g.PixelOffsetMode = oldOffset;
        }

        // ------------------------------------------------------------ window

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            TryRoundCorners();
        }

        private void TryRoundCorners()
        {
            try
            {
                int pref = 2; // DWMWCP_ROUND
                DwmSetWindowAttribute(Handle, 33, ref pref, sizeof(int));
            }
            catch
            {
            }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= 0x20000; // CS_DROPSHADOW
                return cp;
            }
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            // re-centre the row; existing cards glide to their new slot
            RelayoutCards();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);

            if (_frameThread == null) return;

            if (Visible)
            {
                StartAnimating();
                SetRate(IdleInterval);
                Invalidate();
            }
            else
            {
                StopAnimating();
                SnapToRest();
            }
        }

        // ------------------------------------------------------------ input

        private const int IconNone = 0, IconRefresh = 1, IconClose = 2;

        /// <summary>Which icon is under a client-space point (IconNone = nothing).</summary>
        private int HitIcon(int x, int y)
        {
            return BackgroundRenderer.HitTest(x, y, Width, _scale);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            int over = HitIcon(e.X, e.Y);

            bool hotRefresh = over == IconRefresh;
            bool hotClose = over == IconClose;

            if (hotRefresh != _hotRefresh || hotClose != _hotClose)
            {
                _hotRefresh = hotRefresh;
                _hotClose = hotClose;
                Cursor = (over == IconNone) ? Cursors.Default : Cursors.Hand;
                StartAnimating();
            }

            if (!_dragging) return;

            if (Math.Abs(e.X - _dragStart.X) > 3 || Math.Abs(e.Y - _dragStart.Y) > 3)
            {
                Point screen = PointToScreen(new Point(e.X, e.Y));
                Location = new Point(screen.X - _dragStart.X, screen.Y - _dragStart.Y);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);

            if (e.Button != MouseButtons.Left) return;

            // The radio strip is not a drag handle. Without this the click starts a
            // window drag, and OnMouseUp then sees wasDragging=true and drops the
            // selection — the picker looked frozen.
            if (RefreshHit(e.X, e.Y) >= 0) return;

            int over = HitIcon(e.X, e.Y);
            if (over == IconNone)
            {
                _dragging = true;
                _dragStart = new Point(e.X, e.Y);
                return;
            }

            _pressRefresh = over == IconRefresh;
            _pressClose = over == IconClose;
            StartAnimating();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);

            bool wasDragging = _dragging;
            _dragging = false;

            int over = HitIcon(e.X, e.Y);

            if (true)   // radios are not icons: always evaluate on release
            {
                _pressRefresh = false;
                _pressClose = false;

                int rateHit = wasDragging ? -1 : RefreshHit(e.X, e.Y);

                if (rateHit >= 0)
                {
                    RefreshSeconds = RefreshOptions[rateHit];
                    if (RefreshRateChanged != null) RefreshRateChanged(this, EventArgs.Empty);
                }
                else if (!wasDragging && over == IconRefresh) TryRefresh();
                else if (!wasDragging && over == IconClose) Hide();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hotRefresh = false;
            _hotClose = false;
            _pressRefresh = false;
            _pressClose = false;
            Cursor = Cursors.Default;
            StartAnimating();
        }
    }
}
