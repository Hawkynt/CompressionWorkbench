using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text;
using Compression.Core.Dictionary.Lzma;
using Compression.Core.ExecutableUnpacking;
using FileFormat.ExePackers;
using NUnit.Framework;

namespace Compression.Tests.ExePackers;

/// <summary>
/// Exercises the eXpressor container: a payload section holding raw LZMA1 streams laid
/// back to back, each opening with the SDK's five-byte properties header and closing with
/// an end-of-stream marker.
/// </summary>
[TestFixture]
public class ExpressorExecutablePackerHandlerTests {

  [Test, Category("HappyPath")]
  public void Unpack_DecompressesEveryStreamInTheChain() {
    var first = Payload(0x2000, 5);
    var second = Payload(0x400, 9);
    var packed = BuildExpressorPe([first, second]);

    var handler = new ExpressorExecutablePackerHandler();
    var detection = handler.Detect(packed);
    Assert.That(detection.IsMatch, Is.True);

    var result = handler.Unpack(handler.Parse(packed, detection), new UnpackOptions());

    Assert.Multiple(() => {
      Assert.That(result.Level, Is.EqualTo(ExecutableUnpackLevel.PayloadDecompressed));
      Assert.That(Artifact(result, "decompressed/stream_000.bin"), Is.EqualTo(first).AsCollection);
      Assert.That(Artifact(result, "decompressed/stream_001.bin"), Is.EqualTo(second).AsCollection);
      Assert.That(result.Diagnostics.Any(d => d.Code == ExecutableDiagnosticCode.TransformNotReversible), Is.True,
        "the residual x86 branch filter has to be reported, not silently ignored");
    });
  }

  [Test, Category("EdgeCase")]
  public void Unpack_StopsAtTheFirstBytesThatDoNotOpenAStream() {
    var only = Payload(0x800, 3);
    var packed = BuildExpressorPe([only], trailer: [0x5E, 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x11, 0x22, 0x33]);

    var handler = new ExpressorExecutablePackerHandler();
    var result = handler.Unpack(handler.Parse(packed, handler.Detect(packed)), new UnpackOptions());

    Assert.Multiple(() => {
      Assert.That(result.Level, Is.EqualTo(ExecutableUnpackLevel.PayloadDecompressed));
      Assert.That(Artifact(result, "decompressed/stream_000.bin"), Is.EqualTo(only).AsCollection);
      Assert.That(result.Artifacts.Count(a => a.Name.StartsWith("decompressed/", StringComparison.Ordinal)), Is.EqualTo(1));
    });
  }

  [Test, Category("EdgeCase")]
  public void Unpack_ReportsPayloadNotFound_WhenNoSectionReadsAsAChain() {
    var packed = BuildExpressorPe([], trailer: Enumerable.Repeat((byte)0xFF, 0x600).ToArray());

    var handler = new ExpressorExecutablePackerHandler();
    var result = handler.Unpack(handler.Parse(packed, handler.Detect(packed)), new UnpackOptions());

    Assert.Multiple(() => {
      Assert.That(result.Level, Is.LessThan(ExecutableUnpackLevel.PayloadDecompressed));
      Assert.That(result.Diagnostics.Any(d => d.Code == ExecutableDiagnosticCode.PayloadNotFound), Is.True);
    });
  }

  // ── Synthetic eXpressor image ───────────────────────────────────────────────

  private const int PeOffset = 0x80;
  private const int OptionalSize = 0xE0;
  private const int SectionTableOffset = PeOffset + 24 + OptionalSize;
  private const int HeadersSize = 0x200;

  private static byte[] Payload(int length, int seed) {
    var buffer = new byte[length];
    var state = (uint)seed | 1u;
    var pos = 0;
    while (pos < length) {
      state = state * 1103515245 + 12345;
      if (pos > 64 && (state >> 16 & 3) != 0) {
        var from = (int)((state >> 8) % (uint)pos);
        var run = Math.Min(4 + (int)(state >> 4 & 31), length - pos);
        for (var i = 0; i < run; ++i) buffer[pos + i] = buffer[(from + i) % pos];
        pos += run;
      } else
        buffer[pos++] = (byte)(state >> 24);
    }
    return buffer;
  }

  private static byte[] Artifact(UnpackResult result, string name) =>
    result.Artifacts.Single(a => a.Name == name).Data;

  private static byte[] BuildExpressorPe(byte[][] streams, byte[]? trailer = null) {
    using var chain = new MemoryStream();
    foreach (var stream in streams) {
      // eXpressor's own settings: lc = 4, lp = 0, pb = 2, 8 MiB dictionary.
      var encoder = new LzmaEncoder(dictionarySize: 1 << 23, lc: 4, lp: 0, pb: 2);
      chain.Write(encoder.Properties);
      encoder.Encode(chain, stream);
    }
    if (trailer is not null) chain.Write(trailer);
    var payload = chain.ToArray();

    var rawSize = Align((uint)Math.Max(payload.Length, 1), 0x200);
    var image = new byte[HeadersSize + rawSize];
    image[0] = (byte)'M'; image[1] = (byte)'Z';
    "EXpressor"u8.CopyTo(image.AsSpan(0x40));
    BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(0x3C), PeOffset);
    "PE\0\0"u8.CopyTo(image.AsSpan(PeOffset));
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(PeOffset + 4), 0x14C);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(PeOffset + 6), 1);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(PeOffset + 20), OptionalSize);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(PeOffset + 22), 0x010F);

    var optional = PeOffset + 24;
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(optional), 0x10B);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optional + 16), 0x1000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optional + 28), 0x00400000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optional + 32), 0x1000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optional + 36), 0x200);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optional + 56), 0x2000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optional + 60), HeadersSize);

    Encoding.ASCII.GetBytes(".ex_dat").CopyTo(image.AsSpan(SectionTableOffset, 8));
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(SectionTableOffset + 8), (uint)payload.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(SectionTableOffset + 12), 0x1000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(SectionTableOffset + 16), rawSize);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(SectionTableOffset + 20), HeadersSize);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(SectionTableOffset + 36), 0xE0000020);
    payload.CopyTo(image.AsSpan(HeadersSize));
    return image;
  }

  private static uint Align(uint value, uint alignment) => (value + alignment - 1) / alignment * alignment;
}
