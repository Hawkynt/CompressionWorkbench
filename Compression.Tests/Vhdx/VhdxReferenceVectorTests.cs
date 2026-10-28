using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using Compression.Core.Checksums;
using FileFormat.Vhdx;

namespace Compression.Tests.Vhdx;

/// <summary>
/// VHDX files from keramics' test corpus — a differencing disk over its parent, and a
/// dynamic disk — read against what libvhdi (<c>vhdiexport</c>) and <c>qemu-img convert</c>
/// made of them (see <c>ReferenceVectors/README.md</c>), plus the refusals that replace
/// the old zero-filled reads.
/// </summary>
[TestFixture]
public sealed class VhdxReferenceVectorTests {

  private string _dir = null!;

  [OneTimeSetUp]
  public void Unpack() {
    this._dir = Path.Combine(Path.GetTempPath(), "cwb_vhdx_vectors_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._dir);
    var assembly = typeof(VhdxReferenceVectorTests).Assembly;
    foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("VhdxVectors.", StringComparison.Ordinal))) {
      using var resource = assembly.GetManifestResourceStream(name)!;
      var file = name["VhdxVectors.".Length..];
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
      .Where(l => l.Length > 0 && l[0] != '#')
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

  [TestCase("ntfs-differential.vhdx", Description = "differencing disk: NOT_PRESENT blocks and sector bitmaps defer to ntfs-parent.vhdx")]
  [TestCase("ntfs-parent.vhdx")]
  [TestCase("ntfs-dynamic.vhdx")]
  [Category("HappyPath")]
  public void GivenAWindowsWrittenVhdx_WhenTheGuestDiskIsRead_ThenItMatchesTheReference(string image) {
    var expected = this.Manifest()[image];
    using var file = File.OpenRead(Path.Combine(this._dir, image));
    using var disk = VhdxStream.OpenReadOnly(file, out var reason);
    Assert.That(disk, Is.Not.Null, reason);
    Assert.That(Digest(disk!), Is.EqualTo(expected));
  }

  [Test, Category("Exception")]
  public void GivenADifferencingVhdx_WhenOpenedForInPlaceEditing_ThenItIsDeclined() {
    using var file = File.OpenRead(Path.Combine(this._dir, "ntfs-differential.vhdx"));
    Assert.That(VhdxStream.TryOpen(file), Is.Null);
  }

  [Test, Category("Exception")]
  public void GivenADifferencingVhdxFromMemory_WhenRead_ThenItIsRefusedRatherThanZeroFilled() {
    using var file = new MemoryStream(File.ReadAllBytes(Path.Combine(this._dir, "ntfs-differential.vhdx")));
    Assert.That(VhdxStream.OpenReadOnly(file, out var reason), Is.Null);
    Assert.That(reason, Does.Contain("parent"));
  }

  [Test, Category("Exception")]
  public void GivenADifferencingVhdxWithoutItsParent_WhenRead_ThenItIsRefused() {
    var lonely = Path.Combine(this._dir, "lonely");
    Directory.CreateDirectory(lonely);
    File.Copy(Path.Combine(this._dir, "ntfs-differential.vhdx"), Path.Combine(lonely, "ntfs-differential.vhdx"), true);
    using var file = File.OpenRead(Path.Combine(lonely, "ntfs-differential.vhdx"));
    Assert.That(VhdxStream.OpenReadOnly(file, out var reason), Is.Null);
    Assert.That(reason, Does.Contain("not found"));
  }

  [Test, Category("Exception")]
  public void GivenAParentModifiedAfterTheChild_WhenRead_ThenItIsRefused() {
    var changed = Path.Combine(this._dir, "changed");
    Directory.CreateDirectory(changed);
    File.Copy(Path.Combine(this._dir, "ntfs-differential.vhdx"), Path.Combine(changed, "ntfs-differential.vhdx"), true);
    var parent = File.ReadAllBytes(Path.Combine(this._dir, "ntfs-parent.vhdx"));
    // A new DataWriteGuid in both headers is what writing to the parent would leave.
    foreach (var header in new[] { 0x10000, 0x20000 }) {
      Guid.NewGuid().TryWriteBytes(parent.AsSpan(header + 32, 16));
      ResealHeader(parent, header);
    }
    File.WriteAllBytes(Path.Combine(changed, "ntfs-parent.vhdx"), parent);
    using var file = File.OpenRead(Path.Combine(changed, "ntfs-differential.vhdx"));
    Assert.That(VhdxStream.OpenReadOnly(file, out var reason), Is.Null);
    Assert.That(reason, Does.Contain("DataWriteGuid"));
  }

  [Test, Category("Exception")]
  public void GivenAVhdxWithAnUnreplayedLog_WhenRead_ThenItIsRefusedAndListingSaysWhy() {
    var image = File.ReadAllBytes(Path.Combine(this._dir, "ntfs-dynamic.vhdx"));
    foreach (var header in new[] { 0x10000, 0x20000 }) {
      Guid.NewGuid().TryWriteBytes(image.AsSpan(header + 48, 16)); // LogGuid
      ResealHeader(image, header);
    }
    using (var stream = new MemoryStream(image, writable: false)) {
      Assert.That(VhdxStream.OpenReadOnly(stream, out var reason), Is.Null);
      Assert.That(reason, Does.Contain("log"));
      Assert.That(VhdxStream.TryOpen(stream), Is.Null);
    }
    using var listed = new MemoryStream(image, writable: false);
    var entries = new VhdxFormatDescriptor().List(listed, null);
    Assert.That(entries.Select(e => e.Name), Does.Contain("metadata.ini"));
  }

  [Test, Category("Exception")]
  public void GivenAPartiallyPresentBlockInADynamicVhdx_WhenRead_ThenItIsRejected() {
    var writer = new VhdxWriter();
    var guest = new byte[4 << 20];
    guest.AsSpan().Fill(0x5A);
    writer.SetDiskData(guest);
    var image = writer.Build();
    var bat = BatOffset(image);
    var entry = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(bat));
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(bat), (entry & ~7UL) | 7UL); // PARTIALLY_PRESENT
    using var disk = VhdxStream.OpenReadOnly(new MemoryStream(image, writable: false), out _)!;
    Assert.Throws<InvalidDataException>(() => Digest(disk));
  }

  private static void ResealHeader(byte[] image, int header) {
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(header + 4), 0);
    var crc = Crc32.Compute(image.AsSpan(header, 4096), Crc32.Castagnoli);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(header + 4), crc);
  }

  private static int BatOffset(byte[] image) {
    var count = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(0x30008));
    var batGuid = new Guid("2DC27766-F623-4200-9D64-115E9BFD4A08");
    for (var i = 0; i < count; i++) {
      var e = 0x30010 + i * 32;
      if (new Guid(image.AsSpan(e, 16)) == batGuid)
        return checked((int)BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(e + 16)));
    }
    throw new InvalidOperationException("no BAT region");
  }
}
