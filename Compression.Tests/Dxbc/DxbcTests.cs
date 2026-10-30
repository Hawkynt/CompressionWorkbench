using System.Diagnostics;
using System.Reflection;
using Compression.Registry;
using FileFormat.Dxbc;

namespace Compression.Tests.Dxbc;

/// <summary>
/// DXBC shader containers. Oracle: Microsoft's fxc (Windows SDK 10.0.26100), which wrote every
/// fixture, verifies the container checksum before it disassembles, and itself edits containers
/// (<c>-setprivate</c>, <c>-Qstrip_reflect</c>) — our edits must reproduce its output byte for byte.
/// See ReferenceVectors/README.md.
/// </summary>
[TestFixture]
public class DxbcTests {

  private static byte[] Vector(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"DxbcVectors.{name}")
      ?? throw new FileNotFoundException($"Embedded DXBC vector '{name}' is missing from the test assembly.");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  private static readonly byte[] Private = "CompressionWorkbench private data"u8.ToArray();

  [TestCase("fxc-tint-ps50.dxbc", "RDEF,ISGN,OSGN,SHEX,STAT")]
  [TestCase("fxc-tint-ps40l91.dxbc", "Aon9,SHDR,STAT,RDEF,ISGN,OSGN")]
  [TestCase("fxc-tint-ps50-setprivate.dxbc", "RDEF,ISGN,OSGN,SHEX,STAT,PRIV")]
  [TestCase("fxc-tint-ps50-stripreflect.dxbc", "ISGN,OSGN,SHEX")]
  [Category("ThirdPartyVector")]
  public void Given_FxcOutput_When_Read_Then_ChecksumVerifiesAndChunksAreListed(string file, string codes) {
    var chunks = DxbcContainer.Read(Vector(file));
    Assert.That(string.Join(',', chunks.Select(c => c.FourCc)), Is.EqualTo(codes));
  }

