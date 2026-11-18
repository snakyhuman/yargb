using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Jargb;

internal enum LightingEffect : byte { Off, Static, Breathing }
internal readonly record struct Lighting(LightingEffect Effect, byte Red, byte Green, byte Blue);
internal readonly record struct HeaderState(byte Mode, byte Red, byte Green, byte Blue, int BrightnessPercent);

internal sealed class MsiController : IDisposable
{
    // MSI Mystic Light 185-byte settings report, as documented by the OpenRGB controller.
    private const byte ReportId = 0x52;
    private const int ReportSize = 185;
    private const int SaveFlag = 184;
    private const int FirstHeader = 31;
    private const int SecondHeader = 42;
    private const int BrightnessMask = 0x7C;
    private const int BrightnessShift = 2;
    private const string Resource = "MSI-1462-7D98";
    internal const int BrightnessStep = 10;
    internal const int MaximumBrightness = 100;
    private readonly SafeFileHandle handle;

    internal MsiController()
    {
        Guid hid = new("4D1E55B2-F16F-11CF-88CB-001111000030");
        CheckConfig(CM_Get_Device_Interface_List_Size(out uint size, ref hid, null, 0));
        char[] list = new char[size];
        CheckConfig(CM_Get_Device_Interface_List(ref hid, null, list, size, 0));
        string[] paths = new string(list).Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(path => path.Contains("#vid_1462&pid_7d98#", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (paths.Length != 1)
            throw new IOException($"Ожидался один контроллер MSI 1462:7D98. Найдено: {paths.Length}.");

        handle = CreateFile(paths[0], 0xC0000000, 3, 0, 3, 0, 0);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, $"Не удалось открыть MSI 1462:7D98 (Win32 {error}).");
        }
    }

    internal (HeaderState First, HeaderState Second) Read() => LocalAccess.Run(Resource, ReadHeaders);

    private (HeaderState First, HeaderState Second) ReadHeaders()
    {
        byte[] report = ReadReport();
        return (ReadHeader(report, FirstHeader), ReadHeader(report, SecondHeader));
    }

    internal static bool SupportsBrightness(int percent) =>
        percent is >= BrightnessStep and <= MaximumBrightness && percent % BrightnessStep == 0;

    internal void Apply(Lighting lighting, int brightnessPercent) =>
        LocalAccess.Run(Resource, () => ApplyLighting(lighting, brightnessPercent));

    private void ApplyLighting(Lighting lighting, int brightnessPercent)
    {
        CheckBrightness(brightnessPercent);
        byte[] previous = ReadReport();
        byte[] next = (byte[])previous.Clone();
        WriteHeader(next.AsSpan(FirstHeader, 10), lighting, brightnessPercent);
        WriteHeader(next.AsSpan(SecondHeader, 10), lighting, brightnessPercent);
        SendAndVerify(previous, next, $"{lighting.Effect}, RGB {lighting.Red:X2}{lighting.Green:X2}{lighting.Blue:X2}, {brightnessPercent}%");
    }

    internal void ApplyBrightness(int percent) =>
        LocalAccess.Run(Resource, () => ApplyBrightnessToHeaders(percent));

    private void ApplyBrightnessToHeaders(int percent)
    {
        CheckBrightness(percent);
        byte[] previous = ReadReport();
        byte[] next = (byte[])previous.Clone();
        next[FirstHeader + 4] = WithBrightness(next[FirstHeader + 4], percent);
        next[SecondHeader + 4] = WithBrightness(next[SecondHeader + 4], percent);
        SendIfChanged(previous, next, $"яркость {percent}%");
    }

    internal void TurnOff() => LocalAccess.Run(Resource, () =>
    {
        byte[] previous = ReadReport();
        byte[] next = (byte[])previous.Clone();
        next[FirstHeader] = (byte)LightingEffect.Off;
        next[SecondHeader] = (byte)LightingEffect.Off;
        SendIfChanged(previous, next, "выключение");
    });

    internal void TurnOn(int? brightnessPercent) => LocalAccess.Run(Resource, () =>
    {
        if (brightnessPercent is int percent)
            CheckBrightness(percent);
        byte[] previous = ReadReport();
        byte[] next = (byte[])previous.Clone();
        EnableHeader(next.AsSpan(FirstHeader, 10), brightnessPercent);
        EnableHeader(next.AsSpan(SecondHeader, 10), brightnessPercent);
        SendIfChanged(previous, next, "включение");
    });

