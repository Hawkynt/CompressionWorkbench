#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Tfs;

namespace Compression.Tests.Tfs;

/// <summary>
/// Pins the stub-tier surface for <see cref="TfsFormatDescriptor"/>. TFS (BBN Trans-FS)
/// has no public on-disk spec so the descriptor is intentionally detection-only —
/// these tests prevent silent capability creep (CanCreate/CanModify) and stop the
/// opaque-blob entry shape from drifting.
/// </summary>
[TestFixture]
public class TfsStubBehaviorTests {

  private static byte[] BuildMagicOnly() {
    var image = new byte[4096];
    // "TFS\x01"
    image[0] = 0x54; image[1] = 0x46; image[2] = 0x53; image[3] = 0x01;
    return image;
  }

  [Test, Category("Stub")]
  public void Stub_DescriptorHonestlyAdvertisesCapabilities_AndOpaqueEntries() {
    var d = new TfsFormatDescriptor();

    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.False,
      "TFS is stub-tier (no public spec) — must not advertise CanCreate.");
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False,
      "TFS is stub-tier (no public spec) — must not advertise CanModify.");

    var image = BuildMagicOnly();
    using var ms = new MemoryStream(image, writable: false);
    var entries = d.List(ms, null);

    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Is.EquivalentTo(new[] { "FULL.tfs", "metadata.ini" }),
      "TFS minimal-image surface must be exactly the documented opaque pair: FULL.tfs + metadata.ini.");

    // Extract round-trips the opaque FULL.tfs blob byte-for-byte.
    var outDir = Path.Combine(Path.GetTempPath(), "TfsStub_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outDir);
    try {
      using var ms2 = new MemoryStream(image, writable: false);
      d.Extract(ms2, outDir, password: null, files: null);
      var fullPath = Path.Combine(outDir, "FULL.tfs");
      Assert.That(File.Exists(fullPath), Is.True, "Extract must produce FULL.tfs.");
      var roundTrip = File.ReadAllBytes(fullPath);
      Assert.That(roundTrip, Is.EqualTo(image),
        "FULL.tfs must round-trip the magic-only input byte-for-byte.");
    } finally {
      Directory.Delete(outDir, recursive: true);
    }
  }

  [Test, Category("Stub")]
  public void Stub_DoesNotAdvertiseWriteCapability() {
    var d = new TfsFormatDescriptor();
    var description = d.Description.ToLowerInvariant();
    Assert.That(
      description.Contains("stub") || description.Contains("opaque")
      || description.Contains("skeleton") || description.Contains("detection"),
      Is.True,
      $"TFS Description must honestly flag its stub/detection-only/opaque status. Got: '{d.Description}'.");
  }
}
