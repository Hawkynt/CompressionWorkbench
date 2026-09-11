using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Compression.Core.Streams;
using Compression.Registry;
using FileFormat.Bzip2;
using FileFormat.Dmg;
using FileFormat.Lzfse;
using FileFormat.Lzma;

namespace Compression.Tests.Dmg;

[TestFixture]
public sealed class DmgCodecTests {
  private const int SectorSize = 512;
  private const int MishHeaderSize = 204;
  private const int MishBlockSize = 40;

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void ZlibChunk_WithNonZeroDataForkOffset_ExtractsExactly() {
    var payload = Payload(0x13);
    var dmg = BuildSingleBlockDmg("zlib.img", payload, 0x80000005, CompressZlib(payload), dataForkPrefix: 37);

    using var image = new MemoryStream(dmg);
    using var reader = new DmgReader(image);

    Assert.That(reader.Extract(reader.Entries.Single()), Is.EqualTo(payload));
  }

  [TestCase("bzip2", 0x80000006u)]
  [TestCase("lzfse", 0x80000007u)]
  [TestCase("lzma", 0x80000008u)]
  [Category("HappyPath"), Category("RoundTrip")]
  public void ManagedCompressedChunk_RoundTrips(string codec, uint chunkType) {
    var payload = Payload(unchecked((byte)chunkType));
    var stored = codec switch {
      "bzip2" => CompressBzip2(payload),
      "lzfse" => CompressLzfse(payload),
      "lzma" => CompressLzma(payload),
      _ => throw new AssertionException($"Unknown test codec {codec}."),
    };
    var dmg = BuildSingleBlockDmg($"{codec}.img", payload, chunkType, stored);

    using var image = new MemoryStream(dmg);
    using var reader = new DmgReader(image);

    Assert.That(reader.Extract(reader.Entries.Single()), Is.EqualTo(payload));
  }

  [Test, Category("HappyPath")]
  public void IgnoreChunk_ReconstructsAsZeros() {
    var payload = new byte[2 * SectorSize];
    var dmg = BuildSingleBlockDmg("free.img", payload, 0x00000002, []);

    using var image = new MemoryStream(dmg);
    using var reader = new DmgReader(image);

    Assert.That(reader.Extract(reader.Entries.Single()), Is.EqualTo(payload));
  }

  [Test, Category("ErrorHandling")]
  public void CorruptZlibChunk_ThrowsInsteadOfReturningZeroFilledData() {
    var payload = Payload(0x45);
    var dmg = BuildSingleBlockDmg("bad.img", payload, 0x80000005, [0x78, 0x9C, 0x01]);

    using var image = new MemoryStream(dmg);
    using var reader = new DmgReader(image);

    Assert.Throws<InvalidDataException>(() => reader.Extract(reader.Entries.Single()));
  }

  [Test, Category("HappyPath"), Category("Interoperability")]
  public void AdcChunk_DecodesLegacyUdifStream() {
    var payload = Enumerable.Repeat((byte)0x5A, SectorSize).ToArray();
    var stored = BuildAdcRepeatedByte(0x5A, payload.Length);
    var dmg = BuildSingleBlockDmg("adc.img", payload, 0x80000004, stored, dataForkPrefix: 19);

    using var image = new MemoryStream(dmg);
    using var reader = new DmgReader(image);

    Assert.That(reader.Extract(reader.Entries.Single()), Is.EqualTo(payload));
  }

  [Test, Category("HappyPath"), Category("Interoperability")]
  public void AdcDecoder_ShortAndLongOverlappingMatches_ReconstructExactly() {
    byte[] stored = [0x83, (byte)'A', (byte)'B', (byte)'C', (byte)'D', 0x00, 0x03, 0x41, 0x00, 0x06];
    var decoded = new byte[12];

    DmgAdcDecoder.Decode(stored, decoded);

    Assert.That(decoded, Is.EqualTo("ABCDABCABCDA"u8.ToArray()));
  }

