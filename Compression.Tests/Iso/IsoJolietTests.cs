using System.Text;

namespace Compression.Tests.Iso;

/// <summary>
/// Joliet support for the ISO 9660 writer/reader. A CD carries long, mixed-case,
/// Unicode file names through a Supplementary Volume Descriptor (type 2) whose
/// escape sequence selects UCS-2, plus a parallel directory-record tree whose
/// names are UCS-2 (UTF-16) big-endian and its own path tables. The same file
/// data extents are described by both the primary (short ECMA-119 names) and the
/// Joliet (long names) trees.
///
/// <para>Behaviour pinned here:
/// <list type="bullet">
///   <item>Given files added with long, mixed-case, Unicode names at nested
///   paths, when the image is read back (Joliet preferred), then the long names
///   round-trip exactly at their nested paths with content intact.</item>
///   <item>Given the same image, when read with Joliet disabled (the primary
///   ECMA-119 tree), then each file still has a short, uppercased name so that
///   non-Joliet readers continue to work.</item>
/// </list></para>
/// </summary>
[TestFixture]
public class IsoJolietTests {

  [Test, Category("RoundTrip")]
  public void LongMixedCaseUnicodeNames_RoundTripThroughJoliet() {
    var w = new FileSystem.Iso.IsoWriter();
    w.AddFile("Documents/Mixed Case Réadme.txt", "long name body"u8.ToArray());
    w.AddFile("Documents/api/Reference Guide.txt", "deep long name body"u8.ToArray());
    var image = w.Build();

    using var ms = new MemoryStream(image);
    var r = new FileSystem.Iso.IsoReader(ms);

    var files = r.Entries.Where(e => !e.IsDirectory)
                         .ToDictionary(e => e.Name, e => r.Extract(e), StringComparer.Ordinal);
    var dirs = r.Entries.Where(e => e.IsDirectory)
                        .Select(e => e.Name)
                        .ToHashSet(StringComparer.Ordinal);

    Assert.That(files.ContainsKey("Documents/Mixed Case Réadme.txt"), Is.True,
      "Joliet preserves the long, mixed-case, Unicode name at its nested path");
    Assert.That(files.ContainsKey("Documents/api/Reference Guide.txt"), Is.True,
      "Joliet preserves the deep long name at its two-level nested path");

    Assert.That(dirs.Contains("Documents"), Is.True, "Joliet directory keeps its mixed-case name");
    Assert.That(dirs.Contains("Documents/api"), Is.True, "Joliet nested directory keeps its lowercase name");

    Assert.That(files["Documents/Mixed Case Réadme.txt"], Is.EqualTo("long name body"u8.ToArray()),
      "content intact for the long-named file");
    Assert.That(files["Documents/api/Reference Guide.txt"], Is.EqualTo("deep long name body"u8.ToArray()),
      "content intact for the deep long-named file");
  }

  [Test, Category("RoundTrip")]
  public void PrimaryTree_StillCarriesShortUppercasedNames() {
    var w = new FileSystem.Iso.IsoWriter();
    w.AddFile("Documents/Mixed Case Réadme.txt", "long name body"u8.ToArray());
    w.AddFile("Documents/api/Reference Guide.txt", "deep long name body"u8.ToArray());
    var image = w.Build();

    // Read the primary ECMA-119 tree (Joliet disabled): names must be the short,
    // uppercased identifiers so non-Joliet readers still resolve the files.
    using var ms = new MemoryStream(image);
    var r = new FileSystem.Iso.IsoReader(ms, useJoliet: false);

    var files = r.Entries.Where(e => !e.IsDirectory).Select(e => e.Name).ToList();
    var dirs = r.Entries.Where(e => e.IsDirectory).Select(e => e.Name).ToList();

    // Every primary name is ASCII-only and uppercased.
    foreach (var name in files.Concat(dirs)) {
      Assert.That(name, Is.EqualTo(name.ToUpperInvariant()),
        $"primary-tree name '{name}' is uppercased");
      Assert.That(name.All(c => c < 128), Is.True,
        $"primary-tree name '{name}' is ASCII-only (no Unicode)");
    }

    Assert.That(dirs, Does.Contain("DOCUMENTS"), "primary tree has the uppercased directory");
    // Content is still reachable via the primary tree regardless of the short name.
    var bodies = r.Entries.Where(e => !e.IsDirectory).Select(e => r.Extract(e)).ToList();
    Assert.That(bodies, Has.Some.EqualTo("long name body"u8.ToArray()),
      "primary tree still points at the real file data");
    Assert.That(bodies, Has.Some.EqualTo("deep long name body"u8.ToArray()),
      "primary tree still points at the deep file data");
  }

  [Test, Category("HappyPath")]
  public void SupplementaryVolumeDescriptor_IsPresentWithUcs2Escape() {
    var w = new FileSystem.Iso.IsoWriter();
    w.AddFile("Readme.txt", "x"u8.ToArray());
    var image = w.Build();

    // Scan the volume-descriptor set for a type-2 descriptor carrying a UCS-2
    // escape sequence (0x25 0x2F followed by '@', 'C', or 'E') at offset 88.
    var found = false;
    for (var sector = 16; sector < 64; sector++) {
      var off = sector * 2048;
      if (off + 2048 > image.Length) break;
      if (image[off] == 0xFF) break; // terminator
      if (Encoding.ASCII.GetString(image, off + 1, 5) != "CD001") continue;
      if (image[off] != 2) continue;
      var e0 = image[off + 88];
      var e1 = image[off + 89];
      var e2 = image[off + 90];
      if (e0 == 0x25 && e1 == 0x2F && (e2 == 0x40 || e2 == 0x43 || e2 == 0x45)) {
        found = true;
        break;
      }
    }
    Assert.That(found, Is.True, "a Joliet Supplementary Volume Descriptor with a UCS-2 escape is emitted");
  }
}
