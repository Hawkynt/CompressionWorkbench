#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Nwfs386;

namespace Compression.Tests.Nwfs386;

/// <summary>
/// Pins the stub-tier surface for <see cref="Nwfs386FormatDescriptor"/>. Novell
/// NetWare 386 has no public on-disk spec — these tests prevent silent capability
/// creep (CanCreate/CanModify) and stop the opaque-blob entry shape from drifting.
/// </summary>
[TestFixture]
public class Nwfs386StubBehaviorTests {

  private static byte[] BuildMagicOnly() {
    var image = new byte[4096];
    // "NetW"
    image[0] = 0x4E; image[1] = 0x65; image[2] = 0x74; image[3] = 0x57;
    return image;
  }

  [Test, Category("Stub")]
  public void Stub_DescriptorHonestlyAdvertisesCapabilities_AndOpaqueEntries() {
    var d = new Nwfs386FormatDescriptor();

    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.False,
      "NWFS386 is stub-tier (no public spec) — must not advertise CanCreate.");
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False,
      "NWFS386 is stub-tier (no public spec) — must not advertise CanModify.");

    var image = BuildMagicOnly();
    using var ms = new MemoryStream(image, writable: false);
    var entries = d.List(ms, null);

    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Is.EquivalentTo(new[] { "FULL.nwfs386", "metadata.ini" }),
      "NWFS386 minimal-image surface must be exactly the documented opaque pair: FULL.nwfs386 + metadata.ini.");

    var outDir = Path.Combine(Path.GetTempPath(), "Nwfs386Stub_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outDir);
    try {
      using var ms2 = new MemoryStream(image, writable: false);
      d.Extract(ms2, outDir, password: null, files: null);
      var fullPath = Path.Combine(outDir, "FULL.nwfs386");
      Assert.That(File.Exists(fullPath), Is.True, "Extract must produce FULL.nwfs386.");
      var roundTrip = File.ReadAllBytes(fullPath);
      Assert.That(roundTrip, Is.EqualTo(image),
        "FULL.nwfs386 must round-trip the magic-only input byte-for-byte.");
    } finally {
      Directory.Delete(outDir, recursive: true);
    }
  }

  [Test, Category("Stub")]
  public void Stub_DoesNotAdvertiseWriteCapability() {
    var d = new Nwfs386FormatDescriptor();
    var description = d.Description.ToLowerInvariant();
    Assert.That(
      description.Contains("stub") || description.Contains("opaque")
      || description.Contains("skeleton") || description.Contains("detection"),
      Is.True,
      $"NWFS386 Description must honestly flag its stub/detection-only/opaque status. Got: '{d.Description}'.");
  }
}
