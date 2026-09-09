using System.Text.Json.Serialization;

namespace CPT.Core.Models;

// All inbound messages from host adapters and outbound replies share an envelope
// `{type, ...}`. We keep the discriminator loose and parse by inspection in IpcServer.

public sealed class InboundMessage
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("adapter")] public string? Adapter { get; set; }
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("state")] public string? State { get; set; }
}

public sealed class OutboundInject
{
    [JsonPropertyName("type")] public string Type { get; } = "inject";
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("submit")] public bool Submit { get; set; } = true;
}

public sealed class OutboundHologramEvent
{
    [JsonPropertyName("type")] public string Type { get; set; } = ""; // appear|speaking|listening|sending|hide|transcript|level
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("level")] public float? Level { get; set; }
    [JsonPropertyName("persona")] public string? Persona { get; set; }
}
