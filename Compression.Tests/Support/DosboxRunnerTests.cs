#pragma warning disable CS1591
using Compression.Tests.Support;

namespace Compression.Tests.Support;

/// <summary>
/// Light-touch tests for the DOSBox-X runner. We do not actually launch the
/// emulator from these tests — that path is exercised by
/// <c>ExternalFsInteropTests.DoubleSpace_OurImage_DosboxXChkdsk</c>, which
/// gates on real prerequisites and skips cleanly when they are missing.
/// </summary>
[TestFixture]
public class DosboxRunnerTests {
  [Test]
  public void DosboxXAvailable_DoesNotThrow() {
    // Whether dosbox-x is installed or not, the discovery probe must not
    // crash — it only swallows expected errors.
    Assert.DoesNotThrow(() => {
      var _ = DosboxRunner.DosboxXAvailable;
    });
  }

  [Test]
  public void RunWithConf_SkipsWhenDosboxMissing() {
    // Behaviour contract: when DOSBox-X is unavailable we never spawn a
    // process — we return the sentinel so the caller can Assert.Ignore.
    if (DosboxRunner.DosboxXAvailable)
      Assert.Ignore("dosbox-x is installed on this host; this test verifies the missing-tool path only.");

    var result = DosboxRunner.RunWithConf("[autoexec]\nEXIT\n");
    Assert.That(result.ExitCode, Is.EqualTo(DosboxRunner.ExitCode_DosboxMissing));
    Assert.That(result.StdErr, Does.Contain("dosbox-x"));
  }

  [Test]
  public void Sentinels_AreDistinct() {
    Assert.That(DosboxRunner.ExitCode_DosboxMissing, Is.Not.EqualTo(DosboxRunner.ExitCode_TimedOut));
  }
}
