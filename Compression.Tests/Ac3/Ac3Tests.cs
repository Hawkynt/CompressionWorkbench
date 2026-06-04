#pragma warning disable CS1591
using System.Text;
using FileFormat.Ac3;

namespace Compression.Tests.Ac3;

/// <summary>
/// Pins the read-only AC-3 / E-AC-3 stream-info descriptor. Hand-crafted sync frames exercise
/// the fscod sample-rate table, the frmsizecod frame-size / bitrate table, the acmod channel
/// arrangement (including LFE → "5.1"), the AC-3 vs E-AC-3 (bsid) distinction, multi-frame
/// counting and graceful handling of garbage.
/// </summary>
[TestFixture]
public class Ac3Tests {

  /// <summary>
  /// Builds a legacy AC-3 (bsid 8) sync frame: 0x0B77 sync, crc1(16), fscod(2), frmsizecod(6),
  /// bsid(5), bsmod(3), acmod(3), optional mix-level fields, lfeon(1), dialnorm(5), then zero
  /// padding to the table-derived frame size. The bit layout mirrors <see cref="Ac3SyncFrame"/>.
  /// </summary>
  private static byte[] BuildAc3Frame(int fscod, int frmsizecod, int acmod, int lfeon, int dialnorm) {
    var bits = new List<int>();
    void Put(int value, int count) {
      for (var i = count - 1; i >= 0; --i)
        bits.Add((value >> i) & 1);
    }

    Put(0, 16);              // crc1
    Put(fscod, 2);
    Put(frmsizecod, 6);
    Put(8, 5);               // bsid = 8 (legacy AC-3)
    Put(0, 3);               // bsmod
    Put(acmod, 3);
    if ((acmod & 0x1) != 0 && acmod != 1)
      Put(0, 2);             // cmixlev
    if ((acmod & 0x4) != 0)
      Put(0, 2);             // surmixlev
    if (acmod == 2)
      Put(0, 2);             // dsurmod
    Put(lfeon, 1);
    Put(dialnorm, 5);

    // Frame size from the A/52 table (words → bytes). For 48 kHz, frmsizecod 24 (=256 kbps even
    // code) → 512 words = 1024 bytes; we just size the buffer big enough and let the parser read.
    var frame = new byte[2048];
    frame[0] = 0x0B; frame[1] = 0x77;
    for (var i = 0; i < bits.Count; ++i)
      if (bits[i] != 0)
        frame[2 + (i >> 3)] |= (byte)(1 << (7 - (i & 7)));

    // Trim to the actual table-derived frame size so multi-frame walking lands on the next sync.
    var parsed = Ac3SyncFrame.TryParse(frame, 0)!.Value;
    return frame[..parsed.FrameSize];
  }

  private static string MetadataOf(byte[] blob) {
    using var input = new MemoryStream(blob);
    using var meta = new MemoryStream();
    new Ac3FormatDescriptor().ExtractEntry(input, "metadata.ini", meta, null);
    return Encoding.UTF8.GetString(meta.ToArray());
  }

  [Test]
  public void List_SurfacesFullAndMetadata() {
    var frame = BuildAc3Frame(fscod: 0, frmsizecod: 24, acmod: 7, lfeon: 1, dialnorm: 27);
    using var ms = new MemoryStream(frame);
    var entries = new Ac3FormatDescriptor().List(ms, null);

    var full = entries.Single(e => e.Name == "FULL.ac3");
    Assert.That(full.Kind, Is.EqualTo("Container"));
    Assert.That(full.Method, Is.EqualTo("ac3"));
    Assert.That(entries.Any(e => e.Name == "metadata.ini" && e.Kind == "Tag"), Is.True);
  }

  [Test]
  public void Metadata_Decodes51LayoutRateAndBitrateFromTables() {
    // fscod 0 → 48000 Hz; frmsizecod 24 → 256 kbps; acmod 7 + lfe → 3/2 + LFE (5.1).
    var frame = BuildAc3Frame(fscod: 0, frmsizecod: 24, acmod: 7, lfeon: 1, dialnorm: 31);
    var text = MetadataOf(frame);

    Assert.That(text, Does.Contain("codec=AC-3 (Dolby Digital)"));
    Assert.That(text, Does.Contain("sample_rate=48000"));
    Assert.That(text, Does.Contain("bitrate=256000"));
    Assert.That(text, Does.Contain("3/2 (L C R SL SR) + LFE (5.1)"));
    Assert.That(text, Does.Contain("channels=6"));
    Assert.That(text, Does.Contain("lfe=yes"));
  }

  [Test]
  public void Metadata_StereoFortyFourKhz() {
    // fscod 1 → 44100 Hz; acmod 2 → 2/0 stereo; no LFE.
    var frame = BuildAc3Frame(fscod: 1, frmsizecod: 18, acmod: 2, lfeon: 0, dialnorm: 27);
    var text = MetadataOf(frame);
    Assert.That(text, Does.Contain("sample_rate=44100"));
    Assert.That(text, Does.Contain("2/0 (stereo)"));
    Assert.That(text, Does.Contain("channels=2"));
    Assert.That(text, Does.Contain("lfe=no"));
  }

  [Test]
  public void Metadata_CountsMultipleFrames() {
    var frame = BuildAc3Frame(fscod: 0, frmsizecod: 12, acmod: 2, lfeon: 0, dialnorm: 31);
    var four = new byte[frame.Length * 4];
    for (var i = 0; i < 4; ++i)
      Array.Copy(frame, 0, four, i * frame.Length, frame.Length);
    var text = MetadataOf(four);
    Assert.That(text, Does.Contain("frames=4"));
  }

  [Test]
  public void Garbage_IsHandledGracefully() {
    var junk = Encoding.ASCII.GetBytes("definitely not an AC-3 elementary stream");
    using var ms = new MemoryStream(junk);
    List<Compression.Registry.ArchiveEntryInfo> entries = null!;
    Assert.That(() => entries = new Ac3FormatDescriptor().List(ms, null), Throws.Nothing);
    Assert.That(entries.Any(e => e.Name == "FULL.ac3"), Is.True);
    Assert.That(MetadataOf(junk), Does.Contain("frames=0"));
  }
}
