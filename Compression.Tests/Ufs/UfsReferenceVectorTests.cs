#pragma warning disable CS1591
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using Compression.Lib;
using Compression.Registry;
using FileSystem.Ufs;

namespace Compression.Tests.Ufs;

/// <summary>
/// The reader against volumes third-party tools wrote: NetBSD <c>makefs</c> (UFS1 and
/// UFS2, both byte orders) and FreeBSD 8.2's <c>newfs -O2</c>. Provenance and the
/// generator are in <c>ReferenceVectors/README.md</c>.
/// </summary>
[TestFixture]
public sealed class UfsReferenceVectorTests {

  private static MemoryStream Image(string name) {
    using var resource = typeof(UfsReferenceVectorTests).Assembly
      .GetManifestResourceStream("UfsVectors." + name)
      ?? throw new FileNotFoundException($"embedded reference vector {name} is missing");
    using var gzip = new GZipStream(resource, CompressionMode.Decompress);
    var image = new MemoryStream();
    gzip.CopyTo(image);
    image.Position = 0;
    return image;
  }

  private sealed record Manifest(
    IReadOnlyDictionary<string, string> Files,
    IReadOnlyDictionary<string, string> Links,
    IReadOnlyList<string> Directories);

  private static Manifest ReadManifest() {
    using var resource = typeof(UfsReferenceVectorTests).Assembly.GetManifestResourceStream("UfsVectors.manifest.txt")!;
    using var text = new StreamReader(resource);
    var files = new Dictionary<string, string>(StringComparer.Ordinal);
    var links = new Dictionary<string, string>(StringComparer.Ordinal);
    var directories = new List<string>();
    while (text.ReadLine() is { } line) {
      if (line.Length == 0) continue;
      if (line.StartsWith("L ", StringComparison.Ordinal)) {
        var arrow = line.IndexOf(" -> ", StringComparison.Ordinal);
        links[line[2..arrow]] = line[(arrow + 4)..];
      } else if (line.StartsWith("D ", StringComparison.Ordinal))
        directories.Add(line[2..]);
      else
        files[line[65..]] = line[..64];
    }
    return new(files, links, directories);
  }

  private static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

  [TestCase("makefs-ufs1-le.img.gz", 1, false, 8192)]
  [TestCase("makefs-ufs1-be.img.gz", 1, true, 8192)]
  [TestCase("makefs-ufs2-le.img.gz", 2, false, 8192)]
  [TestCase("makefs-ufs2-be.img.gz", 2, true, 8192)]
  [Category("ThirdParty")]
  public void GivenAMakefsVolume_WhenRead_ThenEveryFileLinkAndDirectoryMatchesTheSourceTree(
      string vector, int version, bool bigEndian, long superblock) {
    using var image = Image(vector);
    using var reader = new UfsReader(image);
    var manifest = ReadManifest();

    Assert.That(reader.Version, Is.EqualTo(version));
    Assert.That(reader.IsBigEndian, Is.EqualTo(bigEndian));
    Assert.That(reader.SuperblockOffset, Is.EqualTo(superblock));

    var byName = reader.Entries.ToDictionary(e => e.Name, StringComparer.Ordinal);
    foreach (var (path, sha) in manifest.Files) {
      Assert.That(byName.TryGetValue(path, out var entry), Is.True, $"{path} missing from {vector}");
      Assert.That(Sha256(reader.Extract(entry!)), Is.EqualTo(sha), $"{path} content in {vector}");
    }
    foreach (var (path, target) in manifest.Links) {
      Assert.That(byName.TryGetValue(path, out var entry), Is.True, $"{path} missing from {vector}");
      Assert.That(entry!.IsSymlink, Is.True, path);
      Assert.That(entry.LinkTarget, Is.EqualTo(target), $"{path} target in {vector}");
    }
    foreach (var path in manifest.Directories)
      Assert.That(byName.TryGetValue(path, out var entry) && entry.IsDirectory, Is.True, $"{path} not a directory in {vector}");
    Assert.That(byName.Count, Is.EqualTo(manifest.Files.Count + manifest.Links.Count + manifest.Directories.Count),
      "the listing holds exactly the source tree");
    Assert.That(byName["hello.txt"].LastModified, Is.EqualTo(new DateTime(2024, 2, 29, 12, 34, 56, DateTimeKind.Utc)));
  }

  [Test, Category("ThirdParty")]
  public void GivenAFreeBsdNewfsUfs2Volume_WhenRead_ThenTheSuperblockAtSblockUfs2IsUsed() {
    using var image = Image("newfs-ufs2.img.gz");
    using var reader = new UfsReader(image);

    Assert.That(reader.Version, Is.EqualTo(2));
    Assert.That(reader.IsBigEndian, Is.False);
    Assert.That(reader.SuperblockOffset, Is.EqualTo(65536));
    Assert.That(reader.VolumeName, Is.EqualTo("CWBUFS2"));
    Assert.That(reader.Entries, Is.Empty, "a fresh newfs volume holds only .snap, which is not listed");
  }

  [TestCase("makefs-ufs2-le.img.gz")]
  [TestCase("makefs-ufs2-be.img.gz")]
  [TestCase("makefs-ufs1-be.img.gz")]
  [TestCase("newfs-ufs2.img.gz")]
  public void GivenAVolumeTheWriterDoesNotLayOut_WhenDetected_ThenItIsUfs(string vector) {
    using var image = Image(vector);
    var fmt = FormatDetector.DetectByMagic(image.ToArray());
    Assert.That(fmt.ToString(), Is.EqualTo("Ufs"));
  }

