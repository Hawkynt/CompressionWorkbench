using System.Buffers.Binary;

namespace Compression.Tests.LhF;

[TestFixture]
public class LhFTests {

  /// <summary>
  /// Builds a minimal LhF archive with stored (uncompressed) tracks.
  /// </summary>
  private static byte[] BuildLhF(params (int TrackNum, byte[] Data)[] tracks) {
    using var ms = new MemoryStream();
    // Magic "LhF\0"
    ms.Write("LhF\0"u8);
    // Track count (BE uint16)
    var buf = new byte[4];
    BinaryPrimitives.WriteUInt16BigEndian(buf, (ushort)tracks.Length);
    ms.Write(buf.AsSpan(0, 2));
    // Flags (BE uint16)
    ms.Write(new byte[2]);

    foreach (var (trackNum, data) in tracks) {
      // Track number (BE uint16)
      BinaryPrimitives.WriteUInt16BigEndian(buf, (ushort)trackNum);
      ms.Write(buf.AsSpan(0, 2));
      // Compressed size (BE int32) = same as data (stored)
      BinaryPrimitives.WriteInt32BigEndian(buf, data.Length);
      ms.Write(buf);
      // Checksum (BE uint16)
      ms.Write(new byte[2]);
      // Data
      ms.Write(data);
    }

    return ms.ToArray();
  }

  [Test, Category("HappyPath")]
  public void Read_SingleTrack() {
    var trackData = new byte[11 * 512]; // Standard Amiga track
    Random.Shared.NextBytes(trackData);
    var lhf = BuildLhF((0, trackData));
    using var ms = new MemoryStream(lhf);

    var r = new FileFormat.LhF.LhFReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("track_000.raw"));
  }

  [Test, Category("HappyPath")]
  public void Extract_StoredTrack_ReturnsData() {
    var trackData = new byte[11 * 512];
    Random.Shared.NextBytes(trackData);
    var lhf = BuildLhF((0, trackData));
    using var ms = new MemoryStream(lhf);

    var r = new FileFormat.LhF.LhFReader(ms);
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(trackData));
  }

  [Test, Category("HappyPath")]
  public void Read_MultipleTracks() {
    var t0 = new byte[11 * 512];
    var t1 = new byte[11 * 512];
    var lhf = BuildLhF((0, t0), (1, t1));
    using var ms = new MemoryStream(lhf);

    var r = new FileFormat.LhF.LhFReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(2));
    Assert.That(r.Entries[0].Name, Is.EqualTo("track_000.raw"));
    Assert.That(r.Entries[1].Name, Is.EqualTo("track_001.raw"));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var desc = new FileFormat.LhF.LhFFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("LhF"));
    Assert.That(desc.Extensions, Does.Contain(".lhf"));
    Assert.That(desc.MagicSignatures[0].Bytes, Is.EqualTo(new byte[] { 0x4C, 0x68, 0x46, 0x00 }));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_ViaInterface() {
    var lhf = BuildLhF((5, new byte[11 * 512]));
    using var ms = new MemoryStream(lhf);
    var desc = new FileFormat.LhF.LhFFormatDescriptor();
    var entries = desc.List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("track_005.raw"));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_TooSmall_Throws() {
    using var ms = new MemoryStream(new byte[4]);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.LhF.LhFReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_BadMagic_Throws() {
    var data = new byte[100];
    using var ms = new MemoryStream(data);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.LhF.LhFReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Extract_NullEntry_Throws() {
    var lhf = BuildLhF((0, new byte[11 * 512]));
    using var ms = new MemoryStream(lhf);
    var r = new FileFormat.LhF.LhFReader(ms);
    Assert.Throws<ArgumentNullException>(() => r.Extract(null!));
  }
}
