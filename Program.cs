using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using WinTimer = System.Windows.Forms.Timer;

namespace DotaMmrTracker;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        
        // Создаем главную форму приложения
        MainForm mainForm = new MainForm();
        
        // Проверяем наличие файла иконки рядом с исполняемым файлом и устанавливаем её для окна
        if (System.IO.File.Exists("icon.ico"))
        {
            mainForm.Icon = new System.Drawing.Icon("icon.ico");
        }
        
        Application.Run(mainForm);
    }
}

// ───────────────────────────── Модели ─────────────────────────────

public class Settings
{
    public string Username { get; set; } = "";
    public string PersonaName { get; set; } = "";
    public string TokenEnc { get; set; } = "";
    public uint AccountId { get; set; }
    public bool AutoSync { get; set; } = true;
    public string AccentHex { get; set; } = "";
}

public class MatchEntry
{
    public long MatchId { get; set; }
    public DateTime StartUtc { get; set; }
    public int HeroId { get; set; }
    public bool Won { get; set; }
    public int GameMode { get; set; }
    public int DurationSec { get; set; }
    public int? PrevRating { get; set; }
    public int RankChange { get; set; }
    public int? Rating { get; set; }

    public int? Kills { get; set; }
    public int? Deaths { get; set; }
    public int? Assists { get; set; }
    public int? Gpm { get; set; }
    public int? Xpm { get; set; }
    public int? LastHits { get; set; }
    public int? HeroDamage { get; set; }
    public int? PartySize { get; set; }
    /// leaver_status из OpenDota: 0 — играл до конца, 1 — отключался, ≥2 — покинул игру / AFK.
    public int? LeaverStatus { get; set; }

    [JsonIgnore] public bool HasStats => Kills != null;
    [JsonIgnore] public bool Left => LeaverStatus >= 2;
    [JsonIgnore] public bool NeedsStats => Kills == null || LeaverStatus == null;
    [JsonIgnore] public int? Delta { get; set; }
    [JsonIgnore] public string Note { get; set; } = "";
}

public class DataFile
{
    public List<MatchEntry> Matches { get; set; } = new();
}

public class HeroInfo
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Localized { get; set; } = "";
    [JsonPropertyName("primary_attr")]
    public string PrimaryAttr { get; set; } = ""; // agi, str, int, all
    public List<string> Roles { get; set; } = new();
}

// ───────────────────────────── Нативные вызовы ─────────────────────────────

static class Native
{
    [DllImport("user32.dll")] public static extern bool ReleaseCapture();
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public const int WM_NCLBUTTONDOWN = 0x00A1;
    public const int HTCAPTION = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct NCCALCSIZE_PARAMS { public RECT rgrc0, rgrc1, rgrc2; public IntPtr lppos; }

    static int Bgr(Color c) => c.R | (c.G << 8) | (c.B << 16);

    // Тёмная системная рамка (для диалогов) — работает на Windows 10 1809+ / Windows 11
    public static void DarkTitle(IntPtr handle)
    {
        try
        {
            int on = 1;
            DwmSetWindowAttribute(handle, 20, ref on, 4);
            DwmSetWindowAttribute(handle, 19, ref on, 4);
            int cap = Bgr(Theme.Side), txt = Bgr(Theme.Text), brd = Bgr(Theme.Border);
            DwmSetWindowAttribute(handle, 35, ref cap, 4);
            DwmSetWindowAttribute(handle, 36, ref txt, 4);
            DwmSetWindowAttribute(handle, 34, ref brd, 4);
        }
        catch { }
    }
}

// ───────────────────────────── Хранилище ─────────────────────────────

static class Storage
{
    public static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DotaMmrTracker");
    static readonly JsonSerializerOptions Opt = new() { WriteIndented = true };

    public static T Load<T>(string name) where T : new()
    {
        foreach (var file in new[] { name, name + ".bak" })
        {
            try
            {
                string p = Path.Combine(Dir, file);
                if (!File.Exists(p)) continue;
                var v = JsonSerializer.Deserialize<T>(File.ReadAllText(p));
                if (v != null) return v;
            }
            catch { }
        }
        return new T();
    }

    public static void Save<T>(string name, T obj)
    {
        Directory.CreateDirectory(Dir);
        string path = Path.Combine(Dir, name);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(obj, Opt));
        if (File.Exists(path)) File.Copy(path, path + ".bak", true);
        File.Move(tmp, path, true);
    }

    public static void WriteText(string name, string text)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(Path.Combine(Dir, name), text);
    }

    public static string Protect(string s) =>
        string.IsNullOrEmpty(s) ? "" :
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(s), null, DataProtectionScope.CurrentUser));

    public static string Unprotect(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        try
        {
            return Encoding.UTF8.GetString(
                ProtectedData.Unprotect(Convert.FromBase64String(s), null, DataProtectionScope.CurrentUser));
        }
        catch { return ""; }
    }
}

// ───────────────────────────── Сеть ─────────────────────────────

/// Единый HttpClient на всё приложение (создавать клиент на каждый класс незачем).
static class Net
{
    public static readonly HttpClient Http = Create();

    static HttpClient Create()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("DotaMmrTracker/2.4");
        return c;
    }
}

// ───────────────────────────── OpenDota ─────────────────────────────

static class OpenDota
{
    const string Api = "https://api.opendota.com/api";

    public record Stats(long MatchId, int Kills, int Deaths, int Assists, int Duration,
                        int Gpm, int Xpm, int LastHits, int HeroDamage, int PartySize, int LeaverStatus);

    /// Просит OpenDota заново подтянуть последние матчи игрока из Steam. Возвращает true, если запрос принят.
    public static async Task<bool> RefreshAsync(uint accountId)
    {
        try
        {
            using var r = await Net.Http.PostAsync($"{Api}/players/{accountId}/refresh", null);
            return r.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// Последние матчи игрока со статистикой. null — запрос не удался (сеть/лимиты/закрытый профиль).
    public static async Task<List<Stats>?> RecentAsync(uint accountId, int limit = 40)
    {
        try
        {
            string[] fields =
            {
                "match_id", "kills", "deaths", "assists", "duration",
                "gold_per_min", "xp_per_min", "last_hits", "hero_damage", "party_size", "leaver_status"
            };
            string project = string.Join("&", fields.Select(f => "project=" + f));
            string json = await Net.Http.GetStringAsync($"{Api}/players/{accountId}/matches?limit={limit}&{project}");

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            var list = new List<Stats>();
            foreach (var m in doc.RootElement.EnumerateArray())
            {
                int G(string n) => m.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
                if (!m.TryGetProperty("match_id", out var idEl)) continue;
                list.Add(new Stats(idEl.GetInt64(), G("kills"), G("deaths"), G("assists"), G("duration"),
                                   G("gold_per_min"), G("xp_per_min"), G("last_hits"), G("hero_damage"), G("party_size"), G("leaver_status")));
            }
            return list;
        }
        catch { return null; }
    }

    public static async Task<List<HeroInfo>> GetHeroesAsync()
    {
        try
        {
            string json = await Net.Http.GetStringAsync($"{Api}/heroes");
            using var doc = JsonDocument.Parse(json);
            var list = new List<HeroInfo>();
            foreach (var h in doc.RootElement.EnumerateArray())
            {
                list.Add(new HeroInfo
                {
                    Id = h.GetProperty("id").GetInt32(),
                    Name = h.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    Localized = h.TryGetProperty("localized_name", out var ln) ? ln.GetString() ?? "" : "",
                    PrimaryAttr = h.TryGetProperty("primary_attr", out var pa) ? pa.GetString() ?? "" : ""
                });
            }
            return list;
        }
        catch { return new List<HeroInfo>(); }
    }
}

// ───────────────────────────── Герои и иконки ─────────────────────────────

static class HeroData
{
    const string HeroPrefix = "npc_dota_hero_";
    static readonly Dictionary<int, HeroInfo> Info = new();
    static readonly Dictionary<int, Image> Icons = new();
    static readonly HashSet<int> Tried = new();
    static readonly SemaphoreSlim Gate = new(6);
    static string IconDir => Path.Combine(Storage.Dir, "icons");

    public static bool Ready { get; private set; }

    public static string Name(int id) =>
        Info.TryGetValue(id, out var h) && h.Localized != "" ? h.Localized : Heroes.Name(id);

    public static Image? Icon(int id) => Icons.TryGetValue(id, out var i) ? i : null;

    public static async Task InitAsync()
    {
        try
        {
            var list = Storage.Load<List<HeroInfo>>("heroes.json");
            if (list.Count == 0 || list.All(h => h.Name == ""))
            {
                var fresh = await OpenDota.GetHeroesAsync();
                if (fresh.Count > 0)
                {
                    list = fresh;
                    Storage.Save("heroes.json", list);
                }
            }
            foreach (var h in list) Info[h.Id] = h;
        }
        catch { }
        Ready = true;
    }

    /// Имя файла иконки на CDN Valve (например, "antimage"). Берётся из OpenDota, иначе — из локальной таблицы имён.
    static string? Slug(int id)
    {
        if (Info.TryGetValue(id, out var h) && h.Name.StartsWith(HeroPrefix))
            return h.Name[HeroPrefix.Length..];
        string name = Heroes.Name(id);
        if (name.StartsWith("Hero #")) return null;
        return name.ToLowerInvariant().Replace("'", "").Replace("-", "").Replace(" ", "_");
    }

    public static async Task LoadIconsAsync(IEnumerable<int> ids, Action onLoaded)
    {
        var todo = ids.Distinct().Where(id => !Icons.ContainsKey(id) && Tried.Add(id)).ToList();
        if (todo.Count == 0) return;
        Directory.CreateDirectory(IconDir);
        await Task.WhenAll(todo.Select(async id =>
        {
            await Gate.WaitAsync();
            try
            {
                var img = await LoadIconAsync(id);
                if (img != null)
                {
                    Icons[id] = img;
                    onLoaded();
                }
            }
            catch { }
            finally { Gate.Release(); }
        }));
    }

    static async Task<Image?> LoadIconAsync(int id)
    {
        string path = Path.Combine(IconDir, id + ".png");
        byte[]? bytes = null;
        if (File.Exists(path))
        {
            try { bytes = await File.ReadAllBytesAsync(path); } catch { }
        }
        if (bytes == null || bytes.Length == 0)
        {
            string? slug = Slug(id);
            if (slug == null) return null;
            string[] urls =
            {
                $"https://cdn.cloudflare.steamstatic.com/apps/dota2/images/dota_react/heroes/{slug}.png",
                $"https://cdn.akamai.steamstatic.com/apps/dota2/images/dota_react/heroes/{slug}.png",
                $"https://cdn.cloudflare.steamstatic.com/apps/dota2/images/heroes/{slug}_lg.png"
            };
            bytes = null;
            foreach (var url in urls)
            {
                try
                {
                    var b = await Net.Http.GetByteArrayAsync(url);
                    if (b.Length > 0) { bytes = b; break; }
                }
                catch { }
            }
            if (bytes == null) return null;
            try { await File.WriteAllBytesAsync(path, bytes); } catch { }
        }
        try
        {
            using var ms = new MemoryStream(bytes);
            using var src = Image.FromStream(ms);
            return new Bitmap(src);
        }
        catch
        {
            // битый файл в кеше — удаляем, чтобы скачать заново при следующем запуске
            try { File.Delete(path); } catch { }
            return null;
        }
    }
}

// ───────────────────────────── Московское время ─────────────────────────────

/// Всё время в интерфейсе и границы периодов (день/неделя/месяц) считаются по Москве (UTC+3, без перехода на летнее).
static class Msk
{
    static readonly TimeZoneInfo Tz = Find();

    static TimeZoneInfo Find()
    {
        foreach (var id in new[] { "Russian Standard Time", "Europe/Moscow" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch { }
        }
        return TimeZoneInfo.CreateCustomTimeZone("MSK", TimeSpan.FromHours(3), "MSK", "MSK");
    }

    public static DateTime FromUtc(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Tz);

    public static DateTime ToUtc(DateTime msk) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(msk, DateTimeKind.Unspecified), Tz);

    public static DateTime Now => FromUtc(DateTime.UtcNow);
}

// ───────────────────────────── Тема ─────────────────────────────

static class Theme
{
    public static readonly Color Bg = Color.FromArgb(12, 12, 14);
    public static readonly Color Side = Color.FromArgb(18, 18, 20);
    public static readonly Color Card = Color.FromArgb(25, 25, 28);
    public static readonly Color CardHi = Color.FromArgb(37, 37, 42);
    public static readonly Color Border = Color.FromArgb(50, 50, 56);
    public static readonly Color Text = Color.FromArgb(236, 236, 240);
    public static readonly Color Muted = Color.FromArgb(140, 140, 150);
    public static readonly Color DefaultAccent = Color.FromArgb(214, 40, 40);
    /// Акцентный цвет можно менять в «Параметрах»; AccentHi — его осветлённый вариант для наведения.
    public static Color Accent { get; private set; } = DefaultAccent;
    public static Color AccentHi { get; private set; } = Color.FromArgb(244, 72, 72);
    public static void SetAccent(Color c)
    {
        Accent = c;
        AccentHi = Gfx.Lerp(c, Color.White, 0.2f);
    }
    public static readonly Color Good = Color.FromArgb(74, 200, 130);
    public static readonly Color Bad = Color.FromArgb(255, 96, 96);
    public static readonly Color Warn = Color.FromArgb(240, 170, 60);
}

static class Gfx
{
    public static GraphicsPath Round(RectangleF r, float radius)
    {
        float d = Math.Min(radius * 2f, Math.Min(r.Width, r.Height));
        var p = new GraphicsPath();
        if (d <= 0.5f)
        {
            p.AddRectangle(r);
            return p;
        }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static Color Lerp(Color a, Color b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return Color.FromArgb(
            (int)(a.A + (b.A - a.A) * t),
            (int)(a.R + (b.R - a.R) * t),
            (int)(a.G + (b.G - a.G) * t),
            (int)(a.B + (b.B - a.B) * t));
    }

    public static float EaseOut(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        float u = 1f - t;
        return 1f - u * u * u;
    }

    public static float EaseOutBack(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        const float c1 = 1.70158f;
        const float c3 = c1 + 1f;
        float u = t - 1f;
        return 1f + c3 * u * u * u + c1 * u * u;
    }

    public static void DoubleBuffer(Control c)
    {
        typeof(Control).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.SetValue(c, true);
    }
}

// ───────────────────────────── Таблица и тёмный скроллбар ─────────────────────────────

public class DarkScrollableGrid : DataGridView
{
    public DarkScrollableGrid()
    {
        ScrollBars = ScrollBars.None;
    }

    public int VisibleRows => Math.Max(1, DisplayedRowCount(false));
    public int MaxFirstRow => Math.Max(0, RowCount - VisibleRows);
    public int FirstRow => RowCount == 0 ? 0 : Math.Max(0, FirstDisplayedScrollingRowIndex);

    public void ScrollToRow(int row)
    {
        if (RowCount == 0) return;
        int n = Math.Clamp(row, 0, MaxFirstRow);
        if (n == FirstRow) return;
        try { FirstDisplayedScrollingRowIndex = n; } catch { }
    }

    public void ScrollRows(int delta) => ScrollToRow(FirstRow + delta);

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        ScrollRows(e.Delta > 0 ? -3 : 3);
        if (e is HandledMouseEventArgs h) h.Handled = true;
    }

    // Таблица только для просмотра: никаких кликов, выделения, перетаскивания и подсказок.
    protected override void OnMouseDown(MouseEventArgs e) { }
    protected override void OnMouseUp(MouseEventArgs e) { }
    protected override void OnMouseMove(MouseEventArgs e) { }
    protected override void OnMouseClick(MouseEventArgs e) { }
    protected override void OnMouseDoubleClick(MouseEventArgs e) { }
}

public class DarkScrollBar : Control
{
    readonly DarkScrollableGrid grid;
    readonly WinTimer timer = new() { Interval = 50 };
    int lastFirst = -1, lastTotal = -1, lastVis = -1, lastHeader = -1;
    bool hover, drag;
    float dragOffset;

    public DarkScrollBar(DarkScrollableGrid g)
    {
        grid = g;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Width = 14;
        BackColor = Theme.Card;
        timer.Tick += (_, _) =>
        {
            if (lastFirst != grid.FirstRow || lastTotal != grid.RowCount ||
                lastVis != grid.VisibleRows || lastHeader != grid.ColumnHeadersHeight)
                Invalidate();
        };
        timer.Start();
    }

    RectangleF Track => new(0, grid.ColumnHeadersHeight + 4, Width, Math.Max(1, Height - grid.ColumnHeadersHeight - 8));

    RectangleF? Thumb()
    {
        int total = grid.RowCount, vis = grid.VisibleRows, first = grid.FirstRow;
        if (total <= vis) return null;
        var tr = Track;
        float h = Math.Max(32f, tr.Height * vis / total);
        float max = total - vis;
        float y = tr.Y + (tr.Height - h) * Math.Min(first, max) / max;
        return new RectangleF((Width - 6) / 2f, y, 6, h);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        var th = Thumb();
        if (th == null || e.Button != MouseButtons.Left) return;
        if (e.Y >= th.Value.Y && e.Y <= th.Value.Bottom)
        {
            drag = true;
            dragOffset = e.Y - th.Value.Y;
            Capture = true;
        }
        else grid.ScrollRows(e.Y < th.Value.Y ? -grid.VisibleRows : grid.VisibleRows);
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var th = Thumb();
        if (th == null) return;
        if (drag)
        {
            var tr = Track;
            float range = tr.Height - th.Value.Height;
            if (range > 0)
            {
                float ratio = Math.Clamp((e.Y - dragOffset - tr.Y) / range, 0f, 1f);
                grid.ScrollToRow((int)Math.Round(ratio * grid.MaxFirstRow));
            }
        }
        else
        {
            bool h = e.Y >= th.Value.Y && e.Y <= th.Value.Bottom;
            if (h != hover) { hover = h; Invalidate(); }
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        drag = false;
        Capture = false;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (hover) { hover = false; Invalidate(); }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        grid.ScrollRows(e.Delta > 0 ? -3 : 3);
        if (e is HandledMouseEventArgs h) h.Handled = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);
        lastFirst = grid.FirstRow;
        lastTotal = grid.RowCount;
        lastVis = grid.VisibleRows;
        lastHeader = grid.ColumnHeadersHeight;
        var th = Thumb();
        if (th == null) return;
        var tr = Track;
        var trackRect = new RectangleF((Width - 6) / 2f, tr.Y, 6, tr.Height);
        using (var tp = Gfx.Round(trackRect, 3f))
        using (var tb = new SolidBrush(Color.FromArgb(34, 34, 38)))
            g.FillPath(tb, tp);
        Color c = drag ? Theme.AccentHi : hover ? Theme.Accent : Color.FromArgb(78, 78, 88);
        using var path = Gfx.Round(th.Value, 3f);
        using var br = new SolidBrush(c);
        g.FillPath(br, path);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) timer.Dispose();
        base.Dispose(disposing);
    }
}

// ───────────────────────────── Кастомная шапка окна ─────────────────────────────

public class CaptionButton : Control
{
    public enum Kind { Minimize, Maximize, Restore, Close }
    readonly WinTimer timer = new() { Interval = 15 };
    float hover;
    bool hovering;
    Kind glyph;

