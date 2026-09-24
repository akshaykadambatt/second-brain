using System.Text.Json;

namespace SecondBrain.Core;

public sealed record ResponseTextEvent(string Text = "", bool Complete = false);

// One parser per HTTP stream. Network sequence IDs are independent of reader
// block IDs. Conflicting duplicates, gaps and foreign responses fail visibly.
public sealed class ResponsesEvents
{
    private readonly Dictionary<int, string> seen = [];
    private int next;
    private string? responseId;
    public bool Completed { get; private set; }
    public ResponseTextEvent? Read(string json)
    {
        if (Completed) return null;
        using var doc = JsonDocument.Parse(json); var root = doc.RootElement;
        var type = root.GetProperty("type").GetString();
        if (type is "error" or "response.failed" or "response.incomplete" or "response.refusal.delta")
        {
            var failure = root.TryGetProperty("response", out var failed) ? failed : root;
            var code = failure.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var value) ? value.GetString()
                : failure.TryGetProperty("code", out var directCode) ? directCode.GetString() : null;
            var reason = code switch
            {
                "insufficient_quota" or "credit_balance_exhausted" => "OpenAI API credits are exhausted or unavailable. Add API credits and check project limits, then retry.",
                "rate_limit_exceeded" => "OpenAI rate limit reached. Retry later.",
                "model_not_found" => "The configured OpenAI model is unavailable to this project. Check the model setting.",
                "invalid_api_key" => "OpenAI rejected the API key.",
                _ => "Provider could not complete this answer" + (code is not null && System.Text.RegularExpressions.Regex.IsMatch(code, "^[a-z_]{1,60}$") ? " (" + code + ")" : "") + "."
            };
            throw new InvalidOperationException(reason + " Readable earlier paragraphs are retained.");
        }
        if (root.TryGetProperty("sequence_number", out var number))
        {
            var sequence = number.GetInt32();
            if (seen.TryGetValue(sequence, out var prior))
            { if (prior != json) throw new InvalidDataException("Conflicting provider event."); return null; }
            if (sequence != next || seen.Count >= 12000) throw new InvalidDataException("Missing or excessive provider events.");
            seen.Add(sequence, json); next++;
        }
        if (root.TryGetProperty("response", out var response) && response.TryGetProperty("id", out var id))
        {
            var value = id.GetString();
            if (responseId is not null && responseId != value) throw new InvalidDataException("Response identity changed during streaming.");
            responseId = value;
        }
        if (type == "response.output_text.delta") return new(root.GetProperty("delta").GetString() ?? "");
        if (type == "response.completed") { Completed = true; return new(Complete: true); }
        return null;
    }
}
