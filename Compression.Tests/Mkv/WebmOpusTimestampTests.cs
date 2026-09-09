#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;
using FileFormat.Matroska;

namespace Compression.Tests.Mkv;

[TestFixture]
public sealed class WebmOpusTimestampTests {
  private const ulong IdSegment = 0x18538067;
  private const ulong IdCluster = 0x1F43B675;
  private const ulong IdClusterTimestamp = 0xE7;
  private const ulong IdSimpleBlock = 0xA3;
  private const long TimestampScaleNanoseconds = 1_000_000;
  private const long CodecDelayNanoseconds = 6_500_000;

  [Test]
  public void OpusMux_OffsetsRawTimestampSoCodecDelayDoesNotProduceNegativePresentationTime() {
    var descriptor = new MkvFormatDescriptor();
    var stream = new AudioEncodedStream(
      new AudioStreamFormat("opus", 48_000, 2),
      [new AudioPacket([0xF8, 0x11], 960)],
      MakeOpusHead(preSkip: 312));

    using var output = new MemoryStream();
    descriptor.Mux(output, stream, new FormatCreateOptions());

    var rawTimestampTicks = ReadFirstBlockTimestamp(output.ToArray());
    var presentationTimestampNanoseconds = checked(rawTimestampTicks * TimestampScaleNanoseconds - CodecDelayNanoseconds);

    Assert.Multiple(() => {
      Assert.That(rawTimestampTicks, Is.EqualTo(7));
      Assert.That(presentationTimestampNanoseconds, Is.GreaterThanOrEqualTo(0));
      Assert.That(presentationTimestampNanoseconds, Is.LessThan(TimestampScaleNanoseconds));
    });
  }

  private static byte[] MakeOpusHead(ushort preSkip) {
    var result = new byte[19];
    "OpusHead"u8.CopyTo(result);
    result[8] = 1;
    result[9] = 2;
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(10, 2), preSkip);
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12, 4), 48_000);
    return result;
  }

  private static long ReadFirstBlockTimestamp(byte[] file) {
    var ebml = new EbmlReader(file);
    var pos = 0L;
    while (pos < file.Length) {
      var top = ebml.Read(ref pos);
      if (top is null) break;
      if (top.Value.Id != IdSegment) continue;

      foreach (var child in ebml.Children(top.Value)) {
        if (child.Id != IdCluster) continue;

        long? clusterTimestamp = null;
        short? blockTimestamp = null;
        foreach (var field in ebml.Children(child)) {
          if (field.Id == IdClusterTimestamp) {
            clusterTimestamp = checked((long)ebml.ReadUnsigned(field));
            continue;
          }

          if (field.Id != IdSimpleBlock) continue;
          var block = ebml.Body(field);
          Assert.That(block.Length, Is.GreaterThanOrEqualTo(4));
          Assert.That(block[0], Is.EqualTo(0x81));
          blockTimestamp = BinaryPrimitives.ReadInt16BigEndian(block.Slice(1, 2));
        }

        if (clusterTimestamp is not null && blockTimestamp is not null)
          return checked(clusterTimestamp.Value + blockTimestamp.Value);
      }
    }

    throw new AssertionException("First WebM block timestamp not found.");
  }
}
