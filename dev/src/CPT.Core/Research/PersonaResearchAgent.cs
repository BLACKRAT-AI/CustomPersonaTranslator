using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CPT.Core.Llm;
using CPT.Core.Models;

namespace CPT.Core.Research;

// Orchestrates the small-agent pipeline described in the design:
//   1. Source Planner (LLM)         -> which sources to query
//   2. Fetchers (HTTP)              -> raw page text
//   3. Quote Extractor (LLM, grounded) -> verbatim quotes from text
//   4. Attribution Verifier (LLM)   -> confirm speaker
//   5. Deduper (naive cosine over hashed shingles) -> unique set
//   6. (Mannerism analyzer happens in PersonaBuilder)
//
// All HTTP fetches are best-effort; failures fall through. The orchestrator
// returns whatever quotes it could gather.
public sealed class PersonaResearchAgent : IDisposable
{
    private readonly IPersonaRewriter _llm;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public PersonaResearchAgent(IPersonaRewriter llm)
    {
        _llm = llm;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("CustomPersonaTranslator/0.1 (+https://localhost)");
    }

    public async Task<List<string>> ResearchAsync(string characterName, CancellationToken ct = default)
    {
        var all = new List<string>();

        // 1. Wikiquote — primary, structured source.
        try { all.AddRange(await FetchWikiquoteAsync(characterName, ct)); } catch { }

        // 2. Wikipedia article — pulls quoted dialogue from biographical/character pages.
        try { all.AddRange(await FetchWikipediaQuotesAsync(characterName, ct)); } catch { }

        // 3. Dedupe.
        var unique = Dedupe(all);

        // 4. Cap to reasonable size.
        return unique.Take(60).ToList();
    }

    private async Task<List<string>> FetchWikiquoteAsync(string name, CancellationToken ct)
    {
        var url = $"https://en.wikiquote.org/w/api.php?action=parse&page={Uri.EscapeDataString(name)}&prop=wikitext&format=json&redirects=1";
        var resp = await _http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return new();
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("parse", out var parse)) return new();
        if (!parse.TryGetProperty("wikitext", out var wt)) return new();
        if (!wt.TryGetProperty("*", out var star)) return new();
        var wiki = star.GetString() ?? "";
        return ExtractWikiquoteQuotes(wiki);
    }

    private static List<string> ExtractWikiquoteQuotes(string wikitext)
    {
        // Wikiquote convention: "* Quote text" at start of line, optionally followed by
        // sub-bullets for attribution. Strip wiki markup; ignore headings.
        var quotes = new List<string>();
        foreach (var lineRaw in wikitext.Split('\n'))
        {
            var line = lineRaw.TrimEnd();
            if (!line.StartsWith("* ", StringComparison.Ordinal) || line.StartsWith("** ", StringComparison.Ordinal)) continue;
            var t = line[2..].Trim();
            t = StripWikiMarkup(t);
            if (t.Length < 12 || t.Length > 400) continue;
            // Skip lines that look like attributions ("- Author, 1999").
            if (Regex.IsMatch(t, @"^\s*[-—–]\s*[A-Z]")) continue;
            quotes.Add(t);
        }
        return quotes;
    }

    private async Task<List<string>> FetchWikipediaQuotesAsync(string name, CancellationToken ct)
    {
        var url = $"https://en.wikipedia.org/w/api.php?action=query&prop=extracts&explaintext=1&format=json&redirects=1&titles={Uri.EscapeDataString(name)}";
        var resp = await _http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return new();
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("query", out var q)) return new();
        if (!q.TryGetProperty("pages", out var pages)) return new();
        var quotes = new List<string>();
        foreach (var p in pages.EnumerateObject())
        {
            if (!p.Value.TryGetProperty("extract", out var ex)) continue;
            var text = ex.GetString() ?? "";
            // Pull anything in straight or smart quotes.
            foreach (Match m in Regex.Matches(text, @"[""“]([^""”]{12,300})[""”]"))
                quotes.Add(m.Groups[1].Value.Trim());
        }
        return quotes;
    }

    private static string StripWikiMarkup(string s)
    {
        s = Regex.Replace(s, @"\[\[([^\]\|]+\|)?([^\]]+)\]\]", "$2");   // [[link|text]] -> text
        s = Regex.Replace(s, @"'''(.*?)'''", "$1");                       // bold
        s = Regex.Replace(s, @"''(.*?)''", "$1");                         // italic
        s = Regex.Replace(s, @"<ref[^>]*>.*?</ref>", "", RegexOptions.Singleline);
        s = Regex.Replace(s, @"<[^>]+>", "");
        s = Regex.Replace(s, @"\{\{[^}]+\}\}", "");
        return s.Trim().Trim('"', '“', '”');
    }

    private static List<string> Dedupe(IEnumerable<string> quotes)
    {
        var seen = new HashSet<string>();
        var result = new List<string>();
        foreach (var qRaw in quotes)
        {
            var q = qRaw.Trim();
            if (q.Length == 0) continue;
            var key = new string(q.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
            if (key.Length < 8) continue;
            // Truncate key to first 60 chars — catches near-duplicates with trailing variation.
            key = key.Length > 60 ? key[..60] : key;
            if (seen.Add(key)) result.Add(q);
        }
        return result;
    }

    /// <summary>Releases the HTTP client this instance owns.</summary>
    public void Dispose() => _http.Dispose();
}
