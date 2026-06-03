#pragma warning disable CS1591
using System.Buffers.Binary;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Wav;

namespace Compression.Tests.Audio;

/// <summary>
/// End-to-end coverage for arbitrary channel counts: any multi-channel container
/// decodes into one mono PCM WAV per speaker — mono through NHK 22.2 (24 channels)
/// and beyond (unmapped counts via CH_n) — and assembles back losslessly. Speaker
/// identities come from the FFmpeg default layout for the count, or from an
/// explicit WAVE_FORMAT_EXTENSIBLE speaker mask when the container carries one.
/// </summary>
[TestFixture]
public class MultiChannelSplitTests {

  private const int Frames = 16;

  /// <summary>Interleaved 16-bit PCM where channel <c>c</c> frame <c>f</c> = c*1000+f.</summary>
  private static byte[] MakeInterleaved(int channels) {
    var pcm = new byte[Frames * channels * 2];
    for (var f = 0; f < Frames; ++f)
      for (var c = 0; c < channels; ++c)
        BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan((f * channels + c) * 2), (short)(c * 1000 + f));
    return pcm;
  }

  [Test]
  public void Wav22Point2_SplitsInto24NamedMonoChannels() {
    var wav = PcmCodec.ToWavBlob(MakeInterleaved(24), 24, 48000, 16);
    using var ms = new MemoryStream(wav);
    var entries = new WavFormatDescriptor().List(ms, null);

    var channels = entries.Where(e => e.Kind == "Channel").Select(e => e.Name).ToList();
    Assert.That(channels, Has.Count.EqualTo(24));
    Assert.That(channels, Does.Contain("CENTER.wav"));
    Assert.That(channels, Does.Contain("LFE2.wav"));
    Assert.That(channels, Does.Contain("TOP_SIDE_LEFT.wav"));
    Assert.That(channels, Does.Contain("BOTTOM_FRONT_RIGHT.wav"));
    Assert.That(channels, Does.Not.Contain("CH_0.wav"));
  }

  [Test]
  public void Wav22Point2_EveryChannelIsAValidMonoWav_WithItsOwnSamples() {
    var wav = PcmCodec.ToWavBlob(MakeInterleaved(24), 24, 48000, 16);
    using var ms = new MemoryStream(wav);
    using var output = new MemoryStream();
    // BOTTOM_FRONT_RIGHT is channel index 23 — the last speaker of the 22.2 bed.
    new WavFormatDescriptor().ExtractEntry(ms, "BOTTOM_FRONT_RIGHT.wav", output, null);
    var mono = output.ToArray();

    Assert.That(mono.AsSpan(0, 4).ToArray(), Is.EqualTo("RIFF"u8.ToArray()));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(mono.AsSpan(22)), Is.EqualTo(1));
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(mono.AsSpan(24)), Is.EqualTo(48000u));
    // First sample of channel 23 = 23*1000+0.
    Assert.That(BinaryPrimitives.ReadInt16LittleEndian(mono.AsSpan(44)), Is.EqualTo((short)23000));
  }

  [Test]
  public void Wav22Point2_AssemblesBackFromPerChannelWavs_Losslessly() {
    var original = MakeInterleaved(24);
    var split = PcmCodec.SplitInterleavedPcm(original, 24, 48000, 16);
    Assert.That(split, Has.Count.EqualTo(24));

    // Shuffle the per-channel inputs; Create must restore canonical speaker order.
    var inputs = split
      .OrderBy(c => c.Name, StringComparer.Ordinal)
      .Select(c => ArchiveInputInfo.InMemory($"{c.Name}.wav", c.WavBlob))
      .ToList();

    using var output = new MemoryStream();
    ((IArchiveCreatable)new WavFormatDescriptor()).Create(output, inputs, new FormatCreateOptions());

    var parsed = new WavReader().Read(output.ToArray());
    Assert.That(parsed.NumChannels, Is.EqualTo(24));
    Assert.That(parsed.InterleavedPcm, Is.EqualTo(original));
  }

  [Test]
  public void WavBeyond22Point2_32Channels_DecodesToIndexedMonoChannels() {
    var wav = PcmCodec.ToWavBlob(MakeInterleaved(32), 32, 48000, 16);
    using var ms = new MemoryStream(wav);
    var entries = new WavFormatDescriptor().List(ms, null);

    var channels = entries.Where(e => e.Kind == "Channel").Select(e => e.Name).ToList();
    Assert.That(channels, Has.Count.EqualTo(32));
    Assert.That(channels, Does.Contain("CH_0.wav"));
    Assert.That(channels, Does.Contain("CH_31.wav"));
  }

  [Test]
  public void WavExtensible_SpeakerMask_NamesChannelsFromTheMask() {
    // 4-channel 3.1 (FL FR FC LFE, mask 0xF) — differs from the 4.0 count default.
    var wav = MakeExtensibleWav(MakeInterleaved(4), channels: 4, sampleRate: 44100,
                                bitsPerSample: 16, channelMask: 0x0000000F);
    using var ms = new MemoryStream(wav);
    var entries = new WavFormatDescriptor().List(ms, null);

    var channels = entries.Where(e => e.Kind == "Channel").Select(e => e.Name).ToList();
    Assert.That(channels, Is.EqualTo(new[] { "FRONT_LEFT.wav", "FRONT_RIGHT.wav", "CENTER.wav", "LFE.wav" }));
  }

  [Test]
  public void WavExtensible_MismatchedMask_FallsBackToCountDefault() {
    // Mask claims stereo but the stream has 4 channels → 4.0 default names.
    var wav = MakeExtensibleWav(MakeInterleaved(4), channels: 4, sampleRate: 44100,
                                bitsPerSample: 16, channelMask: 0x00000003);
    using var ms = new MemoryStream(wav);
    var entries = new WavFormatDescriptor().List(ms, null);

    var channels = entries.Where(e => e.Kind == "Channel").Select(e => e.Name).ToList();
    Assert.That(channels, Is.EqualTo(new[] { "FRONT_LEFT.wav", "FRONT_RIGHT.wav", "CENTER.wav", "BACK_CENTER.wav" }));
  }

  /// <summary>Builds a WAVE_FORMAT_EXTENSIBLE (fmt size 40) PCM WAV with an explicit speaker mask.</summary>
  private static byte[] MakeExtensibleWav(byte[] pcm, int channels, int sampleRate, int bitsPerSample, uint channelMask) {
    const int fmtSize = 40;
    var byteRate = sampleRate * channels * bitsPerSample / 8;
    var blockAlign = (ushort)(channels * bitsPerSample / 8);
    var fileSize = 4 + (8 + fmtSize) + (8 + pcm.Length);

    var wav = new byte[8 + fileSize];
    var s = wav.AsSpan();
    "RIFF"u8.CopyTo(s);
    BinaryPrimitives.WriteUInt32LittleEndian(s[4..], (uint)fileSize);
    "WAVE"u8.CopyTo(s[8..]);
    "fmt "u8.CopyTo(s[12..]);
    BinaryPrimitives.WriteUInt32LittleEndian(s[16..], fmtSize);
    BinaryPrimitives.WriteUInt16LittleEndian(s[20..], 0xFFFE);              // WAVE_FORMAT_EXTENSIBLE
    BinaryPrimitives.WriteUInt16LittleEndian(s[22..], (ushort)channels);
    BinaryPrimitives.WriteUInt32LittleEndian(s[24..], (uint)sampleRate);
    BinaryPrimitives.WriteUInt32LittleEndian(s[28..], (uint)byteRate);
    BinaryPrimitives.WriteUInt16LittleEndian(s[32..], blockAlign);
    BinaryPrimitives.WriteUInt16LittleEndian(s[34..], (ushort)bitsPerSample);
    BinaryPrimitives.WriteUInt16LittleEndian(s[36..], 22);                  // cbSize
    BinaryPrimitives.WriteUInt16LittleEndian(s[38..], (ushort)bitsPerSample); // wValidBitsPerSample
    BinaryPrimitives.WriteUInt32LittleEndian(s[40..], channelMask);         // dwChannelMask
    // SubFormat GUID: KSDATAFORMAT_SUBTYPE_PCM = 00000001-0000-0010-8000-00AA00389B71.
    BinaryPrimitives.WriteUInt16LittleEndian(s[44..], 1);                   // sub-format code = PCM
    s[48] = 0x00; s[49] = 0x00; s[50] = 0x10; s[51] = 0x00;
    s[52] = 0x80; s[53] = 0x00; s[54] = 0x00; s[55] = 0xAA;
    s[56] = 0x00; s[57] = 0x38; s[58] = 0x9B; s[59] = 0x71;
    "data"u8.CopyTo(s[60..]);
    BinaryPrimitives.WriteUInt32LittleEndian(s[64..], (uint)pcm.Length);
    pcm.CopyTo(s[68..]);
    return wav;
  }
}