    public Kind Glyph
    {
        get => glyph;
        set { glyph = value; Invalidate(); }
    }

    public CaptionButton(Kind kind)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, false);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        glyph = kind;
        Width = 46;
        Cursor = Cursors.Default;
        timer.Tick += (_, _) =>
        {
            float ht = hovering ? 1f : 0f;
            if (hover < ht) hover = Math.Min(ht, hover + 0.2f);
            else if (hover > ht) hover = Math.Max(ht, hover - 0.2f);
            Invalidate();
            if (hover == ht) timer.Stop();
        };
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovering = true; timer.Start(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovering = false; timer.Start(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        bool close = glyph == Kind.Close;
        Color hoverColor = close ? Theme.Accent : Theme.CardHi;
        g.Clear(Gfx.Lerp(Theme.Side, hoverColor, hover));
        int cx = Width / 2, cy = Height / 2;
        using var pen = new Pen(Theme.Text, 1f);
        switch (glyph)
        {
            case Kind.Minimize:
                g.DrawLine(pen, cx - 5, cy, cx + 5, cy);
                break;
            case Kind.Maximize:
                g.DrawRectangle(pen, cx - 5, cy - 5, 10, 10);
                break;
            case Kind.Restore:
                g.DrawRectangle(pen, cx - 5, cy - 2, 7, 7);
                g.DrawLine(pen, cx - 2, cy - 5, cx + 5, cy - 5);
                g.DrawLine(pen, cx + 5, cy - 5, cx + 5, cy + 2);
                g.DrawLine(pen, cx - 2, cy - 5, cx - 2, cy - 2);
                g.DrawLine(pen, cx + 2, cy + 2, cx + 5, cy + 2);
                break;
            case Kind.Close:
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.DrawLine(pen, cx - 5, cy - 5, cx + 5, cy + 5);
                g.DrawLine(pen, cx - 5, cy + 5, cx + 5, cy - 5);
                break;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) timer.Dispose();
        base.Dispose(disposing);
    }
}

public class TitleBar : Control
{
    const int WM_NCHITTEST = 0x84;
    const int HTTRANSPARENT = -1;
    public readonly CaptionButton MinBtn = new(CaptionButton.Kind.Minimize) { Dock = DockStyle.Right };
    public readonly CaptionButton MaxBtn = new(CaptionButton.Kind.Maximize) { Dock = DockStyle.Right };
    public readonly CaptionButton CloseBtn = new(CaptionButton.Kind.Close) { Dock = DockStyle.Right };
    public event EventHandler? ToggleMaximize;
    readonly Font titleFont = new("Segoe UI Semibold", 8.5F);

    public TitleBar()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Dock = DockStyle.Top;
        Height = 36;
        Controls.Add(MinBtn);
        Controls.Add(MaxBtn);
        Controls.Add(CloseBtn);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        if (e.Clicks == 2) { ToggleMaximize?.Invoke(this, EventArgs.Empty); return; }
        var form = FindForm();
        if (form == null) return;
        Native.ReleaseCapture();
        Native.SendMessage(form.Handle, Native.WM_NCLBUTTONDOWN, (IntPtr)Native.HTCAPTION, IntPtr.Zero);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_NCHITTEST && FindForm() is { WindowState: FormWindowState.Normal })
        {
            long lp = m.LParam.ToInt64();
            var pt = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
            if (pt.Y < 5)
            {
                m.Result = (IntPtr)HTTRANSPARENT;
                return;
            }
        }
        base.WndProc(ref m);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(Theme.Side);
        using (var line = new Pen(Theme.Border, 1f))
            g.DrawLine(line, 0, Height - 1, Width, Height - 1);
        using (var path = Gfx.Round(new RectangleF(14, Height / 2f - 6, 12, 12), 3f))
        using (var br = new SolidBrush(Theme.Accent))
            g.FillPath(br, path);
        using var tb = new SolidBrush(Theme.Text);
        using var sf = new StringFormat { LineAlignment = StringAlignment.Center };
        g.DrawString("DOTA 2  ·  MMR TRACKER", titleFont, tb, new RectangleF(36, 0, Width - 200, Height), sf);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) titleFont.Dispose();
        base.Dispose(disposing);
    }
}

// ───────────────────────────── Кастомные контролы ─────────────────────────────

public class ToggleSwitch : Control
{
    readonly WinTimer timer = new() { Interval = 15 };
    bool isChecked;
    float pos, hover;
    bool hovering;
    public event EventHandler? CheckedChanged;

    public ToggleSwitch()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Size = new Size(54, 28);
        Cursor = Cursors.Hand;
        BackColor = Theme.Card;
        timer.Tick += (_, _) => Step();
    }

    public bool Checked
    {
        get => isChecked;
        set
        {
            if (isChecked == value) return;
            isChecked = value;
            timer.Start();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void SetCheckedSilently(bool v)
    {
        isChecked = v;
        pos = v ? 1f : 0f;
        Invalidate();
    }

    void Step()
    {
        float target = isChecked ? 1f : 0f;
        const float step = 0.075f;
        if (pos < target) pos = Math.Min(target, pos + step);
        else if (pos > target) pos = Math.Max(target, pos - step);
        float ht = hovering ? 1f : 0f;
        if (hover < ht) hover = Math.Min(ht, hover + 0.12f);
        else if (hover > ht) hover = Math.Max(ht, hover - 0.12f);
        Invalidate();
        if (pos == target && hover == ht) timer.Stop();
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovering = true; timer.Start(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovering = false; timer.Start(); }
    protected override void OnClick(EventArgs e) { base.OnClick(e); Checked = !isChecked; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);
        float t = pos;
        var track = new RectangleF(1, 1, Width - 3, Height - 3);
        using (var tp = Gfx.Round(track, track.Height / 2f))
        {
            Color off = Gfx.Lerp(Color.FromArgb(52, 52, 58), Color.FromArgb(68, 68, 76), hover);
            Color on = Gfx.Lerp(Theme.Accent, Theme.AccentHi, hover);
            using var br = new SolidBrush(Gfx.Lerp(off, on, t));
            g.FillPath(br, tp);
        }
        float d = Height - 9f;
        float stretch = 7f * (float)Math.Sin(Math.PI * t);
        float w = d + stretch;
        float eased = isChecked ? Gfx.EaseOutBack(t) : 1f - Gfx.EaseOutBack(1f - t);
        float left = 4.5f, right = Width - 4.5f - d;
        float cx = left + d / 2f + (right - left) * eased;
        float x = Math.Clamp(cx - w / 2f, 3f, Width - 3f - w);
        var thumb = new RectangleF(x, (Height - d) / 2f, w, d);
        using (var tpth = Gfx.Round(thumb, d / 2f))
        using (var tb = new SolidBrush(Gfx.Lerp(Color.FromArgb(190, 190, 200), Color.White, t)))
            g.FillPath(tb, tpth);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) timer.Dispose();
        base.Dispose(disposing);
    }
}

