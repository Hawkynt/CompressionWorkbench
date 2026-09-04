using System.Buffers.Binary;
using Codec.Pcm;
using Compression.Lib;
using Compression.Registry;
using FileFormat.Aiff;
using FileFormat.Au;
using FileFormat.Caf;
using FileFormat.Wav;
using NUnit.Framework;

namespace Compression.Tests.Audio;

[TestFixture]
public sealed class G711PacketRemuxTests {

  [TestCase("alaw", 27u, "alaw")]
  [TestCase("mulaw", 1u, "ulaw")]
  public void WavToAuAndAifc_PreservesEncodedG711Bytes(string codec, uint auEncoding, string aifcCompression) {
    const int sampleRate = 8_000;
    const int channels = 2;
    const int frames = 1_603;
    var sourcePcm = PcmCodec.ToWavBlob(BuildPcm16(sampleRate, channels, frames), channels, sampleRate, 16);

    using var pcmInput = new MemoryStream(sourcePcm, writable: false);
    using var compressedWav = new MemoryStream();
    AudioConversionOperation.Convert(
      pcmInput,
      new WavFormatDescriptor(),
      compressedWav,
      new WavFormatDescriptor(),
      new FormatCreateOptions(Method: codec));

    var wav = new WavReader().Read(compressedWav.ToArray());
    var encodedPayload = wav.InterleavedPcm;

    using var wavForAu = new MemoryStream(compressedWav.ToArray(), writable: false);
    using var auOutput = new MemoryStream();
    AudioConversionOperation.Convert(wavForAu, new WavFormatDescriptor(), auOutput, new AuFormatDescriptor());
    var au = new AuReader().Read(auOutput.ToArray());

    using var wavForAifc = new MemoryStream(compressedWav.ToArray(), writable: false);
    using var aifcOutput = new MemoryStream();
    AudioConversionOperation.Convert(wavForAifc, new WavFormatDescriptor(), aifcOutput, new AiffFormatDescriptor());
    var aifc = new AiffReader().Read(aifcOutput.ToArray());

    Assert.Multiple(() => {
      Assert.That(au.Encoding, Is.EqualTo(auEncoding));
      Assert.That(au.SampleRate, Is.EqualTo(sampleRate));
      Assert.That(au.NumChannels, Is.EqualTo(channels));
      Assert.That(au.SoundData, Is.EqualTo(encodedPayload));

      Assert.That(aifc.IsAifc, Is.True);
      Assert.That(aifc.CompressionId, Is.EqualTo(aifcCompression));
      Assert.That(aifc.SampleRate, Is.EqualTo(sampleRate));
      Assert.That(aifc.NumChannels, Is.EqualTo(channels));
      Assert.That(aifc.SampleFrames, Is.EqualTo(frames));
      Assert.That(aifc.SoundData, Is.EqualTo(encodedPayload));
    });
  }

  /// <summary>
  /// Every directed pair of the four G.711 carriers remuxes without touching the companded
  /// bytes: the payload read back from the target equals the payload written into the source.
  /// </summary>
  [TestCase("Wav", "Aiff", "alaw")]
  [TestCase("Wav", "Au", "alaw")]
  [TestCase("Wav", "Caf", "alaw")]
  [TestCase("Aiff", "Wav", "mulaw")]
  [TestCase("Aiff", "Au", "mulaw")]
  [TestCase("Aiff", "Caf", "mulaw")]
  [TestCase("Au", "Wav", "alaw")]
  [TestCase("Au", "Aiff", "alaw")]
  [TestCase("Au", "Caf", "alaw")]
  [TestCase("Caf", "Wav", "mulaw")]
  [TestCase("Caf", "Aiff", "mulaw")]
  [TestCase("Caf", "Au", "mulaw")]
  public void EveryCarrierPair_RemuxesG711PacketsByteExact(string sourceId, string targetId, string codec) {
    const int sampleRate = 8_000;
    const int channels = 2;
    const int frames = 401;
    var payload = Enumerable.Range(0, frames * channels).Select(static value => unchecked((byte)(value * 53 + 7))).ToArray();
    var source = ResolveDescriptor(sourceId);
    var target = ResolveDescriptor(targetId);

    // Mux the raw payload into the source container first, so the source is a real file of that kind.
    var encoded = new AudioEncodedStream(new AudioStreamFormat(codec, sampleRate, channels, 8), [new AudioPacket(payload, frames)]);
    using var sourceFile = new MemoryStream();
    var sourceMux = AudioConversionInventory.Describe(source);
    Assert.That(sourceMux.CanMuxEncoded, Is.True, $"{sourceId} must mux G.711");
    MuxInto(source, sourceFile, encoded);

    using var input = new MemoryStream(sourceFile.ToArray(), writable: false);
    using var output = new MemoryStream();
    AudioConversionOperation.Convert(input, source, output, target);

    var (readCodec, readRate, readChannels, readPayload) = DemuxFrom(target, output.ToArray());
    Assert.Multiple(() => {
      Assert.That(readCodec, Is.EqualTo(codec));
      Assert.That(readRate, Is.EqualTo(sampleRate));
      Assert.That(readChannels, Is.EqualTo(channels));
      Assert.That(readPayload, Is.EqualTo(payload));
    });
  }

