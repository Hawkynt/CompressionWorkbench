using System.Buffers.Binary;
using FileFormat.Lzfse;

namespace Compression.Tests.Lzfse;

[TestFixture]
public sealed class LzfseHistoryTests {
  [Test, Category("HappyPath"), Category("Interoperability")]
  public void Bvx1Match_CanReferencePreviousStreamBlock() {
    var stream = BuildCrossBlockStream();
    using var input = new MemoryStream(stream, writable: false);
    using var output = new MemoryStream();

    LzfseStream.Decompress(input, output);

    Assert.That(output.ToArray(), Is.EqualTo("ZZZZZ"u8.ToArray()));
  }

  private static byte[] BuildCrossBlockStream() {
    using var stream = new MemoryStream();

    // One raw byte establishes history before the entropy-coded block.
    stream.Write("bvx-"u8);
    Span<byte> length = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(length, 1);
    stream.Write(length);
    stream.WriteByte((byte)'Z');

    const int headerSize = 772;
    var header = new byte[headerSize];
    BinaryPrimitives.WriteUInt32LittleEndian(header, 0x31787662); // bvx1
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 4); // output bytes
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), 8); // payload bytes
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), 0); // literals
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), 1); // one L/M/D tuple
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), 0);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), 8);
    BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28), 0);
    BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(40), 0);

    var offset = 50;
    var lFreq = new ushort[20];
    lFreq[0] = 64; // L = 0
    var mFreq = new ushort[20];
    mFreq[4] = 64; // M = 4
    var dFreq = new ushort[64];
    dFreq[1] = 256; // D = 1
    WriteFrequencies(header, ref offset, lFreq);
    WriteFrequencies(header, ref offset, mFreq);
    WriteFrequencies(header, ref offset, dFreq);
    WriteFrequencies(header, ref offset, new ushort[256]); // no literal symbols needed
    Assert.That(offset, Is.EqualTo(770));

    stream.Write(header);
    stream.Write(new byte[8]); // L/M/D reverse-stream padding; all tables consume zero bits
    stream.Write("bvx$"u8);
    return stream.ToArray();
  }

  private static void WriteFrequencies(Span<byte> header, ref int offset, ReadOnlySpan<ushort> frequencies) {
    foreach (var frequency in frequencies) {
      BinaryPrimitives.WriteUInt16LittleEndian(header[offset..], frequency);
      offset += 2;
    }
  }
}
