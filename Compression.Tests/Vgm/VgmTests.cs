#pragma warning disable CS1591
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using FileFormat.Vgm;

namespace Compression.Tests.Vgm;

[TestFixture]
public class VgmTests {

  // Builds a v1.51 VGM with SN76489 + YM2612 clocks, a command log, and a GD3 tag block.
  private static byte[] BuildVgm() {
    // Header is 0x80 bytes for v1.51; place commands after it, GD3 after commands.
    var commands = new byte[] { 0x50, 0x9F, 0x52, 0x00, 0x40, 0x62, 0x66 };

    var gd3 = BuildGd3();
    var headerLen = 0x80;
    var total = headerLen + commands.Length + gd3.Length;
    var blob = new byte[total];

    "Vgm "u8.CopyTo(blob);
    var dataOffset = headerLen;
    var gd3Offset = headerLen + commands.Length;

    // eofOffset (rel to 0x04)
    WriteU32(blob, 0x04, (uint)(total - 0x04));
    WriteU32(blob, 0x08, 0x00000151);                // version BCD 1.51
    WriteU32(blob, 0x0C, 3579545);                   // SN76489
    WriteU32(blob, 0x10, 0);                          // YM2413 (absent)
    WriteU32(blob, 0x14, (uint)(gd3Offset - 0x14));  // GD3 offset (rel to 0x14)
    WriteU32(blob, 0x18, 88200);                      // total samples = 2.0 s @ 44100
    WriteU32(blob, 0x2C, 7670454);                    // YM2612
    WriteU32(blob, 0x34, (uint)(dataOffset - 0x34));  // data offset (rel to 0x34)

    commands.CopyTo(blob.AsSpan(dataOffset));
    gd3.CopyTo(blob.AsSpan(gd3Offset));
    return blob;
  }

