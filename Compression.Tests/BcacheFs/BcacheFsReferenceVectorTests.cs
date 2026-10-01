using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using Compression.Registry;
using FileSystem.BcacheFs;

namespace Compression.Tests.BcacheFs;

/// <summary>
/// The reader against volumes bcachefs-tools wrote, checked in so it runs everywhere.
/// </summary>
/// <remarks>
/// A volume this package writes is one unpacked bset per node with an empty
/// journal behind it; a volume the tools write packs every key, appends bsets as
/// it goes and leaves journal entries. Reading only the first would be reading our
/// own dialect, not the format. Provenance of the images is in
/// <c>ReferenceVectors/README.md</c>.
/// </remarks>
[TestFixture]
public sealed class BcacheFsReferenceVectorTests {

  private const string Formatted = "bcachefs-tools-1.3-format.img.gz";
  private const string Populated = "bcachefs-tools-1.39-format-source.img.gz";

  private static MemoryStream Image(string name) {
    using var resource = typeof(BcacheFsReferenceVectorTests).Assembly
      .GetManifestResourceStream("BcacheFsVectors." + name)
      ?? throw new FileNotFoundException($"embedded reference vector {name} is missing");
    using var gzip = new GZipStream(resource, CompressionMode.Decompress);
    var image = new MemoryStream();
    gzip.CopyTo(image);
    image.Position = 0;
    return image;
  }

  private sealed record Manifest(
    IReadOnlyDictionary<string, (string Sha256, long Size)> Files,
    IReadOnlyDictionary<string, string> Links,
    IReadOnlyList<string> Directories);

  private static Manifest ReadManifest() {
    using var resource = typeof(BcacheFsReferenceVectorTests).Assembly
      .GetManifestResourceStream("BcacheFsVectors.manifest.txt")!;
    using var text = new StreamReader(resource);
    var files = new Dictionary<string, (string, long)>(StringComparer.Ordinal);
    var links = new Dictionary<string, string>(StringComparer.Ordinal);
    var directories = new List<string>();
    while (text.ReadLine() is { } line) {
      if (line.Length == 0) continue;
      if (line.StartsWith("L ", StringComparison.Ordinal)) {
        var arrow = line.IndexOf(" -> ", StringComparison.Ordinal);
        links[line[2..arrow]] = line[(arrow + 4)..];
      } else if (line.StartsWith("D ", StringComparison.Ordinal)) {
        directories.Add(line[2..]);
      } else {
        var parts = line.Split(' ', 3);
        files[parts[2]] = (parts[0], long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
      }
    }
    return new Manifest(files, links, directories);
  }

  [Test, Category("HappyPath")]
  public void GivenTheDistroToolsFormattedVolume_WhenRead_ThenItIsAnEmptyRootWithLostAndFound() {
    using var image = Image(Formatted);
    using var reader = new BcacheFsReader(image);

    Assert.Multiple(() => {
      Assert.That(reader.Valid, Is.True, reader.Status);
      Assert.That(reader.Directories, Is.EqualTo(new[] { "lost+found" }),
        "bcachefs format creates lost+found under the root and nothing else");
      Assert.That(reader.Entries, Is.Empty);
    });
  }

  [Test, Category("HappyPath")]
  public void GivenAVolumeTheToolsPopulated_WhenRead_ThenEveryFileLinkAndDirectoryMatchesTheManifest() {
    var manifest = ReadManifest();
    using var image = Image(Populated);
    using var reader = new BcacheFsReader(image);
    Assert.That(reader.Valid, Is.True, reader.Status);

    var files = reader.Entries.Where(e => e.LinkTarget == null).ToDictionary(e => e.Name, StringComparer.Ordinal);
    var links = reader.Entries.Where(e => e.LinkTarget != null).ToDictionary(e => e.Name, e => e.LinkTarget!, StringComparer.Ordinal);

    Assert.Multiple(() => {
      Assert.That(files.Keys, Is.EquivalentTo(manifest.Files.Keys));
      Assert.That(links, Is.EquivalentTo(manifest.Links));
      Assert.That(reader.Directories, Is.EquivalentTo(manifest.Directories.Append("lost+found")));
      foreach (var (name, (sha256, size)) in manifest.Files) {
        if (!files.TryGetValue(name, out var entry)) continue;
        Assert.That(entry.Size, Is.EqualTo(size), $"{name} length");
        Assert.That(Convert.ToHexStringLower(SHA256.HashData(reader.Read(entry))), Is.EqualTo(sha256),
          $"{name} must read back byte for byte");
      }
    });
  }

  [Test, Category("HappyPath")]
  public void GivenAVolumeTheToolsPopulated_WhenListedAndExtracted_ThenFilesAndLinksComeOut() {
    var manifest = ReadManifest();
    var descriptor = new BcacheFsFormatDescriptor();
    using var image = Image(Populated);

    var listed = descriptor.List(image, null);
    Assert.Multiple(() => {
      foreach (var name in manifest.Files.Keys)
        Assert.That(listed.Any(e => e.Name == name && !e.IsSymlink), Is.True, $"{name} is listed");
      foreach (var (name, target) in manifest.Links)
        Assert.That(listed.Any(e => e.Name == name && e.IsSymlink && e.LinkTarget == target), Is.True,
          $"{name} is listed as a link to {target}");
    });

    var output = Path.Combine(Path.GetTempPath(), $"cwb_bchref_{Guid.NewGuid():N}");
    try {
      image.Position = 0;
      descriptor.Extract(image, output, null, null);
      Assert.Multiple(() => {
        foreach (var (name, (sha256, _)) in manifest.Files)
          Assert.That(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(output, name)))),
            Is.EqualTo(sha256), $"{name} extracts byte for byte");
      });
    } finally {
      try { Directory.Delete(output, true); } catch { /* best effort */ }
    }
  }

  [Test, Category("ErrorHandling")]
  [TestCase(Formatted)]
  [TestCase(Populated)]
  public void GivenAVolumeTheToolsWrote_WhenEditedInPlace_ThenTheEditIsRefusedAndNothingIsWritten(string name) {
    var descriptor = new BcacheFsFormatDescriptor();
    using var image = Image(name);
    var before = image.ToArray();

    Assert.Multiple(() => {
      Assert.Throws<NotSupportedException>(() => {
        image.Position = 0;
        descriptor.Add(image, [ArchiveInputInfo.InMemory("new.txt", "x"u8.ToArray())]);
      });
      Assert.Throws<NotSupportedException>(() => {
        image.Position = 0;
        descriptor.Remove(image, ["hello.txt"]);
      });
      Assert.Throws<NotSupportedException>(() => {
        image.Position = 0;
        descriptor.Defragment(image, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });
      });
    });
    Assert.That(image.ToArray(), Is.EqualTo(before), "a refused edit must leave the volume exactly as it was");
  }
}
