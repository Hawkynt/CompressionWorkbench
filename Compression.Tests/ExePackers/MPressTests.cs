#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.Dictionary.Lzma;
using Compression.Core.ExecutableUnpacking;
using Compression.Lib;
using FileFormat.ExePackers;

namespace Compression.Tests.ExePackers;

/// <summary>
/// MPRESS (MATCODE Software) unpacking tests. The fixtures rebuild what the packer writes:
/// a <c>.MPRESS1</c> section holding the page count, the packed size, the two lc/lp/pb
/// parameter bytes and a bare LZMA1 stream, over an image whose E8/E9 operands were turned
/// into addresses. Both the container layout and the operand rule were read off the loader
/// stub of packed samples, since MPRESS documents neither.
/// </summary>
[TestFixture]
public class MPressTests {
  private const int PeOffset = 0x80;
  private const int OptionalHeaderSize = 0xE0;
  private const int SectionTableOffset = PeOffset + 24 + OptionalHeaderSize;
  private const int SectionDataOffset = 0x400;
  private const int PageSize = 0x1000;

  [Test, Category("HappyPath")]
  public void Unpack_DecompressesThePayloadAndReversesTheCallTransform() {
    var original = BuildImageWithCalls();
    var image = BuildPackedPe(BuildContainer(original, lc: 4, lp: 0, pb: 2));
    var handler = new MPressExecutablePackerHandler();

    var detection = handler.Detect(image);
    Assert.That(detection.IsMatch, Is.True);

    var result = handler.Unpack(handler.Parse(image, detection), new UnpackOptions());

    Assert.Multiple(() => {
      Assert.That(result.Level, Is.EqualTo(ExecutableUnpackLevel.PayloadDecompressed));
      Assert.That(result.Capabilities.HasFlag(ExecutableUnpackCapabilities.CanDecompressPayload), Is.True);
      Assert.That(result.Artifacts.Single(a => a.Name == "unpacked_image.bin").Data, Is.EqualTo(original).AsCollection);
    });
  }

  [Test, Category("HappyPath")]
  [TestCase(0, 0, 0)]
  [TestCase(0, 0, 1)]
  [TestCase(5, 0, 2)]
  [TestCase(8, 4, 4)]
  public void Unpack_HonoursTheParameterBytes(int lc, int lp, int pb) {
    // MPRESS picks the lc/lp/pb that compress a given image best, and lc alone can exceed
    // what the packed properties byte of the ordinary LZMA container can express.
    var original = BuildImageWithCalls();
    var image = BuildPackedPe(BuildContainer(original, lc, lp, pb));
    var handler = new MPressExecutablePackerHandler();

    var result = handler.Unpack(handler.Parse(image, handler.Detect(image)), new UnpackOptions());

    Assert.Multiple(() => {
      Assert.That(result.Level, Is.EqualTo(ExecutableUnpackLevel.PayloadDecompressed));
      Assert.That(result.Artifacts.Single(a => a.Name == "unpacked_image.bin").Data, Is.EqualTo(original).AsCollection);
    });
  }

  [Test, Category("EdgeCase")]
  public void Unpack_ParameterBytesOutsideTheLzmaRanges_StaysAtPayloadLocated() {
    // MPRESS 1.x writes the same page count and packed size but no parameter bytes, because
    // it packs with another codec. Its stream must not be mistaken for LZMA.
    var container = BuildContainer(BuildImageWithCalls(), lc: 4, lp: 0, pb: 2);
    container[6] = 0x8B; // pb = 8, lp = 11 — impossible for LZMA
    var image = BuildPackedPe(container);
    var handler = new MPressExecutablePackerHandler();

    var result = handler.Unpack(handler.Parse(image, handler.Detect(image)), new UnpackOptions());

    Assert.Multiple(() => {
      Assert.That(result.Level, Is.EqualTo(ExecutableUnpackLevel.PayloadLocated));
      Assert.That(result.Artifacts.Any(a => a.Name == "unpacked_image.bin"), Is.False);
      Assert.That(result.Diagnostics.Any(d => d.Code == ExecutableDiagnosticCode.UnsupportedPackerVersion && d.IsError), Is.True);
    });
  }

  [Test, Category("EdgeCase")]
  public void Unpack_PageCountDisagreeingWithTheSection_StaysAtPayloadLocated() {
    var container = BuildContainer(BuildImageWithCalls(), lc: 4, lp: 0, pb: 2);
    BinaryPrimitives.WriteUInt16LittleEndian(container, 0x40); // claims 0x40000 bytes
    var image = BuildPackedPe(container);
    var handler = new MPressExecutablePackerHandler();

    var result = handler.Unpack(handler.Parse(image, handler.Detect(image)), new UnpackOptions());

    Assert.That(result.Level, Is.EqualTo(ExecutableUnpackLevel.PayloadLocated));
  }

  [Test, Category("EdgeCase")]
  public void Unpack_CorruptStream_ReportsDecompressionFailedInsteadOfThrowing() {
    var container = BuildContainer(BuildImageWithCalls(), lc: 4, lp: 0, pb: 2);
    for (var i = 16; i < container.Length; ++i)
      container[i] ^= 0xFF;
    var image = BuildPackedPe(container);
    var handler = new MPressExecutablePackerHandler();

    var result = handler.Unpack(handler.Parse(image, handler.Detect(image)), new UnpackOptions());

    Assert.Multiple(() => {
      Assert.That(result.Level, Is.EqualTo(ExecutableUnpackLevel.PayloadLocated));
      Assert.That(result.Diagnostics.Any(d => d.Code == ExecutableDiagnosticCode.DecompressionFailed), Is.True);
    });
  }

