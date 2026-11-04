using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using Voltide.Core;

namespace Voltide.UI
{
    /// <summary>
    /// Renders one card using the exact geometry exported from Illustrator.
    ///
    /// Designer reference: viewBox 0 0 220 300
    ///   card      : rect 220x300, rx 30, #0f0f0f
    ///   grid      : #1e1e1e hairlines
    ///   bolt      : single path, #74dd7c, clipped by the artboard on the right
    ///   type      : Montserrat Medium/SemiBold, 汉仪旗黑 70S
    ///
    /// Layering (cheap on purpose):
    ///   Under  - shadowless card body + grid + empty bolt, baked once per size at 1x
    ///   Liquid - the only thing that moves, rendered at 2x inside the bolt's
    ///            bounding box only, then scaled down once
    ///   Text   - baked once per value change, blitted at 1x
    /// The frame loop never re-renders the whole card.
    /// </summary>
    public static class CardRenderer
    {
        public static readonly Color CardBg = Color.FromArgb(15, 15, 15);      // #0f0f0f
        // Liquid ramp, straight from 设备卡片-完整版本.svg (vertical, card height).
        // Ordered bottom -> surface: position 0 is the card bottom.

        public static readonly Color Accent = Color.FromArgb(116, 221, 124);   // #74dd7c

        // Charge tiers from 主界面-电量颜色渐变.svg. Index 0 is the main colour (used
        // by the percentage); the gradient runs 0 / 0.5 / 1 so the upper half of the
        // bolt stays saturated and only the bottom deepens.
        private static readonly Color[] GreenRamp =
        {
            Color.FromArgb(0x74, 0xdd, 0x7c),
            Color.FromArgb(0x74, 0xdd, 0x7c),
            Color.FromArgb(0x00, 0x89, 0x52)
        };

        private static readonly Color[] AmberRamp =
        {
            Color.FromArgb(0xfb, 0xd5, 0x84),
            Color.FromArgb(0xfb, 0xd5, 0x84),
            Color.FromArgb(0xb1, 0x84, 0x1d)
        };

        private static readonly Color[] RedRamp =
        {
            Color.FromArgb(0xfc, 0x86, 0x84),
            Color.FromArgb(0xfc, 0x86, 0x84),
            Color.FromArgb(0x89, 0x27, 0x00)
        };

        /// <summary>Green at 50%+, amber at 20%+, red below that.</summary>
        public static Color[] RampFor(int percent)
        {
            if (percent >= 50) return GreenRamp;
            if (percent >= 20) return AmberRamp;
            return RedRamp;
        }

        /// <summary>Primary colour for the percentage text.</summary>
        public static Color PrimaryFor(int percent)
        {
            return RampFor(percent)[0];
        }
        public static readonly Color BoltIdle = Color.FromArgb(79, 79, 79);    // #4f4f4f
        private static readonly Color GridLine = Color.FromArgb(30, 30, 30);   // #1e1e1e

        // A hairline inside the card edge. Without it, #0f0f0f on a #141414 panel has
        // no readable silhouette and the 30px corners look square.
        private static readonly Color Rim = Color.FromArgb(26, 255, 255, 255);

        private static readonly Color TextBrand = Color.FromArgb(229, 203, 179);   // #e5cbb3
        private static readonly Color TextModel = Color.FromArgb(178, 158, 140);   // #b29e8c
        private static readonly Color TextMuted = Color.FromArgb(135, 135, 135);   // #878787
        private static readonly Color TextWhite = Color.FromArgb(255, 255, 255);   // #ffffff

        public const float DesignW = 220f;
        public const float DesignH = 300f;
        private const float DesignRadius = 30f;

        // The bolt never reaches left of x=85.32, so the animated layer only needs
        // this slice instead of the full 220px card width.
        private const float LiquidX = 82f;
        private const float LiquidW = 139f;
        private const int LiquidScale = 2;

