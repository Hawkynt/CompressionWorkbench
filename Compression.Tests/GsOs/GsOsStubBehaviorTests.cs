#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileSystem.GsOs;

namespace Compression.Tests.GsOs;

/// <summary>
/// Pins the stub-tier surface for <see cref="GsOsFormatDescriptor"/>. The
/// Apple IIgs GS/OS 2IMG wrapper is a header-only container that delegates to
/// the inner ProDOS / HFS / DOS 3.3 volume — we parse the 64-byte header and
/// surface the embedded volume opaque (no walk). These tests prevent silent
/// capability creep (CanCreate/CanModify) and stop the opaque-blob entry
/// shape from drifting.
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
  public void Stub_DescriptorHonestlyAdvertisesCapabilities_AndOpaqueEntries() {
    var d = new GsOsFormatDescriptor();

    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.False,
      "GS/OS 2IMG is stub-tier (delegating wrapper) — must not advertise CanCreate.");
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False,
      "GS/OS 2IMG is stub-tier (delegating wrapper) — must not advertise CanModify.");

    var image = BuildMagicOnly();
    using var ms = new MemoryStream(image, writable: false);
    var entries = d.List(ms, null);

    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Is.EquivalentTo(new[] { "gsos-prodos-volume.po" }),
      "GS/OS minimal-image surface must be exactly the documented opaque inner-volume entry.");

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
  public void Stub_DoesNotAdvertiseWriteCapability() {
    var d = new GsOsFormatDescriptor();
    var description = d.Description.ToLowerInvariant();
    Assert.That(
      description.Contains("stub") || description.Contains("opaque")
      || description.Contains("skeleton") || description.Contains("detection"),
      Is.True,
      $"GS/OS Description must honestly flag its stub/detection-only/opaque status. Got: '{d.Description}'.");
  }
}
