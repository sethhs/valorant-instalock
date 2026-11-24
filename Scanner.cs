using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Instalock
{
    public class Look
    {
        public const int N = 6;
        public const float MinContrast = 6;
        public readonly float[] Z = new float[N * N * 3];
        public readonly float[] Weight = new float[N * N];
        public float WeightSum;

        public static Look FromIcon(Bitmap icon, bool solid)
        {
            const int cell = 8;
            var look = new Look();
            var rgb = new float[N * N * 3];
            using var small = new Bitmap(N * cell, N * cell, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(icon, 0, 0, small.Width, small.Height);
            }
            var px = Scanner.Pixels(small);
            for (int cy = 0; cy < N; cy++)
                for (int cx = 0; cx < N; cx++)
                {
                    double r = 0, gr = 0, b = 0, a = 0;
                    for (int y = cy * cell; y < (cy + 1) * cell; y++)
                        for (int x = cx * cell; x < (cx + 1) * cell; x++)
                        {
                            int p = px[y * small.Width + x];
                            double al = ((p >> 24) & 255) / 255.0;
                            r += ((p >> 16) & 255) * al;
                            gr += ((p >> 8) & 255) * al;
                            b += (p & 255) * al;
                            a += al;
                        }
                    int i = cy * N + cx;
                    double coverage = a / (cell * cell);
                    double div = solid ? cell * cell : a;
                    if (!solid && coverage < 0.6) continue;
                    look.Weight[i] = solid ? 1 : (float)coverage;
                    rgb[i * 3] = (float)(r / div);
                    rgb[i * 3 + 1] = (float)(gr / div);
                    rgb[i * 3 + 2] = (float)(b / div);
                    look.WeightSum += look.Weight[i];
                }
            Normalize(rgb, look.Weight, look.WeightSum, look.Z);
            return look;
        }

        public static float Normalize(Span<float> rgb, float[] weight, float weightSum, Span<float> z)
        {
            float contrast = 0;
            for (int ch = 0; ch < 3; ch++)
            {
                float mean = 0, var = 0;
                for (int i = 0; i < N * N; i++) mean += weight[i] * rgb[i * 3 + ch];
                mean /= weightSum;
                for (int i = 0; i < N * N; i++)
                {
                    float d = rgb[i * 3 + ch] - mean;
                    var += weight[i] * d * d;
                }
                float std = (float)Math.Sqrt(var / weightSum);
                contrast += std / 3;
                float inv = 1 / Math.Max(std, 1);
                for (int i = 0; i < N * N; i++) z[i * 3 + ch] = (rgb[i * 3 + ch] - mean) * inv;
            }
            return contrast;
        }
    }

    public class Frame
    {
        public int W, H;
        public double Scale;
        public Rectangle Bounds;
        public int Teal;
        uint[] ir, ig, ib;
        int[] px;
        Bitmap small;

        public static Frame From(Bitmap shot, Rectangle bounds, int workHeight, Frame recycle = null)
        {
            double scale = (double)bounds.Height / workHeight;
            int w = (int)Math.Round(bounds.Width / scale), stride = w + 1;
            var f = recycle != null && recycle.W == w && recycle.H == workHeight ? recycle : new Frame
            {
                W = w,
                H = workHeight,
                small = new Bitmap(w, workHeight, PixelFormat.Format32bppArgb),
                px = new int[w * workHeight],
                ir = new uint[stride * (workHeight + 1)],
                ig = new uint[stride * (workHeight + 1)],
                ib = new uint[stride * (workHeight + 1)]
            };
            f.Bounds = bounds;
            f.Scale = scale;
            using (var g = Graphics.FromImage(f.small))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.DrawImage(shot, 0, 0, f.W, f.H);
            }
            var px = Scanner.Pixels(f.small, f.px);
            int teal = 0;
            for (int y = 0; y < f.H; y++)
            {
                uint sr = 0, sg = 0, sb = 0;
                for (int x = 0; x < f.W; x++)
                {
                    int p = px[y * f.W + x];
                    sr += (uint)(p >> 16) & 255;
                    sg += (uint)(p >> 8) & 255;
                    sb += (uint)p & 255;
                    if (Scanner.IsButtonColor(p)) teal++;
                    int i = (y + 1) * stride + x + 1;
                    f.ir[i] = f.ir[i - stride] + sr;
                    f.ig[i] = f.ig[i - stride] + sg;
                    f.ib[i] = f.ib[i - stride] + sb;
                }
            }
            f.Teal = teal;
            return f;
        }

        public Point ToScreen(double x, double y) =>
            new Point(Bounds.X + (int)(x * Scale), Bounds.Y + (int)(y * Scale));

        public PointF ToWork(Point p) =>
            new PointF((float)((p.X - Bounds.X) / Scale), (float)((p.Y - Bounds.Y) / Scale));

        public float Score(Look look, int x, int y, int s)
        {
            if (x < 0 || y < 0 || x + s > W || y + s > H) return 9;
            int stride = W + 1;
            Span<float> rgb = stackalloc float[Look.N * Look.N * 3];
            Span<float> z = stackalloc float[Look.N * Look.N * 3];
            for (int cy = 0; cy < Look.N; cy++)
            {
                int y0 = y + cy * s / Look.N, y1 = y + (cy + 1) * s / Look.N;
                for (int cx = 0; cx < Look.N; cx++)
                {
                    int c = cy * Look.N + cx;
                    if (look.Weight[c] == 0) continue;
                    int x0 = x + cx * s / Look.N, x1 = x + (cx + 1) * s / Look.N;
                    int a = y0 * stride + x0, b = y0 * stride + x1, d = y1 * stride + x0, e = y1 * stride + x1;
                    float area = Math.Max(1, (x1 - x0) * (y1 - y0));
                    rgb[c * 3] = (ir[e] - ir[b] - ir[d] + ir[a]) / area;
                    rgb[c * 3 + 1] = (ig[e] - ig[b] - ig[d] + ig[a]) / area;
                    rgb[c * 3 + 2] = (ib[e] - ib[b] - ib[d] + ib[a]) / area;
                }
            }
            if (Look.Normalize(rgb, look.Weight, look.WeightSum, z) < Look.MinContrast) return 9;
            float total = 0;
            for (int c = 0; c < Look.N * Look.N; c++)
            {
                float w = look.Weight[c];
                if (w == 0) continue;
                total += w * (Math.Abs(z[c * 3] - look.Z[c * 3]) + Math.Abs(z[c * 3 + 1] - look.Z[c * 3 + 1]) + Math.Abs(z[c * 3 + 2] - look.Z[c * 3 + 2]));
            }
            return total / (look.WeightSum * 3);
        }
    }

    public class Hit
    {
        public float Score;
        public int X, Y, S;
        public Frame Frame;
        public Point Center => Frame.ToScreen(X + S / 2.0, Y + S / 2.0);
        public int ScreenSize => (int)(S * Frame.Scale);
        public double SizeFraction => (double)S / Frame.H;
    }

    public class Layout
    {
        public Rectangle Monitor;
        public Point[] Tabs;
        public double Pitch;
        public int Glyph;
        public Point LockButton;
        public Point TabFor(string role) => Tabs[Array.IndexOf(Scanner.RoleOrder, role)];

        public string ToText()
        {
            var parts = new List<int> { Monitor.X, Monitor.Y, Monitor.Width, Monitor.Height, Glyph, LockButton.X, LockButton.Y };
            foreach (var t in Tabs) { parts.Add(t.X); parts.Add(t.Y); }
            return string.Join(",", parts) + "," + Pitch.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        public static Layout Parse(string text)
        {
            var n = text.Split(',');
            if (n.Length != 16) return null;
            int I(int i) => int.Parse(n[i], System.Globalization.CultureInfo.InvariantCulture);
            return new Layout
            {
                Monitor = new Rectangle(I(0), I(1), I(2), I(3)),
                Glyph = I(4),
                LockButton = new Point(I(5), I(6)),
                Tabs = Enumerable.Range(0, 4).Select(i => new Point(I(7 + i * 2), I(8 + i * 2))).ToArray(),
                Pitch = double.Parse(n[15], System.Globalization.CultureInfo.InvariantCulture)
            };
        }
    }

    public class Looks
    {
        public Dictionary<string, Look> Agents;
        public Look[] Roles;
    }

    public static class Scanner
    {
        public static readonly string[] RoleOrder = { "Initiator", "Duelist", "Sentinel", "Controller" };
        public const float AgentMax = 0.62f, RoleMax = 0.55f;
        const int DeathmatchHeight = 360, SelectHeight = 720;
        const double MinTile = 0.035, MaxTile = 0.095;
        const double TabPitch = 79, LockDy = 509.5, TileSize = 87, TilePitch = 99, GridDx = -68.5, GridDy = 98.5;
        static Layout cached;
        public static Layout Cached { get => cached; set => cached = value; }
        public static int[] Pixels(Bitmap b, int[] into = null)
        {
            var data = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var px = into != null && into.Length == b.Width * b.Height ? into : new int[b.Width * b.Height];
            for (int y = 0; y < b.Height; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, px, y * b.Width, b.Width);
            b.UnlockBits(data);
            return px;
        }

        static Look Read(string path, bool solid)
        {
            using var ms = new MemoryStream(File.ReadAllBytes(path));
            using var bmp = new Bitmap(ms);
            return Look.FromIcon(bmp, solid);
        }

        public static Looks LoadLooks(List<Agent> agents) => new Looks
        {
            Agents = agents.AsParallel().ToDictionary(a => a.Id, a => Read(a.Icon, false)),
            Roles = RoleOrder.Select(r => Read(agents.First(a => a.Role == r).RoleIcon, true)).ToArray()
        };

        static IntPtr gameWindow;
        static DateTime gameCheckedAt;

        public static Rectangle? GameMonitor()
        {
            if ((DateTime.Now - gameCheckedAt).TotalSeconds > 2 || gameWindow == IntPtr.Zero)
            {
                gameCheckedAt = DateTime.Now;
                gameWindow = IntPtr.Zero;
                foreach (var p in System.Diagnostics.Process.GetProcessesByName("VALORANT-Win64-Shipping"))
                    using (p)
                        if (gameWindow == IntPtr.Zero) gameWindow = p.MainWindowHandle;
            }
            return gameWindow == IntPtr.Zero ? (Rectangle?)null : Screen.FromHandle(gameWindow).Bounds;
        }

        public static Rectangle Strip(Rectangle monitor) =>
            new Rectangle(monitor.X, monitor.Y, Math.Min(monitor.Width, (int)(monitor.Height * 0.6)), monitor.Height);

        public static Bitmap Capture(Rectangle r, Rectangle hide)
        {
            var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
            try
            {
                using var g = Graphics.FromImage(bmp);
                g.CopyFromScreen(r.X, r.Y, 0, 0, r.Size);
                var cover = Rectangle.Intersect(hide, r);
                if (!cover.IsEmpty)
                    g.FillRectangle(Brushes.Black, cover.X - r.X, cover.Y - r.Y, cover.Width, cover.Height);
                return bmp;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                bmp.Dispose();
                return null;
            }
        }

        public static Hit Search(Frame f, Look look, int sMin, int sMax, Rectangle region)
        {
            region.Intersect(new Rectangle(0, 0, f.W, f.H));
            var best = new Hit { Score = float.MaxValue, Frame = f };
            int step = sMax - sMin > 4 ? 2 : 1;
            for (int s = Math.Max(4, sMin); s <= sMax; s += step)
                for (int y = region.Top; y + s <= region.Bottom; y += 2)
                    for (int x = region.Left; x + s <= region.Right; x += 2)
                    {
                        float score = f.Score(look, x, y, s);
                        if (score < best.Score) { best.Score = score; best.X = x; best.Y = y; best.S = s; }
                    }
            if (best.Score == float.MaxValue) return best;
            var coarse = best;
            best = new Hit { Score = coarse.Score, X = coarse.X, Y = coarse.Y, S = coarse.S, Frame = f };
            for (int s = Math.Max(4, coarse.S - 2); s <= coarse.S + 2; s++)
                for (int y = coarse.Y - 2; y <= coarse.Y + 2; y++)
                    for (int x = coarse.X - 2; x <= coarse.X + 2; x++)
                    {
                        float score = f.Score(look, x, y, s);
                        if (score < best.Score) { best.Score = score; best.X = x; best.Y = y; best.S = s; }
                    }
            return best;
        }

        static bool Distinct(Frame f, Hit hit, Look me, IEnumerable<Look> all, float margin = 1.25f) =>
            all.All(other => other == me || f.Score(other, hit.X, hit.Y, hit.S) >= hit.Score * margin);

        public static Layout FindLayout(Frame f, Rectangle monitor, IEnumerable<Look> all)
        {
            if (Roles == null) return null;
            var region = new Rectangle(0, (int)(f.H * 0.08), Math.Min(f.W, (int)(f.H * 0.32)), (int)(f.H * 0.34));
            var first = Search(f, Roles[0], (int)(f.H * 0.022), (int)(f.H * 0.034), region);
            if (first.Score > RoleMax) return null;
            var hits = new List<Hit> { first };
            for (int i = 1; i < 4; i++)
            {
                var prev = hits[i - 1];
                var band = new Rectangle(prev.X + prev.S, prev.Y - prev.S / 2, prev.S * 4, prev.S * 2);
                var next = Search(f, Roles[i], prev.S - 2, prev.S + 2, band);
                if (next.Score > RoleMax) return null;
                hits.Add(next);
            }
            var gaps = Enumerable.Range(1, 3).Select(i => (double)(hits[i].X - hits[i - 1].X)).ToArray();
            if (gaps.Min() <= 0 || gaps.Max() > gaps.Min() * 1.3) return null;
            var tabs = hits.Select(h => h.Center).ToArray();
            double pitch = (tabs[3].X - tabs[0].X) / 3.0;
            var layout = new Layout
            {
                Monitor = monitor,
                Tabs = tabs,
                Pitch = pitch,
                Glyph = first.ScreenSize,
                LockButton = new Point(monitor.X + monitor.Width / 2, (int)(tabs[0].Y + LockDy * pitch / TabPitch))
            };
            return AgentsInFirstRow(f, layout, all) >= 3 ? cached = layout : null;
        }

        static int AgentsInFirstRow(Frame f, Layout layout, IEnumerable<Look> all)
        {
            var tab = f.ToWork(layout.Tabs[0]);
            double unit = layout.Pitch / TabPitch / f.Scale;
            int tile = (int)(TileSize * unit), j = Math.Max(3, tile / 10), count = 0;
            for (int c = 0; c < 4; c++)
            {
                double cx = tab.X + (GridDx + TilePitch * c) * unit, cy = tab.Y + GridDy * unit;
                var cell = new Rectangle((int)(cx - tile / 2.0 - j), (int)(cy - tile / 2.0 - j), tile + 2 * j, tile + 2 * j);
                if (all.Any(look => Search(f, look, (int)(tile * 0.95), (int)(tile * 1.05), cell).Score <= AgentMax)) count++;
            }
            return count;
        }

        public static Layout CheckCached(Frame f, Rectangle monitor, IEnumerable<Look> all)
        {
            if (cached == null || cached.Monitor != monitor || Roles == null) return null;
            int s = (int)Math.Round(cached.Glyph / f.Scale);
            for (int i = 0; i < 4; i++)
            {
                var c = f.ToWork(cached.Tabs[i]);
                var near = new Rectangle((int)(c.X - s), (int)(c.Y - s), s * 2, s * 2);
                if (Search(f, Roles[i], s - 1, s + 1, near).Score > RoleMax) return null;
            }
            return AgentsInFirstRow(f, cached, all) >= 3 ? cached : null;
        }

        static Hit BestInGrid(Frame f, Layout layout, Look me)
        {
            var tab = f.ToWork(layout.Tabs[0]);
            double unit = layout.Pitch / TabPitch / f.Scale;
            int tile = (int)(TileSize * unit), j = Math.Max(3, tile / 10);
            Hit best = null;
            for (int r = 0; r < 4; r++)
                for (int c = 0; c < 4; c++)
                {
                    double cx = tab.X + (GridDx + TilePitch * c) * unit, cy = tab.Y + (GridDy + TilePitch * r) * unit;
                    var cell = new Rectangle((int)(cx - tile / 2.0 - j), (int)(cy - tile / 2.0 - j), tile + 2 * j, tile + 2 * j);
                    var hit = Search(f, me, (int)(tile * 0.95), (int)(tile * 1.05), cell);
                    if (best == null || hit.Score < best.Score) best = hit;
                }
            return best;
        }

        public static Hit FindInGrid(Frame f, Layout layout, Look me, IEnumerable<Look> all)
        {
            var hit = BestInGrid(f, layout, me);
            if (hit.Score > AgentMax || !Distinct(f, hit, me, all, 1.45f)) return null;
            var tab = f.ToWork(layout.Tabs[0]);
            double unit = layout.Pitch / TabPitch / f.Scale, pitch = TilePitch * unit;
            double gx = hit.X + hit.S / 2.0 - (tab.X + GridDx * unit), gy = hit.Y + hit.S / 2.0 - (tab.Y + GridDy * unit);
            double off = Math.Max(Math.Abs(gx - Math.Round(gx / pitch) * pitch), Math.Abs(gy - Math.Round(gy / pitch) * pitch));
            return off <= TileSize * unit * 0.06 ? hit : null;
        }

        static Rectangle PadlockArea(Point center, Layout layout)
        {
            double unit = layout.Pitch / TabPitch;
            return Rectangle.FromLTRB((int)(center.X - 38 * unit), (int)(center.Y + 18 * unit), (int)(center.X - 14 * unit), (int)(center.Y + 38 * unit));
        }

        static bool HasPadlock(Bitmap shot, Rectangle area)
        {
            area.Intersect(new Rectangle(0, 0, shot.Width, shot.Height));
            if (area.Width < 4 || area.Height < 4) return false;
            var px = Pixels(shot);
            int dark = 0, bright = 0, n = 0;
            for (int y = area.Top; y < area.Bottom; y++)
                for (int x = area.Left; x < area.Right; x++)
                {
                    int p = px[y * shot.Width + x], r = (p >> 16) & 255, g = (p >> 8) & 255, b = p & 255;
                    if (Math.Max(r, Math.Max(g, b)) < 55) dark++;
                    if (Math.Min(r, Math.Min(g, b)) > 185) bright++;
                    n++;
                }
            return bright * 100 >= n * 12 && bright * 100 <= n * 34 && dark * 100 >= n * 22;
        }

        public static bool IsLocked(Hit hit, Layout layout)
        {
            var area = PadlockArea(hit.Center, layout);
            using var shot = Capture(area, Rectangle.Empty);
            return shot != null && HasPadlock(shot, new Rectangle(0, 0, area.Width, area.Height));
        }

        public static Hit FindDeathmatch(Frame f, Look me, IEnumerable<Look> all, double? sizeHint)
        {
            int sMin = (int)(MinTile * f.H), sMax = (int)(MaxTile * f.H);
            if (sizeHint is double hint)
            {
                int s = (int)Math.Round(hint * f.H);
                sMin = s - 1;
                sMax = s + 1;
            }
            var hit = Search(f, me, sMin, sMax, new Rectangle(0, 0, f.W, f.H));
            if (hit.Score > AgentMax || !Distinct(f, hit, me, all)) return null;
            return HasNeighbor(f, hit, me, all, (1, 0), (-1, 0)) && HasNeighbor(f, hit, me, all, (0, 1), (0, -1)) ? hit : null;
        }

        static bool HasNeighbor(Frame f, Hit hit, Look me, IEnumerable<Look> all, params (int, int)[] directions)
        {
            int s = hit.S, r = Math.Max(2, s / 4);
            foreach (var (dx, dy) in directions)
            {
                int cx = hit.X + (int)(dx * s * 1.27), cy = hit.Y + (int)(dy * s * 1.27);
                for (int y = cy - r; y <= cy + r; y++)
                    for (int x = cx - r; x <= cx + r; x++)
                        foreach (var other in all)
                            if (other != me && f.Score(other, x, y, s) < AgentMax * 0.85f) return true;
            }
            return false;
        }

        public static Look[] Roles;

        public class Detection
        {
            public Layout Layout;
            public Hit Deathmatch;
            public Frame Frame;
        }

        public static (bool FullLayout, bool Deathmatch) Schedule(int tick, int seen) =>
            (cached == null || tick % 40 == 0, tick % 10 == 5 || seen > 0);

        const int MinTeal = 600;
        static Bitmap bandShot, stripShot, monitorShot;
        static Frame bandFrame, stripFrame, monitorFrame;

        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("gdi32.dll")] static extern int SetStretchBltMode(IntPtr dc, int mode);
        [DllImport("gdi32.dll")] static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr old);
        [DllImport("gdi32.dll")] static extern bool StretchBlt(IntPtr dest, int dx, int dy, int dw, int dh, IntPtr src, int sx, int sy, int sw, int sh, int op);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);

        static Size Shrunk(Rectangle r, int height) => new Size((int)Math.Round(r.Width * (double)height / r.Height), height);

        static void Shrink(Bitmap into, IntPtr source, Rectangle from)
        {
            using var g = Graphics.FromImage(into);
            var dc = g.GetHdc();
            try
            {
                SetStretchBltMode(dc, 4);
                SetBrushOrgEx(dc, 0, 0, IntPtr.Zero);
                StretchBlt(dc, 0, 0, into.Width, into.Height, source, from.X, from.Y, from.Width, from.Height, 0x00CC0020);
            }
            finally
            {
                g.ReleaseHdc(dc);
            }
        }

        static Bitmap CaptureShrunk(ref Bitmap buffer, Rectangle r, int height, Rectangle hide)
        {
            var size = Shrunk(r, height);
            if (buffer == null || buffer.Size != size)
            {
                buffer?.Dispose();
                buffer = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
            }
            var screen = GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero) return null;
            try
            {
                Shrink(buffer, screen, r);
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screen);
            }
            var cover = Rectangle.Intersect(hide, r);
            if (!cover.IsEmpty)
            {
                double k = (double)height / r.Height;
                using var g = Graphics.FromImage(buffer);
                g.FillRectangle(Brushes.Black, (int)((cover.X - r.X) * k), (int)((cover.Y - r.Y) * k), (int)Math.Ceiling(cover.Width * k), (int)Math.Ceiling(cover.Height * k));
            }
            return buffer;
        }

        static Bitmap ShrinkBitmap(Bitmap source, int height)
        {
            var result = new Bitmap(Shrunk(new Rectangle(Point.Empty, source.Size), height).Width, height, PixelFormat.Format32bppArgb);
            var handle = source.GetHbitmap();
            var dc = CreateCompatibleDC(IntPtr.Zero);
            var old = SelectObject(dc, handle);
            try
            {
                Shrink(result, dc, new Rectangle(Point.Empty, source.Size));
            }
            finally
            {
                SelectObject(dc, old);
                DeleteDC(dc);
                DeleteObject(handle);
            }
            return result;
        }

        public static void ReleaseBuffers()
        {
            bandShot?.Dispose();
            stripShot?.Dispose();
            monitorShot?.Dispose();
            bandShot = stripShot = monitorShot = null;
            bandFrame = stripFrame = monitorFrame = null;
        }

        static Bitmap CaptureInto(ref Bitmap buffer, Rectangle r, Rectangle hide)
        {
            if (buffer == null || buffer.Width != r.Width || buffer.Height != r.Height)
            {
                buffer?.Dispose();
                buffer = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
            }
            try
            {
                using var g = Graphics.FromImage(buffer);
                g.CopyFromScreen(r.X, r.Y, 0, 0, r.Size);
                var cover = Rectangle.Intersect(hide, r);
                if (!cover.IsEmpty)
                    g.FillRectangle(Brushes.Black, cover.X - r.X, cover.Y - r.Y, cover.Width, cover.Height);
                return buffer;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return null;
            }
        }

        static Rectangle Band(Layout layout)
        {
            double unit = layout.Pitch / TabPitch;
            var t = layout.Tabs;
            int top = Math.Min(Math.Min(t[0].Y, t[1].Y), Math.Min(t[2].Y, t[3].Y));
            var band = Rectangle.FromLTRB((int)(t[0].X - 130 * unit), (int)(top - 45 * unit), (int)(t[0].X + 300 * unit), (int)(t[0].Y + 160 * unit));
            band.Intersect(layout.Monitor);
            return band;
        }

        static Frame SelectFrame(Rectangle monitor, Rectangle hide)
        {
            var strip = Strip(monitor);
            var shot = CaptureInto(ref stripShot, strip, hide);
            if (shot == null) return null;
            return stripFrame = Frame.From(shot, strip, SelectHeight, stripFrame);
        }

        public static Detection Detect(Rectangle monitor, Look me, IEnumerable<Look> all, double? sizeHint, bool fullLayout, bool deathmatch, Rectangle hide)
        {
            var thread = System.Threading.Thread.CurrentThread;
            var priority = thread.Priority;
            thread.Priority = System.Threading.ThreadPriority.BelowNormal;
            try
            {
                if (!fullLayout && cached != null && cached.Monitor == monitor)
                {
                    var band = Band(cached);
                    var shot = band.Width > 8 && band.Height > 8 ? CaptureInto(ref bandShot, band, hide) : null;
                    if (shot != null)
                    {
                        bandFrame = Frame.From(shot, band, Math.Max(16, (int)Math.Round(band.Height * (double)SelectHeight / monitor.Height)), bandFrame);
                        if (CheckCached(bandFrame, monitor, all) != null)
                        {
                            var hi = SelectFrame(monitor, hide);
                            if (hi != null) return new Detection { Layout = cached, Frame = hi };
                        }
                    }
                }
                else
                {
                    var hi = SelectFrame(monitor, hide);
                    if (hi == null) return null;
                    var layout = CheckCached(hi, monitor, all) ?? FindLayout(hi, monitor, all);
                    if (layout != null) return new Detection { Layout = layout, Frame = hi };
                }
                if (!deathmatch) return null;
                var small = CaptureShrunk(ref monitorShot, monitor, DeathmatchHeight, hide);
                if (small == null) return null;
                monitorFrame = Frame.From(small, monitor, DeathmatchHeight, monitorFrame);
                if (monitorFrame.Teal < MinTeal) return null;
                var hit = FindDeathmatch(monitorFrame, me, all, sizeHint);
                if (hit == null) return null;
                using var full = Capture(monitor, hide);
                if (full == null) return null;
                var top = hit.Center.Y - monitor.Y;
                var button = FindButton(full, monitor, new Rectangle(0, top, monitor.Width, monitor.Height - top), hit.ScreenSize);
                return button == null ? null : new Detection { Deathmatch = hit };
            }
            finally
            {
                thread.Priority = priority;
            }
        }

        public static Hit FindInGridOnScreen(Layout layout, Look me, IEnumerable<Look> all, Rectangle hide)
        {
            var strip = Strip(layout.Monitor);
            using var shot = Capture(strip, hide);
            return shot == null ? null : FindInGrid(Frame.From(shot, strip, SelectHeight), layout, me, all);
        }

        public static bool IsButtonColor(int p)
        {
            int r = (p >> 16) & 255, g = (p >> 8) & 255, b = p & 255;
            return g > r + 30 && b > r + 20 && Math.Abs(g - b) < 45 && g > 115;
        }

        public static Point? FindButton(Bitmap shot, Rectangle bounds, Rectangle area, int tileSize)
        {
            area.Intersect(new Rectangle(0, 0, shot.Width, shot.Height));
            var px = Pixels(shot);
            int minCount = (int)(tileSize * 2.5), maxGap = (int)(tileSize * 0.6);
            int start = -1, last = -1, bestStart = -1, bestEnd = -1;
            for (int y = area.Top; y < area.Bottom; y++)
            {
                int count = 0;
                for (int x = area.Left; x < area.Right; x++)
                    if (IsButtonColor(px[y * shot.Width + x])) count++;
                if (count < minCount) continue;
                if (start < 0 || y - last > maxGap) start = y;
                last = y;
                if (last - start > bestEnd - bestStart) { bestStart = start; bestEnd = last; }
            }
            if (bestStart < 0 || bestEnd - bestStart < tileSize / 5) return null;
            long sumX = 0, n = 0;
            for (int y = bestStart; y <= bestEnd; y++)
                for (int x = area.Left; x < area.Right; x++)
                    if (IsButtonColor(px[y * shot.Width + x])) { sumX += x; n++; }
            return new Point(bounds.X + (int)(sumX / n), bounds.Y + (bestStart + bestEnd) / 2);
        }

        public static Point? FindButtonOnScreen(Hit hit)
        {
            var b = hit.Frame.Bounds;
            using var shot = Capture(b, Rectangle.Empty);
            if (shot == null) return null;
            var top = hit.Center.Y - b.Y;
            return FindButton(shot, b, new Rectangle(0, top, b.Width, b.Height - top), hit.ScreenSize);
        }

        public static string TestImage(string path, Agent agent, Looks looks, int screenHeight)
        {
            Roles = looks.Roles;
            var me = looks.Agents[agent.Id];
            using var src = new Bitmap(path);
            bool full = src.Height >= 1000;
            double k = screenHeight / (full ? (double)src.Height : 1080.0);
            using var shot = new Bitmap(full ? (int)(src.Width * k) : screenHeight * 16 / 9, screenHeight, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(shot))
            {
                g.Clear(Color.FromArgb(18, 16, 14));
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                if (full) g.DrawImage(src, 0, 0, shot.Width, shot.Height);
                else g.DrawImage(src, (int)(500 * k), (int)(300 * k), (int)(src.Width * k), (int)(src.Height * k));
            }
            var bounds = new Rectangle(0, 0, shot.Width, shot.Height);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var strip = Strip(bounds);
            using var stripShot = shot.Clone(new Rectangle(0, 0, strip.Width, strip.Height), PixelFormat.Format32bppArgb);
            var hi = Frame.From(stripShot, strip, SelectHeight);
            var layout = FindLayout(hi, bounds, looks.Agents.Values);
            if (layout != null)
            {
                var grid = FindInGrid(hi, layout, me, looks.Agents.Values);
                var ms = sw.ElapsedMilliseconds;
                var quick = System.Diagnostics.Stopwatch.StartNew();
                var band = Band(layout);
                using var bandShot = shot.Clone(band, PixelFormat.Format32bppArgb);
                var again = CheckCached(Frame.From(bandShot, band, Math.Max(16, (int)Math.Round(band.Height * (double)SelectHeight / bounds.Height))), bounds, looks.Agents.Values);
                ms = again == null ? -1 : ms;
                var quickMs = quick.ElapsedMilliseconds;
                var raw = grid == null ? BestInGrid(hi, layout, me) : null;
                return $"SELECT tabs {string.Join(" ", layout.Tabs)} {agent.Role} tab {layout.TabFor(agent.Role)} lock {layout.LockButton}, first {ms}ms, remembered {quickMs}ms; " +
                       (grid != null ? $"agent at {grid.Center} {(HasPadlock(shot, PadlockArea(grid.Center, layout)) ? "NOT OWNED" : "owned")} score {grid.Score:F2} margin {looks.Agents.Values.Where(o => o != me).Min(o => hi.Score(o, grid.X, grid.Y, grid.S)) / grid.Score:F2}" : $"agent not found (best {raw.Score:F2} at {raw.Center})");
            }
            using var shrunk = ShrinkBitmap(shot, DeathmatchHeight);
            var lo = Frame.From(shrunk, bounds, DeathmatchHeight);
            var teal = $"teal {lo.Teal}; ";
            var hit = FindDeathmatch(lo, me, looks.Agents.Values, null);
            var t = sw.ElapsedMilliseconds;
            if (hit == null)
            {
                var raw = Search(lo, me, (int)(MinTile * lo.H), (int)(MaxTile * lo.H), new Rectangle(0, 0, lo.W, lo.H));
                return teal + $"nothing found (agent best {raw.Score:F2} at {raw.Center}, {t}ms)";
            }
            var button = FindButton(shot, bounds, new Rectangle(0, hit.Center.Y, shot.Width, shot.Height - hit.Center.Y), hit.ScreenSize);
            return teal + $"DEATHMATCH agent at {hit.Center} score {hit.Score:F2}, {t}ms; button at {button?.ToString() ?? "none"}";
        }
    }
}
