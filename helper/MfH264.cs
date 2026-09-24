// MfH264.cs — Media Foundation H.264 编码/解码支持（移植自 Codex 的 MFH264.cs，2026-09-22）
//
// 来源：C:\Users\Administrator\dsh-cu-share\MFH264.cs（Codex 交付；实测见同目录 MF-H264-RESULTS.md）
// 移植改动：只取 MF 工具类与 MemoryEncoder；去掉 NativeColorConverter（本机 NVIDIA MFT 直接接受
// ARGB32/BGRA，不需要 CPU 转 NV12）与命令行入口 Main。代码逻辑未改。
//
// 编译：与本目录的 CuHelper.cs 一起交给系统自带 csc.exe（C# 5）。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

internal static class MF
{
    internal static readonly Guid Video = new Guid("73646976-0000-0010-8000-00aa00389b71");
    internal static readonly Guid H264 = new Guid("34363248-0000-0010-8000-00aa00389b71");
    internal static readonly Guid RGB32 = new Guid("00000016-0000-0010-8000-00aa00389b71");
    internal static readonly Guid ARGB32 = new Guid("00000015-0000-0010-8000-00aa00389b71");
    internal static readonly Guid NV12 = new Guid("3231564e-0000-0010-8000-00aa00389b71");
    internal const string Major = "48eba18e-f8c9-4687-bf11-0a74c9f96a8f";
    internal const string Subtype = "f7e34c9a-42e8-4714-b74b-cb29d72c35e5";
    internal const string Size = "1652c33d-d6b2-4012-b834-72030849a37d";
    internal const string Rate = "c459a2e8-3d2c-4e44-b132-fee5156c7bb0";
    internal const string Aspect = "c6376a1e-8d0a-4027-be45-6d9a0ad39bb6";
    internal const string Interlace = "e2724bb8-e676-4806-b4b2-a8d6efb44ccd";
    internal const string CodecIid = "901db4c7-31ce-41a2-85dc-8fa0bf41b8da";
    internal const string TransformIid = "bf94c121-5b05-4e6f-8000-ba598961414d";
    internal const string RateControl = "1c0608e9-370c-4710-8a58-cb6181c42423";
    internal const string Quality = "fcbf57a3-7ea5-4b0c-9644-69b40c39c391";
    internal const string QP = "2cb5696b-23fb-4ce1-a0f9-ef5b90fd55ca";
    internal const string GOP = "95f31b26-95a4-41aa-9303-246a7fc6eef1";

