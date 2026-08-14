using FlashKit.Core;

namespace FlashKit.Core.Tests;

public class XdeltaPatchTests
{
    static byte[] Patch(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    static readonly byte[] Magic = { 0xD6, 0xC3, 0xC4, 0x00, 0x00 }; // VCDIFF, version 0, no header extensions

    [Fact]
    public void apply_decodes_a_hand_built_copy_run_copy_window()
    {
        var rom = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        // One VCD_SOURCE window over the whole ROM: COPY 4 @0, RUN 3 x 0x09,
        // COPY 4 @4 — opcode 20 is COPY size 4 mode SELF, opcode 0 is RUN.
        var patch = Patch(Magic,
            new byte[] { 0x01, 0x08, 0x00 },             // win_ind VCD_SOURCE, seg len 8, seg pos 0
            new byte[] { 0x0C, 0x0B, 0x00 },             // delta len 12, target len 11, no compression
            new byte[] { 0x01, 0x04, 0x02 },             // data 1, inst 4, addr 2
            new byte[] { 0x09 },                         // data section
            new byte[] { 20, 0, 3, 20 },                 // inst section
            new byte[] { 0x00, 0x04 });                  // addr section

        Assert.Equal(new byte[] { 1, 2, 3, 4, 9, 9, 9, 5, 6, 7, 8 }, XdeltaPatch.Apply(rom, patch));
    }

    [Fact]
    public void apply_handles_a_forward_overlapping_self_copy()
    {
        // No segment: ADD 1 x 0xAB then COPY 4 @0 — the RLE idiom, the copy
        // reads bytes it is writing.
        var patch = Patch(Magic,
            new byte[] { 0x00, 0x09, 0x05, 0x00 },       // win_ind 0, delta len 9, target len 5
            new byte[] { 0x01, 0x02, 0x01 },             // data 1, inst 2, addr 1
            new byte[] { 0xAB },                         // data section
            new byte[] { 2, 20 },                        // inst: ADD size 1, COPY size 4 mode SELF
            new byte[] { 0x00 });                        // addr section

        Assert.Equal(new byte[] { 0xAB, 0xAB, 0xAB, 0xAB, 0xAB }, XdeltaPatch.Apply(Array.Empty<byte>(), patch));
    }

    [Fact]
    public void apply_decodes_near_and_same_cache_address_modes()
    {
        var rom = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        // Three COPY 4 of ROM offset 4 via mode SELF (opcode 20), then near
        // slot 0 (opcode 52, delta 0), then same cache (opcode 116, byte 4).
        var patch = Patch(Magic,
            new byte[] { 0x01, 0x08, 0x00 },             // win_ind VCD_SOURCE, seg len 8, seg pos 0
            new byte[] { 0x0B, 0x0C, 0x00 },             // delta len 11, target len 12, no compression
            new byte[] { 0x00, 0x03, 0x03 },             // data 0, inst 3, addr 3
            new byte[] { 20, 52, 116 },                  // inst section
            new byte[] { 0x04, 0x00, 0x04 });            // addr section

        Assert.Equal(new byte[] { 5, 6, 7, 8, 5, 6, 7, 8, 5, 6, 7, 8 }, XdeltaPatch.Apply(rom, patch));
    }

    [Fact]
    public void apply_skips_an_application_header()
    {
        // xdelta3 writes an app header (the file names) by default; the
        // bytes are irrelevant to patching and must be skipped.
        var patch = Patch(
            new byte[] { 0xD6, 0xC3, 0xC4, 0x00, 0x04 },  // hdr_ind VCD_APPHEADER
            new byte[] { 0x03, 0xDE, 0xAD, 0xBF },        // app header: 3 bytes
            new byte[] { 0x00, 0x09, 0x05, 0x00 },
            new byte[] { 0x01, 0x02, 0x01 },
            new byte[] { 0xAB },
            new byte[] { 2, 20 },
            new byte[] { 0x00 });

        Assert.Equal(new byte[] { 0xAB, 0xAB, 0xAB, 0xAB, 0xAB }, XdeltaPatch.Apply(Array.Empty<byte>(), patch));
    }

    [Fact]
    public void apply_verifies_the_adler32_window_checksum()
    {
        var rom = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        // The copy-run-copy window with VCD_ADLER32 set and a wrong checksum.
        var patch = Patch(Magic,
            new byte[] { 0x05, 0x08, 0x00 },             // win_ind VCD_SOURCE | VCD_ADLER32
            new byte[] { 0x10, 0x0B, 0x00 },             // delta len 16 (checksum adds 4)
            new byte[] { 0x01, 0x04, 0x02 },
            new byte[] { 0x00, 0x00, 0x00, 0x01 },       // bogus Adler-32
            new byte[] { 0x09 },
            new byte[] { 20, 0, 3, 20 },
            new byte[] { 0x00, 0x04 });

        var x = Assert.Throws<XdeltaFormatException>(() => XdeltaPatch.Apply(rom, patch));
        Assert.Contains("Adler-32", x.Message);
    }

    [Fact]
    public void apply_rejects_secondary_compression_with_a_pointed_message()
    {
        var patch = new byte[] { 0xD6, 0xC3, 0xC4, 0x00, 0x01 }; // hdr_ind VCD_DECOMPRESS

        var x = Assert.Throws<XdeltaFormatException>(() => XdeltaPatch.Apply(new byte[4], patch));
        Assert.Contains("-S none", x.Message);
    }

    [Theory]
    [InlineData(new byte[] { 1, 2, 3 })]                          // no VCDIFF magic
    [InlineData(new byte[] { 0xD6, 0xC3, 0xC4, 0x01, 0x00 })]     // unknown version
    [InlineData(new byte[] { 0xD6, 0xC3, 0xC4, 0x00, 0x00, 0x01 })] // window cut short
    public void apply_rejects_malformed_patches(byte[] patch)
    {
        Assert.Throws<XdeltaFormatException>(() => XdeltaPatch.Apply(new byte[4], patch));
    }

    [Fact]
    public void create_emits_the_locked_wire_format()
    {
        // Locks the encoder's output shape: one VCD_SOURCE|VCD_ADLER32
        // window, whole source as segment, ADD/RUN/COPY(SELF) only. Do not
        // "fix" the bytes to match changed code without hand-verifying the
        // new stream against RFC 3284 (and xdelta3 if available).
        var original = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        var modified = Patch(new byte[] { 1, 2, 3, 4 },
            Enumerable.Repeat((byte)0xAA, 8).ToArray(),
            new byte[] { 5, 6, 7, 8, 9, 10 });

        var patch = XdeltaPatch.Create(original, modified);

        Assert.Equal(Patch(Magic,
            new byte[] { 0x05, 0x0A, 0x00 },             // win_ind SOURCE|ADLER32, seg len 10, seg pos 0
            new byte[] { 0x18, 0x12, 0x00 },             // delta len 24, target len 18, no compression
            new byte[] { 0x0B, 0x04, 0x00 },             // data 11, inst 4, addr 0
            new byte[] { 0x39, 0x06, 0x05, 0x88 },       // Adler-32 of the target
            new byte[] { 1, 2, 3, 4, 0xAA, 5, 6, 7, 8, 9, 10 }, // data section
            new byte[] { 5, 0, 8, 7 }),                  // inst: ADD 4, RUN 8, ADD 6
            patch);
        Assert.Equal(modified, XdeltaPatch.Apply(original, patch));
    }

    [Theory]
    [InlineData("in-place single byte")]
    [InlineData("scattered edits")]
    [InlineData("extend longer")]
    [InlineData("truncate shorter")]
    [InlineData("long run")]
    [InlineData("relocated block")]
    [InlineData("empty target")]
    [InlineData("empty source")]
    public void roundtrip_apply_of_create_reproduces_modified(string shape)
    {
        var rng = new Random(1234);
        var original = new byte[5000];
        rng.NextBytes(original);

        byte[] modified = shape switch
        {
            "in-place single byte" => Edit(original, m => m[2500] ^= 0xFF),
            "scattered edits" => Edit(original, m => { for (int k = 0; k < m.Length; k += 137) m[k] ^= 0x5A; }),
            "extend longer" => Grow(original, 3000, 0x00),
            "truncate shorter" => original[..2048],
            "long run" => Edit(original, m => Array.Fill(m, (byte)0xFF, 100, 2000)),
            "relocated block" => original[3000..].Concat(original[..3000]).ToArray(),
            "empty target" => Array.Empty<byte>(),
            "empty source" => original,
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
        if (shape == "empty source") original = Array.Empty<byte>();

        var patch = XdeltaPatch.Create(original, modified);
        Assert.Equal(modified, XdeltaPatch.Apply(original, patch));
    }

    [Fact]
    public void create_keeps_an_unchanged_rom_patch_tiny()
    {
        var rng = new Random(42);
        var rom = new byte[0x20000];
        rng.NextBytes(rom);

        var patch = XdeltaPatch.Create(rom, (byte[])rom.Clone());

        Assert.Equal(rom, XdeltaPatch.Apply(rom, patch));
        Assert.True(patch.Length < 64, $"expected a tiny single-COPY patch, got {patch.Length} bytes");
    }

    [Fact]
    public void create_finds_relocated_content_via_the_block_index()
    {
        var rng = new Random(7);
        var original = new byte[0x8000];
        rng.NextBytes(original);
        // The whole first half moved to a different offset in the target.
        var modified = new byte[0x5000].Concat(original[..0x4000]).ToArray();

        var patch = XdeltaPatch.Create(original, modified);

        Assert.Equal(modified, XdeltaPatch.Apply(original, patch));
        Assert.True(patch.Length < 0x1000, $"expected COPYs for the moved half, got {patch.Length} bytes");
    }

    [Fact]
    public void create_and_apply_roundtrip_over_random_fuzzing()
    {
        var rng = new Random(9001);
        for (int trial = 0; trial < 200; trial++)
        {
            var original = new byte[rng.Next(0, 400)];
            rng.NextBytes(original);
            var modified = new byte[rng.Next(0, 400)];
            rng.NextBytes(modified);
            // sprinkle equal regions and repeated runs so COPY/RUN both fire
            int copy = Math.Min(original.Length, modified.Length);
            for (int k = 0; k < copy; k++) if (rng.Next(3) == 0) modified[k] = original[k];
            if (modified.Length > 20) Array.Fill(modified, (byte)rng.Next(256), 5, 10);

            var patch = XdeltaPatch.Create(original, modified);
            Assert.Equal(modified, XdeltaPatch.Apply(original, patch));
        }
    }

    static byte[] Edit(byte[] src, Action<byte[]> edit)
    {
        var m = (byte[])src.Clone();
        edit(m);
        return m;
    }

    static byte[] Grow(byte[] src, int extra, byte fill)
    {
        var m = new byte[src.Length + extra];
        Array.Copy(src, m, src.Length);
        Array.Fill(m, fill, src.Length, extra);
        return m;
    }
}
