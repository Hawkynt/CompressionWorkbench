#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Mfs1;

namespace Compression.Tests.Mfs1;

/// <summary>
/// Pins the stub-tier surface for <see cref="Mfs1FormatDescriptor"/>. Acorn MFS-1
/// has only weak two-byte heuristic magic and detection is extension-led —
/// these tests prevent silent capability creep (CanCreate/CanModify) and stop the
/// opaque-blob entry shape from drifting.
/// </summary>
[TestFixture]
public class Mfs1StubBehaviorTests {

  private static byte[] BuildMagicOnly() {
    var image = new byte[4096];
    // weak boot pattern 0x00 0x80 at offsets 0-1
    image[0] = 0x00; image[1] = 0x80;
    return image;
  }

  [Test, Category("Stub")]
  public void Stub_DescriptorHonestlyAdvertisesCapabilities_AndOpaqueEntries() {
    var d = new Mfs1FormatDescriptor();

    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.False,
      "MFS-1 is stub-tier (weak magic, no walker) — must not advertise CanCreate.");
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False,
      "MFS-1 is stub-tier (weak magic, no walker) — must not advertise CanModify.");

    var image = BuildMagicOnly();
    using var ms = new MemoryStream(image, writable: false);
    var entries = d.List(ms, null);

    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Is.EquivalentTo(new[] { "FULL.mfs", "metadata.ini" }),
      "MFS-1 minimal-image surface must be exactly the documented opaque pair: FULL.mfs + metadata.ini.");

    var outDir = Path.Combine(Path.GetTempPath(), "Mfs1Stub_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outDir);
    try {
      using var ms2 = new MemoryStream(image, writable: false);
      d.Extract(ms2, outDir, password: null, files: null);
      var fullPath = Path.Combine(outDir, "FULL.mfs");
      Assert.That(File.Exists(fullPath), Is.True, "Extract must produce FULL.mfs.");
      var roundTrip = File.ReadAllBytes(fullPath);
      Assert.That(roundTrip, Is.EqualTo(image),
        "FULL.mfs must round-trip the magic-only input byte-for-byte.");
    } finally {
      Directory.Delete(outDir, recursive: true);
    }
  }

  [Test, Category("Stub")]
  public void Stub_DoesNotAdvertiseWriteCapability() {
    var d = new Mfs1FormatDescriptor();
    var description = d.Description.ToLowerInvariant();
    Assert.That(
      description.Contains("stub") || description.Contains("opaque")
      || description.Contains("skeleton") || description.Contains("detection"),
      Is.True,
      $"MFS-1 Description must honestly flag its stub/detection-only/opaque status. Got: '{d.Description}'.");
  }
}