  [Test, Category("HappyPath")]
  public void MPress_IsRegisteredExactlyOnce() {
    Assert.That(ExecutablePackerHandlers.All.Count(h => h.Id == "mpress"), Is.EqualTo(1));
  }

  /// <summary>
  /// Three pages of low-entropy filler — no stray E8/E9 bytes — with call and jump
  /// instructions planted so that every branch of the operand rule is exercised: a target
  /// inside the scan limit, a negative displacement the limit lifts back, a displacement
  /// too far forward to encode as an address, and one the packer leaves untouched.
  /// </summary>
  private static byte[] BuildImageWithCalls() {
    var image = new byte[3 * PageSize];
    for (var i = 0; i < image.Length; ++i)
      image[i] = (byte)(i * 7 % 0xE0);

    PlantCall(image, 0x100, 0xE8, 0x40);
    PlantCall(image, 0x200, 0xE9, -0x30);
    PlantCall(image, 0x300, 0xE8, 0x1F00);
    PlantCall(image, 0x400, 0xE8, 0x2500);
    return image;
  }

  private static void PlantCall(byte[] image, int offset, byte opcode, int displacement) {
    image[offset] = opcode;
    BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(offset + 1), displacement);
  }

  /// <summary>
  /// Applies the packing-side operand transform: the inverse of what the loader stub does,
  /// which is what the handler has to undo.
  /// </summary>
  private static byte[] ApplyCallTransform(byte[] image) {
    var transformed = (byte[])image.Clone();
    var limit = transformed.Length - PageSize;
    for (var i = 0; i < limit;) {
      if ((transformed[i] & 0xFE) != 0xE8) {
        ++i;
        continue;
      }

      var operand = i + 1;
      var displacement = BinaryPrimitives.ReadInt32LittleEndian(transformed.AsSpan(operand));
      var target = displacement + operand;
      if (target >= 0 && target < limit)
        BinaryPrimitives.WriteInt32LittleEndian(transformed.AsSpan(operand), target);
      else if (displacement - limit < 0 && displacement - limit + operand >= 0)
        BinaryPrimitives.WriteInt32LittleEndian(transformed.AsSpan(operand), displacement - limit);

      i = operand + 4;
    }
    return transformed;
  }

  private static byte[] BuildContainer(byte[] image, int lc, int lp, int pb) {
    var encoder = new LzmaEncoder(dictionarySize: 1 << 16, lc: lc, lp: lp, pb: pb);
    using var packed = new MemoryStream();
    encoder.Encode(packed, ApplyCallTransform(image));
    var stream = packed.ToArray();

    var container = new byte[8 + stream.Length];
    BinaryPrimitives.WriteUInt16LittleEndian(container, (ushort)(image.Length >> 12));
    BinaryPrimitives.WriteUInt32LittleEndian(container.AsSpan(2), (uint)(stream.Length + 2));
    container[6] = (byte)((pb << 4) | lp);
    container[7] = (byte)lc;
    stream.CopyTo(container.AsSpan(8));
    return container;
  }

  /// <summary>
  /// Wraps the container in the two-section PE skeleton MPRESS produces: the packed image
  /// in <c>.MPRESS1</c>, whose virtual size is the unpacked size, and a placeholder
  /// <c>.MPRESS2</c> for the loader.
  /// </summary>
  private static byte[] BuildPackedPe(byte[] container) {
    var unpackedSize = (uint)BinaryPrimitives.ReadUInt16LittleEndian(container) << 12;
    var rawSize = (container.Length + 0x1FF) & ~0x1FF;
    var image = new byte[SectionDataOffset + rawSize + 0x200];

    image[0] = (byte)'M';
    image[1] = (byte)'Z';
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x3C), PeOffset);
    image[PeOffset] = (byte)'P';
    image[PeOffset + 1] = (byte)'E';
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(PeOffset + 4), 0x014C); // i386
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(PeOffset + 4 + 2), 2); // sections
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(PeOffset + 4 + 16), OptionalHeaderSize);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(PeOffset + 24), 0x010B); // PE32

    WriteSection(image, 0, ".MPRESS1", PageSize, unpackedSize, (uint)SectionDataOffset, (uint)rawSize);
    WriteSection(image, 1, ".MPRESS2", PageSize + unpackedSize, 0x200, (uint)(SectionDataOffset + rawSize), 0x200);

    container.CopyTo(image.AsSpan(SectionDataOffset));
    return image;
  }

  private static void WriteSection(byte[] image, int index, string name, uint virtualAddress, uint virtualSize, uint rawOffset, uint rawSize) {
    var offset = SectionTableOffset + index * 40;
    Encoding.ASCII.GetBytes(name).CopyTo(image.AsSpan(offset));
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset + 8), virtualSize);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset + 12), virtualAddress);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset + 16), rawSize);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset + 20), rawOffset);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset + 36), 0xE00000E0);
  }
}
