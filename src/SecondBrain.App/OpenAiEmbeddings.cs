using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SecondBrain.Core;

namespace SecondBrain.App;

internal sealed class OpenAiEmbeddings(Func<string> loadKey) : IEmbeddingProvider, IDisposable
{
    public const string Model = "text-embedding-3-small";
    public const int Dimensions = 512;
    private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };
    public async Task<float[][]> Embed(IReadOnlyList<string> text, CancellationToken cancellation)
    {
        if (text.Count is < 1 or > 32 || text.Any(t => t.Length is < 1 or > 8000)) throw new InvalidDataException("Invalid embedding batch size.");
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/embeddings");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", loadKey());
        request.Content = new StringContent(JsonSerializer.Serialize(new { model = Model, dimensions = Dimensions, input = text, encoding_format = "float" }), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, cancellation);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Semantic search unavailable (HTTP " + (int)response.StatusCode + "). Keyword search remains available.");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
        var result = new float[text.Count][];
        foreach (var item in document.RootElement.GetProperty("data").EnumerateArray())
        {
            var index = item.GetProperty("index").GetInt32(); var vector = item.GetProperty("embedding").EnumerateArray().Select(v => v.GetSingle()).ToArray();
            if (index < 0 || index >= result.Length || result[index] is not null || vector.Length != Dimensions || vector.Any(v => !float.IsFinite(v))) throw new InvalidDataException("Invalid embedding response.");
            result[index] = vector;
        }
        if (result.Any(v => v is null)) throw new InvalidDataException("Missing embedding vector.");
        return result;
    }
    public void Dispose() => http.Dispose();
}
