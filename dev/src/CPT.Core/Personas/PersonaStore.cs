using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CPT.Core.Models;

namespace CPT.Core.Personas;

public sealed class PersonaStore
{
    private readonly string _dir;
    private static readonly JsonSerializerOptions WriteJson = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions ReadJson = new() { PropertyNameCaseInsensitive = true };

    public PersonaStore(string? dir = null)
    {
        _dir = dir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CustomPersonaTranslator", "personas");
        Directory.CreateDirectory(_dir);
    }

    public string Dir => _dir;
    public string SamplesDir => Path.Combine(Path.GetDirectoryName(_dir)!, "samples");

    // Copies a voice-sample file into the persistent samples dir and returns
    // the persistent path. Use this when the source sits in %TEMP% (e.g.
    // freshly downloaded by YoutubeImporter) so it survives temp cleanup.
    public string PersistVoiceSample(string personaId, string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            throw new FileNotFoundException("voice sample not found", sourcePath);
        Directory.CreateDirectory(SamplesDir);
        var ext = Path.GetExtension(sourcePath);
        var dest = Path.Combine(SamplesDir, $"{personaId}{ext}");
        // If it's already inside samplesDir, leave it.
        var src = Path.GetFullPath(sourcePath);
        var samples = Path.GetFullPath(SamplesDir);
        if (src.StartsWith(samples, StringComparison.OrdinalIgnoreCase)) return src;
        File.Copy(sourcePath, dest, overwrite: true);
        return dest;
    }

    public IEnumerable<Persona> LoadAll()
    {
        foreach (var f in Directory.EnumerateFiles(_dir, "*.json"))
        {
            Persona? p = null;
            try { p = JsonSerializer.Deserialize<Persona>(File.ReadAllText(f), ReadJson); } catch { }
            if (p is not null) yield return p;
        }
    }

    public void Save(Persona p)
    {
        if (string.IsNullOrEmpty(p.Id))
            p.Id = Sanitize(p.Name.Length > 0 ? p.Name : Guid.NewGuid().ToString("N")[..8]);
        File.WriteAllText(Path.Combine(_dir, p.Id + ".json"), JsonSerializer.Serialize(p, WriteJson));
    }

    public Persona? Get(string id) => LoadAll().FirstOrDefault(p => p.Id == id);

    private static string Sanitize(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        var chars = s.Where(c => !bad.Contains(c) && c != ' ').ToArray();
        return new string(chars).ToLowerInvariant();
    }
}