        private static readonly Dictionary<string, Bitmap> _underCache = new Dictionary<string, Bitmap>();
        private static readonly Dictionary<string, Layer> _textCache = new Dictionary<string, Layer>();
        private static readonly Dictionary<string, Region> _clipCache = new Dictionary<string, Region>();

        private static Bitmap _liquidBuf;
        private static Graphics _liquidG;
        private static int _liquidBufW, _liquidBufH;
        private static PointF[] _waterPts;
        private const int WaterSteps = 56;

        private static string _cacheSize;

        private class Layer
        {
            public Bitmap Bmp;
            public string Signature;
        }

        // ------------------------------------------------------------ layers

        /// <summary>
        /// Body + hairlines + empty bolt, 1x, transparent background.
        /// Built at 2x and downsampled once, then reused for every frame.
        /// </summary>
        public static Bitmap GetUnder(RectangleF card)
        {
            int ow = Math.Max(1, (int)Math.Round(card.Width));
            int oh = Math.Max(1, (int)Math.Round(card.Height));
            string key = ow + "x" + oh;
            UseSize(key);

            Bitmap cached;
            if (_underCache.TryGetValue(key, out cached)) return cached;

            int pw = ow * 2;
            int ph = oh * 2;

            Bitmap bmp = new Bitmap(ow, oh, PixelFormat.Format32bppPArgb);

            using (Bitmap big = new Bitmap(pw, ph, PixelFormat.Format32bppPArgb))
            {
                using (Graphics bg = Graphics.FromImage(big))
                {
                    bg.SmoothingMode = SmoothingMode.HighQuality;
                    bg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    bg.ScaleTransform(pw / card.Width, ph / card.Height);

                    RectangleF local = new RectangleF(0f, 0f, card.Width, card.Height);
                    float radius = DesignRadius * (card.Width / DesignW);

                    using (GraphicsPath body = RoundRect(local, radius))
                    {
                        using (SolidBrush fill = new SolidBrush(CardBg))
                        {
                            bg.FillPath(fill, body);
                        }

                        // Grid first, bolt second: in the Illustrator file the bolt is
                        // drawn over the guides, so the hairlines must not cross it.
                        DrawGridLines(bg, local, card.Width / DesignW, card.Height / DesignH, radius);

                        // The SVG clips the bolt with the artboard. Without this the
                        // shape spills past the rounded corner and eats it.
                        GraphicsState st = bg.Save();
                        bg.SetClip(body);

                        using (GraphicsPath bolt = BoltPath(local))
                        using (SolidBrush idle = new SolidBrush(BoltIdle))
                        {
                            bg.FillPath(idle, bolt);
                        }

                        bg.Restore(st);

                        using (Pen pen = new Pen(Rim, 1f))
                        using (GraphicsPath inner = RoundRect(
                                   RectangleF.Inflate(local, -0.5f, -0.5f), radius - 0.5f))
                        {
                            bg.DrawPath(pen, inner);
                        }
                    }
                }

                using (Graphics tg = Graphics.FromImage(bmp))
                {
                    tg.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    tg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    tg.DrawImage(big, new Rectangle(0, 0, ow, oh));
                }
            }

            _underCache[key] = bmp;
            return bmp;
        }

