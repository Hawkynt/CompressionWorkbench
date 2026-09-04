#pragma warning disable CS1591
using System.Buffers.Binary;
using Codec.Gsm610;
using FileFormat.Gsm;

namespace Compression.Tests.Gsm;

[TestFixture]
public class GsmRawTests {

  /// <summary>
  /// Builds <paramref name="frameCount"/> synthetic 33-byte GSM 06.10 frames. Each
  /// frame's first byte carries the signature nibble 0xD in its high four bits (so the
  /// raw-frame validator accepts it); the remaining bits are varied filler that the
  /// structurally-correct decoder turns into recognisable PCM.
  /// </summary>
  private static byte[] MakeRawGsm(int frameCount) {
    var data = new byte[frameCount * Gsm610Codec.FrameBytes];
    for (var f = 0; f < frameCount; ++f) {
      var off = f * Gsm610Codec.FrameBytes;
      data[off] = (byte)(0xD0 | (f & 0x0F)); // high nibble 0xD = frame magic
      for (var i = 1; i < Gsm610Codec.FrameBytes; ++i)
        data[off + i] = (byte)((f * 7 + i * 13) & 0xFF);
    }
    return data;
  }

  [Test]
  public void Create_FromMonoWav_EncodesFramesThatDecodeBack() {
    const int frames = 50;
    var pcm = new short[frames * Gsm610Codec.FrameSamples];
    for (var i = 0; i < pcm.Length; ++i)
      pcm[i] = (short)(Math.Sin(2 * Math.PI * 440 * i / 8000.0) * 9000);
    var le = new byte[pcm.Length * 2];
    for (var i = 0; i < pcm.Length; ++i) BinaryPrimitives.WriteInt16LittleEndian(le.AsSpan(i * 2), pcm[i]);
    var wav = Codec.Pcm.PcmCodec.ToWavBlob(le, channels: 1, GsmRawFormatDescriptor.SampleRate, bitsPerSample: 16);

    var descriptor = new GsmRawFormatDescriptor();
    Assert.That(descriptor.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanCreate), Is.True);
    using var output = new MemoryStream();
    descriptor.Create(output, [Compression.Registry.ArchiveInputInfo.InMemory("MONO.wav", wav)], new Compression.Registry.FormatCreateOptions());
    var raw = output.ToArray();

    Assert.That(raw.Length, Is.EqualTo(frames * Gsm610Codec.FrameBytes));
    Assert.That(Gsm610Codec.LooksLikeRawFrames(raw), Is.True);

    using var input = new MemoryStream(raw);
    var entries = descriptor.List(input, null);
    Assert.That(entries.Select(e => e.Name), Does.Contain("MONO.wav"));
    var decoded = Gsm610Codec.DecodeRaw(raw);
    Assert.That(decoded, Has.Length.EqualTo(pcm.Length));
    // Steady-state level after the first two frames of adaptation; RMS is phase-insensitive.
    double source = 0, roundTrip = 0;
    for (var i = 2 * Gsm610Codec.FrameSamples; i < pcm.Length; ++i) {
      source += (double)pcm[i] * pcm[i];
      roundTrip += (double)decoded[i] * decoded[i];
    }
    Assert.That(Math.Sqrt(roundTrip / source), Is.InRange(0.7, 1.3), "GSM round trip keeps the tone within 3 dB");
  }

  [Test]
  public void Create_PassesFullGsmThroughVerbatim() {
    var blob = MakeRawGsm(3);
    using var output = new MemoryStream();
    new GsmRawFormatDescriptor().Create(output, [Compression.Registry.ArchiveInputInfo.InMemory("FULL.gsm", blob)], new Compression.Registry.FormatCreateOptions());
    Assert.That(output.ToArray(), Is.EqualTo(blob));
  }

  [Test]
  public void Descriptor_ListsFullMonoAndMetadata() {
    var blob = MakeRawGsm(4);
    using var ms = new MemoryStream(blob);
    var entries = new GsmRawFormatDescriptor().List(ms, null);

    Assert.Multiple(() => {
      Assert.That(entries.Any(e => e.Name == "FULL.gsm" && e.Kind == "Container"), Is.True);
      Assert.That(entries.Any(e => e.Name == "MONO.wav" && e.Kind == "Channel"), Is.True);
      Assert.That(entries.Any(e => e.Name == "metadata.ini" && e.Kind == "Tag"), Is.True);
    });
  }

  [Test]
  public void Descriptor_ExtractedChannel_IsValidMonoRiffAt8000Hz() {
    var blob = MakeRawGsm(4);
    using var ms = new MemoryStream(blob);
    using var output = new MemoryStream();
    new GsmRawFormatDescriptor().ExtractEntry(ms, "MONO.wav", output, null);
    var wav = output.ToArray();

    Assert.Multiple(() => {
      Assert.That(wav.AsSpan(0, 4).SequenceEqual("RIFF"u8), Is.True);
      Assert.That(wav.AsSpan(8, 4).SequenceEqual("WAVE"u8), Is.True);
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22)), Is.EqualTo(1), "mono");
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24)), Is.EqualTo((uint)GsmRawFormatDescriptor.SampleRate));
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(34)), Is.EqualTo(16));
    });

    // 160 samples per frame, 2 bytes per sample.
    var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(40));
    Assert.That(dataSize, Is.EqualTo((uint)(4 * Gsm610Codec.FrameSamples * 2)));
  }

  [Test]
  public void Descriptor_GracefullyFallsBackOnGarbage() {
    // Not a whole number of frames and wrong signature nibble — no MONO.wav surfaces.
    var garbage = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66 };
    using var ms = new MemoryStream(garbage);
    var entries = new GsmRawFormatDescriptor().List(ms, null);

    Assert.Multiple(() => {
      Assert.That(entries.Any(e => e.Name == "FULL.gsm"), Is.True);
      Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
      Assert.That(entries.Any(e => e.Name == "MONO.wav"), Is.False, "garbage must not decode to a channel");
    });
  }

  [Test]
  public void Descriptor_WrongSignatureNibble_FallsBack() {
    // Right length (one frame) but the magic nibble is not 0xD.
    var notGsm = new byte[Gsm610Codec.FrameBytes];
    for (var i = 0; i < notGsm.Length; ++i) notGsm[i] = (byte)(0xA0 | (i & 0x0F));
    using var ms = new MemoryStream(notGsm);
    var entries = new GsmRawFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "MONO.wav"), Is.False);
  }
}
