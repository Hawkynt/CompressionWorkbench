#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Mfs1;

namespace Compression.Tests.Mfs1;

/// <summary>
/// Pins the capability surface for <see cref="Mfs1FormatDescriptor"/>: WORM
/// (R + Create) only — modify/defrag must NOT silently appear. Also verifies
/// the opaque-blob entries are still surfaced for magic-only inputs that have
/// no parseable catalog.
/// </summary>
[TestFixture]
public class Mfs1StubBehaviorTests {

  private static byte[] BuildMagicOnly() {
    var image = new byte[4096];
    // weak boot pattern 0x00 0x80 at offsets 0-1
    image[0] = 0x00; image[1] = 0x80;
    return image;
  }

  [Test, Category("Spec")]
  public void Descriptor_HonestlyAdvertisesCapabilities_AndOpaqueEntriesOnMagicOnly() {
    var d = new Mfs1FormatDescriptor();

    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True,
      "MFS-1 WORM — must advertise CanCreate (writer emits a real DFS-shaped catalog).");
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False,
      "MFS-1 R+Create only — must not advertise CanModify (no free-sector allocator).");
    Assert.That(d, Is.InstanceOf<IArchiveCreatable>(),
      "MFS-1 WORM — must implement IArchiveCreatable.");
    Assert.That(d, Is.Not.InstanceOf<IArchiveModifiable>(),
      "MFS-1 R+Create only — must not implement IArchiveModifiable.");

    // Magic-only image has no parseable catalog → reader produces zero entries.
    // The descriptor still surfaces the opaque FULL.mfs + metadata.ini pair.
    var image = BuildMagicOnly();
    using var ms = new MemoryStream(image, writable: false);
    var entries = d.List(ms, null);

    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Does.Contain("FULL.mfs"),
      "Even with no catalog entries, MFS-1 must surface FULL.mfs for triage.");
    Assert.That(names, Does.Contain("metadata.ini"),
      "Even with no catalog entries, MFS-1 must surface metadata.ini for triage.");

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
}
