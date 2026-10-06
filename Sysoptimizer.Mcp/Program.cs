using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Sysoptimizer.Mcp;

// Minimal MCP server over stdio: newline-delimited JSON-RPC 2.0, tools only. Hand-rolled on
// System.Text.Json instead of an SDK — the protocol surface used here is four methods.
var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

string? line;
while ((line = input.ReadLine()) != null)
{
    if (string.IsNullOrWhiteSpace(line)) continue;
    JsonObject? message;
    try { message = JsonNode.Parse(line) as JsonObject; }
    catch { Send(Error(null, -32700, "Parse error")); continue; }
    if (message == null) continue;

    var id = message["id"]?.DeepClone();
    if (id == null) continue; // a notification (e.g. notifications/initialized) — never answered
    string method = (string?)message["method"] ?? "";
    var parameters = message["params"] as JsonObject;

    try
    {
        JsonNode? result = method switch
        {
            "initialize" => new JsonObject
            {
                // Echo the client's version: every revision so far handles a tools-only server the same way.
                ["protocolVersion"] = (string?)parameters?["protocolVersion"] ?? "2025-06-18",
                ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                ["serverInfo"] = new JsonObject { ["name"] = "sysoptimizer", ["version"] = version },
                ["instructions"] = "Reads the resource history Sysoptimizer records on this PC (CPU, memory, GPU, temperatures, and the top apps every 10 seconds, kept 30 days). Use it to answer what was using the machine at a given time. Times are the PC's local time.",
            },
            "ping" => new JsonObject(),
            "tools/list" => new JsonObject { ["tools"] = Tools.Definitions() },
            "tools/call" => Tools.Call((string?)parameters?["name"] ?? "", parameters?["arguments"] as JsonObject ?? new JsonObject()),
            _ => null,
        };
        Send(result == null
            ? Error(id, -32601, $"Method not found: {method}")
            : new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result });
    }
    catch (Exception ex)
    {
        Send(Error(id, -32603, ex.Message));
    }
}

void Send(JsonObject message) => output.WriteLine(message.ToJsonString());

static JsonObject Error(JsonNode? id, int code, string text) => new()
{
    ["jsonrpc"] = "2.0",
    ["id"] = id,
    ["error"] = new JsonObject { ["code"] = code, ["message"] = text },
};
