using System.Reflection;
using System.Text.Json;

namespace Relic.Core.Util;

/// <summary>
/// User-facing strings for the C# side. The same flat JSON dictionaries the UI uses
/// (<c>ui/lang/&lt;code&gt;.json</c>) are read here: English is embedded in this assembly as the ultimate
/// fallback (so the uninstaller, the --play process and the spikes work without a ui/ folder next
/// to them), and the selected language is overlaid from <c>&lt;AppContext.BaseDirectory&gt;/ui/lang</c>.
/// A missing key falls back to English, then to the key itself — this class never throws or logs.
/// Placeholders are <c>{name}</c>, filled from an anonymous object or dictionary.
/// </summary>
public static class L
{
    private static readonly Dictionary<string, string> En = LoadEmbeddedEnglish();
    private static volatile Dictionary<string, string> _cur = En;

    /// <summary>Active language code ("en" until <see cref="SetLanguage"/> succeeds).</summary>
    public static string Code { get; private set; } = "en";

    /// <summary>Select a language. Unknown/unavailable codes silently keep English.</summary>
    public static void SetLanguage(string? code)
    {
        code = (code ?? "").Trim().ToLowerInvariant();
        if (code.Length == 0 || code == "en") { _cur = En; Code = "en"; return; }
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "ui", "lang", code + ".json");
            if (!File.Exists(path)) { _cur = En; Code = "en"; return; }
            var overlay = Parse(File.ReadAllText(path));
            var merged = new Dictionary<string, string>(En, StringComparer.Ordinal);
            foreach (var kv in overlay) merged[kv.Key] = kv.Value;
            _cur = merged;
            Code = code;
        }
        catch
        {
            _cur = En; Code = "en";
        }
    }

    /// <summary>Alias of <see cref="SetLanguage"/> for the process entry points.</summary>
    public static void Init(string? code) => SetLanguage(code);

    public static string T(string key)
    {
        var cur = _cur;
        if (cur.TryGetValue(key, out var s)) return s;
        if (!ReferenceEquals(cur, En) && En.TryGetValue(key, out s)) return s;
        return key;
    }

    /// <summary>Translate and fill <c>{name}</c> placeholders from an anonymous object
    /// (<c>new { name = "x" }</c>) or an <see cref="IDictionary{TKey,TValue}"/>.</summary>
    public static string T(string key, object? vars)
    {
        string s = T(key);
        if (vars is null) return s;
        if (vars is IDictionary<string, object?> dict)
            return Fill(s, k => dict.TryGetValue(k, out var v) ? v : null, k => dict.ContainsKey(k));
        var props = vars.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
        return Fill(s, k => props.FirstOrDefault(p => p.Name == k)?.GetValue(vars),
                       k => props.Any(p => p.Name == k));
    }

    /// <summary>True when the active language (or English) knows the key.</summary>
    public static bool Has(string key) => _cur.ContainsKey(key) || En.ContainsKey(key);

    private static string Fill(string s, Func<string, object?> get, Func<string, bool> has)
    {
        if (s.IndexOf('{') < 0) return s;
        var sb = new System.Text.StringBuilder(s.Length + 16);
        int i = 0;
        while (i < s.Length)
        {
            int open = s.IndexOf('{', i);
            if (open < 0) { sb.Append(s, i, s.Length - i); break; }
            int close = s.IndexOf('}', open + 1);
            if (close < 0) { sb.Append(s, i, s.Length - i); break; }
            string name = s.Substring(open + 1, close - open - 1);
            sb.Append(s, i, open - i);
            if (IsIdent(name) && has(name)) sb.Append(Convert.ToString(get(name), System.Globalization.CultureInfo.InvariantCulture));
            else sb.Append('{').Append(name).Append('}');
            i = close + 1;
        }
        return sb.ToString();
    }

    private static bool IsIdent(string n)
    {
        if (n.Length == 0) return false;
        foreach (char c in n) if (!(char.IsLetterOrDigit(c) || c == '_')) return false;
        return true;
    }

    private static Dictionary<string, string> LoadEmbeddedEnglish()
    {
        try
        {
            using var s = typeof(L).Assembly.GetManifestResourceStream("Relic.Core.lang.en.json");
            if (s is null) return new Dictionary<string, string>(StringComparer.Ordinal);
            using var r = new StreamReader(s);
            return Parse(r.ReadToEnd());
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static Dictionary<string, string> Parse(string json)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return d;
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            if (p.Value.ValueKind == JsonValueKind.String) d[p.Name] = p.Value.GetString() ?? "";
        }
        return d;
    }
}
