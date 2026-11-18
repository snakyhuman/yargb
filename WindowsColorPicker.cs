using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Jargb;

internal static class WindowsColorPicker
{
    internal static Lighting? Choose(nint owner, Lighting current, IReadOnlyList<Lighting> presets)
    {
        uint[] custom = new uint[16];
        Array.Fill(custom, 0x00FFFFFFu);
        for (int i = 0; i < presets.Count; i++)
            custom[i] = ToColorRef(presets[i]);

        GCHandle pinned = GCHandle.Alloc(custom, GCHandleType.Pinned);
        try
        {
            ChooseColorData data = new()
            {
                Size = Marshal.SizeOf<ChooseColorData>(),
                Owner = owner,
                Result = ToColorRef(current),
                CustomColors = pinned.AddrOfPinnedObject(),
                Flags = 0x103 // CC_RGBINIT | CC_FULLOPEN | CC_ANYCOLOR
            };
            if (ChooseColorW(ref data))
                return new Lighting(LightingEffect.Static, (byte)data.Result, (byte)(data.Result >> 8), (byte)(data.Result >> 16));

            int error = CommDlgExtendedError();
            if (error != 0)
                throw new Win32Exception(error, $"Не удалось открыть палитру Windows (CommDlg 0x{error:X}).");

            return null;
        }
        finally
        {
            pinned.Free();
        }
    }

    private static uint ToColorRef(Lighting color) => (uint)(color.Red | (color.Green << 8) | (color.Blue << 16));

    [StructLayout(LayoutKind.Sequential)]
    private struct ChooseColorData
    {
        public int Size;
        public nint Owner;
        public nint Instance;
        public uint Result;
        public nint CustomColors;
        public uint Flags;
        public nint Data;
        public nint Hook;
        public nint Template;
    }

    [DllImport("comdlg32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChooseColorW(ref ChooseColorData data);

    [DllImport("comdlg32.dll", ExactSpelling = true)]
    private static extern int CommDlgExtendedError();
}
