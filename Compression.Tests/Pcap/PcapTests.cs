using System.Buffers.Binary;
using FileFormat.Pcap;

namespace Compression.Tests.Pcap;

[TestFixture]
public class PcapTests {

  // Build a little-endian microsecond-resolution pcap with `packetCount` tiny packets.
  //
  // The magic constant identifies byte order by whether the raw bytes, read as a
  // little-endian uint32, match 0xA1B2C3D4 (native BE) or 0xD4C3B2A1 (swap, LE).
  // To produce an LE file we need the raw bytes to form 0xD4C3B2A1 when read LE,
  // i.e. the file must literally start with A1 B2 C3 D4.
  private static byte[] BuildPcap(int packetCount, int payloadSize = 8) {
    var ms = new MemoryStream();
    // Global header (24 bytes) — all multi-byte fields little-endian.
    var ghdr = new byte[24];
    ghdr[0] = 0xA1; ghdr[1] = 0xB2; ghdr[2] = 0xC3; ghdr[3] = 0xD4;  // magic → LE/µs form
    BinaryPrimitives.WriteUInt16LittleEndian(ghdr.AsSpan(4), 2);     // version major
    BinaryPrimitives.WriteUInt16LittleEndian(ghdr.AsSpan(6), 4);     // version minor
    // bytes 8..12 = thiszone, 12..16 = sigfigs — left zero
    BinaryPrimitives.WriteUInt32LittleEndian(ghdr.AsSpan(16), 65535); // snaplen
    BinaryPrimitives.WriteUInt32LittleEndian(ghdr.AsSpan(20), 1);     // link type = Ethernet
    ms.Write(ghdr);

    var rng = new Random(42);
    for (var i = 0; i < packetCount; i++) {
      var rec = new byte[16];
      BinaryPrimitives.WriteUInt32LittleEndian(rec, 1_700_000_000u + (uint)i);
      BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(4), 0);
      BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(8), (uint)payloadSize);
      BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(12), (uint)payloadSize);
      ms.Write(rec);
      var payload = new byte[payloadSize];
      rng.NextBytes(payload);
      ms.Write(payload);
    }
    return ms.ToArray();
  }

  [Category("HappyPath")]
  [Test]
  public void List_ReturnsMetadataAndOneEntryPerPacket() {
    var data = BuildPcap(3);
    using var ms = new MemoryStream(data);
    var desc = new PcapFormatDescriptor();
    var entries = desc.List(ms, null);

    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Count(e => e.Name.StartsWith("packet_")), Is.EqualTo(3));
  }

  [Category("HappyPath"), Category("RoundTrip")]
  [Test]
  public void Extract_WritesPacketBodies() {
    var data = BuildPcap(2, payloadSize: 16);
    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(data);
      var desc = new PcapFormatDescriptor();
      desc.Extract(ms, tmp, null, null);

      var packetFiles = Directory.GetFiles(tmp, "packet_*.bin");
      Assert.That(packetFiles.Length, Is.EqualTo(2));
      Assert.That(new FileInfo(packetFiles[0]).Length, Is.EqualTo(16));
      Assert.That(File.Exists(Path.Combine(tmp, "metadata.ini")), Is.True);
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Category("EdgeCase")]
  [Test]
  public void Capture_With150Packets_TruncatesListingAt100() {
    var data = BuildPcap(150, payloadSize: 4);
    using var ms = new MemoryStream(data);
    var desc = new PcapFormatDescriptor();
    var entries = desc.List(ms, null);

    var packetEntries = entries.Where(e => e.Name.StartsWith("packet_")).ToList();
    Assert.That(packetEntries, Has.Count.EqualTo(100));
  }
}
