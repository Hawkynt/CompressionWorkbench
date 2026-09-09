using System.Buffers.Binary;
using Compression.Registry;
using FileFormat.MacBinary;

namespace Compression.Tests.MacBinary;

[TestFixture]
public class MacBinaryTests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_DataForkOnly() {
    var data = "Hello, MacBinary!"u8.ToArray();
    using var encoded = new MemoryStream();
    MacBinaryWriter.Write(encoded, "Test File", data);

    encoded.Position = 0;
    var header = MacBinaryReader.ReadHeader(encoded);
    Assert.That(header.FileName, Is.EqualTo("Test File"));
    Assert.That(header.DataForkLength, Is.EqualTo(data.Length));
    Assert.That(header.ResourceForkLength, Is.EqualTo(0));

    encoded.Position = 0;
    var extracted = MacBinaryReader.ReadDataFork(encoded);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_WithResourceFork() {
    var data = "data fork content"u8.ToArray();
    var rsrc = "resource fork content"u8.ToArray();

    using var encoded = new MemoryStream();
    MacBinaryWriter.Write(encoded, "MacFile", data, rsrc);

    encoded.Position = 0;
    var header = MacBinaryReader.ReadHeader(encoded);
    Assert.That(header.DataForkLength, Is.EqualTo(data.Length));
    Assert.That(header.ResourceForkLength, Is.EqualTo(rsrc.Length));

    encoded.Position = 0;
    Assert.That(MacBinaryReader.ReadDataFork(encoded), Is.EqualTo(data));

    encoded.Position = 0;
    Assert.That(MacBinaryReader.ReadResourceFork(encoded), Is.EqualTo(rsrc));
  }

  [Test, Category("Spec")]
  public void MacBinaryIII_HasSignatureAndBackwardCompatibleMinimumVersion() {
    using var encoded = new MemoryStream();
    MacBinaryWriter.Write(encoded, "test", "test"u8.ToArray(), version: 130);
    var bytes = encoded.ToArray();

    Assert.Multiple(() => {
      Assert.That(bytes.AsSpan(102, 4).ToArray(), Is.EqualTo("mBIN"u8.ToArray()));
      Assert.That(bytes[122], Is.EqualTo(130), "byte 122 is the writer version");
      Assert.That(bytes[123], Is.EqualTo(129), "MacBinary III remains readable by MacBinary II");
    });
  }

  [Test, Category("Spec")]
  public void Writer_RejectsUnknownVersion() {
    using var encoded = new MemoryStream();
    Assert.That(
      () => MacBinaryWriter.Write(encoded, "test", [], version: 131),
      Throws.TypeOf<ArgumentOutOfRangeException>());
  }

  [Test, Category("HappyPath")]
  public void IsMacBinary_ValidFile_ReturnsTrue() {
    using var encoded = new MemoryStream();
    MacBinaryWriter.Write(encoded, "valid", "test"u8.ToArray());
    encoded.Position = 0;
    Assert.That(MacBinaryReader.IsMacBinary(encoded), Is.True);
  }

  [Test, Category("HappyPath")]
  public void IsMacBinary_InvalidFile_ReturnsFalse() {
    using var ms = new MemoryStream([0xFF, 0xFF, 0xFF, 0xFF]);
    Assert.That(MacBinaryReader.IsMacBinary(ms), Is.False);
  }

