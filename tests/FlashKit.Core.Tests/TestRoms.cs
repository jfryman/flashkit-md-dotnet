namespace FlashKit.Core.Tests;

static class TestRoms
{
    /// <summary>
    /// Synthetic ROM: every 32 KB block gets distinct content (so mirror
    /// probing can tell blocks apart), a name at 0x120, region at 0x1F0.
    /// </summary>
    public static byte[] MakeRom(int size, string name = "TEST GAME", string region = "U")
    {
        var rom = new byte[size];
        for (int i = 0; i < size; i++) rom[i] = (byte)((i >> 15) * 37 + (i & 0xFF));
        for (int i = 0x100; i < 0x200; i++) rom[i] = 0;
        for (int i = 0; i < name.Length; i++) rom[0x120 + i] = (byte)name[i];
        for (int i = 0; i < region.Length; i++) rom[0x1F0 + i] = (byte)region[i];
        return rom;
    }
}
