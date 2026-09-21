using Compression.NativeUI;
using NUnit.Framework;

namespace Compression.Tests.NativeUI;

/// <summary>
/// Keeps the Explorer context-menu registration and the shell's own argument dispatch in step.
/// <para>
/// These were two independent lists of strings. "Extract here (CWB)" registered a command line the
/// shell had no case for, so choosing it in Explorer launched the app, matched nothing, and opened
/// the ordinary browser — a menu entry that looked like it worked and did not.
/// </para>
/// </summary>
[TestFixture]
internal sealed class ShellVerbTests {
  [Test]
  public void GivenEveryExplorerVerb_WhenTheShellParsesIt_ThenItResolvesToARealCommand() {
    Assert.Multiple(() => {
      foreach (var verb in ShellVerbs.ExplorerVerbs)
        Assert.That(
          ShellVerbs.Parse([verb, @"C:\some\archive.zip"]),
          Is.Not.EqualTo(StartupCommand.Browse),
          $"Explorer registers \"{verb}\" but the shell does not act on it");
    });
  }

  [TestCase(ShellVerbs.ExtractHere, StartupCommand.ExtractHere)]
  [TestCase(ShellVerbs.Extract, StartupCommand.Extract)]
  [TestCase(ShellVerbs.CreateZip, StartupCommand.CreateZip)]
  [TestCase(ShellVerbs.Create7z, StartupCommand.Create7z)]
  public void GivenAVerbAndAPath_WhenParsed_ThenItIsTheExpectedCommand(string verb, StartupCommand expected)
    => Assert.That(ShellVerbs.Parse([verb, @"C:\some\archive.zip"]), Is.EqualTo(expected));

  [TestCase(ShellVerbs.Analyze)]
  [TestCase(ShellVerbs.AnalyzeSlash)]
  [TestCase(ShellVerbs.AnalyzeShort)]
  public void GivenAnAnalyzeSpelling_WhenParsed_ThenItOpensAnalysis(string verb) {
    Assert.Multiple(() => {
      Assert.That(ShellVerbs.Parse([verb]), Is.EqualTo(StartupCommand.Analyze), "with no file");
      Assert.That(ShellVerbs.Parse([verb, @"C:\some\file.bin"]), Is.EqualTo(StartupCommand.Analyze), "with a file");
    });
  }

  /// <summary>A verb that needs a path is not that verb without one, or it would swallow the path.</summary>
  [TestCase(ShellVerbs.ExtractHere)]
  [TestCase(ShellVerbs.Extract)]
  [TestCase(ShellVerbs.CreateZip)]
  [TestCase(ShellVerbs.Create7z)]
  public void GivenAVerbWithNoPath_WhenParsed_ThenItFallsBackToBrowsing(string verb)
    => Assert.That(ShellVerbs.Parse([verb]), Is.EqualTo(StartupCommand.Browse));

  [Test]
  public void GivenAScreenshotRequest_WhenParsed_ThenItIsACapture()
    => Assert.That(ShellVerbs.Parse([ShellVerbs.ScreenshotPrefix + "archive-browser"]),
      Is.EqualTo(StartupCommand.Screenshot));

  /// <summary>A bare path is a file association opening that file, not a verb.</summary>
  [Test]
  public void GivenABarePath_WhenParsed_ThenItIsBrowsing()
    => Assert.That(ShellVerbs.Parse([@"C:\some\archive.zip"]), Is.EqualTo(StartupCommand.Browse));

  [Test]
  public void GivenNoArguments_WhenParsed_ThenItIsBrowsing()
    => Assert.That(ShellVerbs.Parse([]), Is.EqualTo(StartupCommand.Browse));

  /// <summary>Every documented screenshot target has to be one the shell will actually accept.</summary>
  [Test]
  public void GivenTheDocumentedScreenshots_WhenCheckedAgainstTheShell_ThenEveryOneIsAValidTarget() {
    Assert.Multiple(() => {
      foreach (var target in new[] { "archive-browser", "analysis", "maintenance" })
        Assert.That(ScreenshotMode.Targets, Does.Contain(target),
          $"README and docs/screenshots reference {target}.png");
    });
  }
}
