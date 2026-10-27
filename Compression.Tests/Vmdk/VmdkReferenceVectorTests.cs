using System.IO.Compression;
using System.Security.Cryptography;
using FileFormat.Vmdk;

namespace Compression.Tests.Vmdk;

/// <summary>
/// The guest disk of VMDKs that qemu-img and qemu-io wrote, compared with what
/// <c>qemu-img convert -O raw</c> made of the same files (see <c>ReferenceVectors/README.md</c>).
/// </summary>
/// <remarks>
/// Before <see cref="VmdkVirtualDisk" />, a streamOptimized disk read as zeros (its grain
/// directory lives in the footer) or as raw deflate data, a descriptor-only disk read as
/// the descriptor text, a delta disk read its unallocated grains as zeros instead of the
/// parent's, and a zeroed grain-table entry read the sparse header as data.
/// </remarks>
[TestFixture]
public sealed class VmdkReferenceVectorTests {

  private string _dir = null!;

  [OneTimeSetUp]
  public void Unpack() {
    this._dir = Path.Combine(Path.GetTempPath(), "cwb_vmdk_vectors_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._dir);
    var assembly = typeof(VmdkReferenceVectorTests).Assembly;
    foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("VmdkVectors.", StringComparison.Ordinal))) {
      using var resource = assembly.GetManifestResourceStream(name)!;
      var file = name["VmdkVectors.".Length..];
      if (file.EndsWith(".gz", StringComparison.Ordinal)) {
        using var gzip = new GZipStream(resource, CompressionMode.Decompress);
        using var output = File.Create(Path.Combine(this._dir, file[..^3]));
        gzip.CopyTo(output);
      } else {
        using var output = File.Create(Path.Combine(this._dir, file));
        resource.CopyTo(output);
      }
    }
  }

  [OneTimeTearDown]
  public void Cleanup() {
    try { Directory.Delete(this._dir, true); } catch { /* best effort */ }
  }

  private Dictionary<string, (string Sha256, long Size)> Manifest() =>
    File.ReadAllLines(Path.Combine(this._dir, "manifest.txt"))
      .Where(l => l.Length > 0)
      .Select(l => l.Split(' ', 3))
      .ToDictionary(p => p[2], p => (p[0], long.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture)));

  private static (string Sha256, long Size) Digest(Stream disk) {
    using var sha = SHA256.Create();
    var buffer = new byte[1 << 20];
    long total = 0;
    disk.Position = 0;
    int n;
    while ((n = disk.Read(buffer, 0, buffer.Length)) > 0) {
      sha.TransformBlock(buffer, 0, n, null, 0);
      total += n;
    }
    sha.TransformFinalBlock([], 0, 0);
    return (Convert.ToHexStringLower(sha.Hash!), total);
  }

  [TestCase("stream.vmdk", Description = "streamOptimized: compressed grains, grain directory in the footer")]
  [TestCase("sparse.vmdk", Description = "monolithicSparse")]
  [TestCase("flat.vmdk", Description = "monolithicFlat: descriptor plus a flat extent file")]
  [TestCase("delta.vmdk", Description = "delta disk over base.vmdk")]
  [TestCase("zeroed.vmdk", Description = "zeroed grain-table entries")]
  [TestCase("split.vmdk", Description = "twoGbMaxExtentSparse: 3 GiB in two sparse extents")]
  [Category("HappyPath")]
  public void GivenAQemuWrittenVmdk_WhenTheGuestDiskIsRead_ThenItMatchesQemuImgConvert(string image) {
    var expected = this.Manifest()[image];
    using var file = File.OpenRead(Path.Combine(this._dir, image));
    using var disk = VmdkVirtualDisk.Open(file);
    Assert.That(disk, Is.Not.Null);
    Assert.That(Digest(disk!), Is.EqualTo(expected));
  }

  [TestCase("stream.vmdk")]
  [TestCase("sparse.vmdk")]
  [TestCase("zeroed.vmdk")]
  [Category("HappyPath")]
  public void GivenASingleFileVmdk_WhenExtractedThroughTheReader_ThenItMatchesQemuImgConvert(string image) {
    var expected = this.Manifest()[image];
    using var file = new MemoryStream(File.ReadAllBytes(Path.Combine(this._dir, image)));
    using var reader = new VmdkReader(file);
    var bytes = reader.Extract(reader.Entries[0]);
    Assert.That((Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.LongLength), Is.EqualTo(expected));
  }

  [Test, Category("Exception")]
  public void GivenAStreamOptimizedVmdk_WhenOpenedForInPlaceEditing_ThenItIsDeclined() {
    using var file = new MemoryStream(File.ReadAllBytes(Path.Combine(this._dir, "stream.vmdk")));
    Assert.That(VmdkStream.TryOpen(file), Is.Null, "editing grains in place would corrupt compressed grains");
  }

  [Test, Category("Exception")]
  public void GivenADeltaVmdk_WhenOpenedForInPlaceEditing_ThenItIsDeclined() {
    using var file = new MemoryStream(File.ReadAllBytes(Path.Combine(this._dir, "delta.vmdk")));
    Assert.That(VmdkStream.TryOpen(file), Is.Null);
  }

  [Test, Category("Exception")]
  public void GivenADeltaVmdkWithoutAPath_WhenRead_ThenItIsRefusedRatherThanZeroFilled() {
    using var file = new MemoryStream(File.ReadAllBytes(Path.Combine(this._dir, "delta.vmdk")));
    Assert.Throws<NotSupportedException>(() => VmdkVirtualDisk.Open(file));
    file.Position = 0;
    using var reader = new VmdkReader(file);
    Assert.Throws<NotSupportedException>(() => reader.Extract(reader.Entries[0]));
  }

  [Test, Category("Exception")]
  public void GivenADeltaVmdkWhoseParentIsMissing_WhenRead_ThenItIsRefused() {
    var lonely = Path.Combine(this._dir, "lonely");
    Directory.CreateDirectory(lonely);
    File.Copy(Path.Combine(this._dir, "delta.vmdk"), Path.Combine(lonely, "delta.vmdk"), true);
    using var file = File.OpenRead(Path.Combine(lonely, "delta.vmdk"));
    Assert.Throws<InvalidDataException>(() => VmdkVirtualDisk.Open(file));
  }

  [Test, Category("Exception")]
  public void GivenADeltaVmdkWhoseParentChanged_WhenRead_ThenItIsRefused() {
    var changed = Path.Combine(this._dir, "changed");
    Directory.CreateDirectory(changed);
    File.Copy(Path.Combine(this._dir, "delta.vmdk"), Path.Combine(changed, "delta.vmdk"), true);
    // VmdkWriter's images carry CID fffffffe, not the CID qemu-img recorded as the
    // delta's parentCID, so one stands in for a parent rewritten after the snapshot.
    var other = new VmdkWriter();
    other.SetDiskData(new byte[8 << 20]);
    File.WriteAllBytes(Path.Combine(changed, "base.vmdk"), other.Build());
    using var file = File.OpenRead(Path.Combine(changed, "delta.vmdk"));
    Assert.Throws<InvalidDataException>(() => VmdkVirtualDisk.Open(file));
  }

  [Test, Category("Exception")]
  public void GivenASplitDescriptorWithAMissingExtent_WhenRead_ThenItIsRefused() {
    var partial = Path.Combine(this._dir, "partial");
    Directory.CreateDirectory(partial);
    File.Copy(Path.Combine(this._dir, "split.vmdk"), Path.Combine(partial, "split.vmdk"), true);
    File.Copy(Path.Combine(this._dir, "split-s001.vmdk"), Path.Combine(partial, "split-s001.vmdk"), true);
    using var file = File.OpenRead(Path.Combine(partial, "split.vmdk"));
    Assert.Throws<InvalidDataException>(() => VmdkVirtualDisk.Open(file));
  }

  [Test, Category("Exception")]
  public void GivenADescriptorOnlyVmdkFromMemory_WhenExtracted_ThenTheDescriptorTextIsNotReturnedAsTheDisk() {
    using var file = new MemoryStream(File.ReadAllBytes(Path.Combine(this._dir, "flat.vmdk")));
    using var reader = new VmdkReader(file);
    Assert.Throws<NotSupportedException>(() => reader.Extract(reader.Entries[0]));
  }

  [Test, Category("Exception")]
  public void GivenAVmdkWhoseCompressedGrainIsCorrupt_WhenRead_ThenItFailsInsteadOfReturningGarbage() {
    var bytes = File.ReadAllBytes(Path.Combine(this._dir, "stream.vmdk"));
    // Grains start after the header and embedded descriptor; damage the first marker's LBA.
    var marker = FindFirstGrainMarker(bytes);
    bytes[marker] ^= 0x01;
    using var disk = VmdkVirtualDisk.Open(new MemoryStream(bytes))!;
    Assert.Throws<InvalidDataException>(() => Digest(disk));
  }

  /// <summary>The first sector whose 12-byte prefix is a grain marker for LBA 0 with a non-zero size.</summary>
  private static int FindFirstGrainMarker(byte[] image) {
    for (var at = 512; at + 12 < image.Length; at += 512)
      if (BitConverter.ToUInt64(image, at) == 0 && BitConverter.ToUInt32(image, at + 8) is > 0 and < 70000 && image[at + 12] == 0x78)
        return at;
    throw new InvalidOperationException("no grain marker found");
  }
}