public class FancyButton : Button
{
    readonly WinTimer timer = new() { Interval = 15 };
    float hover;
    bool hovering, pressed;
    public bool Primary { get; set; }

    public FancyButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
        Font = new Font("Segoe UI Semibold", 9.5F);
        Height = 38;
        timer.Tick += (_, _) => Step();
    }

    void Step()
    {
        float ht = hovering ? 1f : 0f;
        if (hover < ht) hover = Math.Min(ht, hover + 0.14f);
        else if (hover > ht) hover = Math.Max(ht, hover - 0.14f);
        Invalidate();
        if (hover == ht) timer.Stop();
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovering = true; timer.Start(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovering = false; pressed = false; timer.Start(); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); pressed = true; Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); pressed = false; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        var rect = new RectangleF(0.5f, 0.5f, Width - 2, Height - 2);
        if (pressed && Enabled) rect.Inflate(-1.5f, -1.5f);
        Color textColor = Theme.Text;
        using (var path = Gfx.Round(rect, 10))
        {
            if (!Enabled)
            {
                using var br = new SolidBrush(Theme.Card);
                g.FillPath(br, path);
                textColor = Theme.Muted;
            }
            else if (Primary)
            {
                using var br = new SolidBrush(Gfx.Lerp(Theme.Accent, Theme.AccentHi, hover));
                g.FillPath(br, path);
            }
            else
            {
                using var br = new SolidBrush(Gfx.Lerp(Theme.Card, Theme.CardHi, hover));
                g.FillPath(br, path);
                using var pen = new Pen(Gfx.Lerp(Theme.Border, Theme.Accent, hover), 1f);
                g.DrawPath(pen, path);
            }
        }
        using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using var tb = new SolidBrush(textColor);
        g.DrawString(Text, Font, tb, new RectangleF(0, 0, Width, Height), sf);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) timer.Dispose();
        base.Dispose(disposing);
    }
}

public class RoundedPanel : Panel
{
    public int Radius { get; set; } = 12;

    public RoundedPanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Card;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        var rect = new RectangleF(0.5f, 0.5f, Width - 2, Height - 2);
        using var path = Gfx.Round(rect, Radius);
        using var fill = new SolidBrush(BackColor);
        g.FillPath(fill, path);
        using var pen = new Pen(Theme.Border, 1f);
        g.DrawPath(pen, path);
    }
}

public class AvatarBox : Control
{
    readonly Font letterFont = new("Segoe UI Semibold", 14F);
    Image? image;

    public AvatarBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    public Image? Image
    {
        get => image;
        set
        {
            if (ReferenceEquals(image, value)) return;
            image?.Dispose();
            image = value;
            Invalidate();
        }
    }

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        float s = Math.Min(Width, Height);
        using (var pen = new Pen(Theme.Accent, 2.5f))
            g.DrawEllipse(pen, new RectangleF(1.5f, 1.5f, s - 3f, s - 3f));
        var inner = new RectangleF(5f, 5f, s - 10f, s - 10f);
        using var path = new GraphicsPath();
        path.AddEllipse(inner);
        if (image != null)
        {
            g.SetClip(path);
            g.DrawImage(image, inner);
            g.ResetClip();
        }
        else
        {
            using var fill = new SolidBrush(Theme.CardHi);
            g.FillPath(fill, path);
            string ch = string.IsNullOrEmpty(Text) ? "?" : Text.Substring(0, 1).ToUpperInvariant();
            using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using var tb = new SolidBrush(Theme.Text);
            g.DrawString(ch, letterFont, tb, inner, sf);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            image?.Dispose();
            letterFont.Dispose();
        }
        base.Dispose(disposing);
    }
}

public class StatusPill : Control
{
    readonly WinTimer timer = new() { Interval = 30 };
    float phase;
    bool active;

    public StatusPill()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Font = new Font("Segoe UI Semibold", 9F);
        timer.Tick += (_, _) => { phase = (phase + 0.025f) % 1f; Invalidate(); };
    }

    public bool Active
    {
        get => active;
        set
        {
            if (active == value) return;
            active = value;
            if (active) timer.Start(); else timer.Stop();
            Invalidate();
        }
    }

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Side);
        Color c = active ? Theme.Good : Color.FromArgb(110, 110, 120);
        var rect = new RectangleF(0.5f, 0.5f, Width - 2, Height - 2);
        using (var path = Gfx.Round(rect, rect.Height / 2f))
        {
            using var fill = new SolidBrush(Gfx.Lerp(Theme.Card, c, active ? 0.14f : 0f));
            g.FillPath(fill, path);
            using var pen = new Pen(Gfx.Lerp(Theme.Border, c, active ? 0.55f : 0f), 1f);
            g.DrawPath(pen, path);
        }
        float cy = Height / 2f;
        const float cx = 18f;
        if (active)
        {
            float r = 4f + 9f * phase;
            int alpha = (int)(150 * (1f - phase));
            using var ring = new SolidBrush(Color.FromArgb(Math.Clamp(alpha, 0, 255), c));
            g.FillEllipse(ring, cx - r, cy - r, r * 2, r * 2);
        }
        using (var dot = new SolidBrush(c))
            g.FillEllipse(dot, cx - 4f, cy - 4f, 8f, 8f);
        using var sf = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        using var tb = new SolidBrush(active ? Theme.Text : Theme.Muted);
        g.DrawString(Text, Font, tb, new RectangleF(34, 0, Width - 40, Height), sf);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) timer.Dispose();
        base.Dispose(disposing);
    }
}

public class StatCard : Control
{
    readonly WinTimer timer = new() { Interval = 15 };
    readonly Font titleFont = new("Segoe UI Semibold", 8F);
    readonly Font valueFont = new("Segoe UI Semibold", 22F);
    readonly Font subFont = new("Segoe UI", 8.5F);
    Func<double, string> fmt = d => Math.Round(d).ToString("0");
    string? staticText = "—";
    Color valueColor = Theme.Muted;
    double vStart, vEnd, vCur;
    float barStart, barEnd, barCur;
    bool hasBar;
    float t = 1f;
    float hover;
    bool hovering;

    public string Title { get; set; } = "";
    public string Sub { get; set; } = "";
    public Color AccentColor { get; set; } = Theme.Accent;