  private static IFormatDescriptor ResolveDescriptor(string id) => id switch {
    "Wav" => new WavFormatDescriptor(),
    "Aiff" => new AiffFormatDescriptor(),
    "Au" => new AuFormatDescriptor(),
    "Caf" => new CafFormatDescriptor(),
    _ => throw new ArgumentOutOfRangeException(nameof(id)),
  };

  private static void MuxInto(IFormatDescriptor descriptor, Stream output, AudioEncodedStream stream) {
    // Route through the public conversion surface: an Au source carrying the payload is
    // remuxed into the requested container, which exercises the target's mux path.
    var au = BuildAu(stream.Packets[0].Data, stream.Format.SampleRate, stream.Format.Channels,
      stream.Format.CodecId == "alaw" ? 27u : 1u);
    using var input = new MemoryStream(au, writable: false);
    AudioConversionOperation.Convert(input, new AuFormatDescriptor(), output, descriptor);
  }

  private static (string Codec, int SampleRate, int Channels, byte[] Payload) DemuxFrom(IFormatDescriptor descriptor, byte[] file) {
    switch (descriptor.Id) {
      case "Wav": {
        var wav = new WavReader().Read(file);
        return (wav.FormatCode == 0x0006 ? "alaw" : wav.FormatCode == 0x0007 ? "mulaw" : "?", wav.SampleRate, wav.NumChannels, wav.InterleavedPcm);
      }
      case "Aiff": {
        var aifc = new AiffReader().Read(file);
        return (aifc.CompressionId is "alaw" or "ALAW" ? "alaw" : aifc.CompressionId is "ulaw" or "ULAW" ? "mulaw" : "?", aifc.SampleRate, aifc.NumChannels, aifc.SoundData);
      }
      case "Au": {
        var au = new AuReader().Read(file);
        return (au.Encoding == 27 ? "alaw" : au.Encoding == 1 ? "mulaw" : "?", au.SampleRate, au.NumChannels, au.SoundData);
      }
      case "Caf": {
        // CafReader decodes G.711 to PCM, so read the raw chunk list here.
        var formatId = System.Text.Encoding.ASCII.GetString(file, 8 + 12 + 8, 4);
        var sampleRate = (int)BinaryPrimitives.ReadDoubleBigEndian(file.AsSpan(8 + 12, 8));
        var channels = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(8 + 12 + 24, 4));
        var pos = 8;
        byte[] payload = [];
        while (pos + 12 <= file.Length) {
          var type = System.Text.Encoding.ASCII.GetString(file, pos, 4);
          var size = (int)BinaryPrimitives.ReadInt64BigEndian(file.AsSpan(pos + 4, 8));
          if (type == "data") payload = file.AsSpan(pos + 12 + 4, size - 4).ToArray();
          pos += 12 + size;
        }
        return (formatId == "alaw" ? "alaw" : formatId == "ulaw" ? "mulaw" : "?", sampleRate, channels, payload);
      }
      default:
        throw new ArgumentOutOfRangeException(nameof(descriptor));
    }
  }

  [Test]
  public void AuToWav_PreservesMuLawPayloadWithoutPcmDecode() {
    const int sampleRate = 8_000;
    const int channels = 1;
    const int frames = 257;
    var payload = Enumerable.Range(0, frames).Select(static value => unchecked((byte)(value * 37))).ToArray();
    var au = BuildAu(payload, sampleRate, channels, encoding: 1);

    using var input = new MemoryStream(au, writable: false);
    using var output = new MemoryStream();
    AudioConversionOperation.Convert(input, new AuFormatDescriptor(), output, new WavFormatDescriptor());

    var wav = new WavReader().Read(output.ToArray());
    Assert.Multiple(() => {
      Assert.That(wav.FormatCode, Is.EqualTo(0x0007));
      Assert.That(wav.SampleRate, Is.EqualTo(sampleRate));
      Assert.That(wav.NumChannels, Is.EqualTo(channels));
      Assert.That(wav.InterleavedPcm, Is.EqualTo(payload));
    });
  }

  private static byte[] BuildAu(byte[] payload, int sampleRate, int channels, uint encoding) {
    var result = new byte[24 + payload.Length];
    ".snd"u8.CopyTo(result);
    BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(4), 24);
    BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(8), checked((uint)payload.Length));
    BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(12), encoding);
    BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(16), checked((uint)sampleRate));
    BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(20), checked((uint)channels));
    payload.CopyTo(result, 24);
    return result;
  }

  private static byte[] BuildPcm16(int sampleRate, int channels, int frames) {
    var pcm = new byte[frames * channels * 2];
    for (var frame = 0; frame < frames; ++frame)
      for (var channel = 0; channel < channels; ++channel) {
        var sample = (short)Math.Round(Math.Sin(2.0 * Math.PI * (300.0 + channel * 170.0) * frame / sampleRate) * 12_000.0);
        BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan((frame * channels + channel) * 2, 2), sample);
      }
    return pcm;
  }
}
