#pragma warning disable CS1591
using Compression.Tests.Support;

namespace Compression.Tests.Support;

/// <summary>
/// Pure-string assertions on the conf body — no DOSBox-X invocation, no
/// process spawning. Run on every host regardless of whether dosbox-x is
/// installed.
/// </summary>
[TestFixture]
public class DosboxConfBuilderTests {
  [Test]
  public void Build_ContainsAllRequiredSections() {
    var conf = new DosboxConfBuilder().Build();

    Assert.Multiple(() => {
      Assert.That(conf, Does.Contain("[sdl]"));
      Assert.That(conf, Does.Contain("[dosbox]"));
      Assert.That(conf, Does.Contain("[cpu]"));
      Assert.That(conf, Does.Contain("[autoexec]"));
    });
  }

  [Test]
  public void Build_DefaultsToCyclesMax() {
    var conf = new DosboxConfBuilder().Build();
    Assert.That(conf, Does.Contain("cycles=max"));
  }

  [Test]
  public void Build_OverridesCyclesWhenSpecified() {
    var conf = new DosboxConfBuilder().Cycles("50000").Build();
    Assert.That(conf, Does.Contain("cycles=50000"));
    Assert.That(conf, Does.Not.Contain("cycles=max"));
  }

  [Test]
  public void Build_AlwaysAppendsExitWhenScriptOmitsIt() {
    var conf = new DosboxConfBuilder()
      .Autoexec("DIR C:")
      .Build();

    Assert.That(conf, Does.Contain("DIR C:"));
    Assert.That(conf.TrimEnd(), Does.EndWith("EXIT"));
  }

  [Test]
  public void Build_DoesNotDoubleExitWhenScriptAlreadyExits() {
    var conf = new DosboxConfBuilder()
      .Autoexec("DIR C:", "EXIT")
      .Build();

    var exitCount = conf.Split('\n').Count(l => l.Trim().Equals("EXIT", StringComparison.OrdinalIgnoreCase));
    Assert.That(exitCount, Is.EqualTo(1));
  }

  [Test]
  public void Build_GeneratesAutoexecBlockWithImgmount() {
    var conf = new DosboxConfBuilder()
      .MountBootImageAsC(@"C:\msdos.img")
      .MountHdd(@"C:\test.cvf", "D")
      .Autoexec("DIR D:")
      .Build();

    Assert.Multiple(() => {
      Assert.That(conf, Does.Contain("imgmount c \"C:\\msdos.img\" -t hdd -fs fat"));
      Assert.That(conf, Does.Contain("imgmount D \"C:\\test.cvf\" -t hdd -fs none"));
      Assert.That(conf, Does.Contain("DIR D:"));
    });
  }

  [Test]
  public void Build_DriveLetterTrimsTrailingColon() {
    var conf = new DosboxConfBuilder()
      .MountHdd(@"C:\test.cvf", "D:")
      .Build();

    Assert.That(conf, Does.Contain("imgmount D \"C:\\test.cvf\""));
  }

  [Test]
  public void MountHdd_RejectsEmptyPath() {
    Assert.Throws<ArgumentException>(() => new DosboxConfBuilder().MountHdd("", "D"));
  }

  [Test]
  public void MountHdd_RejectsBadDriveLetter() {
    Assert.Throws<ArgumentException>(() => new DosboxConfBuilder().MountHdd(@"C:\x.img", ""));
    Assert.Throws<ArgumentException>(() => new DosboxConfBuilder().MountHdd(@"C:\x.img", "DEF"));
  }

  [Test]
  public void MountBootImageAsC_RejectsEmptyPath() {
    Assert.Throws<ArgumentException>(() => new DosboxConfBuilder().MountBootImageAsC(""));
  }

  [Test]
  public void Build_NoSoundIsRequested() {
    // Headless CI has no sound device — the conf must explicitly disable it
    // so DOSBox-X doesn't fail or stall on mixer init.
    var conf = new DosboxConfBuilder().Build();
    Assert.That(conf, Does.Contain("nosound=true"));
  }

  [Test]
  public void Build_WithoutMountsStillProducesValidAutoexec() {
    var conf = new DosboxConfBuilder()
      .Autoexec("ECHO hello", "EXIT")
      .Build();

    Assert.Multiple(() => {
      Assert.That(conf, Does.Contain("[autoexec]"));
      Assert.That(conf, Does.Contain("ECHO hello"));
      Assert.That(conf, Does.Not.Contain("imgmount"));
    });
  }

  [Test]
  public void Build_HddSizeIsEmittedWhenSpecified() {
    var conf = new DosboxConfBuilder()
      .MountHdd(@"C:\big.img", "D", sizeBytes: 512 * 63 * 16 * 1024L)
      .Build();

    // -size is a documented DOSBox-X option for imgmount.
    Assert.That(conf, Does.Contain("-size 528482304"));
  }

  [Test]
  public void Build_HddSizeOmittedByDefault() {
    var conf = new DosboxConfBuilder()
      .MountHdd(@"C:\small.img", "D")
      .Build();

    Assert.That(conf, Does.Not.Contain("-size"));
  }

  [Test]
  public void Build_MachineTypeOverride() {
    var conf = new DosboxConfBuilder().Machine("vgaonly").Build();
    Assert.That(conf, Does.Contain("machine=vgaonly"));
  }
}
