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
}
