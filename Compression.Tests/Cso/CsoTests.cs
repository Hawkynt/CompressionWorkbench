using System.Buffers.Binary;
using Compression.Registry;
using FileFormat.Cso;

namespace Compression.Tests.Cso;

[TestFixture]
public class CsoTests {
  private const int BlockSize = 2048;

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var descriptor = new CsoFormatDescriptor();
    Assert.That(descriptor.Id, Is.EqualTo("Cso"));
    Assert.That(descriptor.Extensions, Does.Contain(".cso"));
    Assert.That(descriptor.Extensions, Does.Contain(".ziso"));
    Assert.That(descriptor.Extensions, Does.Contain(".zso"));
    Assert.That(descriptor.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(descriptor.MagicSignatures, Has.Count.EqualTo(2));
  }

  [TestCase(null, "CISO", 1, TestName = "CreateRoundTrip_CsoV1")]
  [TestCase("zso", "ZISO", 1, TestName = "CreateRoundTrip_Zso")]
  [TestCase("cso2", "CISO", 2, TestName = "CreateRoundTrip_CsoV2")]
  [Category("RoundTrip")]
  public void CreateAndLogicalBlockExtraction_RoundTrips(string? method, string magic, byte version) {
    var payload = BuildPayload(BlockSize * 3 + 317);
    var image = CreateImage(payload, method);

    Assert.That(System.Text.Encoding.ASCII.GetString(image, 0, 4), Is.EqualTo(magic));
    Assert.That(image[20], Is.EqualTo(version));
    Assert.That(ReadLogicalPayload(image), Is.EqualTo(payload));
  }

  [Test, Category("Interoperability")]
  public void Zso_DecodesLibLz4KnownBlock() {
    // liblz4 raw-block oracle for 2048 zero bytes (no frame header / no stored size prefix).
    var encoded = Convert.FromHexString("1F000100FFFFFFFFFFFFFFEE500000000000");
    var image = BuildSingleBlockImage("ZISO", 1, encoded, methodFlag: false);

    var descriptor = new CsoFormatDescriptor();
    using var stream = new MemoryStream(image);
    var block = ((IArchiveFormatOperations)descriptor).ExtractEntryToMemory(stream, "blocks/block_00000.bin", null);
    Assert.That(block, Is.EqualTo(new byte[BlockSize]));

    stream.Position = 0;
    Assert.That(descriptor.List(stream, null).Single(e => e.Name == "blocks/block_00000.bin").Method,
      Is.EqualTo("LZ4"));
  }

  [Test, Category("Interoperability")]
  public void CsoV2_DecodesLibLz4KnownBlock() {
    var encoded = Convert.FromHexString("1F000100FFFFFFFFFFFFFFEE500000000000");
    var image = BuildSingleBlockImage("CISO", 2, encoded, methodFlag: true);

    var descriptor = new CsoFormatDescriptor();
    using var stream = new MemoryStream(image);
    var block = ((IArchiveFormatOperations)descriptor).ExtractEntryToMemory(stream, "blocks/block_00000.bin", null);
    Assert.That(block, Is.EqualTo(new byte[BlockSize]));

    stream.Position = 0;
    Assert.That(descriptor.List(stream, null).Single(e => e.Name == "blocks/block_00000.bin").Method,
      Is.EqualTo("LZ4"));
  }

  [Test, Category("Compatibility")]
  public void CsoVersionZero_IsAcceptedAsV1() {
    var payload = BuildPayload(BlockSize);
    var image = CreateImage(payload);
    image[20] = 0;

    Assert.That(ReadLogicalPayload(image), Is.EqualTo(payload));
  }

  [Test, Category("HappyPath")]
  public void List_ReportsLogicalAndPhysicalBlockSizes() {
    var payload = new byte[BlockSize + 137];
    var image = CreateImage(payload, "zso");
    var descriptor = new CsoFormatDescriptor();
    using var stream = new MemoryStream(image);

    var entries = descriptor.List(stream, null);
    var first = entries.Single(e => e.Name == "blocks/block_00000.bin");
    var last = entries.Single(e => e.Name == "blocks/block_00001.bin");
    Assert.That(first.OriginalSize, Is.EqualTo(BlockSize));
    Assert.That(last.OriginalSize, Is.EqualTo(137));
    Assert.That(first.CompressedSize, Is.LessThan(BlockSize));
    Assert.That(first.Method, Is.EqualTo("LZ4"));
  }

