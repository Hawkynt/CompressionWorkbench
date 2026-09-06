using System.Buffers.Binary;
using Codec.Pcm;
using Compression.Lib;
using Compression.Registry;
using FileFormat.Wav;

namespace Compression.Tests.Audio;

[TestFixture]
public sealed class WavAacConversionTests {
  [Test]
  public void RawAacWriter_UsesAudioSpecificConfig_AndRoundTripsToCanonicalPcm() {
    const int sampleRate = 44_100;
    const int frames = 3_500;
    var pcm = BuildPcm16(sampleRate, frames);
    var source = PcmCodec.ToWavBlob(pcm, 1, sampleRate, 16);

    using var input = new MemoryStream(source, writable: false);
    using var output = new MemoryStream();
    var descriptor = new WavFormatDescriptor();
    AudioConversionOperation.Convert(input, descriptor, output, descriptor, new FormatCreateOptions("aac"));

    var wav = output.ToArray();
    var fmtChunk = FindChunk(wav, "fmt "u8);
    var fmt = fmtChunk + 8;
    var dataChunk = FindChunk(wav, "data"u8);
    var data = dataChunk + 8;
    var decoded = new WavReader().ReadCanonicalPcm(wav);

    Assert.Multiple(() => {
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(fmtChunk + 4)), Is.EqualTo(20));
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(fmt)), Is.EqualTo(0x00FF));
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(fmt + 2)), Is.EqualTo(1));
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(fmt + 4)), Is.EqualTo(sampleRate));
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(fmt + 12)), Is.EqualTo(1));
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(fmt + 14)), Is.Zero);
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(fmt + 16)), Is.EqualTo(2));
      Assert.That(wav[fmt + 18], Is.EqualTo(0x12));
      Assert.That(wav[fmt + 19], Is.EqualTo(0x08));
      Assert.That((wav[data] == 0xFF && (wav[data + 1] & 0xF0) == 0xF0), Is.False,
        "RAW_AAC1 data must contain raw AAC access units, not ADTS headers.");
      Assert.That(ReadFactSampleFrames(wav), Is.EqualTo((uint)frames));
      Assert.That(decoded.FormatCode, Is.EqualTo(1));
      Assert.That(decoded.SampleRate, Is.EqualTo(sampleRate));
      Assert.That(decoded.NumChannels, Is.EqualTo(1));
      Assert.That(decoded.BitsPerSample, Is.EqualTo(16));
      Assert.That(decoded.InterleavedPcm.Length, Is.EqualTo(pcm.Length));
      Assert.That(decoded.InterleavedPcm.Any(static value => value != 0), Is.True);
    });
  }

  [Test]
  public void AdtsAacWriter_KeepsAdtsFraming_AndRoundTripsToCanonicalPcm() {
    const int sampleRate = 44_100;
    const int frames = 3_500;
    var pcm = BuildPcm16(sampleRate, frames);
    var source = PcmCodec.ToWavBlob(pcm, 1, sampleRate, 16);

    using var input = new MemoryStream(source, writable: false);
    using var output = new MemoryStream();
    var descriptor = new WavFormatDescriptor();
    AudioConversionOperation.Convert(input, descriptor, output, descriptor, new FormatCreateOptions("aac-adts"));

    var wav = output.ToArray();
    var fmtChunk = FindChunk(wav, "fmt "u8);
    var fmt = fmtChunk + 8;
    var dataChunk = FindChunk(wav, "data"u8);
    var data = dataChunk + 8;
    var decoded = new WavReader().ReadCanonicalPcm(wav);

    Assert.Multiple(() => {
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(fmtChunk + 4)), Is.EqualTo(18));
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(fmt)), Is.EqualTo(0x1600));
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(fmt + 16)), Is.Zero);
      Assert.That(wav[data], Is.EqualTo(0xFF));
      Assert.That(wav[data + 1] & 0xF0, Is.EqualTo(0xF0));
      Assert.That(ReadFactSampleFrames(wav), Is.EqualTo((uint)frames));
      Assert.That(decoded.FormatCode, Is.EqualTo(1));
      Assert.That(decoded.SampleRate, Is.EqualTo(sampleRate));
      Assert.That(decoded.NumChannels, Is.EqualTo(1));
      Assert.That(decoded.InterleavedPcm.Length, Is.EqualTo(pcm.Length));
      Assert.That(decoded.InterleavedPcm.Any(static value => value != 0), Is.True);
    });
  }

  [Test]
  public void RawAacReader_DecodesFfmpegAccessUnits() {
    // ffmpeg writes WAVE_FORMAT_RAW_AAC1 data as its ADTS access units with the transport
    // headers removed, so the same bitstream serves both WAVE tags.
    var raw = FfmpegAacVector.RawAccessUnits();
    var wav = BuildCompressedWave(0x00FF, FfmpegAacVector.SampleRate, 1, [2, 0, 0x12, 0x08], raw, FactFrames);

    var decoded = new WavReader().ReadCanonicalPcm(wav);
    var (signalToNoiseDb, levelRatio) = FfmpegAacVector.Compare(AsSamples(decoded.InterleavedPcm));

    Assert.Multiple(() => {
      Assert.That(decoded.FormatCode, Is.EqualTo(1));
      Assert.That(decoded.SampleRate, Is.EqualTo(FfmpegAacVector.SampleRate));
      Assert.That(decoded.NumChannels, Is.EqualTo(1));
      Assert.That(decoded.InterleavedPcm.Length, Is.EqualTo((int)FactFrames * 2));
      Assert.That(signalToNoiseDb, Is.GreaterThan(20.0));
      Assert.That(levelRatio, Is.EqualTo(1.0).Within(0.05));
    });
  }

  [Test]
  public void AdtsAacReader_DecodesTheSameStreamAsRawAac() {
    var adts = Convert.FromBase64String(FfmpegAacVector.AdtsBase64);
    var raw = FfmpegAacVector.RawAccessUnits();

    var fromAdts = new WavReader().ReadCanonicalPcm(
      BuildCompressedWave(0x1600, FfmpegAacVector.SampleRate, 1, [0, 0], adts, FactFrames));
    var fromRaw = new WavReader().ReadCanonicalPcm(
      BuildCompressedWave(0x00FF, FfmpegAacVector.SampleRate, 1, [2, 0, 0x12, 0x08], raw, FactFrames));

    Assert.That(fromAdts.InterleavedPcm, Is.EqualTo(fromRaw.InterleavedPcm),
      "the ADTS and RAW_AAC1 WAVE tags carry the same access units and must decode alike");
  }

  [Test]
  public void RawAacReader_RejectsMissingAudioSpecificConfig() {
    var wav = BuildCompressedWave(0x00FF, 44_100, 1, [0, 0], [0xE0], 1_024);
    Assert.Throws<InvalidDataException>(() => new WavReader().ReadCanonicalPcm(wav));
  }

  /// <summary>Every frame the vector carries, so the reader trims nothing away.</summary>
  private const uint FactFrames = 13 * 1_024;

  private static short[] AsSamples(byte[] pcm) {
    var samples = new short[pcm.Length / sizeof(short)];
    Buffer.BlockCopy(pcm, 0, samples, 0, samples.Length * sizeof(short));
    return samples;
  }

  private static byte[] BuildPcm16(int sampleRate, int frames) {
    var pcm = new byte[frames * 2];
    for (var frame = 0; frame < frames; ++frame) {
      var sample = (short)Math.Round(
        Math.Sin(2 * Math.PI * 310 * frame / sampleRate) * 10_000 +
        Math.Sin(2 * Math.PI * 730 * frame / sampleRate) * 2_000);
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(frame * 2, 2), sample);
    }
    return pcm;
  }

  private static byte[] BuildCompressedWave(
    ushort formatTag,
    int sampleRate,
    ushort channels,
    byte[] extraFmt,
    byte[] payload,
    uint factFrames) {
    var fmtSize = 16 + extraFmt.Length;
    var fmtPadded = fmtSize + (fmtSize & 1);
    var dataPadded = payload.Length + (payload.Length & 1);
    var result = new byte[12 + 8 + fmtPadded + 12 + 8 + dataPadded];
    "RIFF"u8.CopyTo(result);
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), checked((uint)(result.Length - 8)));
    "WAVE"u8.CopyTo(result.AsSpan(8));

    var pos = 12;
    "fmt "u8.CopyTo(result.AsSpan(pos));
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(pos + 4), checked((uint)fmtSize));
    var fmt = result.AsSpan(pos + 8, 16);
    BinaryPrimitives.WriteUInt16LittleEndian(fmt, formatTag);
    BinaryPrimitives.WriteUInt16LittleEndian(fmt[2..], channels);
    BinaryPrimitives.WriteUInt32LittleEndian(fmt[4..], checked((uint)sampleRate));
    BinaryPrimitives.WriteUInt32LittleEndian(fmt[8..], 12_000);
    BinaryPrimitives.WriteUInt16LittleEndian(fmt[12..], 1);
    BinaryPrimitives.WriteUInt16LittleEndian(fmt[14..], 0);
    extraFmt.CopyTo(result.AsSpan(pos + 24));
    pos += 8 + fmtPadded;

    "fact"u8.CopyTo(result.AsSpan(pos));
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(pos + 4), 4);
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(pos + 8), factFrames);
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
