using System.IO;
using System.Net.WebSockets;
using System.Text;
using SecondBrain.Core;

namespace SecondBrain.App;

internal interface ITranscriptConnection : IDisposable
{
    Task Connect(int sampleRate, string key, CancellationToken ct);
    Task Send(byte[] pcm, CancellationToken ct);
    Task Control(string type, CancellationToken ct);
    Task<SpeechSegment?> Receive(CancellationToken ct);
}

internal sealed class TranscriptConnection : ITranscriptConnection
{
    private readonly ClientWebSocket socket = new();
    public async Task Connect(int sampleRate, string key, CancellationToken ct)
    {
        socket.Options.SetRequestHeader("Authorization", "Token " + key);
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);
        socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(10);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(12));
        await socket.ConnectAsync(new Uri($"wss://api.deepgram.com/v1/listen?model=nova-3&language=en&encoding=linear16&sample_rate={sampleRate}&channels=1&interim_results=true&endpointing=300&punctuate=true"), timeout.Token);
    }
    public Task Send(byte[] pcm, CancellationToken ct) => SendFrame(pcm, WebSocketMessageType.Binary, ct);
    public Task Control(string type, CancellationToken ct) => SendFrame(Encoding.UTF8.GetBytes("{\"type\":\"" + type + "\"}"), WebSocketMessageType.Text, ct);
    private async Task SendFrame(byte[] bytes, WebSocketMessageType type, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await socket.SendAsync(bytes.AsMemory(), type, true, timeout.Token);
    }
    public async Task<SpeechSegment?> Receive(CancellationToken ct)
    {
        var bytes = new byte[8192]; using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(bytes.AsMemory(), ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text) throw new InvalidDataException("Unexpected transcription frame.");
            message.Write(bytes, 0, result.Count);
            if (message.Length > 512_000) throw new InvalidDataException("Transcription response too large.");
            if (!result.EndOfMessage) continue;
            var segment = DeepgramProtocol.Parse(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length));
            message.SetLength(0);
            if (segment is not null) return segment;
        }
    }
    public void Dispose() { socket.Abort(); socket.Dispose(); }
}
