using Compression.Registry;
using FileFormat.Shorten;

namespace Compression.Tests.Shorten;

/// <summary>
/// Given a Shorten (.shn) file, When the descriptor lists/extracts it, Then it
/// surfaces FULL.shn + metadata.ini (version, type, channels, block size,
/// predictor) + the raw payload, with FULL byte-identical and no throw on
/// malformed input. Audio decode is deferred (structural-only).
/// </summary>
[TestFixture]
public class ShortenPseudoArchiveTests {

  // Minimal MSB-first bit writer mirroring Shorten's uvar / ulong encodings so we
  // can synthesise a valid header that the descriptor's reader decodes.
  private sealed class BitWriter {
    private readonly List<byte> _bytes = [];
    private int _cur, _nbits;
    private const int UlongSize = 2;

    public void WriteBit(int b) {
      _cur = (_cur << 1) | (b & 1);
      if (++_nbits == 8) { _bytes.Add((byte)_cur); _cur = 0; _nbits = 0; }
    }
    private void WriteBits(long v, int k) {
      for (var i = k - 1; i >= 0; --i) WriteBit((int)((v >> i) & 1));
    }
    public void WriteUvar(long value, int k) {
      var high = value >> k;
      for (var i = 0; i < high; ++i) WriteBit(0);
      WriteBit(1);
      if (k > 0) WriteBits(value & ((1L << k) - 1), k);
    }
    public void WriteUlong(long value) {
      var k = 0;
      while ((1L << k) <= value && k < 40) ++k;
      WriteUvar(k, UlongSize);
      WriteUvar(value, k);
    }
    public byte[] ToArray() {
      if (_nbits > 0) { _cur <<= 8 - _nbits; _bytes.Add((byte)_cur); _cur = 0; _nbits = 0; }
      return _bytes.ToArray();
    }
  }

  // Builds a Shorten v2 file: "ajkg" + version 2, then ulong header fields
  // (fileType=3 s16, nchan=2, blocksize=256, maxnlpc=0 polynomial, nmean=0).
  private static byte[] BuildShorten() {
    var bw = new BitWriter();
    bw.WriteUlong(3);    // fileType: s16 high-low
    bw.WriteUlong(2);    // channels
    bw.WriteUlong(256);  // block size
    bw.WriteUlong(0);    // max LPC order (0 => polynomial)
    bw.WriteUlong(0);    // mean count
    var body = bw.ToArray();

    using var ms = new MemoryStream();
    ms.Write("ajkg"u8);
    ms.WriteByte(2);
    ms.Write(body);
    return ms.ToArray();
  }

  [Test, Category("HappyPath")]
  public void List_ExposesFullMetadataAndPayload() {
    var shn = BuildShorten();
    using var ms = new MemoryStream(shn);
    var names = new ShortenFormatDescriptor().List(ms, null).Select(e => e.Name).ToList();

    Assert.That(names, Does.Contain("FULL.shn"));
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("payload.bin"));
  }

  [Test, Category("HappyPath")]
  public void Extract_FullByteIdentical_MetadataHasDecodedHeader() {
    var shn = BuildShorten();
    var tmp = Path.Combine(Path.GetTempPath(), $"shn-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(shn);
      new ShortenFormatDescriptor().Extract(ms, tmp, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(tmp, "FULL.shn")), Is.EqualTo(shn));
      var meta = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(meta, Does.Contain("version=2"));
      Assert.That(meta, Does.Contain("channels=2"));
      Assert.That(meta, Does.Contain("block_size=256"));
      Assert.That(meta, Does.Contain("predictor_type=polynomial"));
      Assert.That(meta, Does.Contain("internal_type=s16 high-low"));
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("EdgeCase")]
  public void Malformed_DoesNotThrow_FallsBackToFull() {
    var bogus = new byte[] { 0x00, 0x01, 0x02 };
    using var ms = new MemoryStream(bogus);
    List<ArchiveEntryInfo> entries = null!;
    Assert.DoesNotThrow(() => entries = new ShortenFormatDescriptor().List(ms, null));
    Assert.That(entries[0].Name, Is.EqualTo("FULL.shn"));
  }

  [Test, Category("HappyPath")]
  public void ExtractEntry_FullRoundTrips() {
    var shn = BuildShorten();
    using var ms = new MemoryStream(shn);
    using var full = new MemoryStream();
    new ShortenFormatDescriptor().ExtractEntry(ms, "FULL.shn", full, null);
    Assert.That(full.ToArray(), Is.EqualTo(shn));
  }
}
