using System.Buffers.Binary;
using FileFormat.Pcapng;

namespace Compression.Tests.Pcapng;

[TestFixture]
public class PcapngTests {

  /// <summary>
  /// Builds a minimal little-endian pcapng with one SHB, one IDB, and
  /// <paramref name="packetCount"/> Enhanced Packet Blocks of <paramref name="payloadSize"/> bytes each.
  /// </summary>
  private static byte[] BuildPcapng(int packetCount, int payloadSize = 8) {
    using var ms = new MemoryStream();

    // SHB: type(4) + total_len(4) + bom(4) + ver_major(2) + ver_minor(2) + section_len(8) + total_len(4)
    // = 28 bytes. No options.
    var shb = new byte[28];
    BinaryPrimitives.WriteUInt32LittleEndian(shb.AsSpan(0, 4), PcapngReader.BtSectionHeader);
    BinaryPrimitives.WriteUInt32LittleEndian(shb.AsSpan(4, 4), 28);
    BinaryPrimitives.WriteUInt32LittleEndian(shb.AsSpan(8, 4), PcapngReader.ByteOrderMagic);
    BinaryPrimitives.WriteUInt16LittleEndian(shb.AsSpan(12, 2), 1);  // major
    BinaryPrimitives.WriteUInt16LittleEndian(shb.AsSpan(14, 2), 0);  // minor
    BinaryPrimitives.WriteInt64LittleEndian(shb.AsSpan(16, 8), -1);  // section length unknown
    BinaryPrimitives.WriteUInt32LittleEndian(shb.AsSpan(24, 4), 28);
    ms.Write(shb);

    // IDB: type(4) + total_len(4) + linktype(2) + reserved(2) + snaplen(4) + total_len(4) = 20 bytes.
    var idb = new byte[20];
    BinaryPrimitives.WriteUInt32LittleEndian(idb.AsSpan(0, 4), PcapngReader.BtInterfaceDescription);
    BinaryPrimitives.WriteUInt32LittleEndian(idb.AsSpan(4, 4), 20);
    BinaryPrimitives.WriteUInt16LittleEndian(idb.AsSpan(8, 2), 1);     // link type Ethernet
    // reserved bytes 10..12 = 0
    BinaryPrimitives.WriteUInt32LittleEndian(idb.AsSpan(12, 4), 65535); // snaplen
    BinaryPrimitives.WriteUInt32LittleEndian(idb.AsSpan(16, 4), 20);
    ms.Write(idb);

    // EPB per packet: type(4) + total_len(4) + ifid(4) + ts_high(4) + ts_low(4) + cap(4) + orig(4) + data + pad + total_len(4)
    var rng = new Random(42);
    for (var i = 0; i < packetCount; i++) {
      var pad = (4 - (payloadSize & 3)) & 3;
      var totalLen = 32 + payloadSize + pad;
      var epb = new byte[totalLen];
      BinaryPrimitives.WriteUInt32LittleEndian(epb.AsSpan(0, 4), PcapngReader.BtEnhancedPacket);
      BinaryPrimitives.WriteUInt32LittleEndian(epb.AsSpan(4, 4), (uint)totalLen);
      BinaryPrimitives.WriteUInt32LittleEndian(epb.AsSpan(8, 4), 0);  // interface 0
      BinaryPrimitives.WriteUInt32LittleEndian(epb.AsSpan(12, 4), 0); // ts_high
      BinaryPrimitives.WriteUInt32LittleEndian(epb.AsSpan(16, 4), (uint)(1_700_000_000 + i)); // ts_low
      BinaryPrimitives.WriteUInt32LittleEndian(epb.AsSpan(20, 4), (uint)payloadSize); // captured
      BinaryPrimitives.WriteUInt32LittleEndian(epb.AsSpan(24, 4), (uint)payloadSize); // original
      var payload = new byte[payloadSize];
      rng.NextBytes(payload);
      payload.CopyTo(epb.AsSpan(28));
      // padding bytes already zero
      BinaryPrimitives.WriteUInt32LittleEndian(epb.AsSpan(28 + payloadSize + pad, 4), (uint)totalLen);
      ms.Write(epb);
    }

    return ms.ToArray();
  }

  [Test, Category("HappyPath")]
  public void Read_ParsesSectionAndInterfaceAndPackets() {
    var data = BuildPcapng(3, payloadSize: 16);
    var capture = PcapngReader.Read(data);
    Assert.That(capture.LittleEndian, Is.True);
    Assert.That(capture.VersionMajor, Is.EqualTo(1));
    Assert.That(capture.Interfaces, Has.Count.EqualTo(1));
    Assert.That(capture.Interfaces[0].LinkType, Is.EqualTo(1u));
    Assert.That(capture.Packets, Has.Count.EqualTo(3));
    Assert.That(capture.Packets[0].Data, Has.Length.EqualTo(16));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_ReturnsMetadataAndPacketEntries() {
    var data = BuildPcapng(4);
    using var ms = new MemoryStream(data);
    var entries = new PcapngFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Count(e => e.Name.StartsWith("packet_")), Is.EqualTo(4));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Extract_WritesPayloads() {
    var data = BuildPcapng(2, payloadSize: 12);
    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(data);
      new PcapngFormatDescriptor().Extract(ms, tmp, null, null);
      var packetFiles = Directory.GetFiles(tmp, "packet_*.bin");
      Assert.That(packetFiles, Has.Length.EqualTo(2));
      Assert.That(new FileInfo(packetFiles[0]).Length, Is.EqualTo(12));
      Assert.That(File.Exists(Path.Combine(tmp, "metadata.ini")), Is.True);
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("EdgeCase")]
  public void Capture_With150Packets_TruncatesListingAt100() {
    var data = BuildPcapng(150, payloadSize: 4);
    using var ms = new MemoryStream(data);
    var entries = new PcapngFormatDescriptor().List(ms, null);
    Assert.That(entries.Count(e => e.Name.StartsWith("packet_")), Is.EqualTo(100));
  }

  [Test, Category("EdgeCase")]
  public void Read_TruncatedFile_Throws() {
    var data = new byte[6];
    Assert.That(() => PcapngReader.Read(data), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("EdgeCase")]
  public void Read_InvalidByteOrderMagic_Throws() {
    var data = new byte[28];
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0, 4), PcapngReader.BtSectionHeader);
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4, 4), 28);
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8, 4), 0xDEADBEEFu);
    Assert.That(() => PcapngReader.Read(data), Throws.InstanceOf<InvalidDataException>());
  }
}
