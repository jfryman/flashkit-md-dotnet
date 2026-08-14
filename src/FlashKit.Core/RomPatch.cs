namespace FlashKit.Core;

/// <summary>Patch container formats the front-ends can apply and create.</summary>
public enum PatchFormat { Ips, Xdelta }

/// <summary>
/// Format-dispatching front door over <see cref="IpsPatch"/> and
/// <see cref="XdeltaPatch"/>: Apply sniffs the patch bytes (the IPS "PATCH"
/// magic vs the VCDIFF 0xD6C3C4 one), Create takes the format the caller
/// picked — front-ends derive it from the output file name the user chose
/// via <see cref="FormatForPath"/>.
/// </summary>
public static class RomPatch
{
    public static PatchFormat Detect(byte[] patch)
    {
        if (patch.Length >= 5 && patch.AsSpan(0, 5).SequenceEqual("PATCH"u8)) return PatchFormat.Ips;
        if (patch.Length >= 3 && patch[0] == 0xD6 && patch[1] == 0xC3 && patch[2] == 0xC4) return PatchFormat.Xdelta;
        throw new NotSupportedException("not a recognized patch (expected an IPS \"PATCH\" or xdelta VCDIFF header)");
    }

    public static byte[] Apply(byte[] rom, byte[] patch) =>
        Detect(patch) == PatchFormat.Ips ? IpsPatch.Apply(rom, patch) : XdeltaPatch.Apply(rom, patch);

    public static byte[] Create(PatchFormat format, byte[] original, byte[] modified) =>
        format == PatchFormat.Ips ? IpsPatch.Create(original, modified) : XdeltaPatch.Create(original, modified);

    /// <summary>.xdelta/.vcdiff/.xd3 mean xdelta; anything else (the .ips
    /// default) means IPS.</summary>
    public static PatchFormat FormatForPath(string path)
    {
        string ext = Path.GetExtension(path);
        return ext.Equals(".xdelta", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".vcdiff", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".xd3", StringComparison.OrdinalIgnoreCase)
            ? PatchFormat.Xdelta : PatchFormat.Ips;
    }

    /// <summary>The name front-ends print: "IPS" or "xdelta".</summary>
    public static string DisplayName(PatchFormat format) => format == PatchFormat.Ips ? "IPS" : "xdelta";
}
