using System.IO;
using System.Runtime.InteropServices;

namespace SecondBrain.App;

// All objects and MF lifetime belong to the calling encoder thread.
internal sealed class NativeVideoWriter : IDisposable
{
    internal static readonly Guid Major = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f"), Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    internal static readonly Guid Video = new("73646976-0000-0010-8000-00aa00389b71"), Rgb32 = new("00000016-0000-0010-8000-00aa00389b71");
    private nint writer; private bool mf, com, completed; private uint videoStream;
    private readonly int bytes;
    internal NativeVideoWriter(string path, int width, int height)
    {
        if (width < 2 || height < 2 || width > 7680 || height > 4320 || width % 2 != 0 || height % 2 != 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (File.Exists(path)) throw new IOException("A video already exists at this location.");
        bytes = checked(width * height * 4);
        nint attributes = 0, output = 0, input = 0;
        try
        {
            Hr(CoInitializeEx(0, 0)); com = true; Hr(MFStartup(0x20070, 0)); mf = true;
            Hr(MFCreateAttributes(out attributes, 3));
            SetGuid(attributes, new("150ff23f-4abc-478b-ac4f-e1916fba1cca"), new("9ba876f1-419f-4b77-a1e0-35959d9d4004"));
            Set32(attributes, new("08b845d8-2b74-4afe-9d53-be16d2d5ae4f"), 1);
            Hr(MFCreateSinkWriterFromURL(path, 0, attributes, out writer));
            output = VideoType(width, height, new("34363248-0000-0010-8000-00aa00389b71"));
            Set32(output, new("20332624-fb0d-4d9e-bd0d-cbf6786c102e"), (uint)Math.Clamp(width * height * 3, 1_000_000, 24_000_000));
            Hr(Method<AddStream>(writer, 3)(writer, output, out videoStream));
            input = VideoType(width, height, Rgb32);
            Set32(input, new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6"), (uint)(width * 4));
            Hr(Method<InputType>(writer, 4)(writer, videoStream, input, 0));
            Hr(Method<Simple>(writer, 5)(writer));
        }
        catch { Dispose(); throw; }
        finally { Release(input); Release(output); Release(attributes); }
    }
    private static nint VideoType(int width, int height, Guid subtype)
    {
        Hr(MFCreateMediaType(out var type));
        try
        {
            SetGuid(type, Major, Video); SetGuid(type, Subtype, subtype);
            Set64(type, new("1652c33d-d6b2-4012-b834-72030849a37d"), ((ulong)width << 32) | (uint)height);
            Set64(type, new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0"), (30UL << 32) | 1);
            Set64(type, new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6"), (1UL << 32) | 1);
            Set32(type, new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd"), 2); return type;
        }
        catch { Release(type); throw; }
    }
    internal void Write(byte[] pixels, long frame)
    {
        if (pixels.Length != bytes || frame < 0 || completed || writer == 0) throw new InvalidOperationException("Invalid video frame or writer state.");
        WriteSample(videoStream, pixels, frame * 10_000_000 / 30, (frame + 1) * 10_000_000 / 30 - frame * 10_000_000 / 30);
    }
    private void WriteSample(uint stream, byte[] data, long time, long duration)
    {
        nint buffer = 0, sample = 0;
        try
        {
            Hr(MFCreateMemoryBuffer((uint)data.Length, out buffer));
            Hr(Method<LockBuffer>(buffer, 3)(buffer, out var pointer, out _, out _));
            try { Marshal.Copy(data, 0, pointer, data.Length); } finally { Hr(Method<Simple>(buffer, 4)(buffer)); }
            Hr(Method<UIntValue>(buffer, 6)(buffer, (uint)data.Length));
            Hr(MFCreateSample(out sample)); Hr(Method<PointerValue>(sample, 42)(sample, buffer));
            Hr(Method<LongValue>(sample, 36)(sample, time)); Hr(Method<LongValue>(sample, 38)(sample, duration));
            Hr(Method<StreamSample>(writer, 6)(writer, stream, sample));
        }
        finally { Release(sample); Release(buffer); }
    }
    internal void Complete() { if (completed || writer == 0) return; Hr(Method<Simple>(writer, 11)(writer)); completed = true; }
    public void Dispose()
    {
        Release(writer); writer = 0;
        if (mf) { MFShutdown(); mf = false; } if (com) { CoUninitialize(); com = false; }
    }
    internal static T Method<T>(nint value, int slot) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(value), slot * nint.Size));
    internal static void Hr(int result) => Marshal.ThrowExceptionForHR(result);
    internal static void Release(nint value) { if (value != 0) Marshal.Release(value); }
    internal static void Set32(nint value, Guid key, uint number) => Hr(Method<Attribute32>(value, 21)(value, ref key, number));
    internal static void Set64(nint value, Guid key, ulong number) => Hr(Method<Attribute64>(value, 22)(value, ref key, number));
    internal static void SetGuid(nint value, Guid key, Guid data) => Hr(Method<AttributeGuid>(value, 24)(value, ref key, ref data));
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int Simple(nint self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int AddStream(nint self, nint type, out uint index);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int InputType(nint self, uint index, nint type, nint parameters);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Attribute32(nint self, ref Guid key, uint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Attribute64(nint self, ref Guid key, ulong value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int AttributeGuid(nint self, ref Guid key, ref Guid value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int LockBuffer(nint self, out nint data, out uint maximum, out uint length);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int UIntValue(nint self, uint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int LongValue(nint self, long value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int PointerValue(nint self, nint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int StreamSample(nint self, uint index, nint sample);
    [DllImport("ole32.dll")] internal static extern int CoInitializeEx(nint reserved, uint flags);
    [DllImport("ole32.dll")] internal static extern void CoUninitialize();
    [DllImport("mfplat.dll")] internal static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll")] internal static extern int MFShutdown();
    [DllImport("mfplat.dll")] internal static extern int MFCreateMediaType(out nint type);
    [DllImport("mfplat.dll")] internal static extern int MFCreateAttributes(out nint attributes, uint size);
    [DllImport("mfplat.dll")] private static extern int MFCreateSample(out nint sample);
    [DllImport("mfplat.dll")] private static extern int MFCreateMemoryBuffer(uint size, out nint buffer);
    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)] private static extern int MFCreateSinkWriterFromURL(string path, nint stream, nint attributes, out nint writer);
}
