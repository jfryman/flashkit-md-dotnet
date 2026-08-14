using FlashKit.Core;

namespace FlashKit.Core.Tests;

public class RomPatchTests
{
    static readonly byte[] Rom = { 10, 20, 30, 40, 50, 60, 70, 80 };
    static readonly byte[] Modified = { 10, 20, 99, 40, 50, 60, 70, 80 };

    [Fact]
    public void apply_sniffs_an_ips_patch_from_its_magic()
    {
        var patch = IpsPatch.Create(Rom, Modified);

        Assert.Equal(PatchFormat.Ips, RomPatch.Detect(patch));
        Assert.Equal(Modified, RomPatch.Apply(Rom, patch));
    }

    [Fact]
    public void apply_sniffs_an_xdelta_patch_from_its_magic()
    {
        var patch = XdeltaPatch.Create(Rom, Modified);

        Assert.Equal(PatchFormat.Xdelta, RomPatch.Detect(patch));
        Assert.Equal(Modified, RomPatch.Apply(Rom, patch));
    }

    [Fact]
    public void detect_rejects_unrecognized_bytes()
    {
        Assert.Throws<NotSupportedException>(() => RomPatch.Detect(new byte[] { 1, 2, 3, 4, 5 }));
    }

    [Theory]
    [InlineData("hack.ips", PatchFormat.Ips)]
    [InlineData("hack.xdelta", PatchFormat.Xdelta)]
    [InlineData("hack.XDELTA", PatchFormat.Xdelta)]
    [InlineData("hack.vcdiff", PatchFormat.Xdelta)]
    [InlineData("hack.xd3", PatchFormat.Xdelta)]
    [InlineData("hack", PatchFormat.Ips)]
    public void format_for_path_keys_off_the_extension(string path, PatchFormat expected)
    {
        Assert.Equal(expected, RomPatch.FormatForPath(path));
    }

    [Theory]
    [InlineData(PatchFormat.Ips, "IPS")]
    [InlineData(PatchFormat.Xdelta, "xdelta")]
    public void create_produces_the_requested_format(PatchFormat format, string display)
    {
        var patch = RomPatch.Create(format, Rom, Modified);

        Assert.Equal(format, RomPatch.Detect(patch));
        Assert.Equal(Modified, RomPatch.Apply(Rom, patch));
        Assert.Equal(display, RomPatch.DisplayName(format));
    }
}
