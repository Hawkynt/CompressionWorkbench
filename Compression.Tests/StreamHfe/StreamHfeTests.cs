using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.StreamHfe;

namespace Compression.Tests.StreamHfe;

[TestFixture]
public class StreamHfeTests {
  private static StreamHfeImage Sample() {
    var image = new StreamHfeImage { Sides = 2, InterfaceMode = 0, Name = "CWB", Date = "2026-10-30" };
    image.Tracks[(0, 0)] = new StreamHfeImage.Track(StreamHfeImage.Encode([100, 200, 100, 20_000, 3_000_000]), 5_000_000);
    image.Tracks[(0, 1)] = new StreamHfeImage.Track(StreamHfeImage.Encode([50, 50]), 4_999_968);
    image.Tracks[(1, 0)] = new StreamHfeImage.Track(StreamHfeImage.Encode([127, 128]), 300, Flags: 0);
    return image;
  }

  private static byte[] Bytes(StreamHfeImage image) {
    using var ms = new MemoryStream();
    image.Write(ms);
    return ms.ToArray();
  }

  [Test]
  public void GivenTracks_WhenWritten_ThenTheLayoutIsHxcfes() {
    var data = Bytes(Sample());
    Assert.That(data.AsSpan(0, 16).SequenceEqual("HxC_Stream_Image"u8), Is.True);
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x1C)), Is.EqualTo(0x200), "track list");
    // 2 cylinders × 2 sides + 1 spare entry fit one block: index block at 0x400, data at 0x600.
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x18)), Is.EqualTo(0x400));
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x20)), Is.EqualTo(0x600));
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x24)), Is.EqualTo(2));
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x2C)), Is.EqualTo(40_000));
    // Cylinder 1 side 1 was never given: it is written as an unformatted track, a one-token LZ4 block.
    var e = data.AsSpan(0x200 + 3 * 32);
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(e[8..]), Is.EqualTo(1));
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(e[12..]), Is.Zero);
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(e[20..]), Is.Zero);
  }

  [Test]
  public void GivenAWrittenImage_WhenRead_ThenTracksAndHeaderSurvive() {
    var back = StreamHfeImage.Read(Bytes(Sample()));
    Assert.That(back.Name, Is.EqualTo("CWB"));
    Assert.That(back.Date, Is.EqualTo("2026-10-30"));
    Assert.That(StreamHfeImage.Decode(back.Tracks[(0, 0)].Pulses), Is.EqualTo(new uint[] { 100, 200, 100, 20_000, 3_000_000 }));
    Assert.That(back.Tracks[(0, 0)].Length, Is.EqualTo(5_000_000));
    Assert.That(back.Tracks[(1, 0)].Flags, Is.Zero, "an unpacked track stays unpacked");
    Assert.That(back.Tracks[(1, 1)].Pulses, Is.Empty);
  }

  [TestCase(0u, new byte[] { 0x00 })]
  [TestCase(127u, new byte[] { 0x7F })]
  [TestCase(128u, new byte[] { 0x80, 0x80 })]
  [TestCase(0x3FFFu, new byte[] { 0xBF, 0xFF })]
  [TestCase(0x4000u, new byte[] { 0xC0, 0x40, 0x00 })]
  [TestCase(0x1F_FFFFu, new byte[] { 0xDF, 0xFF, 0xFF })]
  [TestCase(0x20_0000u, new byte[] { 0xE0, 0x20, 0x00, 0x00 })]
  [TestCase(0x0FFF_FFFFu, new byte[] { 0xEF, 0xFF, 0xFF, 0xFF })]
  [TestCase(0xFFFF_FFFFu, new byte[] { 0xEF, 0xFF, 0xFF, 0xFF })]
  public void GivenADelta_WhenEncoded_ThenTheCodeLengthFollowsItsSize(uint delta, byte[] code) {
    Assert.That(StreamHfeImage.Encode([delta]), Is.EqualTo(code));
    Assert.That(StreamHfeImage.Decode(code), Is.EqualTo(new[] { Math.Min(delta, 0x0FFF_FFFFu) }));
  }

  [Test]
  public void GivenACutShortOrReservedCode_WhenDecoded_ThenInvalidData() {
    Assert.Throws<InvalidDataException>(() => StreamHfeImage.Decode([0xC0, 0x01]));
    Assert.Throws<InvalidDataException>(() => StreamHfeImage.Decode([0xF0]));
  }

  [Test]
  public void GivenAPulseCountThatDisagrees_WhenRead_ThenInvalidDataAndListFallsBack() {
    var data = Bytes(Sample());
    data[0x200 + 20] ^= 1;
    Assert.Throws<InvalidDataException>(() => StreamHfeImage.Read(data));
    Assert.That(new StreamHfeFormatDescriptor().List(new MemoryStream(data), null).Single().Name, Is.EqualTo("FULL.hfe"));
  }

  [Test]
  public void GivenATruncatedTrack_WhenRead_ThenInvalidData()
    => Assert.Throws<InvalidDataException>(() => StreamHfeImage.Read(Bytes(Sample())[..^2]));

  [TestCase(0)]
  [TestCase(3)]
  public void GivenAnImpossibleSideCount_WhenRead_ThenInvalidData(int sides) {
    var data = Bytes(Sample());
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x28), (uint)sides);
    Assert.Throws<InvalidDataException>(() => StreamHfeImage.Read(data));
  }

  [Test]
  public void GivenTheBitCellHfeSignature_WhenRead_ThenInvalidData() {
    var data = new byte[1024];
    "HXCPICFE"u8.CopyTo(data);
    Assert.Throws<InvalidDataException>(() => StreamHfeImage.Read(data));
  }

  [Test]
  public void GivenTheDescriptor_WhenATrackIsReplacedAndAnotherRemoved_ThenTheImageFollows() {
    var ms = new MemoryStream();
    ms.Write(Bytes(Sample()));
    var d = new StreamHfeFormatDescriptor();
    var path = Path.GetTempFileName();
    try {
      File.WriteAllBytes(path, StreamHfeImage.Encode([7, 8, 9]));
      d.Add(ms, [new ArchiveInputInfo(path, "tracks/track_000_1.bin", false)]);
      d.Remove(ms, ["tracks/track_000_0.bin"]);
    } finally {
      File.Delete(path);
    }
    var image = StreamHfeImage.Read(ms.ToArray());
    Assert.That(StreamHfeImage.Decode(image.Tracks[(0, 1)].Pulses), Is.EqualTo(new uint[] { 7, 8, 9 }));
    Assert.That(image.Tracks[(0, 1)].Length, Is.EqualTo(4_999_968), "the replaced track's revolution time is kept");
    Assert.That(image.Tracks[(0, 0)].Pulses, Is.Empty, "a removed track becomes unformatted");
  }

  [Test]
  public void GivenPartsAndMetadata_WhenCreated_ThenTheHeaderFollowsTheMetadata() {
    var dir = Path.Combine(Path.GetTempPath(), "cwb-shfe-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      var track = Path.Combine(dir, "t");
      var ini = Path.Combine(dir, "m");
      File.WriteAllBytes(track, StreamHfeImage.Encode([100, 100]));
      File.WriteAllText(ini, "[StreamHfe]\nsides=1\ninterface_mode=7\nname=Made\ntrack_002_0_length=640\n");
      using var ms = new MemoryStream();
      new StreamHfeFormatDescriptor().Create(ms, [new ArchiveInputInfo(track, "tracks/track_002_0.bin", false), new ArchiveInputInfo(ini, "metadata.ini", false)], new FormatCreateOptions());
      var image = StreamHfeImage.Read(ms.ToArray());
      Assert.That((image.Sides, image.Cylinders, image.InterfaceMode, image.Name), Is.EqualTo((1, 3, 7u, "Made")));
      Assert.That(image.Tracks[(2, 0)].Length, Is.EqualTo(640));
      Assert.That(image.Tracks[(0, 0)].Pulses, Is.Empty);
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }

  [Test]
  public void GivenAnArbitraryFile_WhenOffered_ThenItIsDeclined() {
    var d = new StreamHfeFormatDescriptor();
    Assert.That(d.CanAccept(new ArchiveInputInfo("PROBE.BIN", "PROBE.BIN", false), out _), Is.False);
    Assert.That(d.CanAccept(new ArchiveInputInfo("x", "tracks/track_079_1.bin", false), out _), Is.True);
  }
}

