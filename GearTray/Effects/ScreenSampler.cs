using System.Runtime.InteropServices;
using GearTray.Util;

namespace GearTray.Effects;

/// <summary>Average colors of a monitor's picture on a cols × rows grid (row-major, RGB bytes).</summary>
internal interface IScreenSampler : IDisposable
{
    string Method { get; }
    /// <summary>Fills grid (cols*rows*3 bytes). Returns false if nothing could be captured this time.</summary>
    bool Sample(Span<byte> grid, int cols, int rows);
}

internal static class ScreenSamplers
{
    /// <summary>GPU desktop duplication when possible, GDI otherwise.</summary>
    public static IScreenSampler Create(Screen screen)
    {
        try { return new DesktopDuplicationSampler(screen); }
        catch (Exception ex)
        {
            Log.Warn($"Desktop duplication unavailable ({ex.Message}); using the slower GDI capture");
            return new GdiSampler(screen);
        }
    }

    /// <summary>Average a BGRA image into a cols × rows grid of RGB bytes.</summary>
    public static unsafe void Downscale(byte* bgra, int width, int height, int stride, Span<byte> grid, int cols, int rows)
    {
        for (int gy = 0; gy < rows; gy++)
        {
            int y0 = gy * height / rows, y1 = Math.Max(y0 + 1, (gy + 1) * height / rows);
            for (int gx = 0; gx < cols; gx++)
            {
                int x0 = gx * width / cols, x1 = Math.Max(x0 + 1, (gx + 1) * width / cols);
                long r = 0, g = 0, b = 0; int n = 0;
                for (int y = y0; y < y1 && y < height; y++)
                {
                    byte* p = bgra + y * stride + x0 * 4;
                    for (int x = x0; x < x1 && x < width; x++, p += 4) { b += p[0]; g += p[1]; r += p[2]; n++; }
                }
                int o = (gy * cols + gx) * 3;
                if (n == 0) { grid[o] = grid[o + 1] = grid[o + 2] = 0; continue; }
                grid[o] = (byte)(r / n); grid[o + 1] = (byte)(g / n); grid[o + 2] = (byte)(b / n);
            }
        }
    }
}

