using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Polaris.Student;

/// <summary>The drawn star as an icon of Windows (32-bit colour with alpha, no file).</summary>
[SupportedOSPlatform("windows")]
internal static unsafe class IconHandle
{
    public static nint Create(int size)
    {
        var header = new Native.BITMAPINFOHEADER
        {
            biSize = sizeof(Native.BITMAPINFOHEADER),
            biWidth = size,
            biHeight = -size, // rows from the top
            biPlanes = 1,
            biBitCount = 32,
        };
        var color = Native.CreateDIBSection(0, ref header, 0, out var bits, 0, 0);
        if (color == 0)
        {
            throw new InvalidOperationException("Obrázok ikony sa nedá vytvoriť.");
        }

        var mask = Native.CreateBitmap(size, size, 1, 1, 0);
        try
        {
            var pixels = StarIcon.Pixels(size);
            Marshal.Copy(Array.ConvertAll(pixels, p => unchecked((int)p)), 0, bits, pixels.Length);
            var info = new Native.ICONINFO { fIcon = 1, hbmMask = mask, hbmColor = color };
            var icon = Native.CreateIconIndirect(ref info);
            return icon != 0 ? icon : throw new InvalidOperationException("Ikona sa nedá vytvoriť (" + Marshal.GetLastPInvokeError() + ").");
        }
        finally
        {
            Native.DeleteObject(color);
            Native.DeleteObject(mask);
        }
    }
}

/// <summary>For the test mode: makes the icon in Windows and frees it again.</summary>
[SupportedOSPlatform("windows")]
public static class Probe
{
    public static void Icon(int size) => Native.DestroyIcon(IconHandle.Create(size));
}
