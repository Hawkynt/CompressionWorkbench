using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.Pcap;
using FileFormat.Pcapng;

namespace Compression.Tests.Pcap;

/// <summary>
/// Create and edit for pcap and pcapng: what we write must read back packet for packet, edits must
/// leave every untouched record byte-identical, and anything we cannot rewrite losslessly is refused
/// with the input left as it was.
/// </summary>
[TestFixture]
public class CaptureWriteTests {

  private static readonly DateTimeOffset T0 = new(2026, 10, 26, 9, 30, 15, TimeSpan.Zero);

  private static ArchiveInputInfo Packet(string name, byte[] data, DateTimeOffset? at = null)
    => ArchiveInputInfo.InMemory(name, data, new ArchiveEntryMetadata(LastWriteTimeUtc: at));

  private static byte[] Bytes(int length, int seed) => [.. Enumerable.Range(0, length).Select(i => (byte)(i * 7 + seed))];

  private static byte[] Create(IArchiveCreatable d, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions? options = null) {
    using var ms = new MemoryStream();
    d.Create(ms, inputs, options ?? new FormatCreateOptions());
    return ms.ToArray();
  }

  private static byte[] Entry(IArchiveFormatOperations d, byte[] archive, string name) {
    using var ms = new MemoryStream(archive);
    return d.ExtractEntryToMemory(ms, name, null);
  }

  private static List<ArchiveEntryInfo> List(IArchiveFormatOperations d, byte[] archive) {
    using var ms = new MemoryStream(archive);
    return d.List(ms, null);
  }

  // ── pcap ────────────────────────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void Given_TwoFiles_When_PcapCreated_Then_LittleEndianMicrosecondCaptureWithBothPackets() {
    var a = Bytes(60, 1);
    var b = Bytes(1514, 2);
    var image = Create(new PcapFormatDescriptor(), [Packet("a.bin", a, T0), Packet("b.bin", b, T0.AddTicks(1234567))]);