/// <summary>
/// DXGI Desktop Duplication: the GPU copies the monitor's picture into a mip-mapped texture and shrinks it,
/// so only a ~50×20 pixel image is read back. Cheap enough to run while gaming.
/// </summary>
internal sealed unsafe class DesktopDuplicationSampler : IScreenSampler
{
    private static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private static readonly Guid IID_IDXGIOutput1 = new("00cddea8-939b-4b83-a340-a685226666cc");
    private static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    private const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);
    private const int DXGI_ERROR_ACCESS_LOST = unchecked((int)0x887A0026);
    private const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);

    [DllImport("dxgi.dll")] private static extern int CreateDXGIFactory1(Guid* riid, IntPtr* factory);
    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr levels,
        uint numLevels, uint sdkVersion, IntPtr* device, int* featureLevel, IntPtr* context);

    [StructLayout(LayoutKind.Sequential)]
    private struct TextureDesc { public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CpuAccess, MiscFlags; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Mapped { public byte* Data; public uint RowPitch, DepthPitch; }
    [StructLayout(LayoutKind.Sequential)]
    private struct OutputDesc { public fixed char DeviceName[32]; public int Left, Top, Right, Bottom, Attached, Rotation; public IntPtr Monitor; }

    private readonly string _deviceName;
    private readonly Screen _screen;
    private IntPtr _device, _context, _output1, _dup, _mipTex, _srv, _staging;
    private uint _width, _height, _level, _levelW, _levelH;
    private byte[]? _last;

    public string Method => "GPU desktop duplication";

    /// <summary>Diagnostics: frames handed out by Windows, and how many of them carried a new picture.</summary>
    public int FramesAcquired { get; private set; }
    public int PictureFrames { get; private set; }

    public DesktopDuplicationSampler(Screen screen)
    {
        _screen = screen;
        _deviceName = screen.DeviceName;
        Open();
    }

    // vtable helpers
    private static void** VT(IntPtr p) => *(void***)p;
    private static void Release(ref IntPtr p)
    {
        if (p != IntPtr.Zero) ((delegate* unmanaged<IntPtr, uint>)VT(p)[2])(p);
        p = IntPtr.Zero;
    }
    private static int QueryInterface(IntPtr p, Guid iid, out IntPtr result)
    {
        IntPtr r;
        int hr = ((delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)VT(p)[0])(p, &iid, &r);
        result = r;
        return hr;
    }
    private static void Check(int hr, string what)
    {
        if (hr < 0) throw new COMException($"{what} failed (0x{hr:X8})", hr);
    }

    private void Open()
    {
        IntPtr factory = IntPtr.Zero, adapter = IntPtr.Zero, output = IntPtr.Zero;
        try
        {
            Guid fid = IID_IDXGIFactory1;
            Check(CreateDXGIFactory1(&fid, &factory), "CreateDXGIFactory1");
            // Find the adapter + output that drive this monitor.
            for (uint a = 0; output == IntPtr.Zero; a++)
            {
                IntPtr ad;
                int hr = ((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)VT(factory)[7])(factory, a, &ad); // EnumAdapters
                if (hr == DXGI_ERROR_NOT_FOUND) throw new InvalidOperationException($"No graphics output found for {_deviceName}");
                Check(hr, "EnumAdapters");
                for (uint o = 0; ; o++)
                {
                    IntPtr op;
                    hr = ((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)VT(ad)[7])(ad, o, &op); // EnumOutputs
                    if (hr == DXGI_ERROR_NOT_FOUND) break;
                    Check(hr, "EnumOutputs");
                    OutputDesc d;
                    ((delegate* unmanaged<IntPtr, OutputDesc*, int>)VT(op)[7])(op, &d); // GetDesc
                    if (new string(d.DeviceName).Equals(_deviceName, StringComparison.OrdinalIgnoreCase)) { output = op; adapter = ad; break; }
                    Release(ref op);
                }
                if (output == IntPtr.Zero) Release(ref ad);
            }

            IntPtr dev, ctx; int level;
            Check(D3D11CreateDevice(adapter, 0 /* UNKNOWN, adapter given */, IntPtr.Zero, 0, IntPtr.Zero, 0, 7, &dev, &level, &ctx), "D3D11CreateDevice");
            _device = dev; _context = ctx;
            Check(QueryInterface(output, IID_IDXGIOutput1, out _output1), "IDXGIOutput1");
            IntPtr dup;
            Check(((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, int>)VT(_output1)[22])(_output1, _device, &dup), "DuplicateOutput");
            _dup = dup;
        }
        catch
        {
            Dispose();
            throw;
        }
        finally
        {
            Release(ref output);
            Release(ref adapter);
            Release(ref factory);
        }
    }

    private void CreateTextures(uint width, uint height)
    {
        ReleaseTextures();
        _width = width; _height = height;
        // Pick the smallest mip level that still has at least ~2 pixels per grid cell.
        _level = 0;
        while ((width >> (int)(_level + 1)) >= 44 && (height >> (int)(_level + 1)) >= 12) _level++;
        _levelW = Math.Max(1, width >> (int)_level);
        _levelH = Math.Max(1, height >> (int)_level);

        var mip = new TextureDesc
        {
            Width = width, Height = height, MipLevels = _level + 1, ArraySize = 1, Format = 87 /* B8G8R8A8_UNORM */,
            SampleCount = 1, Usage = 0 /* DEFAULT */, BindFlags = 0x8 | 0x20 /* SHADER_RESOURCE | RENDER_TARGET */, MiscFlags = 0x1 /* GENERATE_MIPS */,
        };
        IntPtr t, v, s;
        Check(((delegate* unmanaged<IntPtr, TextureDesc*, IntPtr, IntPtr*, int>)VT(_device)[5])(_device, &mip, IntPtr.Zero, &t), "CreateTexture2D(mips)");
        _mipTex = t;
        Check(((delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr*, int>)VT(_device)[7])(_device, _mipTex, IntPtr.Zero, &v), "CreateShaderResourceView");
        _srv = v;
        var staging = new TextureDesc
        {
            Width = _levelW, Height = _levelH, MipLevels = 1, ArraySize = 1, Format = 87, SampleCount = 1,
            Usage = 3 /* STAGING */, CpuAccess = 0x20000 /* READ */,
        };
        Check(((delegate* unmanaged<IntPtr, TextureDesc*, IntPtr, IntPtr*, int>)VT(_device)[5])(_device, &staging, IntPtr.Zero, &s), "CreateTexture2D(staging)");
        _staging = s;
    }

    public bool Sample(Span<byte> grid, int cols, int rows)
    {
        if (_dup == IntPtr.Zero)
        {
            try { Open(); } catch { return false; }
        }
        var info = stackalloc byte[64];
        IntPtr resource;
        int hr = ((delegate* unmanaged<IntPtr, uint, byte*, IntPtr*, int>)VT(_dup)[8])(_dup, 40, info, &resource); // AcquireNextFrame
        if (hr == DXGI_ERROR_WAIT_TIMEOUT) return Reuse(grid, cols, rows);   // picture unchanged
        if (hr == DXGI_ERROR_ACCESS_LOST || hr < 0)
        {
            // Mode change, UAC prompt, lock screen…: start over next time.
            Release(ref _dup);
            return Reuse(grid, cols, rows);
        }
        IntPtr tex = IntPtr.Zero;
        try
        {
            // DXGI_OUTDUPL_FRAME_INFO.AccumulatedFrames (offset 16): 0 = only the mouse pointer moved, and the
            // picture may not even be filled in yet. Only frames with a new picture are read.
            FramesAcquired++;
            if (*(uint*)(info + 16) == 0) return Reuse(grid, cols, rows);
            PictureFrames++;
            if (QueryInterface(resource, IID_ID3D11Texture2D, out tex) < 0) return Reuse(grid, cols, rows);
            TextureDesc d;
            ((delegate* unmanaged<IntPtr, TextureDesc*, void>)VT(tex)[10])(tex, &d); // GetDesc
            if (d.Width != _width || d.Height != _height || _mipTex == IntPtr.Zero) CreateTextures(d.Width, d.Height);

            var copy = (delegate* unmanaged<IntPtr, IntPtr, uint, uint, uint, uint, IntPtr, uint, IntPtr, void>)VT(_context)[46]; // CopySubresourceRegion
            copy(_context, _mipTex, 0, 0, 0, 0, tex, 0, IntPtr.Zero);
            ((delegate* unmanaged<IntPtr, IntPtr, void>)VT(_context)[54])(_context, _srv); // GenerateMips
            copy(_context, _staging, 0, 0, 0, 0, _mipTex, _level, IntPtr.Zero);

            Mapped m;
            if (((delegate* unmanaged<IntPtr, IntPtr, uint, uint, uint, Mapped*, int>)VT(_context)[14])(_context, _staging, 0, 1 /* READ */, 0, &m) < 0)
                return Reuse(grid, cols, rows);
            try { ScreenSamplers.Downscale(m.Data, (int)_levelW, (int)_levelH, (int)m.RowPitch, grid, cols, rows); }
            finally { ((delegate* unmanaged<IntPtr, IntPtr, uint, void>)VT(_context)[15])(_context, _staging, 0); } // Unmap
            _last = grid.ToArray();
            return true;
        }
        finally
        {
            Release(ref tex);
            Release(ref resource);
            ((delegate* unmanaged<IntPtr, int>)VT(_dup)[14])(_dup); // ReleaseFrame
        }
    }

    /// <summary>
    /// No new picture: keep the last colors. Duplication only delivers frames when something changes, so a still
    /// screen right after starting gets one GDI snapshot instead.
    /// </summary>
    private bool Reuse(Span<byte> grid, int cols, int rows)
    {
        if (_last is null || _last.Length != grid.Length)
        {
            using var gdi = new GdiSampler(_screen);
            if (!gdi.Sample(grid, cols, rows)) return false;
            _last = grid.ToArray();
            return true;
        }
        _last.CopyTo(grid);
        return true;
    }

    private void ReleaseTextures()
    {
        Release(ref _staging);
        Release(ref _srv);
        Release(ref _mipTex);
    }

    public void Dispose()
    {
        ReleaseTextures();
        Release(ref _dup);
        Release(ref _output1);
        Release(ref _context);
        Release(ref _device);
    }
}

