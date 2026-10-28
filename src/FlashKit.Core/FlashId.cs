using System.Globalization;

namespace FlashKit.Core;

/// <summary>
/// Result of the read-only flash identification probe
/// (<see cref="FlashKitSession.TryIdentifyFlash"/>). <paramref name="CfiOk"/>
/// is true when the chip answered the CFI query with "QRY";
/// <paramref name="SizeBytes"/> is the CFI device size (2^n bytes), 0 when
/// unknown. <paramref name="Manufacturer"/> and <paramref name="DeviceId"/>
/// come from AMD/Spansion autoselect: DeviceId holds three words for the
/// extended-ID scheme (first word ending in 0x7E), one otherwise, and is
/// empty when the chip did not enter autoselect. <paramref name="ByteWide"/>
/// marks a pair of 8-bit chips where only the low-byte chip answered, so
/// the IDs are byte values (typical of repro carts).
/// </summary>
public sealed record FlashId(bool CfiOk, int SizeBytes, ushort Manufacturer, IReadOnlyList<ushort> DeviceId,
    bool ByteWide = false)
{
    // Known parts, keyed by IdKey ("0001:227E/2210/2200" for a 16-bit chip,
    // "C2:A4" for an 8-bit pair). Add entries only from real-hardware
    // autoselect reads or datasheets.
    static readonly Dictionary<string, string> Chips = new()
    {
        ["0001:227E/2210/2200"] = "Spansion S29GL064N family",
        ["C2:A4"] = "Macronix MX29F040",
    };

    // JEDEC manufacturer codes (bank 1), for IDs not in the chip table.
    static readonly Dictionary<ushort, string> Manufacturers = new()
    {
        [0x0001] = "AMD/Spansion",
        [0x0004] = "Fujitsu",
        [0x0020] = "ST/Micron",
        [0x00BF] = "SST",
        [0x00C2] = "Macronix",
    };

    string Hex(ushort v) => v.ToString(ByteWide ? "X2" : "X4", CultureInfo.InvariantCulture);

    string IdKey => Hex(Manufacturer) + ":" + string.Join('/', DeviceId.Select(Hex));

    /// <summary>Chip family name from the lookup table, or null.</summary>
    public string? ChipName => DeviceId.Count > 0 && Chips.TryGetValue(IdKey, out var name) ? name : null;

    /// <summary>Chip name, else manufacturer name, else null.</summary>
    string? FriendlyName =>
        ChipName ?? (DeviceId.Count > 0 && Manufacturers.TryGetValue(Manufacturer, out var m) ? m : null);

    /// <summary>"8 MiB", "512 KiB", or null when CFI gave no size.</summary>
    public string? SizeText => SizeBytes switch
    {
        <= 0 => null,
        >= 0x100000 => SizeBytes / 0x100000 + " MiB",
        _ => SizeBytes / 1024 + " KiB",
    };

    /// <summary>Full one-line description, e.g. "CFI OK, 8 MiB, mfr 0x0001
    /// dev 227E/2210/2200 (Spansion S29GL064N family)" or "no CFI, 8-bit
    /// pair, mfr 0xC2 dev A4 (Macronix MX29F040)".</summary>
    public string Describe()
    {
        var parts = new List<string> { CfiOk ? "CFI OK" : "no CFI" };
        if (SizeText is string size) parts.Add(size);
        if (ByteWide) parts.Add("8-bit pair");
        parts.Add(DeviceId.Count > 0
            ? $"mfr 0x{Hex(Manufacturer)} dev {string.Join('/', DeviceId.Select(Hex))}"
            : "no autoselect ID");
        string text = string.Join(", ", parts);
        return FriendlyName is string name ? $"{text} ({name})" : text;
    }

    /// <summary>Compact form for narrow UI fields, e.g. "8 MiB flash,
    /// Spansion S29GL064N family".</summary>
    public string ShortDescription
    {
        get
        {
            string head = SizeText is string size ? size + " flash" : "flash";
            return FriendlyName is string name ? $"{head}, {name}" : head;
        }
    }
}