  [Test, Category("ErrorHandling")]
  public void AdcDecoder_InvalidHistoryDistance_IsRejected() {
    byte[] stored = [0x00, 0x03]; // distance 4 with no output history
    var decoded = new byte[3];

    Assert.Throws<InvalidDataException>(() => DmgAdcDecoder.Decode(stored, decoded));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void AddToForeignCompressedProfile_RebuildsAndPreservesExistingPartition() {
    var descriptor = new DmgFormatDescriptor();
    var original = Payload(0x69);
    var added = Enumerable.Range(0, 731).Select(i => (byte)(i * 17)).ToArray();
    var dmg = BuildSingleBlockDmg("base.img", original, 0x80000005, CompressZlib(original), dataForkPrefix: 29);

    using var image = ExpandableStream(dmg);
    ((IArchiveModifiable)descriptor).Add(image, [ArchiveInputInfo.InMemory("new.bin", added)]);

    AssertPayload(image, "base.img", original);
    AssertPayload(image, "new.bin", added);

    image.Position = 0;
    using var reader = new DmgReader(image, leaveOpen: true);
    Assert.That(reader.IsWorkbenchRawProfile, Is.True,
      "foreign profiles are normalized to the writer's canonical raw UDIF profile after editing");
  }

  [Test, Category("ErrorHandling")]
  public void StoredChunkOutsideDataFork_IsRejected() {
    var payload = Payload(0x7B);
    var stored = CompressZlib(payload);
    var dmg = BuildSingleBlockDmg("bad-range.img", payload, 0x80000005, stored);

    var koly = dmg.AsSpan(dmg.Length - 512, 512);
    BinaryPrimitives.WriteUInt64BigEndian(koly[32..], checked((ulong)(stored.Length - 1)));

    using var image = new MemoryStream(dmg);
    using var reader = new DmgReader(image);

    Assert.Throws<InvalidDataException>(() => reader.Extract(reader.Entries.Single()));
  }

  private static byte[] Payload(byte seed) {
    var result = new byte[SectorSize];
    for (var i = 0; i < result.Length; ++i)
      result[i] = unchecked((byte)(seed + i * 29));
    return result;
  }

  private static byte[] BuildAdcRepeatedByte(byte value, int outputLength) {
    if (outputLength < 1)
      throw new ArgumentOutOfRangeException(nameof(outputLength));

    using var stored = new MemoryStream();
    stored.WriteByte(0x80); // one literal
    stored.WriteByte(value);

    var remaining = outputLength - 1;
    while (remaining > 0) {
      var length = Math.Min(67, remaining);
      if (length < 4)
        throw new AssertionException("Synthetic ADC stream needs a final match of at least four bytes.");
      stored.WriteByte((byte)(0x40 | (length - 4))); // long match
      stored.WriteByte(0x00);
      stored.WriteByte(0x00); // encoded offset 0 => distance 1
      remaining -= length;
    }

    return stored.ToArray();
  }

  private static byte[] CompressZlib(byte[] data) {
    using var output = new MemoryStream();
    using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
      zlib.Write(data);
    return output.ToArray();
  }

  private static byte[] CompressBzip2(byte[] data) {
    using var output = new MemoryStream();
    using (var bzip2 = new Bzip2Stream(output, CompressionStreamMode.Compress, leaveOpen: true))
      bzip2.Write(data);
    return output.ToArray();
  }

  private static byte[] CompressLzfse(byte[] data) {
    using var input = new MemoryStream(data, writable: false);
    using var output = new MemoryStream();
    LzfseStream.Compress(input, output);
    return output.ToArray();
  }

  private static byte[] CompressLzma(byte[] data) {
    using var input = new MemoryStream(data, writable: false);
    using var output = new MemoryStream();
    LzmaStream.Compress(input, output);
    return output.ToArray();
  }

  private static MemoryStream ExpandableStream(byte[] data) {
    var result = new MemoryStream();
    result.Write(data);
    result.Position = 0;
    return result;
  }

  private static void AssertPayload(MemoryStream image, string name, byte[] expected) {
    image.Position = 0;
    using var reader = new DmgReader(image, leaveOpen: true);
    var entry = reader.Entries.Single(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
    Assert.That(reader.Extract(entry), Is.EqualTo(expected));
  }

  private static byte[] BuildSingleBlockDmg(string name, byte[] logicalData, uint chunkType,
      byte[] storedData, int dataForkPrefix = 0) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(logicalData);
    ArgumentNullException.ThrowIfNull(storedData);
    if (logicalData.Length == 0 || logicalData.Length % SectorSize != 0)
      throw new ArgumentException("Synthetic DMG payload must be a non-empty whole number of sectors.", nameof(logicalData));
    if (dataForkPrefix < 0)
      throw new ArgumentOutOfRangeException(nameof(dataForkPrefix));

    var sectorCount = checked((ulong)(logicalData.Length / SectorSize));
    var mish = BuildMish(chunkType, sectorCount, checked((ulong)storedData.Length));
    var xml = BuildXml(name, mish);
    var xmlBytes = Encoding.UTF8.GetBytes(xml);
    var dataForkOffset = dataForkPrefix;
    var xmlOffset = checked(dataForkOffset + storedData.Length);
    var koly = BuildKoly(dataForkOffset, storedData.Length, xmlOffset, xmlBytes.Length, sectorCount);

    var image = new byte[checked(xmlOffset + xmlBytes.Length + koly.Length)];
    Array.Fill(image, (byte)0xE7, 0, dataForkPrefix);
    storedData.CopyTo(image, dataForkOffset);
    xmlBytes.CopyTo(image, xmlOffset);
    koly.CopyTo(image, xmlOffset + xmlBytes.Length);
    return image;
  }

  private static byte[] BuildMish(uint chunkType, ulong sectorCount, ulong storedLength) {
    var result = new byte[MishHeaderSize + 2 * MishBlockSize];
    var span = result.AsSpan();
    "mish"u8.CopyTo(span);
    BinaryPrimitives.WriteUInt32BigEndian(span[4..], 1);
    BinaryPrimitives.WriteUInt64BigEndian(span[8..], 0);
    BinaryPrimitives.WriteUInt64BigEndian(span[16..], sectorCount);
    BinaryPrimitives.WriteUInt64BigEndian(span[24..], 0);
    BinaryPrimitives.WriteUInt32BigEndian(span[32..], SectorSize);
    BinaryPrimitives.WriteUInt32BigEndian(span[200..], 2);

    var chunk = span[MishHeaderSize..];
    BinaryPrimitives.WriteUInt32BigEndian(chunk, chunkType);
    BinaryPrimitives.WriteUInt64BigEndian(chunk[8..], 0);
    BinaryPrimitives.WriteUInt64BigEndian(chunk[16..], sectorCount);
    BinaryPrimitives.WriteUInt64BigEndian(chunk[24..], 0);
    BinaryPrimitives.WriteUInt64BigEndian(chunk[32..], storedLength);

    var terminator = span[(MishHeaderSize + MishBlockSize)..];
    BinaryPrimitives.WriteUInt32BigEndian(terminator, 0xFFFFFFFF);
    BinaryPrimitives.WriteUInt64BigEndian(terminator[8..], sectorCount);
    return result;
  }

  private static string BuildXml(string name, byte[] mish) => $"""
    <?xml version="1.0" encoding="UTF-8"?>
    <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
    <plist version="1.0">
    <dict>
      <key>resource-fork</key>
      <dict>
        <key>blkx</key>
        <array>
          <dict>
            <key>Name</key><string>{name}</string>
            <key>Data</key><data>{Convert.ToBase64String(mish)}</data>
          </dict>
        </array>
      </dict>
    </dict>
    </plist>
    """;

  private static byte[] BuildKoly(long dataForkOffset, long dataForkLength,
      long xmlOffset, long xmlLength, ulong sectorCount) {
    var result = new byte[512];
    var span = result.AsSpan();
    "koly"u8.CopyTo(span);
    BinaryPrimitives.WriteUInt32BigEndian(span[4..], 4);
    BinaryPrimitives.WriteUInt32BigEndian(span[8..], 512);
    BinaryPrimitives.WriteUInt32BigEndian(span[12..], 1);
    BinaryPrimitives.WriteUInt64BigEndian(span[24..], checked((ulong)dataForkOffset));
    BinaryPrimitives.WriteUInt64BigEndian(span[32..], checked((ulong)dataForkLength));
    BinaryPrimitives.WriteUInt32BigEndian(span[56..], 1);
    BinaryPrimitives.WriteUInt32BigEndian(span[60..], 1);
    BinaryPrimitives.WriteUInt64BigEndian(span[216..], checked((ulong)xmlOffset));
    BinaryPrimitives.WriteUInt64BigEndian(span[224..], checked((ulong)xmlLength));
    BinaryPrimitives.WriteUInt32BigEndian(span[488..], 1);
    BinaryPrimitives.WriteUInt64BigEndian(span[492..], sectorCount);
    return result;
  }
}
