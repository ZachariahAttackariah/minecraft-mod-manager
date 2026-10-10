// The app window: a tab strip whose first, pinned tab is the manager page. Links from the manager
// and pop-ups from websites open as more tabs in the same window, each with back / forward /
// reload and an address bar. A .jar downloaded in any tab goes straight into the mods folder.
// Colours follow Windows' light/dark app setting and match the page.
//
// Keys: Ctrl+T new tab (Modrinth), Ctrl+W close tab, Ctrl+Tab / Ctrl+Shift+Tab switch tabs,
// Ctrl+1..9 jump to a tab, Ctrl+Shift+T reopen the last closed tab, Ctrl+L or F6 address bar.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace MinecraftModManager
{
    static partial class Program
    {
        const string NewTabUrl = "https://modrinth.com/mods";

        static Task<CoreWebView2Environment> environment;

        // One shared browser profile for every tab, kept in %LOCALAPPDATA%, so sign-ins and
        // cookies on Modrinth / CurseForge survive between runs.
        static Task<CoreWebView2Environment> GetEnvironment()
        {
            if (environment == null)
            {
                string profileDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MinecraftModManager", "WebView2");
                environment = CoreWebView2Environment.CreateAsync(null, profileDir, null);
            }
            return environment;
        }

        // Anything that wants a web page shown: a tab in the app window, or the normal browser without one.
        static void OpenInAppBrowser(string url)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)) return;
            if (window != null && !window.IsDisposed) window.OpenTab(uri.AbsoluteUri);
            else try { Process.Start(uri.AbsoluteUri); } catch { }
        }

        // Tells the page something happened ("refresh", "server", "status:<text>", "confirm-quit"). Any thread.
        static void NotifyPage(string message)
        {
            AppWindow w = window;
            if (w == null || ui == null) return;
            ui.Post(delegate { if (!w.IsDisposed) w.Notify(message); }, null);
        }

        sealed class Palette
        {
            public bool Dark;
            public Color Strip, Tab, Hover, Ink, Muted, Line, Accent, Field;
        }

        // The page's own colour tokens (see :root in mod-manager.template.html): the Minecraft Launcher's
        // dark greys and green. The page is dark-only, so the window is too.
        static Palette ReadPalette()
        {
            return new Palette { Dark = true, Strip = Rgb(0x1b1b1b), Tab = Rgb(0x262626), Hover = Rgb(0x313233), Ink = Rgb(0xffffff),
                Muted = Rgb(0xb1b2b5), Line = Rgb(0x3e3f41), Accent = Rgb(0x3c8527), Field = Rgb(0x1b1b1b) };
        }
        static Color Rgb(int rgb) { return Color.FromArgb(255, (rgb >> 16) & 0xff, (rgb >> 8) & 0xff, rgb & 0xff); }

        sealed class BrowserTab
        {
            public WebView2 View;
            public bool IsManager;
            public string Title = "New tab";
            public string Url = "";
            public Image Icon;
            public bool Loading = true;
            public string Status;
            public Rectangle Bounds, Close;
        }

        sealed class AppWindow : Form
        {
            readonly List<BrowserTab> tabs = new List<BrowserTab>();
            readonly Stack<string> closedUrls = new Stack<string>();
            readonly BrowserTab manager = new BrowserTab { IsManager = true, Title = "Mod manager" };
            readonly TabStrip strip;
            readonly ToolStrip nav = new ToolStrip();
            readonly ToolStripButton back = new ToolStripButton("←");
            readonly ToolStripButton forward = new ToolStripButton("→");
            readonly ToolStripButton reload = new ToolStripButton("↻");
            readonly ToolStripTextBox address = new ToolStripTextBox();
            readonly ToolStripLabel status = new ToolStripLabel();
            readonly ToolStripButton external = new ToolStripButton("Open in my browser");
            readonly Panel host = new Panel();
            readonly Icon appIcon = LoadAppIcon();
            Palette palette = ReadPalette();
            BrowserTab active;
            bool allowClose;

            public AppWindow()
            {
                Text = AppName;
                Icon = appIcon;
                StartPosition = FormStartPosition.CenterScreen;
                Size = new Size(1320, 900);
                MinimumSize = new Size(640, 480);
                KeyPreview = true;
                LoadBounds();

                strip = new TabStrip(this);
                strip.Dock = DockStyle.Top;

                nav.GripStyle = ToolStripGripStyle.Hidden;
                nav.Dock = DockStyle.Top;
                nav.CanOverflow = false;
                nav.Padding = new Padding(Px(6), Px(4), Px(6), Px(4));
                nav.Font = new Font("Segoe UI", 9.5f);
                foreach (ToolStripButton b in new[] { back, forward, reload })
                {
                    b.Font = new Font("Segoe UI Symbol", 11f);
                    b.AutoSize = false;
                    b.Size = new Size(Px(34), Px(28));
                }
                back.ToolTipText = "Back (Alt+Left)";
                forward.ToolTipText = "Forward (Alt+Right)";
                reload.ToolTipText = "Reload (F5)";
                back.Click += delegate { CoreWebView2 c = Core(active); if (c != null && c.CanGoBack) c.GoBack(); };
                forward.Click += delegate { CoreWebView2 c = Core(active); if (c != null && c.CanGoForward) c.GoForward(); };
                reload.Click += delegate { CoreWebView2 c = Core(active); if (c != null) c.Reload(); };
                address.AutoSize = false;
                address.BorderStyle = BorderStyle.FixedSingle;
                address.Font = new Font("Segoe UI", 10f);
                address.KeyDown += OnAddressKey;
                address.GotFocus += delegate { BeginInvoke(new Action(address.SelectAll)); };
                external.Alignment = ToolStripItemAlignment.Right;
                external.ToolTipText = "Open this page in your normal web browser";
                external.Click += delegate
                {
                    if (active == null || active.IsManager || string.IsNullOrEmpty(active.Url)) return;
                    try { Process.Start(active.Url); } catch { }
                };
                status.Alignment = ToolStripItemAlignment.Right;
                nav.Items.AddRange(new ToolStripItem[] { back, forward, reload, address, external, status });
                nav.Resize += delegate { FitAddress(); };
                nav.Visible = false;

                host.Dock = DockStyle.Fill;
                Controls.Add(host);
                Controls.Add(nav);
                Controls.Add(strip);

                tabs.Add(manager);
                ApplyPalette();
                SystemEvents.UserPreferenceChanged += OnThemeChanged;
                Load += OnLoad;
            }

            int Px(int px) { return (int)Math.Round(px * DeviceDpi / 96.0); }

            void OnThemeChanged(object sender, UserPreferenceChangedEventArgs e)
            {
                if (e.Category != UserPreferenceCategory.General) return;
                if (IsDisposed) return;
                BeginInvoke(new Action(delegate { palette = ReadPalette(); ApplyPalette(); }));
            }

            void ApplyPalette()
            {
                BackColor = palette.Tab;
                host.BackColor = palette.Tab;
                nav.Renderer = new NavRenderer(palette);
                nav.BackColor = palette.Tab;
                nav.ForeColor = palette.Ink;
                address.BackColor = palette.Field;
                address.ForeColor = palette.Ink;
                strip.Invalidate();
                if (IsHandleCreated) ApplyTitleBarColours();
            }

            // Windows 11 lets the title bar take the tab strip's colour, so the two read as one bar.
            void ApplyTitleBarColours()
            {
                try
                {
                    int dark = palette.Dark ? 1 : 0;
                    DwmSetWindowAttribute(Handle, 20, ref dark, 4);
                    int caption = ColorTranslator.ToWin32(palette.Strip);
                    DwmSetWindowAttribute(Handle, 35, ref caption, 4);
                    int text = ColorTranslator.ToWin32(palette.Ink);
                    DwmSetWindowAttribute(Handle, 36, ref text, 4);
                }
                catch { }
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                windowHandle = Handle;
                ApplyTitleBarColours();
            }

            async void OnLoad(object sender, EventArgs e)
            {
                try
                {
                    manager.View = NewView();
                    await manager.View.EnsureCoreWebView2Async(await GetEnvironment());
                }
                catch (Exception ex)
                {
                    Hide();
                    MessageBox.Show("Couldn't open the app window, so Minecraft Mod Manager will open in your web browser instead.\n\n" + ex.Message,
                        AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    OpenBrowser();
                    return;
                }
                CoreWebView2 core = manager.View.CoreWebView2;
                core.Settings.IsStatusBarEnabled = false;
                // Keep the manager tab on the manager: anything else opens in a tab of its own.
                core.NewWindowRequested += delegate (object s, CoreWebView2NewWindowRequestedEventArgs a)
                {
                    a.Handled = true;
                    OpenTab(a.Uri);
                };
                core.NavigationStarting += delegate (object s, CoreWebView2NavigationStartingEventArgs a)
                {
                    if (!a.Uri.StartsWith(origin + "/", StringComparison.OrdinalIgnoreCase))
                    {
                        a.Cancel = true;
                        OpenTab(a.Uri);
                    }
                };
                core.NavigationCompleted += delegate { manager.Loading = false; strip.Invalidate(); };
                core.DownloadStarting += delegate (object s, CoreWebView2DownloadStartingEventArgs a)
                {
                    HandleDownload(a, delegate (string message) { Notify("status:" + message); });
                };
                core.Navigate(origin + "/");
                ShowTab(manager);
            }

            WebView2 NewView()
            {
                var view = new WebView2();
                view.Dock = DockStyle.Fill;
                view.Visible = false;
                view.DefaultBackgroundColor = palette.Tab;
                view.KeyDown += OnViewKeyDown;
                host.Controls.Add(view);
                return view;
            }

            static CoreWebView2 Core(BrowserTab tab)
            {
                return tab == null || tab.View == null ? null : tab.View.CoreWebView2;
            }

            // Messages the page listens for: see NotifyPage.
            public void Notify(string message)
            {
                CoreWebView2 core = Core(manager);
                if (core != null) core.PostWebMessageAsString(message);
            }

            // Clears cached pages and images for every tab (sign-ins and cookies stay).
            public Task ClearBrowserCache()
            {
                CoreWebView2 core = Core(manager);
                if (core == null) return null;
                return core.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.DiskCache | CoreWebView2BrowsingDataKinds.CacheStorage);
            }

            public void ShowManager()
            {
                if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                Activate();
                ShowTab(manager);
            }

            public void ForceClose()
            {
                allowClose = true;
                Close();
            }

            public async void OpenTab(string url)
            {
                Uri uri;
                if (!Uri.TryCreate(url ?? "", UriKind.Absolute, out uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)) return;
                try
                {
                    BrowserTab tab = await CreateTab(uri.Host);
                    tab.Url = uri.AbsoluteUri;
                    tab.View.CoreWebView2.Navigate(uri.AbsoluteUri);
                }
                catch (Exception ex)
                {
                    Notify("status:Couldn't open a tab: " + ex.Message);
                }
            }

            async Task<BrowserTab> CreateTab(string title)
            {
                var tab = new BrowserTab { Title = string.IsNullOrEmpty(title) ? "New tab" : title };
                tab.View = NewView();
                tabs.Add(tab);
                ShowTab(tab);
                await tab.View.EnsureCoreWebView2Async(await GetEnvironment());
                WireWebTab(tab);
                return tab;
            }

            void WireWebTab(BrowserTab tab)
            {
                CoreWebView2 core = tab.View.CoreWebView2;
                core.DocumentTitleChanged += delegate
                {
                    tab.Title = string.IsNullOrWhiteSpace(core.DocumentTitle) ? HostOf(core.Source) : core.DocumentTitle;
                    Changed(tab);
                };
                core.SourceChanged += delegate { tab.Url = core.Source; Changed(tab); };
                core.HistoryChanged += delegate { Changed(tab); };
                core.NavigationStarting += delegate { tab.Loading = true; Changed(tab); };
                core.NavigationCompleted += delegate { tab.Loading = false; Changed(tab); };
                core.FaviconChanged += async delegate
                {
                    Image icon = null;
                    try
                    {
                        using (Stream stream = await core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png))
                        {
                            if (stream != null)
                            {
                                var copy = new MemoryStream();
                                stream.CopyTo(copy);
                                if (copy.Length > 0)
                                {
                                    copy.Position = 0;
                                    using (Image img = Image.FromStream(copy)) icon = new Bitmap(img);
                                }
                            }
                        }
                    }
                    catch { icon = null; }
                    tab.Icon = icon;
                    strip.Invalidate();
                };
                // Pop-ups and target=_blank links become tabs too (keeping window.opener, which sign-in pages need).
                core.NewWindowRequested += async delegate (object s, CoreWebView2NewWindowRequestedEventArgs a)
                {
                    CoreWebView2Deferral deferral = a.GetDeferral();
                    try
                    {
                        BrowserTab opened = await CreateTab(HostOf(a.Uri));
                        a.NewWindow = opened.View.CoreWebView2;
                        a.Handled = true;
                    }
                    catch
                    {
                        a.Handled = false;
                    }
                    finally
                    {
                        deferral.Complete();
                    }
                };
                core.WindowCloseRequested += delegate { CloseTab(tab); };
                core.DownloadStarting += delegate (object s, CoreWebView2DownloadStartingEventArgs a)
                {
                    HandleDownload(a, delegate (string message)
                    {
                        tab.Status = message;
                        if (tab == active) UpdateChrome();
                        Notify("status:" + message);
                    });
                };
            }

            static string HostOf(string url)
            {
                Uri uri;
                return Uri.TryCreate(url ?? "", UriKind.Absolute, out uri) ? uri.Host.Replace("www.", "") : "New tab";
            }

            void Changed(BrowserTab tab)
            {
                strip.Invalidate();
                if (tab == active) UpdateChrome();
            }

            void ShowTab(BrowserTab tab)
            {
                if (tab == null || tab == active) return;
                BrowserTab previous = active;
                active = tab;
                if (tab.View != null)
                {
                    tab.View.Visible = true;
                    tab.View.BringToFront();
                }
                if (previous != null && previous.View != null) previous.View.Visible = false;
                UpdateChrome();
                strip.Invalidate();
                if (tab.View != null && ContainsFocus) tab.View.Focus();
            }

            void UpdateChrome()
            {
                if (active == null) return;
                bool web = !active.IsManager;
                nav.Visible = web;
                Text = web ? active.Title + " - " + AppName : AppName;
                if (!web) return;
                CoreWebView2 core = Core(active);
                back.Enabled = core != null && core.CanGoBack;
                forward.Enabled = core != null && core.CanGoForward;
                if (!address.Focused) address.Text = active.Url;
                status.Text = active.Status ?? "Jars you download here go into the mods folder";
                status.ToolTipText = modsDir;
                FitAddress();
            }

            void FitAddress()
            {
                int others = 0;
                foreach (ToolStripItem item in nav.Items)
                    if (item != address && item.Visible) others += item.Width + item.Margin.Horizontal;
                int width = nav.DisplayRectangle.Width - others - address.Margin.Horizontal - 8;
                address.Width = Math.Max(Px(160), width);
                address.Height = Px(26);
            }

            void OnAddressKey(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape)
                {
                    address.Text = active.Url;
                    if (active.View != null) active.View.Focus();
                    e.SuppressKeyPress = true;
                    return;
                }
                if (e.KeyCode != Keys.Enter) return;
                e.SuppressKeyPress = true;
                string text = address.Text.Trim();
                if (text.Length == 0) return;
                if (!text.Contains("://")) text = (text.Contains(".") && !text.Contains(" ") ? "https://" : "https://modrinth.com/mods?q=") + (text.Contains(".") && !text.Contains(" ") ? text : Uri.EscapeDataString(text));
                Uri uri;
                if (!Uri.TryCreate(text, UriKind.Absolute, out uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)) return;
                CoreWebView2 core = Core(active);
                if (core == null) return;
                core.Navigate(uri.AbsoluteUri);
                active.View.Focus();
            }

            void CloseTab(BrowserTab tab)
            {
                if (tab == null || tab.IsManager) return;
                int index = tabs.IndexOf(tab);
                if (index < 0) return;
                tabs.RemoveAt(index);
                if (!string.IsNullOrEmpty(tab.Url)) closedUrls.Push(tab.Url);
                if (active == tab)
                {
                    active = null;
                    ShowTab(tabs[Math.Min(index, tabs.Count - 1)]);
                }
                if (tab.View != null)
                {
                    host.Controls.Remove(tab.View);
                    tab.View.Dispose();
                }
                if (tab.Icon != null) tab.Icon.Dispose();
                strip.Invalidate();
            }

            void Cycle(int step)
            {
                int i = tabs.IndexOf(active);
                ShowTab(tabs[((i + step) % tabs.Count + tabs.Count) % tabs.Count]);
            }

            // Shortcut keys arrive two ways: through ProcessCmdKey when a WinForms control (the address bar)
            // has focus, and as the WebView2 control's own KeyDown event when a page has focus.
            protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
            {
                return HandleShortcut(keyData) || base.ProcessCmdKey(ref msg, keyData);
            }

            void OnViewKeyDown(object sender, KeyEventArgs e)
            {
                if (HandleShortcut(e.KeyData)) e.Handled = true;
            }

            bool HandleShortcut(Keys keyData)
            {
                switch (keyData)
                {
                    case Keys.Control | Keys.T:
                        OpenTab(NewTabUrl);
                        return true;
                    case Keys.Control | Keys.W:
                    case Keys.Control | Keys.F4:
                        if (active != null && !active.IsManager) { CloseTab(active); return true; }
                        break;
                    case Keys.Control | Keys.Tab:
                    case Keys.Control | Keys.PageDown:
                        Cycle(1);
                        return true;
                    case Keys.Control | Keys.Shift | Keys.Tab:
                    case Keys.Control | Keys.PageUp:
                        Cycle(-1);
                        return true;
                    case Keys.Control | Keys.Shift | Keys.T:
                        if (closedUrls.Count > 0) OpenTab(closedUrls.Pop());
                        return true;
                    case Keys.Control | Keys.L:
                    case Keys.F6:
                        if (active != null && !active.IsManager) { address.Focus(); address.SelectAll(); return true; }
                        break;
                }
                Keys key = keyData & Keys.KeyCode;
                if ((keyData & Keys.Modifiers) == Keys.Control && key >= Keys.D1 && key <= Keys.D9)
                {
                    int n = key - Keys.D1;
                    ShowTab(tabs[n == 8 ? tabs.Count - 1 : Math.Min(n, tabs.Count - 1)]);
                    return true;
                }
                return false;
            }

            protected override void OnFormClosing(FormClosingEventArgs e)
            {
                // A running server needs a proper "stop" (it saves the world), so ask the page first.
                if (!allowClose && e.CloseReason == CloseReason.UserClosing && ServerBusy())
                {
                    e.Cancel = true;
                    ShowManager();
                    Notify("confirm-quit");
                    return;
                }
                SaveBounds();
                base.OnFormClosing(e);
            }

            protected override void OnFormClosed(FormClosedEventArgs e)
            {
                SystemEvents.UserPreferenceChanged -= OnThemeChanged;
                base.OnFormClosed(e);
            }

            void LoadBounds()
            {
                try
                {
                    if (savedWindow == null) return;
                    var r = new Rectangle(Convert.ToInt32(Get(savedWindow, "x")), Convert.ToInt32(Get(savedWindow, "y")),
                        Convert.ToInt32(Get(savedWindow, "width")), Convert.ToInt32(Get(savedWindow, "height")));
                    if (r.Width < 640 || r.Height < 480) return;
                    if (!Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(new Rectangle(r.X + 40, r.Y, r.Width - 80, 40)))) return;
                    StartPosition = FormStartPosition.Manual;
                    Bounds = r;
                    if (Get(savedWindow, "maximized") is bool && (bool)Get(savedWindow, "maximized")) WindowState = FormWindowState.Maximized;
                }
                catch { }
            }

            void SaveBounds()
            {
                Rectangle r = WindowState == FormWindowState.Normal ? Bounds : base.RestoreBounds;
                savedWindow = Obj("x", r.X, "y", r.Y, "width", r.Width, "height", r.Height, "maximized", WindowState == FormWindowState.Maximized);
                SaveSettings();
            }

            // ---- The tab strip ----

            sealed class TabStrip : Control
            {
                readonly AppWindow w;
                readonly ToolTip tip = new ToolTip();
                Rectangle plus;
                int hover = -1;
                bool hoverClose, hoverPlus;
                string tipText = "";

                public TabStrip(AppWindow owner)
                {
                    w = owner;
                    SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                    Font = new Font("Segoe UI", 9.5f);
                    Height = Px(38);
                }

                int Px(int px) { return (int)Math.Round(px * DeviceDpi / 96.0); }

                void ArrangeTabs()
                {
                    int x = Px(8), top = Px(7), height = Height - top;
                    int plusWidth = Px(34);
                    int pinned = Px(168);
                    int webCount = w.tabs.Count - 1;
                    int room = Width - x - pinned - plusWidth - Px(16);
                    int each = webCount == 0 ? 0 : Math.Max(Px(56), Math.Min(Px(230), room / webCount));
                    foreach (BrowserTab tab in w.tabs)
                    {
                        int width = tab.IsManager ? pinned : each;
                        tab.Bounds = new Rectangle(x, top, width, height);
                        int c = Px(16);
                        tab.Close = tab.IsManager || width < Px(90) && tab != w.active ? Rectangle.Empty
                            : new Rectangle(x + width - c - Px(9), top + (height - c) / 2, c, c);
                        x += width + Px(2);
                    }
                    plus = new Rectangle(x + Px(4), top + Px(4), Px(28), height - Px(8));
                }

                protected override void OnPaint(PaintEventArgs e)
                {
                    Palette p = w.palette;
                    Graphics g = e.Graphics;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var b = new SolidBrush(p.Strip)) g.FillRectangle(b, ClientRectangle);
                    ArrangeTabs();
                    for (int i = 0; i < w.tabs.Count; i++) DrawTab(g, p, w.tabs[i], i);
                    // + opens Modrinth in a new tab.
                    if (hoverPlus) using (var b = new SolidBrush(p.Hover)) FillRound(g, b, plus, Px(5));
                    using (var pen = new Pen(p.Muted, Px(2) * 0.8f))
                    {
                        int cx = plus.X + plus.Width / 2, cy = plus.Y + plus.Height / 2, r = Px(6);
                        g.DrawLine(pen, cx - r, cy, cx + r, cy);
                        g.DrawLine(pen, cx, cy - r, cx, cy + r);
                    }
                }

                void DrawTab(Graphics g, Palette p, BrowserTab tab, int index)
                {
                    bool isActive = tab == w.active;
                    Rectangle r = tab.Bounds;
                    if (isActive || index == hover)
                    {
                        using (var path = TopRounded(new Rectangle(r.X, r.Y, r.Width, r.Height + Px(4)), Px(8)))
                        using (var b = new SolidBrush(isActive ? p.Tab : p.Hover))
                            g.FillPath(b, path);
                    }
                    else if (index + 1 < w.tabs.Count && w.tabs[index + 1] != w.active && index + 1 != hover)
                    {
                        using (var pen = new Pen(p.Line)) g.DrawLine(pen, r.Right + Px(1), r.Y + Px(8), r.Right + Px(1), r.Bottom - Px(8));
                    }

                    int icon = Px(16);
                    var iconRect = new Rectangle(r.X + Px(11), r.Y + (r.Height - icon) / 2, icon, icon);
                    if (tab.IsManager)
                    {
                        g.DrawIcon(w.appIcon, iconRect);
                    }
                    else if (tab.Loading && tab.Icon == null)
                    {
                        using (var pen = new Pen(p.Accent, Px(2))) g.DrawArc(pen, Rectangle.Inflate(iconRect, -Px(2), -Px(2)), 0, 270);
                    }
                    else if (tab.Icon != null)
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.DrawImage(tab.Icon, iconRect);
                    }
                    else
                    {
                        using (var pen = new Pen(p.Muted)) g.DrawEllipse(pen, Rectangle.Inflate(iconRect, -Px(2), -Px(2)));
                    }

                    int textLeft = iconRect.Right + Px(8);
                    int textRight = tab.Close.IsEmpty ? r.Right - Px(8) : tab.Close.Left - Px(4);
                    var textRect = new Rectangle(textLeft, r.Y, Math.Max(0, textRight - textLeft), r.Height);
                    Color ink = isActive ? p.Ink : p.Muted;
                    using (Font f = tab.IsManager ? new Font(Font, FontStyle.Bold) : null)
                        TextRenderer.DrawText(g, tab.Title, f ?? Font, textRect, ink,
                            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);

                    if (!tab.Close.IsEmpty && (isActive || index == hover))
                    {
                        if (index == hover && hoverClose) using (var b = new SolidBrush(p.Line)) g.FillEllipse(b, Rectangle.Inflate(tab.Close, Px(2), Px(2)));
                        int m = Px(5);
                        using (var pen = new Pen(ink, Px(2) * 0.75f))
                        {
                            Rectangle c = tab.Close;
                            g.DrawLine(pen, c.X + m, c.Y + m, c.Right - m, c.Bottom - m);
                            g.DrawLine(pen, c.Right - m, c.Y + m, c.X + m, c.Bottom - m);
                        }
                    }
                }

                static GraphicsPath TopRounded(Rectangle r, int radius)
                {
                    var path = new GraphicsPath();
                    int d = radius * 2;
                    path.AddArc(r.X, r.Y, d, d, 180, 90);
                    path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                    path.AddLine(r.Right, r.Bottom, r.X, r.Bottom);
                    path.CloseFigure();
                    return path;
                }

                static void FillRound(Graphics g, Brush b, Rectangle r, int radius)
                {
                    using (var path = new GraphicsPath())
                    {
                        int d = radius * 2;
                        path.AddArc(r.X, r.Y, d, d, 180, 90);
                        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                        path.CloseFigure();
                        g.FillPath(b, path);
                    }
                }

                int HitTab(Point pt)
                {
                    for (int i = 0; i < w.tabs.Count; i++) if (w.tabs[i].Bounds.Contains(pt)) return i;
                    return -1;
                }

                protected override void OnMouseMove(MouseEventArgs e)
                {
                    int h = HitTab(e.Location);
                    bool hc = h >= 0 && w.tabs[h].Close.Contains(e.Location);
                    bool hp = plus.Contains(e.Location);
                    if (h != hover || hc != hoverClose || hp != hoverPlus)
                    {
                        hover = h;
                        hoverClose = hc;
                        hoverPlus = hp;
                        Invalidate();
                    }
                    string text = hp ? "New tab (Ctrl+T)" : hc ? "Close tab (Ctrl+W)" : h < 0 ? "" : w.tabs[h].IsManager ? "Minecraft Mod Manager" : w.tabs[h].Title + "\n" + w.tabs[h].Url;
                    if (text != tipText)
                    {
                        tipText = text;
                        tip.SetToolTip(this, text);
                    }
                }

                protected override void OnMouseLeave(EventArgs e)
                {
                    hover = -1;
                    hoverClose = hoverPlus = false;
                    Invalidate();
                }

                protected override void OnMouseDown(MouseEventArgs e)
                {
                    int h = HitTab(e.Location);
                    if (e.Button == MouseButtons.Left)
                    {
                        if (plus.Contains(e.Location)) w.OpenTab(NewTabUrl);
                        else if (h >= 0 && w.tabs[h].Close.Contains(e.Location)) w.CloseTab(w.tabs[h]);
                        else if (h >= 0) w.ShowTab(w.tabs[h]);
                    }
                    else if (e.Button == MouseButtons.Middle && h >= 0)
                    {
                        w.CloseTab(w.tabs[h]);
                    }
                }
            }

            sealed class NavRenderer : ToolStripProfessionalRenderer
            {
                readonly Palette p;
                public NavRenderer(Palette palette) { p = palette; RoundedEdges = false; }

                protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
                {
                    using (var b = new SolidBrush(p.Tab)) e.Graphics.FillRectangle(b, e.AffectedBounds);
                }

                protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
                {
                    using (var pen = new Pen(p.Line)) e.Graphics.DrawLine(pen, 0, e.ToolStrip.Height - 1, e.ToolStrip.Width, e.ToolStrip.Height - 1);
                }

                protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e)
                {
                    if (!e.Item.Enabled || !(e.Item.Selected || e.Item.Pressed)) return;
                    var r = new Rectangle(1, 1, e.Item.Width - 2, e.Item.Height - 2);
                    using (var b = new SolidBrush(e.Item.Pressed ? p.Line : p.Hover)) e.Graphics.FillRectangle(b, r);
                    using (var pen = new Pen(p.Line)) e.Graphics.DrawRectangle(pen, r);
                }

                protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
                {
                    Color c = !e.Item.Enabled ? Color.FromArgb(110, p.Muted) : e.Item is ToolStripLabel ? p.Muted : p.Ink;
                    TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, e.TextRectangle, c, e.TextFormat | TextFormatFlags.EndEllipsis);
                }
            }
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    }
}