        /// <summary>
        /// The moving part: liquid clipped to the bolt, inside the bolt's x-range only.
        /// Everything outside the surface line stays transparent so the cached card
        /// shows through untouched.
        /// </summary>
        public static void DrawLiquid(Graphics screen, RectangleF card,
                                      float pct, float phase, float energy, float colorPct)
        {
            if (pct <= 0.2f) return;
            if (pct > 100f) pct = 100f;
            if (energy < 0f) energy = 0f;
            if (energy > 1f) energy = 1f;

            float amp = card.Height * 0.024f * energy;

            // Artboard units -> this card's pixels (DPI scaling).
            float k = card.Width / DesignW;
            float liquidX = LiquidX * k;
            float liquidW = LiquidW * k;

            int bw = (int)Math.Round(liquidW * LiquidScale);
            int bh = (int)Math.Round(card.Height * LiquidScale);

            if (_liquidBuf == null || _liquidBufW != bw || _liquidBufH != bh)
            {
                if (_liquidG != null) { _liquidG.Dispose(); _liquidG = null; }
                if (_liquidBuf != null) _liquidBuf.Dispose();

                _liquidBuf = new Bitmap(bw, bh, PixelFormat.Format32bppPArgb);
                _liquidG = Graphics.FromImage(_liquidBuf);
                _liquidG.SmoothingMode = SmoothingMode.AntiAlias;
                _liquidG.PixelOffsetMode = PixelOffsetMode.HighQuality;
                _liquidBufW = bw;
                _liquidBufH = bh;
            }

            // The clip (card corner radius intersected with the bolt) never changes
            // for a given card size, so it is built once as a device-space Region.
            // Building it from paths every frame was the single most expensive step.
            _liquidG.ResetTransform();
            _liquidG.Clear(Color.Transparent);
            _liquidG.SetClip(GetClip(card), CombineMode.Replace);

            BuildWater(card, pct, phase, amp, k);

            // Two-stop ramp pinned to the card: bright at the top edge, deep at the
            // bottom. The axis must be in BUFFER coordinates (0 .. bh), not the card's
            // screen coordinates — the liquid is drawn into its own offscreen bitmap,
            // so using card.Top/card.Bottom put the ramp halfway outside the buffer
            // and clamped both ends, which is what showed up as flat bands.
            // Colour follows the real level, not the animated one: during a refill the
            // surface sweeps up from the bottom, and reading the ramp off that sweep
            // made a 90% card flash red -> amber -> green on every refresh.
            Color[] ramp = RampFor((int)Math.Round(colorPct));

            using (LinearGradientBrush wb = new LinearGradientBrush(
                new PointF(0f, 0f),
                new PointF(0f, bh),
                ramp[0], ramp[2]))
            {
                ColorBlend blend = new ColorBlend();
                blend.Positions = new float[] { 0f, 0.5f, 1f };
                blend.Colors = ramp;
                wb.InterpolationColors = blend;

                _liquidG.FillPolygon(wb, _waterPts);
            }

            Rectangle dest = Rectangle.Round(new RectangleF(
                card.Left + liquidX, card.Top, liquidW, card.Height));

            InterpolationMode oldInterp = screen.InterpolationMode;
            PixelOffsetMode oldOffset = screen.PixelOffsetMode;

            // Plain Bilinear, not HighQualityBilinear: at an exact 2:1 reduction both
            // are a 2x2 box filter, but GDI+ has a fast path for the former
            // (~0.57 ms vs ~1.23 ms per card, measured).
            screen.InterpolationMode = InterpolationMode.Bilinear;
            screen.PixelOffsetMode = PixelOffsetMode.HighQuality;

            screen.DrawImage(_liquidBuf, dest, 0, 0, _liquidBuf.Width, _liquidBuf.Height,
                             GraphicsUnit.Pixel);

            screen.InterpolationMode = oldInterp;
            screen.PixelOffsetMode = oldOffset;
        }

        /// <summary>
        /// Card-corners ∩ bolt, already scaled into the liquid buffer's pixel space.
        /// A Region is 1-bit, exactly like Graphics.SetClip(path), so the quality after
        /// the 2x downscale is unchanged.
        /// </summary>
        private static Region GetClip(RectangleF card)
        {
            int cw = Math.Max(1, (int)Math.Round(card.Width));
            int ch = Math.Max(1, (int)Math.Round(card.Height));
            string key = cw + "x" + ch;
            UseSize(key);

            Region r;
            if (_clipCache.TryGetValue(key, out r)) return r;

            RectangleF local = new RectangleF(0f, 0f, cw, ch);
            float radius = DesignRadius * (cw / DesignW);

            using (GraphicsPath body = RoundRect(local, radius))
            using (GraphicsPath bolt = BoltPath(local))
            {
                r = new Region(body);
                r.Intersect(bolt);
            }

            using (Matrix m = new Matrix())
            {
                m.Scale(LiquidScale, LiquidScale);
                m.Translate(-LiquidX * (cw / DesignW) * LiquidScale, 0f, MatrixOrder.Append);
                r.Transform(m);
            }

            _clipCache[key] = r;
            return r;
        }