  [TestCase("makefs-ufs2-le.img.gz")]
  [TestCase("makefs-ufs1-be.img.gz")]
  [TestCase("newfs-ufs2.img.gz")]
  public void GivenAUfs2OrBigEndianVolume_WhenEdited_ThenEveryEditIsRefusedAndTheImageIsUntouched(string vector) {
    using var image = Image(vector);
    var before = image.ToArray();
    var d = new UfsFormatDescriptor();
    var input = ArchiveInputInfo.InMemory("x.txt", "x"u8.ToArray());

    Assert.Throws<NotSupportedException>(() => d.Add(image, [input]));
    Assert.Throws<NotSupportedException>(() => d.Remove(image, ["hello.txt"]));
    Assert.Throws<NotSupportedException>(() => d.Defragment(image));
    Assert.Throws<NotSupportedException>(() => d.WipeUnusedSpace(image));
    Assert.Throws<NotSupportedException>(() => d.MoveExtent(image, 0, 4096, 4096));
    Assert.That(image.ToArray(), Is.EqualTo(before));
  }

  [Test]
  public void GivenAUfs2SuperblockWhoseSblockLocDisagrees_WhenRead_ThenItIsNotTakenForThePrimary() {
    using var image = Image("newfs-ufs2.img.gz");
    var bytes = image.ToArray();
    // fs_sblockloc (struct fs offset 1000) claims the copy lives elsewhere: it is a backup.
    BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(65536 + 1000), 8192);
    Assert.Throws<InvalidDataException>(() => _ = new UfsReader(new MemoryStream(bytes)));
  }

  [Test]
  public void GivenAnImplausibleBlockSize_WhenRead_ThenItIsRejected() {
    using var image = Image("makefs-ufs2-le.img.gz");
    var bytes = image.ToArray();
    BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8192 + 48), 3000);
    Assert.Throws<InvalidDataException>(() => _ = new UfsReader(new MemoryStream(bytes)));
  }

  [Test]
  public void GivenAVolumeCutShort_WhenListed_ThenListingSucceedsAndALostFileRefusesToExtract() {
    using var image = Image("makefs-ufs2-le.img.gz");
    var full = image.ToArray();
    using var probe = new UfsReader(new MemoryStream(full));
    var big = probe.Entries.Single(e => e.Name == "sub/big.bin");

    // Keep the metadata, drop the tail of the data area.
    var cut = full.AsSpan(0, full.Length / 2).ToArray();
    using var reader = new UfsReader(new MemoryStream(cut));
    Assert.That(reader.Entries.Any(e => e.Name == "hello.txt"), Is.True);
    var shortBig = reader.Entries.Single(e => e.Name == "sub/big.bin");
    Assert.That(shortBig.Size, Is.EqualTo(big.Size));
    Assert.Throws<InvalidDataException>(() => reader.Extract(shortBig),
      "bytes that are not in the image must not be invented");
  }

  /// <summary>
  /// A whole FreeBSD release volume: <c>FreeBSD-14.5-RELEASE-amd64-mini-memstick.img</c>,
  /// partition <c>a</c> of the FreeBSD slice, carved out raw. Its manifest is what the
  /// Linux kernel's ufs driver (<c>ufstype=ufs2</c>, through the libguestfs appliance)
  /// copied out. Too large to check in: set <c>CWB_UFS2_FREEBSD_IMAGE</c> and
  /// <c>CWB_UFS2_FREEBSD_MANIFEST</c> to run it.
  /// </summary>
  [Test, Category("ThirdParty")]
  public void GivenAFreeBsdReleaseVolume_WhenRead_ThenEveryFileMatchesTheKernelCopy() {
    var imagePath = Environment.GetEnvironmentVariable("CWB_UFS2_FREEBSD_IMAGE");
    var manifestPath = Environment.GetEnvironmentVariable("CWB_UFS2_FREEBSD_MANIFEST");
    if (imagePath is null || manifestPath is null || !File.Exists(imagePath) || !File.Exists(manifestPath))
      Assert.Ignore("set CWB_UFS2_FREEBSD_IMAGE and CWB_UFS2_FREEBSD_MANIFEST to a carved FreeBSD UFS2 partition and its kernel manifest");

    using var fs = File.OpenRead(imagePath);
    using var reader = new UfsReader(fs);
    Assert.That(reader.Version, Is.EqualTo(2));
    Assert.That(reader.SuperblockOffset, Is.EqualTo(65536));
    var byName = reader.Entries.ToDictionary(e => e.Name, StringComparer.Ordinal);

    var files = 0;
    var links = 0;
    var mismatches = new List<string>();
    foreach (var line in File.ReadLines(manifestPath)) {
      if (line.StartsWith("L ", StringComparison.Ordinal)) {
        var arrow = line.IndexOf(" -> ", StringComparison.Ordinal);
        var path = line[4..arrow];
        if (!byName.TryGetValue(path, out var link) || link.LinkTarget != line[(arrow + 4)..]) mismatches.Add("L " + path);
        ++links;
        continue;
      }
      var sha = line[..64];
      var name = line[68..];
      if (!byName.TryGetValue(name, out var entry)) { mismatches.Add("missing " + name); continue; }
      using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
      using var sink = new HashingStream(hash);
      reader.ExtractTo(entry, sink);
      if (Convert.ToHexStringLower(hash.GetHashAndReset()) != sha) mismatches.Add("content " + name);
      ++files;
    }
    Assert.That(mismatches, Is.Empty);
    Assert.That(files, Is.GreaterThan(1000));
    Assert.That(links, Is.GreaterThan(100));
  }

  private sealed class HashingStream(IncrementalHash hash) : Stream {
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => hash.AppendData(buffer, offset, count);
    public override void Write(ReadOnlySpan<byte> buffer) => hash.AppendData(buffer);
  }
}
