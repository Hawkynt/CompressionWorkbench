#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Stacker;

namespace Compression.Tests.Stacker;

/// <summary>
/// Pins the stub-tier surface for <see cref="StackerFormatDescriptor"/>. Stacker
/// CVF is a proprietary compressed-wrapper from Stac Electronics (MS-DOS) — the
/// inner LZS compression and FAT layout are not surfaced; only the SCB header is
/// parsed and the wrapped inner volume is exposed opaque. These tests prevent
/// silent capability creep (CanCreate/CanModify) and stop the opaque-blob entry
/// shape from drifting.
/// </summary>
[TestFixture]
public class StackerStubBehaviorTests {

  private static byte[] BuildMagicOnly() {
    var image = new byte[4096];
    // "STK" + version 3 (Stacker 3.x).
    image[0] = 0x53; image[1] = 0x54; image[2] = 0x4B; image[3] = 0x03;
    // Inner boot sector offset = 1 → inner data starts at byte 512.
    image[12] = 0x01;
    return image;
  }

  [Test, Category("Stub")]
  public void Stub_DescriptorHonestlyAdvertisesCapabilities_AndOpaqueEntries() {
    var d = new StackerFormatDescriptor();

    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.False,
      "Stacker is stub-tier (proprietary LZS wrapper) — must not advertise CanCreate.");
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False,
      "Stacker is stub-tier (proprietary LZS wrapper) — must not advertise CanModify.");

    var image = BuildMagicOnly();
    using var ms = new MemoryStream(image, writable: false);
    var entries = d.List(ms, null);

    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Is.EquivalentTo(new[] { "stacker-volume.bin" }),
      "Stacker minimal-image surface must be exactly the documented opaque inner-volume entry.");

    // Extract the opaque blob and confirm it round-trips the inner-sector region.
    var outDir = Path.Combine(Path.GetTempPath(), "StackerStub_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outDir);
    try {
      using var ms2 = new MemoryStream(image, writable: false);
      d.Extract(ms2, outDir, password: null, files: null);
      var blobPath = Path.Combine(outDir, "stacker-volume.bin");
      Assert.That(File.Exists(blobPath), Is.True, "Extract must produce stacker-volume.bin.");
      var roundTrip = File.ReadAllBytes(blobPath);
      // Inner volume begins at InnerBootSectorOffset*512 = 1*512 = 512.
      var expected = image.AsSpan(512).ToArray();
      Assert.That(roundTrip, Is.EqualTo(expected),
        "stacker-volume.bin must round-trip the inner-sector byte range exactly.");
    } finally {
      Directory.Delete(outDir, recursive: true);
    }
  }

  [Test, Category("Stub")]
  public void Stub_DoesNotAdvertiseWriteCapability() {
    var d = new StackerFormatDescriptor();
    var description = d.Description.ToLowerInvariant();
    Assert.That(
      description.Contains("stub") || description.Contains("opaque")
      || description.Contains("skeleton") || description.Contains("detection"),
      Is.True,
      $"Stacker Description must honestly flag its stub/detection-only/opaque status. Got: '{d.Description}'.");
  }
}
