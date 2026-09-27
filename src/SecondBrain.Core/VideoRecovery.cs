using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace SecondBrain.Core;

public sealed record VideoPrefix(long Bytes, int Fragments, long DiscardedBytes);
public sealed record RecoveredVideo(string Path, VideoPrefix Prefix);

// Only complete fMP4 fragment pairs are retained. Never rewrite an original clip.
public static class VideoRecovery
{
    public static VideoPrefix Inspect(Stream stream)
    {
        if (!stream.CanSeek) throw new ArgumentException("A seekable video is required.");
        stream.Position = 0;
        var header = new byte[16]; var ftyp = false; var moov = false; var pending = false;
        long retained = 0; var fragments = 0; var boxes = 0;
        while (stream.Length - stream.Position >= 8 && ++boxes <= 1_000_000)
        {
            var start = stream.Position; stream.ReadExactly(header.AsSpan(0, 8));
            ulong size = BinaryPrimitives.ReadUInt32BigEndian(header); var type = Encoding.ASCII.GetString(header, 4, 4); var head = 8;
            if (size == 1)
            {
                if (stream.Length - stream.Position < 8) break;
                stream.ReadExactly(header.AsSpan(8, 8)); size = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8)); head = 16;
            }
            // An open-ended box is not evidence that a crash left a complete fragment.
            if (size < (ulong)head || size > (ulong)(stream.Length - start)) break;
            if (type == "ftyp" && start == 0) ftyp = true;
            else if (type == "moov" && ftyp && !pending && fragments == 0) moov = true;
            else if (type == "moof") { if (!ftyp || !moov || pending) break; pending = true; }
            else if (type == "mdat")
            {
                if (!pending) break;
                pending = false; fragments++; retained = start + (long)size;
            }
            else if (pending) break;
            stream.Position = start + (long)size;
        }
        if (fragments == 0) throw new InvalidDataException("No complete video fragments found. The original is preserved; the final fragment may not have reached disk.");
        return new(retained, fragments, stream.Length - retained);
    }

    public static RecoveredVideo Recover(string directory, string file)
    {
        directory = LocalBackup.Root(directory);
        if (file != Path.GetFileName(file) || !file.StartsWith("video-", StringComparison.Ordinal) || !file.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Select a video file inside this meeting.");
        var source = LocalBackup.Root(Path.Combine(directory, file));
        using var sessionLock = new FileStream(LocalBackup.Root(Path.Combine(directory, "recording.lock")), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var clipLock = new FileStream(LocalBackup.Root(source + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        var prefix = Inspect(input); input.Position = 0; var hash = Convert.ToHexString(SHA256.HashData(input)); input.Position = 0;
        var destination = Path.Combine(directory, "video-recovered-" + Guid.NewGuid().ToString("N") + ".mp4"); var partial = destination + ".partial";
        try
        {
            using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[131072]; var remaining = prefix.Bytes;
                while (remaining > 0) { var count = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining)); if (count == 0) throw new IOException("Video changed during recovery."); output.Write(buffer, 0, count); remaining -= count; }
                output.Flush(true);
            }
            JsonObject metadata = new(); var metadataPath = LocalBackup.Root(Path.ChangeExtension(source, ".json"));
            if (File.Exists(metadataPath) && new FileInfo(metadataPath).Length <= 1_000_000)
            { try { metadata = JsonNode.Parse(File.ReadAllText(metadataPath)) as JsonObject ?? new(); } catch (System.Text.Json.JsonException) { } }
            metadata["Schema"] = 1; metadata["State"] = "RecoveredPartial"; metadata["File"] = Path.GetFileName(destination);
            metadata["RecoveredFrom"] = file; metadata["OriginalSha256"] = hash; metadata["RetainedBytes"] = prefix.Bytes; metadata["DiscardedBytes"] = prefix.DiscardedBytes;
            // Last known encoder counts can exceed the prefix on disk; do not claim them as recovered duration.
            metadata["Frames"] = null; metadata["DurationSeconds"] = null; metadata["AudioFrames"] = null;
            metadata["RecoveryNotice"] = "Complete fragments copied; playback and duration need review. Original kept unchanged.";
            VaultTransaction.SaveJson(Path.ChangeExtension(destination, ".json"), metadata);
            File.Move(partial, destination);
            return new(destination, prefix);
        }
        catch { if (File.Exists(partial)) File.Delete(partial); throw; }
    }
}
