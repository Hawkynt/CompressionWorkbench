#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.Matroska;

namespace Compression.Tests.Mkv;

[TestFixture]
public class MkvLayoutMapTests {

  /// <summary>
  /// Builds a minimal MKV file: EBML header + Segment containing Info + one Cluster.
  /// Uses unknown-size encoding for Segment so the file is self-terminating.
  /// </summary>
  private static MemoryStream BuildTestMkv(bool includeCues = false) {
    var ms = new MemoryStream();

    // EBML Header element (ID: 0x1A45DFA3)
    // Body: just EBMLVersion=1 + DocType="matroska"
    var ebmlBody = BuildEbmlHeaderBody();
    WriteEbmlElement(ms, 0x1A45DFA3, ebmlBody);

    // Segment (ID: 0x18538067) with unknown size
    var segStart = ms.Position;
    WriteEbmlId(ms, 0x18538067);
    // Unknown size: 0x01FFFFFFFFFFFFFF (8 bytes, all-ones data)
    ms.Write(new byte[] { 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });

    // Info (ID: 0x1549A966)
    var infoBody = new byte[8]; // minimal
    WriteEbmlElement(ms, 0x1549A966, infoBody);

    // Tracks (ID: 0x1654AE6B)
    var tracksBody = new byte[4]; // minimal
    WriteEbmlElement(ms, 0x1654AE6B, tracksBody);

    // Cluster (ID: 0x1F43B675)
    var clusterBody = new byte[32];
    WriteEbmlElement(ms, 0x1F43B675, clusterBody);

    if (includeCues) {
      // Cues (ID: 0x1C53BB6B)
      var cuesBody = new byte[16];
      WriteEbmlElement(ms, 0x1C53BB6B, cuesBody);
    }

    // Second Cluster
    WriteEbmlElement(ms, 0x1F43B675, new byte[48]);

    ms.Position = 0;
    return ms;
  }

  private static byte[] BuildEbmlHeaderBody() {
    // Minimal: EBMLVersion=1 (ID 0x4286), DocType="matroska" (ID 0x4282)
    var ms = new MemoryStream();
    // EBMLVersion: ID=0x4286, size=1, value=1
    ms.Write(new byte[] { 0x42, 0x86, 0x81, 0x01 });
    // DocType: ID=0x4282, size=8, value="matroska"
    ms.WriteByte(0x42); ms.WriteByte(0x82); ms.WriteByte(0x88);
    ms.Write("matroska"u8);
    return ms.ToArray();
  }

  private static void WriteEbmlId(MemoryStream ms, ulong id) {
    // Write EBML variable-length ID (with leading marker bit)
    if (id <= 0xFF) {
      ms.WriteByte((byte)id);
    } else if (id <= 0xFFFF) {
      ms.WriteByte((byte)(id >> 8));
      ms.WriteByte((byte)(id & 0xFF));
    } else if (id <= 0xFFFFFF) {
      ms.WriteByte((byte)(id >> 16));
      ms.WriteByte((byte)((id >> 8) & 0xFF));
      ms.WriteByte((byte)(id & 0xFF));
    } else {
      ms.WriteByte((byte)(id >> 24));
      ms.WriteByte((byte)((id >> 16) & 0xFF));
      ms.WriteByte((byte)((id >> 8) & 0xFF));
      ms.WriteByte((byte)(id & 0xFF));
    }
  }

  private static void WriteEbmlSize(MemoryStream ms, int size) {
    // Write EBML variable-length size. For simplicity, use 2-byte encoding
    // which supports up to 16383 bytes.
    if (size <= 127) {
      ms.WriteByte((byte)(0x80 | size));
    } else if (size <= 16383) {
      ms.WriteByte((byte)(0x40 | (size >> 8)));
      ms.WriteByte((byte)(size & 0xFF));
    } else {
      // 3-byte encoding
      ms.WriteByte((byte)(0x20 | (size >> 16)));
      ms.WriteByte((byte)((size >> 8) & 0xFF));
      ms.WriteByte((byte)(size & 0xFF));
    }
  }

  private static void WriteEbmlElement(MemoryStream ms, ulong id, byte[] body) {
    WriteEbmlId(ms, id);
    WriteEbmlSize(ms, body.Length);
    ms.Write(body);
  }

  [Test]
  public void EnumerateChunks_HasEbmlHeader() {
    using var ms = BuildTestMkv();
    var chunks = MkvLayoutMap.Enumerate(ms).ToList();

    Assert.That(chunks.Count, Is.GreaterThanOrEqualTo(1));
    var header = chunks.First(c => c.FileName != null && c.FileName.Contains("EBML"));
    Assert.That(header.Kind, Is.EqualTo(DefragBlockKind.MetadataReserved));
  }

  [Test]
  public void EnumerateChunks_HasInfoAndTracks() {
    using var ms = BuildTestMkv();
    var chunks = MkvLayoutMap.Enumerate(ms).ToList();

    var info = chunks.FirstOrDefault(c => c.FileName != null && c.FileName.Contains("Info"));
    var tracks = chunks.FirstOrDefault(c => c.FileName != null && c.FileName.Contains("Tracks"));
    Assert.That(info, Is.Not.Null, "Expected Info element");
    Assert.That(tracks, Is.Not.Null, "Expected Tracks element");
    Assert.That(info!.Kind, Is.EqualTo(DefragBlockKind.MetadataReserved));
    Assert.That(tracks!.Kind, Is.EqualTo(DefragBlockKind.MetadataReserved));
  }

  [Test]
  public void EnumerateChunks_ClustersAreUsed() {
    using var ms = BuildTestMkv();
    var chunks = MkvLayoutMap.Enumerate(ms).ToList();

    var clusters = chunks.Where(c => c.FileName != null && c.FileName.Contains("Cluster")).ToList();
    Assert.That(clusters, Has.Count.GreaterThanOrEqualTo(1));
    foreach (var c in clusters)
      Assert.That(c.Kind, Is.EqualTo(DefragBlockKind.Used));
  }

  [Test]
  public void Descriptor_ImplementsInterfaces() {
    var d = new MkvFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IFileInternalLayoutMap>());
    Assert.That(d, Is.InstanceOf<IFileInternalChunkMover>());
  }

  [Test]
  public void EnumerateChunks_EmptyStream_ReturnsNothing() {
    using var ms = new MemoryStream();
    var chunks = MkvLayoutMap.Enumerate(ms).ToList();
    Assert.That(chunks, Is.Empty);
  }

  [Test]
  public void EnumerateChunks_WithCues_HasCuesElement() {
    using var ms = BuildTestMkv(includeCues: true);
    var chunks = MkvLayoutMap.Enumerate(ms).ToList();

    var cues = chunks.FirstOrDefault(c => c.FileName != null && c.FileName.Contains("Cues"));
    Assert.That(cues, Is.Not.Null, "Expected Cues element");
    Assert.That(cues!.Kind, Is.EqualTo(DefragBlockKind.MetadataReserved));
  }
}
