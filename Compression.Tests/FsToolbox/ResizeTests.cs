#pragma warning disable CS1591
using Compression.Registry;

namespace Compression.Tests.FsToolbox;

[TestFixture]
public class ResizeTests {

  /// <summary>
  /// Resizes a FAT12 image from 1.44 MB to 720 KB. Content must survive.
  /// </summary>
  [Test]
  public void Resize_FatShrink_PreservesFiles() {
    var writer = new FileSystem.Fat.FatWriter();
    writer.AddFile("SMALL.TXT", "tiny"u8.ToArray());
    var image = writer.Build(totalSectors: 2880); // 1.44 MB

    var tempPath = Path.Combine(Path.GetTempPath(), $"cwb_resize_{Guid.NewGuid():N}.img");
    try {
      File.WriteAllBytes(tempPath, image);

      // Resize to 720 KB (3.5" DD floppy)
      Compression.Lib.ArchiveOperations.Resize(tempPath, 737_280);

      var fi = new FileInfo(tempPath);
      Assert.That(fi.Length, Is.EqualTo(737_280));

      // Verify content.
      using var fs = File.OpenRead(tempPath);
      var reader = new FileSystem.Fat.FatReader(fs);
      var entries = reader.Entries.Where(e => !e.IsDirectory).ToList();
      Assert.That(entries, Has.Count.EqualTo(1));
      Assert.That(reader.Extract(entries[0]), Is.EqualTo("tiny"u8.ToArray()));
    } finally {
      if (File.Exists(tempPath)) File.Delete(tempPath);
    }
  }

  /// <summary>
  /// Resizes a FAT12 image from 720 KB to 1.44 MB (grow).
  /// </summary>
  [Test]
  public void Resize_FatGrow_PreservesFiles() {
    var writer = new FileSystem.Fat.FatWriter();
    writer.AddFile("GROW.TXT", "growing"u8.ToArray());
    var image = writer.Build(totalSectors: 1440); // 720 KB

    var tempPath = Path.Combine(Path.GetTempPath(), $"cwb_resize_grow_{Guid.NewGuid():N}.img");
    try {
      File.WriteAllBytes(tempPath, image);

      // Resize to 1.44 MB
      Compression.Lib.ArchiveOperations.Resize(tempPath, 1_474_560);

      var fi = new FileInfo(tempPath);
      Assert.That(fi.Length, Is.EqualTo(1_474_560));

      // Verify content.
      using var fs = File.OpenRead(tempPath);
      var reader = new FileSystem.Fat.FatReader(fs);
      var entries = reader.Entries.Where(e => !e.IsDirectory).ToList();
      Assert.That(entries, Has.Count.EqualTo(1));
      Assert.That(reader.Extract(entries[0]), Is.EqualTo("growing"u8.ToArray()));
    } finally {
      if (File.Exists(tempPath)) File.Delete(tempPath);
    }
  }

  /// <summary>
  /// Preview shows correct fit/no-fit status.
  /// </summary>
  [Test]
  public void PreviewResize_CorrectFitStatus() {
    var writer = new FileSystem.Fat.FatWriter();
    writer.AddFile("FILE.DAT", new byte[500_000]); // ~500 KB of data
    var image = writer.Build(totalSectors: 2880);

    var tempPath = Path.Combine(Path.GetTempPath(), $"cwb_resize_preview_{Guid.NewGuid():N}.img");
    try {
      File.WriteAllBytes(tempPath, image);

      // Preview resize to 1.44 MB — should fit.
      var previewFit = Compression.Lib.ArchiveOperations.PreviewResize(tempPath, 1_474_560);
      Assert.That(previewFit.Fits, Is.True);

      // Preview resize to 100 KB — should NOT fit (500 KB content).
      var previewNoFit = Compression.Lib.ArchiveOperations.PreviewResize(tempPath, 100_000);
      Assert.That(previewNoFit.Fits, Is.False);
    } finally {
      if (File.Exists(tempPath)) File.Delete(tempPath);
    }
  }

  /// <summary>
  /// MediaProfile lookup works for all known profiles.
  /// </summary>
  [Test]
  public void MediaProfileLookup_AllProfiles_Valid() {
    foreach (var (name, profile, expectedSize) in MediaProfileLookup.AllProfiles) {
      Assert.That(MediaProfileLookup.TryParse(name, out var parsed), Is.True, $"Failed to parse: {name}");
      Assert.That(parsed, Is.EqualTo(profile));
      Assert.That(MediaProfileLookup.GetSize(parsed), Is.EqualTo(expectedSize));
    }
  }

  /// <summary>
  /// MediaProfile unknown name returns false.
  /// </summary>
  [Test]
  public void MediaProfileLookup_UnknownName_ReturnsFalse() {
    Assert.That(MediaProfileLookup.TryParse("unknown", out _), Is.False);
  }
}