    public StatCard()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        timer.Tick += (_, _) => Step();
    }

    public void SetText(string text, Color color, string sub)
    {
        staticText = text;
        valueColor = color;
        Sub = sub;
        hasBar = false;
        Invalidate();
    }

    public void SetValue(double target, Func<double, string> format, Color color, string sub, double? bar = null)
    {
        vStart = staticText == null ? vCur : 0;
        staticText = null;
        vEnd = target;
        fmt = format;
        valueColor = color;
        Sub = sub;
        if (bar.HasValue)
        {
            barStart = hasBar ? barCur : 0f;
            barEnd = (float)bar.Value;
            hasBar = true;
        }
        else hasBar = false;
        t = 0f;
        timer.Start();
    }

    void Step()
    {
        if (t < 1f)
        {
            t = Math.Min(1f, t + 0.045f);
            float e = Gfx.EaseOut(t);
            vCur = vStart + (vEnd - vStart) * e;
            if (hasBar) barCur = barStart + (barEnd - barStart) * e;
        }
        float ht = hovering ? 1f : 0f;
        if (hover < ht) hover = Math.Min(ht, hover + 0.12f);
        else if (hover > ht) hover = Math.Max(ht, hover - 0.12f);
        Invalidate();
        if (t >= 1f && hover == ht) timer.Stop();
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovering = true; timer.Start(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovering = false; timer.Start(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        var rect = new RectangleF(0.5f, 0.5f, Width - 2, Height - 2);
        using (var path = Gfx.Round(rect, 12))
        {
            using var fill = new SolidBrush(Gfx.Lerp(Theme.Card, Theme.CardHi, hover));
            g.FillPath(fill, path);
            using var pen = new Pen(Gfx.Lerp(Theme.Border, AccentColor, hover), 1f);
            g.DrawPath(pen, path);
        }
        using (var tabPath = Gfx.Round(new RectangleF(16, 1, 28, 3), 1.5f))
        using (var tabBr = new SolidBrush(AccentColor))
            g.FillPath(tabBr, tabPath);
        using var muted = new SolidBrush(Theme.Muted);
        g.DrawString(Title, titleFont, muted, 16, 14);
        string val = staticText ?? fmt(vCur);
        using (var vb = new SolidBrush(valueColor))
            g.DrawString(val, valueFont, vb, 13, 30);
        g.DrawString(Sub, subFont, muted, 16, 68);
        if (hasBar)
        {
            var track = new RectangleF(16, Height - 16, Width - 32, 5);
            using (var tp = Gfx.Round(track, 2.5f))
            using (var tb = new SolidBrush(Theme.Border))
                g.FillPath(tb, tp);
            float fw = Math.Clamp(barCur, 0f, 1f) * track.Width;
            if (fw > 1.5f)
            {
                var fr = new RectangleF(track.X, track.Y, fw, track.Height);
                using var fp = Gfx.Round(fr, 2.5f);
                using var fb = new SolidBrush(valueColor);
                g.FillPath(fb, fp);
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            timer.Dispose();
            titleFont.Dispose();
            valueFont.Dispose();
            subFont.Dispose();
        }
        base.Dispose(disposing);
    }
}

public class MenuButton : Control
{
    readonly WinTimer timer = new() { Interval = 15 };
    float hover;
    bool hovering, open;

    public MenuButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Size = new Size(40, 40);
        Cursor = Cursors.Hand;
        timer.Tick += (_, _) => Step();
    }

    public bool Open
    {
        get => open;
        set { if (open == value) return; open = value; Invalidate(); }
    }

    void Step()
    {
        float ht = hovering ? 1f : 0f;
        if (hover < ht) hover = Math.Min(ht, hover + 0.14f);
        else if (hover > ht) hover = Math.Max(ht, hover - 0.14f);
        Invalidate();
        if (hover == ht) timer.Stop();
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovering = true; timer.Start(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovering = false; timer.Start(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        float h = open ? 1f : hover;
        var rect = new RectangleF(0.5f, 0.5f, Width - 2, Height - 2);
        using (var path = Gfx.Round(rect, 10))
        {
            using var fill = new SolidBrush(Gfx.Lerp(Theme.Card, Theme.CardHi, h));
            g.FillPath(fill, path);
            using var pen = new Pen(Gfx.Lerp(Theme.Border, Theme.Accent, h), 1f);
            g.DrawPath(pen, path);
        }
        // три горизонтальные полоски
        const float bw = 18f, bh = 3f, gap = 4f;
        float x = (Width - bw) / 2f;
        float y = (Height - (bh * 3 + gap * 2)) / 2f;
        using var br = new SolidBrush(Theme.Accent);
        for (int i = 0; i < 3; i++)
        {
            using var bar = Gfx.Round(new RectangleF(x, y + i * (bh + gap), bw, bh), 1.5f);
            g.FillPath(br, bar);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) timer.Dispose();
        base.Dispose(disposing);
    }
}

/// Выпадающее меню выбора экрана (история матчей / статистика игр).
public class ViewMenu : Control
{
    const int ItemH = 38, Pad = 6;
    readonly string[] items;
    readonly Font font = new("Segoe UI Semibold", 10F);
    int selected, hoverIdx = -1;
    public event Action<int>? ItemChosen;

    public ViewMenu(params string[] items)
    {
        this.items = items;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Size = new Size(230, items.Length * ItemH + Pad * 2);
        Cursor = Cursors.Hand;
        Visible = false;
    }

    public int SelectedIndex
    {
        get => selected;
        set { selected = value; Invalidate(); }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        using var p = Gfx.Round(new RectangleF(0, 0, Width, Height), 10);
        Region = new Region(p);
    }

    int HitIndex(Point pt)
    {
        if (pt.X < Pad || pt.X > Width - Pad) return -1;
        int i = (pt.Y - Pad) / ItemH;
        return pt.Y >= Pad && i >= 0 && i < items.Length ? i : -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int i = HitIndex(e.Location);
        if (i != hoverIdx) { hoverIdx = i; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        hoverIdx = -1;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        int i = HitIndex(e.Location);
        if (i >= 0) ItemChosen?.Invoke(i);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        g.Clear(Theme.Card);
        using (var path = Gfx.Round(new RectangleF(0.5f, 0.5f, Width - 2, Height - 2), 10))
        using (var pen = new Pen(Theme.Border, 1f))
            g.DrawPath(pen, path);
        using var sf = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        for (int i = 0; i < items.Length; i++)
        {
            var r = new RectangleF(Pad, Pad + i * ItemH, Width - Pad * 2, ItemH);
            bool sel = i == selected;
            if (sel || i == hoverIdx)
            {
                using var p = Gfx.Round(r, 8);
                using var fill = new SolidBrush(sel ? Theme.CardHi : Color.FromArgb(30, 30, 34));
                g.FillPath(fill, p);
            }
            if (sel)
            {
                using var bar = Gfx.Round(new RectangleF(r.X + 8, r.Y + (ItemH - 16) / 2f, 3, 16), 1.5f);
                using var ab = new SolidBrush(Theme.Accent);
                g.FillPath(ab, bar);
            }
            using var tb = new SolidBrush(sel || i == hoverIdx ? Theme.Text : Theme.Muted);
            g.DrawString(items[i], font, tb, new RectangleF(r.X + 22, r.Y, r.Width - 28, r.Height), sf);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) font.Dispose();
        base.Dispose(disposing);
    }
}

/// Крупная плитка для экрана «Статистика игр». Если задан герой — слева рисуется его иконка.
public class StatTile : Control
{
    readonly Font titleFont = new("Segoe UI Semibold", 9F);
    readonly Font subFont = new("Segoe UI", 10F);
    string value = "—";
    Color valueColor = Theme.Muted;
    string sub = "";
    int heroId;

    public string Title { get; set; } = "";

    public StatTile()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    public void Set(string v, Color color, string subText, int hero = 0)
    {
        value = v;
        valueColor = color;
        sub = subText;
        heroId = hero;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        var rect = new RectangleF(0.5f, 0.5f, Width - 2, Height - 2);
        using (var path = Gfx.Round(rect, 12))
        {
            using var fill = new SolidBrush(Theme.Card);
            g.FillPath(fill, path);
            using var pen = new Pen(Theme.Border, 1f);
            g.DrawPath(pen, path);
        }
        using (var tabPath = Gfx.Round(new RectangleF(20, 1, 28, 3), 1.5f))
        using (var tabBr = new SolidBrush(Theme.Accent))
            g.FillPath(tabBr, tabPath);
        using var muted = new SolidBrush(Theme.Muted);
        g.DrawString(Title, titleFont, muted, 20, 18);

        float left = 20f, areaTop = 44f, areaH = Height - areaTop - 12f;
        if (heroId != 0)
        {
            const float iw = 112f, ih = 63f;
            var ir = new RectangleF(left, areaTop + (areaH - ih) / 2f, iw, ih);
            using var ip = Gfx.Round(ir, 8);
            var img = HeroData.Icon(heroId);
            if (img != null)
            {
                var st = g.Save();
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.SetClip(ip, CombineMode.Intersect);
                g.DrawImage(img, ir);
                g.Restore(st);
            }
            else
            {
                using var ph = new SolidBrush(Theme.CardHi);
                g.FillPath(ph, ip);
            }
            using var ipen = new Pen(Color.FromArgb(90, Theme.Border), 1f);
            g.DrawPath(ipen, ip);
            left += iw + 18f;
        }

        float maxW = Math.Max(40f, Width - left - 16f);
        Font vf = null!;
        SizeF vs = default;
        for (float sz = 30f; sz >= 14f; sz -= 2f)
        {
            vf?.Dispose();
            vf = new Font("Segoe UI Semibold", sz);
            vs = g.MeasureString(value, vf);
            if (vs.Width <= maxW) break;
        }
        using (vf)
        {
            var subSize = g.MeasureString(sub, subFont, (int)maxW);
            float y = areaTop + (areaH - (vs.Height + 4f + subSize.Height)) / 2f;
            using (var vb = new SolidBrush(valueColor))
                g.DrawString(value, vf, vb, left - 3f, y);
            g.DrawString(sub, subFont, muted, new RectangleF(left, y + vs.Height + 4f, maxW, subSize.Height + 2f));
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            titleFont.Dispose();
            subFont.Dispose();
        }
        base.Dispose(disposing);
    }
}

public class ColorSwatch : Control
{
    bool hover;
    public Color Swatch { get; set; }
    public bool Selected { get; set; }
    /// Кнопка «свой цвет»: пока не выбрана — рисует «+», после выбора — сам цвет.
    public bool Custom { get; set; }

    public ColorSwatch()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Size = new Size(30, 30);
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Side);
        var outer = new RectangleF(1, 1, Width - 3, Height - 3);
        if (Selected)
        {
            using var ring = new Pen(Theme.Text, 2f);
            g.DrawEllipse(ring, outer);
        }
        else if (hover)
        {
            using var ring = new Pen(Theme.Muted, 1.5f);
            g.DrawEllipse(ring, outer);
        }
        var inner = RectangleF.Inflate(outer, -4f, -4f);
        bool plus = Custom && !Selected;
        using (var fill = new SolidBrush(plus ? Theme.CardHi : Swatch))
            g.FillEllipse(fill, inner);
        if (plus)
        {
            float cx = Width / 2f, cy = Height / 2f;
            using var pen = new Pen(Theme.Muted, 1.5f);
            g.DrawLine(pen, cx - 4, cy, cx + 4, cy);
            g.DrawLine(pen, cx, cy - 4, cx, cy + 4);
        }
    }
}

// ───────────────────────────── Выбор цвета ─────────────────────────────

static class Hsv
{
    public static Color ToColor(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        double r, g, b;
        switch ((int)(h / 60) % 6)
        {
            case 0: (r, g, b) = (c, x, 0); break;
            case 1: (r, g, b) = (x, c, 0); break;
            case 2: (r, g, b) = (0, c, x); break;
            case 3: (r, g, b) = (0, x, c); break;
            case 4: (r, g, b) = (x, 0, c); break;
            default: (r, g, b) = (c, 0, x); break;
        }
        return Color.FromArgb((int)Math.Round((r + m) * 255), (int)Math.Round((g + m) * 255), (int)Math.Round((b + m) * 255));
    }

    public static (double H, double S, double V) From(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = d == 0 ? 0
            : max == r ? 60 * (((g - b) / d % 6 + 6) % 6)
            : max == g ? 60 * ((b - r) / d + 2)
            : 60 * ((r - g) / d + 4);
        return (h, max == 0 ? 0 : d / max, max);
    }

    /// Слишком тёмный акцент на тёмном фоне не виден.
    public static bool TooDark(Color c) => 0.299 * c.R + 0.587 * c.G + 0.114 * c.B < 50;
}

/// Квадрат «насыщенность / яркость» для выбранного оттенка.
public class SvPicker : Control
{
    Bitmap? cache;
    double cacheHue = -1, hue;
    bool drag;
    public double S { get; private set; } = 1;
    public double V { get; private set; } = 1;
    public event EventHandler? Changed;

    public SvPicker()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Cross;
    }

    public double Hue
    {
        get => hue;
        set { hue = value; Invalidate(); }
    }

    public void SetSv(double s, double v)
    {
        S = Math.Clamp(s, 0, 1);
        V = Math.Clamp(v, 0, 1);
        Invalidate();
    }

    void Rebuild()
    {
        int w = Math.Max(2, Width), h = Math.Max(2, Height);
        cache?.Dispose();
        cache = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var data = cache.LockBits(new Rectangle(0, 0, w, h),
            System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var px = new int[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                px[y * w + x] = Hsv.ToColor(hue, x / (double)(w - 1), 1 - y / (double)(h - 1)).ToArgb();
        System.Runtime.InteropServices.Marshal.Copy(px, 0, data.Scan0, px.Length);
        cache.UnlockBits(data);
        cacheHue = hue;
    }

    void Pick(Point p)
    {
        SetSv(p.X / (double)Math.Max(1, Width - 1), 1 - p.Y / (double)Math.Max(1, Height - 1));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        drag = true;
        Capture = true;
        Pick(e.Location);
    }

    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (drag) Pick(e.Location); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); drag = false; Capture = false; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        if (cache == null || cache.Width != Width || cache.Height != Height || cacheHue != hue) Rebuild();
        g.DrawImageUnscaled(cache!, 0, 0);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float x = (float)(S * (Width - 1)), y = (float)((1 - V) * (Height - 1));
        using (var dark = new Pen(Color.FromArgb(160, 0, 0, 0), 3f)) g.DrawEllipse(dark, x - 6, y - 6, 12, 12);
        using (var light = new Pen(Color.White, 1.5f)) g.DrawEllipse(light, x - 6, y - 6, 12, 12);
        using var border = new Pen(Theme.Border, 1f);
        g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) cache?.Dispose();
        base.Dispose(disposing);
    }
}

/// Вертикальная полоса оттенков.
public class HueBar : Control
{
    bool drag;
    public double Hue { get; private set; }
    public event EventHandler? Changed;

    public HueBar()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
    }

    public void SetHue(double h)
    {
        Hue = Math.Clamp(h, 0, 359.99);
        Invalidate();
    }

    void Pick(Point p)
    {
        SetHue(p.Y / (double)Math.Max(1, Height - 1) * 360);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        drag = true;
        Capture = true;
        Pick(e.Location);
    }

    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (drag) Pick(e.Location); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); drag = false; Capture = false; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        for (int y = 0; y < Height; y++)
        {
            using var pen = new Pen(Hsv.ToColor(y / (double)Math.Max(1, Height - 1) * 360, 1, 1));
            g.DrawLine(pen, 0, y, Width, y);
        }
        float my = (float)(Hue / 360 * (Height - 1));
        using (var dark = new Pen(Color.FromArgb(160, 0, 0, 0), 4f)) g.DrawRectangle(dark, 1, my - 3, Width - 3, 6);
        using (var light = new Pen(Color.White, 2f)) g.DrawRectangle(light, 1, my - 3, Width - 3, 6);
        using var border = new Pen(Theme.Border, 1f);
        g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
    }
}

// ───────────────────────────── Диалоги ─────────────────────────────

