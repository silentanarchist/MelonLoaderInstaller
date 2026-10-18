using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Android.Content;

namespace WarpforgePatcher;

/// <summary>
/// The app's text in the player's language (0.4.0), the same scheme as the mod's Loc (warpforge-revival-mod,
/// WarpforgeRevival/Loc.cs). Every sentence the app shows goes through Loc.T("area.key", "English text", ("name", value)):
/// the English is in the code, so there is always something to show, and the mod repo's
/// tools/localization/extract-patcher.py collects every key with its English into localization/patcher/en.json, the
/// source the translators work from (Weblate). {name} in a text is replaced by the value given for it.
///
/// Texts come from three places, each laid over the one before: the English in the code; the language files built
/// into the app (Assets/localization/&lt;code&gt;.json, copied from the mod repo's localization/patcher/ at build); and
/// a newer copy from the revival server (/api/v1/content/files/localization/patcher/&lt;code&gt;.json, checked against
/// localization/index.json), kept in the app's files, so a corrected translation reaches players without an app update.
/// A text that has no translation is shown in English.
///
/// The language is the device's, unless the player picked one on the Tools page. The log stays in English (it is
/// read by whoever helps the player), so log lines never go through T, and UserError keeps both versions.
/// </summary>
internal static class Loc
{
    /// <summary>The languages the game itself ships, in the order the language list shows them, with their own names.</summary>
    public static readonly (string Code, string Name)[] Languages =
    {
        ("en", "English"), ("es", "Español"), ("ru", "Русский"), ("fr", "Français"), ("de", "Deutsch"),
        ("pt-BR", "Português (Brasil)"), ("it", "Italiano"), ("ko", "한국어"), ("zh-CN", "简体中文"), ("ja", "日本語"),
        ("pl", "Polski"), ("cs", "Čeština"), ("tr", "Türkçe"),
    };

    private const string Prefs = "warpforge-patcher", PrefKey = "language";
    private static Dictionary<string, string> table = new();
    public static string Language { get; private set; } = "en";
    private static string cacheDir = "";

    /// <summary>"" when the app follows the device's language, else the code the player picked.</summary>
    public static string Chosen(Context c) => c.GetSharedPreferences(Prefs, FileCreationMode.Private)!.GetString(PrefKey, "") ?? "";

    public static void Choose(Context c, string code)
    {
        var e = c.GetSharedPreferences(Prefs, FileCreationMode.Private)!.Edit()!;
        e.PutString(PrefKey, code);
        e.Commit();
    }

    /// <summary>Picks the language and loads its texts (built-in, then the copy last fetched from the server). Call before building the screen.</summary>
    public static void Start(Context c)
    {
        cacheDir = Path.Combine(c.FilesDir!.AbsolutePath, "localization");
        string chosen = Chosen(c);
        Language = chosen.Length > 0 && Languages.Any(l => l.Code == chosen) ? chosen : DeviceLanguage();
        var t = new Dictionary<string, string>();
        if (Language != "en")
        {
            try
            {
                using var s = c.Assets!.Open($"localization/{Language}.json");
                using var r = new StreamReader(s);
                Merge(t, r.ReadToEnd());
            }
            catch { }
            try { if (File.Exists(CachePath)) Merge(t, File.ReadAllText(CachePath)); } catch { }
        }
        table = t;
    }

    private static string CachePath => Path.Combine(cacheDir, Language + ".json");

    /// <summary>The device's language mapped to one the game ships: any Portuguese is pt-BR, any Chinese zh-CN; others English.</summary>
    public static string DeviceLanguage()
    {
        var l = Java.Util.Locale.Default;
        string lang = (l.Language ?? "en").ToLowerInvariant();
        if (lang == "pt") return "pt-BR";
        if (lang == "zh") return "zh-CN";
        if (lang == "iw") lang = "he";
        return Languages.Any(x => x.Code == lang) ? lang : "en";
    }

    public static string NameOf(string code) => Languages.FirstOrDefault(l => l.Code == code).Name ?? code;

    /// <summary>The text in the player's language, with {name} filled in.</summary>
    public static string T(string key, string english, params (string name, object? value)[] args) =>
        Fill(table.TryGetValue(key, out var s) && s.Length > 0 ? s : english, args, shown: true);

    /// <summary>The English text with {name} filled in (for the log).</summary>
    public static string En(string english, params (string name, object? value)[] args) => Fill(english, args, shown: false);

    internal static string Fill(string text, (string name, object? value)[] args, bool shown)
    {
        foreach (var (name, value) in args)
            text = text.Replace("{" + name + "}", value is LocText lt ? (shown ? lt.Shown : lt.English) : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "");
        return text;
    }

    private static void Merge(Dictionary<string, string> into, string json)
    {
        using var doc = JsonDocument.Parse(json);
        foreach (var p in doc.RootElement.EnumerateObject())
            if (p.Value.ValueKind == JsonValueKind.String && (p.Value.GetString() ?? "").Length > 0) into[p.Name] = p.Value.GetString()!;
    }

    /// <summary>
    /// Looks on the server for a newer copy of this language's texts (once per start, in the background). Returns true
    /// when one was fetched; the screen then shows it after its next refresh. Never throws; English needs nothing.
    /// </summary>
    public static async Task<bool> Refresh(Action<string> log)
    {
        if (Language == "en") return false;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("WarpforgePatcher/1");
            string server = ModFiles.ServerBase();
            string want;
            using (var index = JsonDocument.Parse(await http.GetStringAsync(server + "/api/v1/content/files/localization/index.json")))
            {
                if (!index.RootElement.TryGetProperty("patcher", out var area) || !area.TryGetProperty(Language, out var sha)) return false;
                want = (sha.GetString() ?? "").ToLowerInvariant();
            }
            if (want.Length != 64) return false;
            if (File.Exists(CachePath) && Sha(File.ReadAllBytes(CachePath)) == want) return false;
            byte[] data = await http.GetByteArrayAsync(server + $"/api/v1/content/files/localization/patcher/{Language}.json");
            if (Sha(data) != want) { log($"Translations ({Language}) from the server do not match their checksum; not used"); return false; }
            var t = new Dictionary<string, string>(table);
            Merge(t, Encoding.UTF8.GetString(data));
            Directory.CreateDirectory(cacheDir);
            File.WriteAllBytes(CachePath, data);
            table = t;
            log($"Translations ({Language}) updated from the server");
            return true;
        }
        catch (Exception e)
        {
            log($"Translations ({Language}): the server's copy could not be checked ({e.Message}); using the built-in one");
            return false;
        }
    }

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
}

/// <summary>A text that is shown translated and logged in English (for messages passed around before they are shown).</summary>
internal sealed class LocText(string key, string english, params (string name, object? value)[] args)
{
    public string Shown => Loc.T(key, english, args);
    public string English => Loc.En(english, args);
    public override string ToString() => Shown;
}
