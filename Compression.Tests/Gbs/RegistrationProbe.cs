using Compression.Registry;

namespace Compression.Tests.Gbs;

[TestFixture]
public class ChiptuneRegistrationProbe {
  [Test, Category("Detection")]
  public void AllFiveDescriptors_AreRegistered() {
    var ids = FormatRegistry.All.Select(d => d.Id).ToHashSet();
    Assert.That(ids, Does.Contain("Ktx2"));
    Assert.That(ids, Does.Contain("Vgm"));
    Assert.That(ids, Does.Contain("Sid"));
    Assert.That(ids, Does.Contain("Nsf"));
    Assert.That(ids, Does.Contain("Gbs"));
  }
}
