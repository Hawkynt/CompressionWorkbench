#pragma warning disable CS1591
using System.Buffers.Binary;
using FileFormat.Wav;

namespace Compression.Tests.Wav;

/// <summary>
/// Pins the WAV dispatch for DSP Group TrueSpeech (<c>wFormatTag</c> 0x0022): a synthetic
/// TrueSpeech-tagged WAV must decode through <c>Codec.TrueSpeech</c> to mono 16-bit PCM
/// without throwing, with the correct sample count (<c>dataBytes / 32 * 240</c>) and the
/// post-decode <see cref="WavReader.ParsedWav.FormatCode"/> normalised to linear PCM.
/// </summary>
[TestFixture]
public class WavTrueSpeechTests {

  [Test]
  public void TrueSpeechWav_DecodesToMono16BitPcm_WithCorrectSampleCount() {
    const int frames = 3;
    var data = new byte[frames * 32];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i * 7 + 1);

    var wav = BuildWav(formatTag: 0x0022, channels: 1, sampleRate: 8000, bitsPerSample: 1, data);

    var parsed = new WavReader().Read(wav);

    // TrueSpeech is mono; decode normalises to linear 16-bit PCM (format code 1).
    Assert.That(parsed.NumChannels, Is.EqualTo(1));
    Assert.That(parsed.SampleRate, Is.EqualTo(8000));
    Assert.That(parsed.BitsPerSample, Is.EqualTo(16));
    Assert.That(parsed.FormatCode, Is.EqualTo(1));
    // dataBytes / 32 * 240 samples × 2 bytes each.
    Assert.That(parsed.InterleavedPcm.Length, Is.EqualTo(frames * 240 * 2));
  }

  [Test]
  public void TrueSpeechWav_RaggedDataTail_IsTrimmedToFrameBoundary() {
    // One full 32-byte frame plus a 16-byte tail that the decoder drops.
    var data = new byte[32 + 16];
    var wav = BuildWav(formatTag: 0x0022, channels: 1, sampleRate: 8000, bitsPerSample: 1, data);

    var parsed = new WavReader().Read(wav);
    Assert.That(parsed.InterleavedPcm.Length, Is.EqualTo(1 * 240 * 2));
  }

  private static byte[] BuildWav(int formatTag, int channels, int sampleRate, int bitsPerSample, byte[] data) {
    const int fmtSize = 16;
    var fileSize = 4 + (8 + fmtSize) + (8 + data.Length);
    var wav = new byte[8 + fileSize];
    var s = wav.AsSpan();
    "RIFF"u8.CopyTo(s);
    BinaryPrimitives.WriteUInt32LittleEndian(s[4..], (uint)fileSize);
    "WAVE"u8.CopyTo(s[8..]);
    "fmt "u8.CopyTo(s[12..]);
    BinaryPrimitives.WriteUInt32LittleEndian(s[16..], fmtSize);
    BinaryPrimitives.WriteUInt16LittleEndian(s[20..], (ushort)formatTag);
    BinaryPrimitives.WriteUInt16LittleEndian(s[22..], (ushort)channels);
    BinaryPrimitives.WriteUInt32LittleEndian(s[24..], (uint)sampleRate);
    BinaryPrimitives.WriteUInt32LittleEndian(s[28..], (uint)(sampleRate * channels * bitsPerSample / 8));
    BinaryPrimitives.WriteUInt16LittleEndian(s[32..], (ushort)(channels * bitsPerSample / 8));
    BinaryPrimitives.WriteUInt16LittleEndian(s[34..], (ushort)bitsPerSample);
    "data"u8.CopyTo(s[36..]);
    BinaryPrimitives.WriteUInt32LittleEndian(s[40..], (uint)data.Length);
    data.CopyTo(wav.AsSpan(44));
    return wav;
  }
}
