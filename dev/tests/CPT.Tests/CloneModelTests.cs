using System.Text.Json;
using CPT.Core.Models;
using Xunit;
namespace CPT.Tests;
public class CloneModelTests
{
    [Fact]
    public void Existing_voice_keeps_original_engine_until_user_changes_it()
    {
        var voice = JsonSerializer.Deserialize<VoiceConfig>("{\"Engine\":\"chatterbox\",\"VoiceSampleFile\":\"selected.wav\"}")!;
        Assert.Equal("original", voice.CloneModel);
        Assert.Equal("selected.wav", voice.VoiceSampleFile);
    }
    [Fact]
    public void Turbo_selection_roundtrips_without_replacing_selected_reference()
    {
        var persona = new Persona { Voice = new() { Engine = "chatterbox", CloneModel = "turbo", VoiceSampleFile = "my-clips.wav" } };
        var loaded = JsonSerializer.Deserialize<Persona>(JsonSerializer.Serialize(persona))!;
        Assert.Equal("turbo", loaded.Voice.CloneModel);
        Assert.Equal("chatterbox", loaded.Voice.Engine);
        Assert.Equal("my-clips.wav", loaded.Voice.VoiceSampleFile);
    }
}