    private static void EnableHeader(Span<byte> header, int? brightnessPercent)
    {
        if (header[0] != (byte)LightingEffect.Off)
            return;

        int current = ReadBrightness(header[4]);
        int brightness = brightnessPercent ?? (SupportsBrightness(current) ? current : MaximumBrightness);
        WriteHeader(header, new(LightingEffect.Static, header[1], header[2], header[3]), brightness);
    }

    private void SendIfChanged(byte[] previous, byte[] next, string requested)
    {
        if (!previous.AsSpan().SequenceEqual(next))
            SendAndVerify(previous, next, requested);
    }

    private void SendAndVerify(byte[] previous, byte[] next, string requested)
    {
        // The controller expects the previous settings report before the updated one.
        Send(previous);
        Send(next);
        for (int attempt = 0; attempt < 8; attempt++)
        {
            Thread.Sleep(60);
            byte[] actual = ReadReport();
            if (ReadHeader(actual, FirstHeader) == ReadHeader(next, FirstHeader) &&
                ReadHeader(actual, SecondHeader) == ReadHeader(next, SecondHeader))
                return;
        }

        throw new IOException($"MSI 1462:7D98 не подтвердил JARGB_V2: {requested}.");
    }

    private static HeaderState ReadHeader(byte[] report, int offset) =>
        new(report[offset], report[offset + 1], report[offset + 2], report[offset + 3], ReadBrightness(report[offset + 4]));

    private static int ReadBrightness(byte flags) => ((flags & BrightnessMask) >> BrightnessShift) * BrightnessStep;

    private static void WriteHeader(Span<byte> header, Lighting lighting, int brightnessPercent)
    {
        header[0] = (byte)lighting.Effect;
        header[1] = header[5] = lighting.Red;
        header[2] = header[6] = lighting.Green;
        header[3] = header[7] = lighting.Blue;
        if (lighting.Effect == LightingEffect.Off)
            return;

        header[4] = WithBrightness((byte)(header[4] & 0x80), brightnessPercent);
        header[8] |= 0x80;
    }

    private static byte WithBrightness(byte flags, int percent) =>
        (byte)((flags & ~BrightnessMask) | ((percent / BrightnessStep) << BrightnessShift));

    private static void CheckBrightness(int percent)
    {
        if (!SupportsBrightness(percent))
            throw new ArgumentOutOfRangeException(nameof(percent), percent, "Expected 10–100 percent in 10-point steps.");
    }

    private byte[] ReadReport()
    {
        return RetryIo(() =>
        {

            byte[] report = new byte[ReportSize + 1];
            report[0] = ReportId;
            // IOCTL_HID_GET_FEATURE exposes the actual report length.
            if (!DeviceIoControl(handle, 0x000B0192, report, report.Length, report, report.Length, out int read, 0))
                throw LastError("чтение");
            if (read is not (ReportSize or ReportSize + 1) || report[0] != ReportId)
                throw new IOException($"Неподдерживаемый отчёт MSI 1462:7D98. ID 0x{report[0]:X2}, размер {read}.");
            Array.Resize(ref report, ReportSize);
            report[SaveFlag] = 0;
            return report;
        });
    }

    private void Send(byte[] report)
    {
        RetryIo(() =>
        {
            if (!HidD_SetFeature(handle, report, report.Length))
                throw LastError("запись");
            return true;
        });
    }

    private static T RetryIo<T>(Func<T> action)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { return action(); }
            catch (Win32Exception error) when (attempt < 3)
            {
                Trace.TraceWarning("HID request failed. Attempt={0} NativeErrorCode={1}", attempt, error.NativeErrorCode);
                Thread.Sleep(70);
            }
        }
    }

    private static Win32Exception LastError(string operation)
    {
        int error = Marshal.GetLastWin32Error();
        return new Win32Exception(error, $"MSI 1462:7D98, отчёт 0x52, {operation}: Win32 {error}.");
    }

    private static void CheckConfig(uint status)
    {
        if (status != 0)
            throw new IOException($"Перечисление HID через cfgmgr32 завершилось с кодом 0x{status:X}.");
    }

    public void Dispose() => handle.Dispose();

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_Interface_List_Size(out uint size, ref Guid guid, string? device, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_Interface_List(ref Guid guid, string? device, [Out] char[] list, uint size, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint sharing, nint security,
        uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, int inputSize,
        [In, Out] byte[] output, int outputSize, out int read, nint overlapped);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_SetFeature(SafeFileHandle device, byte[] report, int length);
}