  [Test, Category("Spec"), Category("RoundTrip")]
  public void Optimizer_PreservesAllDeclaredPayloads_AndCanonicalizesPadding() {
    var data = "DATA"u8.ToArray();
    var resource = "RSRC!"u8.ToArray();
    var secondaryHeader = "SECONDARY"u8.ToArray();
    var comment = "comment"u8.ToArray();
    var original = BuildExtendedMacBinary(data, resource, secondaryHeader, comment, appendGarbage: true);

    using var input = new MemoryStream(original, writable: false);
    using var optimized = new MemoryStream();
    MacBinaryOptimizer.Optimize(input, optimized);
    var result = optimized.ToArray();

    var expectedLength = 128
      + RoundUp(secondaryHeader.Length)
      + RoundUp(data.Length)
      + RoundUp(resource.Length)
      + RoundUp(comment.Length);

    Assert.Multiple(() => {
      Assert.That(result.Length, Is.EqualTo(expectedLength), "bytes after the declared container are trimmed");
      Assert.That(result[122], Is.EqualTo(130));
      Assert.That(result[123], Is.EqualTo(129));
      Assert.That(result[108], Is.Zero, "MacBinary III reserved header bytes are canonicalized");
      Assert.That(result[126], Is.Zero);
    });

    using (var check = new MemoryStream(result, writable: false)) {
      Assert.That(MacBinaryReader.IsMacBinary(check), Is.True, "optimizer recomputes the header CRC");
      check.Position = 0;
      var header = MacBinaryReader.ReadHeader(check);
      Assert.Multiple(() => {
        Assert.That(header.SecondaryHeaderLength, Is.EqualTo(secondaryHeader.Length));
        Assert.That(header.GetInfoCommentLength, Is.EqualTo(comment.Length));
        Assert.That(header.MinimumVersion, Is.EqualTo(129));
      });

      check.Position = 0;
      Assert.That(MacBinaryReader.ReadDataFork(check), Is.EqualTo(data));
      check.Position = 0;
      Assert.That(MacBinaryReader.ReadResourceFork(check), Is.EqualTo(resource));
    }

    var secondaryOffset = 128;
    var dataOffset = secondaryOffset + RoundUp(secondaryHeader.Length);
    var resourceOffset = dataOffset + RoundUp(data.Length);
    var commentOffset = resourceOffset + RoundUp(resource.Length);
    Assert.Multiple(() => {
      Assert.That(result.AsSpan(secondaryOffset, secondaryHeader.Length).ToArray(), Is.EqualTo(secondaryHeader));
      Assert.That(result.AsSpan(commentOffset, comment.Length).ToArray(), Is.EqualTo(comment));
      AssertPaddingIsZero(result, secondaryOffset, secondaryHeader.Length);
      AssertPaddingIsZero(result, dataOffset, data.Length);
      AssertPaddingIsZero(result, resourceOffset, resource.Length);
      AssertPaddingIsZero(result, commentOffset, comment.Length);
    });
  }

  [Test, Category("Negative")]
  public void Optimizer_TruncatedFork_ThrowsInvalidData() {
    using var encoded = new MemoryStream();
    MacBinaryWriter.Write(encoded, "broken", "payload"u8.ToArray());
    var truncated = encoded.ToArray()[..^1];

    using var input = new MemoryStream(truncated, writable: false);
    using var output = new MemoryStream();
    Assert.That(() => MacBinaryOptimizer.Optimize(input, output), Throws.TypeOf<InvalidDataException>());
  }

  [Test, Category("Spec")]
  public void Descriptor_AdvertisesOptimizer() {
    var descriptor = new MacBinaryFormatDescriptor();
    Assert.Multiple(() => {
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.SupportsOptimize), Is.True);
      Assert.That(descriptor.Methods.Single().SupportsOptimize, Is.True);
    });
  }

  private static byte[] BuildExtendedMacBinary(
      byte[] data, byte[] resource, byte[] secondaryHeader, byte[] comment, bool appendGarbage) {
    using var baseline = new MemoryStream();
    MacBinaryWriter.Write(baseline, "extended", data, resource, version: 130);
    var header = baseline.ToArray()[..128];

    BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(99, 2), checked((ushort)comment.Length));
    BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(120, 2), checked((ushort)secondaryHeader.Length));

    // Seed non-canonical but semantically ignored bytes. They are covered by the CRC, so make the
    // source internally valid before asking the optimizer to canonicalize them.
    header[20] = 0xA5; // unused tail of the fixed filename field
    header[108] = 0xA5; // MacBinary III reserved byte
    header[126] = 0xA5; // reserved, outside the CRC range
    BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(124, 2), ComputeMacBinaryCrc(header.AsSpan(0, 124)));

    using var file = new MemoryStream();
    file.Write(header);
    WriteSectionWithDirtyPadding(file, secondaryHeader);
    WriteSectionWithDirtyPadding(file, data);
    WriteSectionWithDirtyPadding(file, resource);
    WriteSectionWithDirtyPadding(file, comment);
    if (appendGarbage)
      file.Write("transport garbage"u8);
    return file.ToArray();
  }

  private static void WriteSectionWithDirtyPadding(Stream output, ReadOnlySpan<byte> payload) {
    output.Write(payload);
    var padding = RoundUp(payload.Length) - payload.Length;
    for (var i = 0; i < padding; ++i)
      output.WriteByte(0xA5);
  }

  private static int RoundUp(int length) => (length + 127) & ~127;

  private static void AssertPaddingIsZero(byte[] data, int sectionOffset, int payloadLength) {
    var paddedLength = RoundUp(payloadLength);
    var padding = data.AsSpan(sectionOffset + payloadLength, paddedLength - payloadLength);
    Assert.That(padding.ToArray(), Is.All.Zero);
  }

  private static ushort ComputeMacBinaryCrc(ReadOnlySpan<byte> data) {
    ushort crc = 0;
    foreach (var value in data) {
      crc ^= (ushort)(value << 8);
      for (var bit = 0; bit < 8; ++bit)
        crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
    }
    return crc;
  }
}
