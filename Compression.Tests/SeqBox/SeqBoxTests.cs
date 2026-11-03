using System.Reflection;
using Compression.Registry;
using FileFormat.SeqBox;

namespace Compression.Tests.SeqBox;

/// <summary>
/// SeqBox (.sbx): containers written by the reference sbxenc (SeqBox/ReferenceVectors), our
/// writer, and the reference sbxdec as a live oracle for what we write.
/// </summary>
[TestFixture]
public class SeqBoxTests {

  private static readonly byte[] FoxText =
    System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog. ", 50)));

  private static readonly byte[] Noise = Enumerable.Range(0, 1000).Select(i => (byte)((i * 31 + 7) % 256)).ToArray();

  private static readonly long FoxTime = new DateTimeOffset(2001, 2, 3, 4, 5, 6, TimeSpan.Zero).ToUnixTimeSeconds();

  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_sbx_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(this._tmpDir, true); } catch { /* best effort */ }
  }

  // ── reference containers (sbxenc 1.0.2) ─────────────────────────────

  [TestCase("fox-v1.sbx", 1, "fox.txt")]
  [TestCase("fox-v3.sbx", 3, "fox.txt")]
  [TestCase("noise-v2.sbx", 2, "noise.bin")]
  [TestCase("noise-v4.sbx", 4, "noise.bin")]
  [Category("ThemVsUs")]
  public void GivenSbxencContainer_WhenRead_ThenMetadataAndPayloadMatchTheSource(string vector, int version, string name) {
    var p = SeqBoxContainer.Read(new MemoryStream(LoadVector(vector)));

    Assert.That(p.Version, Is.EqualTo(version));
    Assert.That(p.Metadata, Is.Not.Null);
    Assert.That(p.Metadata!.FileName, Is.EqualTo(name));
    Assert.That(p.Metadata.SbxName, Is.EqualTo(vector));
    Assert.That(p.Metadata.FileTime, Is.EqualTo(FoxTime));
    Assert.That(p.Data, Is.EqualTo(name == "fox.txt" ? FoxText : Noise));
  }

  [Test, Category("ThemVsUs")]
  public void GivenSbxencContainerWithoutMetadata_WhenRead_ThenPayloadKeepsTheLastBlocksPadding() {
    var p = SeqBoxContainer.Read(new MemoryStream(LoadVector("fox-nometa-v1.sbx")));

    Assert.That(p.Metadata, Is.Null);
    Assert.That(p.DataBlocks, Is.EqualTo(5));
    Assert.That(p.Data.AsSpan(0, FoxText.Length).ToArray(), Is.EqualTo(FoxText));
    Assert.That(p.Data.Skip(FoxText.Length), Has.All.EqualTo((byte)0x1A));
  }

  [Test, Category("Boundary")]
  public void GivenSbxencContainerOfAnEmptyFile_WhenRead_ThenOnlyTheMetadataBlockAndAnEmptyPayload() {
    var p = SeqBoxContainer.Read(new MemoryStream(LoadVector("empty-v1.sbx")));

    Assert.That(p.DataBlocks, Is.Zero);
    Assert.That(p.Metadata!.FileSize, Is.Zero);
    Assert.That(p.Data, Is.Empty);
  }

  [Test, Category("ThemVsUs")]
  public void GivenSbxencContainer_WhenListedAndExtractedThroughTheDescriptor_ThenOneNamedEntry() {
    var d = new SeqBoxFormatDescriptor();
    var bytes = LoadVector("fox-v1.sbx");

    var list = d.List(new MemoryStream(bytes), null);
    Assert.That(list.Single().Name, Is.EqualTo("fox.txt"));
    Assert.That(list.Single().OriginalSize, Is.EqualTo(FoxText.Length));

    d.Extract(new MemoryStream(bytes), this._tmpDir, null, null);
    Assert.That(File.ReadAllBytes(Path.Combine(this._tmpDir, "fox.txt")), Is.EqualTo(FoxText));
  }

  // ── our writer ──────────────────────────────────────────────────────

  [TestCase(1, 512)]
  [TestCase(2, 128)]
  [TestCase(3, 4096)]
  [TestCase(4, 488)]
  [Category("RoundTrip")]
  public void GivenData_WhenWrittenAtEachVersion_ThenBlockAlignedAndRoundTrips(int version, int blockSize) {
    var ms = new MemoryStream();
    SeqBoxContainer.Write(ms, Noise, "noise.bin", version, uid: [0, 0, 0xC0, 0xFF, 0xEE, 0x09]);

    Assert.That(ms.Length % blockSize, Is.Zero);
    var p = SeqBoxContainer.Read(new MemoryStream(ms.ToArray()));
    Assert.That(p.Version, Is.EqualTo(version));
    Assert.That(p.Uid, Is.EqualTo(new byte[] { 0, 0, 0xC0, 0xFF, 0xEE, 0x09 }));
    Assert.That(p.Data, Is.EqualTo(Noise));
  }

  [Test, Category("Boundary")]
  public void GivenPayloadOfExactlyOneBlock_WhenWritten_ThenNoPaddingBlockFollows() {
    var data = Enumerable.Range(0, 496).Select(i => (byte)i).ToArray();
    var ms = new MemoryStream();
    SeqBoxContainer.Write(ms, data, "x.bin");

    Assert.That(ms.Length, Is.EqualTo(2 * 512));
    Assert.That(SeqBoxContainer.Read(new MemoryStream(ms.ToArray())).Data, Is.EqualTo(data));
  }

  [Test, Category("Boundary")]
  public void GivenEmptyFile_WhenWritten_ThenOnlyTheMetadataBlock() {
    var ms = new MemoryStream();
    SeqBoxContainer.Write(ms, [], "empty.bin");

    Assert.That(ms.Length, Is.EqualTo(512));
    Assert.That(SeqBoxContainer.Read(new MemoryStream(ms.ToArray())).Data, Is.Empty);
  }

  [Test, Category("Exception")]
  public void GivenTwoFiles_WhenCreated_ThenRefusesBecauseSeqBoxHoldsOneFile() {
    var d = new SeqBoxFormatDescriptor();
    Assert.That(() => d.Create(new MemoryStream(),
      [ArchiveInputInfo.InMemory("a", [1]), ArchiveInputInfo.InMemory("b", [2])], new FormatCreateOptions()),
      Throws.InstanceOf<NotSupportedException>());
  }

  [Test, Category("Exception")]
  public void GivenFlippedDataByte_WhenRead_ThenTheBlockCrcRejectsIt() {
    var bytes = LoadVector("fox-v1.sbx");
    bytes[512 * 2 + 100] ^= 0x01;

    Assert.That(() => SeqBoxContainer.Read(new MemoryStream(bytes)), Throws.InstanceOf<InvalidDataException>().With.Message.Contains("CRC"));
  }

  [Test, Category("Exception")]
  public void GivenSwappedBlocks_WhenRead_ThenTheSequenceCheckRejectsIt() {
    var bytes = LoadVector("fox-v1.sbx");
    var swapped = bytes.ToArray();
    bytes.AsSpan(512, 512).CopyTo(swapped.AsSpan(1024));
    bytes.AsSpan(1024, 512).CopyTo(swapped.AsSpan(512));

    Assert.That(() => SeqBoxContainer.Read(new MemoryStream(swapped)), Throws.InstanceOf<InvalidDataException>().With.Message.Contains("order"));
  }

  [Test, Category("Exception")]
  public void GivenMissingLastBlock_WhenRead_ThenTheFileSizeCheckRejectsIt() {
    var bytes = LoadVector("fox-v1.sbx");

    Assert.That(() => SeqBoxContainer.Read(new MemoryStream(bytes[..^512])), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("Exception")]
  public void GivenUnknownVersion_WhenRead_ThenRefused() {
    var bytes = LoadVector("fox-v1.sbx");
    bytes[3] = 9;

    Assert.That(() => SeqBoxContainer.Read(new MemoryStream(bytes)), Throws.InstanceOf<InvalidDataException>());
    Assert.That(SeqBoxContainer.IsSeqBox(bytes), Is.False);
  }

  // ── oracle: the reference decoder reads what we write ───────────────

  [TestCase(1)]
  [TestCase(2)]
  [TestCase(3)]
  [TestCase(4)]
  [Category("ExternalInterop")]
  public void GivenOurContainer_WhenSbxdecDecodesIt_ThenHashMatchesAndBytesAreIdentical(int version) {
    var sbxdec = RequireSbxdec();
    var big = Enumerable.Range(0, 20_000).Select(i => (byte)(i * 7 + (i >> 8))).ToArray();
    var path = Path.Combine(this._tmpDir, "ours.sbx");
    using (var f = File.Create(path))
      SeqBoxContainer.Write(f, big, "big.bin", version);
    var outPath = Path.Combine(this._tmpDir, "decoded.bin");

    var r = FsInteropToolbox.RunWsl(
      $"python3 {sbxdec} -o '{FsInteropToolbox.WinToWsl(path)}' '{FsInteropToolbox.WinToWsl(outPath)}'");

    Assert.That(r.ExitCode, Is.EqualTo(0), $"sbxdec rejected our container:\n{r.StdOut}\n{r.StdErr}");
    Assert.That(r.StdOut, Does.Contain("hash match!"));
    Assert.That(File.ReadAllBytes(outPath), Is.EqualTo(big));
  }

  private static string RequireSbxdec() {
    if (OperatingSystem.IsWindows() && !FsInteropToolbox.WslAvailable)
      Assert.Ignore("WSL is not available.");
    var dir = Environment.GetEnvironmentVariable("SEQBOX_HOME") ?? "/opt/seqbox";
    var probe = FsInteropToolbox.RunWsl($"test -f '{dir}/sbxdec.py' && command -v python3");
    if (probe.ExitCode != 0)
      Assert.Ignore($"SeqBox reference tools not found at {dir} (set SEQBOX_HOME to a checkout of github.com/MarcoPon/SeqBox).");
    return $"'{dir}/sbxdec.py'";
  }

  private static byte[] LoadVector(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"SeqBoxVectors.{name}")
      ?? throw new InvalidOperationException($"missing embedded vector {name}");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }
}
