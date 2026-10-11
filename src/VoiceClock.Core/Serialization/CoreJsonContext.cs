using System.Text.Json.Serialization;
using VoiceClock.Core.Time;

namespace VoiceClock.Core.Serialization;

/// <summary>
/// Compile-time JSON metadata for every document the app reads or writes. Source generation keeps
/// serialization working in trimmed/AOT Android release builds, where reflection-based JSON fails.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(SpeechRulesDocument))]
internal sealed partial class CoreJsonContext : JsonSerializerContext
{
}