  [Test, Category("HappyPath")]
  public void Extract_WritesMetadataAndLogicalBlocks() {
    var payload = BuildPayload(BlockSize * 2);
    var image = CreateImage(payload, "cso2");
    var descriptor = new CsoFormatDescriptor();
    var directory = Path.Combine(Path.GetTempPath(), "cso_test_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try {
      using var stream = new MemoryStream(image);
      descriptor.Extract(stream, directory, null, ["metadata.ini", "block_00001.bin"]);

      var metadata = File.ReadAllText(Path.Combine(directory, "metadata.ini"));
      Assert.That(metadata, Does.Contain("variant=cso2"));
      Assert.That(metadata, Does.Contain("block_size=2048"));
      Assert.That(File.ReadAllBytes(Path.Combine(directory, "blocks/block_00001.bin")),
        Is.EqualTo(payload.AsSpan(BlockSize, BlockSize).ToArray()));
    } finally {
      Directory.Delete(directory, recursive: true);
    }
  }

  [Test, Category("ErrorHandling")]
  public void List_RejectsInvalidMagic() {
    var bogus = new byte[64];
    bogus[0] = (byte)'X';
    var descriptor = new CsoFormatDescriptor();
    using var stream = new MemoryStream(bogus);
    Assert.That(() => descriptor.List(stream, null), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("ErrorHandling")]
  public void List_RejectsNonMonotonicIndex() {
    var image = CreateImage(new byte[BlockSize * 2]);
    var first = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(24, 4));
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(28, 4), first - 1);

    var descriptor = new CsoFormatDescriptor();
    using var stream = new MemoryStream(image);
    Assert.That(() => descriptor.List(stream, null), Throws.InstanceOf<InvalidDataException>());
  }

  private static byte[] CreateImage(byte[] payload, string? method = null, int blockSize = BlockSize) {
    var creator = (IArchiveCreatable)new CsoFormatDescriptor();
    using var stream = new MemoryStream();
    var options = new FormatCreateOptions(method);
    options.FormatSpecific["BlockSize"] = blockSize.ToString(System.Globalization.CultureInfo.InvariantCulture);
    creator.Create(stream, [ArchiveInputInfo.InMemory("disc.iso", payload)], options);
    return stream.ToArray();
  }

  private static byte[] ReadLogicalPayload(byte[] image) {
    var descriptor = new CsoFormatDescriptor();
    using var stream = new MemoryStream(image, writable: false);
    var names = descriptor.List(stream, null)
      .Where(e => !e.IsDirectory && e.Name.StartsWith("blocks/block_", StringComparison.Ordinal))
      .OrderBy(e => e.Index)
      .Select(e => e.Name)
      .ToArray();
    using var output = new MemoryStream();
    foreach (var name in names) {
      stream.Position = 0;
      output.Write(((IArchiveFormatOperations)descriptor).ExtractEntryToMemory(stream, name, null));
    }
    return output.ToArray();
  }

  private static byte[] BuildSingleBlockImage(string magic, byte version, byte[] encoded, bool methodFlag) {
    using var stream = new MemoryStream();
    stream.Write(System.Text.Encoding.ASCII.GetBytes(magic));
    Span<byte> word = stackalloc byte[8];
    BinaryPrimitives.WriteUInt32LittleEndian(word[..4], 24);
    stream.Write(word[..4]);
    BinaryPrimitives.WriteUInt64LittleEndian(word, BlockSize);
    stream.Write(word);
    BinaryPrimitives.WriteUInt32LittleEndian(word[..4], BlockSize);
    stream.Write(word[..4]);
    stream.WriteByte(version);
    stream.WriteByte(0);
    stream.WriteByte(0);
    stream.WriteByte(0);

    const uint dataOffset = 32;
    var first = dataOffset | (methodFlag ? 0x8000_0000u : 0u);
    BinaryPrimitives.WriteUInt32LittleEndian(word[..4], first);
    stream.Write(word[..4]);
    BinaryPrimitives.WriteUInt32LittleEndian(word[..4], dataOffset + checked((uint)encoded.Length));
    stream.Write(word[..4]);
    stream.Write(encoded);
    return stream.ToArray();
  }

  private static byte[] BuildPayload(int length) {
    var payload = new byte[length];
    for (var i = 0; i < payload.Length; ++i)
      payload[i] = (byte)((i * 17 + i / 31) & 0xFF);
    return payload;
  }
}
