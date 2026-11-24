using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Instalock
{
    static class Mouse
    {
        const int Reach = 70, Timeout = 15000;
        public const string ClickedElsewhere = "YOU CLICKED SOMEWHERE ELSE", LostFocus = "WAITING FOR YOU TO TAB BACK IN";

        [DllImport("user32.dll")] static extern bool GetCursorPos(out Point p);
        [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

        public static Point Cursor()
        {
            GetCursorPos(out var p);
            return p;
        }

        static bool LeftDown() => (GetAsyncKeyState(0x01) & 0x8000) != 0;

        static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

        static IntPtr lastWindow;
        static bool lastFocused;

        public static bool GameFocused()
        {
            var window = GetForegroundWindow();
            if (window == lastWindow) return lastFocused;
            GetWindowThreadProcessId(window, out var pid);
            try
            {
                using var process = Process.GetProcessById((int)pid);
                lastFocused = process.ProcessName.StartsWith("VALORANT", StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException)
            {
                lastFocused = false;
            }
            lastWindow = window;
            return lastFocused;
        }

        public static async Task<string> PointAndWait(Point target, CancellationToken ct)
        {
            if (!GameFocused()) return LostFocus;
            while (LeftDown()) await Task.Delay(4, ct);
            SetCursorPos(target.X, target.Y);
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < Timeout)
            {
                await Task.Delay(4, ct);
                if (!GameFocused()) return LostFocus;
                var at = Cursor();
                if (LeftDown())
                {
                    while (LeftDown()) await Task.Delay(4, ct);
                    if (Distance(at, target) <= Reach) return null;
                    await Task.Delay(250, ct);
                    return GameFocused() ? ClickedElsewhere : LostFocus;
                }
            }
            return "NO CLICK FOR 15 SECONDS";
        }
    }
}
