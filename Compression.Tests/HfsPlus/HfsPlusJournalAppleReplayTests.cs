using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using FileSystem.HfsPlus;

namespace Compression.Tests.HfsPlus;

/// <summary>
/// Journaled volumes macOS 26 left mid-transaction (go-apfs-v3's capture, see
/// <c>HfsPlus/ReferenceVectors/journal/README.md</c>), against the bytes Apple's own kernel replay
/// leaves on each. Our replay written into the image must equal Apple's byte for byte — that is
/// the oracle for the replay itself, including the transaction a log carries past its recorded
/// end. An edit of such a volume replays first and then edits, so fsck.hfsplus (which never
/// replays) judges the result the way macOS would leave it.
/// </summary>
[TestFixture]
public sealed class HfsPlusJournalAppleReplayTests {

  private const string Linear = "linear";
  private const string Wrapped = "wrapped";
  private const string CompleteTail = "complete-tail";
  private const string IncompleteTail = "incomplete-tail";

  private string _tmp = null!;

  [SetUp]
  public void SetUp() {
    this._tmp = Path.Combine(Path.GetTempPath(), "cwb_hfsjapple_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(this._tmp);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmp, recursive: true); } catch { /* best effort */ }
  }

  /// <summary>(profile, shape, SHA-256 as captured, SHA-256 after Apple's replay) — from the README.</summary>
  private static readonly (string Profile, string Shape, string Captured, string AppleReplayed)[] Volumes = [
    ("hfsplus-j", Linear, "e69ab01c3c56fd2d2527a09b3be240c4c1928d4faded1399737d0e7d7c1e265f", "0eb7b56863bd6d0df872e9f5537427e09066b2c5b5fe278bbb5ab3161939cb9d"),
    ("hfsplus-j", Wrapped, "e73a70429ce656c08bd46eb6828364d7366e4181b0c6f0a894e902f8a4b2c863", "00692072d03c237395741f3657c0ebe39c2d3df74da82e5cdba4bc68dfbe6e50"),
    ("hfsplus-j", CompleteTail, "28a24642312f64691147f72ed87d9bb8ca6fd01bbf5056c281a63f691f4dbbab", "9be91d32d3b286a8e3f08fe9d1f8e27d530d6d811844327058ec89ea93bdb657"),
    ("hfsplus-j", IncompleteTail, "3dc08967e380471d5bbba15b20df175524ceb663e9a0959821705aee46300d76", "bd14b77e624f35d6a4642c8cb8342d382bd79ab4ab6e84a04bbb887371764851"),
    ("hfsx-j", Linear, "c6ffdb1ca0e2102e799dea0e06468ff60d9d0a3affda9f0bcd41d2d19b1757ee", "9c7bd9f7479e76dbcb49d798d242e70c35f3ff61f6b199df26b71f229fa1d814"),
    ("hfsx-j", Wrapped, "875734195de02fcd6543a818791737c2e1cd2379573f7497e33603916b22abb8", "033ad1558cb7a93073f7110b73ac7c03adaa3999a50851a8e7e9a19ddfc3d8c0"),
    ("hfsx-j", CompleteTail, "f720ed0cdbfae34fb9a71cfa2f7759bccc5efba45e61694c34f8cc2a2c29a081", "6fa7fdbfbcf32fbdaa71490022088253e40929a64e4865f5249d0160bbe5b9b2"),
    ("hfsx-j", IncompleteTail, "74e14bb1842719ef2d20b6eddb100ce66eca81c15af1d31732bafc163602b866", "68b05146cb36d0454bc0f029ae26a7ecfbff015c0d04d04f643cab287a18e5e6"),
  ];

  private static IEnumerable<TestCaseData> AllVolumes()
    => Volumes.Select(static v => new TestCaseData(v.Profile, v.Shape).SetArgDisplayNames(v.Profile, v.Shape));

  private static (string Captured, string AppleReplayed) Expected(string profile, string shape)
    => Volumes.Single(v => v.Profile == profile && v.Shape == shape) is var v ? (v.Captured, v.AppleReplayed) : default;

  internal static byte[] Interrupted(string profile, string shape) {
    var name = $"HfsPlusJournalVectors.{profile}.interrupted-{shape}.raw.gz";
    using var resource = typeof(HfsPlusJournalAppleReplayTests).Assembly.GetManifestResourceStream(name)
                         ?? throw new InvalidOperationException($"{name} is not embedded.");
    using var gzip = new GZipStream(resource, CompressionMode.Decompress);
    using var raw = new MemoryStream();
    gzip.CopyTo(raw);
    return raw.ToArray();
  }

  private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

  private static HfsPlusJournal Journal(byte[] image)
    => HfsPlusJournal.Locate(image, image.AsSpan(1024, 512), BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(1024 + 40)))!;

  private static Dictionary<string, byte[]> Files(byte[] image) {
    using var reader = new HfsPlusReader(new MemoryStream(image));
    return reader.Entries.Where(static e => !e.IsDirectory).ToDictionary(static e => e.FullPath, reader.Extract);
  }

  [TestCaseSource(nameof(AllVolumes)), Category("HappyPath")]
  public void GivenAVolumeMacOsLeftMidTransaction_WhenEmbedded_ThenItIsTheCapturedVolumeWithTransactionsPending(string profile, string shape) {
    var image = Interrupted(profile, shape);
    Assert.Multiple(() => {
      Assert.That(Sha256(image), Is.EqualTo(Expected(profile, shape).Captured));
      Assert.That(Journal(image).TryReadHeader(image, out var header), Is.True);
      Assert.That(header.Start, Is.Not.EqualTo(header.End), "the journal holds transactions");
    });
  }