static class Dialogs
{
    static Form MakeForm(string title, int height)
    {
        var f = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(420, height),
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            BackColor = Theme.Side,
            ForeColor = Theme.Text,
            Font = new Font("Segoe UI", 9.5F)
        };
        f.HandleCreated += (_, _) => Native.DarkTitle(f.Handle);
        return f;
    }

    static TextBox MakeBox(int top, string text = "", bool password = false) => new()
    {
        Left = 16, Top = top, Width = 388,
        BackColor = Theme.Bg, ForeColor = Theme.Text,
        BorderStyle = BorderStyle.FixedSingle, Text = text, UseSystemPasswordChar = password
    };

    public static string? AskText(IWin32Window owner, string title, string text)
    {
        using var f = MakeForm(title, 150);
        var lbl = new Label { Left = 16, Top = 16, Width = 388, Height = 50, Text = text, ForeColor = Theme.Muted };
        var tb = MakeBox(72);
        var ok = new FancyButton { Text = "OK", Left = 232, Top = 108, Width = 80, Height = 32, Primary = true, DialogResult = DialogResult.OK };
        var cancel = new FancyButton { Text = "Отмена", Left = 320, Top = 108, Width = 84, Height = 32, DialogResult = DialogResult.Cancel };
        f.Controls.AddRange(new Control[] { lbl, tb, ok, cancel });
        f.AcceptButton = ok; f.CancelButton = cancel;
        f.Shown += (_, _) => tb.Focus();
        if (f.ShowDialog(owner) != DialogResult.OK) return null;
        var v = tb.Text.Trim();
        return v == "" ? null : v;
    }

    public static (string User, string Pass)? AskCredentials(IWin32Window owner, string defaultUser)
    {
        using var f = MakeForm("Вход в Steam", 232);
        var info = new Label
        {
            Left = 16, Top = 12, Width = 388, Height = 50,
            ForeColor = Theme.Muted,
            Text = "Логин и пароль нужны один раз для получения сессии. Пароль не сохраняется на диске."
        };
        var l1 = new Label { Left = 16, Top = 68, AutoSize = true, Text = "Логин Steam:" };
        var u = MakeBox(88, defaultUser);
        var l2 = new Label { Left = 16, Top = 120, AutoSize = true, Text = "Пароль:" };
        var p = MakeBox(140, "", true);
        var ok = new FancyButton { Text = "Войти", Left = 232, Top = 186, Width = 80, Height = 32, Primary = true, DialogResult = DialogResult.OK };
        var cancel = new FancyButton { Text = "Отмена", Left = 320, Top = 186, Width = 84, Height = 32, DialogResult = DialogResult.Cancel };
        f.Controls.AddRange(new Control[] { info, l1, u, l2, p, ok, cancel });
        f.AcceptButton = ok; f.CancelButton = cancel;
        if (f.ShowDialog(owner) != DialogResult.OK) return null;
        if (u.Text.Trim() == "" || p.Text == "") return null;
        return (u.Text.Trim(), p.Text);
    }

    /// Выбор своего акцентного цвета в стиле приложения. null — отмена.
    public static Color? PickColor(IWin32Window owner, Color current)
    {
        using var f = MakeForm("Свой цвет", 290);
        var sv = new SvPicker { Bounds = new Rectangle(16, 16, 240, 170) };
        var bar = new HueBar { Bounds = new Rectangle(266, 16, 22, 170) };
        var preview = new RoundedPanel { Bounds = new Rectangle(300, 16, 104, 62), Radius = 10 };
        var hexLbl = new Label { Left = 300, Top = 92, AutoSize = true, ForeColor = Theme.Muted, Text = "HEX" };
        var hex = MakeBox(112);
        hex.Left = 300;
        hex.Width = 104;
        hex.MaxLength = 7;
        var warn = new Label
        {
            Left = 16, Top = 198, Width = 388, Height = 20,
            ForeColor = Theme.Warn, Text = "Слишком тёмный цвет — на тёмном фоне его не будет видно"
        };
        var ok = new FancyButton { Text = "Применить", Left = 212, Top = 240, Width = 100, Height = 32, Primary = true, DialogResult = DialogResult.OK };
        var cancel = new FancyButton { Text = "Отмена", Left = 320, Top = 240, Width = 84, Height = 32, DialogResult = DialogResult.Cancel };
        f.Controls.AddRange(new Control[] { sv, bar, preview, hexLbl, hex, warn, ok, cancel });
        f.AcceptButton = ok; f.CancelButton = cancel;

        Color color = current;
        bool updating = false;

        void Refresh(bool fromHex)
        {
            if (updating) return;
            updating = true;
            color = Hsv.ToColor(bar.Hue, sv.S, sv.V);
            sv.Hue = bar.Hue;
            preview.BackColor = color;
            if (!fromHex) hex.Text = ColorTranslator.ToHtml(color);
            bool dark = Hsv.TooDark(color);
            warn.Visible = dark;
            ok.Enabled = !dark;
            updating = false;
        }

        var (h0, s0, v0) = Hsv.From(current);
        bar.SetHue(h0);
        sv.Hue = h0;
        sv.SetSv(s0, v0);
        Refresh(false);

        sv.Changed += (_, _) => Refresh(false);
        bar.Changed += (_, _) => Refresh(false);
        hex.TextChanged += (_, _) =>
        {
            if (updating) return;
            string t = hex.Text.Trim();
            if (!t.StartsWith('#')) t = "#" + t;
            if (t.Length != 7 || !t.Skip(1).All(Uri.IsHexDigit)) return;
            var c = ColorTranslator.FromHtml(t);
            var (h, s, v) = Hsv.From(c);
            // у серых цветов оттенок не определён — оставляем текущий
            if (s > 0 && v > 0) bar.SetHue(h);
            sv.SetSv(s, v);
            Refresh(true);
            color = c; // ровно тот цвет, что введён, без округления через HSV
        };

        if (f.ShowDialog(owner) != DialogResult.OK) return null;
        return color;
    }
}

// ───────────────────────────── Главное окно ─────────────────────────────

public class MainForm : Form, IMessageFilter
{
    static HttpClient Http => Net.Http;
    const int ColTime = 0, ColHero = 1, ColResult = 2, ColKda = 3, ColDur = 4, ColGpm = 5, ColRating = 6, ColDelta = 7;
    static readonly Font HeroFont = new("Segoe UI Semibold", 9.5F);
    static readonly Font KdaFont = new("Segoe UI Semibold", 9.5F);
    static readonly Font BoldCellFont = new("Segoe UI Semibold", 9.5F);
    readonly Settings settings = Storage.Load<Settings>("settings.json");
    DataFile data = Storage.Load<DataFile>("data.json");
    readonly TitleBar titleBar = new();
    readonly Panel sideFrame = new() { Dock = DockStyle.Left, Width = 320, BackColor = Theme.Side };
    readonly Panel mainFrame = new() { Dock = DockStyle.Fill, BackColor = Theme.Bg };
    readonly Label appKickerLabel = new() { Text = "DOTA 2", Font = new Font("Segoe UI Semibold", 8F), ForeColor = Theme.Accent, AutoSize = true };
    readonly Label appTitleLabel = new() { Text = "MMR Tracker", Font = new Font("Segoe UI", 19F, FontStyle.Bold), ForeColor = Theme.Text, AutoSize = false, Width = 276, Height = 38 };
    readonly AvatarBox avatarBox = new() { Width = 46, Height = 46 };
    readonly Label personaLabel = new() { Font = new Font("Segoe UI Semibold", 10.5F), ForeColor = Theme.Text, AutoSize = false, AutoEllipsis = true, Width = 202, Height = 20 };
    readonly Label accountSubLabel = new() { Font = new Font("Segoe UI", 8.5F), ForeColor = Theme.Muted, AutoSize = false, AutoEllipsis = true, Width = 202, Height = 18 };
    readonly StatusPill dotaLabel = new() { Width = 280, Height = 34 };
    readonly ToggleSwitch autoToggle = new();
    readonly FancyButton syncBtn = new() { Text = "Синхронизировать", Primary = true };
    readonly FancyButton accountBtn = new() { Text = "Сменить аккаунт" };
    readonly FancyButton resetBtn = new() { Text = "Сброс истории" };
    readonly FancyButton settingsBtn = new() { Text = "Параметры" };
    readonly FancyButton stratzBtn = new() { Text = "Stratz" };
    readonly FancyButton dotabuffBtn = new() { Text = "Dotabuff" };
    readonly Panel settingsPanel = new() { BackColor = Theme.Side, Height = 0, Visible = false };
    readonly List<ColorSwatch> swatches = new();
    readonly WinTimer settingsTimer = new() { Interval = 15 };
    readonly MenuButton menuBtn = new();
    readonly ViewMenu viewMenu = new("История матчей", "Статистика игр");
    readonly Label titleLbl = new() { Text = "История матчей", Font = new Font("Segoe UI", 22F, FontStyle.Bold), ForeColor = Theme.Text, AutoSize = true, Location = new Point(54, 0) };
    RoundedPanel gridCard = null!;
    readonly Panel statsView = new() { Dock = DockStyle.Fill, BackColor = Theme.Bg, Visible = false };
    readonly StatTile tileBest = new() { Title = "ЛУЧШИЙ ГЕРОЙ ПО ВИНРЕЙТУ" };
    readonly StatTile tileKda = new() { Title = "СРЕДНИЙ K / D / A" };
    readonly StatTile tileGpm = new() { Title = "СРЕДНИЙ GPM" };
    readonly StatTile tileXpm = new() { Title = "СРЕДНИЙ XPM" };
    int viewIndex; // 0: История матчей, 1: Статистика игр
    bool settingsOpen;
    const int SettingsFullHeight = 200;
    static readonly Color[] AccentPresets =
    {
        Color.FromArgb(214, 40, 40),   // красный (по умолчанию)
        Color.FromArgb(235, 120, 30),  // оранжевый
        Color.FromArgb(225, 180, 40),  // жёлтый
        Color.FromArgb(60, 180, 110),  // зелёный
        Color.FromArgb(60, 130, 230),  // синий
        Color.FromArgb(150, 90, 230)   // фиолетовый
    };
    int currentPeriodIndex = 1; // 0: День, 1: Неделя, 2: Месяц, 3: Всё время
    readonly DarkScrollableGrid grid = new();
    readonly StatCard cardRating = new() { Title = "РЕЙТИНГ", AccentColor = Theme.Accent };
    readonly StatCard cardLast = new() { Title = "ПОСЛЕДНЯЯ ИГРА", AccentColor = Theme.Accent };
    readonly StatCard cardPeriod = new() { Title = "ЗА ПЕРИОД", AccentColor = Theme.Accent };
    readonly StatCard cardWin = new() { Title = "ВИНРЕЙТ", AccentColor = Theme.Accent };
    readonly ToolStripStatusLabel statusLabel = new() { Text = "Готово" };
    readonly WinTimer watcher = new() { Interval = 15_000 };
    readonly WinTimer statsTimer = new() { Interval = 90_000 };
    bool busy, enriching, wasDotaRunning, watching, dotaChecked;
    bool? dotaState;                 // статус Dota по данным Steam: true/false, null — неизвестен
    DateTime ignorePresenceUntil;    // после своей синхронизации Steam ещё пару секунд показывает «в игре»

    public MainForm()
    {
        Text = "Dota 2 MMR Tracker";
        Width = 1400;
        Height = 740;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1200, 640);
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Opacity = 0;

        // Установка иконки приложения, если файл icon.ico присутствует в корневой папке
        if (File.Exists("icon.ico"))
        {
            Icon = new Icon("icon.ico");
        }

        BuildLayout();
        ConfigureGrid();
        InitAccent();
        autoToggle.SetCheckedSilently(settings.AutoSync);
        UpdateAccountInfo();

        Application.AddMessageFilter(this);
        FormClosed += (_, _) => Application.RemoveMessageFilter(this);
        Deactivate += (_, _) => CloseMenu();
        menuBtn.Click += (_, _) => ToggleMenu();
        viewMenu.ItemChosen += SetView;
        stratzBtn.Click += (_, _) => OpenProfile("https://stratz.com/players/");
        dotabuffBtn.Click += (_, _) => OpenProfile("https://www.dotabuff.com/players/");

        titleBar.MinBtn.Click += (_, _) => WindowState = FormWindowState.Minimized;
        titleBar.MaxBtn.Click += (_, _) => ToggleMaximize();
        titleBar.CloseBtn.Click += (_, _) => Close();
        titleBar.ToggleMaximize += (_, _) => ToggleMaximize();
        Resize += (_, _) =>
            titleBar.MaxBtn.Glyph = WindowState == FormWindowState.Maximized
                ? CaptionButton.Kind.Restore
                : CaptionButton.Kind.Maximize;

        syncBtn.Click += async (_, _) => await SyncAsync(true);
        accountBtn.Click += async (_, _) =>
        {
            settings.TokenEnc = "";
            settings.Username = "";
            settings.PersonaName = "";
            SaveAll();
            UpdateAccountInfo();
            await SyncAsync(true);
        };
        resetBtn.Click += (_, _) => ResetHistory();
        settingsBtn.Click += (_, _) => ToggleSettings();
        settingsTimer.Tick += (_, _) => StepSettings();
        autoToggle.CheckedChanged += (_, _) =>
        {
            settings.AutoSync = autoToggle.Checked;
            SaveAll();
            SetStatus(autoToggle.Checked ? "Автосинхронизация включена" : "Автосинхронизация выключена");
        };

        cardPeriod.Cursor = Cursors.Hand;
        cardPeriod.Click += (_, _) =>
        {
            currentPeriodIndex = (currentPeriodIndex + 1) % 4;
            Rebind();
        };

        watcher.Tick += (_, _) => { _ = WatchDotaAsync(); };
        watcher.Start();

        statsTimer.Tick += (_, _) =>
        {
            if (!busy && PendingStats().Count > 0) _ = EnrichAsync(false);
        };
        statsTimer.Start();

