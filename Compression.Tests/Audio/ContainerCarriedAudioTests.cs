using System.Security.Cryptography;
using Compression.Lib;
using Compression.Registry;
using FileFormat.Wav;
using NUnit.Framework;

namespace Compression.Tests.Audio;

/// <summary>
/// Two audio streams the pipeline could not read correctly: FLAC carried in Ogg,
/// and AAC in MP4 handed back with the encoder's warm-up still attached.
/// </summary>
[TestFixture]
public sealed class ContainerCarriedAudioTests {

  private static IFormatDescriptor Descriptor(string id) {
    FormatRegistration.EnsureInitialized();
    return FormatRegistry.All.Single(descriptor => descriptor.Id == id);
  }

  private static byte[] ConvertToWav(byte[] input, string sourceId) {
    using var source = new MemoryStream(input, writable: false);
    using var output = new MemoryStream();
    AudioConversionOperation.Convert(
      source, Descriptor(sourceId), output, new WavFormatDescriptor(),
      new FormatCreateOptions(Method: "pcm"));
    return output.ToArray();
  }

  private static byte[] WavPayload(byte[] wav, out int channels, out int sampleRate) {
    var position = 12;
    channels = 0;
    sampleRate = 0;
    while (position + 8 <= wav.Length) {
      var id = System.Text.Encoding.ASCII.GetString(wav, position, 4);
      var size = BitConverter.ToInt32(wav, position + 4);
      if (id == "fmt ") {
        channels = BitConverter.ToInt16(wav, position + 10);
        sampleRate = BitConverter.ToInt32(wav, position + 12);
      } else if (id == "data")
        return wav[(position + 8)..Math.Min(wav.Length, position + 8 + size)];

      position += 8 + size + (size & 1);
    }

    return [];
  }

  /// <summary>0.09 s of a 440 Hz sine, 22.05 kHz stereo, FLAC in an Ogg mapping.</summary>
  private const string FlacInOgg =
    "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";

  /// <summary>SHA-256 of the PCM libavcodec decodes from it.</summary>
  private const string FlacInOggDigest = "45dcfd32c21ef26d6e4d558c6792ac08640bd5a058ef794cbdab24eafe1d9c13";

  /// <summary>
  /// The Ogg reader recognised Opus and Speex and treated everything else as
  /// Vorbis, so FLAC in an Ogg mapping failed on the missing Vorbis header. The
  /// mapping's first packet carries a nine-byte prologue and then the native
  /// "fLaC" signature, and every packet after it is one metadata block or one
  /// audio frame — concatenating them reproduces the native stream.
  /// </summary>
  [Test]
  [Category("RoundTrip")]
  public void FlacCarriedInOggDecodesLikeLibavcodec() {
    var wav = ConvertToWav(Convert.FromBase64String(FlacInOgg), "Ogg");
    var pcm = WavPayload(wav, out var channels, out var sampleRate);

    Assert.Multiple(() => {
      Assert.That(channels, Is.EqualTo(2));
      Assert.That(sampleRate, Is.EqualTo(22_050));
      Assert.That(Convert.ToHexString(SHA256.HashData(pcm)).ToLowerInvariant(), Is.EqualTo(FlacInOggDigest),
        "FLAC is lossless, so this must be byte-for-byte what libavcodec decodes");
    });
  }

