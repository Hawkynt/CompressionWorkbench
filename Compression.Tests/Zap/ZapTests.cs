using System.Buffers.Binary;

namespace Compression.Tests.Zap;

[TestFixture]
public class ZapTests {

  /// <summary>
  /// Builds a minimal ZAP archive with stored (uncompressed) tracks.
  /// </summary>
  private static byte[] BuildZap(params (int TrackNum, byte[] Data)[] tracks) {
    using var ms = new MemoryStream();
    // Magic "ZAP\0"
    ms.Write("ZAP\0"u8);
    // Track count (BE uint16)
    var buf = new byte[4];
    BinaryPrimitives.WriteUInt16BigEndian(buf, (ushort)tracks.Length);
    ms.Write(buf.AsSpan(0, 2));
    // Padding
    ms.Write(new byte[2]);

    foreach (var (trackNum, data) in tracks) {
      BinaryPrimitives.WriteUInt16BigEndian(buf, (ushort)trackNum);
      ms.Write(buf.AsSpan(0, 2));
      BinaryPrimitives.WriteInt32BigEndian(buf, data.Length);
      ms.Write(buf);
      ms.Write(data);
    }

    return ms.ToArray();
  }

  [Test, Category("HappyPath")]
  public void Read_SingleTrack() {
    var trackData = new byte[11 * 512];
    var zap = BuildZap((0, trackData));
    using var ms = new MemoryStream(zap);

    var r = new FileFormat.Zap.ZapReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("track_000.raw"));
  }

  [Test, Category("HappyPath")]
  public void Extract_StoredTrack() {
    var trackData = new byte[11 * 512];
    Random.Shared.NextBytes(trackData);
    var zap = BuildZap((0, trackData));
    using var ms = new MemoryStream(zap);

    var r = new FileFormat.Zap.ZapReader(ms);
    // Stored track (size == TrackSize), should return as-is
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(trackData));
  }

  [Test, Category("HappyPath")]
  public void Read_MultipleTracks() {
    var zap = BuildZap((0, new byte[11 * 512]), (1, new byte[11 * 512]), (2, new byte[11 * 512]));
    using var ms = new MemoryStream(zap);

    var r = new FileFormat.Zap.ZapReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(3));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var desc = new FileFormat.Zap.ZapFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("Zap"));
    Assert.That(desc.Extensions, Does.Contain(".zap"));
    Assert.That(desc.MagicSignatures[0].Bytes, Is.EqualTo("ZAP\0"u8.ToArray()));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_TooSmall_Throws() {
    using var ms = new MemoryStream(new byte[4]);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Zap.ZapReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_BadMagic_Throws() {
    var data = new byte[100];
    using var ms = new MemoryStream(data);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Zap.ZapReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Extract_NullEntry_Throws() {
    var zap = BuildZap((0, new byte[11 * 512]));
    using var ms = new MemoryStream(zap);
    var r = new FileFormat.Zap.ZapReader(ms);
    Assert.Throws<ArgumentNullException>(() => r.Extract(null!));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ReportsWormCapability() {
    var d = new FileFormat.Zap.ZapFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanCreate), Is.True);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Writer_StoredTrack_RoundTrip() {
    // Reader treats compSize == TrackSize as stored (skips decompression).
    // The writer always emits stored tracks for WORM creation.
    var t0 = new byte[FileFormat.Zap.ZapWriter.TrackSize];
    new Random(7).NextBytes(t0);
    var t1 = new byte[FileFormat.Zap.ZapWriter.TrackSize];
    new Random(11).NextBytes(t1);

    var w = new FileFormat.Zap.ZapWriter();
    w.AddTrack(0, t0);
    w.AddTrack(1, t1);
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;

    var r = new FileFormat.Zap.ZapReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(2));
    // Reader treats compSize == TrackSize as stored (no decompression).
    Assert.That(r.Entries[0].CompressedSize, Is.EqualTo(r.Entries[0].Size));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(t0));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo(t1));
  }
}
