using System.IO;
using Compression.NativeUI;
using NUnit.Framework;

namespace Compression.Tests.NativeUI;

/// <summary>Where the shell keeps its settings, including on a profile that has no data folder yet.</summary>
[TestFixture]
public sealed class UserSettingsTests {

  [Test]
  public void GivenADataFolder_WhenTheSettingsPathIsResolved_ThenItLiesInAFolderOfItsOwnThere() {
    var data = Path.GetTempPath();

    Assert.That(UserSettings.SettingsPathUnder(data), Is.EqualTo(Path.Combine(data, "CompressionWorkbench", "settings.json")));
  }

  [TestCase("", TestName = "GivenNoDataFolder_WhenTheSettingsPathIsResolved_ThenThereIsNoneRatherThanOneInTheWorkingFolder")]
  [TestCase("relative", TestName = "GivenARelativeDataFolder_WhenTheSettingsPathIsResolved_ThenThereIsNone")]
  public void GivenAnUnusableDataFolder_WhenTheSettingsPathIsResolved_ThenThereIsNone(string data)
    => Assert.That(UserSettings.SettingsPathUnder(data), Is.Null,
      "a platform without the folder reports an empty path; combining it would write into whatever folder the shell was started from");
}
