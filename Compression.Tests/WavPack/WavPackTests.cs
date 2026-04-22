using System.Buffers.Binary;
using FileFormat.WavPack;

namespace Compression.Tests.WavPack;

[TestFixture]
public class WavPackTests {

  /// <summary>
  /// Builds a synthetic WavPack block: 32-byte header + <paramref name="bodyBytes"/>
  /// worth of payload.
  /// </summary>
  private static byte[] MakeBlock(uint totalSamples, uint blockSamples, uint flags, uint blockIndex, int bodyBytes) {
    var blockSize = 32 + bodyBytes;
    var buf = new byte[blockSize];
    // "wvpk" magic
    buf[0] = 0x77; buf[1] = 0x76; buf[2] = 0x70; buf[3] = 0x6B;
    // ckSize = blockSize - 8
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), (uint)(blockSize - 8));
    // version
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(8), 0x0410);
    // block_index_u8 / total_samples_u8 high bytes
    buf[10] = 0; buf[11] = 0;
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(12), totalSamples);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(16), blockIndex);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(20), blockSamples);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(24), flags);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(28), 0xDEADBEEF); // crc placeholder
    for (var i = 0; i < bodyBytes; ++i) buf[32 + i] = (byte)(i & 0xFF);
    return buf;
  }

  private static byte[] BuildMultiBlock(int count, uint blockSamples, uint totalSamples, uint flags) {
    using var ms = new MemoryStream();
    for (var i = 0; i < count; ++i)
      ms.Write(MakeBlock(totalSamples, blockSamples, flags, (uint)i * blockSamples, bodyBytes: 16 + i));
    return ms.ToArray();
  }

  // 16-bit stereo @ 44100 flags:
  //   bits[0..1]=01 (16-bit), bit 2 = 0 (stereo), rate idx 9 (44100) at bits[23..26]
  private const uint Flags16Stereo44k = 0x1u | (9u << 23);

  [Test, Category("HappyPath")]
  public void ParsesSingleBlock() {
    var file = BuildMultiBlock(count: 1, blockSamples: 1024, totalSamples: 1024, Flags16Stereo44k);
    using var ms = new MemoryStream(file);
    var entries = new WavPackFormatDescriptor().List(ms, null);
    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Does.Contain("block_0000.wv"));
    Assert.That(names, Does.Contain("metadata.ini"));
  }

  [Test, Category("HappyPath")]
  public void ParsesMultipleBlocks() {
    var file = BuildMultiBlock(count: 3, blockSamples: 1024, totalSamples: 3072, Flags16Stereo44k);
    using var ms = new MemoryStream(file);
    var entries = new WavPackFormatDescriptor().List(ms, null);
    var blockNames = entries.Where(e => e.Name.StartsWith("block_", StringComparison.Ordinal)).Select(e => e.Name).ToList();
    Assert.That(blockNames, Has.Count.EqualTo(3));
    Assert.That(blockNames[0], Is.EqualTo("block_0000.wv"));
    Assert.That(blockNames[1], Is.EqualTo("block_0001.wv"));
    Assert.That(blockNames[2], Is.EqualTo("block_0002.wv"));
  }

  [Test, Category("HappyPath")]
  public void Extract_WritesBlocksToDisk() {
    var file = BuildMultiBlock(count: 2, blockSamples: 1024, totalSamples: 2048, Flags16Stereo44k);

    var tmp = Path.Combine(Path.GetTempPath(), $"wvpk-test-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(file);
      new WavPackFormatDescriptor().Extract(ms, tmp, null, null);

      Assert.That(File.Exists(Path.Combine(tmp, "block_0000.wv")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "block_0001.wv")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "metadata.ini")), Is.True);

      var ini = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(ini, Does.Contain("sample_rate=44100"));
      Assert.That(ini, Does.Contain("bits_per_sample=16"));
      Assert.That(ini, Does.Contain("total_samples=2048"));

      // Block 0 should start with the wvpk magic.
      var b0 = File.ReadAllBytes(Path.Combine(tmp, "block_0000.wv"));
      Assert.That(b0[0], Is.EqualTo(0x77));
      Assert.That(b0[1], Is.EqualTo(0x76));
      Assert.That(b0[2], Is.EqualTo(0x70));
      Assert.That(b0[3], Is.EqualTo(0x6B));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new WavPackFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("WavPack"));
    Assert.That(d.DefaultExtension, Is.EqualTo(".wv"));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo("wvpk"u8.ToArray()));
  }

  [Test, Category("EdgeCase")]
  public void Empty_Stream_ReturnsNoEntries() {
    using var ms = new MemoryStream();
    var entries = new WavPackFormatDescriptor().List(ms, null);
    Assert.That(entries, Is.Empty);
  }

  [Test, Category("EdgeCase")]
  public void ExtractEntry_UnknownName_Throws() {
    var file = BuildMultiBlock(1, 1024, 1024, Flags16Stereo44k);
    using var ms = new MemoryStream(file);
    using var output = new MemoryStream();
    Assert.Throws<FileNotFoundException>(() =>
      new WavPackFormatDescriptor().ExtractEntry(ms, "nope.bin", output, null));
  }
}
