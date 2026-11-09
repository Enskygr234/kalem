using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace Kalem;

public partial class MainWindow : Window
{
    sealed record ActionDef(string Label, string Instruction);

    static readonly ActionDef[] ActionList =
    {
        new("Düzelt", "Yazım, noktalama ve dilbilgisi hatalarını düzelt. Anlamı, üslubu ve dili değiştirme."),
        new("Kısalt", "Metni anlamını koruyarak belirgin şekilde kısalt."),
        new("Netleştir", "Metni daha açık, akıcı ve anlaşılır hâle getir; anlamı değiştirme."),
        new("Resmî", "Metni profesyonel ve resmî bir üsluba çevir."),
        new("Samimi", "Metni sıcak, samimi ve doğal bir üsluba çevir."),
        new("İngilizce", "Metni doğal, akıcı bir İngilizceye çevir."),
        new("Türkçe", "Metni doğal, akıcı bir Türkçeye çevir."),
        new("Özetle", "Metni Türkçe olarak birkaç kısa madde hâlinde özetle. Her madde \"- \" ile başlasın."),
        new("Açıkla", "Metnin ne anlatmak istediğini Türkçe ve sade bir dille açıkla."),
    };

    Settings _settings = Settings.Load();
    IntPtr _hwnd, _target;
    WF.NotifyIcon? _tray;
    string _hotkeyText = "";
    bool _hadSelection, _busy;
    CancellationTokenSource? _cts;
    (string Instruction, string Label)? _last;
    WF.DataObject? _clipBackup;
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public MainWindow()
    {
        InitializeComponent();
        BuildActions();
        Deactivated += (_, _) => { if (IsVisible) HidePopup(); };
        PreviewKeyDown += OnKey;
    }

    // ================= Startup =================
    public void Init(bool startHidden)
    {
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        HwndSource.FromHwnd(_hwnd)!.AddHook(WndProc);
        RegisterHotkey();
        CreateTray();
        UpdateModelTag();

        if (startHidden) return;
        if (_settings.ApiKey == null) OpenFromTray();
        else _tray!.ShowBalloonTip(5000, "Kalem hazır",
            $"Herhangi bir uygulamada metin seç ve {_hotkeyText} tuşlarına bas.", WF.ToolTipIcon.None);
    }

