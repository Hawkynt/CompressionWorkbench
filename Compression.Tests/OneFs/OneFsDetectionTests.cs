using System.Text;
using Compression.Registry;
using FileSystem.OneFs;

namespace Compression.Tests.OneFs;

/// <summary>
/// Stage 0 acceptance gate for <see cref="OneFsFormatDescriptor"/>:
/// pins the detection magic, surface entry shape, and honest
/// "detection-only" Description.
/// </summary>
[TestFixture]
public class OneFsDetectionTests {

  private static byte[] BuildMinimal(int payloadLen = 128) {
    var image = new byte[16 + payloadLen];
    Encoding.ASCII.GetBytes("OneFS").CopyTo(image.AsSpan(0, 5));
    for (var i = 0; i < payloadLen; i++) image[16 + i] = (byte)(i & 0xFF);
    return image;
  }

  [Test, Category("HappyPath")]
  public void Detector_IdentifiesByMagic() {
    var d = new OneFsFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("OneFs"));
    Assert.That(d.Extensions, Does.Contain(".onefs"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(2));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo("OneFS"u8.ToArray()));
    Assert.That(d.MagicSignatures[1].Bytes, Is.EqualTo("ONEF"u8.ToArray()));
    Assert.That(d, Is.Not.InstanceOf<IArchiveCreatable>());
  }

  [Test, Category("HappyPath")]
  public void List_ReturnsTwoEntries() {
    var d = new OneFsFormatDescriptor();
    using var ms = new MemoryStream(BuildMinimal(payloadLen: 256));
    var entries = d.List(ms, password: null);
    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Is.EquivalentTo(new[] { "metadata.ini", "onefs-volume.bin" }));
  }

  [Test, Category("Stub")]
  public void Description_FlagsDetectionOnly() {
    var d = new OneFsFormatDescriptor();
    Assert.That(d.Description.ToLowerInvariant(), Does.Contain("detection-only"),
      $"OneFS Description must flag Stage 0 honestly. Got: '{d.Description}'.");
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.False);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False);
  }

  [Test, Category("Stub")]
  public void Description_NamesRoPromotionBlockers() {
    // Honest Stage-0 doctrine: Description must surface WHY we cannot promote to R/O.
    // Distributed/FEC-striped + no public spec are the two structural blockers; UFS
    // ancestry must NOT be advertised as a fallback path.
    var d = new OneFsFormatDescriptor();
    var desc = d.Description.ToLowerInvariant();
    Assert.That(desc, Does.Contain("distributed").Or.Contain("clustered"),
      "OneFS Description must call out the distributed/clustered architecture.");
    Assert.That(desc, Does.Contain("spec").Or.Contain("proprietary"),
      "OneFS Description must call out the lack of a public on-disk specification.");
    Assert.That(desc, Does.Contain("ufs"),
      "OneFS Description must explicitly state UFS-incompatibility so nobody routes images through UfsReader.");
  }

  [Test, Category("Stub")]
  public void Metadata_NamesRoPromotionBlockers() {
    // Stage-0 metadata.ini must enumerate the R/O-promotion blockers so end-users
    // see them without reading source. Pin the structural reasons (distributed,
    // FEC, no spec, UFS-incompatible) — not the surface phrasing.
    using var ms = new MemoryStream(BuildMinimal(payloadLen: 64));
    var reader = new OneFsReader(ms);
    var meta = reader.Entries.First(e => e.Name == "metadata.ini");
    var ini = Encoding.UTF8.GetString(meta.Data).ToLowerInvariant();
    Assert.That(ini, Does.Contain("stage=0"), "metadata.ini must pin stage=0.");
    Assert.That(ini, Does.Contain("ro_promotion=blocked"), "metadata.ini must pin ro_promotion=blocked.");
    Assert.That(ini, Does.Contain("fec"), "metadata.ini must call out FEC striping as a blocker.");
    Assert.That(ini, Does.Contain("lin tree"), "metadata.ini must call out the LIN tree being cluster-wide.");
    Assert.That(ini, Does.Contain("spec"), "metadata.ini must call out the missing public spec.");
    Assert.That(ini, Does.Contain("ufs"), "metadata.ini must call out UFS-incompatibility at the on-disk layer.");
  }
}