        /// <summary>Fills the shared point buffer with the surface polygon, in buffer pixels.</summary>
        private static void BuildWater(RectangleF card, float pct, float phase, float amp, float k)
        {
            int need = WaterSteps + 5;
            if (_waterPts == null || _waterPts.Length != need) _waterPts = new PointF[need];

            float w = card.Width;
            float h = card.Height;
            float waterTop = h - h * (pct / 100f);
            float liquidX = LiquidX * k;
            float pad = 4f * k;

            // at full charge the sine crest must not dip below the card top,
            // otherwise a sliver of the empty bolt shows through
            if (pct >= 99.5f) waterTop = Math.Min(waterTop - amp * 3f, -k);

            int n = 0;
            _waterPts[n++] = new PointF((-pad - liquidX) * LiquidScale, (h + pad) * LiquidScale);
            _waterPts[n++] = new PointF((-pad - liquidX) * LiquidScale, waterTop * LiquidScale);

            for (int i = 0; i <= WaterSteps; i++)
            {
                float x = -pad + (w + pad * 2f) * i / WaterSteps;
                float y = waterTop + (float)Math.Sin(i / (float)WaterSteps * Math.PI * 2f + phase) * amp;
                _waterPts[n++] = new PointF((x - liquidX) * LiquidScale, y * LiquidScale);
            }

            _waterPts[n++] = new PointF((w + pad - liquidX) * LiquidScale, waterTop * LiquidScale);
            _waterPts[n++] = new PointF((w + pad - liquidX) * LiquidScale, (h + pad) * LiquidScale);
        }

        /// <summary>
        /// All type on the card, drawn once at 2x and cached at 1x.
        /// The caller blits it over the animated card, which keeps the glyphs
        /// out of the per-frame scaling work.
        /// </summary>
        public static Bitmap GetTextLayer(RectangleF card, DeviceSnapshot snap, float lastPct)
        {
            CardText text = CardText.From(snap, lastPct);
            int ow = Math.Max(1, (int)Math.Round(card.Width));
            int oh = Math.Max(1, (int)Math.Round(card.Height));
            int pw = ow * 2;
            int ph = oh * 2;

            UseSize(ow + "x" + oh);

            string key = IdOf(snap) + "@" + ow + "x" + oh + "|" + lastPct;
            string sig = text.Signature;

            Layer layer;
            if (_textCache.TryGetValue(key, out layer) && layer.Signature == sig)
                return layer.Bmp;

            if (layer != null && layer.Bmp != null) layer.Bmp.Dispose();

            Bitmap bmp = new Bitmap(ow, oh, PixelFormat.Format32bppPArgb);
            using (Bitmap big = new Bitmap(pw, ph, PixelFormat.Format32bppPArgb))
            {
                using (Graphics bg = Graphics.FromImage(big))
                {
                    bg.SmoothingMode = SmoothingMode.HighQuality;
                    bg.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    bg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    bg.ScaleTransform(pw / card.Width, ph / card.Height);

                    DrawText(bg, new RectangleF(0f, 0f, card.Width, card.Height), text);
                }

                using (Graphics tg = Graphics.FromImage(bmp))
                {
                    tg.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    tg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    tg.DrawImage(big, new Rectangle(0, 0, ow, oh));
                }
            }

            layer = new Layer();
            layer.Bmp = bmp;
            layer.Signature = sig;
            _textCache[key] = layer;
            return bmp;
        }

        private static string IdOf(DeviceSnapshot snap)
        {
            return (snap != null && snap.Info != null && snap.Info.Id != null) ? snap.Info.Id : "none";
        }

