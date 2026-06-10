#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileSystem.GsOs;

namespace Compression.Tests.GsOs;

/// <summary>
/// Pins the capability surface for <see cref="GsOsFormatDescriptor"/>. The
/// Apple IIgs GS/OS 2IMG wrapper was promoted from stub-tier to a real
/// R/W descriptor (CanCreate via <see cref="GsOsWriter"/>, CanModify via
/// <see cref="GsOsInPlaceModifier"/>) that emits a 2IMG-wrapped ProDOS
/// volume and rebuilds the inner payload through ProDosWriter/ProDosReader
/// on mutation. These tests lock the new capability advertisement and
/// preserve the inner-volume List/Extract shape: when the embedded format
/// is ProDOS-ordered (image_format = 1) the descriptor walks the inner
/// ProDOS volume; when it isn't, it falls back to the opaque blob entry.
/// </summary>
[TestFixture]
public class GsOsStubBehaviorTests {

  private static byte[] BuildMagicOnlyDos33() {
    // Build a non-ProDOS-ordered 2IMG (image_format = 0 = DOS 3.3) so the
    // descriptor falls back to the legacy opaque-blob entry path.
    var content = new byte[256];
    for (var i = 0; i < content.Length; i++) content[i] = (byte)(i ^ 0x33);
    var img = new byte[64 + content.Length];
    Encoding.ASCII.GetBytes("2IMG").CopyTo(img.AsSpan(0, 4));
    Encoding.ASCII.GetBytes("XGS!").CopyTo(img.AsSpan(4, 4));
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(8, 2), 64);  // header size
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(10, 2), 1);   // version
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(12, 4), 0);   // image format = DOS 3.3
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(16, 4), 0);   // flags
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(20, 4), 1);   // data block count
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(24, 4), 64);  // data offset
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(28, 4), (uint)content.Length);
    content.CopyTo(img.AsSpan(64));
    return img;
  }

  [Test, Category("Spec")]
  public void Descriptor_AdvertisesRwCapabilities() {
    var d = new GsOsFormatDescriptor();

    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True,
      "GS/OS 2IMG advertises CanCreate via GsOsWriter (2IMG-wrapped ProDOS emitter).");
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True,
      "GS/OS 2IMG advertises CanModify via GsOsInPlaceModifier.");
    Assert.That(d, Is.InstanceOf<IArchiveCreatable>());
    Assert.That(d, Is.InstanceOf<IArchiveModifiable>());
  }

  [Test, Category("Spec")]
  public void Descriptor_NonProDosPayload_FallsBackToOpaqueEntry() {
    var d = new GsOsFormatDescriptor();
    var image = BuildMagicOnlyDos33();
    using var ms = new MemoryStream(image, writable: false);
    var entries = d.List(ms, null);

    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Is.EquivalentTo(new[] { "gsos-dos33-image.dsk" }),
      "Non-ProDOS-ordered payloads must still surface their opaque inner-volume entry " +
      "(name driven by the 2IMG image_format field).");

    var outDir = Path.Combine(Path.GetTempPath(), "GsOsStub_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outDir);
    try {
      using var ms2 = new MemoryStream(image, writable: false);
      d.Extract(ms2, outDir, password: null, files: null);
      var blobPath = Path.Combine(outDir, "gsos-dos33-image.dsk");
      Assert.That(File.Exists(blobPath), Is.True);
      var roundTrip = File.ReadAllBytes(blobPath);
      var expected = image.AsSpan(64).ToArray();
      Assert.That(roundTrip, Is.EqualTo(expected),
        "opaque-blob entry must round-trip the embedded-volume byte range exactly.");
    } finally {
      Directory.Delete(outDir, recursive: true);
    }
  }

  [Test, Category("Spec")]
  public void Descriptor_DescriptionMentionsRwSurface() {
    var d = new GsOsFormatDescriptor();
    var description = d.Description.ToLowerInvariant();
    Assert.That(
      description.Contains("prodos") || description.Contains("2img"),
      Is.True,
      $"GS/OS Description must mention the ProDOS / 2IMG surface it implements. Got: '{d.Description}'.");
  }
}
