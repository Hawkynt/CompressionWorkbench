#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.SevenZip;

namespace Compression.Tests.Operations;

/// <summary>
/// Verifies the 7z <see cref="IWipeEmpty"/> operation: dead bytes outside the
/// signature header, solid blocks and end-of-archive metadata are zeroed while
/// every entry still extracts byte-identically.
/// </summary>
[TestFixture]
public class SevenZipWipeEmptyOperationTests {

  private static byte[] BuildArchive(params (string Name, byte[] Data)[] files) {
    using var ms = new MemoryStream();
    using (var w = new SevenZipWriter(ms, SevenZipCodec.Lzma2, leaveOpen: true)) {
      foreach (var (n, d) in files)
        w.AddEntry(new SevenZipEntry { Name = n, Size = d.Length }, d);
      w.Finish();
    }
    return ms.ToArray();
  }

  private static Dictionary<string, byte[]> ReadAll(Stream s) {
    s.Position = 0;
    var r = new SevenZipReader(s);
    var map = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    for (var i = 0; i < r.Entries.Count; ++i) {
      var e = r.Entries[i];
      if (!e.IsDirectory) map[e.Name] = r.Extract(i);
    }
    return map;
  }

  [Test]
  public void SevenZipDescriptorImplementsWipeEmpty()
    => Assert.That(new SevenZipFormatDescriptor(), Is.InstanceOf<IWipeEmpty>());

  [Test]
  public void Wipe_ZerosTrailingJunk_AndPreservesEntries() {
    var payload = "the quick brown fox"u8.ToArray();
    var archive = BuildArchive(("doc.txt", payload));

    using var ms = new MemoryStream();
    ms.Write(archive);
    // Forensic remnant trailing the live metadata.
    var marker = new byte[256];
    Array.Fill(marker, (byte)0x7E);
    ms.Write(marker);
    ms.SetLength(ms.Length);

    var wiped = new SevenZipFormatDescriptor().WipeUnusedSpace(ms);
    Assert.That(wiped, Is.GreaterThanOrEqualTo(marker.Length), "trailing junk must be zeroed");

    // Live entry still round-trips.
    var got = ReadAll(ms);
    Assert.That(got["doc.txt"], Is.EqualTo(payload));

    // Trailing region is now all zero.
    ms.Position = archive.Length;
    var probe = new byte[marker.Length];
    _ = ms.Read(probe, 0, probe.Length);
    Assert.That(probe, Is.All.Zero);
  }
}
