namespace FlashKit.Core.Tests;

static class TestRoms
{
    /// <summary>
    /// Synthetic ROM: every 32 KB block gets distinct content (so mirror
    /// probing can tell blocks apart), a system name at 0x100, a name at
    /// 0x120, ROM start/end addresses at 0x1A0/0x1A4, region at 0x1F0.
    /// <paramref name="system"/> is the 0x100 field ("SEGA GENESIS" for MD,
    /// "SEGA 32X" for a 32X cart).
    /// </summary>
    public static byte[] MakeRom(int size, string name = "TEST GAME", string region = "U",
        int? headerRomSize = null, string system = "SEGA GENESIS")
    {
        var rom = new byte[size];
        for (int i = 0; i < size; i++) rom[i] = (byte)((i >> 15) * 37 + (i & 0xFF));
        for (int i = 0x100; i < 0x200; i++) rom[i] = 0;
        for (int i = 0; i < system.Length && i < 16; i++) rom[0x100 + i] = (byte)system[i];
        for (int i = 0; i < name.Length; i++) rom[0x120 + i] = (byte)name[i];
        uint end = (uint)((headerRomSize ?? size) - 1);
        rom[0x1A4] = (byte)(end >> 24);
        rom[0x1A5] = (byte)(end >> 16);
        rom[0x1A6] = (byte)(end >> 8);
        rom[0x1A7] = (byte)end;
        for (int i = 0; i < region.Length; i++) rom[0x1F0 + i] = (byte)region[i];
        return rom;
    }
}