        FormClosing += (_, _) => SaveAll();
        Shown += async (_, _) =>
        {
            FadeIn();
            Rebind();
            var first = await FetchDotaPresenceAsync();
            dotaChecked = true;
            dotaState = first;
            wasDotaRunning = first == true;
            SetDotaState(first);
            await HeroData.InitAsync();
            Rebind();
            _ = EnrichAsync(false);
            if (settings.TokenEnc != "" && !wasDotaRunning) await SyncAsync(false);
        };
    }

    // ── Параметры и акцентный цвет ──

    void ToggleSettings()
    {
        settingsOpen = !settingsOpen;
        settingsTimer.Start();
    }

    void StepSettings()
    {
        int target = settingsOpen ? SettingsFullHeight : 0;
        int h = settingsPanel.Height;
        int step = Math.Max(8, Math.Abs(target - h) / 4);
        h = h < target ? Math.Min(target, h + step) : Math.Max(target, h - step);
        if (h > 0) settingsPanel.Visible = true;
        settingsPanel.Height = h;
        if (h == target)
        {
            settingsPanel.Visible = h > 0;
            settingsTimer.Stop();
        }
    }

    void InitAccent()
    {
        Color c = Theme.DefaultAccent;
        if (settings.AccentHex != "")
        {
            try { c = ColorTranslator.FromHtml(settings.AccentHex); } catch { }
        }
        ApplyAccent(c, false);
    }

    void OnSwatchClick(ColorSwatch sw)
    {
        Color c = sw.Swatch;
        if (sw.Custom)
        {
            var picked = Dialogs.PickColor(this, Theme.Accent);
            if (picked == null) return;
            c = picked.Value;
        }
        ApplyAccent(c, true);
    }

    void ApplyAccent(Color c, bool save)
    {
        c = Color.FromArgb(c.R, c.G, c.B);
        Theme.SetAccent(c);
        foreach (var card in new[] { cardRating, cardLast, cardPeriod, cardWin }) card.AccentColor = c;
        appKickerLabel.ForeColor = c;

        bool matched = false;
        foreach (var sw in swatches.Where(x => !x.Custom))
        {
            sw.Selected = sw.Swatch.ToArgb() == c.ToArgb();
            matched |= sw.Selected;
        }
        var custom = swatches.First(x => x.Custom);
        custom.Selected = !matched;
        if (!matched) custom.Swatch = c;

        if (save)
        {
            settings.AccentHex = ColorTranslator.ToHtml(c);
            SaveAll();
        }
        Repaint(this);
    }

    static void Repaint(Control c)
    {
        c.Invalidate();
        foreach (Control child in c.Controls) Repaint(child);
    }

    void ToggleMaximize() =>
        WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;

    protected override void WndProc(ref Message m)
    {
        const int WM_NCCALCSIZE = 0x0083;
        const int WM_NCHITTEST = 0x0084;
        if (m.Msg == WM_NCCALCSIZE && m.WParam != IntPtr.Zero)
        {
            var p = Marshal.PtrToStructure<Native.NCCALCSIZE_PARAMS>(m.LParam);
            int origTop = p.rgrc0.Top;
            base.WndProc(ref m);
            p = Marshal.PtrToStructure<Native.NCCALCSIZE_PARAMS>(m.LParam);
            if (WindowState == FormWindowState.Maximized)
            {
                int frame = Native.GetSystemMetrics(33) + Native.GetSystemMetrics(92);
                p.rgrc0.Top = origTop + frame;
            }
            else p.rgrc0.Top = origTop;
            Marshal.StructureToPtr(p, m.LParam, false);
            return;
        }
        if (m.Msg == WM_NCHITTEST)
        {
            base.WndProc(ref m);
            if ((int)m.Result == 1 && WindowState == FormWindowState.Normal)
            {
                long lp = m.LParam.ToInt64();
                var pt = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                const int grip = 6;
                bool l = pt.X < grip, r = pt.X >= ClientSize.Width - grip, t = pt.Y < grip;
                if (t && l) m.Result = (IntPtr)13;
                else if (t && r) m.Result = (IntPtr)14;
                else if (t) m.Result = (IntPtr)12;
            }
            return;
        }
        base.WndProc(ref m);
    }

    void FadeIn()
    {
        var t = new WinTimer { Interval = 15 };
        t.Tick += (_, _) =>
        {
            Opacity = Math.Min(1.0, Opacity + 0.08);
            if (Opacity >= 1.0) { t.Stop(); t.Dispose(); }
        };
        t.Start();
    }

    void BuildLayout()
    {
        appKickerLabel.Location = new Point(22, 18);
        appTitleLabel.Location = new Point(20, 34);

        var profileCard = new RoundedPanel { Location = new Point(20, 84), Size = new Size(280, 66) };
        avatarBox.Location = new Point(10, 10);
        personaLabel.Location = new Point(66, 13);
        accountSubLabel.Location = new Point(66, 35);
        profileCard.Controls.AddRange(new Control[] { avatarBox, personaLabel, accountSubLabel });

        dotaLabel.Location = new Point(20, 164);

        stratzBtn.SetBounds(20, 204, 136, 36);
        dotabuffBtn.SetBounds(164, 204, 136, 36);
        syncBtn.Location = new Point(20, 250);
        syncBtn.Size = new Size(280, 42);
        settingsBtn.Location = new Point(20, 300);
        settingsBtn.Size = new Size(280, 36);

        // Выпадающая вниз панель параметров: автосинхронизация, смена аккаунта, сброс истории, акцентный цвет
        settingsPanel.Location = new Point(20, 344);
        settingsPanel.Width = 280;
        var toggleCard = new RoundedPanel { Location = new Point(0, 4), Size = new Size(280, 46) };
        var toggleTitle = new Label
        {
            Text = "Автосинхронизация",
            Font = new Font("Segoe UI Semibold", 9.5F),
            ForeColor = Theme.Text,
            AutoSize = false, Width = 190, Height = 20,
            Location = new Point(14, 5)
        };
        var toggleSub = new Label
        {
            Text = "после закрытия Dota",
            Font = new Font("Segoe UI", 8F),
            ForeColor = Theme.Muted,
            AutoSize = false, Width = 190, Height = 16,
            Location = new Point(14, 25)
        };
        autoToggle.Location = new Point(280 - 14 - autoToggle.Width, (46 - autoToggle.Height) / 2);
        toggleCard.Controls.AddRange(new Control[] { toggleTitle, toggleSub, autoToggle });
        accountBtn.SetBounds(0, 58, 280, 36);
        resetBtn.SetBounds(0, 100, 280, 36);
        var accentTitle = new Label
        {
            Text = "АКЦЕНТНЫЙ ЦВЕТ",
            Font = new Font("Segoe UI Semibold", 8F),
            ForeColor = Theme.Muted,
            AutoSize = true,
            Location = new Point(2, 146)
        };
        settingsPanel.Controls.AddRange(new Control[] { toggleCard, accountBtn, resetBtn, accentTitle });
        for (int i = 0; i <= AccentPresets.Length; i++)
        {
            bool custom = i == AccentPresets.Length;
            var sw = new ColorSwatch
            {
                Custom = custom,
                Swatch = custom ? Theme.Accent : AccentPresets[i],
                Location = new Point(i * 41, 166)
            };
            sw.Click += (_, _) => OnSwatchClick(sw);
            swatches.Add(sw);
            settingsPanel.Controls.Add(sw);
        }

        var footer = new Label
        {
            Text = "gl hf  ·  v2.4",
            Dock = DockStyle.Bottom,
            Height = 36,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Theme.Muted,
            Font = new Font("Segoe UI", 8.5F)
        };

        sideFrame.Controls.AddRange(new Control[]
        {
            appKickerLabel, appTitleLabel, profileCard, dotaLabel, stratzBtn, dotabuffBtn,
            syncBtn, settingsBtn, settingsPanel, footer
        });

        sideFrame.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Border, 1f);
            e.Graphics.DrawLine(pen, sideFrame.Width - 1, 0, sideFrame.Width - 1, sideFrame.Height);
        };
        sideFrame.Resize += (_, _) => sideFrame.Invalidate();
        Gfx.DoubleBuffer(sideFrame);

        mainFrame.Padding = new Padding(28, 22, 28, 16);
        var header = new Panel { Dock = DockStyle.Top, Height = 50, BackColor = Theme.Bg };
        menuBtn.Bounds = new Rectangle(0, 1, 40, 40);
        header.Controls.Add(menuBtn);
        header.Controls.Add(titleLbl);

        gridCard = new RoundedPanel { Dock = DockStyle.Fill, Padding = new Padding(10, 8, 8, 10) };
        grid.Dock = DockStyle.Fill;
        var scroll = new DarkScrollBar(grid) { Dock = DockStyle.Right };
        gridCard.Controls.Add(grid);
        gridCard.Controls.Add(scroll);

        var cardsHost = new Panel { Dock = DockStyle.Bottom, Height = 114, Padding = new Padding(0, 14, 0, 0), BackColor = Theme.Bg };
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            BackColor = Theme.Bg
        };
        for (int i = 0; i < 4; i++) table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25f));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        var cards = new[] { cardRating, cardLast, cardPeriod, cardWin };
        for (int i = 0; i < cards.Length; i++)
        {
            cards[i].Dock = DockStyle.Fill;
            cards[i].Margin = new Padding(0, 0, i == cards.Length - 1 ? 0 : 12, 0);
            table.Controls.Add(cards[i], i, 0);
        }
        cardsHost.Controls.Add(table);

        var content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
        var statTable = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            BackColor = Theme.Bg
        };
        for (int i = 0; i < 2; i++)
        {
            statTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            statTable.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));
        }
        var tiles = new[] { tileBest, tileKda, tileGpm, tileXpm };
        for (int i = 0; i < tiles.Length; i++)
        {
            tiles[i].Dock = DockStyle.Fill;
            tiles[i].Margin = new Padding(0, 0, i % 2 == 0 ? 12 : 0, i < 2 ? 12 : 0);
            statTable.Controls.Add(tiles[i], i % 2, i / 2);
        }
        statsView.Controls.Add(statTable);

        content.Controls.Add(gridCard);
        content.Controls.Add(statsView);
        content.Controls.Add(cardsHost);

        mainFrame.Controls.Add(content);
        mainFrame.Controls.Add(header);
        viewMenu.Location = new Point(28, 68);
        mainFrame.Controls.Add(viewMenu);
        viewMenu.BringToFront();

        var strip = new StatusStrip { BackColor = Theme.Side, ForeColor = Theme.Muted, SizingGrip = false };
        statusLabel.ForeColor = Theme.Muted;
        strip.Items.Add(statusLabel);

        Controls.Add(mainFrame);
        Controls.Add(sideFrame);
        Controls.Add(strip);
        Controls.Add(titleBar);
    }

    void ConfigureGrid()
    {
        Gfx.DoubleBuffer(grid);
        grid.ReadOnly = true;
        grid.TabStop = false;
        grid.ShowCellToolTips = false;
        grid.AllowUserToResizeColumns = false;
        grid.AllowUserToOrderColumns = false;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.AllowUserToResizeRows = false;
        grid.RowHeadersVisible = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.MultiSelect = false;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        grid.BackgroundColor = Theme.Card;
        grid.BorderStyle = BorderStyle.None;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.GridColor = Color.FromArgb(38, 38, 43);
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        grid.ColumnHeadersHeight = 38;
        grid.ColumnHeadersDefaultCellStyle.BackColor = Theme.Card;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Theme.Muted;
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Theme.Card;
        grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Theme.Muted;
        grid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI Semibold", 8.5F);
        grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(8, 0, 0, 0);
        grid.DefaultCellStyle.BackColor = Theme.Card;
        grid.DefaultCellStyle.ForeColor = Color.FromArgb(214, 214, 222);
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(68, 24, 28);
        grid.DefaultCellStyle.SelectionForeColor = Color.White;
        grid.DefaultCellStyle.Font = new Font("Segoe UI", 9.5F);
        grid.DefaultCellStyle.Padding = new Padding(8, 0, 0, 0);
        grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(21, 21, 24);
        grid.AlternatingRowsDefaultCellStyle.SelectionBackColor = Color.FromArgb(68, 24, 28);
        grid.RowTemplate.Height = 46;

        foreach (var (name, header, weight) in new[]
        {
            ("time", "  ВРЕМЯ", 95), ("hero", "ГЕРОЙ", 215), ("res", "ИТОГ", 125),
            ("kda", "K / D / A", 105), ("dur", "ВРЕМЯ ИГРЫ", 90), ("gpm", "GPM / XPM", 105),
            ("rating", "РЕЙТИНГ", 75), ("delta", "Δ", 65)
        })
        {
            int i = grid.Columns.Add(name, header);
            grid.Columns[i].FillWeight = weight;
            grid.Columns[i].SortMode = DataGridViewColumnSortMode.NotSortable;
        }
        grid.CellPainting += Grid_CellPainting;
    }

    void Grid_CellPainting(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || (e.ColumnIndex != ColHero && e.ColumnIndex != ColKda)) return;
        if (grid.Rows[e.RowIndex].Tag is not MatchEntry m) return;
        if (e.ColumnIndex == ColKda && !m.HasStats) return;
        var g = e.Graphics;
        if (g == null) return;
        e.Paint(e.CellBounds, e.PaintParts & ~DataGridViewPaintParts.ContentForeground);
        var b = e.CellBounds;
        if (e.ColumnIndex == ColHero)
        {
            const int iw = 53, ih = 30;
            var rect = new Rectangle(b.X + 10, b.Y + (b.Height - ih) / 2, iw, ih);
            var img = HeroData.Icon(m.HeroId);
            var state = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            using (var path = Gfx.Round(rect, 6))
            {
                if (img != null)
                {
                    g.SetClip(path, CombineMode.Intersect);
                    g.DrawImage(img, rect);
                    g.Restore(state);
                    state = g.Save();
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                }
                else
                {
                    using var ph = new SolidBrush(Theme.CardHi);
                    g.FillPath(ph, path);
                }
                using var pen = new Pen(Color.FromArgb(90, Theme.Border), 1f);
                g.DrawPath(pen, path);
            }
            g.Restore(state);
            var textRect = new Rectangle(rect.Right + 10, b.Y, Math.Max(10, b.Right - rect.Right - 14), b.Height);
            TextRenderer.DrawText(g, HeroData.Name(m.HeroId), HeroFont, textRect, Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }
        else
        {
            string[] parts = { m.Kills.ToString()!, " / ", m.Deaths.ToString()!, " / ", m.Assists.ToString()! };
            Color[] cols = { Theme.Text, Theme.Muted, Theme.Bad, Theme.Muted, Color.FromArgb(170, 170, 184) };
            const TextFormatFlags fl = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
            int x = b.X + 10;
            for (int i = 0; i < parts.Length; i++)
            {
                var size = TextRenderer.MeasureText(g, parts[i], KdaFont, new Size(int.MaxValue, int.MaxValue), fl);
                TextRenderer.DrawText(g, parts[i], KdaFont, new Point(x, b.Y + (b.Height - size.Height) / 2), cols[i], fl);
                x += size.Width;
            }
        }
        e.Handled = true;
    }

    // ── Статус Dota 2 по данным Steam (а не по процессам этого ПК) ──

    /// Спрашивает у Steam Community, запущена ли Dota 2 именно на аккаунте пользователя.
    /// true/false — статус известен; null — неизвестен (нет аккаунта, нет сети, профиль не публичный).
    /// Нужно, чтобы в настройках приватности Steam «Сведения об играх» были открыты.
    async Task<bool?> FetchDotaPresenceAsync()
    {
        if (settings.AccountId == 0) return null;
        try
        {
            ulong id64 = settings.AccountId + 76561197960265728UL;
            string xml = await Http.GetStringAsync(
                $"https://steamcommunity.com/profiles/{id64}?xml=1&t={DateTime.UtcNow.Ticks}");
            var root = XDocument.Parse(xml).Root;
            if (root == null) return null;
            var game = root.Element("inGameInfo");
            if (game != null)
            {
                string link = (game.Element("gameLink")?.Value ?? "").TrimEnd('/');
                string name = game.Element("gameName")?.Value ?? "";
                return link.EndsWith("/570") || name.Equals("Dota 2", StringComparison.OrdinalIgnoreCase);
            }
            string privacy = root.Element("privacyState")?.Value ?? "";
            return privacy == "public" ? false : null;
        }
        catch { return null; }
    }

    void SetDotaState(bool? running)
    {
        dotaLabel.Active = running == true;
        dotaLabel.Text =
            settings.AccountId == 0 ? "Dota 2: войдите в Steam" :
            running == null ? (dotaChecked ? "Статус Steam недоступен" : "Проверка Steam…") :
            running.Value ? "Dota 2 запущена" : "Dota 2 закрыта";
    }

    async Task WatchDotaAsync()
    {
        // пока идёт наша синхронизация (и сразу после неё) Steam видит «Dota» от нашей же сессии — игнорируем
        if (watching || busy || DateTime.UtcNow < ignorePresenceUntil) return;
        watching = true;
        try
        {
            var state = await FetchDotaPresenceAsync();
            if (busy || DateTime.UtcNow < ignorePresenceUntil) return;
            dotaChecked = true;
            dotaState = state;
            SetDotaState(state);
            if (state == null) return;
            bool running = state.Value;
            if (wasDotaRunning && !running && autoToggle.Checked && settings.TokenEnc != "")
                _ = DelayedSync();
            wasDotaRunning = running;
        }
        finally { watching = false; }
    }

    async Task DelayedSync()
    {
        SetStatus("Dota закрыта — синхронизация через несколько секунд…");
        await Task.Delay(15_000);
        if (busy) return;
        if (await FetchDotaPresenceAsync() != true) await SyncAsync(false);
    }

    // ── Ссылки на профиль ──

    void OpenProfile(string baseUrl)
    {
        if (settings.AccountId == 0)
        {
            SetStatus("Сначала войдите в Steam — нужен ID аккаунта");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(baseUrl + settings.AccountId) { UseShellExecute = true });
        }
        catch (Exception ex) { SetStatus("Не удалось открыть ссылку: " + ex.Message); }
    }

    // ── Меню выбора экрана ──

    void ToggleMenu()
    {
        if (viewMenu.Visible) { CloseMenu(); return; }
        viewMenu.SelectedIndex = viewIndex;
        viewMenu.Visible = true;
        viewMenu.BringToFront();
        menuBtn.Open = true;
    }

    void CloseMenu()
    {
        if (!viewMenu.Visible) return;
        viewMenu.Visible = false;
        menuBtn.Open = false;
    }

    void SetView(int index)
    {
        CloseMenu();
        viewIndex = index;
        titleLbl.Text = index == 0 ? "История матчей" : "Статистика игр";
        gridCard.Visible = index == 0;
        statsView.Visible = index == 1;
        UpdateStatsView();
    }

    /// Клик мимо открытого меню закрывает его.
    public bool PreFilterMessage(ref Message m)
    {
        const int WM_LBUTTONDOWN = 0x0201, WM_RBUTTONDOWN = 0x0204, WM_NCLBUTTONDOWN = 0x00A1;
        if (viewMenu.Visible && (m.Msg == WM_LBUTTONDOWN || m.Msg == WM_RBUTTONDOWN || m.Msg == WM_NCLBUTTONDOWN))
        {
            var c = Control.FromHandle(m.HWnd);
            if (c != viewMenu && c != menuBtn) CloseMenu();
        }
        return false;
    }

    Task<string> AskCodeAsync(string prompt)
    {
        string? code = null;
        Invoke((Action)(() => code = Dialogs.AskText(this, "Steam Guard", prompt)));
        if (code == null) throw new OperationCanceledException("Ввод кода отменён.");
        return Task.FromResult(code);
    }

    async Task SyncAsync(bool manual)
    {
        if (busy) return;
        if (wasDotaRunning)
        {
            if (!manual) return;
            var answer = MessageBox.Show(this,
                "Steam показывает, что Dota 2 сейчас запущена на вашем аккаунте. Синхронизация открывает вторую игровую сессию Steam " +
                "и может прервать текущую игру.\n\nЛучше закрыть Dota — тогда трекер сам заберёт результаты. " +
                "Всё равно продолжить?",
                "Dota 2 запущена", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (answer != DialogResult.Yes) return;
        }
        string username = settings.Username;
        string? password = null;
        string? token = Storage.Unprotect(settings.TokenEnc);
        if (string.IsNullOrEmpty(token))
        {
            token = null;
            var creds = Dialogs.AskCredentials(this, username);
            if (creds == null) return;
            username = creds.Value.User;
            password = creds.Value.Pass;
        }
        busy = true;
        syncBtn.Enabled = accountBtn.Enabled = false;
        syncBtn.Text = "Синхронизация…";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var res = await GcClient.FetchAsync(username, password, token, AskCodeAsync, SetStatus, cts.Token);
            if (res.NewRefreshToken != null)
            {
                settings.TokenEnc = Storage.Protect(res.NewRefreshToken);
                settings.Username = res.AccountName;
            }
            settings.AccountId = res.AccountId;
            Storage.WriteText("gc_dump.txt", res.Dump);
            int added = Import(res);
            SaveAll();
            UpdateAccountInfo();
            Rebind();
            SetStatus("Загрузка статистики матчей…");
            await EnrichAsync(true);
            bool anyRating = res.Matches.Any(m => m.PreviousRank > 0);
            string dumpPath = Path.Combine(Storage.Dir, "gc_dump.txt");
            if (res.Matches.Count == 0)
                SetStatus($"GC вернул пустую историю. Диагностика: {dumpPath}");
            else if (!anyRating)
                SetStatus($"GC вернул матчи ({res.Matches.Count}), но без рейтинга. Диагностика: {dumpPath}");
            else
            {
                int pending = PendingStats().Count;
                string tail = pending > 0 ? $" · статистика OpenDota для {pending} матч. подтянется позже" : "";
                SetStatus($"Синхронизировано в {DateTime.Now:HH:mm:ss}. Новых матчей: {added}{tail}");
            }
        }
        catch (TokenRejectedException)
        {
            settings.TokenEnc = "";
            settings.PersonaName = "";
            SaveAll();
            UpdateAccountInfo();
            SetStatus("Сессия Steam истекла — войдите заново.");
            if (manual)
                MessageBox.Show(this, "Сохранённая сессия Steam недействительна. Войдите заново.",
                    "Требуется вход", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (OperationCanceledException ex)
        {
            SetStatus(ex.Message);
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка: " + ex.Message);
            if (manual)
                MessageBox.Show(this, ex.Message, "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            busy = false;
            ignorePresenceUntil = DateTime.UtcNow.AddSeconds(30);
            if (dotaState == null) { dotaChecked = false; SetDotaState(null); }
            syncBtn.Text = "Синхронизировать";
            syncBtn.Enabled = accountBtn.Enabled = true;
        }
    }

    int Import(GcResult res)
    {
        var known = data.Matches.Select(m => m.MatchId).ToHashSet();
        int added = 0;
        foreach (var g in res.Matches)
        {
            bool ranked = g.Lobby == 7 || g.PreviousRank > 0;
            if (!ranked || known.Contains(g.MatchId)) continue;
            data.Matches.Add(new MatchEntry
            {
                MatchId = g.MatchId,
                StartUtc = DateTimeOffset.FromUnixTimeSeconds(g.StartTime).UtcDateTime,
                HeroId = g.HeroId,
                Won = g.Won,
                GameMode = g.GameMode,
                DurationSec = g.Duration,
                PrevRating = g.PreviousRank > 0 ? g.PreviousRank : null,
                RankChange = g.RankChange,
                Rating = g.PreviousRank > 0 ? g.PreviousRank + g.RankChange : null
            });
            added++;
        }
        return added;
    }

    List<MatchEntry> PendingStats() =>
        data.Matches.OrderByDescending(m => m.StartUtc).Take(40).Where(m => m.NeedsStats).ToList();

    /// Подтягивает K/D/A, GPM/XPM и т.д. из OpenDota для матчей без статистики.
    /// refreshFirst = true (кнопка «Синхронизировать» и автосинхронизация): сначала просим OpenDota
    /// обновить профиль, затем несколько раз опрашиваем API, пока матчи не появятся.
    async Task EnrichAsync(bool refreshFirst)
    {
        if (settings.AccountId == 0) return;
        // если фоновая подгрузка ещё идёт, дождёмся её, чтобы синхронизация точно отправила refresh
        while (enriching && refreshFirst) await Task.Delay(300);
        if (enriching) return;
        if (!refreshFirst && PendingStats().Count == 0) return;

        enriching = true;
        try
        {
            if (refreshFirst)
            {
                SetStatus("Запрашиваю обновление у OpenDota…");
                bool accepted = await OpenDota.RefreshAsync(settings.AccountId);
                if (!accepted) SetStatus("OpenDota не принял запрос обновления, пробую получить данные как есть…");
                if (PendingStats().Count == 0) return;
                await Task.Delay(3000); // OpenDota нужно время, чтобы забрать матчи из Steam
            }

            int filled = 0;
            bool reached = false;
            int attempts = refreshFirst ? 4 : 1;
            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                var missing = PendingStats();
                if (missing.Count == 0) break;

                SetStatus(attempts > 1
                    ? $"Загружаю статистику из OpenDota ({attempt}/{attempts})…"
                    : "Загружаю статистику матчей из OpenDota…");
                var recent = await OpenDota.RecentAsync(settings.AccountId);
                if (recent != null)
                {
                    reached = true;
                    var map = recent.ToDictionary(s => s.MatchId);
                    foreach (var m in missing)
                    {
                        if (!map.TryGetValue(m.MatchId, out var s)) continue;
                        m.Kills = s.Kills;
                        m.Deaths = s.Deaths;
                        m.Assists = s.Assists;
                        m.Gpm = s.Gpm;
                        m.Xpm = s.Xpm;
                        m.LastHits = s.LastHits;
                        m.HeroDamage = s.HeroDamage;
                        m.PartySize = s.PartySize;
                        m.LeaverStatus = s.LeaverStatus;
                        if (m.DurationSec <= 0) m.DurationSec = s.Duration;
                        filled++;
                    }
                }
                if (PendingStats().Count == 0 || attempt == attempts) break;
                await Task.Delay(6000);
            }

            if (filled > 0)
            {
                SaveAll();
                Rebind();
            }
            int left = PendingStats().Count;
            if (!reached)
                SetStatus("Не удалось связаться с OpenDota (сеть или лимит запросов)");
            else if (left > 0)
                SetStatus($"Статистика загружена для {filled}, ещё {left} матч. OpenDota пока не отдал " +
                          "(проверьте включена ли общедоступная история матчей в настройках Dota 2)");
            else if (filled > 0)
                SetStatus($"Статистика для {filled} матчей загружена");
        }
        catch { }
        finally
        {
            enriching = false;
        }
    }

    void Recalc()
    {
        MatchEntry? prev = null;
        foreach (var m in data.Matches.OrderBy(x => x.StartUtc).ThenBy(x => x.MatchId))
        {
            m.Delta = null;
            m.Note = "";
            if (m.Rating != null)
            {
                if (prev?.Rating != null)
                {
                    m.Delta = m.Rating - prev.Rating;
                    if (m.Delta != m.RankChange)
                        m.Note = $"Valve: {Fmt(m.RankChange)}. Разница не совпала: пропущены игры или изменение MMR";
                }
                else
                {
                    m.Delta = m.RankChange;
                    m.Note = "начало истории";
                }
            }
            else m.Note = "GC не вернул рейтинг";
            prev = m;
        }
    }

    static string Fmt(int? d) => d == null ? "" : (d > 0 ? $"+{d}" : d.ToString()!);

    static string Sign(double d)
    {
        int v = (int)Math.Round(d);
        return v > 0 ? $"+{v}" : v.ToString();
    }

    static string FmtDuration(int s) =>
        s <= 0 ? "—" : s >= 3600 ? $"{s / 3600}:{s % 3600 / 60:00}:{s % 60:00}" : $"{s / 60}:{s % 60:00}";

    void Rebind()
    {
        Recalc();
        int keepFirst = grid.FirstRow;
        grid.Rows.Clear();
        foreach (var m in data.Matches.OrderByDescending(x => x.StartUtc).ThenByDescending(x => x.MatchId))
        {
            int i = grid.Rows.Add(
                Msk.FromUtc(m.StartUtc).ToString("dd.MM HH:mm"),
                HeroData.Name(m.HeroId),
                m.Left ? "⚠ Покинута" : m.Won ? "▲ Победа" : "▼ Поражение",
                m.HasStats ? $"{m.Kills} / {m.Deaths} / {m.Assists}" : "—",
                FmtDuration(m.DurationSec),
                m.Gpm != null ? $"{m.Gpm} / {m.Xpm}" : "—",
                m.Rating?.ToString() ?? "—",
                Fmt(m.Delta));
            var row = grid.Rows[i];
            row.Tag = m;
            row.Cells[ColResult].Style.ForeColor = m.Left ? Theme.Warn : m.Won ? Theme.Good : Theme.Bad;
            row.Cells[ColResult].Style.Font = BoldCellFont;
            row.Cells[ColRating].Style.ForeColor = Theme.Text;
            row.Cells[ColRating].Style.Font = BoldCellFont;
            if (m.Delta > 0) row.Cells[ColDelta].Style.ForeColor = Theme.Good;
            else if (m.Delta < 0) row.Cells[ColDelta].Style.ForeColor = Theme.Bad;
            row.Cells[ColDelta].Style.Font = BoldCellFont;
            row.Cells[ColDur].Style.ForeColor = Theme.Muted;
            row.Cells[ColGpm].Style.ForeColor = Theme.Muted;
            row.Cells[ColTime].Style.ForeColor = Theme.Muted;
        }
        grid.ClearSelection();
        grid.ScrollToRow(keepFirst);
        UpdateStats();
        UpdateStatsView();
        if (HeroData.Ready)
            _ = HeroData.LoadIconsAsync(data.Matches.Select(m => m.HeroId), () => { grid.Invalidate(); tileBest.Invalidate(); });
    }

    string GetPeriodName() => currentPeriodIndex switch
    {
        0 => "сегодня",
        1 => "эта неделя",
        2 => "этот месяц",
        _ => "всё время"
    };

    /// Начало периода по московскому времени: сегодня с 00:00, с понедельника, с 1-го числа месяца.
    static DateTime? PeriodStartUtc(int period)
    {
        DateTime today = Msk.Now.Date;
        DateTime? startMsk = period switch
        {
            0 => today,
            1 => today.AddDays(-(((int)today.DayOfWeek + 6) % 7)),
            2 => new DateTime(today.Year, today.Month, 1),
            _ => null
        };
        return startMsk == null ? null : Msk.ToUtc(startMsk.Value);
    }

    List<MatchEntry> GetFilteredMatchesForPeriod()
    {
        var all = data.Matches.OrderBy(m => m.StartUtc).ThenBy(m => m.MatchId).ToList();
        var from = PeriodStartUtc(currentPeriodIndex);
        return from == null ? all : all.Where(m => m.StartUtc >= from.Value).ToList();
    }

    void UpdateStats()
    {
        var all = data.Matches.OrderBy(m => m.StartUtc).ThenBy(m => m.MatchId).ToList();
        if (all.Count == 0)
        {
            cardRating.SetText("—", Theme.Muted, "нет данных");
            cardLast.SetText("—", Theme.Muted, "запустите синхронизацию");
            cardPeriod.SetText("—", Theme.Muted, "");
            cardWin.SetText("—", Theme.Muted, "");
            return;
        }
        var periodMatches = GetFilteredMatchesForPeriod();
        int winsPeriod = periodMatches.Count(m => m.Won);
        int lossesPeriod = periodMatches.Count - winsPeriod;
        double wrPeriod = periodMatches.Count > 0 ? 100.0 * winsPeriod / periodMatches.Count : 0.0;
        int periodMmrDelta = periodMatches.Sum(m => m.Delta ?? m.RankChange);
        var rated = all.Where(m => m.Rating != null).ToList();
        if (rated.Count > 0)
        {
            var last = rated[^1];
            int cur = last.Rating!.Value;
            int lastDelta = last.Delta ?? 0;
            string lastSub = HeroData.Name(last.HeroId) +
                (last.HasStats ? $" · {last.Kills}/{last.Deaths}/{last.Assists}" : "");
            cardRating.SetValue(cur, d => Math.Round(d).ToString("0"), Theme.Text, $"игр: {all.Count}");
            cardLast.SetValue(lastDelta, Sign,
                lastDelta > 0 ? Theme.Good : lastDelta < 0 ? Theme.Bad : Theme.Text,
                lastSub);
            string periodTitle = GetPeriodName();
            cardPeriod.SetValue(periodMmrDelta, Sign,
                periodMmrDelta > 0 ? Theme.Good : periodMmrDelta < 0 ? Theme.Bad : Theme.Text,
                $"{periodTitle} · игр: {periodMatches.Count}");
        }
        else
        {
            cardRating.SetText("—", Theme.Muted, "рейтинг не получен");
            cardLast.SetText("—", Theme.Muted, "");
            cardPeriod.SetText("—", Theme.Muted, "");
        }
        string periodNameSub = GetPeriodName();
        cardWin.SetValue(wrPeriod, d => $"{Math.Round(d):0}%", wrPeriod >= 50 ? Theme.Good : Theme.Bad,
            $"{winsPeriod} поб. · {lossesPeriod} пор. · {periodNameSub}");
    }

    /// Экран «Статистика игр»: лучший герой по винрейту и средние показатели за выбранный период.
    /// Лучший герой — среди тех, кого играли минимум 2 раза (если таких нет — среди всех).
    void UpdateStatsView()
    {
        var pm = GetFilteredMatchesForPeriod();
        string period = GetPeriodName();

        var groups = pm.GroupBy(m => m.HeroId)
            .Select(g => (Id: g.Key, Games: g.Count(), Wins: g.Count(m => m.Won))).ToList();
        if (groups.Count == 0)
            tileBest.Set("—", Theme.Muted, $"нет игр · {period}");
        else
        {
            var pool = groups.Where(g => g.Games >= 2).ToList();
            if (pool.Count == 0) pool = groups;
            var best = pool.OrderByDescending(g => (double)g.Wins / g.Games).ThenByDescending(g => g.Games).First();
            double wr = 100.0 * best.Wins / best.Games;
            tileBest.Set(HeroData.Name(best.Id), wr >= 50 ? Theme.Good : Theme.Bad,
                $"винрейт {Math.Round(wr):0}% · {best.Wins} поб. из {best.Games} игр · {period}", best.Id);
        }

        var st = pm.Where(m => m.HasStats).ToList();
        if (st.Count == 0)
        {
            string none = $"нет статистики · {period}";
            tileKda.Set("—", Theme.Muted, none);
            tileGpm.Set("—", Theme.Muted, none);
            tileXpm.Set("—", Theme.Muted, none);
            return;
        }
        double k = st.Average(m => m.Kills ?? 0);
        double d = st.Average(m => m.Deaths ?? 0);
        double a = st.Average(m => m.Assists ?? 0);
        tileKda.Set($"{k:0.0} / {d:0.0} / {a:0.0}", Theme.Text,
            $"KDA {(k + a) / Math.Max(1.0, d):0.00} · игр: {st.Count} · {period}");
        var gp = st.Where(m => m.Gpm != null).ToList();
        if (gp.Count == 0)
        {
            tileGpm.Set("—", Theme.Muted, $"нет данных · {period}");
            tileXpm.Set("—", Theme.Muted, $"нет данных · {period}");
            return;
        }
        tileGpm.Set($"{Math.Round(gp.Average(m => m.Gpm ?? 0)):0}", Theme.Text, $"игр: {gp.Count} · {period}");
        tileXpm.Set($"{Math.Round(gp.Average(m => m.Xpm ?? 0)):0}", Theme.Text, $"игр: {gp.Count} · {period}");
    }

    void UpdateAccountInfo()
    {
        if (settings.TokenEnc != "" && settings.Username != "")
        {
            personaLabel.Text = settings.PersonaName != "" ? settings.PersonaName : settings.Username;
            avatarBox.Text = personaLabel.Text;
            accountSubLabel.Text = "Steam · подключено";
            if (settings.AccountId != 0) _ = LoadProfileAsync();
        }
        else
        {
            personaLabel.Text = "Вход не выполнен";
            avatarBox.Text = "?";
            avatarBox.Image = null;
            accountSubLabel.Text = "нажмите «Синхронизировать»";
        }
        SetDotaState(dotaState);
    }

    async Task LoadProfileAsync()
    {
        try
        {
            ulong id64 = settings.AccountId + 76561197960265728UL;
            string xml = await Http.GetStringAsync($"https://steamcommunity.com/profiles/{id64}?xml=1");
            var root = XDocument.Parse(xml).Root;
            string? name = root?.Element("steamID")?.Value;
            string? url = root?.Element("avatarFull")?.Value;
            if (!string.IsNullOrWhiteSpace(name))
            {
                settings.PersonaName = name;
                personaLabel.Text = name;
                avatarBox.Text = name;
                Storage.Save("settings.json", settings);
            }
            if (!string.IsNullOrWhiteSpace(url))
            {
                byte[] bytes = await Http.GetByteArrayAsync(url);
                using var ms = new MemoryStream(bytes);
                using var src = Image.FromStream(ms);
                avatarBox.Image = new Bitmap(src);
            }
        }
        catch { }
    }

    void SetStatus(string text)
    {
        if (InvokeRequired) BeginInvoke((Action)(() => statusLabel.Text = text));
        else statusLabel.Text = text;
    }

    void ResetHistory()
    {
        if (MessageBox.Show(this, "Удалить всю сохранённую историю матчей? Действие необратимо.",
            "Сброс", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        data = new DataFile();
        SaveAll();
        Rebind();
        SetStatus("История очищена");
    }

    void SaveAll()
    {
        Storage.Save("settings.json", settings);
        Storage.Save("data.json", data);
    }
}