        /// <summary>
        /// Only one card size is ever on screen at a time, so a size change drops every
        /// cached layer instead of accumulating one set per pixel of window resize.
        /// </summary>
        private static void UseSize(string sizeKey)
        {
            if (_cacheSize == sizeKey) return;
            _cacheSize = sizeKey;

            foreach (Bitmap b in _underCache.Values) b.Dispose();
            _underCache.Clear();

            foreach (Layer l in _textCache.Values) if (l.Bmp != null) l.Bmp.Dispose();
            _textCache.Clear();

            foreach (Region r in _clipCache.Values) r.Dispose();
            _clipCache.Clear();
        }

        // ---------------------------------------------------------- geometry

        /// <summary>
        /// Hairline grid from the Illustrator file: one vertical at x=22.01 and
        /// six horizontals, colour #1e1e1e, width 0.75, clipped to the card radius.
        /// </summary>
        private static void DrawGridLines(Graphics g, RectangleF card, float sx, float sy,
                                          float radius)
        {
            using (GraphicsPath clip = RoundRect(card, radius))
            {
                GraphicsState st = g.Save();
                g.SetClip(clip);

                using (Pen p = new Pen(GridLine, Math.Max(0.6f, 0.75f * sx)))
                {
                    float vx = card.Left + 22.01f * sx;
                    g.DrawLine(p, vx, card.Top, vx, card.Bottom);

                    float[] ys = new float[] { 21.91f, 49.8f, 60.82f, 97.44f, 225.35f, 250.61f };
                    for (int i = 0; i < ys.Length; i++)
                    {
                        float y = card.Top + ys[i] * sy;
                        g.DrawLine(p, card.Left, y, card.Right, y);
                    }
                }

                g.Restore(st);
            }
        }

        /// <summary>
        /// Bolt path lifted verbatim from the Illustrator SVG:
        ///   M109.22,300
        ///   l110.78,-129.51  v-64.4  h-22.69  l22.69,-35.84  V30
        ///   C220,13.43 206.57,0 190,0
        ///   h-23.55  l-81.13,161.97
        ///   c-1.96,3.91 0.88,8.51 5.26,8.51
        ///   h54.29  l-54.44,129.52  h18.81  Z
        /// </summary>
        public static GraphicsPath BoltPath(RectangleF card)
        {
            float sx = card.Width / DesignW;
            float sy = card.Height / DesignH;

            GraphicsPath p = new GraphicsPath();

            PointF cur = new PointF(card.Left + 109.22f * sx, card.Top + 300f * sy);
            PointF first = cur;

            PointF n1 = new PointF(card.Left + 220.00f * sx, card.Top + 170.49f * sy);
            p.AddLine(cur, n1); cur = n1;

            PointF n2 = new PointF(card.Left + 220.00f * sx, card.Top + 106.09f * sy);
            p.AddLine(cur, n2); cur = n2;

            PointF n3 = new PointF(card.Left + 197.31f * sx, card.Top + 106.09f * sy);
            p.AddLine(cur, n3); cur = n3;

            PointF n4 = new PointF(card.Left + 220.00f * sx, card.Top + 70.25f * sy);
            p.AddLine(cur, n4); cur = n4;

            PointF n5 = new PointF(card.Left + 220.00f * sx, card.Top + 30.00f * sy);
            p.AddLine(cur, n5); cur = n5;

            PointF n6 = new PointF(card.Left + 190.00f * sx, card.Top + 0.00f * sy);
            p.AddBezier(cur,
                new PointF(card.Left + 220.00f * sx, card.Top + 13.43f * sy),
                new PointF(card.Left + 206.57f * sx, card.Top + 0.00f * sy),
                n6);
            cur = n6;

            PointF n7 = new PointF(card.Left + 166.45f * sx, card.Top + 0.00f * sy);
            p.AddLine(cur, n7); cur = n7;

            PointF n8 = new PointF(card.Left + 85.32f * sx, card.Top + 161.97f * sy);
            p.AddLine(cur, n8); cur = n8;

            PointF n9 = new PointF(card.Left + 90.58f * sx, card.Top + 170.48f * sy);
            p.AddBezier(cur,
                new PointF(card.Left + 83.36f * sx, card.Top + 165.88f * sy),
                new PointF(card.Left + 86.20f * sx, card.Top + 170.48f * sy),
                n9);
            cur = n9;

            PointF n10 = new PointF(card.Left + 144.87f * sx, card.Top + 170.48f * sy);
            p.AddLine(cur, n10); cur = n10;

            PointF n11 = new PointF(card.Left + 90.43f * sx, card.Top + 300.00f * sy);
            p.AddLine(cur, n11); cur = n11;

            PointF n12 = new PointF(card.Left + 109.24f * sx, card.Top + 300.00f * sy);
            p.AddLine(cur, n12); cur = n12;

            p.AddLine(cur, first);
            p.CloseFigure();

            return p;
        }

