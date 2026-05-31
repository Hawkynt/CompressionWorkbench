using System.Text;

namespace Compression.Tests.Iso;

/// <summary>
/// Large-directory support for the ISO 9660 writer. A directory whose records do
/// not fit in a single 2048-byte logical sector must occupy a multi-sector
/// extent: an individual directory record may not straddle a sector boundary, so
/// a record that would not fit is pushed to the next sector and the remainder of
/// the current sector is zero-padded. The writer must size the extent to the
/// required number of sectors (and widen the path-table reservation as needed),
/// and the reader must walk the whole multi-sector extent. Every file in one big
/// directory must round-trip at its correct path with content intact.
/// </summary>
[TestFixture]
public class IsoLargeDirectoryTests {

  [Test, Category("RoundTrip")]
  public void ThousandFilesInOneDirectory_SpanMultipleSectors_AllRoundTrip() {
    const int count = 1000;

    var w = new FileSystem.Iso.IsoWriter();
    for (var i = 0; i < count; i++)
      w.AddFile($"big/f{i:D4}.txt", Encoding.ASCII.GetBytes($"content-{i}"));
    var image = w.Build();

    using var ms = new MemoryStream(image);
    var r = new FileSystem.Iso.IsoReader(ms);

    var byPath = r.Entries.Where(e => !e.IsDirectory)
                          .ToDictionary(e => e.Name.ToUpperInvariant(), e => r.Extract(e));

    Assert.That(byPath.Count, Is.EqualTo(count),
      $"all {count} files present in the multi-sector directory extent");

    for (var i = 0; i < count; i++) {
      var path = $"BIG/F{i:D4}.TXT";
      Assert.That(byPath.ContainsKey(path), Is.True, $"{path} present");
    }

    // Spot-check several full contents across the range.
    foreach (var i in new[] { 0, 1, 42, 499, 500, 998, 999 })
      Assert.That(byPath[$"BIG/F{i:D4}.TXT"],
        Is.EqualTo(Encoding.ASCII.GetBytes($"content-{i}")),
        $"content of f{i:D4}.txt intact");
  }
}
