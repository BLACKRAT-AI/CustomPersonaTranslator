using System;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using CPT.Core.Models;

namespace CPT.Core.Personas;

// Self-contained persona bundle:
//   persona.json           — the Persona JSON (paths stripped to relative)
//   voice/sample<ext>      — voice sample file (optional)
//   image/image<ext>       — persona image (optional)
//
// Recipients get a fully working persona without needing to re-import the
// YouTube source, re-run the LLM analysis, or download the voice sample again.
public static class PersonaPackage
{
    public const string Extension = ".cptpersona";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions PackageJson = new() { PropertyNameCaseInsensitive = true };

    public static void Export(Persona persona, string destZipPath)
    {
        if (File.Exists(destZipPath)) File.Delete(destZipPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destZipPath)!);

        // Clone the persona so we can strip absolute paths without mutating
        // the caller's instance.
        var copy = JsonSerializer.Deserialize<Persona>(JsonSerializer.Serialize(persona))!;
        string? voiceSrc = null;
        string? imageSrc = null;
        string? modelSrc = null;

        if (!string.IsNullOrEmpty(copy.Voice.VoiceSampleFile) && File.Exists(copy.Voice.VoiceSampleFile))
        {
            voiceSrc = copy.Voice.VoiceSampleFile;
            copy.Voice.VoiceSampleFile = "voice/sample" + Path.GetExtension(voiceSrc);
        }
        if (!string.IsNullOrEmpty(copy.Visual.ImageFile) && File.Exists(copy.Visual.ImageFile))
        {
            imageSrc = copy.Visual.ImageFile;
            copy.Visual.ImageFile = "image/image" + Path.GetExtension(imageSrc);
        }

        if (!string.IsNullOrEmpty(copy.Visual.ModelFile))
        {
            if (!File.Exists(copy.Visual.ModelFile)) throw new FileNotFoundException("Persona 3D model is missing.", copy.Visual.ModelFile);
            modelSrc = copy.Visual.ModelFile;
            copy.Visual.ModelFile = "model/avatar.glb";
        }

        using var fs = new FileStream(destZipPath, FileMode.Create);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

        var jsonEntry = zip.CreateEntry("persona.json");
        using (var w = new StreamWriter(jsonEntry.Open()))
            w.Write(JsonSerializer.Serialize(copy, Json));

        if (voiceSrc is not null)
        {
            var ve = zip.CreateEntry("voice/sample" + Path.GetExtension(voiceSrc), CompressionLevel.Optimal);
            using var es = ve.Open();
            using var ss = File.OpenRead(voiceSrc);
            ss.CopyTo(es);
        }
        if (modelSrc is not null)
        {
            var entry = zip.CreateEntry("model/avatar.glb", CompressionLevel.Optimal);
            using var destination = entry.Open();
            using var source = File.OpenRead(modelSrc);
            source.CopyTo(destination);
        }
        if (imageSrc is not null)
        {
            var ie = zip.CreateEntry("image/image" + Path.GetExtension(imageSrc), CompressionLevel.Optimal);
            using var es = ie.Open();
            using var ss = File.OpenRead(imageSrc);
            ss.CopyTo(es);
        }
    }

    // Returns the imported persona (with absolute local paths restored).
    // If a persona with the same Id already exists, appends a numeric suffix.
    public static Persona Import(string zipPath, PersonaStore store)
    {
        using var fs = new FileStream(zipPath, FileMode.Open, FileAccess.Read);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);

        var jsonEntry = zip.GetEntry("persona.json")
            ?? throw new InvalidDataException("Package missing persona.json");
        Persona persona;
        using (var r = new StreamReader(jsonEntry.Open()))
        {
            var text = r.ReadToEnd();
            persona = JsonSerializer.Deserialize<Persona>(text,
                PackageJson)
                ?? throw new InvalidDataException("persona.json is malformed");
        }

        // Avoid clobbering an existing persona with the same id.
        if (!string.IsNullOrEmpty(persona.Id))
        {
            var existingIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in store.LoadAll()) existingIds.Add(p.Id);
            if (existingIds.Contains(persona.Id))
            {
                var baseName = persona.Id; int n = 2;
                while (existingIds.Contains($"{baseName}_{n}")) n++;
                persona.Id = $"{baseName}_{n}";
                persona.Name = persona.Name + $" ({n})";
            }
        }

        // Extract voice sample → samples/{id}.{ext}
        Directory.CreateDirectory(store.SamplesDir);
        var voiceEntry = FindEntry(zip, "voice/");
        if (voiceEntry is not null)
        {
            var dest = Path.Combine(store.SamplesDir, persona.Id + Path.GetExtension(voiceEntry.FullName));
            using (var s = voiceEntry.Open())
            using (var fout = File.Create(dest))
                s.CopyTo(fout);
            persona.Voice.VoiceSampleFile = dest;
        }
        else
        {
            persona.Voice.VoiceSampleFile = null;
        }

        // Extract image (if any) → samples/{id}_image.{ext}
        var imageEntry = FindEntry(zip, "image/");
        if (imageEntry is not null)
        {
            var dest = Path.Combine(store.SamplesDir, persona.Id + "_image" + Path.GetExtension(imageEntry.FullName));
            using (var s = imageEntry.Open())
            using (var fout = File.Create(dest))
                s.CopyTo(fout);
            persona.Visual.ImageFile = dest;
        }
        else
        {
            persona.Visual.ImageFile = null;
        }

        var modelEntry = zip.GetEntry("model/avatar.glb");
        persona.Visual.ModelFile = null;
        if (modelEntry is not null)
        {
            var folder = Path.Combine(store.Dir, "models");
            Directory.CreateDirectory(folder);
            var destination = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".glb");
            using (var source = modelEntry.Open())
            using (var output = File.Create(destination)) source.CopyTo(output);
            persona.Visual.ModelFile = destination;
        }

        store.Save(persona);
        return persona;
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive zip, string prefix)
    {
        foreach (var e in zip.Entries)
            if (e.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && e.Length > 0)
                return e;
        return null;
    }
}