        public static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            GraphicsPath p = new GraphicsPath();
            float d = radius * 2f;
            p.AddArc(r.X, r.Y, d, d, 180f, 90f);
            p.AddArc(r.Right - d, r.Y, d, d, 270f, 90f);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0f, 90f);
            p.AddArc(r.X, r.Bottom - d, d, d, 90f, 90f);
            p.CloseFigure();
            return p;
        }

        // -------------------------------------------------------------- text

        private struct CardText
        {
            public string Brand;
            public string Model;
            public string Pct;
        public Color PctColor;
            public string Status;
            public string Source;
            public string Time;

            public string Signature
            {
                get { return Brand + "|" + Model + "|" + Pct + "|" + Status + "|" + Source + "|" + Time; }
            }

            public static CardText From(DeviceSnapshot snap, float lastPct)
            {
                CardText t = new CardText();
                t.Brand = "UNKNOWN";
                t.Model = "";
                t.Pct = "--";
                t.Status = "未连接";
                t.Source = "";
                t.Time = "";

                if (snap == null) return t;

                if (snap.Info != null)
                {
                    if (snap.Info.Brand != null) t.Brand = snap.Info.Brand.ToUpperInvariant();
                    t.Model = DeviceAliases.Get(snap.Info.Id) ?? snap.Info.Model ?? "";
                }

                DeviceReading r = snap.Reading;
                if (r == null) return t;

                // Full charge reads "Full" rather than "100%": three digits plus the
                // sign overrun the designed width and collide with the bolt.
                // A device that has reported before keeps its last level while it
                // sleeps: "--" told the reader nothing, whereas a grey 48% says "still
                // paired, just idle". The liquid stays off, so it never looks live.
                bool live = r.Percent.HasValue;
                int? shown = live
                    ? r.Percent
                    : (lastPct > 0.5f ? (int?)Math.Round(lastPct) : null);

                t.PctColor = (live && shown.HasValue) ? PrimaryFor(shown.Value) : TextMuted;

                t.Pct = shown.HasValue
                    ? (shown.Value >= 100 ? "Full" : shown.Value.ToString() + "%")
                    : "--";

                // Three states only: charging, discharging, asleep. A device that is
                // awake but has not answered yet is shown as asleep — the distinction
                // meant nothing to the reader and only added a fourth look.
                if (r.Sleeping || !r.Online || !r.Percent.HasValue) t.Status = "设备休眠中";
                else if (r.Charging) t.Status = "电池充电中";
                else t.Status = "电池放电中";

                t.Source = r.Detail != null ? r.Detail : "";
                t.Time = (r.Online && r.UpdatedAt != default(DateTime))
                    ? r.UpdatedAt.ToString("HH:mm")
                    : "";
                return t;
            }
        }

        /// <summary>
        /// The bolt's left edge meets this baseline at x≈143, and the text starts at
        /// 23.86, so 118 design units is all the room a model name has before it runs
        /// under the lightning bolt.
        /// </summary>
        private const float ModelMaxWidth = 118f;

        /// <summary>
        /// Model names are kept short in the providers; this is the safety net for a
        /// long localised name or a newly mapped PID. Latin runs use Montserrat, CJK
        /// falls back to Alibaba PuHui Ti, and anything wider than the slot shrinks
        /// (never below 9 px) instead of running into the bolt.
        /// </summary>
        private static void DrawModel(Graphics g, string text, float x, float baselineY,
                                      float sx, Color color)
        {
            if (string.IsNullOrEmpty(text)) return;

            bool cjk = HasCjk(text);
            FontKind kind = cjk ? FontKind.Chinese : FontKind.MontserratMedium;

            float px = 13f * sx;
            float max = ModelMaxWidth * sx;

            Font font = FontManager.Get(kind, px);
            float width = Measure(g, text, font, cjk);

            if (width > max && width > 0f)
            {
                px = Math.Max(9f * sx, px * (max / width));
                font = FontManager.Get(kind, px);
            }

            DrawBaseline(g, text, font, x, baselineY, color, cjk);
        }

        /// <summary>Detail line: Latin in Montserrat, CJK in the Chinese face, side by side.</summary>
        private const float DetailMaxWidth = 74f;

        private static void DrawDetail(Graphics g, string text, float x, float baselineY,
                                       float sx, Color color)
        {
            if (string.IsNullOrEmpty(text)) return;

            float px = 11f * sx;
            float max = DetailMaxWidth * sx;

            float width = MeasureMixed(g, text, px);
            if (width > max && width > 0f)
            {
                px = Math.Max(8f * sx, px * (max / width));
            }

            DrawMixed(g, text, px, x, baselineY, color);
        }

        /// <summary>
        /// Draws one line with per-script faces: "XInput 四档" renders the Latin part in
        /// Montserrat and the two ideographs in Alibaba PuHui Ti, sharing one baseline.
        /// Both faces sit on the same y, so ascent differences do not shift the line.
        /// </summary>
        private static void DrawMixed(Graphics g, string text, float px,
                                      float x, float baselineY, Color color)
        {
            float cursor = x;
            int i = 0;

            while (i < text.Length)
            {
                bool cjk = IsCjk(text[i]);
                int start = i;

                while (i < text.Length && IsCjk(text[i]) == cjk) i++;

                string run = text.Substring(start, i - start);
                Font font = FontManager.Get(cjk ? FontKind.Chinese : FontKind.MontserratMedium, px);

                DrawBaseline(g, run, font, cursor, baselineY, color, cjk);
                cursor += Measure(g, run, font, cjk);
            }
        }

        private static float MeasureMixed(Graphics g, string text, float px)
        {
            float total = 0f;
            int i = 0;

            while (i < text.Length)
            {
                bool cjk = IsCjk(text[i]);
                int start = i;

                while (i < text.Length && IsCjk(text[i]) == cjk) i++;

                string run = text.Substring(start, i - start);
                Font font = FontManager.Get(cjk ? FontKind.Chinese : FontKind.MontserratMedium, px);
                total += Measure(g, run, font, cjk);
            }

            return total;
        }

        /// <summary>Han, CJK punctuation and fullwidth forms belong to the Chinese face.</summary>
        private static bool IsCjk(char c)
        {
            return (c >= 0x2E80 && c <= 0x9FFF)
                   || (c >= 0x3000 && c <= 0x303F)
                   || (c >= 0xFF00 && c <= 0xFFEF);
        }

        private static float Measure(Graphics g, string text, Font font, bool cjk)
        {
            using (StringFormat sf = cjk
                       ? new StringFormat()
                       : new StringFormat(StringFormat.GenericTypographic))
            {
                return g.MeasureString(text, font, int.MaxValue, sf).Width;
            }
        }

        internal static bool HasCjk(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;

            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] >= 0x2E80 && s[i] <= 0x9FFF) return true;
            }

            return false;
        }

        private static void DrawText(Graphics g, RectangleF card, CardText t)
        {
            float sx = card.Width / DesignW;
            float sy = card.Height / DesignH;
            float ox = card.Left;
            float oy = card.Top;

            // ---- brand (baseline 34.14, size 13, #e5cbb3, Medium 500) ----
            DrawBaseline(g, t.Brand, FontKind.MontserratMedium, 13f * sx,
                ox + 23.86f * sx, oy + 34.14f * sy, TextBrand);

            // ---- model (baseline 46.67, size 13, #b29e8c, Medium 500) ----
            DrawModel(g, t.Model, ox + 23.86f * sx, oy + 46.67f * sy, sx, TextModel);

            // ---- percentage (baseline 94.01, size 42.52, #74dd7c, SemiBold 600) ----
            // "--" means there is no reading at all, so it is drawn muted: a bright
            // green placeholder reads as a value at a glance.
            DrawBaseline(g, t.Pct, FontKind.MontserratSemiBold, 42.52f * sx,
                ox + 23.86f * sx, oy + 94.01f * sy,
                t.PctColor);

            // ---- status (baseline 109.14, size 10, #878787) ----
            DrawBaseline(g, t.Status, FontKind.Chinese, 10f * sx,
                ox + 23.86f * sx, oy + 109.14f * sy, TextMuted, true);

            // ---- source (baseline 236.14, size 11, #ffffff, Medium 500) ----
            // Detail can mix scripts ("XInput 四档"), so the face is chosen by content:
            // Latin runs keep Montserrat, anything with CJK uses the Chinese face.
            // The space before the bolt is ~74 units here, so a long detail shrinks
            // rather than sliding under it.
            DrawDetail(g, t.Source, ox + 23.86f * sx, oy + 236.14f * sy, sx, TextWhite);

            // ---- time (baseline 247.72, size 10, #878787, Medium 500) ----
            DrawBaseline(g, t.Time, FontKind.MontserratMedium, 10f * sx,
                ox + 23.86f * sx, oy + 247.72f * sy, TextMuted);
        }

        /// <summary>
        /// SVG positions text by baseline; GDI+ positions by top-left.
        /// Convert using the real font metrics so the two line up exactly.
        /// The Font object is cached by FontManager and must not be disposed here.
        /// </summary>
        private static void DrawBaseline(Graphics g, string text, FontKind kind, float px,
                                         float x, float baselineY, Color color,
                                         bool cjk = false)
        {
            DrawBaseline(g, text, FontManager.Get(kind, px), x, baselineY, color, cjk);
        }

        private static void DrawBaseline(Graphics g, string text, Font font,
                                         float x, float baselineY, Color color,
                                         bool cjk = false)
        {
            if (string.IsNullOrEmpty(text)) return;

            // GenericTypographic drops the per-glyph advance handling GDI+ uses for
            // CJK, which showed up as gaps inside "设备休眠" (it rendered as two
            // blobs). Latin runs keep it, because it gives exact kerning.
            StringFormat sf = cjk ? new StringFormat() : new StringFormat(StringFormat.GenericTypographic);

            using (sf)
            using (SolidBrush brush = new SolidBrush(color))
            {
                float ascent = FontManager.Ascent(font);
                g.DrawString(text, font, brush, x, baselineY - ascent, sf);
            }
        }

        /// <summary>Shrinks a font until the string fits maxWidth (floor at 9 px).</summary>
        private static Font Fit(Graphics g, string text, FontKind kind, float px, float maxWidth)
        {
            Font font = FontManager.Get(kind, px);
            if (string.IsNullOrEmpty(text) || maxWidth <= 0f) return font;

            using (StringFormat sf = new StringFormat(StringFormat.GenericTypographic))
            {
                for (int i = 0; i < 10; i++)
                {
                    if (g.MeasureString(text, font, 10000, sf).Width <= maxWidth) break;
                    if (px <= 9f) break;
                    px = Math.Max(9f, px * 0.94f);
                    font = FontManager.Get(kind, px);
                }
            }
            return font;
        }
    }
}
