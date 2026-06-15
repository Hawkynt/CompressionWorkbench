#pragma warning disable CS1591
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using FileFormat.Gym;

namespace Compression.Tests.Gym;

[TestFixture]
public class GymTests {

  private const int HeaderSize = 0x1A4;

  private static byte[] BuildGym(byte[] log, uint packedSize) {
    var blob = new byte[HeaderSize + log.Length];
    "GYMX"u8.CopyTo(blob);
    WriteText(blob, 0x04, "Green Hill", 32);
    WriteText(blob, 0x24, "Sonic", 32);
    WriteText(blob, 0x44, "1991 Sega", 32);
    WriteText(blob, 0x64, "Gens", 32);
    WriteText(blob, 0x84, "Dumper Z", 32);
    WriteText(blob, 0xA4, "a comment", 256);
    BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(0x19C), 0); // loop start
    BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(0x1A0), packedSize);
    log.CopyTo(blob, HeaderSize);
    return blob;
  }

  private static void WriteText(byte[] b, int off, string t, int len) {
    var bytes = Encoding.ASCII.GetBytes(t);
    Array.Copy(bytes, 0, b, off, Math.Min(bytes.Length, len - 1));
  }

  private static byte[] Zlib(byte[] data) {
    using var ms = new MemoryStream();
    using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
      z.Write(data);
    return ms.ToArray();
  }

  private static string Meta(byte[] blob) => Encoding.UTF8.GetString(Bytes(blob, "metadata.ini"));

  private static byte[] Bytes(byte[] blob, string entry) {
    using var ms = new MemoryStream(blob);
    using var output = new MemoryStream();
    new GymFormatDescriptor().ExtractEntry(ms, entry, output, null);
    return output.ToArray();
  }

  [Test]
  public void Unpacked_SurfacesLogBinAndCountsFrames() {
    // log: three 0x00 frame markers interleaved with command bytes → 3 frames → 0.05s.
    var log = new byte[] { 0x00, 0x01, 0x10, 0x00, 0x02, 0x20, 0x00 };
    var blob = BuildGym(log, packedSize: 0);

    using var ms = new MemoryStream(blob);
    var entries = new GymFormatDescriptor().List(ms, null);
    Assert.That(entries.First(e => e.Name == "FULL.gym").Kind, Is.EqualTo("Container"));
    Assert.That(entries.First(e => e.Name == "log.bin").Kind, Is.EqualTo("Stream"));
    Assert.That(Bytes(blob, "log.bin"), Is.EqualTo(log));

    var ini = Meta(blob);
    Assert.That(ini, Does.Contain("song=Green Hill"));
    Assert.That(ini, Does.Contain("game=Sonic"));
    Assert.That(ini, Does.Contain("compression=none"));
    Assert.That(ini, Does.Contain("frame_count=3"));
    Assert.That(ini, Does.Contain("duration_seconds=0.05"));
  }

  [Test]
  public void Packed_SurfacesLogZWithNote() {
    var raw = new byte[] { 0x00, 0x01, 0x00, 0x02, 0x00 };
    var packed = Zlib(raw);
    var blob = BuildGym(packed, packedSize: (uint)raw.Length);

    using var ms = new MemoryStream(blob);
    var entries = new GymFormatDescriptor().List(ms, null);
    Assert.That(entries.First(e => e.Name == "log.z").Kind, Is.EqualTo("Stream"));
    Assert.That(Bytes(blob, "log.z"), Is.EqualTo(packed));
    Assert.That(entries.Any(e => e.Name == "log.bin"), Is.False);

    var ini = Meta(blob);
    Assert.That(ini, Does.Contain("compression=zlib"));
    Assert.That(ini, Does.Contain("packed_size=" + raw.Length));
  }

  [Test]
  public void ShortBlob_DegradesToFullOnly() {
    using var ms = new MemoryStream("GYMX"u8.ToArray());
    var entries = new GymFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "FULL.gym"), Is.True);
    Assert.That(entries.Any(e => e.Name == "log.bin" || e.Name == "log.z"), Is.False);
  }

  // ──────────── rendering ────────────

  private static short[] ReadWavLeftSamples(byte[] wav) {
    var count = (wav.Length - 44) / 2;
    var samples = new short[count];
    for (var i = 0; i < count; ++i)
      samples[i] = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44 + i * 2, 2));
    return samples;
  }

  [Test]
  public void Render_PsgTone_ProducesLeftRightWavs() {
    // PSG (0x03) writes program channel-0 tone, then frame waits (0x00) advance time.
    var log = new List<byte>();
    void Psg(byte b) { log.Add(0x03); log.Add(b); }
    Psg((byte)(0x80 | 0x00));               // latch ch0 tone low nibble 0
    Psg((byte)((0x100 >> 4) & 0x3F));       // data high → period 0x100
    Psg((byte)(0x80 | 0x10 | 0x00));        // ch0 volume full
    Psg((byte)(0x80 | (1 << 5) | 0x10 | 0x0F));
    Psg((byte)(0x80 | (2 << 5) | 0x10 | 0x0F));
    Psg((byte)(0x80 | (3 << 5) | 0x10 | 0x0F));
    for (var f = 0; f < 30; ++f) log.Add(0x00); // 30 frames ≈ 0.5 s

    var blob = BuildGym(log.ToArray(), packedSize: 0);
    using var ms = new MemoryStream(blob);
    var entries = new GymFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "LEFT.wav" && e.Kind == "Channel"), Is.True);
    Assert.That(entries.Any(e => e.Name == "RIGHT.wav" && e.Kind == "Channel"), Is.True);

    var samples = ReadWavLeftSamples(Bytes(blob, "LEFT.wav"));
    var crossings = 0;
    for (var i = 1; i < samples.Length; ++i)
      if ((samples[i - 1] < 0 && samples[i] >= 0) || (samples[i - 1] >= 0 && samples[i] < 0))
        ++crossings;
    Assert.That(crossings, Is.GreaterThan(0), "a programmed PSG tone must oscillate");
  }

  [Test]
  public void Render_PackedLog_IsInflatedAndRendered() {
    var raw = new List<byte> { 0x03, (byte)(0x80 | 0x00), 0x03, (byte)((0x100 >> 4) & 0x3F),
                               0x03, (byte)(0x80 | 0x10 | 0x00) };
    for (var f = 0; f < 20; ++f) raw.Add(0x00);
    var packed = Zlib(raw.ToArray());
    var blob = BuildGym(packed, packedSize: (uint)raw.Count);

    using var ms = new MemoryStream(blob);
    var entries = new GymFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "log.z"), Is.True, "still surfaces the packed stream");
    Assert.That(entries.Any(e => e.Name == "LEFT.wav"), Is.True, "packed log is inflated then rendered");
  }
}
