using System.Buffers.Binary;
using Codec.Mp3;
using Codec.Pcm;
using Compression.Lib;
using Compression.Registry;
using FileFormat.Wav;

namespace Compression.Tests.Audio;

[TestFixture]
public sealed class WavMp3ConversionTests {
  [TestCase(32_000, 1, 64)]
  [TestCase(44_100, 2, 128)]
  public void Writer_EmitsMpegLayer3WaveFormat_AndRoundTripsToCanonicalPcm(int sampleRate, int channels, int bitrateKbps) {
    const int frames = 6_000;
    var pcm = BuildPcm16(sampleRate, channels, frames);
    var source = PcmCodec.ToWavBlob(pcm, channels, sampleRate, 16);
    var options = new FormatCreateOptions("mp3") {
      FormatSpecific = new(StringComparer.OrdinalIgnoreCase) {
        ["bitrate"] = bitrateKbps.ToString(System.Globalization.CultureInfo.InvariantCulture),
      },
    };

    using var input = new MemoryStream(source, writable: false);
    using var output = new MemoryStream();
    var descriptor = new WavFormatDescriptor();
    AudioConversionOperation.Convert(input, descriptor, output, descriptor, options);

    var wav = output.ToArray();
    var fmtChunk = FindChunk(wav, "fmt "u8);
    var fmt = fmtChunk + 8;
    var dataChunk = FindChunk(wav, "data"u8);
    var data = dataChunk + 8;
    var firstFrame = Mp3FrameHeader.Parse(wav.AsSpan(data, 4));
    var decoded = new WavReader().ReadCanonicalPcm(wav);

    Assert.Multiple(() => {
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(fmtChunk + 4)), Is.EqualTo(30));
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(fmt)), Is.EqualTo(0x0055));
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(fmt + 2)), Is.EqualTo(channels));
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(fmt + 4)), Is.EqualTo((uint)sampleRate));
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(fmt + 12)), Is.EqualTo(1));
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(fmt + 14)), Is.Zero);
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(fmt + 16)), Is.EqualTo(12));
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(fmt + 18)), Is.EqualTo(1));
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(fmt + 20)), Is.Zero);
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(fmt + 24)), Is.EqualTo(firstFrame.FrameLengthBytes));
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(fmt + 26)), Is.EqualTo(1));
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(fmt + 28)), Is.EqualTo(576));
      Assert.That(ReadFactSampleFrames(wav), Is.EqualTo((uint)frames));
      Assert.That(firstFrame.Layer, Is.EqualTo(3));
      Assert.That(firstFrame.SampleRateHz, Is.EqualTo(sampleRate));
      Assert.That(firstFrame.Channels, Is.EqualTo(channels));
      Assert.That(firstFrame.BitrateKbps, Is.EqualTo(bitrateKbps));
      Assert.That(decoded.FormatCode, Is.EqualTo(1));
      Assert.That(decoded.NumChannels, Is.EqualTo(channels));
      Assert.That(decoded.SampleRate, Is.EqualTo(sampleRate));
      Assert.That(decoded.BitsPerSample, Is.EqualTo(16));
      Assert.That(decoded.InterleavedPcm.Length, Is.EqualTo(pcm.Length));
      Assert.That(decoded.InterleavedPcm.Any(static value => value != 0), Is.True);
    });
  }

  [Test]
  public void Reader_RejectsLayer2PayloadBehindLayer3Tag() {
    // MPEG-1 Layer II, 128 kbit/s, 44.1 kHz, mono. The body only needs to be long enough
    // for the frame scanner to accept the declared frame before the WAVE tag mismatch is checked.
    var frame = new byte[417];
    frame[0] = 0xFF;
    frame[1] = 0xFD;
    frame[2] = 0x80;
    frame[3] = 0xC0;
    var wav = BuildMpegLayer3Wave(frame, 44_100, 1);

    Assert.Throws<InvalidDataException>(() => new WavReader().ReadCanonicalPcm(wav));
  }

  private static byte[] BuildPcm16(int sampleRate, int channels, int frames) {
    var pcm = new byte[frames * channels * 2];
    for (var frame = 0; frame < frames; ++frame)
      for (var channel = 0; channel < channels; ++channel) {
        var sample = (short)Math.Round(
          Math.Sin(2 * Math.PI * (310 + channel * 170) * frame / sampleRate) * 10_000 +
          Math.Sin(2 * Math.PI * (730 + channel * 90) * frame / sampleRate) * 2_000);
        BinaryPrimitives.WriteInt16LittleEndian(
          pcm.AsSpan((frame * channels + channel) * 2, 2),
          sample);
      }
    return pcm;
  }

  private static byte[] BuildMpegLayer3Wave(byte[] payload, int sampleRate, int channels) {
    const int fmtSize = 30;
    var result = new byte[12 + 8 + fmtSize + 12 + 8 + payload.Length + (payload.Length & 1)];
    "RIFF"u8.CopyTo(result);
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), checked((uint)(result.Length - 8)));
    "WAVE"u8.CopyTo(result.AsSpan(8));

    var pos = 12;
    "fmt "u8.CopyTo(result.AsSpan(pos));
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(pos + 4), fmtSize);
    var fmt = result.AsSpan(pos + 8, fmtSize);
    BinaryPrimitives.WriteUInt16LittleEndian(fmt, 0x0055);
    BinaryPrimitives.WriteUInt16LittleEndian(fmt[2..], checked((ushort)channels));
    BinaryPrimitives.WriteUInt32LittleEndian(fmt[4..], checked((uint)sampleRate));
    BinaryPrimitives.WriteUInt32LittleEndian(fmt[8..], 16_000);
    BinaryPrimitives.WriteUInt16LittleEndian(fmt[12..], 1);
    BinaryPrimitives.WriteUInt16LittleEndian(fmt[14..], 0);
    BinaryPrimitives.WriteUInt16LittleEndian(fmt[16..], 12);
    BinaryPrimitives.WriteUInt16LittleEndian(fmt[18..], 1);
    BinaryPrimitives.WriteUInt32LittleEndian(fmt[20..], 0);
    BinaryPrimitives.WriteUInt16LittleEndian(fmt[24..], checked((ushort)payload.Length));
    BinaryPrimitives.WriteUInt16LittleEndian(fmt[26..], 1);
    BinaryPrimitives.WriteUInt16LittleEndian(fmt[28..], 0);
    pos += 8 + fmtSize;

    "fact"u8.CopyTo(result.AsSpan(pos));
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(pos + 4), 4);
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(pos + 8), 1_152);
    pos += 12;

    "data"u8.CopyTo(result.AsSpan(pos));
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(pos + 4), checked((uint)payload.Length));
    payload.CopyTo(result.AsSpan(pos + 8));
    return result;
  }

  private static uint ReadFactSampleFrames(ReadOnlySpan<byte> wav) {
    var offset = FindChunk(wav, "fact"u8);
    return BinaryPrimitives.ReadUInt32LittleEndian(wav.Slice(offset + 8, 4));
  }

  private static int FindChunk(ReadOnlySpan<byte> wav, ReadOnlySpan<byte> id) {
    for (var offset = 12; offset + 8 <= wav.Length;) {
      if (wav.Slice(offset, 4).SequenceEqual(id))
        return offset;
      var size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(wav.Slice(offset + 4, 4)));
      offset += 8 + size + (size & 1);
    }
    throw new InvalidDataException($"WAVE chunk '{System.Text.Encoding.ASCII.GetString(id)}' not found.");
  }
}