    void RegisterHotkey()
    {
        (uint mods, uint vk, string text)[] options =
        {
            (Native.MOD_CONTROL | Native.MOD_ALT, 0x20, "Ctrl+Alt+Boşluk"),
            (Native.MOD_CONTROL | Native.MOD_ALT, 0x4B, "Ctrl+Alt+K"),
            (Native.MOD_CONTROL | Native.MOD_SHIFT, 0x4B, "Ctrl+Shift+K"),
        };
        foreach (var o in options)
            if (Native.RegisterHotKey(_hwnd, 1, o.mods | Native.MOD_NOREPEAT, o.vk)) { _hotkeyText = o.text; return; }
        _hotkeyText = "";
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && wParam == (IntPtr)1) { _ = OnHotkey(); handled = true; }
        return IntPtr.Zero;
    }

    // ================= Opening =================
    async Task OnHotkey()
    {
        if (IsVisible && IsActive) { HidePopup(); return; }
        var fg = Native.GetForegroundWindow();
        _target = fg == _hwnd ? IntPtr.Zero : fg;
        bool terminal = _target != IntPtr.Zero && Native.IsTerminal(_target);
        string selected = "";
        if (_target != IntPtr.Zero && !terminal)
        {
            try { selected = await GrabSelection(); }
            catch (Exception ex) { App.Log(ex); }
        }
        OpenPopup(selected, centered: false, terminal);
    }

    public void OpenFromTray()
    {
        _target = IntPtr.Zero;
        _clipBackup = null;
        OpenPopup("", centered: true, terminal: false);
    }

    /// <summary>Copies the user's current selection via Ctrl+C, then puts their previous clipboard back.</summary>
    async Task<string> GrabSelection()
    {
        var sw = Stopwatch.StartNew();
        while (Native.HotkeyKeysDown() && sw.ElapsedMilliseconds < 700) await Task.Delay(15);

        await Task.Delay(40);   // let the target app see the hotkey's key-ups first

        _clipBackup = BackupClipboard();
        uint seq = Native.GetClipboardSequenceNumber();
        string text = "";
        bool changed = false;
        // Some apps occasionally drop a synthetic Ctrl+C that arrives right after a hotkey; try twice.
        for (int attempt = 0; attempt < 2 && !changed; attempt++)
        {
            Native.SendCopy();
            for (int i = 0; i < (attempt == 0 ? 13 : 20); i++)
            {
                await Task.Delay(20);
                if (Native.GetClipboardSequenceNumber() != seq) { changed = true; await Task.Delay(40); break; }
            }
        }
        if (changed)
        {
            try { if (WF.Clipboard.ContainsText()) text = WF.Clipboard.GetText(); } catch { }
            RestoreClipboard(_clipBackup);
        }
        return text;
    }

    static WF.DataObject? BackupClipboard()
    {
        try
        {
            var d = WF.Clipboard.GetDataObject();
            if (d == null) return null;
            var copy = new WF.DataObject();
            bool any = false;
            foreach (var f in new[] { WF.DataFormats.UnicodeText, WF.DataFormats.Rtf, WF.DataFormats.Html, WF.DataFormats.FileDrop, WF.DataFormats.Bitmap })
            {
                try
                {
                    if (d.GetDataPresent(f, false) && d.GetData(f, false) is object v) { copy.SetData(f, false, v); any = true; }
                }
                catch { }
            }
            return any ? copy : null;
        }
        catch { return null; }
    }

    static void RestoreClipboard(WF.DataObject? backup)
    {
        try
        {
            if (backup != null) WF.Clipboard.SetDataObject(backup, true, 5, 40);
            else WF.Clipboard.Clear();
        }
        catch { }
    }

    void OpenPopup(string selected, bool centered, bool terminal)
    {
        _cts?.Cancel();
        _hadSelection = selected.Trim().Length > 0;
        Source.Text = _hadSelection ? selected : "";
        SourceLabel.Text = _hadSelection ? "SEÇİLİ METİN" : "METİN (İSTEĞE BAĞLI)";
        Instruction.Text = "";
        ResetResult();
        _last = null;

        bool canPaste = _target != IntPtr.Zero && !terminal;
        ApplyTxt.Text = !canPaste ? "Kopyala ve kapat" : _hadSelection ? "Değiştir" : "Yapıştır";
        Hint.Text = terminal ? "Terminallerde seçim otomatik alınmaz; metni yukarıya yapıştırabilirsin."
            : !canPaste ? "Sonuç panoya kopyalanır."
            : _hadSelection ? "Ctrl+Enter: sonucu seçili metnin yerine koyar."
            : "Ctrl+Enter: sonucu imlecin olduğu yere yapıştırır.";
        if (terminal) _target = IntPtr.Zero;

        if (_settings.ApiKey == null) ShowSettings(); else ShowMain();

        PositionWindow(centered);
        Show();
        Activate();
        Native.SetForegroundWindow(_hwnd);
        if (SettingsView.Visibility == Visibility.Visible) KeyBox.Focus(); else { Instruction.Focus(); Keyboard.Focus(Instruction); }
    }

    void PositionWindow(bool centered)
    {
        Native.GetCursorPos(out var p);
        var wa = Native.WorkAreaAt(p);
        Native.GetWindowRect(_hwnd, out var wr);
        int w = wr.R - wr.L, h = wr.B - wr.T, x, y;
        if (centered) { x = wa.L + (wa.R - wa.L - w) / 2; y = wa.T + (wa.B - wa.T - h) / 2; }
        else { x = p.X - 60; y = p.Y + 8; if (y + h > wa.B) y = p.Y - h - 8; }
        x = Math.Max(wa.L, Math.Min(x, wa.R - w));
        y = Math.Max(wa.T, Math.Min(y, wa.B - h));
        Native.SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0, 0x1 | 0x4 | 0x10);
    }

    void HidePopup()
    {
        _cts?.Cancel();
        if (IsVisible) Hide();
    }

    // ================= Actions =================
    void BuildActions()
    {
        for (int i = 0; i < ActionList.Length; i++)
        {
            var a = ActionList[i];
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = a.Label, VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(new Border
            {
                Background = (Brush)FindResource("AccentSoftBrush"), CornerRadius = new CornerRadius(4),
                Padding = new Thickness(5, 0, 5, 1), Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = (i + 1).ToString(), FontSize = 10.5, Foreground = (Brush)FindResource("AccentBrush") },
            });
            var b = new Button { Content = sp, Style = (Style)FindResource("ActionBtn"), ToolTip = $"Ctrl+{i + 1}", Tag = i };
            b.Click += (s, _) => RunAction((int)((Button)s).Tag);
            Actions.Children.Add(b);
        }
        UpdateActionState();
    }

    void UpdateActionState()
    {
        bool has = Source.Text.Trim().Length > 0;
        foreach (UIElement c in Actions.Children) c.IsEnabled = has;
        SourceCount.Text = has ? $"{Source.Text.Length:N0} karakter" : "";
    }

    void Source_TextChanged(object sender, TextChangedEventArgs e) => UpdateActionState();

    void RunAction(int i)
    {
        if (i < 0 || i >= ActionList.Length || Source.Text.Trim().Length == 0) return;
        _ = Run(ActionList[i].Instruction, ActionList[i].Label);
    }

    void Instruction_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None) return;
        var t = Instruction.Text.Trim();
        if (t.Length > 0) _ = Run(t, "Talimat");
        else if (ApplyBtn.IsEnabled) _ = Apply();
        e.Handled = true;
    }

    async Task Run(string instruction, string label)
    {
        var key = _settings.ApiKey;
        if (key == null) { ShowSettings(); return; }
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        _last = (instruction, label);

        ResetResult();
        SetBusy(true, $"{label} · Gemini yazıyor…");
        var sb = new StringBuilder();
        var sw = Stopwatch.StartNew();
        try
        {
            await Gemini.StreamAsync(key, _settings.Model, Gemini.BuildPrompt(instruction, Source.Text), d =>
            {
                if (cts.IsCancellationRequested) return;
                sb.Append(d);
                Result.Text = sb.ToString();
                Result.ScrollToEnd();
            }, cts.Token);
            if (cts != _cts) return;
            Result.Text = Clean(sb.ToString());
            SetBusy(false, $"{label} · {sw.Elapsed.TotalSeconds:0.0} sn");
        }
        catch (OperationCanceledException) { if (cts == _cts) SetBusy(false, "İptal edildi"); }
        catch (GeminiException ex) { if (cts == _cts) ShowError(ex.Message); }
        catch (Exception ex) { App.Log(ex); if (cts == _cts) ShowError("Beklenmeyen hata: " + ex.Message); }
        finally
        {
            if (cts == _cts)
            {
                bool ok = Result.Text.Trim().Length > 0 && ErrorTxt.Visibility != Visibility.Visible;
                ApplyBtn.IsEnabled = CopyBtn.IsEnabled = ok;
                RetryBtn.IsEnabled = _last != null;
                if (ok && !Instruction.IsKeyboardFocused) Instruction.Focus();
            }
        }
    }

    /// <summary>Strips wrappers small models sometimes add despite instructions (code fences, surrounding quotes).</summary>
    static string Clean(string s)
    {
        s = s.Trim();
        if (s.StartsWith("```"))
        {
            int nl = s.IndexOf('\n');
            if (nl > 0) s = s[(nl + 1)..];
            if (s.EndsWith("```")) s = s[..^3];
            s = s.Trim();
        }
        if (s.Length > 1 && ((s[0] == '"' && s[^1] == '"') || (s[0] == '“' && s[^1] == '”')) && s.IndexOf('"', 1) == s.Length - 1) s = s[1..^1];
        return s;
    }

    void ResetResult()
    {
        Result.Text = "";
        ErrorTxt.Visibility = Visibility.Collapsed;
        Result.Visibility = Visibility.Visible;
        ApplyBtn.IsEnabled = CopyBtn.IsEnabled = false;
        RetryBtn.IsEnabled = false;
        SetBusy(false, "");
    }

    void ShowError(string msg)
    {
        Result.Visibility = Visibility.Collapsed;
        ErrorTxt.Text = msg;
        ErrorTxt.Visibility = Visibility.Visible;
        SetBusy(false, "Hata");
    }

    void SetBusy(bool busy, string status)
    {
        _busy = busy;
        Status.Text = status;
        BusySpark.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy)
            SparkRot.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever });
        else SparkRot.BeginAnimation(RotateTransform.AngleProperty, null);
    }

    void Retry_Click(object sender, RoutedEventArgs e) { if (_last is { } l) _ = Run(l.Instruction, l.Label); }

    void Copy_Click(object sender, RoutedEventArgs e) => CopyResult(close: false);

    void CopyResult(bool close)
    {
        var text = Result.Text;
        if (text.Length == 0) return;
        try { WF.Clipboard.SetDataObject(text, true, 5, 40); } catch { }
        if (close) HidePopup();
        else Status.Text = "Panoya kopyalandı";
    }

    void Apply_Click(object sender, RoutedEventArgs e) => _ = Apply();

    async Task Apply()
    {
        var text = Result.Text;
        if (text.Length == 0 || _busy) return;
        var target = _target;
        if (target == IntPtr.Zero || !Native.IsWindow(target)) { CopyResult(close: true); return; }

        var backup = _clipBackup ?? BackupClipboard();
        try { WF.Clipboard.SetDataObject(text, true, 5, 40); }
        catch { ShowError("Pano şu an meşgul, tekrar dene."); return; }
        HidePopup();
        Native.SetForegroundWindow(target);
        await Task.Delay(90);
        Native.SendPaste();
        await Task.Delay(600);
        RestoreClipboard(backup);   // leave the user's clipboard as it was
    }

    void Close_Click(object sender, RoutedEventArgs e) => HidePopup();

    // ================= Keyboard =================
    void OnKey(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape)
        {
            if (SettingsView.Visibility == Visibility.Visible && _settings.ApiKey != null) ShowMain();
            else if (_busy) _cts?.Cancel();
            else HidePopup();
            e.Handled = true;
            return;
        }
        if (SettingsView.Visibility == Visibility.Visible) return;

        if (ctrl && key >= Key.D1 && key <= Key.D9) { RunAction(key - Key.D1); e.Handled = true; }
        else if (ctrl && key >= Key.NumPad1 && key <= Key.NumPad9) { RunAction(key - Key.NumPad1); e.Handled = true; }
        else if (ctrl && key == Key.Enter) { _ = Apply(); e.Handled = true; }
        else if (ctrl && key == Key.R) { Retry_Click(this, new RoutedEventArgs()); e.Handled = true; }
        else if (ctrl && shift && key == Key.C) { CopyResult(close: false); e.Handled = true; }
    }

    // ================= Settings =================
    void OpenSettings_Click(object sender, RoutedEventArgs e) => ShowSettings();
    void CloseSettings_Click(object sender, RoutedEventArgs e) { if (_settings.ApiKey != null) ShowMain(); }

    void ShowSettings()
    {
        MainView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Visible;
        bool hasKey = _settings.ApiKey != null;
        Welcome.Visibility = hasKey ? Visibility.Collapsed : Visibility.Visible;
        SettingsBack.Visibility = hasKey ? Visibility.Visible : Visibility.Collapsed;
        KeyBox.Password = "";
        KeyNote.Text = _settings.ProtectedKey != null
            ? $"Kayıtlı bir anahtar var (…{Tail(_settings.ApiKey)}). Değiştirmek için yenisini yapıştır."
            : _settings.KeyFromEnvironment ? "GEMINI_API_KEY ortam değişkenindeki anahtar kullanılıyor."
            : "Google AI Studio'dan Google hesabınla ücretsiz alabilirsin. Kopyaladığın anahtarı olduğu gibi yapıştır.";
        ModelBox.Text = _settings.Model;
        TestBox.Visibility = Visibility.Collapsed;
        HotkeyInfo.Text = _hotkeyText.Length > 0
            ? $"{_hotkeyText} — herhangi bir uygulamada metin seçip bu tuşlara bas. Kalem sistem tepsisinde çalışır."
            : "Kısayol kaydedilemedi (başka bir program kullanıyor). Kalem'i tepsi simgesinden açabilirsin.";
        KeyBox.Focus();
    }

    static string Tail(string? k) => k is { Length: > 4 } ? k[^4..] : "";

    void ShowMain()
    {
        SettingsView.Visibility = Visibility.Collapsed;
        MainView.Visibility = Visibility.Visible;
        Instruction.Focus();
    }

    void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        if (KeyBox.Password.Trim().Length > 0) _settings.SetApiKey(KeyBox.Password);
        var model = ModelBox.Text.Trim();
        _settings.Model = model.Length > 0 ? model : Gemini.DefaultModel;
        if (_settings.ApiKey == null) { ShowTest("Kaydetmek için bir API anahtarı gerekli.", error: true); return; }
        try { _settings.Save(); }
        catch (Exception ex) { ShowTest("Ayarlar kaydedilemedi: " + ex.Message, error: true); return; }
        UpdateModelTag();
        ShowMain();
    }

    string? KeyForTest() => KeyBox.Password.Trim().Length > 0 ? KeyBox.Password.Trim() : _settings.ApiKey;

    async void TestKey_Click(object sender, RoutedEventArgs e)
    {
        var key = KeyForTest();
        if (key == null) { ShowTest("Önce API anahtarını yapıştır.", error: true); return; }
        var model = ModelBox.Text.Trim().Length > 0 ? ModelBox.Text.Trim() : Gemini.DefaultModel;
        ShowTest("Deneniyor…", error: false);
        TestBtn.IsEnabled = false;
        try
        {
            var models = await Gemini.ListModelsAsync(key, CancellationToken.None);
            if (models.Contains(model)) ShowTest($"✓ Bağlantı başarılı. {model} kullanılabilir.", error: false);
            else
            {
                var best = Gemini.NewestFlashLite(models);
                ShowTest($"Bağlantı başarılı ama “{model}” bu anahtarla kullanılamıyor." + (best != null ? $" Önerilen: {best} (En yeniyi bul)." : ""), error: true);
            }
        }
        catch (GeminiException ex) { ShowTest(ex.Message, error: true); }
        catch (Exception ex) { ShowTest("Hata: " + ex.Message, error: true); }
        finally { TestBtn.IsEnabled = true; }
    }

    async void FindModel_Click(object sender, RoutedEventArgs e)
    {
        var key = KeyForTest();
        if (key == null) { ShowTest("Model listesini almak için önce API anahtarını yapıştır.", error: true); return; }
        ShowTest("Modeller alınıyor…", error: false);
        try
        {
            var best = Gemini.NewestFlashLite(await Gemini.ListModelsAsync(key, CancellationToken.None));
            if (best == null) ShowTest("Bu anahtarla erişilebilen bir Flash-Lite modeli bulunamadı.", error: true);
            else { ModelBox.Text = best; ShowTest($"En yeni Flash-Lite: {best}. Kaydet'e basmayı unutma.", error: false); }
        }
        catch (GeminiException ex) { ShowTest(ex.Message, error: true); }
        catch (Exception ex) { ShowTest("Hata: " + ex.Message, error: true); }
    }

    void ShowTest(string msg, bool error)
    {
        TestBox.Visibility = Visibility.Visible;
        TestTxt.Text = msg;
        TestTxt.Foreground = (Brush)FindResource(error ? "DangerBrush" : "FgBrush");
    }

    void GetKey_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://aistudio.google.com/apikey") { UseShellExecute = true }); } catch { }
    }

    void UpdateModelTag() => ModelTag.Text = _settings.Model;

    // ================= Tray =================
    void CreateTray()
    {
        var res = Application.GetResourceStream(new Uri("pack://application:,,,/app.ico"));
        var menu = new WF.ContextMenuStrip
        {
            Renderer = new DarkMenuRenderer(), ShowImageMargin = false, ShowCheckMargin = true,
            Font = new SD.Font("Segoe UI", 9.5f), Padding = new WF.Padding(2, 4, 2, 4),
        };
        var open = new WF.ToolStripMenuItem("Kalem'i aç", null, (_, _) => OpenFromTray())
        { ShortcutKeyDisplayString = _hotkeyText, Font = new SD.Font("Segoe UI", 9.5f, SD.FontStyle.Bold) };
        var settings = new WF.ToolStripMenuItem("Ayarlar", null, (_, _) => { OpenFromTray(); ShowSettings(); });
        var auto = new WF.ToolStripMenuItem("Windows ile başlat") { Checked = IsAutoStart(), CheckOnClick = true };
        auto.CheckedChanged += (_, _) => SetAutoStart(auto.Checked);
        var exit = new WF.ToolStripMenuItem("Çıkış", null, (_, _) => ExitApp());
        menu.Items.AddRange(new WF.ToolStripItem[] { open, settings, new WF.ToolStripSeparator(), auto, new WF.ToolStripSeparator(), exit });

        _tray = new WF.NotifyIcon
        {
            Icon = new SD.Icon(res!.Stream, WF.SystemInformation.SmallIconSize),
            Text = "Kalem — yapay zekâ yazım asistanı",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseClick += (_, e) => { if (e.Button == WF.MouseButtons.Left) OpenFromTray(); };
    }

    static bool IsAutoStart()
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue("Kalem") is string; }
        catch { return false; }
    }

    static void SetAutoStart(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) k.SetValue("Kalem", $"\"{Environment.ProcessPath}\" --hidden");
            else k.DeleteValue("Kalem", false);
        }
        catch (Exception ex) { App.Log(ex); }
    }

    void ExitApp()
    {
        _cts?.Cancel();
        Native.UnregisterHotKey(_hwnd, 1);
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        Application.Current.Shutdown();
    }
}

