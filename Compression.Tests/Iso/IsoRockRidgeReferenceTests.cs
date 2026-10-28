using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using FileSystem.Iso;

namespace Compression.Tests.Iso;

/// <summary>
/// Rock Ridge images xorriso wrote, compared with what the Linux kernel's isofs driver
/// read from them (see <c>ReferenceVectors/README.md</c>): POSIX names, symbolic links,
/// directories relocated past ECMA-119's depth limit, and zisofs-compressed files.
/// </summary>
/// <remarks>
/// The reader used to take only the first NM entry of a name, list a symbolic link as an
/// empty file, show a relocated directory under <c>rr_moved</c> and an empty file where it
/// belongs, and return zisofs files still compressed.
/// </remarks>
[TestFixture]
public sealed class IsoRockRidgeReferenceTests {

  private static MemoryStream Image(string name) {
    using var resource = typeof(IsoRockRidgeReferenceTests).Assembly.GetManifestResourceStream("IsoVectors." + name)
      ?? throw new FileNotFoundException($"embedded reference vector {name} is missing");
    using var gzip = new GZipStream(resource, CompressionMode.Decompress);
    var image = new MemoryStream();
    gzip.CopyTo(image);
    image.Position = 0;
    return image;
  }

  private sealed record KernelView(
    HashSet<string> Directories, Dictionary<string, string> Links, Dictionary<string, string> Files);

  private static KernelView Manifest(string image) {
    using var resource = typeof(IsoRockRidgeReferenceTests).Assembly.GetManifestResourceStream($"IsoVectors.manifest-{image}.txt")!;
    using var text = new StreamReader(resource);
    var dirs = new HashSet<string>(StringComparer.Ordinal);
    var links = new Dictionary<string, string>(StringComparer.Ordinal);
    var files = new Dictionary<string, string>(StringComparer.Ordinal);
    while (text.ReadLine() is { } line) {
      if (line.Length < 3) continue;
      if (line[1] == ' ' && line[0] is 'd' or 'l' or 'f') {
        var bar = line.LastIndexOf('|');
        var path = line[2..bar];
        if (line[0] == 'd') dirs.Add(path);
        else if (line[0] == 'l') links[path] = line[(bar + 1)..];
      } else {
        var space = line.IndexOf(' ');
        files[line[(space + 1)..]] = line[..space];
      }
    }
    return new KernelView(dirs, links, files);
  }

  [TestCase("rr")]
  [TestCase("rr-moved")]
  [Category("HappyPath")]
  public void GivenARockRidgeImage_WhenListedAndExtracted_ThenItMatchesTheKernelsView(string image) {
    using var stream = Image(image + ".iso.gz");
    using var reader = new IsoReader(stream);
    var expected = Manifest(image);

    var dirs = reader.Entries.Where(e => e.IsDirectory).Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
    var links = reader.Entries.Where(e => e.IsSymlink).ToDictionary(e => e.Name, e => e.LinkTarget!, StringComparer.Ordinal);
    var files = reader.Entries.Where(e => !e.IsDirectory && !e.IsSymlink)
      .ToDictionary(e => e.Name, e => Convert.ToHexStringLower(SHA256.HashData(reader.Extract(e))), StringComparer.Ordinal);

    Assert.Multiple(() => {
      Assert.That(dirs, Is.EquivalentTo(expected.Directories), "directories");
      Assert.That(links, Is.EquivalentTo(expected.Links), "symbolic links and their targets");
      Assert.That(files, Is.EquivalentTo(expected.Files), "file contents");
    });
  }

  [Test, Category("HappyPath")]
  public void GivenAJolietImageWithZisofsFiles_WhenReadThroughJoliet_ThenTheFilesAreDecompressed() {
    using var stream = Image("rr-joliet.iso.gz");
    using var reader = new IsoReader(stream);
    var expected = Manifest("rr-joliet");
    foreach (var name in new[] { "big.txt", "zeros.bin", "noise.bin", "sub/small.txt" }) {
      var entry = reader.Entries.Single(e => e.Name == name);
      Assert.That(Convert.ToHexStringLower(SHA256.HashData(reader.Extract(entry))), Is.EqualTo(expected.Files[name]), name);
    }
  }

  [Test, Category("HappyPath")]
  public void GivenAZisofsFile_WhenListed_ThenItReportsItsUncompressedLength() {
    using var stream = Image("rr.iso.gz");
    var entries = new IsoFormatDescriptor().List(stream, null);
    var big = entries.Single(e => e.Name == "big.txt");
    Assert.Multiple(() => {
      Assert.That(big.Method, Is.EqualTo("zisofs"));
      Assert.That(big.CompressedSize, Is.LessThan(big.OriginalSize));
    });
  }

  [Test, Category("Exception")]
  public void GivenACorruptZisofsBlock_WhenExtracted_ThenItFailsInsteadOfReturningGarbage() {
    using var stream = Image("rr.iso.gz");
    var bytes = stream.ToArray();
    using var reader = new IsoReader(new MemoryStream(bytes));
    var big = reader.Entries.Single(e => e.Name == "big.txt");
    var offset = (int)IsoReader.Segments(big)[0].PhysicalOffset;
    // The first block starts right after the header and the block-pointer table.
    var firstBlock = BitConverter.ToInt32(bytes, offset + 16);
    bytes[offset + firstBlock] = 0x00; // no longer a zlib header
    using var damaged = new IsoReader(new MemoryStream(bytes));
    Assert.Throws<InvalidDataException>(() => damaged.Extract(damaged.Entries.Single(e => e.Name == "big.txt")));
  }

  [Test, Category("HappyPath")]
  public void GivenASymbolicLink_WhenExtracted_ThenTheContentIsItsTarget() {
    using var stream = Image("rr.iso.gz");
    using var reader = new IsoReader(stream);
    var link = reader.Entries.Single(e => e.Name == "rel-link");
    Assert.That(Encoding.UTF8.GetString(reader.Extract(link)), Is.EqualTo("sub/small.txt"));
  }
}