  [TestCase("fxc-tint-ps50.dxbc")]
  [TestCase("fxc-tint-ps40l91.dxbc")]
  [TestCase("fxc-tint-ps50-setprivate.dxbc")]
  [TestCase("fxc-tint-ps50-stripreflect.dxbc")]
  [Category("RoundTrip")]
  public void Given_FxcOutput_When_ChunksAreWrittenBack_Then_ByteIdenticalIncludingTheChecksum(string file) {
    var original = Vector(file);
    Assert.That(DxbcContainer.Write(DxbcContainer.Read(original)), Is.EqualTo(original));
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_FxcContainer_When_PrivateDataIsAdded_Then_ByteIdenticalToFxcSetPrivate() {
    using var ms = new MemoryStream();
    ms.Write(Vector("fxc-tint-ps50.dxbc"));
    ((IArchiveModifiable)new DxbcFormatDescriptor()).Add(ms, [ArchiveInputInfo.InMemory("PRIV", Private)]);
    Assert.That(ms.ToArray(), Is.EqualTo(Vector("fxc-tint-ps50-setprivate.dxbc")));
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_FxcContainer_When_ReflectionChunksAreRemoved_Then_ByteIdenticalToFxcStripReflect() {
    using var ms = new MemoryStream();
    ms.Write(Vector("fxc-tint-ps50.dxbc"));
    ((IArchiveModifiable)new DxbcFormatDescriptor()).Remove(ms, ["RDEF", "STAT"]);
    Assert.That(ms.ToArray(), Is.EqualTo(Vector("fxc-tint-ps50-stripreflect.dxbc")));
  }

  [Test, Category("RoundTrip")]
  public void Given_ExtractedChunks_When_CreatedAgainInOrder_Then_TheOriginalContainerComesBack() {
    var original = Vector("fxc-tint-ps40l91.dxbc");
    var d = new DxbcFormatDescriptor();
    var inputs = DxbcContainer.Read(original).Select(c => ArchiveInputInfo.InMemory(c.FourCc + ".bin", c.Data)).ToList();
    using var ms = new MemoryStream();
    d.Create(ms, inputs, new FormatCreateOptions());
    Assert.That(ms.ToArray(), Is.EqualTo(original));
  }

  [Test, Category("EdgeCase")]
  public void Given_RepeatedChunkCodes_When_Listed_Then_NamesAreUniqueAndCreateAcceptsThem() {
    var image = DxbcContainer.Write([new("PRIV", [1]), new("PRIV", [2, 3])]);
    var d = new DxbcFormatDescriptor();
    var names = d.List(new MemoryStream(image), null).Select(e => e.Name).ToList();
    Assert.That(names, Is.EqualTo(new[] { "PRIV", "PRIV~2" }));
    using var ms = new MemoryStream();
    d.Create(ms, [ArchiveInputInfo.InMemory("PRIV", [1]), ArchiveInputInfo.InMemory("PRIV~2", [2, 3])], new FormatCreateOptions());
    Assert.That(ms.ToArray(), Is.EqualTo(image));
  }

  [Test, Category("Boundary")]
  public void Given_NoChunks_When_Written_Then_HeaderOnlyContainerThatReadsBackEmpty() {
    var image = DxbcContainer.Write([]);
    Assert.That(image, Has.Length.EqualTo(DxbcContainer.HeaderSize));
    Assert.That(DxbcContainer.Read(image), Is.Empty);
  }

  // Container sizes around the checksum's one-row/two-row final-block switch (tail of 55, 56, 63, 0 bytes after offset 20).
  [TestCase(55)]
  [TestCase(56)]
  [TestCase(63)]
  [TestCase(64)]
  [Category("Boundary")]
  public void Given_HashedLengthAroundThePaddingSwitch_When_WrittenAndRead_Then_ChecksumAgrees(int hashedTail) {
    // hashed bytes = total - 20 = 12 (rest of header) + 4 (offset) + 8 (chunk header) + payload
    var payload = new byte[Math.Max(0, hashedTail + 64 - 24)];
    var image = DxbcContainer.Write([new("PRIV", payload)]);
    Assert.That((image.Length - 20) % 64, Is.EqualTo(hashedTail % 64));
    Assert.That(DxbcContainer.Read(image), Has.Count.EqualTo(1));
  }

  [Test, Category("EdgeCase")]
  public void Given_CorruptedBody_When_Read_Then_ChecksumMismatch() {
    var bytes = Vector("fxc-tint-ps50.dxbc");
    bytes[0x200] ^= 1;
    var ex = Assert.Throws<InvalidDataException>(() => DxbcContainer.Read(bytes));
    Assert.That(ex!.Message, Does.Contain("checksum"));
  }

  [Test, Category("EdgeCase")]
  public void Given_ChunkOffsetPastTheEnd_When_Read_Then_InvalidData() {
    var bytes = Vector("fxc-tint-ps50.dxbc");
    bytes[32] = 0xF0; bytes[33] = 0xFF;
    Assert.Throws<InvalidDataException>(() => DxbcContainer.Read(bytes, verifyChecksum: false));
  }

  [Test, Category("EdgeCase")]
  public void Given_DeclaredSizeLargerThanTheFile_When_Read_Then_InvalidData() {
    var bytes = Vector("fxc-tint-ps50.dxbc")[..500];
    Assert.Throws<InvalidDataException>(() => DxbcContainer.Read(bytes));
  }

  [Test, Category("EdgeCase")]
  public void Given_EntryNameThatIsNotAChunkCode_When_Created_Then_Refused() {
    Assert.Throws<ArgumentException>(() =>
      new DxbcFormatDescriptor().Create(new MemoryStream(), [ArchiveInputInfo.InMemory("shader.bin", [1])], new FormatCreateOptions()));
  }

  [Test, Category("ExternalTool")]
  public void Given_OurEditedContainer_When_FxcDisassemblesIt_Then_ItAcceptsTheChecksum() {
    var fxc = FindFxc();
    if (fxc == null) Assert.Ignore("fxc.exe (Windows SDK) not found.");
    var dir = Path.Combine(Path.GetTempPath(), "dxbc_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      using var ms = new MemoryStream();
      ms.Write(Vector("fxc-tint-ps40l91.dxbc"));
      var d = (IArchiveModifiable)new DxbcFormatDescriptor();
      d.Add(ms, [ArchiveInputInfo.InMemory("PRIV", Private)]);
      d.Remove(ms, ["STAT"]);
      var path = Path.Combine(dir, "ours.dxbc");
      File.WriteAllBytes(path, ms.ToArray());
      var psi = new ProcessStartInfo(fxc, ["-nologo", "-dumpbin", path]) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
      using var p = Process.Start(psi)!;
      var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
      p.WaitForExit();
      Assert.That(p.ExitCode, Is.Zero, output);
      Assert.That(output, Does.Contain("ps_4_0"));
    } finally {
      Directory.Delete(dir, true);
    }
  }

  private static string? FindFxc() {
    if (!OperatingSystem.IsWindows()) return null;
    var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits", "10", "bin");
    if (!Directory.Exists(root)) return null;
    return Directory.GetDirectories(root).OrderDescending()
      .Select(v => Path.Combine(v, "x64", "fxc.exe")).FirstOrDefault(File.Exists);
  }
}