  /// <summary>The same audio as AAC-LC in MP4.</summary>
  private const string AacInMp4 =
    "AAAAHGZ0eXBNNEEgAAACAE00QSBpc29taXNvMgAAAAhmcmVlAAAHgW1kYXTcAExhdmM2My4xLjEwMQBCV6i7vTWGQWNYVMuUB9/n" +
    "iTXPA9fFLk95xNdxz5ht/+Y//v/3+l/F/pf2fqX5HHwM6hJFAREfHxyIEklhIkcSgqIwYxKbYI5HGE+qdkJbthFNiiqmCw5WhW6i" +
    "gkXSGsgkZMklLikZTCT4nivMXJOxv3v2r8j4N+d+O+o+xfM/NfE+vWKH+NUoP22tc1Zpy98z4N677F3j2F1TzV1Txdt3Z23dbbN1" +
    "t0Hr3QeG6DwylUpVKVXC8NwvDbbvW269jcdjcdcbFca1ca1Wa1Wa1Wa1jcdccdcblcblcbljdCuNyuNyuNyuNy0G5aDcrjcrjcrj" +
    "crjciSiSiSiSiSiSiSiSiSiSiSiSiSlq5auWrlq5auWrlq5auWrlq5auWrlq5auWroWGhYaFhoWGhYaFhoWGhYaFhqqKqKqKqKqK" +
    "qKqKqKqKqKqKqKqKqKqKqKqKqKqKqKqKqKqKqKqKqKqKqKqKqIooooooooooopZ6qKp6p6qJZ5aKqKp6qKqJaKqJZ5Z5Z5Z5Z5Z5" +
    "Z5Z5aJZ5Z5Z5Z5Z5Z5Z5Z5Z5Z5Z5Z5Z5Z5Z5Z5Z5Z5Z5Z5Z5Z5Z5Z5Z5Z5Z5Z5Z5Z5Z5Z5Z5Z5Z1nWFhYWFhYWFhYWFhYWFhYWFh" +
    "YWFhYWFhYWFhYWFhYWFghgrJPA2CGCsE8HZIYOuTw9whhLpPA2SGCqk8XgyF+2TwFghhLBPE4Eheqk8ZgyGEwhO3aIYO8TwN4hgL" +
    "pO/ZIYXAE71YhisGTweBIYPBk7+AIYPDk6lQhhbpPD4OLH8FMuUB9/niTXPA9fFLk95xNdxz5ht/+YAAAAAAAAAAAAAAAAAAAAAA" +
    "AAAAAAAAAAAAAAAAAAAADiFPbL7g///////9G6qAWe2+6MFHf5v8r//Hicddmda58/n9fxNe1zWd5ud//H/6deeHGnPI/9P+n/5e" +
    "13q+L52qA+A21tGtjJpqa8nXhi7Ozs7ZiyqOmfxT+5cJ26F6kMCjUxLBB1JqqG6Zf27794NrXl3Q3J2pc1UzjmqslR7l6mcJsqko" +
    "1/FfafDfYusuqfvX2XmruH0n2LsHVXXW3dlal7q4t2No3tXQ2qcvWVZMhYTzVpHuLVOmfXtu5+vbrnmqra1wOOkbVWdt27I3qFpq" +
    "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";

  /// <summary>
  /// A transform codec cannot start cold: AAC-LC returns a whole frame of
  /// warm-up before the first real sample and rounds the tail up to a frame.
  /// mdhd's duration counts every coded frame, warm-up included, so the audible
  /// length is that duration less the warm-up. Emitting the priming instead put
  /// the whole track 1024 frames ahead of every other decoder.
  /// </summary>
  [Test]
  [Category("RoundTrip")]
  public void AacInMp4DropsThePrimingAndTheTailPadding() {
    var wav = ConvertToWav(Convert.FromBase64String(AacInMp4), "Mp4");
    var pcm = WavPayload(wav, out var channels, out var sampleRate);

    Assert.Multiple(() => {
      Assert.That(channels, Is.EqualTo(2));
      Assert.That(sampleRate, Is.EqualTo(22_050));
      // libavcodec reports 1985 frames for this track.
      Assert.That(pcm.Length / 4, Is.EqualTo(1_985), "frames after trimming");
    });

    // The first samples are the tone, not a frame of warm-up silence.
    var openingPeak = 0;
    for (var i = 0; i + 1 < Math.Min(pcm.Length, 4 * 256); i += 2)
      openingPeak = Math.Max(openingPeak, Math.Abs(BitConverter.ToInt16(pcm, i)));
    Assert.That(openingPeak, Is.GreaterThan(500), "the track still opens with the decoder's warm-up");
  }
}
