#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.Ahx;

namespace Compression.Tests.Ahx;

[TestFixture]
public class AhxTests {

  // Builds a minimal THX module: header + position list + track data + instrument
  // bytes + a NUL-terminated title block at titleOff.
  private static byte[] MakeSyntheticAhx() {
    const int positions = 1;
    const int trackLen = 1;
    const int trackNr = 0; // 1 track total (track 0 present, not omitted)
    const int instrBytes = 4;

    var pos = 16;                       // header end (no subsongs)
    var posBytes = positions * 4 * 2;   // position list
    var trackBytes = (trackNr + 1) * trackLen * 3;
    var instrOff = pos + posBytes + trackBytes;
    var titleOff = instrOff + instrBytes;
    var title = "AhxSong";
    var total = titleOff + title.Length + 1;

    var buf = new byte[total];
    buf[0] = (byte)'T'; buf[1] = (byte)'H'; buf[2] = (byte)'X';
    buf[3] = 0; // version 0
    BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(4, 2), (ushort)titleOff);
    BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(6, 2), 0); // flags (track 0 present)
    BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(8, 2), positions);
    BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(10, 2), 0); // restart
    buf[12] = trackLen;
    buf[13] = trackNr;
    buf[14] = 0; // instruments
    buf[15] = 0; // subsongs

    // Instrument bytes ramp.
    for (var i = 0; i < instrBytes; ++i) buf[instrOff + i] = (byte)(0x10 + i);

    var t = Encoding.ASCII.GetBytes(title);
    Buffer.BlockCopy(t, 0, buf, titleOff, t.Length);
    return buf;
  }

  [Test]
  public void List_ExposesFullMetadataAndBlocks() {
    using var ms = new MemoryStream(MakeSyntheticAhx());
    var entries = new AhxFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "FULL.ahx"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name == "positions.bin"), Is.True);
  }

  [Test]
  public void Extract_WritesFullByteIdentical_AndTitleParsed() {
    var blob = MakeSyntheticAhx();
    var tmp = Path.Combine(Path.GetTempPath(), "ahx_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(blob);
      new AhxFormatDescriptor().Extract(ms, tmp, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(tmp, "FULL.ahx")), Is.EqualTo(blob));
      var meta = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(meta, Does.Contain("magic = THX"));
      Assert.That(meta, Does.Contain("title = AhxSong"));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    }
  }

  [Test]
  public void List_Malformed_DoesNotThrow() {
    using var ms = new MemoryStream([(byte)'T', (byte)'H', (byte)'X', 0, 1]);
    List<ArchiveEntryInfo> entries = null!;
    Assert.DoesNotThrow(() => entries = new AhxFormatDescriptor().List(ms, null));
    Assert.That(entries.Any(e => e.Name == "FULL.ahx"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
  }

  [Test]
  public void Detection_Magic() {
    var d = new AhxFormatDescriptor();
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo("THX"u8.ToArray()));
    Assert.That(d.Extensions, Does.Contain(".ahx"));
    Assert.That(d.Extensions, Does.Contain(".thx"));
  }
}
