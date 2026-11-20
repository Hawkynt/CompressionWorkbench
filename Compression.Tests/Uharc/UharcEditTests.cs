using Compression.Registry;
using FileFormat.Uharc;

namespace Compression.Tests.Uharc;

/// <summary>
/// Editing UHARC archives by rebuilding them (docs/UHARC-ON-DISK.md, "Editing"): add, replace and
/// remove keep the version, the method byte, the password, the stub, and every kept file's time
/// stamp and attribute byte; a refused edit leaves the bytes as they were.
/// </summary>
[TestFixture]
public class UharcEditTests {

  private static readonly DateTime Stamp = new(2005, 10, 1, 12, 34, 56, DateTimeKind.Local);

  /// <summary>An archive written directly, so attribute bytes other than the default can be set.</summary>
  private static byte[] Original(int version, UharcWriter.Mode mode, string? password = null, byte[]? stub = null, int? method = null) {
    var random = new Random(version);
    var noise = new byte[4000];
    random.NextBytes(noise);
    List<UharcWriter.Item> items = [
      new("TEXT.TXT", System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 800).Select(i => $"line {i % 17} of text\r\n"))), Stamp),
      new("NOISE.BIN", noise, Stamp),
      new("SUB/READONLY.TXT", "read only\r\n"u8.ToArray(), Stamp, 0x21),
      new("SUB/DEEP/HIDDEN.BIN", [1, 2, 3], Stamp, 0x22),
      new("EMPTY.DAT", [], Stamp),
    ];
    using var output = new MemoryStream();
    UharcWriter.Write(output, items, mode, Stamp, password, stub, version: version, forcedMethod: method);
    return output.ToArray();
  }

  private static UharcReader Read(byte[] archive, string? password = null) => new(new MemoryStream(archive), password);

  private static byte[] Edited(byte[] archive, Action<Stream> edit) {
    using var stream = new MemoryStream();
    stream.Write(archive);
    edit(stream);
    return stream.ToArray();
  }

  private static IEnumerable<TestCaseData> VersionsAndModes() {
    foreach (var version in new[] { 6, 4, 3 })
      foreach (var mode in Enum.GetNames<UharcWriter.Mode>())
        yield return new TestCaseData(version, mode).SetName($"Edit_v{version}_{mode}");
    foreach (var mode in new[] { "Store", "Rle", "Lz78", "Alz" })
      yield return new TestCaseData(2, mode).SetName($"Edit_v2_{mode}");
  }

  [TestCaseSource(nameof(VersionsAndModes))]
  [Category("HappyPath"), Category("RoundTrip")]
  public void GivenAnArchive_WhenFilesAreAddedReplacedAndRemoved_ThenTheRestKeepsItsMethodTimesAndAttributes(int version, string mode) {
    var original = Original(version, Enum.Parse<UharcWriter.Mode>(mode));
    var before = Read(original);
    var replacement = "the new text\r\n"u8.ToArray();
    var edited = Edited(original, s => {
      new UharcFormatDescriptor().Add(s, [ArchiveInputInfo.InMemory("TEXT.TXT", replacement), ArchiveInputInfo.InMemory("ADDED/NEW.TXT", "new\r\n"u8.ToArray())]);
      new UharcFormatDescriptor().Remove(s, ["NOISE.BIN"]);
    });
    var after = Read(edited);
    var files = after.Entries.Where(e => !e.IsDirectory).ToDictionary(e => e.FileName, StringComparer.OrdinalIgnoreCase);
    Assert.Multiple(() => {
      Assert.That(after.Version, Is.EqualTo(version));
      Assert.That(after.Method, Is.EqualTo(before.Method));
      Assert.That(files.Keys, Is.EquivalentTo(new[] { "TEXT.TXT", "SUB/READONLY.TXT", "SUB/DEEP/HIDDEN.BIN", "EMPTY.DAT", "ADDED/NEW.TXT" }).IgnoreCase);
      Assert.That(after.Extract(files["TEXT.TXT"]), Is.EqualTo(replacement));
      Assert.That(after.Extract(files["SUB/READONLY.TXT"]), Is.EqualTo("read only\r\n"u8.ToArray()));
      Assert.That(files["SUB/READONLY.TXT"].Attributes, Is.EqualTo(0x21));
      Assert.That(files["SUB/DEEP/HIDDEN.BIN"].Attributes, Is.EqualTo(0x22));
      Assert.That(files["SUB/READONLY.TXT"].LastModified, Is.EqualTo(Stamp));
      Assert.That(files["ADDED/NEW.TXT"].Attributes, Is.EqualTo(0x20));
    });
  }

  [TestCase(6)]
  [TestCase(4)]
  [TestCase(3)]
  [Category("HappyPath")]
  public void GivenAPasswordArchive_WhenEditedWithItsPassword_ThenItStaysEnciphered(int version) {
    var edited = Edited(Original(version, UharcWriter.Mode.Lzp, "secret"),
      s => new UharcFormatDescriptor().Remove(s, ["NOISE.BIN"], new ArchiveMutationOptions { Password = "secret" }));
    Assert.That(() => Read(edited), Throws.TypeOf<InvalidOperationException>(), "no password");
    var reader = Read(edited, "secret");
    Assert.Multiple(() => {
      Assert.That(reader.IsEncrypted, Is.True);
      Assert.That(reader.Entries.Any(e => e.FileName == "NOISE.BIN"), Is.False);
      Assert.That(reader.Extract(reader.Entries.Single(e => e.FileName == "TEXT.TXT")), Has.Length.GreaterThan(0));
    });
  }

  [TestCase(6, null)]
  [TestCase(6, "wrong")]
  [TestCase(4, null)]
  [TestCase(4, "wrong")]
  [TestCase(3, null)]
  [TestCase(3, "wrong")]
  [Category("Exceptional")]
  public void GivenAPasswordArchive_WhenEditedWithoutTheRightPassword_ThenItIsRefusedAndLeftUnchanged(int version, string? password) {
    var original = Original(version, UharcWriter.Mode.Lzp, "secret");
    using var stream = new MemoryStream();
    stream.Write(original);
    Assert.That(() => new UharcFormatDescriptor().Remove(stream, ["NOISE.BIN"], new ArchiveMutationOptions { Password = password }),
      Throws.TypeOf<UnauthorizedAccessException>());
    Assert.That(stream.ToArray(), Is.EqualTo(original));
  }

  [TestCase(6)]
  [TestCase(2)]
  [Category("Exceptional"), Category("Boundary")]
  public void GivenAnArchive_WhenEveryFileIsRemoved_ThenTheEditIsRefusedAndLeftUnchanged(int version) {
    var original = Original(version, UharcWriter.Mode.Store);
    using var stream = new MemoryStream();
    stream.Write(original);
    Assert.Multiple(() => {
      Assert.That(new UharcFormatDescriptor().CanPurgeToEmpty, Is.False);
      Assert.That(() => new UharcFormatDescriptor().Remove(stream, ["TEXT.TXT", "NOISE.BIN", "SUB", "EMPTY.DAT"]), Throws.TypeOf<NotSupportedException>());
      Assert.That(stream.ToArray(), Is.EqualTo(original));
    });
  }

  [Test, Category("HappyPath")]
  public void GivenADirectory_WhenRemoved_ThenEverythingBeneathItGoes() {
    var edited = Edited(Original(6, UharcWriter.Mode.Alz), s => new UharcFormatDescriptor().Remove(s, ["sub"]));
    Assert.That(Read(edited).Entries.Select(e => e.FileName), Is.EquivalentTo(new[] { "TEXT.TXT", "NOISE.BIN", "EMPTY.DAT" }));
  }

  [Test, Category("HappyPath")]
  public void GivenASelfExtractor_WhenEdited_ThenItKeepsItsStub() {
    var stub = UharcTests.MinimalPe(0x200, 0x400);
    var original = Original(6, UharcWriter.Mode.Lzp, stub: stub);
    var edited = Edited(original, s => new UharcFormatDescriptor().Remove(s, ["NOISE.BIN"]));
    Assert.Multiple(() => {
      Assert.That(edited.AsSpan(0, stub.Length).SequenceEqual(stub), Is.True);
      Assert.That(Read(edited).Entries.Any(e => e.FileName == "NOISE.BIN"), Is.False);
    });
  }

  [TestCase(78)]
  [TestCase(79)]
  [Category("Boundary"), Category("RoundTrip")]
  public void GivenVersion03AlzMethods78And79_WhenWrittenAndEdited_ThenTheMethodStays(int method) {
    var edited = Edited(Original(3, UharcWriter.Mode.Alz, method: method), s => new UharcFormatDescriptor().Remove(s, ["NOISE.BIN"]));
    var reader = Read(edited);
    Assert.Multiple(() => {
      Assert.That(reader.Method, Is.EqualTo(method));
      Assert.That(reader.Extract(reader.Entries.Single(e => e.FileName == "SUB/READONLY.TXT")), Is.EqualTo("read only\r\n"u8.ToArray()));
    });
  }

  [TestCase(6, 87, "Alz")]
  [TestCase(6, 24, "Ppm")]
  [TestCase(4, 23, "Lzp")]
  [TestCase(3, 21, "Lzp")]
  [TestCase(3, 22, "Ppm")]
  [TestCase(3, 79, "Alz")]
  [TestCase(2, 43, "Alz")]
  [TestCase(2, 7, "Lz78")]
  [Category("Boundary")]
  public void GivenAMethodByte_WhenItsModeIsLookedUp_ThenTheVersionsBandsDecide(int version, int method, string mode)
    => Assert.That(UharcWriter.ModeOf(version, method).ToString(), Is.EqualTo(mode));

  [TestCase(6, 88)]
  [TestCase(3, 80)]
  [TestCase(2, 44)]
  [Category("Exceptional")]
  public void GivenAMethodTheVersionLacks_WhenItsModeIsLookedUp_ThenItIsRefused(int version, int method)
    => Assert.That(() => UharcWriter.ModeOf(version, method), Throws.TypeOf<NotSupportedException>());
}
