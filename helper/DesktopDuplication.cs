using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public sealed class DesktopDuplication : IDisposable
{
    private const int WaitTimeout = unchecked((int)0x887A0027);
    private const int AccessLost = unchecked((int)0x887A0026);
    private const int NotFound = unchecked((int)0x887A0002);
    private const int OutputDuplicateSlot = 22;
    private const int DeviceCreateTexture2DSlot = 5;
    private const int ContextMapSlot = 14;
    private const int ContextUnmapSlot = 15;
    private const int ContextCopyResourceSlot = 47;
    private IntPtr device, context, duplication, staging, pixels;
    private Bitmap bitmap;
    private AcquireFrame acquire;
    private ReleaseFrame releaseFrame;
    private MapResource map;
    private UnmapResource unmap;
    private CopyResource copy;
    private bool hasImage;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public int Left { get; private set; }
    public int Top { get; private set; }
    public string DisplayName { get; private set; }
    public long LastPresentQpc { get; private set; }
    public long LastMouseQpc { get; private set; }
    public uint AccumulatedFrames { get; private set; }
    public bool PointerVisible { get; private set; }
    public int PointerX { get; private set; }
    public int PointerY { get; private set; }
    public bool ProtectedContentMaskedOut { get; private set; }
    /// <summary>是否已经成功取到过一帧（在此之前 Image 不可读）。</summary>
    public bool HasImage { get { return hasImage; } }

    public Bitmap Image
    {
        get
        {
            if (!hasImage) throw new InvalidOperationException("No desktop image has been acquired");
            return bitmap;
        }
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(ref Guid iid, out IntPtr factory);
    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software,
        uint flags, IntPtr levels, uint levelCount, uint sdkVersion,
        out IntPtr device, out uint featureLevel, out IntPtr context);
    [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory", ExactSpelling = true)]
    private static extern void CopyMemory(IntPtr destination, IntPtr source, UIntPtr length);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Enumerate(IntPtr instance, uint index, out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetOutputDesc(IntPtr instance, out OutputDesc desc);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DuplicateOutput(IntPtr instance, IntPtr device, out IntPtr duplication);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void GetDuplicationDesc(IntPtr instance, out DuplicationDesc desc);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int AcquireFrame(IntPtr instance, uint timeout, out FrameInfo info, out IntPtr resource);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ReleaseFrame(IntPtr instance);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateTexture2D(IntPtr instance, ref TextureDesc desc, IntPtr initialData, out IntPtr texture);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void CopyResource(IntPtr instance, IntPtr destination, IntPtr source);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int MapResource(IntPtr instance, IntPtr resource, uint subresource, uint mapType, uint flags, out MappedResource mapped);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void UnmapResource(IntPtr instance, IntPtr resource, uint subresource);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OutputDesc
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public int Left, Top, Right, Bottom;
        public int AttachedToDesktop;
        public uint Rotation;
        public IntPtr Monitor;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ModeDesc
    {
        public uint Width, Height, RefreshNumerator, RefreshDenominator, Format, ScanlineOrdering, Scaling;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct DuplicationDesc
    {
        public ModeDesc Mode;
        public uint Rotation;
        public int DesktopImageInSystemMemory;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FrameInfo
    {
        public long LastPresentTime, LastMouseUpdateTime;
        public uint AccumulatedFrames;
        public int RectsCoalesced, ProtectedContentMaskedOut;
        public int PointerX, PointerY, PointerVisible;
        public uint TotalMetadataBufferSize, PointerShapeBufferSize;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct TextureDesc
    {
        public uint Width, Height, MipLevels, ArraySize, Format;
        public uint SampleCount, SampleQuality, Usage, BindFlags, CPUAccessFlags, MiscFlags;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MappedResource
    {
        public IntPtr Data;
        public uint RowPitch, DepthPitch;
    }

    private static T Method<T>(IntPtr instance, int slot) where T : class
    {
        IntPtr address = Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size);
        return (T)(object)Marshal.GetDelegateForFunctionPointer(address, typeof(T));
    }

    private static void Check(int result, string operation)
    {
        if (result >= 0) return;
        string detail = result == AccessLost ? "; desktop changed: dispose and recreate capture" : "";
        throw new COMException(operation + " failed: 0x" + result.ToString("X8") + detail, result);
    }

    private static void Release(ref IntPtr instance)
    {
        if (instance == IntPtr.Zero) return;
        Marshal.Release(instance);
        instance = IntPtr.Zero;
    }

    public DesktopDuplication(string displayName)
    {
        if (IntPtr.Size != 8) throw new PlatformNotSupportedException("Build with /platform:x64");
        if (Marshal.SizeOf(typeof(OutputDesc)) != 96 || Marshal.SizeOf(typeof(FrameInfo)) != 48)
            throw new InvalidOperationException("DXGI native structure layout mismatch");
        IntPtr factory = IntPtr.Zero, adapter = IntPtr.Zero, output = IntPtr.Zero, output1 = IntPtr.Zero;
        try
        {
            Guid factoryId = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
            Check(CreateDXGIFactory1(ref factoryId, out factory), "CreateDXGIFactory1");
            Enumerate enumAdapters = Method<Enumerate>(factory, 12);
            bool selected = false;
            for (uint adapterIndex = 0; !selected; adapterIndex++)
            {
                int result = enumAdapters(factory, adapterIndex, out adapter);
                if (result == NotFound) break;
                Check(result, "EnumAdapters1");
                Enumerate enumOutputs = Method<Enumerate>(adapter, 7);
                for (uint outputIndex = 0; !selected; outputIndex++)
                {
                    result = enumOutputs(adapter, outputIndex, out output);
                    if (result == NotFound) break;
                    Check(result, "EnumOutputs");
                    OutputDesc desc;
                    Check(Method<GetOutputDesc>(output, 7)(output, out desc), "GetDesc(output)");
                    selected = desc.AttachedToDesktop != 0 && (displayName == null
                        ? desc.Left == 0 && desc.Top == 0
                        : string.Equals(displayName, desc.DeviceName, StringComparison.OrdinalIgnoreCase));
                    if (!selected) { Release(ref output); continue; }
                    DisplayName = desc.DeviceName;
                    Left = desc.Left;
                    Top = desc.Top;
                    Guid outputId = new Guid("00cddea8-939b-4b83-a340-a685226666cc");
                    Check(Marshal.QueryInterface(output, ref outputId, out output1), "QueryInterface(IDXGIOutput1)");
                    uint featureLevel;
                    Check(D3D11CreateDevice(adapter, 0, IntPtr.Zero, 0x20, IntPtr.Zero, 0, 7,
                        out device, out featureLevel, out context), "D3D11CreateDevice");
                    Check(Method<DuplicateOutput>(output1, OutputDuplicateSlot)(output1, device, out duplication), "DuplicateOutput");
                }
                Release(ref adapter);
            }
            if (!selected) throw new InvalidOperationException("No attached output matches the requested display");
            DuplicationDesc duplicationDesc;
            Method<GetDuplicationDesc>(duplication, 7)(duplication, out duplicationDesc);
            if (duplicationDesc.Rotation != 1)
                throw new NotSupportedException("Rotated outputs require pixel rotation; this helper supports landscape identity rotation");
            if (duplicationDesc.Mode.Format != 87)
                throw new NotSupportedException("Expected DXGI_FORMAT_B8G8R8A8_UNORM");
            Width = checked((int)duplicationDesc.Mode.Width);
            Height = checked((int)duplicationDesc.Mode.Height);
            TextureDesc texture = new TextureDesc();
            texture.Width = (uint)Width;
            texture.Height = (uint)Height;
            texture.MipLevels = 1;
            texture.ArraySize = 1;
            texture.Format = 87;
            texture.SampleCount = 1;
            texture.Usage = 3;
            texture.CPUAccessFlags = 0x20000;
            Check(Method<CreateTexture2D>(device, DeviceCreateTexture2DSlot)(device, ref texture, IntPtr.Zero, out staging), "CreateTexture2D(staging)");
            pixels = Marshal.AllocHGlobal(checked(Width * Height * 4));
            bitmap = new Bitmap(Width, Height, checked(Width * 4), PixelFormat.Format32bppRgb, pixels);
            acquire = Method<AcquireFrame>(duplication, 8);
            releaseFrame = Method<ReleaseFrame>(duplication, 14);
            map = Method<MapResource>(context, ContextMapSlot);
            unmap = Method<UnmapResource>(context, ContextUnmapSlot);
            copy = Method<CopyResource>(context, ContextCopyResourceSlot);
        }
        catch
        {
            Dispose();
            throw;
        }
        finally
        {
            Release(ref output1);
            Release(ref output);
            Release(ref adapter);
            Release(ref factory);
        }
    }

    public bool ReadFrame(int timeoutMs)
    {
        if (duplication == IntPtr.Zero) throw new ObjectDisposedException("DesktopDuplication");
        if (timeoutMs < 0) throw new ArgumentOutOfRangeException("timeoutMs");
        IntPtr resource = IntPtr.Zero, texture = IntPtr.Zero;
        FrameInfo info;
        int result = acquire(duplication, (uint)timeoutMs, out info, out resource);
        AccumulatedFrames = 0;
        if (result == WaitTimeout) return false;
        Check(result, "AcquireNextFrame");
        try
        {
            AccumulatedFrames = info.AccumulatedFrames;
            ProtectedContentMaskedOut = info.ProtectedContentMaskedOut != 0;
            if (info.LastMouseUpdateTime != 0)
            {
                LastMouseQpc = info.LastMouseUpdateTime;
                PointerVisible = info.PointerVisible != 0;
                PointerX = info.PointerX;
                PointerY = info.PointerY;
            }
            if (info.LastPresentTime == 0) return false;
            Guid textureId = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
            Check(Marshal.QueryInterface(resource, ref textureId, out texture), "QueryInterface(ID3D11Texture2D)");
            copy(context, staging, texture);
            MappedResource mapped;
            Check(map(context, staging, 0, 1, 0, out mapped), "Map(staging)");
            try
            {
                int stride = checked(Width * 4);
                if (mapped.RowPitch < stride) throw new InvalidOperationException("GPU RowPitch is smaller than the image row");
                if (mapped.RowPitch == stride)
                    CopyMemory(pixels, mapped.Data, new UIntPtr(checked((uint)(stride * Height))));
                else
                    for (int row = 0; row < Height; row++)
                        CopyMemory(IntPtr.Add(pixels, checked(row * stride)),
                            IntPtr.Add(mapped.Data, checked(row * (int)mapped.RowPitch)), new UIntPtr((uint)stride));
            }
            finally { unmap(context, staging, 0); }
            LastPresentQpc = info.LastPresentTime;
            hasImage = true;
            return true;
        }
        finally
        {
            Release(ref texture);
            Release(ref resource);
            Check(releaseFrame(duplication), "ReleaseFrame");
        }
    }

    public void Dispose()
    {
        if (bitmap != null) { bitmap.Dispose(); bitmap = null; }
        if (pixels != IntPtr.Zero) { Marshal.FreeHGlobal(pixels); pixels = IntPtr.Zero; }
        Release(ref staging);
        Release(ref duplication);
        Release(ref context);
        Release(ref device);
    }
}
