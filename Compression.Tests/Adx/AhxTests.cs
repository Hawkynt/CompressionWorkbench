#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.CriAdx;
using Compression.Registry;
using FileFormat.Adx;

namespace Compression.Tests.Adx;

/// <summary>
/// AHX is CRI's MPEG-2 Layer II (22.05 kHz mono) audio wrapped in an ADX-style header
/// (encoding type 0x10 / 0x11). These tests exercise the dispatch from
/// <see cref="AdxFormatDescriptor"/> into the MP3 decode path. The embedded payload is a
/// real MPEG-2 LSF Layer II elementary stream, so the positive-decode assertions hold;
/// a header with a junk payload exercises the documented decode-or-note fallback.
/// </summary>
[TestFixture]
public class AhxTests {

  // A genuine MPEG-2 LSF Layer II, 22050 Hz, mono elementary stream (≈0.2 s sine), the
  // exact payload form found after an AHX header's data offset.
  private const string Mpeg2Layer2Base64 =
    "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" +
    "ii+ohAWibH1ahUhPwJFOhe4A";

  /// <summary>Wraps an arbitrary payload in a minimal ADX header marked AHX (encoding type 0x10).</summary>
  private static byte[] BuildAhx(byte[] payload, byte encodingType = AdxCodec.EncodingTypeAhx) {
    const int copyrightOffset = 22;            // smallest legal offset (see AdxCodec)
    const int dataOffset = copyrightOffset + 4;
    var file = new byte[dataOffset + payload.Length];

    BinaryPrimitives.WriteUInt16BigEndian(file, AdxCodec.Magic);
    BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(2), copyrightOffset);
    file[4] = encodingType;     // AHX
    file[5] = 18;               // block size (unused for AHX)
    file[6] = 4;                // bit depth (unused)
    file[7] = 1;                // mono
    BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(8), 22050);  // sample rate
    BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(12), 0);     // total samples (AHX uses MPEG framing)
    BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(16), 0);     // highpass
    file[18] = 3;               // version
    file[19] = 0;               // flags
    Encoding.ASCII.GetBytes("(c)CRI").CopyTo(file.AsSpan(copyrightOffset - 2));
    payload.CopyTo(file, dataOffset);
    return file;
  }

  [Test]
  public void Descriptor_AhxHeader_DispatchesToAhxBranch() {
    // Junk payload: the AHX branch is still taken (metadata marks codec=ahx); the MP3
    // decode then fails gracefully, leaving FULL.adx + a documented note.
    var ahx = BuildAhx(new byte[64]);
    using var ms = new MemoryStream(ahx);
    var entries = new AdxFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.adx"), Is.True);

    using var meta = new MemoryStream();
    using var ms2 = new MemoryStream(ahx);
    new AdxFormatDescriptor().ExtractEntry(ms2, "metadata.ini", meta, null);
    var text = Encoding.UTF8.GetString(meta.ToArray());
    Assert.That(text, Does.Contain("codec=ahx"));
    Assert.That(text, Does.Contain("payload=mpeg2-layer2"));
  }

  [Test]
  public void Descriptor_AhxV11_AlsoDispatches() {
    var ahx = BuildAhx(new byte[32], encodingType: AdxCodec.EncodingTypeAhx11);
    var info = AdxCodec.ReadInfo(ahx);
    Assert.That(info.IsAhx, Is.True);
    Assert.That(info.IsStandard, Is.False);
  }

  [Test]
  public void Descriptor_AhxRealPayload_DecodesToMonoWav() {
    var payload = Convert.FromBase64String(Mpeg2Layer2Base64);
    var ahx = BuildAhx(payload);
    using var ms = new MemoryStream(ahx);
    var entries = new AdxFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "MONO.wav"), Is.True,
      "a genuine MPEG-2 Layer II payload must decode via the MP3 path");
    var mono = entries.First(e => e.Name == "MONO.wav");
    Assert.That(mono.Kind, Is.EqualTo("Channel"));
    Assert.That(mono.Method, Is.EqualTo("mp2"));

    using var output = new MemoryStream();
    using var ms2 = new MemoryStream(ahx);
    new AdxFormatDescriptor().ExtractEntry(ms2, "MONO.wav", output, null);
    var wav = output.ToArray();
    Assert.That(wav.AsSpan(0, 4).ToArray(), Is.EqualTo("RIFF"u8.ToArray()));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22)), Is.EqualTo(1)); // mono
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24)), Is.EqualTo(22050u));

    var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(40));
    Assert.That(dataSize, Is.GreaterThan(0u), "decoded PCM must be non-empty");
  }

  [Test]
  public void Descriptor_AhxRealPayload_MetadataRecordsRateAndChannels() {
    var payload = Convert.FromBase64String(Mpeg2Layer2Base64);
    using var ms = new MemoryStream(BuildAhx(payload));
    using var meta = new MemoryStream();
    new AdxFormatDescriptor().ExtractEntry(ms, "metadata.ini", meta, null);
    var text = Encoding.UTF8.GetString(meta.ToArray());

    Assert.That(text, Does.Contain("codec=ahx"));
    Assert.That(text, Does.Contain("sample_rate=22050"));
    Assert.That(text, Does.Contain("channels=1"));
  }
}
