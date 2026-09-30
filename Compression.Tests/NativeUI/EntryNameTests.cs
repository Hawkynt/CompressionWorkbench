using Compression.NativeUI.Editing;
using NUnit.Framework;

namespace Compression.Tests.NativeUI;

/// <summary>What a user may type as a new name, on each platform's rules.</summary>
[TestFixture]
public sealed class EntryNameTests {
  [TestCase("report.txt")]
  [TestCase("no extension")]
  [TestCase(".hidden")]
  [TestCase("ünïcödé 名前.txt")]
  [TestCase("CONSOLE.txt", Description = "only the exact device names are reserved")]
  [TestCase("a")]
  public void GivenAnOrdinaryName_WhenValidated_ThenItIsAcceptedAsTyped(string typed) {
    Assert.That(EntryName.Validate(typed, windows: true), Is.EqualTo(((string?)typed, (string?)null)));
    Assert.That(EntryName.Validate(typed, windows: false), Is.EqualTo(((string?)typed, (string?)null)));
  }

  [Test]
  public void GivenBlanksAroundAName_WhenValidated_ThenTheyAreDropped()
    => Assert.That(EntryName.Validate("  report.txt\t", windows: false).Name, Is.EqualTo("report.txt"));

  [TestCase(null)]
  [TestCase("")]
  [TestCase("   ")]
  [TestCase(".")]
  [TestCase("..")]
  [TestCase("a/b")]
  [TestCase("a\\b")]
  [TestCase("tab\there")]
  [TestCase("nul\0byte")]
  public void GivenANameNoPlatformAccepts_WhenValidated_ThenItIsRefusedWithAReason(string? typed) {
    foreach (var windows in new[] { true, false }) {
      var (name, error) = EntryName.Validate(typed, windows);
      Assert.That(name, Is.Null);
      Assert.That(error, Is.Not.Empty);
    }
  }

  [Test]
  public void GivenTheLongestAllowedName_WhenValidated_ThenItIsAcceptedAndOneMoreIsNot() {
    Assert.That(EntryName.Validate(new string('a', EntryName.MaxLength), windows: false).Error, Is.Null);
    Assert.That(EntryName.Validate(new string('a', EntryName.MaxLength + 1), windows: false).Error, Does.Contain("255"));
  }

  [TestCase("what?")]
  [TestCase("a:b")]
  [TestCase("star*")]
  [TestCase("\"quoted\"")]
  [TestCase("pipe|")]
  [TestCase("<angle>")]
  [TestCase("trailing.")]
  [TestCase("CON")]
  [TestCase("con.txt")]
  [TestCase("Lpt9.log")]
  [TestCase("COM1 .txt")]
  public void GivenANameOnlyWindowsRefuses_WhenValidated_ThenItDependsOnThePlatform(string typed) {
    Assert.That(EntryName.Validate(typed, windows: true).Error, Is.Not.Null, "Windows");
    Assert.That(EntryName.Validate(typed, windows: false).Error, Is.Null, "POSIX");
  }
}
