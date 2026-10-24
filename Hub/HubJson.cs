using System.Text.Json.Serialization;

namespace EmpireHub;

// Source-generated JSON for the hub's files, so they also load in trimmed builds.
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(HubSettings))]
[JsonSerializable(typeof(EmpireProject))]
[JsonSerializable(typeof(CreditsFile))]
internal sealed partial class HubJson : JsonSerializerContext;