    Assert.That(image.AsSpan(0, 4).ToArray(), Is.EqualTo(new byte[] { 0xD4, 0xC3, 0xB2, 0xA1 }));
    var capture = PcapReader.Read(image);
    Assert.That(capture.LittleEndian, Is.True);
    Assert.That(capture.Nanosecond, Is.False);
    Assert.That((capture.VersionMajor, capture.VersionMinor), Is.EqualTo(((ushort)2, (ushort)4)));
    Assert.That(capture.LinkType, Is.EqualTo(1u));
    Assert.That(capture.Snaplen, Is.EqualTo(PcapSettings.DefaultSnaplen));
    Assert.That(capture.Packets.Select(p => p.Data), Is.EqualTo(new[] { a, b }));
    Assert.That(capture.Packets[1].TimestampSeconds, Is.EqualTo((uint)T0.ToUnixTimeSeconds()));
    Assert.That(capture.Packets[1].TimestampFraction, Is.EqualTo(123456u));
    Assert.That(image, Has.Length.EqualTo(24 + 16 + 60 + 16 + 1514));
  }

  [Test, Category("RoundTrip")]
  public void Given_NanosecondOption_When_PcapCreated_Then_NanosecondMagicAndFraction() {
    var image = Create(new PcapFormatDescriptor(), [Packet("a", [1, 2, 3], T0.AddTicks(9_876_543))],
      new FormatCreateOptions { FormatSpecific = new() { ["nanosecond"] = "true", ["linktype"] = "101" } });
    Assert.That(image.AsSpan(0, 4).ToArray(), Is.EqualTo(new byte[] { 0x4D, 0x3C, 0xB2, 0xA1 }));
    var capture = PcapReader.Read(image);
    Assert.That(capture.Nanosecond, Is.True);
    Assert.That(capture.LinkType, Is.EqualTo(101u));
    Assert.That(capture.Packets[0].TimestampFraction, Is.EqualTo(987_654_300u));
  }

  [Test, Category("RoundTrip")]
  public void Given_ExtractedCapture_When_RecreatedFromItsEntries_Then_HeaderSettingsSurvive() {
    var d = new PcapFormatDescriptor();
    var original = Create(d, [Packet("a", Bytes(40, 3), T0)],
      new FormatCreateOptions { FormatSpecific = new() { ["linktype"] = "113", ["snaplen"] = "4096", ["nanosecond"] = "1" } });
    var inputs = List(d, original).Select(e => Packet(e.Name, Entry(d, original, e.Name), e.LastModified)).ToList();
    var copy = Create(d, inputs);
    Assert.That(copy, Is.EqualTo(original));
  }

  [Test, Category("EdgeCase")]
  public void Given_PacketOfExactlySnaplen_When_Created_Then_Accepted_AndOneMoreByteIsRefused() {
    var options = new FormatCreateOptions { FormatSpecific = new() { ["snaplen"] = "64" } };
    Assert.That(PcapReader.Read(Create(new PcapFormatDescriptor(), [Packet("a", new byte[64])], options)).Packets, Has.Count.EqualTo(1));
    Assert.Throws<ArgumentException>(() => Create(new PcapFormatDescriptor(), [Packet("a", new byte[65])], options));
  }

  [Test, Category("EdgeCase")]
  public void Given_NoInputs_When_PcapCreated_Then_HeaderOnlyCaptureListsOnlyMetadata() {
    var image = Create(new PcapFormatDescriptor(), []);
    Assert.That(image, Has.Length.EqualTo(24));
    Assert.That(List(new PcapFormatDescriptor(), image).Select(e => e.Name), Is.EqualTo(new[] { "metadata.ini" }));
  }

  [Test, Category("EdgeCase")]
  public void Given_TimestampBefore1970_When_PcapCreated_Then_Refused() {
    Assert.Throws<ArgumentOutOfRangeException>(() =>
      Create(new PcapFormatDescriptor(), [Packet("a", [1], new DateTimeOffset(1969, 12, 31, 0, 0, 0, TimeSpan.Zero))]));
  }

  [Test, Category("RoundTrip")]
  public void Given_Capture_When_PacketRemoved_Then_OtherRecordsAndHeaderAreByteIdentical() {
    var d = new PcapFormatDescriptor();
    var image = Create(d, [Packet("a", Bytes(10, 1), T0), Packet("b", Bytes(11, 2), T0.AddSeconds(1)), Packet("c", Bytes(12, 3), T0.AddSeconds(2))]);
    using var ms = new MemoryStream();
    ms.Write(image);
    d.Remove(ms, ["packet_0001.bin"]);
    var expected = image[..(24 + 26)].Concat(image[(24 + 26 + 27)..]).ToArray();
    Assert.That(ms.ToArray(), Is.EqualTo(expected));
  }

  [Test, Category("RoundTrip")]
  public void Given_Capture_When_PacketReplacedAndOneAppended_Then_ReplacementKeepsItsTimestamp() {
    var d = new PcapFormatDescriptor();
    var image = Create(d, [Packet("a", Bytes(10, 1), T0), Packet("b", Bytes(11, 2), T0.AddSeconds(1))]);
    using var ms = new MemoryStream();
    ms.Write(image);
    d.Add(ms, [Packet("packet_0000.bin", Bytes(30, 9)), Packet("new.bin", Bytes(5, 4), T0.AddSeconds(5))]);
    var capture = PcapReader.Read(ms.ToArray());
    Assert.That(capture.Packets.Select(p => p.Data), Is.EqualTo(new[] { Bytes(30, 9), Bytes(11, 2), Bytes(5, 4) }));
    Assert.That(capture.Packets.Select(p => p.TimestampSeconds),
      Is.EqualTo(new[] { (uint)T0.ToUnixTimeSeconds(), (uint)T0.ToUnixTimeSeconds() + 1, (uint)T0.ToUnixTimeSeconds() + 5 }));
    Assert.That(ms.ToArray().AsSpan(24 + 46, 27).ToArray(), Is.EqualTo(image.AsSpan(24 + 26, 27).ToArray()));
  }

  [Test, Category("RoundTrip")]
  public void Given_BigEndianCapture_When_PacketAppended_Then_TheNewRecordIsBigEndianToo() {
    var d = new PcapFormatDescriptor();
    var metadata = Encoding.UTF8.GetBytes("[pcap]\nendian = big\nlink_type = 1\n");
    var image = Create(d, [Packet("metadata.ini", metadata), Packet("a", Bytes(8, 1), T0)]);
    Assert.That(image.AsSpan(0, 4).ToArray(), Is.EqualTo(new byte[] { 0xA1, 0xB2, 0xC3, 0xD4 }));
    using var ms = new MemoryStream();
    ms.Write(image);
    d.Add(ms, [Packet("b", Bytes(9, 2), T0)]);
    var capture = PcapReader.Read(ms.ToArray());
    Assert.That(capture.LittleEndian, Is.False);
    Assert.That(capture.Packets.Select(p => p.Data), Is.EqualTo(new[] { Bytes(8, 1), Bytes(9, 2) }));
    Assert.That(BinaryPrimitives.ReadUInt32BigEndian(ms.ToArray().AsSpan(24 + 24 + 8)), Is.EqualTo(9u));
  }

  [Test, Category("RoundTrip")]
  public void Given_Capture_When_Purged_Then_OnlyTheGlobalHeaderRemains() {
    var d = new PcapFormatDescriptor();
    var image = Create(d, [Packet("a", Bytes(10, 1), T0), Packet("b", Bytes(11, 2), T0)]);
    using var ms = new MemoryStream();
    ms.Write(image);
    ((IArchivePurgeable)d).Purge(ms);
    Assert.That(ms.ToArray(), Is.EqualTo(image[..24]));
  }

  [Test, Category("EdgeCase")]
  public void Given_TruncatedLastRecord_When_Edited_Then_RefusedAndUntouched() {
    var d = new PcapFormatDescriptor();
    var image = Create(d, [Packet("a", Bytes(10, 1), T0), Packet("b", Bytes(11, 2), T0)])[..^3];
    using var ms = new MemoryStream();
    ms.Write(image);
    Assert.Throws<NotSupportedException>(() => d.Remove(ms, ["packet_0000.bin"]));
    Assert.Throws<NotSupportedException>(() => d.Add(ms, [Packet("c", [1])]));
    Assert.That(ms.ToArray(), Is.EqualTo(image));
    Assert.That(PcapReader.Read(image).Packets, Has.Count.EqualTo(1), "the complete first packet still reads");
  }

  [Test, Category("EdgeCase")]
  public void Given_Capture_When_MetadataRemovedOrChanged_Then_Refused() {
    var d = new PcapFormatDescriptor();
    var image = Create(d, [Packet("a", Bytes(10, 1), T0)]);
    using var ms = new MemoryStream();
    ms.Write(image);
    Assert.Throws<NotSupportedException>(() => d.Remove(ms, ["metadata.ini"]));
    Assert.Throws<NotSupportedException>(() => d.Add(ms, [Packet("metadata.ini", "[pcap]\nlink_type = 101\n"u8.ToArray())]));
    Assert.That(ms.ToArray(), Is.EqualTo(image));
  }

  [TestCase(new byte[] { 0xD4, 0xC3, 0xB2, 0xA1 }, true, false)]
  [TestCase(new byte[] { 0xA1, 0xB2, 0xC3, 0xD4 }, false, false)]
  [TestCase(new byte[] { 0x4D, 0x3C, 0xB2, 0xA1 }, true, true)]
  [TestCase(new byte[] { 0xA1, 0xB2, 0x3C, 0x4D }, false, true)]
  [Category("EdgeCase")]
  public void Given_EachMagicForm_When_Read_Then_ByteOrderAndResolutionFollowLibpcap(byte[] magic, bool little, bool nano) {
    var header = new byte[24];
    magic.CopyTo(header, 0);
    var capture = PcapReader.Read(header);
    Assert.That((capture.LittleEndian, capture.Nanosecond), Is.EqualTo((little, nano)));
    Assert.That(capture.VersionMajor, Is.EqualTo(0));
  }

  [Test, Category("EdgeCase")]
  public void Given_ForeignMagic_When_Read_Then_InvalidData()
    => Assert.Throws<InvalidDataException>(() => PcapReader.Read(new byte[24]));

  // ── pcapng ──────────────────────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void Given_TwoFiles_When_PcapngCreated_Then_ShbIdbAndTwoEnhancedPacketBlocks() {
    var a = Bytes(61, 1);
    var b = Bytes(64, 2);
    var image = Create(new PcapngFormatDescriptor(), [Packet("a", a, T0), Packet("b", b, T0.AddTicks(10))]);
    Assert.That(image, Has.Length.EqualTo(28 + 20 + (32 + 64) + (32 + 64)));
    var capture = PcapngReader.Read(image);
    Assert.That(capture.Interfaces.Single().LinkType, Is.EqualTo(1u));
    Assert.That(capture.Packets.Select(p => p.Data), Is.EqualTo(new[] { a, b }));
    Assert.That(capture.Packets[1].TimestampRaw, Is.EqualTo((ulong)T0.ToUnixTimeSeconds() * 1_000_000 + 1));
    var listed = List(new PcapngFormatDescriptor(), image);
    Assert.That(listed[2].LastModified, Is.EqualTo(T0.AddTicks(10).UtcDateTime));
  }

  [Test, Category("RoundTrip")]
  public void Given_NanosecondOption_When_PcapngCreated_Then_IfTsResolNineDrivesTheTimestamps() {
    var d = new PcapngFormatDescriptor();
    var image = Create(d, [Packet("a", [9, 9], T0.AddTicks(1))],
      new FormatCreateOptions { FormatSpecific = new() { ["nanosecond"] = "true" } });
    var layout = PcapngReader.Read(image);
    Assert.That(layout.Interfaces[0].UnitsPerSecond, Is.EqualTo(1_000_000_000UL));
    Assert.That(layout.Interfaces[0].TsResolOption, Is.EqualTo((byte)9));
    Assert.That(layout.Packets[0].TimestampRaw, Is.EqualTo((ulong)T0.ToUnixTimeSeconds() * 1_000_000_000 + 100));
    Assert.That(List(d, image)[1].LastModified, Is.EqualTo(T0.AddTicks(1).UtcDateTime));
    Assert.That(Encoding.UTF8.GetString(Entry(d, image, "metadata.ini")), Does.Contain("timestamp_resolution = nanosecond"));
  }

  [Test, Category("RoundTrip")]
  public void Given_ExtractedPcapng_When_RecreatedFromItsEntries_Then_ByteIdentical() {
    var d = new PcapngFormatDescriptor();
    var original = Create(d, [Packet("a", Bytes(40, 3), T0), Packet("b", Bytes(3, 1), T0.AddSeconds(3))],
      new FormatCreateOptions { FormatSpecific = new() { ["linktype"] = "228", ["snaplen"] = "1500", ["nanosecond"] = "1" } });
    var inputs = List(d, original).Select(e => Packet(e.Name, Entry(d, original, e.Name), e.LastModified)).ToList();
    Assert.That(Create(d, inputs), Is.EqualTo(original));
  }

  [Test, Category("RoundTrip")]
  public void Given_Pcapng_When_PacketRemovedReplacedAndAppended_Then_UntouchedBlocksSurviveVerbatim() {
    var d = new PcapngFormatDescriptor();
    var image = Create(d, [Packet("a", Bytes(4, 1), T0), Packet("b", Bytes(5, 2), T0.AddSeconds(1)), Packet("c", Bytes(6, 3), T0.AddSeconds(2))]);
    using var ms = new MemoryStream();
    ms.Write(image);
    d.Remove(ms, ["packet_0000.bin"]);
    d.Add(ms, [Packet("packet_0001.bin", Bytes(20, 7)), Packet("z", Bytes(7, 5), T0.AddSeconds(9))]);
    var edited = ms.ToArray();
    var capture = PcapngReader.Read(edited);
    Assert.That(capture.Packets.Select(p => p.Data), Is.EqualTo(new[] { Bytes(5, 2), Bytes(20, 7), Bytes(7, 5) }));
    Assert.That(capture.Packets[1].TimestampRaw, Is.EqualTo((ulong)(T0.ToUnixTimeSeconds() + 2) * 1_000_000));
    // SHB + IDB unchanged, then packet b's block verbatim
    Assert.That(edited.AsSpan(0, 48 + 40).ToArray(), Is.EqualTo(image[..48].Concat(image.AsSpan(48 + 36, 40).ToArray()).ToArray()));
  }

  [Test, Category("RoundTrip")]
  public void Given_DeclaredSectionLength_When_PacketRemoved_Then_SectionLengthIsRecomputed() {
    var d = new PcapngFormatDescriptor();
    var image = Create(d, [Packet("a", Bytes(4, 1), T0), Packet("b", Bytes(8, 2), T0)]);
    BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(16), image.Length - 28);
    using var ms = new MemoryStream();
    ms.Write(image);
    d.Remove(ms, ["packet_0000.bin"]);
    var edited = ms.ToArray();
    Assert.That(BinaryPrimitives.ReadInt64LittleEndian(edited.AsSpan(16)), Is.EqualTo(edited.Length - 28));
    Assert.That(PcapngReader.Read(edited).Packets.Single().Data, Is.EqualTo(Bytes(8, 2)));
  }

  [Test, Category("EdgeCase")]
  public void Given_PcapngWithoutInterface_When_PacketAppended_Then_RefusedAndUntouched() {
    var image = PcapngWriter.SectionHeader();
    using var ms = new MemoryStream();
    ms.Write(image);
    Assert.Throws<NotSupportedException>(() => new PcapngFormatDescriptor().Add(ms, [Packet("a", [1])]));
    Assert.That(ms.ToArray(), Is.EqualTo(image));
  }

  [Test, Category("EdgeCase")]
  public void Given_SimplePacketBlock_When_Replaced_Then_Refused() {
    var spb = new byte[20];
    BinaryPrimitives.WriteUInt32LittleEndian(spb, PcapngReader.BtSimplePacket);
    BinaryPrimitives.WriteUInt32LittleEndian(spb.AsSpan(4), 20);
    BinaryPrimitives.WriteUInt32LittleEndian(spb.AsSpan(8), 4);
    BinaryPrimitives.WriteUInt32LittleEndian(spb.AsSpan(16), 20);
    var image = PcapngWriter.SectionHeader().Concat(PcapngWriter.InterfaceDescription(new PcapngSettings())).Concat(spb).ToArray();
    using var ms = new MemoryStream();
    ms.Write(image);
    var d = new PcapngFormatDescriptor();
    Assert.That(PcapngReader.Read(image).Packets.Single().Data, Has.Length.EqualTo(4));
    Assert.Throws<NotSupportedException>(() => d.Add(ms, [Packet("packet_0000.bin", [1, 2])]));
    Assert.That(ms.ToArray(), Is.EqualTo(image));
  }

  [Test, Category("EdgeCase")]
  public void Given_TrailingGarbage_When_PcapngEdited_Then_RefusedAndUntouched() {
    var d = new PcapngFormatDescriptor();
    var image = Create(d, [Packet("a", Bytes(4, 1), T0)]).Concat(new byte[] { 1, 2, 3 }).ToArray();
    using var ms = new MemoryStream();
    ms.Write(image);
    Assert.Throws<NotSupportedException>(() => d.Remove(ms, ["packet_0000.bin"]));
    Assert.That(ms.ToArray(), Is.EqualTo(image));
  }

  [TestCase((byte)6, 1_000_000UL)]
  [TestCase((byte)9, 1_000_000_000UL)]
  [TestCase((byte)0, 1UL)]
  [TestCase((byte)0x8A, 1024UL)]
  [Category("EdgeCase")]
  public void Given_IfTsResol_When_Decoded_Then_UnitsFollowTheSpec(byte value, ulong units)
    => Assert.That(PcapngReader.Interface.UnitsFromTsResol(value), Is.EqualTo(units));

  [Test, Category("EdgeCase")]
  public void Given_IfTsResolBeyondUlong_When_Decoded_Then_InvalidData()
    => Assert.Throws<InvalidDataException>(() => PcapngReader.Interface.UnitsFromTsResol(20));
}
