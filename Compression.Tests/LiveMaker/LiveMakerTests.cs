using System.Buffers.Binary;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Compression.Registry;
using FileFormat.LiveMaker;

namespace Compression.Tests.LiveMaker;

/// <summary>
/// LiveMaker VF archives. Oracles: pylivemaker 1.2.1 wrote the <c>pylm-*</c> fixtures (the scrambled
/// members built with its own chunk-order generator) and, with GARbro 1.5.44, read every fixture
/// including the ones we wrote (<c>pylivemaker-listing.txt</c>, <c>garbro-listing.txt</c>); a save
/// archive written by LiveMaker itself backs the directory layout. See ReferenceVectors/README.md.
/// </summary>
[TestFixture]
public class LiveMakerTests {

  private static byte[] Vector(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"LiveMakerVectors.{name}")
      ?? throw new FileNotFoundException($"Embedded LiveMaker vector '{name}' is missing from the test assembly.");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  private static List<string[]> Listing(string listing, string vector)
    => Encoding.UTF8.GetString(Vector(listing)).Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries)
      .Select(l => l.Split('\t')).Where(f => f[0] == vector).Select(f => f[1..]).ToList();

  private static readonly byte[] BBin = [.. Enumerable.Range(0, 4096).Select(i => (byte)i)];

  private static IReadOnlyList<ArchiveInputInfo> Inputs() => [
    ArchiveInputInfo.InMemory("hello.txt", "Hello world!\n"u8.ToArray()),
    ArchiveInputInfo.InMemory("sub/data.bin", BBin),
    ArchiveInputInfo.InMemory("日本語.txt", Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("こんにちは ", 20)))),
  ];

  private static byte[] Create(IReadOnlyList<ArchiveInputInfo> inputs, string? method = null) {
    using var ms = new MemoryStream();
    new LiveMakerFormatDescriptor().Create(ms, inputs, new FormatCreateOptions { MethodName = method });
    return ms.ToArray();
  }

  private static void DumpIfRequested(string name, byte[] bytes) {
    var dir = Environment.GetEnvironmentVariable("CWB_DUMP_LIVEMAKER");
    if (!string.IsNullOrEmpty(dir)) File.WriteAllBytes(Path.Combine(dir, name), bytes);
  }

  [TestCase("pylm-v102.dat")]
  [TestCase("pylm-scrambled.dat")]
  [TestCase("cwb-v102.dat")]
  [TestCase("cwb-v102-stored.dat")]
  [Category("ThirdPartyVector")]
  public void Given_Fixture_When_Read_Then_MatchesPylivemakerAndGarbro(string vector) {
    var expected = Listing("pylivemaker-listing.txt", vector);
    Assert.That(expected, Is.Not.Empty, "no pylivemaker listing for " + vector);
    var garbro = Listing("garbro-listing.txt", vector);
    Assert.That(garbro.Select(f => f[3]), Is.EqualTo(expected.Select(f => f[3])), "GARbro and pylivemaker disagree");
    var r = new LiveMakerReader(new MemoryStream(Vector(vector)));
    Assert.That(r.Entries.Select(e => e.Name), Is.EqualTo(expected.Select(f => f[0])));
    foreach (var (e, f) in r.Entries.Zip(expected)) {
      var data = r.Extract(e);
      Assert.That(data, Has.Length.EqualTo(int.Parse(f[1])), e.Name);
      Assert.That(Convert.ToHexStringLower(SHA256.HashData(data)), Is.EqualTo(f[3]), e.Name);
    }
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_PylivemakerScrambledArchive_When_Read_Then_MethodsAreTheLiveMakerProOnes() {
    var r = new LiveMakerReader(new MemoryStream(Vector("pylm-scrambled.dat")));
    Assert.That(r.Entries.Select(e => e.Method), Is.EqualTo(new[] { LiveMakerMethod.Stored, LiveMakerMethod.Scrambled, LiveMakerMethod.ScrambledZlib }));
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_PylivemakerArchive_When_Read_Then_ItsLvTrailerIsTolerated() {
    var bytes = Vector("pylm-v102.dat");
    Assert.That(Encoding.ASCII.GetString(bytes, bytes.Length - 2, 2), Is.EqualTo("lv"));
    Assert.That(new LiveMakerReader(new MemoryStream(bytes)).BaseOffset, Is.Zero);
  }

  [TestCase("cwb-v102.dat", null)]
  [TestCase("cwb-v102-stored.dat", "store")]
  [Category("ThirdPartyVector")]
  public void Given_Inputs_When_WeWrite_Then_IdenticalToTheOracleVerifiedFixture(string vector, string? method) {
    var bytes = Create(Inputs(), method);
    DumpIfRequested(vector, bytes);
    Assert.That(bytes, Is.EqualTo(Vector(vector)));
  }

  /// <summary>
  /// Reads the save archive LiveMaker itself wrote (pylivemaker's tests/data/save.dat, not committed)
  /// when CWB_LIVEMAKER_SAVE names it: the payload pylivemaker extracts, SHA-256 b99c08f7….
  /// </summary>
  [Test, Category("ExternalTool")]
  public void Given_LiveMakerSaveArchive_When_Read_Then_TheSaveDataDecodes() {
    var path = Environment.GetEnvironmentVariable("CWB_LIVEMAKER_SAVE");
    if (string.IsNullOrEmpty(path) || !File.Exists(path))
      Assert.Ignore("Set CWB_LIVEMAKER_SAVE to pylivemaker's tests/data/save.dat to run this check.");
    var r = new LiveMakerReader(new MemoryStream(File.ReadAllBytes(path!)));
    var e = r.Entries.Single();
    Assert.That(e.Name, Is.EqualTo("STATUSSAVEDATA"));
    Assert.That(e.Method, Is.EqualTo(LiveMakerMethod.Zlib));
    Assert.That(Convert.ToHexStringLower(SHA256.HashData(r.Extract(e))),
      Is.EqualTo("b99c08f7b777241331f4530a517d8367e7112a27a7af0f1b081dc2629665d8b3"));
  }

  [TestCase(null)]
  [TestCase("store")]
  [Category("RoundTrip")]
  public void Given_Inputs_When_Created_Then_ReadsBackIdentically(string? method) {
    var r = new LiveMakerReader(new MemoryStream(Create(Inputs(), method)));
    Assert.That(r.Version, Is.EqualTo(102u));
    foreach (var (e, i) in r.Entries.Zip(Inputs())) {
      Assert.That(e.Name, Is.EqualTo(i.ArchiveName));
      Assert.That(r.Extract(e), Is.EqualTo(i.ReadContent()), e.Name);
    }
    Assert.That(r.Entries.Any(e => e.Method == LiveMakerMethod.Zlib), Is.EqualTo(method != "store"));
  }

  [Test, Category("RoundTrip")]
  public void Given_ScrambledArchive_When_AddedAndRemoved_Then_ScrambledMembersCarryAcrossUntouched() {
    var original = Vector("pylm-scrambled.dat");
    var before = new LiveMakerReader(new MemoryStream(original));
    using var ms = new MemoryStream();
    ms.Write(original);
    var d = (IArchiveModifiable)new LiveMakerFormatDescriptor();
    d.Add(ms, [ArchiveInputInfo.InMemory("new.txt", "added"u8.ToArray())]);
    d.Remove(ms, ["plain.txt"]);
    ms.Position = 0;
    var r = new LiveMakerReader(ms);
    Assert.That(r.Entries.Select(e => e.Name), Is.EqualTo(new[] { "scr.bin", "scrz.txt", "new.txt" }));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(BBin));
    Assert.That(r.ReadStored(r.Entries[1]), Is.EqualTo(before.ReadStored(before.Entries[2])));
    Assert.That(r.Extract(r.Entries[2]), Is.EqualTo("added"u8.ToArray()));
  }

  [Test, Category("EdgeCase")]
  public void Given_ArchiveAppendedToAnExecutable_When_Read_Then_FoundThroughTheTrailer() {
    var archive = Create(Inputs());
    var exe = new byte[300];
    "MZ"u8.CopyTo(exe);
    var image = new byte[exe.Length + archive.Length + 6];
    exe.CopyTo(image, 0);
    archive.CopyTo(image, exe.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(exe.Length + archive.Length), (uint)exe.Length);
    "lv"u8.CopyTo(image.AsSpan(image.Length - 2));
    var r = new LiveMakerReader(new MemoryStream(image));
    Assert.That(r.BaseOffset, Is.EqualTo(exe.Length));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo(BBin));
    using var ms = new MemoryStream(image);
    Assert.Throws<NotSupportedException>(() => ((IArchiveModifiable)new LiveMakerFormatDescriptor()).Remove(ms, ["hello.txt"]));
  }

  [Test, Category("EdgeCase")]
  public void Given_NoFiles_When_Created_Then_EmptyListing()
    => Assert.That(new LiveMakerFormatDescriptor().List(new MemoryStream(Create([])), null), Is.Empty);

  [Test, Category("EdgeCase")]
  public void Given_NameWithoutShiftJisSpelling_When_Created_Then_Refused()
    => Assert.Throws<ArgumentException>(() => Create([ArchiveInputInfo.InMemory("smile-😀.txt", new byte[] { 1 })]));

  [Test, Category("EdgeCase")]
  public void Given_UnknownMethod_When_Created_Then_NotSupported()
    => Assert.Throws<NotSupportedException>(() => Create(Inputs(), "lzma"));

  [Test, Category("EdgeCase")]
  public void Given_CorruptedMember_When_Extracted_Then_FailsItsCrc() {
    var bytes = Create(Inputs(), "store");
    bytes[^1] ^= 0x01; // last byte of the last member
    var r = new LiveMakerReader(new MemoryStream(bytes));
    Assert.Throws<InvalidDataException>(() => r.Extract(r.Entries[^1]));
  }

  [Test, Category("EdgeCase")]
  public void Given_UnknownVersion_When_Read_Then_InvalidData() {
    var bytes = Create(Inputs());
    bytes[2] = 0x63; // 99
    Assert.Throws<InvalidDataException>(() => _ = new LiveMakerReader(new MemoryStream(bytes)));
  }

  [Test, Category("EdgeCase")]
  public void Given_NotAnArchive_When_Read_Then_InvalidData()
    => Assert.Throws<InvalidDataException>(() => _ = new LiveMakerReader(new MemoryStream(new byte[64])));

  [Test, Category("EdgeCase")]
  public void Given_TruncatedArchive_When_Read_Then_Throws()
    => Assert.Catch<Exception>(() => _ = new LiveMakerReader(new MemoryStream(Create(Inputs())[..40])));

  [Test, Category("HappyPath")]
  public void Given_VfBytes_When_DetectedByMagic_Then_LiveMaker()
    => Assert.That(Compression.Lib.FormatDetector.DetectByMagic(Vector("pylm-v102.dat")).ToString(), Is.EqualTo("LiveMaker"));
}