sealed class DarkMenuRenderer : WF.ToolStripProfessionalRenderer
{
    public DarkMenuRenderer() : base(new DarkColors()) { RoundedEdges = false; }

    protected override void OnRenderItemText(WF.ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? SD.Color.FromArgb(238, 237, 247) : SD.Color.FromArgb(120, 116, 145);
        if (e.Text == (e.Item as WF.ToolStripMenuItem)?.ShortcutKeyDisplayString) e.TextColor = SD.Color.FromArgb(152, 147, 178);
        base.OnRenderItemText(e);
    }

    protected override void OnRenderItemCheck(WF.ToolStripItemImageRenderEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
        var r = e.ImageRectangle;
        using var pen = new SD.Pen(SD.Color.FromArgb(167, 139, 250), 2f);
        g.DrawLines(pen, new[]
        {
            new SD.PointF(r.Left + r.Width * 0.2f, r.Top + r.Height * 0.52f),
            new SD.PointF(r.Left + r.Width * 0.42f, r.Top + r.Height * 0.74f),
            new SD.PointF(r.Left + r.Width * 0.82f, r.Top + r.Height * 0.28f),
        });
    }

    sealed class DarkColors : WF.ProfessionalColorTable
    {
        static readonly SD.Color Bg = SD.Color.FromArgb(26, 25, 40);
        static readonly SD.Color Sel = SD.Color.FromArgb(42, 35, 80);
        static readonly SD.Color Line = SD.Color.FromArgb(42, 40, 64);
        public override SD.Color ToolStripDropDownBackground => Bg;
        public override SD.Color ImageMarginGradientBegin => Bg;
        public override SD.Color ImageMarginGradientMiddle => Bg;
        public override SD.Color ImageMarginGradientEnd => Bg;
        public override SD.Color MenuBorder => Line;
        public override SD.Color MenuItemBorder => Sel;
        public override SD.Color MenuItemSelected => Sel;
        public override SD.Color SeparatorDark => Line;
        public override SD.Color SeparatorLight => Line;
        public override SD.Color CheckBackground => Bg;
        public override SD.Color CheckSelectedBackground => Sel;
        public override SD.Color CheckPressedBackground => Sel;
    }
}
