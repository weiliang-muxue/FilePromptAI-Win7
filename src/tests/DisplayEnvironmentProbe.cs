using System;
using System.Runtime.InteropServices;

internal static class DisplayEnvironmentProbe
{
    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;
    private const int LogPixelsX = 88;
    private const int LogPixelsY = 90;
    private const int EnumCurrentSettings = -1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DeviceMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        public short SpecVersion;
        public short DriverVersion;
        public short Size;
        public short DriverExtra;
        public int Fields;
        public int PositionX;
        public int PositionY;
        public int DisplayOrientation;
        public int DisplayFixedOutput;
        public short Color;
        public short Duplex;
        public short YResolution;
        public short TTOption;
        public short Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string FormName;
        public short LogPixels;
        public int BitsPerPel;
        public int PelsWidth;
        public int PelsHeight;
        public int DisplayFlags;
        public int DisplayFrequency;
        public int ICMMethod;
        public int ICMIntent;
        public int MediaType;
        public int DitherType;
        public int Reserved1;
        public int Reserved2;
        public int PanningWidth;
        public int PanningHeight;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessDPIAware();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettings(
        string deviceName,
        int modeNumber,
        ref DeviceMode deviceMode);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int ReleaseDC(IntPtr window, IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(IntPtr deviceContext, int index);

    private static int Main(string[] args)
    {
        if (args.Length == 1 && string.Equals(
            args[0],
            "--self-test",
            StringComparison.Ordinal))
        {
            return RunSelfTest();
        }

        if (args.Length != 0)
        {
            Console.Error.WriteLine("Usage: DisplayEnvironmentProbe.exe [--self-test]");
            return 2;
        }

        try
        {
            if (!IsProcessDPIAware())
            {
                return Fail(
                    3,
                    "The display probe is not system-DPI-aware; its manifest was not applied.");
            }

            int metricsWidth = GetSystemMetrics(SmCxScreen);
            int metricsHeight = GetSystemMetrics(SmCyScreen);
            if (metricsWidth <= 0 || metricsHeight <= 0)
            {
                return Fail(4, "GetSystemMetrics did not return a valid primary display size.");
            }

            DeviceMode mode = new DeviceMode();
            mode.Size = (short)Marshal.SizeOf(typeof(DeviceMode));
            if (!EnumDisplaySettings(null, EnumCurrentSettings, ref mode) ||
                mode.PelsWidth <= 0 || mode.PelsHeight <= 0)
            {
                return Fail(
                    5,
                    "EnumDisplaySettings could not read the current primary display mode.");
            }

            IntPtr deviceContext = GetDC(IntPtr.Zero);
            if (deviceContext == IntPtr.Zero)
            {
                return Fail(6, "GetDC could not open the primary display device context.");
            }

            int dpiX;
            int dpiY;
            int releaseResult;
            try
            {
                dpiX = GetDeviceCaps(deviceContext, LogPixelsX);
                dpiY = GetDeviceCaps(deviceContext, LogPixelsY);
            }
            finally
            {
                releaseResult = ReleaseDC(IntPtr.Zero, deviceContext);
            }

            if (releaseResult != 1)
            {
                return Fail(7, "ReleaseDC failed after reading the primary display DPI.");
            }
            if (dpiX <= 0 || dpiY <= 0)
            {
                return Fail(8, "GetDeviceCaps did not return a valid primary display DPI.");
            }

            string reason;
            if (!IsAcceptedEnvironment(
                true,
                metricsWidth,
                metricsHeight,
                mode.PelsWidth,
                mode.PelsHeight,
                dpiX,
                dpiY,
                out reason))
            {
                return Fail(9, reason);
            }

            Console.WriteLine(
                "PASS | fullhd100 display | aware=true | metrics=" +
                metricsWidth + "x" + metricsHeight + " | mode=" +
                mode.PelsWidth + "x" + mode.PelsHeight + " | dpi=" +
                dpiX + "x" + dpiY);
            return 0;
        }
        catch (EntryPointNotFoundException exception)
        {
            return Fail(10, "A required Windows 7 display API is unavailable: " + exception.Message);
        }
        catch (DllNotFoundException exception)
        {
            return Fail(11, "A required Windows display library is unavailable: " + exception.Message);
        }
        catch (Exception exception)
        {
            return Fail(12, "Display environment verification failed: " + exception.Message);
        }
    }

    private static bool IsAcceptedEnvironment(
        bool processDpiAware,
        int metricsWidth,
        int metricsHeight,
        int modeWidth,
        int modeHeight,
        int dpiX,
        int dpiY,
        out string reason)
    {
        if (!processDpiAware)
        {
            reason = "The display probe is not system-DPI-aware.";
            return false;
        }
        if (metricsWidth != 1920 || metricsHeight != 1080)
        {
            reason = "FullHd100 requires 1920x1080 primary screen metrics; actual " +
                metricsWidth + "x" + metricsHeight + ".";
            return false;
        }
        if (modeWidth != 1920 || modeHeight != 1080)
        {
            reason = "FullHd100 requires a 1920x1080 primary display mode; actual " +
                modeWidth + "x" + modeHeight + ".";
            return false;
        }
        if (metricsWidth != modeWidth || metricsHeight != modeHeight)
        {
            reason = "Primary screen metrics do not match the current display mode.";
            return false;
        }
        if (dpiX != 96 || dpiY != 96)
        {
            reason = "FullHd100 requires 96x96 system DPI; actual " +
                dpiX + "x" + dpiY + ".";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static int RunSelfTest()
    {
        string reason;
        if (!IsAcceptedEnvironment(true, 1920, 1080, 1920, 1080, 96, 96, out reason) ||
            IsAcceptedEnvironment(false, 1920, 1080, 1920, 1080, 96, 96, out reason) ||
            IsAcceptedEnvironment(true, 1600, 900, 1920, 1080, 96, 96, out reason) ||
            IsAcceptedEnvironment(true, 1920, 1080, 1600, 900, 96, 96, out reason) ||
            IsAcceptedEnvironment(true, 1920, 1080, 1920, 1080, 120, 96, out reason) ||
            IsAcceptedEnvironment(true, 1920, 1080, 1920, 1080, 96, 120, out reason) ||
            IsAcceptedEnvironment(true, 0, 0, 1920, 1080, 96, 96, out reason) ||
            IsAcceptedEnvironment(true, 1920, 1080, 0, 0, 96, 96, out reason) ||
            IsAcceptedEnvironment(true, 1920, 1080, 1920, 1080, 0, 0, out reason))
        {
            return Fail(20, "Display environment negative self-test failed.");
        }

        Console.WriteLine("PASS | fullhd100 display self-test");
        return 0;
    }

    private static int Fail(int code, string message)
    {
        Console.Error.WriteLine("FAIL | fullhd100 display | " + message);
        return code;
    }
}
