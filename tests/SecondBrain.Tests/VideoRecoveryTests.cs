using System.Buffers.Binary;
using System.Text;
using SecondBrain.Core;

internal static class VideoRecoveryTests
{
    public static void Run(Action<string, Action> test, Action<bool, string> check, Func<string, string> folder)
    {
        byte[] Box(string type, int payload = 12) { var bytes = new byte[payload + 8]; BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)bytes.Length); Encoding.ASCII.GetBytes(type).CopyTo(bytes, 4); return bytes; }
        var prefix = Box("ftyp").Concat(Box("moov")).Concat(Box("moof")).Concat(Box("mdat", 80)).ToArray();
        test("Video recovery retains complete fragment pairs and never edits the interrupted original", () =>
        {
            var dir = folder("video-recovery"); var path = Path.Combine(dir, "video-fixture.mp4");
            var bytes = prefix.Concat(Box("moof")).Concat(Box("mdat", 40).Take(19)).ToArray(); File.WriteAllBytes(path, bytes);
            File.WriteAllText(Path.ChangeExtension(path, ".json"), "{\"Schema\":1,\"State\":\"Recording\",\"SessionStartSeconds\":12.5,\"Frames\":800}");
            var recovered = VideoRecovery.Recover(dir, Path.GetFileName(path));
            check(File.ReadAllBytes(recovered.Path).SequenceEqual(prefix) && File.ReadAllBytes(path).SequenceEqual(bytes), "Recovery changed original or included partial fragment");
            check(recovered.Prefix.Fragments == 1 && recovered.Prefix.DiscardedBytes == bytes.Length - prefix.Length, "Discarded tail not reported");
            var metadata = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.ChangeExtension(recovered.Path, ".json")))!;
            check(metadata["SessionStartSeconds"]!.GetValue<double>() == 12.5 && metadata["State"]!.GetValue<string>() == "RecoveredPartial" && metadata["Frames"] is null, "Recovered timing provenance is misleading");
        });
        test("Video recovery refuses active clips, escaping paths and files with no complete initialization and fragment", () =>
        {
            var dir = folder("video-reject");var path = Path.Combine(dir, "video-fixture.mp4");File.WriteAllBytes(path, prefix);
            void Reject(Action action) { var rejected = false; try { action(); } catch (Exception ex) when (ex is IOException or InvalidDataException) { rejected = true; } check(rejected, "Unsafe video recovery accepted"); }
            using (var held = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) Reject(() => VideoRecovery.Recover(dir, Path.GetFileName(path)));
            Reject(() => VideoRecovery.Recover(dir, "../video-fixture.mp4"));
            Reject(() => VideoRecovery.Inspect(new MemoryStream(Box("moof").Concat(Box("mdat")).ToArray())));
            var extended = Box("moof", 8);BinaryPrimitives.WriteUInt32BigEndian(extended, 1);BinaryPrimitives.WriteUInt64BigEndian(extended.AsSpan(8), ulong.MaxValue);
            var inspected = VideoRecovery.Inspect(new MemoryStream(prefix.Concat(extended).ToArray()));
            check(inspected.Bytes == prefix.Length, "Oversized extended box overflowed prefix bounds");
        });
        test("Backups preserve original and recovered video bytes and provenance", () =>
        {
            var dir=folder("video-backup");var data=Path.Combine(dir,"data");var vault=Path.Combine(dir,"Vault");Directory.CreateDirectory(data);Directory.CreateDirectory(vault);
            File.WriteAllText(Path.Combine(data,"settings.json"),"{}");File.WriteAllText(Path.Combine(vault,"Home.md"),"# Test");var exe=Path.Combine(dir,"SecondBrain.exe");File.WriteAllText(exe,"fixture");
            var recording=Path.Combine(data,"recordings","fixture");Directory.CreateDirectory(recording);var path=Path.Combine(recording,"video-fixture.mp4");File.WriteAllBytes(path,prefix);
            var recovered=VideoRecovery.Recover(recording,Path.GetFileName(path));var backup=LocalBackup.Create(data,vault,exe,Path.Combine(dir,"backups"));var restored=LocalBackup.Restore(backup,Path.Combine(dir,"restored"));
            check(File.ReadAllBytes(Path.Combine(restored,"data/recordings/fixture",Path.GetFileName(recovered.Path))).SequenceEqual(prefix),"Recovered video missing from restore");
            check(File.ReadAllBytes(Path.Combine(restored,"data/recordings/fixture/video-fixture.mp4")).SequenceEqual(prefix),"Original video missing from restore");
        });
    }
}