  private static byte[] BuildGd3() {
    var fields = new[] {
      "Stage 1", "ステージ1", "Test Game", "テストゲーム", "Sega Mega Drive", "メガドライブ",
      "Composer", "作曲者", "1991/01/01", "Ripper", "Some notes",
    };
    using var body = new MemoryStream();
    foreach (var f in fields) {
      body.Write(Encoding.Unicode.GetBytes(f));
      body.Write([0, 0]);                              // NUL terminator (UTF-16)
    }
    var bodyBytes = body.ToArray();

    using var ms = new MemoryStream();
    ms.Write("Gd3 "u8);
    var u32 = new byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(u32, 0x00000100);
    ms.Write(u32);
    BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)bodyBytes.Length);
    ms.Write(u32);
    ms.Write(bodyBytes);
    return ms.ToArray();
  }

  private static void WriteU32(byte[] blob, int offset, uint value)
    => BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(offset, 4), value);

  private static string Extract(byte[] blob, string entry) {
    using var ms = new MemoryStream(blob);
    using var output = new MemoryStream();
    new VgmFormatDescriptor().ExtractEntry(ms, entry, output, null);
    return Encoding.UTF8.GetString(output.ToArray());
  }

  // ──────────────────────────────────────────────────────────────────────────

  [Test]
  public void List_SurfacesFullMetadataCommandsAndGd3() {
    using var ms = new MemoryStream(BuildVgm());
    var entries = new VgmFormatDescriptor().List(ms, null);

    Assert.That(entries.First(e => e.Name == "FULL.vgm").Kind, Is.EqualTo("Container"));
    Assert.That(entries.Any(e => e.Name == "metadata.ini" && e.Kind == "Tag"), Is.True);
    Assert.That(entries.Any(e => e.Name == "commands.bin" && e.Kind == "Stream"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata/gd3.ini" && e.Kind == "Tag"), Is.True);
  }

  [Test]
  public void Metadata_ReportsVersionDurationAndChipClocks() {
    var ini = Extract(BuildVgm(), "metadata.ini");
    Assert.That(ini, Does.Contain("version=1.51"));
    Assert.That(ini, Does.Contain("duration_seconds=2.000"));
    Assert.That(ini, Does.Contain("SN76489=3579545"));
    Assert.That(ini, Does.Contain("YM2612=7670454"));
    Assert.That(ini, Does.Not.Contain("YM2413="), "zero clock is omitted");
  }

  [Test]
  public void Gd3_ParsesUtf16Fields() {
    var ini = Extract(BuildVgm(), "metadata/gd3.ini");
    Assert.That(ini, Does.Contain("track_en=Stage 1"));
    Assert.That(ini, Does.Contain("track_jp=ステージ1"));
    Assert.That(ini, Does.Contain("game_en=Test Game"));
    Assert.That(ini, Does.Contain("author_en=Composer"));
    Assert.That(ini, Does.Contain("date=1991/01/01"));
    Assert.That(ini, Does.Contain("notes=Some notes"));
  }

  [Test]
  public void Commands_AreExtractedExactly() {
    using var ms = new MemoryStream(BuildVgm());
    using var output = new MemoryStream();
    new VgmFormatDescriptor().ExtractEntry(ms, "commands.bin", output, null);
    Assert.That(output.ToArray(), Is.EqualTo(new byte[] { 0x50, 0x9F, 0x52, 0x00, 0x40, 0x62, 0x66 }));
  }

  [Test]
  public void Vgz_GzipCompressedInputIsTransparentlyDecompressed() {
    var raw = BuildVgm();
    using var compressed = new MemoryStream();
    using (var gz = new GZipStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
      gz.Write(raw);

    using var ms = new MemoryStream(compressed.ToArray());
    var entries = new VgmFormatDescriptor().List(ms, null);
    var full = entries.First(e => e.Name == "FULL.vgm");
    Assert.That(full.OriginalSize, Is.EqualTo(raw.Length), "FULL.vgm is the decompressed log");

    var ini = Extract(compressed.ToArray(), "metadata.ini");
    Assert.That(ini, Does.Contain("SN76489=3579545"));
  }

  [Test]
  public void GarbageInput_DegradesToFullOnly() {
    using var ms = new MemoryStream(new byte[] { 0x00, 0x01, 0x02, 0x03 });
    var entries = new VgmFormatDescriptor().List(ms, null);
    Assert.That(entries.Count, Is.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("FULL.vgm"));
  }

  // ──────────── rendering ────────────

  // Builds a v1.51 VGM with only a PSG clock and a command stream that plays a square tone.
  private static byte[] BuildPsgToneVgm(int period, uint totalSamples, byte[] commands) {
    var headerLen = 0x80;
    var total = headerLen + commands.Length;
    var blob = new byte[total];
    "Vgm "u8.CopyTo(blob);
    WriteU32(blob, 0x04, (uint)(total - 0x04));
    WriteU32(blob, 0x08, 0x00000151);
    WriteU32(blob, 0x0C, 3579545);                   // SN76489 only
    WriteU32(blob, 0x18, totalSamples);
    WriteU32(blob, 0x34, (uint)(headerLen - 0x34));  // data offset
    commands.CopyTo(blob.AsSpan(headerLen));
    return blob;
  }

  private static short[] ReadWavLeftSamples(byte[] wav) {
    // Minimal RIFF: data starts at offset 44.
    var count = (wav.Length - 44) / 2;
    var samples = new short[count];
    for (var i = 0; i < count; ++i)
      samples[i] = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44 + i * 2, 2));
    return samples;
  }

  private static int CountZeroCrossings(short[] s) {
    var crossings = 0;
    for (var i = 1; i < s.Length; ++i)
      if ((s[i - 1] < 0 && s[i] >= 0) || (s[i - 1] >= 0 && s[i] < 0))
        ++crossings;
    return crossings;
  }

  [Test]
  public void Render_PsgTone_ProducesLeftRightWavsWithExpectedFrequency() {
    const int period = 0x100;
    // Commands: latch ch0 tone low nibble 0, data high bits, ch0 volume full, then wait.
    var commands = new byte[] {
      0x50, (byte)(0x80 | (period & 0x0F)),    // latch ch0 tone low
      0x50, (byte)((period >> 4) & 0x3F),      // data high
      0x50, (byte)(0x80 | 0x10 | 0x00),        // ch0 volume full
      0x50, (byte)(0x80 | (1 << 5) | 0x10 | 0x0F), // ch1 mute
      0x50, (byte)(0x80 | (2 << 5) | 0x10 | 0x0F), // ch2 mute
      0x50, (byte)(0x80 | (3 << 5) | 0x10 | 0x0F), // noise mute
      0x61, 0x44, 0xAC,                        // wait 44100 samples (1 s)
      0x66,
    };
    var blob = BuildPsgToneVgm(period, totalSamples: 44100, commands);

    using var ms = new MemoryStream(blob);
    var entries = new VgmFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "LEFT.wav" && e.Kind == "Channel"), Is.True);
    Assert.That(entries.Any(e => e.Name == "RIGHT.wav" && e.Kind == "Channel"), Is.True);

    using var output = new MemoryStream();
    new VgmFormatDescriptor().ExtractEntry(new MemoryStream(blob), "LEFT.wav", output, null);
    var samples = ReadWavLeftSamples(output.ToArray());
    var measuredHz = CountZeroCrossings(samples) / 2.0;
    var expectedHz = 3579545.0 / (32.0 * period);
    Assert.That(measuredHz, Is.EqualTo(expectedHz).Within(expectedHz * 0.05),
      $"expected ~{expectedHz:F1} Hz, measured {measuredHz:F1} Hz");
  }

  [Test]
  public void Render_DacDataBlock_FeedsYm2612Dac() {
    // VGM with YM2612 + PSG clocks; a data block then DAC writes (0x8x) + waits.
    var commands = new List<byte> {
      // data block: 0x67 0x66 type ssssssss <data>
      0x67, 0x66, 0x00, 0x04, 0x00, 0x00, 0x00, 0xFF, 0x00, 0xFF, 0x00,
      0x52, 0x2B, 0x80,        // YM2612: enable DAC
      0x80,                    // DAC write sample 0 + wait 0
      0x61, 0x10, 0x00,        // wait 16 samples
      0x80,                    // DAC write sample 1 + wait 0
      0x61, 0x10, 0x00,
      0x66,
    };
    var headerLen = 0x80;
    var blob = new byte[headerLen + commands.Count];
    "Vgm "u8.CopyTo(blob);
    WriteU32(blob, 0x08, 0x00000151);
    WriteU32(blob, 0x0C, 3579545);    // PSG
    WriteU32(blob, 0x18, 64);
    WriteU32(blob, 0x2C, 7670454);    // YM2612
    WriteU32(blob, 0x34, (uint)(headerLen - 0x34));
    commands.ToArray().CopyTo(blob.AsSpan(headerLen));

    using var ms = new MemoryStream(blob);
    var entries = new VgmFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "LEFT.wav"), Is.True, "DAC stream renders audio");
  }

  [Test]
  public void Render_UnsupportedChip_KeepsMetadataOnly() {
    // YM2151 clock present (unsupported) → no WAVs, a render.txt note instead.
    var commands = new byte[] { 0x50, 0x9F, 0x66 };
    var headerLen = 0x80;
    var blob = new byte[headerLen + commands.Length];
    "Vgm "u8.CopyTo(blob);
    WriteU32(blob, 0x08, 0x00000151);
    WriteU32(blob, 0x0C, 3579545);   // SN76489 (supported)
    WriteU32(blob, 0x30, 3579545);   // YM2151 (unsupported) → blocks rendering
    WriteU32(blob, 0x18, 100);
    WriteU32(blob, 0x34, (uint)(headerLen - 0x34));
    commands.CopyTo(blob.AsSpan(headerLen));

    using var ms = new MemoryStream(blob);
    var entries = new VgmFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "LEFT.wav"), Is.False, "unsupported chip blocks rendering");
    Assert.That(entries.Any(e => e.Name == "render.txt"), Is.True, "records why rendering was skipped");
  }
}