  [TestCaseSource(nameof(AllVolumes)), Category("HappyPath")]
  public void GivenAVolumeMacOsLeftMidTransaction_WhenReplayedInPlace_ThenTheBytesAreThoseApplesReplayLeaves(string profile, string shape) {
    var image = Interrupted(profile, shape);
    Assert.That(Journal(image).ReplayInPlace(image), Is.True);
    Assert.That(Sha256(image), Is.EqualTo(Expected(profile, shape).AppleReplayed));
  }

  [TestCaseSource(nameof(AllVolumes)), Category("HappyPath")]
  public void GivenAVolumeMacOsLeftMidTransaction_WhenRead_ThenTheReaderShowsWhatApplesReplayLeaves(string profile, string shape) {
    var image = Interrupted(profile, shape);
    var replayed = image.ToArray();
    Journal(replayed).ReplayInPlace(replayed);
    using var reader = new HfsPlusReader(new MemoryStream(image));
    var viewed = reader.Entries.Where(static e => !e.IsDirectory).ToDictionary(static e => e.FullPath, reader.Extract);
    var expected = Files(replayed);
    Assert.Multiple(() => {
      Assert.That(reader.JournalReplayed, Is.True);
      Assert.That(viewed.Keys, Is.EquivalentTo(expected.Keys));
      foreach (var (path, bytes) in expected) Assert.That(viewed[path], Is.EqualTo(bytes), path);
      Assert.That(expected.Keys, Does.Contain("Fixture/retained"));
      Assert.That(Journal(image).Plan((o, l) => image.AsSpan((int)o, l).ToArray()).ReachedEnd, Is.True);
    });
  }

  [TestCaseSource(nameof(AllVolumes)), Category("HappyPath")]
  public void GivenAVolumeMacOsLeftMidTransaction_WhenAFileIsAddedAndOneRemoved_ThenTheEditLandsOnTheReplayedVolume(string profile, string shape) {
    var image = Interrupted(profile, shape);
    var replayed = image.ToArray();
    Journal(replayed).ReplayInPlace(replayed);
    var before = Files(replayed);
    var removed = before.Keys.First(static p => p.StartsWith("Fixture/entry-", StringComparison.Ordinal));

    using var ms = new MemoryStream();
    ms.Write(image);
    HfsPlusModifier.AddFile(ms, "Fixture/added.txt", "added on a volume macOS left mid-transaction"u8.ToArray());
    Assert.That(HfsPlusModifier.RemoveFile(ms, removed), Is.True);
    var after = ms.ToArray();

    var files = Files(after);
    Assert.Multiple(() => {
      Assert.That(Journal(after).TryReadHeader(after, out var header), Is.True);
      Assert.That(header.Start, Is.EqualTo(header.End), "the journal is left empty");
      Assert.That(files["Fixture/added.txt"], Is.EqualTo("added on a volume macOS left mid-transaction"u8.ToArray()));
      Assert.That(files.Keys, Does.Not.Contain(removed));
      foreach (var (path, bytes) in before.Where(p => p.Key != removed))
        Assert.That(files[path], Is.EqualTo(bytes), path);
    });
  }

  [Test, Category("ExceptionalCase")]
  public void GivenABlockListBeforeTheRecordedEndIsDamaged_WhenEdited_ThenTheEditIsRefusedAndTheVolumeIsUnchanged() {
    var image = Interrupted("hfsplus-j", Linear);
    var journal = Journal(image);
    Assert.That(journal.TryReadHeader(image, out var header), Is.True);
    // The second block list begins one first list's bytes_used after the start; break its checksum.
    var first = (int)(journal.Offset + header.Start);
    var bytesUsed = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(first + 4));
    image[(int)(journal.Offset + header.Start + bytesUsed) + 8] ^= 0xFF;
    var damaged = image.ToArray();

    using var ms = new MemoryStream();
    ms.Write(image);
    Assert.Throws<NotSupportedException>(() => HfsPlusModifier.AddFile(ms, "Fixture/added.txt", [1]));
    Assert.That(ms.ToArray(), Is.EqualTo(damaged));
  }

  [Test, Category("Boundary")]
  public void GivenTheTornTransactionPastTheEnd_WhenPlanned_ThenTheLogEndsAtTheRecordedEnd() {
    var image = Interrupted("hfsplus-j", IncompleteTail);
    var journal = Journal(image);
    Assert.That(journal.TryReadHeader(image, out var header), Is.True);
    var plan = journal.Plan((o, l) => image.AsSpan((int)o, l).ToArray());
    Assert.That(plan.NewStart, Is.EqualTo(header.End));
  }

  [Test, Category("Boundary")]
  public void GivenTheWholeTransactionPastTheEnd_WhenPlanned_ThenTheLogEndsAfterIt() {
    var image = Interrupted("hfsplus-j", CompleteTail);
    var journal = Journal(image);
    Assert.That(journal.TryReadHeader(image, out var header), Is.True);
    var plan = journal.Plan((o, l) => image.AsSpan((int)o, l).ToArray());
    Assert.That(plan.NewStart, Is.Not.EqualTo(header.End));
    Assert.That(plan.NewStart, Is.EqualTo(126976), "where macOS restarts the log on this volume");
  }

  [Test, Category("ExternalFsInterop")]
  public void GivenEveryVolumeMacOsLeftMidTransaction_WhenEdited_ThenFsckHfsplusAcceptsTheResult() {
    HfsPlusOracles.RequireFsck();
    foreach (var (profile, shape, _, _) in Volumes) {
      var image = Interrupted(profile, shape);
      using var ms = new MemoryStream();
      ms.Write(image);
      HfsPlusModifier.AddFile(ms, "Fixture/added.txt", "added"u8.ToArray());
      HfsPlusOracles.AssertFsckClean(ms.ToArray(), this._tmp, $"{profile} {shape}, replayed and edited");
    }
  }
}