    [DllImport("mfplat.dll", ExactSpelling = true)] internal static extern int MFStartup(int version, int flags);
    [DllImport("mfplat.dll", ExactSpelling = true)] internal static extern int MFShutdown();
    [DllImport("mfplat.dll", ExactSpelling = true)] internal static extern int MFCreateMediaType(out IntPtr value);
    [DllImport("mfplat.dll", ExactSpelling = true)] internal static extern int MFCreateAttributes(out IntPtr value, uint count);
    [DllImport("mfplat.dll", ExactSpelling = true)] internal static extern int MFCreateSample(out IntPtr value);
    [DllImport("mfplat.dll", ExactSpelling = true)] internal static extern int MFCreateMemoryBuffer(uint size, out IntPtr value);
    [DllImport("mfplat.dll", ExactSpelling = true)] internal static extern int MFCreateMFByteStreamOnStream(IntPtr stream, out IntPtr value);
    [DllImport("mfreadwrite.dll", ExactSpelling = true, CharSet = CharSet.Unicode)] internal static extern int MFCreateSinkWriterFromURL(string url, IntPtr stream, IntPtr attributes, out IntPtr value);
    [DllImport("mfreadwrite.dll", ExactSpelling = true, CharSet = CharSet.Unicode)] internal static extern int MFCreateSourceReaderFromURL(string url, IntPtr attributes, out IntPtr value);
    [DllImport("mfreadwrite.dll", ExactSpelling = true)] internal static extern int MFCreateSourceReaderFromByteStream(IntPtr stream, IntPtr attributes, out IntPtr value);
    [DllImport("ole32.dll", ExactSpelling = true)] internal static extern int CoInitializeEx(IntPtr reserved, uint mode);
    [DllImport("ole32.dll", ExactSpelling = true)] internal static extern void CoUninitialize();
    [DllImport("ole32.dll", ExactSpelling = true)] internal static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, out IntPtr instance);
    [DllImport("shlwapi.dll", ExactSpelling = true)] internal static extern IntPtr SHCreateMemStream(IntPtr initial, uint size);
    [DllImport("shlwapi.dll", ExactSpelling = true, EntryPoint = "SHCreateMemStream")] internal static extern IntPtr SHCreateMemStreamFromBytes(byte[] initial, uint size);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int SetGuid(IntPtr self, ref Guid key, ref Guid value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int GetGuid(IntPtr self, ref Guid key, out Guid value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int Set32(IntPtr self, ref Guid key, uint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int Set64(IntPtr self, ref Guid key, ulong value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int Get32(IntPtr self, ref Guid key, out uint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int GetString(IntPtr self, ref Guid key, out IntPtr value, out uint size);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int OutPointer(IntPtr self, out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int Simple(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int AddStream(IntPtr self, IntPtr type, out uint stream);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int SetInput(IntPtr self, uint stream, IntPtr type, IntPtr attributes);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int WriteSample(IntPtr self, uint stream, IntPtr sample);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int GetService(IntPtr self, uint stream, ref Guid service, ref Guid iid, out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int AddBuffer(IntPtr self, IntPtr buffer);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int SetTime(IntPtr self, long time);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int GetTime(IntPtr self, out long time);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int LockBuffer(IntPtr self, out IntPtr data, out uint max, out uint length);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int SetLength(IntPtr self, uint length);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int Length(IntPtr self, out ulong length);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int SeekStream(IntPtr self, long offset, uint origin, out ulong position);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int ReadStream(IntPtr self, [Out] byte[] buffer, uint size, out uint read);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int CodecValue(IntPtr self, ref Guid key, ref Variant value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int GetTransform(IntPtr self, uint stream, uint index, out Guid category, out IntPtr transform);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int SetCurrentType(IntPtr self, uint stream, IntPtr reserved, IntPtr type);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int ReadSample(IntPtr self, uint stream, uint flags, out uint actualStream, out uint streamFlags, out long timestamp, out IntPtr sample);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int SetPosition(IntPtr self, ref Guid timeFormat, ref Variant position);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int TransformType(IntPtr self, uint stream, IntPtr type, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int AvailableType(IntPtr self, uint stream, uint index, out IntPtr type);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int TransformInput(IntPtr self, uint stream, IntPtr sample, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int TransformMessage(IntPtr self, uint message, UIntPtr parameter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int TransformOutput(IntPtr self, uint flags, uint count, ref OutputBuffer buffer, out uint status);
    [StructLayout(LayoutKind.Sequential)] internal struct OutputBuffer
    {
        public uint Stream;
        public IntPtr Sample;
        public uint Status;
        public IntPtr Events;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)] internal struct Variant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public uint UInt32;
        [FieldOffset(8)] public ulong UInt64;
        [FieldOffset(8)] public long Int64;
    }

    internal static T Call<T>(IntPtr instance, int slot) where T : class
    {
        if (instance == IntPtr.Zero) throw new ArgumentNullException("instance");
        return (T)(object)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size), typeof(T));
    }
    internal static void Check(int result, string operation)
    {
        if (result < 0) throw new COMException(operation + ": 0x" + result.ToString("X8"), result);
    }
    internal static void Release(ref IntPtr value)
    {
        if (value != IntPtr.Zero) { Marshal.Release(value); value = IntPtr.Zero; }
    }
    internal static void GuidValue(IntPtr instance, string key, Guid value)
    {
        Guid attribute = new Guid(key); Check(Call<SetGuid>(instance, 24)(instance, ref attribute, ref value), "SetGUID " + key);
    }
    internal static void UInt32(IntPtr instance, string key, uint value)
    {
        Guid attribute = new Guid(key); Check(Call<Set32>(instance, 21)(instance, ref attribute, value), "SetUINT32 " + key);
    }
    internal static void Pair(IntPtr instance, string key, uint high, uint low)
    {
        Guid attribute = new Guid(key); Check(Call<Set64>(instance, 22)(instance, ref attribute, ((ulong)high << 32) | low), "SetUINT64 " + key);
    }
    internal static string StringValue(IntPtr instance, string key)
    {
        Guid attribute = new Guid(key); IntPtr data; uint count;
        int result = Call<GetString>(instance, 13)(instance, ref attribute, out data, out count);
        if (result < 0) return "<absent>";
        try { return Marshal.PtrToStringUni(data); } finally { Marshal.FreeCoTaskMem(data); }
    }
    internal static IntPtr Type(Guid subtype, int width, int height)
    {
        IntPtr type; Check(MFCreateMediaType(out type), "MFCreateMediaType");
        try
        {
            GuidValue(type, Major, Video); GuidValue(type, Subtype, subtype);
            Pair(type, Size, (uint)width, (uint)height); Pair(type, Rate, 30, 1); Pair(type, Aspect, 1, 1);
            UInt32(type, Interlace, 2);
            return type;
        }
        catch { Release(ref type); throw; }
    }
    internal static IntPtr Sample(byte[] data, long time, long duration)
    {
        IntPtr sample = IntPtr.Zero, buffer = IntPtr.Zero;
        try
        {
            Check(MFCreateSample(out sample), "MFCreateSample");
            Check(MFCreateMemoryBuffer((uint)data.Length, out buffer), "MFCreateMemoryBuffer");
            IntPtr pixels; uint capacity, length;
            Check(Call<LockBuffer>(buffer, 3)(buffer, out pixels, out capacity, out length), "Lock");
            try { Marshal.Copy(data, 0, pixels, data.Length); }
            finally { Check(Call<Simple>(buffer, 4)(buffer), "Unlock"); }
            Check(Call<SetLength>(buffer, 6)(buffer, (uint)data.Length), "SetCurrentLength");
            Check(Call<AddBuffer>(sample, 42)(sample, buffer), "AddBuffer");
            Check(Call<SetTime>(sample, 36)(sample, time), "SetSampleTime");
            Check(Call<SetTime>(sample, 38)(sample, duration), "SetSampleDuration");
            return sample;
        }
        catch { Release(ref sample); throw; }
        finally { Release(ref buffer); }
    }
    internal static byte[] Bytes(IntPtr sample)
    {
        IntPtr buffer = IntPtr.Zero;
        Check(Call<OutPointer>(sample, 41)(sample, out buffer), "ConvertToContiguousBuffer");
        try
        {
            IntPtr data; uint capacity, size;
            Check(Call<LockBuffer>(buffer, 3)(buffer, out data, out capacity, out size), "Buffer.Lock");
            try { byte[] bytes = new byte[checked((int)size)]; Marshal.Copy(data, bytes, 0, bytes.Length); return bytes; }
            finally { Check(Call<Simple>(buffer, 4)(buffer), "Buffer.Unlock"); }
        }
        finally { Release(ref buffer); }
    }
    internal static IntPtr OpenMemoryReader(byte[] data)
    {
        IntPtr stream = SHCreateMemStreamFromBytes(data, (uint)data.Length), byteStream = IntPtr.Zero, reader;
        if (stream == IntPtr.Zero) throw new InvalidOperationException("SHCreateMemStream failed");
        try
        {
            Check(MFCreateMFByteStreamOnStream(stream, out byteStream), "MemoryReader.ByteStream");
            Check(MFCreateSourceReaderFromByteStream(byteStream, IntPtr.Zero, out reader), "MemoryReader.SourceReader");
            return reader;
        }
        finally { Release(ref byteStream); Release(ref stream); }
    }
}

// ---- MemoryEncoder：内存 MP4 切片编码器（每片一个实例，独立可解码）----
internal sealed class MemoryEncoder : IDisposable
{
    private IntPtr writer, stream, byteStream, codec;
    private uint index;
    private bool finished;
    internal string EncoderName { get; private set; }
    internal MemoryEncoder(int width, int height, bool hardware, string mode, uint quality, Guid inputFormat)
    {
        IntPtr attributes = IntPtr.Zero, input = IntPtr.Zero, output = IntPtr.Zero, encodingParameters = IntPtr.Zero;
        try
        {
            MF.Check(MF.MFCreateAttributes(out attributes, 5), "MFCreateAttributes");
            MF.UInt32(attributes, "a634a91c-822b-41b9-a494-4de4643612b0", hardware ? 1U : 0U);
            MF.UInt32(attributes, "08b845d8-2b74-4afe-9d53-be16d2d5ae4f", 1);
            MF.GuidValue(attributes, "150ff23f-4abc-478b-ac4f-e1916fba1cca", new Guid("dc6cd05d-b9d0-40ef-bd35-fa622c1ab28a"));
            stream = MF.SHCreateMemStream(IntPtr.Zero, 0);
            if (stream == IntPtr.Zero) throw new InvalidOperationException("SHCreateMemStream failed");
            MF.Check(MF.MFCreateMFByteStreamOnStream(stream, out byteStream), "MFCreateMFByteStreamOnStream");
            MF.Check(MF.MFCreateSinkWriterFromURL(null, byteStream, attributes, out writer), "MFCreateSinkWriterFromURL");
            output = MF.Type(MF.H264, width, height);
            MF.UInt32(output, "20332624-fb0d-4d9e-bd0d-cbf6786c102e", 12000000);
            MF.UInt32(output, "ad76a80b-2d5c-4e0b-b375-64e520137036", 100);
            MF.Check(MF.Call<MF.AddStream>(writer, 3)(writer, output, out index), "AddStream(H264)");
            input = MF.Type(inputFormat, width, height);
            if (inputFormat == MF.RGB32 || inputFormat == MF.ARGB32) MF.UInt32(input, "644b4e48-1e02-4516-b0eb-c01ca9d49ac6", (uint)(width * 4));
            MF.Check(MF.MFCreateAttributes(out encodingParameters, 4), "MFCreateAttributes(encoding)");
            if (mode != "default")
            {
                MF.UInt32(encodingParameters, MF.RateControl, mode == "qp" || mode == "quality" ? 3U : 0U);
                MF.UInt32(encodingParameters, MF.GOP, 30);
                if (mode == "qp") MF.Pair(encodingParameters, MF.QP, 0, quality);
                if (mode == "quality") MF.UInt32(encodingParameters, MF.Quality, quality);
            }
            MF.Check(MF.Call<MF.SetInput>(writer, 4)(writer, index, input, encodingParameters), "SetInputMediaType");
            Guid service = Guid.Empty, codecId = new Guid(MF.CodecIid);
            MF.Check(MF.Call<MF.GetService>(writer, 12)(writer, index, ref service, ref codecId, out codec), "GetService(ICodecAPI)");
            ReportTransforms();
            MF.Check(MF.Call<MF.Simple>(writer, 5)(writer), "BeginWriting");
            if (mode != "default")
            {
                SetCodec(MF.RateControl, 19, mode == "qp" || mode == "quality" ? 3UL : 0UL);
                if (mode == "qp") SetCodec(MF.QP, 21, quality);
                if (mode == "quality") SetCodec(MF.Quality, 19, quality);
                SetCodec(MF.GOP, 19, 30);
            }
        }
        catch { Dispose(); throw; }
        finally { MF.Release(ref encodingParameters); MF.Release(ref input); MF.Release(ref output); MF.Release(ref attributes); }
    }

    private void SetCodec(string key, ushort type, ulong value)
    {
        Guid id = new Guid(key); MF.Variant variant = new MF.Variant(); variant.Type = type; variant.UInt64 = value;
        int result = MF.Call<MF.CodecValue>(codec, 9)(codec, ref id, ref variant);
        Console.Error.WriteLine("codec {0} value={1} hr=0x{2:X8}", key, value, result);
        MF.Check(result, "ICodecAPI.SetValue " + key);
        MF.Variant observed = new MF.Variant();
        MF.Check(MF.Call<MF.CodecValue>(codec, 8)(codec, ref id, ref observed), "ICodecAPI.GetValue");
        ulong observedValue = observed.Type == 19 ? observed.UInt32 : observed.UInt64;
        Console.Error.WriteLine("codec readback type={0} value={1}", observed.Type, observedValue);
        if (observed.Type != type || observedValue != value) throw new InvalidOperationException("Encoder did not retain codec setting " + key);
    }

    private void ReportTransforms()
    {
        IntPtr extended = IntPtr.Zero;
        Guid id = new Guid("588d72ab-5bc1-496a-8714-b70617141b25");
        MF.Check(Marshal.QueryInterface(writer, ref id, out extended), "QueryInterface(IMFSinkWriterEx)");
        try
        {
            for (uint position = 0; position < 8; position++)
            {
                Guid category; IntPtr transform = IntPtr.Zero, attributes = IntPtr.Zero;
                int result = MF.Call<MF.GetTransform>(extended, 14)(extended, index, position, out category, out transform);
                if (result < 0) break;
                try
                {
                    MF.Check(MF.Call<MF.OutPointer>(transform, 8)(transform, out attributes), "Transform.GetAttributes");
                    Console.Error.WriteLine("transform[{0}] category={1} name={2} hardware={3}", position, category,
                        MF.StringValue(attributes, "314ffbae-5b41-4c95-9c19-4e7d586face3"),
                        MF.StringValue(attributes, "2fb866ac-b078-4942-ab6c-003d05cda674"));
                    if (category == new Guid("f79eac7d-e545-4387-bdee-d647d7bde42a"))
                    {
                        EncoderName = MF.StringValue(attributes, "314ffbae-5b41-4c95-9c19-4e7d586face3");
                        for (uint typeIndex = 0; typeIndex < 16; typeIndex++)
                        {
                            IntPtr type;
                            int available = MF.Call<MF.AvailableType>(transform, 13)(transform, 0, typeIndex, out type);
                            if (available < 0) { Console.Error.WriteLine("encoder input enumeration ended: 0x{0:X8}", available); break; }
                            try
                            {
                                Guid key = new Guid(MF.Subtype), subtype;
                                MF.Check(MF.Call<MF.GetGuid>(type, 10)(type, ref key, out subtype), "Input.GetSubtype");
                                Console.Error.WriteLine("encoder input[{0}]={1}", typeIndex, subtype);
                            }
                            finally { MF.Release(ref type); }
                        }
                    }
                }
                finally { MF.Release(ref attributes); MF.Release(ref transform); }
            }
        }
        finally { MF.Release(ref extended); }
    }

    internal void Write(byte[] data, int frame)
    {
        IntPtr sample = MF.Sample(data, (long)frame * 10000000 / 30, (long)(frame + 1) * 10000000 / 30 - (long)frame * 10000000 / 30);
        try { MF.Check(MF.Call<MF.WriteSample>(writer, 6)(writer, index, sample), "WriteSample"); }
        finally { MF.Release(ref sample); }
    }

    internal byte[] Finish()
    {
        if (finished) throw new InvalidOperationException("Already finalized");
        MF.Check(MF.Call<MF.Simple>(writer, 11)(writer), "Finalize");
        finished = true;
        ulong size;
        MF.Check(MF.Call<MF.Length>(byteStream, 4)(byteStream, out size), "ByteStream.GetLength");
        ulong position;
        MF.Check(MF.Call<MF.SeekStream>(stream, 5)(stream, 0, 0, out position), "IStream.Seek");
        byte[] result = new byte[checked((int)size)]; uint read;
        MF.Check(MF.Call<MF.ReadStream>(stream, 3)(stream, result, (uint)result.Length, out read), "IStream.Read");
        if (read != result.Length) throw new InvalidOperationException("Short memory stream read");
        return result;
    }

    public void Dispose() { MF.Release(ref codec); MF.Release(ref writer); MF.Release(ref byteStream); MF.Release(ref stream); }
}
