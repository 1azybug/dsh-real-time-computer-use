using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace Dsh.MediaFoundation
{
    public sealed class DecodedFrame : IDisposable
    {
        public Bitmap Bitmap { get; private set; }
        public long Timestamp100ns { get; private set; }
        public int DecodedSamples { get; private set; }
        public double SeekMilliseconds { get; private set; }
        public double DecodeMilliseconds { get; private set; }
        public double ConvertMilliseconds { get; private set; }
        public double CopyMilliseconds { get; private set; }

        internal DecodedFrame(Bitmap bitmap, long timestamp, int samples, double seek, double decode, double convert, double copy)
        {
            Bitmap = bitmap;
            Timestamp100ns = timestamp;
            DecodedSamples = samples;
            SeekMilliseconds = seek;
            DecodeMilliseconds = decode;
            ConvertMilliseconds = convert;
            CopyMilliseconds = copy;
        }

        public void Dispose()
        {
            if (Bitmap != null) { Bitmap.Dispose(); Bitmap = null; }
        }
    }

    public sealed class MFFrameDecoder : IDisposable
    {
        private const uint VideoStream = 0xFFFFFFFC;
        private IntPtr reader;
        private IntPtr converter, convertedSample, convertedBuffer;
        private int width, height, stride;
        private long lastTimestamp = long.MinValue;
        private bool ended;
        public double OpenMilliseconds { get; private set; }

        public MFFrameDecoder(byte[] finalizedMp4)
        {
            if (finalizedMp4 == null) throw new ArgumentNullException("finalizedMp4");
            if (finalizedMp4.Length == 0) throw new ArgumentException("MP4 must not be empty", "finalizedMp4");
            Stopwatch opening = Stopwatch.StartNew();
            IntPtr stream = IntPtr.Zero, byteStream = IntPtr.Zero, mediaType = IntPtr.Zero;
            try
            {
                stream = Native.SHCreateMemStream(finalizedMp4, checked((uint)finalizedMp4.Length));
                if (stream == IntPtr.Zero) throw new OutOfMemoryException("SHCreateMemStream");
                Native.Check(Native.MFCreateMFByteStreamOnStream(stream, out byteStream), "MFCreateMFByteStreamOnStream");
                Native.Check(Native.MFCreateSourceReaderFromByteStream(byteStream, IntPtr.Zero, out reader), "MFCreateSourceReaderFromByteStream (host must call MFStartup)");
                Native.Check(Native.Call<Native.SelectStream>(reader, 4)(reader, 0xFFFFFFFE, 0), "Deselect streams");
                Native.Check(Native.Call<Native.SelectStream>(reader, 4)(reader, VideoStream, 1), "Select video");
                Native.Check(Native.MFCreateMediaType(out mediaType), "MFCreateMediaType");
                Native.SetGuid(mediaType, "48eba18e-f8c9-4687-bf11-0a74c9f96a8f", Native.Video);
                Native.SetGuid(mediaType, "f7e34c9a-42e8-4714-b74b-cb29d72c35e5", Native.Nv12);
                Native.Check(Native.Call<Native.SetMediaType>(reader, 7)(reader, VideoStream, IntPtr.Zero, mediaType), "SetCurrentMediaType(NV12)");
                ReadLayout();
                OpenMilliseconds = opening.Elapsed.TotalMilliseconds;
            }
            catch { Dispose(); throw; }
            finally
            {
                Native.Release(ref mediaType);
                Native.Release(ref byteStream);
                Native.Release(ref stream);
            }
        }

        public DecodedFrame ReadAtOrAfter(long target100ns, bool seek = true)
        {
            if (reader == IntPtr.Zero) throw new ObjectDisposedException("MFFrameDecoder");
            if (target100ns < 0) throw new ArgumentOutOfRangeException("target100ns");
            if (!seek && target100ns <= lastTimestamp)
                throw new ArgumentException("Sequential target must exceed the previously returned timestamp; use seek for repeated or earlier targets", "target100ns");
            double seekMs = 0;
            if (seek)
            {
                Guid format = Guid.Empty;
                Native.PropVariant position = new Native.PropVariant();
                position.Type = 20;
                position.Int64 = target100ns;
                Stopwatch seeking = Stopwatch.StartNew();
                Native.Check(Native.Call<Native.SetPosition>(reader, 8)(reader, ref format, ref position), "SetCurrentPosition");
                seekMs = seeking.Elapsed.TotalMilliseconds;
                lastTimestamp = long.MinValue;
                ended = false;
            }
            if (ended) return null;
            Stopwatch decoding = Stopwatch.StartNew();
            int samples = 0;
            for (;;)
            {
                uint actualStream, flags;
                long timestamp;
                IntPtr sample = IntPtr.Zero;
                try
                {
                    Native.Check(Native.Call<Native.ReadSample>(reader, 9)(reader, VideoStream, 0, out actualStream, out flags, out timestamp, out sample), "ReadSample");
                    if ((flags & 1) != 0) throw new InvalidDataException("Source reader reported MF_SOURCE_READERF_ERROR");
                    if ((flags & 0x20) != 0) ReadLayout();
                    ended = (flags & 2) != 0;
                    if (sample != IntPtr.Zero)
                    {
                        if (timestamp < lastTimestamp) throw new InvalidDataException("Decoded timestamps moved backwards");
                        lastTimestamp = timestamp;
                        samples++;
                        if (timestamp >= target100ns)
                        {
                            double decodeMs = decoding.Elapsed.TotalMilliseconds;
                            Stopwatch converting = Stopwatch.StartNew();
                            ConvertSelectedSample(sample);
                            double convertMs = converting.Elapsed.TotalMilliseconds;
                            Stopwatch copying = Stopwatch.StartNew();
                            Bitmap bitmap = CopyBitmap(convertedSample);
                            return new DecodedFrame(bitmap, timestamp, samples, seekMs, decodeMs, convertMs, copying.Elapsed.TotalMilliseconds);
                        }
                    }
                    if (ended) return null;
                }
                finally { Native.Release(ref sample); }
            }
        }

        private void ReadLayout()
        {
            IntPtr mediaType = IntPtr.Zero;
            try
            {
                Native.Check(Native.Call<Native.GetMediaType>(reader, 6)(reader, VideoStream, out mediaType), "GetCurrentMediaType");
                Guid key = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5"), subtype;
                Native.Check(Native.Call<Native.GetGuidValue>(mediaType, 10)(mediaType, ref key, out subtype), "Get subtype");
                if (subtype != Native.Nv12) throw new NotSupportedException("Expected NV12 decoder output");
                key = new Guid("1652c33d-d6b2-4012-b834-72030849a37d");
                ulong size;
                Native.Check(Native.Call<Native.GetUInt64>(mediaType, 8)(mediaType, ref key, out size), "Get frame size");
                width = checked((int)(size >> 32));
                height = checked((int)(size & 0xFFFFFFFF));
                if (width <= 0 || height <= 0) throw new InvalidDataException("Invalid frame dimensions");
                stride = checked(width * 4);
                CreateConverter(mediaType, size);
            }
            finally { Native.Release(ref mediaType); }
        }

        private void CreateConverter(IntPtr inputType, ulong size)
        {
            ReleaseConverter();
            IntPtr outputType = IntPtr.Zero;
            try
            {
                Guid clsid = new Guid("98230571-0087-4204-b020-3282538e57d3");
                Guid iid = new Guid("bf94c121-5b05-4e6f-8000-ba598961414d");
                Native.Check(Native.CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out converter), "CoCreateInstance(ColorConvertDMO)");
                Native.Check(Native.MFCreateMediaType(out outputType), "Create RGB32 media type");
                Native.SetGuid(outputType, "48eba18e-f8c9-4687-bf11-0a74c9f96a8f", Native.Video);
                Native.SetGuid(outputType, "f7e34c9a-42e8-4714-b74b-cb29d72c35e5", Native.Rgb32);
                Guid key = new Guid("1652c33d-d6b2-4012-b834-72030849a37d");
                Native.Check(Native.Call<Native.SetUInt64>(outputType, 22)(outputType, ref key, size), "Set RGB32 size");
                Native.SetUInt32(outputType, "644b4e48-1e02-4516-b0eb-c01ca9d49ac6", checked((uint)stride));
                Native.SetUInt32(outputType, "e2724bb8-e676-4806-b4b2-a8d6efb44ccd", 2);
                Native.Check(Native.Call<Native.TransformType>(converter, 15)(converter, 0, inputType, 0), "Color.SetInputType(NV12)");
                Native.Check(Native.Call<Native.TransformType>(converter, 16)(converter, 0, outputType, 0), "Color.SetOutputType(RGB32)");
                Native.OutputStreamInfo info;
                Native.Check(Native.Call<Native.GetOutputInfo>(converter, 7)(converter, 0, out info), "Color.GetOutputStreamInfo");
                if ((info.Flags & 0x100) != 0) throw new NotSupportedException("Color converter unexpectedly requires its own output samples");
                uint capacity = Math.Max(checked((uint)(stride * height)), info.Size);
                Native.Check(Native.MFCreateSample(out convertedSample), "MFCreateSample");
                Native.Check(Native.MFCreateAlignedMemoryBuffer(capacity, info.Alignment > 0 ? info.Alignment - 1 : 0, out convertedBuffer), "MFCreateAlignedMemoryBuffer");
                Native.Check(Native.Call<Native.AddBuffer>(convertedSample, 42)(convertedSample, convertedBuffer), "Sample.AddBuffer");
                Native.Check(Native.Call<Native.TransformMessage>(converter, 23)(converter, 0x10000000, UIntPtr.Zero), "Color.BeginStreaming");
                Native.Check(Native.Call<Native.TransformMessage>(converter, 23)(converter, 0x10000003, UIntPtr.Zero), "Color.StartOfStream");
            }
            catch { ReleaseConverter(); throw; }
            finally { Native.Release(ref outputType); }
        }

        private void ConvertSelectedSample(IntPtr sample)
        {
            Native.Check(Native.Call<Native.SetLength>(convertedBuffer, 6)(convertedBuffer, 0), "Reset converted length");
            Native.Check(Native.Call<Native.TransformInput>(converter, 24)(converter, 0, sample, 0), "Color.ProcessInput");
            Native.OutputBuffer output = new Native.OutputBuffer();
            output.Sample = convertedSample;
            try
            {
                uint status;
                Native.Check(Native.Call<Native.TransformOutput>(converter, 25)(converter, 0, 1, ref output, out status), "Color.ProcessOutput");
                if (output.Sample != convertedSample || (output.Status & 0x1000300) != 0)
                    throw new InvalidDataException("Color converter did not produce one complete RGB32 frame");
            }
            finally { Native.Release(ref output.Events); }
        }

        private Bitmap CopyBitmap(IntPtr sample)
        {
            IntPtr buffer = IntPtr.Zero;
            try
            {
                Native.Check(Native.Call<Native.OutPointer>(sample, 41)(sample, out buffer), "ConvertToContiguousBuffer");
                IntPtr pixels;
                uint capacity, length;
                Native.Check(Native.Call<Native.LockBuffer>(buffer, 3)(buffer, out pixels, out capacity, out length), "Buffer.Lock");
                try
                {
                    long rowSpan = Math.Abs((long)stride) * (height - 1);
                    if (rowSpan + checked(width * 4) > length) throw new InvalidDataException("RGB32 buffer is too short");
                    return CopyRows(pixels, stride);
                }
                finally { Native.Check(Native.Call<Native.Simple>(buffer, 4)(buffer), "Buffer.Unlock"); }
            }
            finally { Native.Release(ref buffer); }
        }

        private Bitmap CopyRows(IntPtr pixels, int pitch)
        {
            int rowBytes = checked(width * 4);
            if (Math.Abs((long)pitch) < rowBytes) throw new InvalidDataException("RGB32 pitch is too small");
            Bitmap bitmap = new Bitmap(width, height, PixelFormat.Format32bppRgb);
            try
            {
                BitmapData destination = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                try { Native.Check(Native.MFCopyImage(destination.Scan0, destination.Stride, pixels, pitch, checked((uint)rowBytes), checked((uint)height)), "MFCopyImage"); }
                finally { bitmap.UnlockBits(destination); }
                return bitmap;
            }
            catch { bitmap.Dispose(); throw; }
        }

        private void ReleaseConverter()
        {
            Native.Release(ref convertedBuffer);
            Native.Release(ref convertedSample);
            Native.Release(ref converter);
        }

        public void Dispose() { ReleaseConverter(); Native.Release(ref reader); }

        private static class Native
        {
            internal static readonly Guid Video = new Guid("73646976-0000-0010-8000-00aa00389b71");
            internal static readonly Guid Rgb32 = new Guid("00000016-0000-0010-8000-00aa00389b71");
            internal static readonly Guid Nv12 = new Guid("3231564e-0000-0010-8000-00aa00389b71");
            [DllImport("ole32.dll", ExactSpelling = true)] internal static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, out IntPtr instance);
            [DllImport("shlwapi.dll", ExactSpelling = true)] internal static extern IntPtr SHCreateMemStream(byte[] initial, uint size);
            [DllImport("mfplat.dll", ExactSpelling = true)] internal static extern int MFCreateMFByteStreamOnStream(IntPtr stream, out IntPtr value);
            [DllImport("mfplat.dll", ExactSpelling = true)] internal static extern int MFCreateSample(out IntPtr value);
            [DllImport("mfplat.dll", ExactSpelling = true)] internal static extern int MFCreateAlignedMemoryBuffer(uint size, uint alignmentMask, out IntPtr value);
            [DllImport("mfplat.dll", ExactSpelling = true)] internal static extern int MFCreateMediaType(out IntPtr value);
            [DllImport("mfplat.dll", ExactSpelling = true)] internal static extern int MFCopyImage(IntPtr destination, int destinationStride, IntPtr source, int sourceStride, uint widthBytes, uint lines);
            [DllImport("mfreadwrite.dll", ExactSpelling = true)] internal static extern int MFCreateSourceReaderFromByteStream(IntPtr stream, IntPtr attributes, out IntPtr reader);
            [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int SetGuidValue(IntPtr self, ref Guid key, ref Guid value);
            [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int GetGuidValue(IntPtr self, ref Guid key, out Guid value);
            [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int SetUInt32Value(IntPtr self, ref Guid key, uint value);
            [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int SetUInt64(IntPtr self, ref Guid key, ulong value);
            [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int GetUInt64(IntPtr self, ref Guid key, out ulong value);
            [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int SelectStream(IntPtr self, uint stream, int selected);
            [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int GetMediaType(IntPtr self, uint stream, out IntPtr value);
            [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int SetMediaType(IntPtr self, uint stream, IntPtr reserved, IntPtr value);
            [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int SetPosition(IntPtr self, ref Guid format, ref PropVariant position);
            [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int ReadSample(IntPtr self, uint stream, uint flags, out uint actualStream, out uint streamFlags, out long timestamp, out IntPtr sample);
            [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int OutPointer(IntPtr self, out IntPtr value);
            [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int Simple(IntPtr self);
            [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int LockBuffer(IntPtr self, out IntPtr data, out uint capacity, out uint length);
            [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int AddBuffer(IntPtr self, IntPtr buffer);
            [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int SetLength(IntPtr self, uint length);
            [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int TransformType(IntPtr self, uint stream, IntPtr type, uint flags);
            [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int GetOutputInfo(IntPtr self, uint stream, out OutputStreamInfo info);
            [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int TransformInput(IntPtr self, uint stream, IntPtr sample, uint flags);
            [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int TransformMessage(IntPtr self, uint message, UIntPtr parameter);
            [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int TransformOutput(IntPtr self, uint flags, uint count, ref OutputBuffer buffer, out uint status);
            [StructLayout(LayoutKind.Sequential)] internal struct OutputStreamInfo
            {
                internal uint Flags, Size, Alignment;
            }
            [StructLayout(LayoutKind.Sequential)] internal struct OutputBuffer
            {
                internal uint Stream;
                internal IntPtr Sample;
                internal uint Status;
                internal IntPtr Events;
            }
            [StructLayout(LayoutKind.Explicit, Size = 24)] internal struct PropVariant
            {
                [FieldOffset(0)] internal ushort Type;
                [FieldOffset(8)] internal long Int64;
            }
            internal static T Call<T>(IntPtr instance, int slot) where T : class
            {
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
            internal static void SetGuid(IntPtr attributes, string name, Guid value)
            {
                Guid key = new Guid(name);
                Check(Call<SetGuidValue>(attributes, 24)(attributes, ref key, ref value), "SetGUID " + name);
            }
            internal static void SetUInt32(IntPtr attributes, string name, uint value)
            {
                Guid key = new Guid(name);
                Check(Call<SetUInt32Value>(attributes, 21)(attributes, ref key, value), "SetUINT32 " + name);
            }
        }
    }
}
