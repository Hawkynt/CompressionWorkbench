#pragma warning disable CS1591
using System.Text;
using FileFormat.Dts;

namespace Compression.Tests.Dts;

/// <summary>
/// Pins the read-only DTS (Coherent Acoustics) stream-info descriptor. Hand-crafted core frames
/// (sync word + the bit-packed header fields) exercise the SFREQ / RATE / AMODE tables, the LFE
/// flag, multi-frame counting via FSIZE, DTS-HD detection, and graceful handling of garbage.
/// </summary>
[TestFixture]
public class DtsTests {

  /// <summary>
  /// Builds a minimal DTS core frame: the 0x7FFE8001 sync word followed by an MSB-first
  /// big-endian packing of the header fields, then zero padding out to <paramref name="frameSize"/>.
  /// The bit layout mirrors <see cref="Codec.Dts.DtsFrameHeader"/>'s reader.
  /// </summary>
  private static byte[] BuildCoreFrame(
      int nblks, int frameSize, int amode, int sfreq, int rate, int lff) {
    var bits = new List<int>();
    void Put(int value, int count) {
      for (var i = count - 1; i >= 0; --i)
        bits.Add((value >> i) & 1);
    }

    Put(1, 1);            // FTYPE
    Put(0, 5);            // SHORT
    Put(0, 1);            // CPF
    Put(nblks, 7);        // NBLKS = sampleBlocks - 1
    Put(frameSize - 1, 14); // FSIZE = frameSize - 1
    Put(amode, 6);        // AMODE
    Put(sfreq, 4);        // SFREQ
    Put(rate, 5);         // RATE
    Put(0, 1);            // FixedBit
    Put(0, 1);            // DYNF
    Put(0, 1);            // TIMEF
    Put(0, 1);            // AUXF
    Put(0, 1);            // HDCD
    Put(0, 3);            // EXT_AUDIO_ID
    Put(0, 1);            // EXT_AUDIO
    Put(0, 1);            // ASPF
    Put(lff, 2);          // LFF

    var headerBytes = (bits.Count + 7) / 8;
    var frame = new byte[Math.Max(frameSize, 4 + headerBytes)];
    frame[0] = 0x7F; frame[1] = 0xFE; frame[2] = 0x80; frame[3] = 0x01;
    for (var i = 0; i < bits.Count; ++i)
      if (bits[i] != 0)
        frame[4 + (i >> 3)] |= (byte)(1 << (7 - (i & 7)));
    return frame[..frameSize];
  }

  private static string MetadataOf(byte[] blob) {
    using var input = new MemoryStream(blob);
    using var meta = new MemoryStream();
    new DtsFormatDescriptor().ExtractEntry(input, "metadata.ini", meta, null);
    return Encoding.UTF8.GetString(meta.ToArray());
  }

  [Test]
  public void List_AlwaysSurfacesFullAndMetadata() {
    var frame = BuildCoreFrame(nblks: 7, frameSize: 64, amode: 7, sfreq: 13, rate: 24, lff: 1);
    using var ms = new MemoryStream(frame);
    var entries = new DtsFormatDescriptor().List(ms, null);

    var full = entries.Single(e => e.Name == "FULL.dts");
    Assert.That(full.Kind, Is.EqualTo("Container"));
    Assert.That(full.Method, Is.EqualTo("dts"));
    Assert.That(entries.Any(e => e.Name == "metadata.ini" && e.Kind == "Tag"), Is.True);
    // The file IS the stream: no separate frames.bin payload.
    Assert.That(entries.Any(e => e.Name == "frames.bin"), Is.False);
  }

  [Test]
  public void Metadata_DecodesSfreqRateAmodeAndLfeFromTables() {
    // sfreq 13 → 48000 Hz, rate 24 → 1536000 bit/s (canonical DCA RATE table), amode 7 → 3.1, lff 1 → LFE.
    var frame = BuildCoreFrame(nblks: 7, frameSize: 96, amode: 7, sfreq: 13, rate: 24, lff: 1);
    var text = MetadataOf(frame);

    Assert.That(text, Does.Contain("sample_rate=48000"));
    Assert.That(text, Does.Contain("bitrate=1536000"));
    Assert.That(text, Does.Contain("lfe=yes"));
    Assert.That(text, Does.Contain("C+L+R+S (3.1 surround)"));
    // amode 7 = 4 channels + LFE = 5.
    Assert.That(text, Does.Contain("channels=5"));
  }

  [Test]
  public void Metadata_StereoNoLfe_DecodesChannelLayout() {
    // amode 2 → L+R stereo, sfreq 8 → 44100 Hz, no LFE.
    var frame = BuildCoreFrame(nblks: 7, frameSize: 64, amode: 2, sfreq: 8, rate: 20, lff: 0);
    var text = MetadataOf(frame);
    Assert.That(text, Does.Contain("sample_rate=44100"));
    Assert.That(text, Does.Contain("L+R (stereo)"));
    Assert.That(text, Does.Contain("lfe=no"));
    Assert.That(text, Does.Contain("channels=2"));
  }

  [Test]
  public void Metadata_CountsMultipleFramesByFrameSize() {
    var frame = BuildCoreFrame(nblks: 15, frameSize: 64, amode: 2, sfreq: 13, rate: 20, lff: 0);
    var three = new byte[frame.Length * 3];
    for (var i = 0; i < 3; ++i)
      Array.Copy(frame, 0, three, i * frame.Length, frame.Length);

    var text = MetadataOf(three);
    Assert.That(text, Does.Contain("frames=3"));
  }

  [Test]
  public void Metadata_DetectsDtsHdHeaderChunk() {
    var core = BuildCoreFrame(nblks: 7, frameSize: 64, amode: 2, sfreq: 13, rate: 20, lff: 0);
    var hd = new byte[8 + core.Length];
    "DTSHDHDR"u8.CopyTo(hd);
    core.CopyTo(hd.AsSpan(8));
    var text = MetadataOf(hd);
    Assert.That(text, Does.Contain("dts_hd_present=yes"));
  }

  [Test]
  public void Garbage_IsHandledGracefully() {
    var junk = Encoding.ASCII.GetBytes("this is not a DTS stream at all");
    using var ms = new MemoryStream(junk);
    List<Compression.Registry.ArchiveEntryInfo> entries = null!;
    Assert.That(() => entries = new DtsFormatDescriptor().List(ms, null), Throws.Nothing);
    Assert.That(entries.Any(e => e.Name == "FULL.dts"), Is.True);
    Assert.That(MetadataOf(junk), Does.Contain("frames=0"));
  }
}
