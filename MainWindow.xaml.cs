using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Instalock
{
    public partial class MainWindow : Window
    {
        static readonly Brush Red = (Brush)Application.Current.Resources["Red"];
        static readonly Brush Dim = (Brush)Application.Current.Resources["Dim"];
        static readonly Brush Cream = (Brush)Application.Current.Resources["Cream"];
        static readonly Brush Good = new SolidColorBrush(Color.FromRgb(0x3E, 0xE0, 0xA0));
        static readonly Brush Stop = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
        static readonly string SettingsPath = System.IO.Path.Combine(Agents.Folder, "settings.txt");
        static readonly string FavoritesPath = System.IO.Path.Combine(Agents.Folder, "favorites.txt");
        const double TileW = 94, TileH = 114;

        List<Agent> agents = new List<Agent>();
        Looks looks;
        Agent picked;
        double? tileFraction;
        CancellationTokenSource running;
        Task<Looks> looksLoading;
        bool settingsLoaded, focusMissed, lockedIn;

        [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);
        UniformGrid tilePanel;

        [DllImport("user32.dll")]
        static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        struct RECT { public int Left, Top, Right, Bottom; }

        System.Drawing.Rectangle OwnWindow()
        {
            GetWindowRect(new WindowInteropHelper(this).Handle, out var r);
            return System.Drawing.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        }

        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint key);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd, int id);
        const int StopHotkey = 1;

        [DllImport("psapi.dll")] static extern bool EmptyWorkingSet(IntPtr process);

        static void Trim()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            using var self = System.Diagnostics.Process.GetCurrentProcess();
            EmptyWorkingSet(self.Handle);
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public MainWindow()
        {
            InitializeComponent();
            foreach (var chip in RoleChips.Children.OfType<RadioButton>())
            {
                var role = (string)chip.Tag;
                var glyph = new Rectangle
                {
                    Width = 14,
                    Height = 14,
                    Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    OpacityMask = new ImageBrush(new BitmapImage(new Uri($"pack://application:,,,/icons/{role.ToLowerInvariant()}.png"))) { Stretch = Stretch.Uniform }
                };
                glyph.SetBinding(Shape.FillProperty, new Binding("Foreground") { Source = chip });
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(glyph);
                row.Children.Add(new TextBlock { Text = role, VerticalAlignment = VerticalAlignment.Center });
                chip.Content = row;
            }
        }

        void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        void Close_Click(object sender, RoutedEventArgs e) => Close();

        void Window_StateChanged(object sender, EventArgs e)
        {
            if (WindowState == WindowState.Minimized) Trim();
        }

        const double DesignWidth = 920, DesignHeight = 720, DesignScreenHeight = 1080;

        void FitToScreen(System.Windows.Forms.Screen screen, bool center)
        {
            var dpi = VisualTreeHelper.GetDpi(this).DpiScaleY;
            double k = screen.Bounds.Height / DesignScreenHeight / dpi;
            double cx = Left + Width / 2, cy = Top + Height / 2;
            Root.LayoutTransform = new ScaleTransform(k, k);
            Width = DesignWidth * k;
            Height = DesignHeight * k;
            if (center)
            {
                var area = screen.WorkingArea;
                cx = (area.Left + area.Width / 2.0) / dpi;
                cy = (area.Top + area.Height / 2.0) / dpi;
            }
            Left = cx - Width / 2;
            Top = cy - Height / 2;
        }

        void Refit() => FitToScreen(System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle), false);

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            Dispatcher.BeginInvoke(new Action(Refit));
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            int dark = 1;
            var handle = new WindowInteropHelper(this).Handle;
            DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
            int round = 2;
            DwmSetWindowAttribute(handle, 33, ref round, sizeof(int));
            RegisterHotKey(handle, StopHotkey, 0x4000, 0x77);
            HwndSource.FromHwnd(handle).AddHook(OnMessage);
            if (WindowStartupLocation != WindowStartupLocation.Manual)
                FitToScreen(System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position), true);
        }

        IntPtr OnMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == 0x0312 && wParam.ToInt32() == StopHotkey && running != null)
            {
                StopRunning("STOPPED WITH F8", Dim);
                handled = true;
            }
            if (msg == 0x0232) Dispatcher.BeginInvoke(new Action(Refit));
            return IntPtr.Zero;
        }

        async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                agents = await Agents.Fetch();
                var favorites = new HashSet<string>(Load(FavoritesPath).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
                foreach (var a in agents)
                {
                    a.Favorite = favorites.Contains(a.Id);
                    a.PropertyChanged += (s, args) =>
                    {
                        if (args.PropertyName == nameof(Agent.Favorite))
                            Store(FavoritesPath, string.Join(Environment.NewLine, agents.Where(x => x.Favorite).Select(x => x.Id)));
                    };
                }
                AgentList.ItemsSource = agents;
                looksLoading = Task.Run(() => Scanner.LoadLooks(agents));
                LoadingText.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                LoadingText.Text = "COULDN'T LOAD AGENTS: " + ex.Message.ToUpperInvariant();
                LoadingText.Foreground = Red;
            }
            LoadSettings();
            settingsLoaded = true;
            FitGrid();
            if (looksLoading == null) return;
            try
            {
                var loaded = await looksLoading;
                looks ??= loaded;
                Scanner.Roles = looks.Roles;
            }
            catch (Exception)
            {
                looksLoading = null;
            }
            if (running == null) Trim();
        }

        void LoadSettings()
        {
            var parts = Load(SettingsPath).Split('|');
            if (parts.Length > 1 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) tileFraction = f;
            if (parts.Length > 2 && parts[2].Length > 0)
                try { Scanner.Cached = Layout.Parse(parts[2]); } catch (Exception ex) when (ex is FormatException || ex is OverflowException) { }
            if (parts.Length > 3 && parts[3] == "api") ApiMode.IsChecked = true;
            var chip = RoleChips.Children.OfType<RadioButton>().FirstOrDefault(c => parts.Length > 4 && (string)c.Tag == parts[4]);
            if (chip != null) chip.IsChecked = true;
            var agent = agents.FirstOrDefault(a => a.Id == parts[0]);
            if (agent == null) return;
            picked = agent;
            AgentList.UpdateLayout();
            if (AgentList.ItemContainerGenerator.ContainerFromItem(agent) is ContentPresenter cp
                && VisualTreeHelper.GetChildrenCount(cp) > 0
                && VisualTreeHelper.GetChild(cp, 0) is RadioButton tile)
                tile.IsChecked = true;
            RefreshFooter();
        }

        void SaveSettings()
        {
            if (!settingsLoaded) return;
            Store(SettingsPath, $"{picked?.Id}|{tileFraction?.ToString(CultureInfo.InvariantCulture)}|{Scanner.Cached?.ToText()}|{(ApiMode.IsChecked == true ? "api" : "cursor")}|{SelectedRole()}");
        }

        string SelectedRole() => (string)RoleChips.Children.OfType<RadioButton>().First(c => c.IsChecked == true).Tag;

        static string Load(string path)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path) : "";
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return "";
            }
        }

        static void Store(string path, string text)
        {
            try
            {
                Directory.CreateDirectory(Agents.Folder);
                File.WriteAllText(path, text);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }

        void Mode_Changed(object sender, RoutedEventArgs e)
        {
            SaveSettings();
        }

        void TilePanel_Loaded(object sender, RoutedEventArgs e)
        {
            tilePanel = (UniformGrid)sender;
            FitGrid();
        }

        void GridHost_SizeChanged(object sender, SizeChangedEventArgs e) => FitGrid();

        void FitGrid()
        {
            int n = agents.Count(a => a.Shown);
            if (tilePanel == null || n == 0 || GridHost.ActualWidth <= 0 || GridHost.ActualHeight <= 0) return;
            double best = 0;
            int bestCols = 1;
            for (int cols = 1; cols <= n; cols++)
            {
                int rows = (n + cols - 1) / cols;
                double scale = Math.Min(GridHost.ActualWidth / (cols * TileW), GridHost.ActualHeight / (rows * TileH));
                if (scale > best) { best = scale; bestCols = cols; }
            }
            tilePanel.Columns = bestCols;
            best = Math.Min(best, 1.12);
            AgentList.LayoutTransform = new ScaleTransform(best, best);
        }

        void Filter_Changed(object sender, RoutedEventArgs e)
        {
            if (AgentList == null || agents.Count == 0) return;
            var query = SearchBox.Text.Trim();
            var role = SelectedRole();
            foreach (var a in agents)
                a.Shown = (role == "All" || a.Role == role) && a.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
            FitGrid();
            if (sender is RadioButton) SaveSettings();
        }

        void Agent_Checked(object sender, RoutedEventArgs e)
        {
            picked = (Agent)((RadioButton)sender).DataContext;
            SaveSettings();
            RefreshFooter();
        }

        void RefreshFooter()
        {
            PickedText.Text = picked?.Name ?? "None";
            PickedIcon.Source = picked?.Image;
            PickedPlaceholder.Visibility = picked == null ? Visibility.Visible : Visibility.Collapsed;
            ToggleButton.IsEnabled = picked != null;
        }

        void Toggle_Click(object sender, RoutedEventArgs e)
        {
            if (running != null)
            {
                StopRunning("STOPPED", Dim);
                return;
            }
            running = new CancellationTokenSource();
            SetRunning(true);
            _ = ApiMode.IsChecked == true ? RunApi(picked, running.Token) : Run(picked, running.Token);
        }

        void StopRunning(string message, Brush color)
        {
            running?.Cancel();
            running = null;
            SetRunning(false);
            SetStatus(message, color);
        }

        void SetRunning(bool on)
        {
            AgentList.IsEnabled = !on;
            ModeChips.IsEnabled = !on;
            ToggleText.Text = on ? "STOP" : "LOCK IN";
            ToggleIcon.Tag = FindResource(on ? "IconStop" : "IconLock");
            ToggleIcon.Background = on ? ToggleIcon.Foreground : null;
            ToggleButton.Background = on ? Stop : Red;
        }

        public void PreviewStatus(string text, bool error) => SetStatus(text, error ? Red : Good);

        string shownStatus;

        void SetStatus(string text, Brush color)
        {
            shownStatus = text;
            StatusText.Text = text;
            StatusText.Foreground = color;
            StatusDot.Fill = color;
        }

        async Task Run(Agent agent, CancellationToken ct)
        {
            timeBeginPeriod(1);
            try
            {
                if (looks == null)
                {
                    SetStatus("LOADING AGENT ART", Cream);
                    try
                    {
                        looks = await (looksLoading ?? Task.Run(() => Scanner.LoadLooks(agents)));
                    }
                    finally
                    {
                        looksLoading = null;
                    }
                    Scanner.Roles = looks.Roles;
                }
                if (!looks.Agents.TryGetValue(agent.Id, out var me)) throw new InvalidOperationException("No art for " + agent.Name);
                var all = looks.Agents.Values;
                bool locked = false;
                int tick = 0, tries = 0, seen = 0;
                var lastSeen = DateTime.MinValue;
                var savedLayout = Scanner.Cached?.ToText();
                SetStatus("WATCHING FOR AGENT SELECT", Cream);
                while (!ct.IsCancellationRequested)
                {
                  try
                  {
                    var monitor = Scanner.GameMonitor();
                    if (monitor == null)
                    {
                        if (!locked) SetStatus("WAITING FOR VALORANT TO OPEN", Dim);
                        await Task.Delay(500, ct);
                        continue;
                    }
                    var (fullLayout, deathmatch) = Scanner.Schedule(tick, seen);
                    tick++;
                    var hint = tick % 50 == 5 ? null : tileFraction;
                    var hide = OwnWindow();
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    var found = await Task.Run(() => Scanner.Detect(monitor.Value, me, all, hint, fullLayout, deathmatch, hide), ct);
                    if (ct.IsCancellationRequested) return;
                    seen = found == null ? 0 : seen + 1;
                    if (found != null) lastSeen = DateTime.Now;
                    if (found != null && !locked && seen < 3)
                    {
                        await Task.Delay(30, ct);
                        continue;
                    }
                    if (found == null && !locked && tries == 0 && !deathmatch) SetStatus($"WATCHING  ·  {clock.ElapsedMilliseconds}MS SCAN", Cream);
                    if (found?.Layout != null && found.Layout.ToText() != savedLayout)
                    {
                        savedLayout = found.Layout.ToText();
                        SaveSettings();
                    }
                    if (found != null && !locked && ++tries > 6)
                    {
                        locked = true;
                        SetStatus("GAVE UP AFTER 6 TRIES - PICK BY HAND", Red);
                    }
                    else if (found != null && !locked)
                    {
                        if (!Mouse.GameFocused())
                        {
                            tries--;
                            SetStatus("AGENT SELECT FOUND - CLICK INTO VALORANT", Red);
                        }
                        else
                        {
                            focusMissed = lockedIn = false;
                            locked = found.Layout != null
                                ? await LockFromSelect(found.Layout, found.Frame, agent, me, all, hide, ct)
                                : await LockFromDeathmatch(found.Deathmatch, agent, ct);
                            if (focusMissed) tries--;
                            if (lockedIn && !agent.Favorite)
                            {
                                StopRunning(shownStatus, Good);
                                return;
                            }
                        }
                    }
                    else if (found == null && (locked || tries > 0) && (DateTime.Now - lastSeen).TotalSeconds > 4)
                    {
                        locked = false;
                        tries = 0;
                        SetStatus("WATCHING FOR AGENT SELECT", Cream);
                    }
                    await Task.Delay(found == null || !locked ? 40 : 400, ct);
                  }
                  catch (Exception ex) when (!(ex is OperationCanceledException))
                  {
                    SetStatus("SCAN ERROR - RETRYING", Red);
                    await Task.Delay(300, ct);
                  }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                StopRunning(ex.Message.ToUpperInvariant(), Red);
            }
            finally
            {
                timeEndPeriod(1);
                if (running == null || running.Token == ct)
                {
                    Scanner.ReleaseBuffers();
                    if (running == null) Trim();
                }
            }
        }

        async Task RunApi(Agent agent, CancellationToken ct)
        {
            var name = agent.Name.ToUpperInvariant();
            RiotSession session = null;
            var connectedAt = DateTime.MinValue;
            string handled = null, shown = null, tryingMatch = null;
            int attempts = 0;
            var serverCheckedAt = DateTime.MinValue;
            void Show(string text, Brush color)
            {
                if (text == shown) return;
                shown = text;
                SetStatus(text, color);
            }
            Show("API  ·  CONNECTING", Cream);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (session == null || (DateTime.Now - connectedAt).TotalMinutes > 10)
                    {
                        session = await RiotApi.Connect();
                        connectedAt = DateTime.Now;
                        if (session == null)
                        {
                            Show("API  ·  WAITING FOR VALORANT", Dim);
                            await Task.Delay(3000, ct);
                            continue;
                        }
                    }
                    var selecting = await RiotApi.InAgentSelect(session);
                    if (selecting == false && (DateTime.Now - serverCheckedAt).TotalSeconds < 5)
                    {
                        handled = null;
                        Show("API  ·  WATCHING FOR AGENT SELECT", Cream);
                        await Task.Delay(400, ct);
                        continue;
                    }
                    serverCheckedAt = DateTime.Now;
                    var match = await RiotApi.PregameMatch(session);
                    if (match == null)
                    {
                        handled = null;
                        Show("API  ·  WATCHING FOR AGENT SELECT", Cream);
                        await Task.Delay(selecting == null ? 2000 : 400, ct);
                        continue;
                    }
                    if (match == handled)
                    {
                        await Task.Delay(2000, ct);
                        continue;
                    }
                    if (match != tryingMatch)
                    {
                        tryingMatch = match;
                        attempts = 0;
                    }
                    var (open, alreadyLocked) = await RiotApi.PickPhase(session, match);
                    if (alreadyLocked)
                    {
                        handled = match;
                        Show(attempts > 0 ? $"LOCKED {name}" : "API  ·  ALREADY LOCKED", Good);
                        if (attempts > 0 && !agent.Favorite)
                        {
                            StopRunning($"LOCKED {name}", Good);
                            return;
                        }
                        continue;
                    }
                    if (!open)
                    {
                        Show("API  ·  MATCH FOUND - WAITING FOR PICKS", Cream);
                        await Task.Delay(250, ct);
                        continue;
                    }
                    if (attempts >= 6)
                    {
                        handled = match;
                        Show($"COULD NOT LOCK {name} - PICK BY HAND", Red);
                        continue;
                    }
                    if (attempts == 0)
                    {
                        Show($"API  ·  PICKS OPEN - LOCKING {name}", Cream);
                        await Task.Delay(700, ct);
                    }
                    attempts++;
                    var failed = await RiotApi.Lock(session, match, agent.Id, ct);
                    Show(failed == null ? $"LOCKING {name}" : $"API  ·  {failed} REJECTED - RETRYING", failed == null ? Cream : Red);
                    await Task.Delay(800, ct);
                }
                catch (OperationCanceledException) { return; }
                catch (RiotAuthExpired)
                {
                    session = null;
                }
                catch (RiotBusy)
                {
                    Show("API  ·  RIOT BUSY - SLOWING DOWN", Red);
                    try { await Task.Delay(5000, ct); } catch (OperationCanceledException) { return; }
                }
                catch (Exception)
                {
                    session = null;
                    Show("API  ·  CONNECTION ERROR - RETRYING", Red);
                    try { await Task.Delay(3000, ct); } catch (OperationCanceledException) { return; }
                }
            }
        }

        bool Missed(string miss)
        {
            if (miss == Mouse.LostFocus) focusMissed = true;
            bool tookOver = miss == Mouse.ClickedElsewhere;
            SetStatus(tookOver ? "YOU TOOK OVER - HANDS OFF" : miss == Mouse.LostFocus ? miss : miss + " - RETRYING", Red);
            return tookOver;
        }

        async Task<bool> PointUntilClicked(System.Drawing.Point target, CancellationToken ct)
        {
            for (int i = 0; i < 4; i++)
            {
                var miss = await Mouse.PointAndWait(target, ct);
                if (miss == null) return true;
                if (Missed(miss)) return false;
                await Task.Delay(150, ct);
            }
            return false;
        }

        async Task<bool> LockFromSelect(Layout layout, Frame frame, Agent agent, Look me, IEnumerable<Look> all, System.Drawing.Rectangle hide, CancellationToken ct)
        {
            var name = agent.Name.ToUpperInvariant();
            var hit = await Task.Run(() => Scanner.FindInGrid(frame, layout, me, all), ct);
            if (hit == null)
            {
                SetStatus($"CLICK - {agent.Role.ToUpperInvariant()} TAB", Good);
                var missed = await Mouse.PointAndWait(layout.TabFor(agent.Role), ct);
                if (missed != null) return Missed(missed);
                for (int i = 0; i < 30 && hit == null; i++)
                {
                    await Task.Delay(40, ct);
                    hit = await Task.Run(() => Scanner.FindInGridOnScreen(layout, me, all, hide), ct);
                }
                if (hit == null)
                {
                    SetStatus($"{name} NOT FOUND IN GRID - RETRYING", Red);
                    return false;
                }
            }
            if (Scanner.IsLocked(hit, layout))
            {
                SetStatus($"{name} NOT OWNED - PICK BY HAND", Red);
                return true;
            }
            SetStatus($"CLICK - {name}", Good);
            var miss = await Mouse.PointAndWait(hit.Center, ct);
            if (miss != null) return Missed(miss);
            SetStatus("CLICK - LOCK IN", Good);
            if (!await PointUntilClicked(layout.LockButton, ct)) return true;
            SetStatus($"LOCKED {name}", Good);
            lockedIn = true;
            return true;
        }

        async Task<bool> LockFromDeathmatch(Hit hit, Agent agent, CancellationToken ct)
        {
            if (tileFraction == null || Math.Abs(tileFraction.Value - hit.SizeFraction) > 0.004)
            {
                tileFraction = hit.SizeFraction;
                SaveSettings();
            }
            var name = agent.Name.ToUpperInvariant();
            SetStatus($"CLICK - {name}", Good);
            var miss = await Mouse.PointAndWait(hit.Center, ct);
            if (miss != null) return Missed(miss);
            System.Drawing.Point? button = null;
            for (int i = 0; i < 12 && button == null; i++)
            {
                await Task.Delay(70, ct);
                button = await Task.Run(() => Scanner.FindButtonOnScreen(hit), ct);
            }
            if (button == null)
            {
                SetStatus("PLAY BUTTON NOT FOUND - PICK BY HAND", Red);
                return true;
            }
            SetStatus("CLICK - PLAY", Good);
            if (!await PointUntilClicked(button.Value, ct)) return true;
            SetStatus($"PLAYING {name}", Good);
            lockedIn = true;
            return true;
        }

        protected override void OnClosed(EventArgs e)
        {
            running?.Cancel();
            UnregisterHotKey(new WindowInteropHelper(this).Handle, StopHotkey);
            base.OnClosed(e);
        }
    }
}
