using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace Instalock
{
    public partial class App : Application
    {
        void OnStartup(object sender, StartupEventArgs e)
        {
            if (e.Args.Contains("--selftest"))
            {
                try { Agents.SelfTest(); Shutdown(0); }
                catch (Exception ex) { Console.Error.WriteLine(ex); Shutdown(1); }
                return;
            }
            if (e.Args.Length == 3 && e.Args[0] == "--findtest")
            {
                var agents = Task.Run(Agents.Fetch).Result;
                var looks = Task.Run(() => Scanner.LoadLooks(agents)).Result;
                foreach (var name in e.Args[2].Split(','))
                {
                    var agent = agents.First(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    foreach (var height in new[] { 720, 1080, 1440, 2160 })
                        Console.WriteLine($"{agent.Name} {height}p: " + Scanner.TestImage(e.Args[1], agent, looks, height));
                }
                Shutdown(0);
                return;
            }
            if (e.Args.Contains("--bench"))
            {
                var agents = Task.Run(Agents.Fetch).Result;
                var looks = Task.Run(() => Scanner.LoadLooks(agents)).Result;
                Scanner.Roles = looks.Roles;
                var monitor = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
                double k = monitor.Height / 1080.0;
                System.Drawing.Point P(double x, double y) => new System.Drawing.Point(monitor.X + (int)(x * k), monitor.Y + (int)(y * k));
                Scanner.Cached = new Layout { Monitor = monitor, Glyph = (int)(30 * k), Pitch = 79.5 * k, LockButton = P(960, 769), Tabs = new[] { P(153, 254), P(233, 254), P(312, 255), P(393, 255) } };
                var me = looks.Agents.Values.First();
                for (int tick = -10; tick < 0; tick++) Scanner.Detect(monitor, me, looks.Agents.Values, 0.06, false, false, System.Drawing.Rectangle.Empty);
                GC.Collect();
                var process = System.Diagnostics.Process.GetCurrentProcess();
                var cpu = process.TotalProcessorTime;
                long allocated = GC.GetTotalAllocatedBytes();
                var clock = System.Diagnostics.Stopwatch.StartNew();
                const int ticks = 200;
                for (int tick = 0; tick < ticks; tick++)
                {
                    var (full, dm) = Scanner.Schedule(tick, 0);
                    Scanner.Detect(monitor, me, looks.Agents.Values, 0.06, full, dm, System.Drawing.Rectangle.Empty);
                }
                process.Refresh();
                Console.WriteLine($"{monitor.Width}x{monitor.Height}: {clock.Elapsed.TotalMilliseconds / ticks:F1}ms/scan wall, {(process.TotalProcessorTime - cpu).TotalMilliseconds / ticks:F1}ms/scan cpu, {(GC.GetTotalAllocatedBytes() - allocated) / ticks / 1048576.0:F1}MB allocated/scan, peak {process.PeakWorkingSet64 / 1048576}MB");
                Shutdown(0);
                return;
            }
            if (e.Args.Contains("--apitest"))
            {
                var session = Task.Run(RiotApi.Connect).Result;
                Console.WriteLine(session == null ? "not connected" : $"connected {session.Glz} version {session.Version} in agent select (local check) {Task.Run(() => RiotApi.InAgentSelect(session)).Result?.ToString() ?? "unknown"} pregame {Task.Run(() => RiotApi.PregameMatch(session)).Result ?? "none"}");
                Shutdown(0);
                return;
            }
            if (e.Args.Length >= 2 && e.Args[0] == "--shot")
            {
                var window = new MainWindow { WindowStartupLocation = WindowStartupLocation.Manual, Left = -4000, Top = 0, ShowActivated = false };
                window.Show();
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                timer.Tick += (s, a) =>
                {
                    timer.Stop();
                    if (e.Args.Length >= 3) window.PreviewStatus(e.Args[2], e.Args.Length < 4 || e.Args[3] != "ok");
                    window.UpdateLayout();
                    var root = (FrameworkElement)window.Content;
                    var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    var bg = new System.Windows.Media.DrawingVisual();
                    using (var dc = bg.RenderOpen()) dc.DrawRectangle(window.Background, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
                    bmp.Render(bg);
                    bmp.Render(root);
                    var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                    using (var file = System.IO.File.Create(e.Args[1])) png.Save(file);
                    Shutdown(0);
                };
                timer.Start();
                return;
            }
            new MainWindow().Show();
        }
    }
}
