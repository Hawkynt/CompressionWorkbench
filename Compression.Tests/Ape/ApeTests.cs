using System.Buffers.Binary;
using FileFormat.Ape;

namespace Compression.Tests.Ape;

[TestFixture]
public class ApeTests {

  // ── Synthetic modern (3.98+) APE file builder ──────────────────────────────

  private const int DescSize = 52;
  private const int HeaderSize = 24;

  private static byte[] BuildModernApe(
      ushort version,
      ushort compressionLevel,
      ushort formatFlags,
      uint blocksPerFrame,
      uint finalFrameBlocks,
      uint totalFrames,
      ushort bitsPerSample,
      ushort channels,
      uint sampleRate,
      byte[] wavHeader,
      byte[] seekTable,
      byte[] frames,
      byte[] terminating) {

    var descriptorBytes = (uint)DescSize;
    var headerBytes = (uint)HeaderSize;
    var seekTableBytes = (uint)seekTable.Length;
    var wavHeaderBytes = (uint)wavHeader.Length;
    var frameDataBytes = (uint)frames.Length;
    var terminatingBytes = (uint)terminating.Length;

    using var ms = new MemoryStream();

    // APE_DESCRIPTOR (52 bytes)
    ms.Write("MAC "u8);
    Span<byte> u16 = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16LittleEndian(u16, version); ms.Write(u16);
    ms.Write(new byte[2]); // padding
    Span<byte> u32 = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(u32, descriptorBytes); ms.Write(u32);
    BinaryPrimitives.WriteUInt32LittleEndian(u32, headerBytes); ms.Write(u32);
    BinaryPrimitives.WriteUInt32LittleEndian(u32, seekTableBytes); ms.Write(u32);
    BinaryPrimitives.WriteUInt32LittleEndian(u32, wavHeaderBytes); ms.Write(u32);
    BinaryPrimitives.WriteUInt32LittleEndian(u32, frameDataBytes); ms.Write(u32);
    BinaryPrimitives.WriteUInt32LittleEndian(u32, 0); ms.Write(u32); // frameDataBytesHigh
    BinaryPrimitives.WriteUInt32LittleEndian(u32, terminatingBytes); ms.Write(u32);
    ms.Write(new byte[16]); // md5

    // APE_HEADER (24 bytes)
    BinaryPrimitives.WriteUInt16LittleEndian(u16, compressionLevel); ms.Write(u16);
    BinaryPrimitives.WriteUInt16LittleEndian(u16, formatFlags); ms.Write(u16);
    BinaryPrimitives.WriteUInt32LittleEndian(u32, blocksPerFrame); ms.Write(u32);
    BinaryPrimitives.WriteUInt32LittleEndian(u32, finalFrameBlocks); ms.Write(u32);
    BinaryPrimitives.WriteUInt32LittleEndian(u32, totalFrames); ms.Write(u32);
    BinaryPrimitives.WriteUInt16LittleEndian(u16, bitsPerSample); ms.Write(u16);
    BinaryPrimitives.WriteUInt16LittleEndian(u16, channels); ms.Write(u16);
    BinaryPrimitives.WriteUInt32LittleEndian(u32, sampleRate); ms.Write(u32);

    // Seek table
    ms.Write(seekTable);
    // WAV header
    ms.Write(wavHeader);
    // Frame data
    ms.Write(frames);
    // Terminating data
    ms.Write(terminating);

    return ms.ToArray();
  }

  [Test, Category("HappyPath")]
  public void Modern_List_ReturnsExpectedEntries() {
    var wav = new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F', 0x20, 0, 0, 0 };
    var seek = new byte[] { 0x00, 0x01, 0x02, 0x03 };
    var frames = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD };
    var term = new byte[] { 0xEE };
    var ape = BuildModernApe(
      version: 3990, compressionLevel: 2000, formatFlags: 0,
      blocksPerFrame: 73728, finalFrameBlocks: 12345, totalFrames: 10,
      bitsPerSample: 16, channels: 2, sampleRate: 44100,
      wav, seek, frames, term);

    using var ms = new MemoryStream(ape);
    var entries = new ApeFormatDescriptor().List(ms, null);
    var names = entries.Select(e => e.Name).ToList();

    Assert.That(names, Does.Contain("FULL.ape"));
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("wav_header.bin"));
    Assert.That(names, Does.Contain("seek_table.bin"));
    Assert.That(names, Does.Contain("frames.bin"));
  }

  [Test, Category("HappyPath")]
  public void Modern_Extract_WritesFilesToDisk() {
    var wav = new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' };
    var seek = new byte[] { 0xAA, 0xBB };
    var frames = new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55 };
    var term = new byte[] { 0x99 };
    var ape = BuildModernApe(
      version: 3990, compressionLevel: 3000, formatFlags: 0,
      blocksPerFrame: 73728, finalFrameBlocks: 0, totalFrames: 5,
      bitsPerSample: 24, channels: 2, sampleRate: 48000,
      wav, seek, frames, term);

    var tmp = Path.Combine(Path.GetTempPath(), $"ape-test-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(ape);
      new ApeFormatDescriptor().Extract(ms, tmp, null, null);

      Assert.That(File.Exists(Path.Combine(tmp, "FULL.ape")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "metadata.ini")), Is.True);
      Assert.That(File.ReadAllBytes(Path.Combine(tmp, "wav_header.bin")), Is.EqualTo(wav));
      Assert.That(File.ReadAllBytes(Path.Combine(tmp, "seek_table.bin")), Is.EqualTo(seek));
      Assert.That(File.ReadAllBytes(Path.Combine(tmp, "frames.bin")), Is.EqualTo(frames));

      var ini = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(ini, Does.Contain("version=3990"));
      Assert.That(ini, Does.Contain("compression_level=3000"));
      Assert.That(ini, Does.Contain("sample_rate=48000"));
      Assert.That(ini, Does.Contain("channels=2"));
      Assert.That(ini, Does.Contain("bits_per_sample=24"));
      Assert.That(ini, Does.Contain("total_frames=5"));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new ApeFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Ape"));
    Assert.That(d.DefaultExtension, Is.EqualTo(".ape"));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo(new byte[] { 0x4D, 0x41, 0x43, 0x20 }));
    Assert.That(d.Category, Is.EqualTo(Compression.Registry.FormatCategory.Audio));
  }

  [Test, Category("EdgeCase")]
  public void BadMagic_ReturnsOnlyPassthrough() {
    var bogus = new byte[32];
    bogus[0] = 0xFF; bogus[1] = 0xFF;
    using var ms = new MemoryStream(bogus);
    var entries = new ApeFormatDescriptor().List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("FULL.ape"));
  }

  [Test, Category("HappyPath")]
  public void ExtractEntry_FullApe_ReturnsWholeFile() {
    var wav = new byte[] { 0x11, 0x22 };
    var seek = new byte[] { 0x33 };
    var frames = new byte[] { 0x44, 0x55, 0x66 };
    var term = new byte[] { };
    var ape = BuildModernApe(
      version: 3990, compressionLevel: 1000, formatFlags: 0,
      blocksPerFrame: 73728, finalFrameBlocks: 100, totalFrames: 1,
      bitsPerSample: 16, channels: 1, sampleRate: 22050,
      wav, seek, frames, term);

    using var ms = new MemoryStream(ape);
    using var output = new MemoryStream();
    new ApeFormatDescriptor().ExtractEntry(ms, "FULL.ape", output, null);
    Assert.That(output.ToArray(), Is.EqualTo(ape));
  }
}
