#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.InterplayAcm;
using FileFormat.Acm;

namespace Compression.Tests.Acm;

/// <summary>
/// Pseudo-archive tests for the Interplay ACM descriptor: it must surface a
/// byte-exact <c>FULL.acm</c>, a decoded mono channel for decodable input, and a
/// <c>metadata.ini</c> recording the raw header (channels verbatim). Undecodable
/// input falls back to <c>FULL.acm</c> + metadata only.
/// </summary>
[TestFixture]
public class AcmTests {

  private static byte[] BuildAcm(uint totalSamples, int channels, int sampleRate, int level, int rows, byte[] bitstream) {
    var header = new byte[14];
    BinaryPrimitives.WriteUInt32LittleEndian(header, InterplayAcmCodec.Magic);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), totalSamples);
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), (ushort)channels);
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10), (ushort)sampleRate);
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12), (ushort)((level & 0xF) | (rows << 4)));
    var blob = new byte[header.Length + bitstream.Length];
    header.CopyTo(blob.AsSpan());
    bitstream.CopyTo(blob.AsSpan(header.Length));
    return blob;
  }

  [Test]
  public void List_SurfacesFullMonoAndMetadata() {
    var blob = BuildAcm(totalSamples: 32, channels: 1, sampleRate: 22050, level: 2, rows: 8, bitstream: new byte[64]);
    using var ms = new MemoryStream(blob);
    var entries = new AcmFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.acm" && e.Kind == "Container"), Is.True);
    Assert.That(entries.Any(e => e.Name == "MONO.wav" && e.Kind == "Channel"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini" && e.Kind == "Tag"), Is.True);
  }

  [Test]
  public void Extract_FullAcm_RoundTripsBytes() {
    var blob = BuildAcm(totalSamples: 32, channels: 1, sampleRate: 22050, level: 2, rows: 8, bitstream: new byte[64]);
    using var input = new MemoryStream(blob);
    using var output = new MemoryStream();
    new AcmFormatDescriptor().ExtractEntry(input, "FULL.acm", output, null);
    Assert.That(output.ToArray(), Is.EqualTo(blob));
  }

  [Test]
  public void Extract_MonoWav_IsValidRiffAtHeaderSampleRate() {
    const int rate = 22050;
    var blob = BuildAcm(totalSamples: 32, channels: 1, sampleRate: rate, level: 2, rows: 8, bitstream: new byte[64]);
    using var input = new MemoryStream(blob);
    using var output = new MemoryStream();
    new AcmFormatDescriptor().ExtractEntry(input, "MONO.wav", output, null);
    var wav = output.ToArray();

    Assert.That(wav.AsSpan(0, 4).ToArray(), Is.EqualTo("RIFF"u8.ToArray()));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22)), Is.EqualTo(1)); // mono
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24)), Is.EqualTo((uint)rate));
  }

  [Test]
  public void Metadata_RecordsRawChannelCount() {
    // channels=2 in the header is surfaced verbatim even though many assets lie about it.
    var blob = BuildAcm(totalSamples: 16, channels: 2, sampleRate: 22050, level: 1, rows: 8, bitstream: new byte[64]);
    using var input = new MemoryStream(blob);
    using var output = new MemoryStream();
    new AcmFormatDescriptor().ExtractEntry(input, "metadata.ini", output, null);
    var meta = Encoding.UTF8.GetString(output.ToArray());

    Assert.That(meta, Does.Contain("channels=2"));
    Assert.That(meta, Does.Contain("sample_rate=22050"));
    Assert.That(meta, Does.Contain("level=1"));
  }

  [Test]
  public void List_UndecodableInput_FallsBackToFullPlusMetadata() {
    // Valid magic but a too-short header makes the decoder bail; the archive still lists
    // FULL.acm + metadata.ini (no channel).
    var blob = new byte[10];
    BinaryPrimitives.WriteUInt32LittleEndian(blob, InterplayAcmCodec.Magic);
    using var ms = new MemoryStream(blob);
    var entries = new AcmFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.acm"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Kind == "Channel"), Is.False);
  }
}
