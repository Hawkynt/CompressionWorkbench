#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileSystem.GsOs;

namespace Compression.Tests.GsOs;

/// <summary>
/// Pins the read-path surface for <see cref="GsOsFormatDescriptor"/>. The
/// reader still surfaces the embedded ProDOS / HFS / DOS 3.3 volume as an
/// opaque entry — downstream callers route the .po payload through
/// FileSystem.ProDos for a full hierarchical walk. Promoted from stub to
/// WORM via <see cref="GsOsWriter"/>; tests still pin that CanModify is
/// not advertised (rewriting the inner volume requires recomputing 2IMG
/// header offsets, which only Create handles correctly).
/// </summary>
[TestFixture]
public class GsOsStubBehaviorTests {

  private static byte[] BuildMagicOnly() {
    var content = new byte[256];
    for (var i = 0; i < content.Length; i++) content[i] = (byte)(i ^ 0x33);
    var img = new byte[64 + content.Length];
    Encoding.ASCII.GetBytes("2IMG").CopyTo(img.AsSpan(0, 4));
    Encoding.ASCII.GetBytes("XGS!").CopyTo(img.AsSpan(4, 4));
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(8, 2), 64);  // header size
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(10, 2), 1);   // version
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(12, 4), 1);   // image format = ProDOS order
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(16, 4), 0);   // flags
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(20, 4), 1);   // data block count
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(24, 4), 64);  // data offset
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(28, 4), (uint)content.Length);
    content.CopyTo(img.AsSpan(64));
    return img;
  }

  [Test, Category("Stub")]
  public void Descriptor_HonestlyAdvertisesWormCapabilities_AndOpaqueReadEntries() {
    var d = new GsOsFormatDescriptor();

    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True,
      "GS/OS 2IMG promoted to WORM — must advertise CanCreate.");
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False,
      "GS/OS 2IMG is WORM-tier (emit-only) — must not advertise CanModify; rewrites recompute the 2IMG header.");

    var image = BuildMagicOnly();
    using var ms = new MemoryStream(image, writable: false);
    var entries = d.List(ms, null);

    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Is.EquivalentTo(new[] { "gsos-prodos-volume.po" }),
      "GS/OS read path surfaces the documented opaque inner-volume entry.");

    var outDir = Path.Combine(Path.GetTempPath(), "GsOsStub_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outDir);
    try {
      using var ms2 = new MemoryStream(image, writable: false);
      d.Extract(ms2, outDir, password: null, files: null);
      var blobPath = Path.Combine(outDir, "gsos-prodos-volume.po");
      Assert.That(File.Exists(blobPath), Is.True, "Extract must produce gsos-prodos-volume.po.");
      var roundTrip = File.ReadAllBytes(blobPath);
      var expected = image.AsSpan(64).ToArray();
      Assert.That(roundTrip, Is.EqualTo(expected),
        "gsos-prodos-volume.po must round-trip the embedded-volume byte range exactly.");
    } finally {
      Directory.Delete(outDir, recursive: true);
    }
  }

  [Test, Category("Stub")]
  public void Description_HonestlyFlagsWormTier() {
    var d = new GsOsFormatDescriptor();
    var description = d.Description.ToLowerInvariant();
    Assert.That(
      description.Contains("worm") || description.Contains("opaque") || description.Contains("emit"),
      Is.True,
      $"GS/OS Description must honestly flag its WORM / opaque-read-path status. Got: '{d.Description}'.");
  }
}
