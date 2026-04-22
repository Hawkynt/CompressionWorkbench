#pragma warning disable CS1591
using System.Text;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Mp3;
using FileFormat.Wav;

namespace Compression.Tests.Audio;

[TestFixture]
public class MediaWriteBackTests {

  [Test]
  public void Mp3_CreateFromMetadataAndCover_ProducesReadableTag() {
    var tmpDir = Path.Combine(Path.GetTempPath(), "mp3_wb_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmpDir);
    try {
      var metaPath = Path.Combine(tmpDir, "metadata.ini");
      File.WriteAllText(metaPath, "TIT2=Round-trip song\nTPE1=Test Artist\n");
      var coverPath = Path.Combine(tmpDir, "cover.png");
      File.WriteAllBytes(coverPath, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);  // PNG header

      using var ms = new MemoryStream();
      var desc = new Mp3FormatDescriptor();
      ((IArchiveCreatable)desc).Create(ms,
        [new ArchiveInputInfo(metaPath, "metadata.ini", false),
         new ArchiveInputInfo(coverPath, "cover.png", false)],
        new FormatCreateOptions());

      var bytes = ms.ToArray();
      Assert.That(bytes[0], Is.EqualTo((byte)'I'));
      Assert.That(bytes[1], Is.EqualTo((byte)'D'));
      Assert.That(bytes[2], Is.EqualTo((byte)'3'));

      var (_, frames) = new Id3v2Reader().Read(bytes);
      Assert.That(frames.Any(f => f.Id == "TIT2" && Encoding.UTF8.GetString(f.Payload).Contains("Round-trip song")), Is.True);
      Assert.That(frames.Any(f => f.Id == "APIC" && f.MimeType == "image/png"), Is.True);
    } finally {
      Directory.Delete(tmpDir, true);
    }
  }

  [Test]
  public void Wav_CreateFromFullPassesThrough() {
    var fullBytes = PcmCodec.ToWavBlob(new byte[40], channels: 2, sampleRate: 44100, bitsPerSample: 16);
    var tmpDir = Path.Combine(Path.GetTempPath(), "wav_wb_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmpDir);
    try {
      var fullPath = Path.Combine(tmpDir, "FULL.wav");
      File.WriteAllBytes(fullPath, fullBytes);

      using var ms = new MemoryStream();
      ((IArchiveCreatable)new WavFormatDescriptor()).Create(ms,
        [new ArchiveInputInfo(fullPath, "FULL.wav", false)],
        new FormatCreateOptions());
      Assert.That(ms.ToArray(), Is.EqualTo(fullBytes));
    } finally {
      Directory.Delete(tmpDir, true);
    }
  }

  [Test]
  public void Wav_CreateFromLeftRight_InterleavesStereo() {
    // Build LEFT and RIGHT mono WAVs with distinctive patterns.
    var sampleCount = 10;
    var leftPcm = new byte[sampleCount * 2];
    var rightPcm = new byte[sampleCount * 2];
    for (var i = 0; i < sampleCount; ++i) {
      System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(leftPcm.AsSpan(i * 2), (short)(i * 100));
      System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(rightPcm.AsSpan(i * 2), (short)(-i * 100));
    }
    var leftBytes = PcmCodec.ToWavBlob(leftPcm, 1, 44100, 16);
    var rightBytes = PcmCodec.ToWavBlob(rightPcm, 1, 44100, 16);

    var tmpDir = Path.Combine(Path.GetTempPath(), "wav_wb_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmpDir);
    try {
      var leftPath = Path.Combine(tmpDir, "LEFT.wav");
      var rightPath = Path.Combine(tmpDir, "RIGHT.wav");
      File.WriteAllBytes(leftPath, leftBytes);
      File.WriteAllBytes(rightPath, rightBytes);

      using var ms = new MemoryStream();
      ((IArchiveCreatable)new WavFormatDescriptor()).Create(ms,
        [new ArchiveInputInfo(leftPath, "LEFT.wav", false),
         new ArchiveInputInfo(rightPath, "RIGHT.wav", false)],
        new FormatCreateOptions());

      var parsed = new WavReader().Read(ms.ToArray());
      Assert.That(parsed.NumChannels, Is.EqualTo(2));
      Assert.That(parsed.SampleRate, Is.EqualTo(44100));
      // Interleaved: L[0], R[0], L[1], R[1], ...
      for (var i = 0; i < sampleCount; ++i) {
        var l = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(parsed.InterleavedPcm.AsSpan(i * 4));
        var r = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(parsed.InterleavedPcm.AsSpan(i * 4 + 2));
        Assert.That(l, Is.EqualTo((short)(i * 100)));
        Assert.That(r, Is.EqualTo((short)(-i * 100)));
      }
    } finally {
      Directory.Delete(tmpDir, true);
    }
  }
}
