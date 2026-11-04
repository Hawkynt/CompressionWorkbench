using System.Diagnostics;
using System.Reflection;
using Compression.Registry;
using FileFormat.RpgMakerMvAsset;

namespace Compression.Tests.RpgMakerMvAsset;

/// <summary>
/// RPG Maker MV/MZ encrypted assets. Oracle: Petschko's RPG-Maker-MV-Decrypter (MIT), whose
/// <c>Decrypter.js</c> encrypted the <c>tile/tone.rpgmv*</c> fixtures from FFmpeg-generated media with
/// key <see cref="Key"/>, driven by <c>oracle.js</c>. See ReferenceVectors/README.md.
/// </summary>
[TestFixture]
public class RpgMakerMvAssetTests {

  private const string Key = "1a2b3c4d5e6f708192a3b4c5d6e7f809";

  private static byte[] Vector(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"RpgMakerMvVectors.{name}")
      ?? throw new FileNotFoundException($"Embedded RPG Maker MV vector '{name}' is missing from the test assembly.");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  private static byte[] ExtractSingle(byte[] asset, string? password, out string name) {
    var dir = Path.Combine(Path.GetTempPath(), $"cwb_rpgmv_{Guid.NewGuid():N}");
    try {
      new RpgMakerMvAssetFormatDescriptor().Extract(new MemoryStream(asset), dir, password, null);
      var file = Directory.GetFiles(dir).Single();
      name = Path.GetFileName(file);
      return File.ReadAllBytes(file);
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
  }

  [TestCase("tile.rpgmvp", "tile.png", "decrypted.png")]
  [TestCase("tone.rpgmvo", "tone.ogg", "decrypted.ogg")]
  [TestCase("tone.rpgmvm", "tone.m4a", "decrypted.m4a")]
  [Category("ThirdPartyVector")]
  public void Given_OracleEncryptedAsset_When_ExtractedWithKey_Then_Original(string asset, string original, string entry) {
    Assert.That(ExtractSingle(Vector(asset), Key, out var name), Is.EqualTo(Vector(original)));
    Assert.That(name, Is.EqualTo(entry));
  }

  [TestCase("tile.rpgmvp", "tile.png")]
  [TestCase("tone.rpgmvo", "tone.ogg")]
  [Category("ThirdPartyVector")]
  public void Given_PngOrOggAsset_When_ExtractedWithoutKey_Then_OriginalAndKeyRecovered(string asset, string original) {
    Assert.That(ExtractSingle(Vector(asset), null, out _), Is.EqualTo(Vector(original)));
    var key = FileFormat.RpgMakerMvAsset.RpgMakerMvAsset.RecoverKey(Vector(asset));
    Assert.That(key is null ? null : FileFormat.RpgMakerMvAsset.RpgMakerMvAsset.FormatKey(key), Is.EqualTo(Key));
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_M4aAssetWithoutKey_When_Listed_Then_EncryptedAndExtractRefused() {
    var asset = Vector("tone.rpgmvm");
    var entry = new RpgMakerMvAssetFormatDescriptor().List(new MemoryStream(asset), null).Single();
    Assert.That((entry.Name, entry.IsEncrypted, entry.OriginalSize), Is.EqualTo(("decrypted.bin", true, (long)Vector("tone.m4a").Length)));
    Assert.Throws<InvalidOperationException>(() => ExtractSingle(asset, null, out _));
  }

  [TestCase("tile.png", "tile.rpgmvp")]
  [TestCase("tone.ogg", "tone.rpgmvo")]
  [TestCase("tone.m4a", "tone.rpgmvm")]
  [Category("ThirdPartyVector")]
  public void Given_OriginalMedia_When_WeEncrypt_Then_ByteIdenticalToOracle(string original, string asset) {
    using var ms = new MemoryStream();
    new RpgMakerMvAssetFormatDescriptor().Create(ms, [ArchiveInputInfo.InMemory(original, Vector(original))],
      new FormatCreateOptions { Password = Key });
    Assert.That(ms.ToArray(), Is.EqualTo(Vector(asset)));
  }

  [TestCase(".rpgmvp")]
  [TestCase(".png_")]
  [TestCase(".rpgmvo")]
  [Category("HappyPath")]
  public void Given_AssetPath_When_Detected_Then_RpgMakerMvAsset(string extension) {
    var path = Path.Combine(Path.GetTempPath(), $"cwb_rpgmv_{Guid.NewGuid():N}{extension}");
    File.WriteAllBytes(path, Vector("tile.rpgmvp"));
    try {
      Assert.That(Compression.Lib.FormatDetector.Detect(path).ToString(), Is.EqualTo("RpgMakerMvAsset"));
    } finally { File.Delete(path); }
  }

  [Test, Category("RoundTrip")]
  public void Given_ArbitraryBytes_When_EncryptedAndDecrypted_Then_Identical() {
    var data = Enumerable.Range(0, 100).Select(i => (byte)(i * 3)).ToArray();
    using var ms = new MemoryStream();
    new RpgMakerMvAssetFormatDescriptor().Create(ms, [ArchiveInputInfo.InMemory("x.bin", data)], new FormatCreateOptions { Password = Key });
    Assert.That(ExtractSingle(ms.ToArray(), Key, out var name), Is.EqualTo(data));
    Assert.That(name, Is.EqualTo("decrypted.bin"));
  }

  [TestCase(0)]
  [TestCase(5)]
  [TestCase(16)]
  [Category("Boundary")]
  public void Given_ContentNoLongerThanTheKey_When_RoundTripped_Then_Identical(int length) {
    var data = Enumerable.Range(0, length).Select(i => (byte)(0xF0 - i)).ToArray();
    var key = Convert.FromHexString(Key);
    var asset = FileFormat.RpgMakerMvAsset.RpgMakerMvAsset.Encrypt(data, key);
    Assert.That(asset.Length, Is.EqualTo(16 + length));
    Assert.That(FileFormat.RpgMakerMvAsset.RpgMakerMvAsset.Decrypt(asset, key), Is.EqualTo(data));
    Assert.That(FileFormat.RpgMakerMvAsset.RpgMakerMvAsset.RecoverKey(asset), Is.Null);
  }

  [Test, Category("EdgeCase")]
  public void Given_WrongHeader_When_Listed_Then_InvalidData() {
    var asset = Vector("tile.rpgmvp");
    asset[10] = 2; // version byte
    Assert.Throws<InvalidDataException>(() => new RpgMakerMvAssetFormatDescriptor().List(new MemoryStream(asset), null));
  }

  [Test, Category("EdgeCase")]
  public void Given_DamagedOggPage_When_KeyRecovered_Then_NoneInsteadOfAGuess() {
    var asset = Vector("tone.rpgmvo");
    asset[16 + 30] ^= 0xFF; // inside the first page, past the encrypted prefix: its CRC no longer matches
    Assert.That(FileFormat.RpgMakerMvAsset.RpgMakerMvAsset.RecoverKey(asset), Is.Null);
  }

  [TestCase("1a2b")]
  [TestCase("zz2b3c4d5e6f708192a3b4c5d6e7f809")]
  [Category("EdgeCase")]
  public void Given_MalformedKey_When_Created_Then_Refused(string key) {
    Assert.Throws<ArgumentException>(() => new RpgMakerMvAssetFormatDescriptor().Create(new MemoryStream(),
      [ArchiveInputInfo.InMemory("a.png", [1, 2, 3])], new FormatCreateOptions { Password = key }));
  }

  [Test, Category("EdgeCase")]
  public void Given_NoKeyOrTwoFiles_When_Created_Then_Refused() {
    var d = new RpgMakerMvAssetFormatDescriptor();
    Assert.Throws<ArgumentException>(() => d.Create(new MemoryStream(), [ArchiveInputInfo.InMemory("a.png", [1])], new FormatCreateOptions()));
    Assert.Throws<ArgumentException>(() => d.Create(new MemoryStream(),
      [ArchiveInputInfo.InMemory("a.png", [1]), ArchiveInputInfo.InMemory("b.png", [2])], new FormatCreateOptions { Password = Key }));
  }

  // ── Petschko's decrypter as the oracle for what we write ─────────────

  private static string? Node() {
    var exe = OperatingSystem.IsWindows() ? "node.exe" : "node";
    return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
      .Where(d => d.Length > 0).Select(d => Path.Combine(d.Trim(), exe)).FirstOrDefault(File.Exists);
  }

  [TestCase("tone.ogg")]
  [TestCase("tile.png")]
  [Category("EndToEnd")]
  public void Given_OurAsset_When_DecryptedByPetschko_Then_Original(string original) {
    var node = Node();
    if (node == null) Assert.Ignore("node not on PATH.");
    var dir = Path.Combine(Path.GetTempPath(), $"cwb_rpgmv_{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    try {
      foreach (var f in new[] { "oracle.js", "Decrypter.js", "ErrorException.js" })
        File.WriteAllBytes(Path.Combine(dir, f), Vector(f));
      var ours = Path.Combine(dir, "ours.rpgmv");
      using (var fs = File.Create(ours))
        new RpgMakerMvAssetFormatDescriptor().Create(fs, [ArchiveInputInfo.InMemory(original, Vector(original))],
          new FormatCreateOptions { Password = Key });
      var back = Path.Combine(dir, "back.bin");
      var psi = new ProcessStartInfo(node, $"\"{Path.Combine(dir, "oracle.js")}\" \"{dir}\" decrypt {Key} \"{ours}\" \"{back}\"") {
        RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false,
      };
      using var p = Process.Start(psi)!;
      var errors = p.StandardError.ReadToEnd();
      p.StandardOutput.ReadToEnd();
      p.WaitForExit();
      Assert.That(p.ExitCode, Is.Zero, errors);
      Assert.That(File.ReadAllBytes(back), Is.EqualTo(Vector(original)));
    } finally { Directory.Delete(dir, true); }
  }
}