/// <summary>
/// hxcfe (HxC Floppy Emulator command line, GPL, pinned commit) as the oracle. It writes a Stream HFE
/// from a 720K sector image; our varint codec reproduces its pulse streams bit for bit; we rebuild a
/// fresh image from the parts we list, and hxcfe decodes our rebuild back to the identical sectors,
/// also after one track was replaced with another disk's. The hxcfe binary is <c>CWB_HXCFE</c>
/// (a path inside WSL on Windows); skipped when unset unless <c>CWB_REQUIRE_HXCFE=1</c>.
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public class StreamHfeExternalConformanceTests {
  private static string RequireHxcfe() {
    var hxcfe = Environment.GetEnvironmentVariable("CWB_HXCFE");
    if (!string.IsNullOrEmpty(hxcfe) && FsInteropToolbox.WslAvailable) return hxcfe;
    const string hint = "hxcfe not configured: build HxCFloppyEmulator_cmdline and set CWB_HXCFE to the hxcfe binary.";
    if (Environment.GetEnvironmentVariable("CWB_REQUIRE_HXCFE") == "1") Assert.Fail(hint);
    Assert.Ignore(hint);
    return "";
  }

  private static void Hxcfe(string hxcfe, string input, string format, string output) {
    // hxcfe finds its shared libraries next to itself (rpath "."), so it runs from its own folder.
    var dir = hxcfe[..hxcfe.LastIndexOf('/')];
    var r = FsInteropToolbox.RunWsl($"cd '{dir}' && ./hxcfe -finput:{FsInteropToolbox.WinToWsl(input)} -conv:{format} -foutput:{FsInteropToolbox.WinToWsl(output)}");
    TestContext.Out.WriteLine($"hxcfe {input} -> {format} -> {r.ExitCode}\n{r.StdOut[^Math.Min(r.StdOut.Length, 2000)..]}\n{r.StdErr}");
    Assert.That(File.Exists(output), Is.True, $"hxcfe wrote no {format} output");
  }

  private string _tmp = null!;

  [SetUp]
  public void SetUp() {
    this._tmp = Path.Combine(Path.GetTempPath(), "cwb-shfex-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._tmp);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmp, recursive: true); } catch { /* best effort */ }
  }

  private static byte[] Disk(int seed) {
    var d = new byte[737280];
    new Random(seed).NextBytes(d);
    return d;
  }

  private string Theirs(string hxcfe, byte[] disk, string name) {
    var img = Path.Combine(this._tmp, name + ".img");
    File.WriteAllBytes(img, disk);
    var hfe = Path.Combine(this._tmp, name + ".hfe");
    Hxcfe(hxcfe, img, "HXC_STREAMHFE", hfe);
    return hfe;
  }

  private List<ArchiveInputInfo> TakeApart(string hfe) {
    var dir = Path.Combine(this._tmp, "parts-" + Guid.NewGuid().ToString("N")[..8]);
    using (var fs = File.OpenRead(hfe))
      new StreamHfeFormatDescriptor().Extract(fs, dir, null, null);
    return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
      .Select(p => new ArchiveInputInfo(p, Path.GetRelativePath(dir, p).Replace('\\', '/'), false)).ToList();
  }

  private byte[] Sectors(string hxcfe, string hfe) {
    var img = Path.Combine(this._tmp, Path.GetFileNameWithoutExtension(hfe) + "-back.img");
    Hxcfe(hxcfe, hfe, "RAW_LOADER", img);
    return File.ReadAllBytes(img);
  }

  [Test]
  public void GivenAnHxcfeImage_WhenWeRebuildItFromItsParts_ThenHxcfeDecodesTheSameSectors() {
    var hxcfe = RequireHxcfe();
    var disk = Disk(21);
    var theirs = this.Theirs(hxcfe, disk, "theirs");

    var image = StreamHfeImage.Read(File.ReadAllBytes(theirs));
    Assert.That(image.Tracks, Has.Count.EqualTo(image.Cylinders * 2));
    foreach (var t in image.Tracks.Values) {
      Assert.That(StreamHfeImage.Encode(StreamHfeImage.Decode(t.Pulses)), Is.EqualTo(t.Pulses), "our delta code is hxcfe's");
      Assert.That(StreamHfeImage.Decode(t.Pulses).Aggregate(0L, (a, d) => a + d), Is.LessThanOrEqualTo(t.Length));
    }

    var ours = Path.Combine(this._tmp, "ours.hfe");
    using (var fs = File.Create(ours))
      new StreamHfeFormatDescriptor().Create(fs, this.TakeApart(theirs), new FormatCreateOptions());
    Assert.That(this.Sectors(hxcfe, ours), Is.EqualTo(disk));
  }

  [Test]
  public void GivenOneTrackReplacedFromAnotherDisk_WhenHxcfeDecodes_ThenExactlyThatTrackChanged() {
    var hxcfe = RequireHxcfe();
    var a = Disk(22);
    var b = Disk(23);
    var edited = this.Theirs(hxcfe, a, "a");
    var track = this.TakeApart(this.Theirs(hxcfe, b, "b")).Single(i => i.ArchiveName == "tracks/track_002_1.bin");
    using (var fs = File.Open(edited, FileMode.Open, FileAccess.ReadWrite))
      new StreamHfeFormatDescriptor().Add(fs, [track]);

    // 720K: 9 sectors × 512 bytes per side; cylinder 2, side 1 is the sixth track.
    var expected = (byte[])a.Clone();
    Array.Copy(b, 5 * 9 * 512, expected, 5 * 9 * 512, 9 * 512);
    Assert.That(this.Sectors(hxcfe, edited), Is.EqualTo(expected));
  }

  [Test]
  public void GivenAnImageWeEncodeFromScratch_WhenHxcfeReadsIt_ThenItReportsOurGeometry() {
    var hxcfe = RequireHxcfe();
    var image = new StreamHfeImage { Sides = 2, InterfaceMode = 0 };
    for (var c = 0; c < 2; ++c)
      for (var s = 0; s < 2; ++s)
        image.Tracks[(c, s)] = new StreamHfeImage.Track(StreamHfeImage.Encode(Enumerable.Repeat(100u, 49_000)), 5_000_000);
    var path = Path.Combine(this._tmp, "scratch.hfe");
    using (var fs = File.Create(path)) image.Write(fs);
    var dir = hxcfe[..hxcfe.LastIndexOf('/')];
    var r = FsInteropToolbox.RunWsl($"cd '{dir}' && ./hxcfe -finput:{FsInteropToolbox.WinToWsl(path)} -infos");
    TestContext.Out.WriteLine(r.StdOut);
    Assert.That(r.StdOut, Does.Contain("HXC_STREAMHFE").And.Contain("Number of Track : 2").And.Contain("Number of Side : 2"));
  }
}
