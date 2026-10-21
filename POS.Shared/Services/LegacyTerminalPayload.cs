using System.Text.Json;
using System.Text.Json.Nodes;

namespace POS.Shared.Services;

/// <summary>Reads pre-hierarchy local snapshots without changing their bytes or idempotency hashes.</summary>
public static class LegacyTerminalPayload
{
    public static T? Deserialize<T>(string json)
    {
        var node = JsonNode.Parse(json);
        if (node is JsonObject obj && !obj.ContainsKey("TerminalId") && obj["OutletId"] is JsonNode legacy)
            obj["TerminalId"] = legacy.DeepClone();
        return node == null ? default : node.Deserialize<T>();
    }
}
