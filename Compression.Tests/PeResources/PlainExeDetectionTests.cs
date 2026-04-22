#pragma warning disable CS1591
using Compression.Lib;

namespace Compression.Tests.PeResources;

[TestFixture]
public class PlainExeDetectionTests {

  [Test, Category("RealWorld")]
  public void PlainWindowsExe_DetectsAsPeResources() {
    // Any Windows system EXE with no installer trailer should surface via PeResources.
    string[] candidates = [
      @"C:\Windows\System32\notepad.exe",
      @"C:\Windows\System32\calc.exe",
      @"C:\Windows\explorer.exe",
    ];
    var path = candidates.FirstOrDefault(File.Exists);
    if (path == null) Assert.Ignore("No system EXE available on this platform");

    var format = FormatDetector.Detect(path!);
    Assert.That(format, Is.EqualTo(FormatDetector.Format.PeResources),
      $"{Path.GetFileName(path)} should route to PeResources so embedded icons / version info / manifest surface as entries");
  }

  [Test, Category("RealWorld")]
  public void PlainWindowsExe_ListsResources() {
    string[] candidates = [
      @"C:\Windows\System32\notepad.exe",
      @"C:\Windows\System32\user32.dll",
    ];
    var path = candidates.FirstOrDefault(File.Exists);
    if (path == null) Assert.Ignore("No suitable PE file available");

    using var fs = File.OpenRead(path!);
    var desc = new FileFormat.PeResources.PeResourcesFormatDescriptor();
    var entries = desc.List(fs, null);
    Assert.That(entries, Is.Not.Empty,
      "A standard Windows PE file should have at least one embedded resource (icon / manifest / version).");
  }
}
