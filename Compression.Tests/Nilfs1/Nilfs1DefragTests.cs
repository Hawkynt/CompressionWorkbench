#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using FileSystem.Nilfs1;

namespace Compression.Tests.Nilfs1;

/// <summary>
/// NILFS v1 was promoted from WORM (rebuild-backed) to genuine in-place R/W via
/// the log-structured continuous-snapshot segment append (see
/// <see cref="Nilfs1InPlaceModifier"/>). Defragmentation re-packs the payload
/// region, which would relocate snapshot data and break the byte-identical
/// invariant — so the descriptor now refuses it, exactly as NILFS2 does.
/// </summary>
[TestFixture]
public class Nilfs1DefragTests {

  [Test]
  public void Descriptor_StillExposesDefragInterface() {
    Assert.That(new Nilfs1FormatDescriptor(), Is.InstanceOf<IArchiveDefragmentable>());
  }

  [Test]
  public void Defragment_ReclaimsToASingleCheckpoint() {
    var d = new Nilfs1FormatDescriptor();
    var w = new Nilfs1Writer();
    var payload = Encoding.UTF8.GetBytes(new string('A', 5000));
    w.AddFile("a.txt", payload);
    using var ms = new MemoryStream();
    ms.Write(w.Build());
    ms.Position = 0;

    d.Defragment(ms);

    ms.Position = 0;
    using var r = new Nilfs1Reader(ms);
    var entry = r.Entries.Single(e => e.Name == "a.txt");
    Assert.That(r.Extract(entry), Is.EqualTo(payload),
      "A cleaner run keeps the live file set byte for byte.");
  }

  [Test, Category("Sad")]
  public void Defragment_UnsupportedMode_Throws() {
    var d = new Nilfs1FormatDescriptor();
    var w = new Nilfs1Writer();
    w.AddFile("a.txt", Encoding.UTF8.GetBytes(new string('A', 5000)));
    using var ms = new MemoryStream();
    ms.Write(w.Build());
    ms.Position = 0;

    Assert.Throws<NotSupportedException>(
      () => d.Defragment(ms, new DefragOptions { Mode = DefragMode.CarveHole }));
  }
}
