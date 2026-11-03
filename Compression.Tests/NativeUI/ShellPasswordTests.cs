using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Compression.Lib;
using Compression.NativeUI;
using Compression.NativeUI.ViewModels;
using Compression.NativeUI.Views;
using Compression.Registry;
using Compression.Tests.Rar;
using NUnit.Framework;

namespace Compression.Tests.NativeUI;

/// <summary>
/// An encrypted archive asks for its password when the shell opens or extracts it, asks again
/// after a wrong one, and keeps the right one for every later read; the create dialog offers
/// RAR's volume size.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class ShellPasswordTests {
  private string _root = null!;
  private MainViewModel _model = null!;
  private readonly List<(string Path, bool Retry)> _prompts = [];

  [SetUp]
  public void SetUp() {
    this._root = Path.Combine(Path.GetTempPath(), "cwb-shellpw-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._root);
    UserSettings.PathOverride = Path.Combine(this._root, "settings.json");
    FormatRegistration.EnsureInitialized();
    this._model = new MainViewModel();
    this._prompts.Clear();
  }

  [TearDown]
  public void TearDown() {
    UserSettings.PathOverride = null;
    try { Directory.Delete(this._root, recursive: true); } catch { }
  }

  private string Fixture(string name) {
    var path = Path.Combine(this._root, name);
    File.Copy(RarConformanceSet.PathOf(RarConformanceSet.Modern, name), path);
    return path;
  }

  private void Answer(params string?[] answers) {
    var queue = new Queue<string?>(answers);
    this._model.PasswordPrompt = (path, retry) => {
      this._prompts.Add((path, retry));
      return queue.Count > 0 ? queue.Dequeue() : null;
    };
  }

  [Test]
  public void Given_AnArchiveWithEncryptedNames_When_Opened_Then_ThePasswordIsAskedForAgainAfterAWrongOne() {
    var path = this.Fixture("r5-enc-hp.rar");
    this.Answer("wrong", "secret");
    this._model.Open(path);
    Assert.That(this._prompts.Select(p => p.Retry), Is.EqualTo(new[] { false, true }));
    Assert.That(this._model.ArchivePassword, Is.EqualTo("secret"));
    Assert.That(this._model.HasArchive, Is.True);
  }

  [Test]
  public void Given_AnEncryptedArchive_When_ThePromptIsCancelled_Then_NoPasswordIsKept() {
    var path = this.Fixture("r4-enc-hp.rar");
    this.Answer((string?)null);
    this._model.Open(path);
    Assert.That(this._prompts, Has.Count.EqualTo(1));
    Assert.That(this._model.ArchivePassword, Is.Null);
  }

  [Test]
  public void Given_EncryptedEntries_When_ReadThroughWithPassword_Then_TheGivenPasswordUnlocksThem() {
    var path = this.Fixture("r5-enc.rar");
    this.Answer("secret");
    var bytes = this._model.WithPassword(path, pw => ArchiveOperations.ExtractEntry(path, "text.txt", pw));
    Assert.That(bytes, Has.Length.EqualTo(20000));
    Assert.That(this._model.ArchivePassword, Is.EqualTo("secret"));
  }

  [TestCase("The archive is encrypted; a password is required.", true)]
  [TestCase("The password is wrong for this RAR archive.", true)]
  [TestCase("CRC-32 mismatch for RAR entry 'x'.", false)]
  public void Given_AnError_When_Classified_Then_OnlyPasswordProblemsPrompt(string message, bool expected) {
    Assert.That(MainViewModel.IsPasswordProblem(new InvalidDataException(message)), Is.EqualTo(expected));
    Assert.That(MainViewModel.IsPasswordProblem(new InvalidOperationException(message)), Is.EqualTo(expected));
  }

  [Test]
  public void Given_TheCreateDialogForRar_When_Shown_Then_ItOffersAVolumeSize() {
    var options = new CreateOptionsViewModel(FormatDetector.Format.Rar);
    Assert.That(options.FormatSpecificOptions.Select(o => o.Key), Does.Contain(FormatOptionKeys.VolumeSize));
  }
}
