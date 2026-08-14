using System.Buffers.Binary;
using System.Text;
using Compression.Core.Dictionary.Nrv2b;

namespace Compression.Tests.BuildingBlocks;

/// <summary>
/// Checks the NRV2B stream against the format itself rather than against our own
/// decoder.
/// </summary>
/// <remarks>
/// <para>Round-trip tests cannot catch an encoder and a decoder that drift
/// together: for a long time both spoke a private dialect that agreed with
/// itself and with nothing else, so every round-trip passed while no
/// UPX-produced stream would decode. These tests read what we emit with a
/// second decoder written straight from the format description, so the two can
/// only agree by both being right.</para>
///
/// <para>The format: bits are packed into little-endian words and consumed most
/// significant first. A set control bit means a literal byte follows. A clear
/// one starts a match — an offset written as a variable-length integer (start
/// at one, then repeat a value bit followed by a continue bit, stopping on a set
/// continue bit), where the value 2 reuses the previous offset and anything else
/// takes an inline byte as its low eight bits. Then two bits of length, most
/// significant first, with zero escaping to the same variable-length integer
/// biased by two. The decoder copies one more byte than the length says, and one
/// more again when the offset exceeds 0xD00.</para>
/// </remarks>
[TestFixture]
public class Nrv2bWireFormatTests {

  private static readonly Nrv2bBuildingBlock Bb = new();

  /// <summary>A decoder written only from the format description above.</summary>
  private static byte[] ReferenceDecode(byte[] stream, int outputSize) {
    var output = new byte[outputSize];
    var pos = 0;
    uint word = 0;
    var bitsLeft = 0;
    var op = 0;
    uint lastOffset = 1;

    int Bit() {
      if (bitsLeft == 0) {
        word = pos + 4 <= stream.Length ? BinaryPrimitives.ReadUInt32LittleEndian(stream.AsSpan(pos)) : 0u;
        pos += 4;
        bitsLeft = 32;
      }

      var b = (int)((word >> 31) & 1);
      word <<= 1;
      --bitsLeft;
      return b;
    }

    byte Byte() => pos < stream.Length ? stream[pos++] : (byte)0;

    uint VarInt() {
      uint v = 1;
      do
        v = v * 2 + (uint)Bit();
      while (Bit() == 0);
      return v;
    }

    while (op < outputSize) {
      while (Bit() == 1) {
        output[op++] = Byte();
        if (op >= outputSize) return output;
      }

      var coded = VarInt();
      uint offset;
      if (coded == 2)
        offset = lastOffset;
      else {
        var raw = (coded - 3) * 256 + Byte();
        if (raw == 0xFFFFFFFFu) break;
        offset = raw + 1;
        lastOffset = offset;
      }

      var length = (uint)((Bit() << 1) | Bit());
      if (length == 0) length = VarInt() + 2;
      if (offset > 0xD00) ++length;

      Assert.That(offset, Is.LessThanOrEqualTo((uint)op), "match offset reaches before the start of the output");
      var from = op - (int)offset;
      for (var i = 0; i <= length && op < outputSize; ++i)
        output[op++] = output[from + i];
    }

    return output;
  }

  private static void AssertReferenceReadsOurs(byte[] data) {
    var compressed = Bb.Compress(data);
    var declared = BinaryPrimitives.ReadInt32LittleEndian(compressed);
    Assert.That(declared, Is.EqualTo(data.Length), "the size header must state the payload length");

    var decoded = ReferenceDecode(compressed[4..], data.Length);
    Assert.That(decoded, Is.EqualTo(data), "a decoder written from the format could not read our stream");
  }

  [Test, Category("HappyPath")]
  public void AStreamWeWrite_IsReadableByADecoderWrittenFromTheFormat() {
    AssertReferenceReadsOurs(Encoding.ASCII.GetBytes(
      "the same sentence twice; the same sentence twice; and then something else entirely."));
  }

  [Test, Category("EdgeCase")]
  public void ARunOfOneByte_IsReadableByTheReferenceDecoder() {
    // Overlapping copies at offset 1 are the shortest match the format allows.
    AssertReferenceReadsOurs(new byte[512]);
  }

  [Test, Category("EdgeCase")]
  public void AMatchBeyondTheLargeOffsetThreshold_IsReadableByTheReferenceDecoder() {
    // Past 0xD00 the decoder adds a byte to every match, which the encoder must
    // subtract; getting that backwards only shows up at this distance.
    var rng = new Random(7);
    var data = new byte[0x2000];
    rng.NextBytes(data);
    var tail = new byte[data.Length + 64];
    data.CopyTo(tail, 0);
    data.AsSpan(0, 64).CopyTo(tail.AsSpan(data.Length));
    AssertReferenceReadsOurs(tail);
  }

  [Test, Category("EdgeCase")]
  public void EveryMatchLengthFromTwoUpwards_IsReadableByTheReferenceDecoder() {
    // The length coding is immediate for 1..3 and escapes above that; a gap at
    // any single length would show here and nowhere else.
    for (var run = 2; run <= 40; ++run) {
      var data = new byte[run * 2 + 8];
      for (var i = 0; i < data.Length; ++i) data[i] = (byte)(i % run);
      AssertReferenceReadsOurs(data);
    }
  }

  [Test, Category("RoundTrip")]
  public void RandomPayloads_AreReadableByTheReferenceDecoder() {
    var rng = new Random(1234);
    for (var trial = 0; trial < 12; ++trial) {
      var data = new byte[rng.Next(1, 6000)];
      rng.NextBytes(data);
      // Repeat a slice so the stream contains matches as well as literals.
      if (data.Length > 300) data.AsSpan(0, 128).CopyTo(data.AsSpan(data.Length - 128));
      AssertReferenceReadsOurs(data);
    }
  }
}