/// <summary>Fallback: GDI StretchBlt of the whole monitor (≈20 ms per frame at 3440×1440, so it runs slower).</summary>
internal sealed unsafe class GdiSampler : IScreenSampler
{
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, byte* bmi, uint usage, byte** bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] private static extern bool StretchBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, int sw, int sh, int rop);

    private const int W = 44, H = 12;
    private readonly Rectangle _bounds;
    private readonly IntPtr _memDc, _bitmap;
    private readonly byte* _bits;

    public string Method => "GDI screen copy";

    public GdiSampler(Screen screen)
    {
        _bounds = screen.Bounds;
        IntPtr scr = GetDC(IntPtr.Zero);
        _memDc = CreateCompatibleDC(scr);
        ReleaseDC(IntPtr.Zero, scr);
        // BITMAPINFOHEADER for a top-down 32-bit DIB
        byte* bmi = stackalloc byte[44];
        new Span<byte>(bmi, 44).Clear();
        *(int*)bmi = 40; *(int*)(bmi + 4) = W; *(int*)(bmi + 8) = -H; *(short*)(bmi + 12) = 1; *(short*)(bmi + 14) = 32;
        byte* bits;
        _bitmap = CreateDIBSection(_memDc, bmi, 0, &bits, IntPtr.Zero, 0);
        _bits = bits;
        SelectObject(_memDc, _bitmap);
        SetStretchBltMode(_memDc, 4 /* HALFTONE */);
    }

    public bool Sample(Span<byte> grid, int cols, int rows)
    {
        IntPtr scr = GetDC(IntPtr.Zero);
        try
        {
            if (!StretchBlt(_memDc, 0, 0, W, H, scr, _bounds.X, _bounds.Y, _bounds.Width, _bounds.Height, 0x00CC0020)) return false;
        }
        finally { ReleaseDC(IntPtr.Zero, scr); }
        ScreenSamplers.Downscale(_bits, W, H, W * 4, grid, cols, rows);
        return true;
    }

    public void Dispose()
    {
        DeleteDC(_memDc);
        DeleteObject(_bitmap);
    }
}
