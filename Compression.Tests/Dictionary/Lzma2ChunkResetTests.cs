using System.Reflection;
using System.Security.Cryptography;
using Compression.Core.Dictionary.Lzma;
using Compression.Core.Streams;
using FileFormat.Xz;

namespace Compression.Tests.Dictionary;

/// <summary>
/// The LZMA2 chunk-reset rules around uncompressed (copy) chunks, as the LZMA SDK's Lzma2Dec.c
/// (<c>needInitLevel</c>) and liblzma's lzma2_decoder.c (<c>need_properties</c>) define them: a
/// dictionary-resetting copy chunk (0x01) obliges the next LZMA chunk to carry properties, a
/// continuing copy chunk (0x02) obliges nothing.
/// </summary>
[TestFixture]
public class Lzma2ChunkResetTests {
  [Test]
  [Category("ThirdPartyProof")]
  public void Given_7ZipStreamWithContinuingCopyChunk_When_Decoded_Then_TheNextUnresetLzmaChunkContinuesTheState() {
    using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("DictionaryVectors.lzma2-copy-then-continue.xz")!;
    using var xz = new XzStream(resource, CompressionStreamMode.Decompress);
    using var output = new MemoryStream();
    xz.CopyTo(output);

    Assert.That(output.Length, Is.EqualTo(108_000));
    Assert.That(Convert.ToHexStringLower(SHA256.HashData(output.ToArray())), Is.EqualTo("73904a71b348a61232473c72824c366c58d2dfdf898849040453d3478a80a002"));
  }

  [Test]
  [Category("EdgeCase")]
  public void Given_DictionaryResettingCopyChunk_When_AnLzmaChunkWithoutPropertiesFollows_Then_DecodingIsRefused() {
    // 0x01: copy 1 byte with dictionary reset; 0x80: LZMA chunk with reset level 0; 0x00: end.
    byte[] stream = [0x01, 0x00, 0x00, (byte)'A', 0x80, 0x00, 0x00, 0x00, 0x04, 0, 0, 0, 0, 0, 0x00];

    Assert.That(() => new Lzma2Decoder(new MemoryStream(stream), 1 << 16).Decode(), Throws.InstanceOf<InvalidDataException>());
  }

  [Test]
  [Category("EdgeCase")]
  public void Given_ContinuingCopyChunkFirst_When_Decoded_Then_TheMissingDictionaryResetIsRefused() {
    byte[] stream = [0x02, 0x00, 0x00, (byte)'A', 0x00];

    Assert.That(() => new Lzma2Decoder(new MemoryStream(stream), 1 << 16).Decode(), Throws.InstanceOf<InvalidDataException>());
  }

  [Test]
  [Category("EdgeCase")]
  public void Given_OnlyCopyChunks_When_Decoded_Then_TheBytesComeBackVerbatim() {
    byte[] stream = [0x01, 0x00, 0x01, (byte)'A', (byte)'B', 0x02, 0x00, 0x00, (byte)'C', 0x00];

    Assert.That(new Lzma2Decoder(new MemoryStream(stream), 1 << 16).Decode(), Is.EqualTo("ABC"u8.ToArray()));
  }
}
