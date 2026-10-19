using System;
using System.IO;
using System.Runtime.InteropServices;
using Compression.Analysis.ExternalTools;
using NUnit.Framework;

namespace Compression.Tests.Analysis;

/// <summary>Where the external reference tools the interop tests drive are looked for.</summary>
[TestFixture]
[NonParallelizable]
public sealed class ToolDiscoveryTests {
  private string _root = null!;

  [SetUp]
  public void SetUp() {
    this._root = Path.Combine(Path.GetTempPath(), "cwb-tools-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._root);
  }

  [TearDown]
  public void TearDown() {
    Environment.SetEnvironmentVariable("CWB_TOOL_7Z", null);
    ToolDiscovery.InvalidateCache();
    try { Directory.Delete(this._root, recursive: true); } catch { }
  }

  private string Touch(params string[] parts) {
    var path = Path.Combine([this._root, .. parts]);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllBytes(path, []);
    return path;
  }

  [Test]
  public void GivenAnOverrideVariable_WhenTheToolIsLookedUp_ThenItWinsOverEverythingElse() {
    var custom = this.Touch("custom", "7z.exe");
    Environment.SetEnvironmentVariable("CWB_TOOL_7Z", custom);
    ToolDiscovery.InvalidateCache();

    Assert.That(ToolDiscovery.GetToolPath("7z"), Is.EqualTo(custom));
  }

  [Test]
  public void GivenAnOverrideNamingAMissingFile_WhenTheToolIsLookedUp_ThenItIsIgnored() {
    Environment.SetEnvironmentVariable("CWB_TOOL_7Z", Path.Combine(this._root, "nowhere", "7z.exe"));
    ToolDiscovery.InvalidateCache();

    Assert.That(ToolDiscovery.GetToolPath("7z"), Is.Not.EqualTo(Path.Combine(this._root, "nowhere", "7z.exe")));
  }

  [Test]
  public void GivenAPortableAppsFolder_WhenSevenZipIsLookedUp_ThenTheBuildForThisProcessorIsChosen() {
    var x86 = this.Touch("PortableApps", "7-ZipPortable", "App", "7-Zip", "7z.exe");
    var x64 = this.Touch("PortableApps", "7-ZipPortable", "App", "7-Zip64", "7z.exe");
    var arm64 = this.Touch("PortableApps", "7-ZipPortable", "App", "7-ZipARM64", "7z.exe");

    var expected = RuntimeInformation.ProcessArchitecture switch {
      System.Runtime.InteropServices.Architecture.X64 => x64,
      System.Runtime.InteropServices.Architecture.Arm64 => arm64,
      _ => x86,
    };
    Assert.That(ToolDiscovery.FindPortableSevenZip(Path.Combine(this._root, "PortableApps")), Is.EqualTo(expected));
  }

  [Test]
  public void GivenAPortableAppsFolderWithOnlyThe32BitBuild_WhenSevenZipIsLookedUp_ThenThatOneIsUsed() {
    var x86 = this.Touch("PortableApps", "7-ZipPortable", "App", "7-Zip", "7z.exe");

    Assert.That(ToolDiscovery.FindPortableSevenZip(Path.Combine(this._root, "PortableApps")), Is.EqualTo(x86));
  }

  [Test]
  public void GivenAPortableAppsFolderWithoutSevenZip_WhenLookedUp_ThenThereIsNone()
    => Assert.That(ToolDiscovery.FindPortableSevenZip(this._root), Is.Null);
}
