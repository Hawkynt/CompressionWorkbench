#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.Dbm;

namespace Compression.Tests.Dbm;

[TestFixture]
public class DbmTests {

  // Builds a minimal DBM0 module: "DBM0" + version + NAME + INFO + PATT + SMPL.
  private static byte[] MakeSyntheticDbm() {
    using var ms = new MemoryStream();
    void Chunk(string tag, byte[] payload) {
      ms.Write(Encoding.ASCII.GetBytes(tag));
      var len = new byte[4];
      BinaryPrimitives.WriteUInt32BigEndian(len, (uint)payload.Length);
      ms.Write(len);
      ms.Write(payload);
    }
    ms.Write("DBM0"u8);
    ms.Write([0x02, 0x00]); // tracker version
    ms.Write([0x00, 0x00]); // reserved (so chunks start at offset 8)

    Chunk("NAME", PadAscii("DbmSong", 44));
    var info = new byte[10];
    BinaryPrimitives.WriteUInt16BigEndian(info.AsSpan(0, 2), 1); // instruments
    BinaryPrimitives.WriteUInt16BigEndian(info.AsSpan(2, 2), 1); // samples
    BinaryPrimitives.WriteUInt16BigEndian(info.AsSpan(4, 2), 1); // songs
    BinaryPrimitives.WriteUInt16BigEndian(info.AsSpan(6, 2), 1); // patterns
    BinaryPrimitives.WriteUInt16BigEndian(info.AsSpan(8, 2), 4); // channels
    Chunk("INFO", info);
    Chunk("PATT", [1, 2, 3, 4]);
    Chunk("SMPL", [0x10, 0x20, 0x30, 0x40]);
    return ms.ToArray();
  }

  private static byte[] PadAscii(string s, int len) {
    var b = new byte[len];
    var a = Encoding.ASCII.GetBytes(s);
    Buffer.BlockCopy(a, 0, b, 0, Math.Min(a.Length, len));
    return b;
  }

  [Test]
  public void List_ExposesFullMetadataPatternsSamples() {
    using var ms = new MemoryStream(MakeSyntheticDbm());
    var entries = new DbmFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "FULL.dbm"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name.StartsWith("patterns/pattern_")), Is.True);
    Assert.That(entries.Any(e => e.Name.StartsWith("samples/")), Is.True);
  }

  [Test]
  public void Extract_FullByteIdentical_InfoParsed() {
    var blob = MakeSyntheticDbm();
    var tmp = Path.Combine(Path.GetTempPath(), "dbm_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(blob);
      new DbmFormatDescriptor().Extract(ms, tmp, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(tmp, "FULL.dbm")), Is.EqualTo(blob));
      var meta = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(meta, Does.Contain("magic = DBM0"));
      Assert.That(meta, Does.Contain("song_name = DbmSong"));
      Assert.That(meta, Does.Contain("num_instruments = 1"));
      Assert.That(meta, Does.Contain("num_channels = 4"));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    }
  }

  [Test]
  public void List_Malformed_DoesNotThrow() {
    using var ms = new MemoryStream([(byte)'D', (byte)'B', (byte)'M', (byte)'0', 0, 0]);
    List<ArchiveEntryInfo> entries = null!;
    Assert.DoesNotThrow(() => entries = new DbmFormatDescriptor().List(ms, null));
    Assert.That(entries.Any(e => e.Name == "FULL.dbm"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
  }

  [Test]
  public void Detection_Magic() {
    var d = new DbmFormatDescriptor();
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo("DBM0"u8.ToArray()));
    Assert.That(d.Extensions, Does.Contain(".dbm"));
  }
}
