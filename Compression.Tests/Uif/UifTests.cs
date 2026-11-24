using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Compression.Registry;
using FileFormat.Uif;

namespace Compression.Tests.Uif;

[TestFixture]
public class UifTests {
  /// <summary>
  /// Builds a version 1 UIF from blocks given as (sector, sectors, type, payload). For type 9 the
  /// <paramref name="blms"/> layout and the <paramref name="descriptor"/> of <paramref name="source"/> follow the table.
  /// </summary>
  internal static byte[] Build(uint sectors, uint sectorSize, (uint Sector, uint Count, uint Type, byte[] Payload)[] blocks,
      bool compressTable = true, ushort version = 1, ushort imageType = 8, byte key1 = 0, byte key2 = 0, uint lastDiff = 0,
      byte[]? blms = null, uint source = 1, byte[]? descriptor = null) {
    using var ms = new MemoryStream();
    var table = new byte[blocks.Length * UifImage.EntrySize];
    for (var i = 0; i < blocks.Length; ++i) {
      var (sector, count, type, payload) = blocks[i];
      var e = table.AsSpan(i * UifImage.EntrySize);
      BinaryPrimitives.WriteUInt64LittleEndian(e, type == 3 ? 0 : (ulong)ms.Position);
      BinaryPrimitives.WriteUInt32LittleEndian(e[8..], (uint)payload.Length);
      BinaryPrimitives.WriteUInt32LittleEndian(e[12..], sector);
      BinaryPrimitives.WriteUInt32LittleEndian(e[16..], count);
      BinaryPrimitives.WriteUInt32LittleEndian(e[20..], type);
      ms.Write(payload);
    }
    var tableAt = ms.Position;
    var stored = compressTable ? Zlib(table) : table;
    ms.Write(Header("blhr"u8, (uint)stored.Length + 8, compressTable, (uint)blocks.Length));
    ms.Write(stored);
    if (blms is not null) {
      var z = Zlib(blms);
      ms.Write(Header("blms"u8, (uint)z.Length + 8, true, (uint)blms.Length));
      ms.Write(z);
      var d = descriptor ?? [];
      var zd = d.Length == 0 ? [] : Zlib(d);
      ms.Write(Header("blss"u8, (uint)zd.Length + 12, true, (uint)d.Length));
      Span<byte> kind = stackalloc byte[4];
      BinaryPrimitives.WriteUInt32LittleEndian(kind, source);
      ms.Write(kind);
      ms.Write(zd);
    }
    var t = new byte[UifImage.TrailerSize];
    "bbis"u8.CopyTo(t);
    BinaryPrimitives.WriteUInt32LittleEndian(t.AsSpan(4), UifImage.TrailerSize);
    BinaryPrimitives.WriteUInt16LittleEndian(t.AsSpan(8), version);
    BinaryPrimitives.WriteUInt16LittleEndian(t.AsSpan(10), imageType);
    BinaryPrimitives.WriteUInt32LittleEndian(t.AsSpan(16), sectors);
    BinaryPrimitives.WriteUInt32LittleEndian(t.AsSpan(20), sectorSize);
    BinaryPrimitives.WriteUInt32LittleEndian(t.AsSpan(24), lastDiff);
    BinaryPrimitives.WriteUInt64LittleEndian(t.AsSpan(28), (ulong)tableAt);
    BinaryPrimitives.WriteUInt32LittleEndian(t.AsSpan(36), (uint)(stored.Length + 16 + UifImage.TrailerSize));
    t[56] = key1;
    t[57] = key2;
    ms.Write(t);
    return ms.ToArray();
  }

  private static byte[] Header(ReadOnlySpan<byte> signature, uint size, bool compressed, uint count) {
    var head = new byte[16];
    signature.CopyTo(head);
    BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(4), size);
    BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(8), compressed ? 1u : 0u);
    BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(12), count);
    return head;
  }

  /// <summary>A blms layout naming the image and descriptor at the offsets MagicISO uses (0x04 and 0x10).</summary>
  internal static byte[] Layout(string imagePath, string descriptorPath) {
    var names = Encoding.Latin1.GetBytes(imagePath + "\0" + descriptorPath + "\0");
    var layout = new byte[0x40 + names.Length];
    BinaryPrimitives.WriteUInt32LittleEndian(layout.AsSpan(0x04), 0x40);
    BinaryPrimitives.WriteUInt32LittleEndian(layout.AsSpan(0x10), (uint)(0x40 + imagePath.Length + 1));
    names.CopyTo(layout, 0x40);
    return layout;
  }

  internal static byte[] Zlib(byte[] data) {
    using var ms = new MemoryStream();
    using (var z = new FileFormat.Zlib.ZlibStream(ms, Compression.Core.Streams.CompressionStreamMode.Compress, leaveOpen: true))
      z.Write(data);
    return ms.ToArray();
  }

  internal static byte[] Pattern(int length, int seed) {
    var d = new byte[length];
    new Random(seed).NextBytes(d);
    return d;
  }

  private static Dictionary<string, byte[]> ExtractAll(byte[] uif, string? password = null) {
    var dir = Path.Combine(Path.GetTempPath(), "cwb-uif-" + Guid.NewGuid().ToString("N"));
    try {
      new UifFormatDescriptor().Extract(new MemoryStream(uif), dir, password, null);
      return Directory.GetFiles(dir).ToDictionary(f => Path.GetFileName(f)!, File.ReadAllBytes);
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
  }

  private static byte[] Extract(byte[] uif, string? password = null) => ExtractAll(uif, password)["image.iso"];

  internal static byte[] Write(byte[] image, string? password = null) {
    using var ms = new MemoryStream();
    UifWriter.Write(ms, new MemoryStream(image), image.Length, password);
    return ms.ToArray();
  }

  [Test]
  public void GivenStoredZeroAndCompressedRuns_WhenExtracted_ThenEachLandsAtItsSector() {
    var a = Pattern(2 * 2048, 1);
    var c = Pattern(3 * 2048, 2);
    var uif = Build(8, 2048, [(0, 2, 1, a), (2, 3, 3, []), (5, 3, 5, Zlib(c))]);
    var iso = Extract(uif);
    Assert.That(iso, Has.Length.EqualTo(8 * 2048));
    Assert.That(iso.AsSpan(0, 4096).SequenceEqual(a), Is.True);
    Assert.That(iso.AsSpan(4096, 3 * 2048).ContainsAnyExcept((byte)0), Is.False);
    Assert.That(iso.AsSpan(5 * 2048).SequenceEqual(c), Is.True);
  }

  [Test]
  public void GivenAShortStoredRun_WhenExtracted_ThenItIsZeroPadded() {
    var iso = Extract(Build(2, 2048, [(0, 2, 1, [1, 2, 3])]));
    Assert.That(iso.AsSpan(0, 3).ToArray(), Is.EqualTo(new byte[] { 1, 2, 3 }));
    Assert.That(iso.AsSpan(3).ContainsAnyExcept((byte)0), Is.False);
  }

  [Test]
  public void GivenAnUncompressedTableAndAPartialLastSector_WhenExtracted_ThenTheImageIsCutThere() {
    var a = Pattern(2 * 2048, 3);
    var iso = Extract(Build(2, 2048, [(0, 2, 1, a)], compressTable: false, lastDiff: 100));
    Assert.That(iso, Is.EqualTo(a[..(2048 + 100)]));
  }

  [Test]
  public void GivenAnImage_WhenListed_ThenMetadataAndTheImageSizeAppear() {
    var entries = new UifFormatDescriptor().List(new MemoryStream(Build(4, 2048, [(0, 4, 3, [])])), null);
    Assert.That(entries.Select(e => e.Name), Is.EqualTo(new[] { "metadata.ini", "image.iso" }));
    Assert.That(entries[1].OriginalSize, Is.EqualTo(4 * 2048));
  }

  [TestCase(2, (byte)1, (byte)0)]
  [TestCase(2, (byte)16, (byte)0)]
  [TestCase(1, (byte)0, (byte)2)]
  public void GivenKeyInTrailerEncryption_WhenListed_ThenItIsRefusedWithoutThrowing(int version, byte key1, byte key2) {
    var uif = Build(1, 2048, [(0, 1, 3, [])], version: (ushort)version, key1: key1, key2: key2);
    Assert.Throws<NotSupportedException>(() => UifImage.Read(new MemoryStream(uif)));
    Assert.That(new UifFormatDescriptor().List(new MemoryStream(uif), null).Single().Name, Is.EqualTo("FULL.uif"));
  }

  [TestCase((byte)5)]
  [TestCase((byte)17)]
  public void GivenAKeyByteThatSelectsNoEncryption_WhenRead_ThenItIsIgnoredAsTheFormatDoes(byte key1)
    => Assert.That(UifImage.Read(new MemoryStream(Build(1, 2048, [(0, 1, 3, [])], version: key1 == 5 ? (ushort)1 : (ushort)2, key1: key1))).Blocks, Has.Count.EqualTo(1));

  [Test]
  public void GivenAVersion2ImageWithoutKey_WhenExtracted_ThenItDecodes() {
    var a = Pattern(2048, 9);
    Assert.That(Extract(Build(1, 2048, [(0, 1, 5, Zlib(a))], version: 2)), Is.EqualTo(a));
  }

  [TestCase(3, 8)]
  [TestCase(4, 8)]
  [TestCase(1, 7)]
  [TestCase(1, 10)]
  public void GivenAnUnsupportedVersionOrType_WhenRead_ThenNotSupported(int version, int type)
    => Assert.Throws<NotSupportedException>(() => UifImage.Read(new MemoryStream(Build(1, 2048, [(0, 1, 3, [])], version: (ushort)version, imageType: (ushort)type))));

  [Test]
  public void GivenAMixedModeImageWithoutItsBlmsBlock_WhenRead_ThenInvalidData()
    => Assert.Throws<InvalidDataException>(() => UifImage.Read(new MemoryStream(Build(1, 2048, [(0, 1, 3, [])], imageType: 9))));

  [Test]
  public void GivenAMixedModeCueImage_WhenExtracted_ThenTheStoredNamesAndTheDescriptorComeBack() {
    var bin = Pattern(3 * 2048, 11);
    var cue = Encoding.ASCII.GetBytes("FILE \"disc.bin\" BINARY\r\n  TRACK 01 AUDIO\r\n    INDEX 01 00:00:00\r\n");
    var uif = Build(3, 2048, [(0, 3, 5, Zlib(bin))], imageType: 9, blms: Layout(@"C:\rips\disc.bin", @"C:\rips\disc.cue"), source: 1, descriptor: cue);
    var entries = new UifFormatDescriptor().List(new MemoryStream(uif), null);
    Assert.That(entries.Select(e => e.Name), Is.EqualTo(new[] { "metadata.ini", "disc.bin", "disc.cue" }));
    var files = ExtractAll(uif);
    Assert.That(files["disc.bin"], Is.EqualTo(bin));
    Assert.That(files["disc.cue"], Is.EqualTo(cue));
  }

  [Test]
  public void GivenAMixedModeCloneCdImage_WhenExtracted_ThenTheCcdAndSubAreSplit() {
    var img = Pattern(2 * 2048, 12);
    var ccd = Encoding.ASCII.GetBytes("[CloneCD]\r\nVersion=3\r\n");
    var sub = Pattern(96 * 2, 13);
    var blss = new byte[8 + ccd.Length + sub.Length];
    BinaryPrimitives.WriteUInt32LittleEndian(blss, (uint)ccd.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(blss.AsSpan(4), (uint)sub.Length);
    ccd.CopyTo(blss, 8);
    sub.CopyTo(blss, 8 + ccd.Length);
    var uif = Build(2, 2048, [(0, 2, 5, Zlib(img))], imageType: 9, blms: Layout("x.img", "x.ccd"), source: 3, descriptor: blss);
    var files = ExtractAll(uif);
    Assert.That(files.Keys, Is.EquivalentTo(new[] { "metadata.ini", "x.img", "x.ccd", "x.sub" }));
    Assert.That(files["x.ccd"], Is.EqualTo(ccd));
    Assert.That(files["x.sub"], Is.EqualTo(sub));
  }

  [Test]
  public void GivenAMixedModeImageWithUnusableStoredNames_WhenListed_ThenGenericNamesAreUsed() {
    var uif = Build(1, 2048, [(0, 1, 3, [])], imageType: 9, blms: Layout(@"C:\a\", "same.cue"), source: 1, descriptor: [1, 2, 3]);
    var names = new UifFormatDescriptor().List(new MemoryStream(uif), null).Select(e => e.Name);
    Assert.That(names, Is.EqualTo(new[] { "metadata.ini", "image.bin", "same.cue" }));
  }

  [TestCase(0u)]
  [TestCase(2u)]
  [TestCase(5u)]
  public void GivenASourceKindNoSampleProves_WhenRead_ThenNotSupported(uint source)
    => Assert.Throws<NotSupportedException>(() => UifImage.Read(new MemoryStream(Build(1, 2048, [(0, 1, 3, [])], imageType: 9, blms: Layout("a.bin", "a.cue"), source: source))));

  [Test]
  public void GivenAnUnknownBlockType_WhenRead_ThenInvalidData()
    => Assert.Throws<InvalidDataException>(() => UifImage.Read(new MemoryStream(Build(1, 2048, [(0, 1, 7, [])]))));

  [Test]
  public void GivenABlockPastTheLastSector_WhenRead_ThenInvalidData()
    => Assert.Throws<InvalidDataException>(() => UifImage.Read(new MemoryStream(Build(1, 2048, [(0, 2, 3, [])]))));

  [Test]
  public void GivenACorruptCompressedRun_WhenExtracted_ThenInvalidDataAndNoImageIsLeft() {
    var payload = Zlib(Pattern(2048, 4));
    payload[^1] ^= 0xFF; // the Adler-32 no longer matches
    var uif = Build(1, 2048, [(0, 1, 5, payload)]);
    var dir = Path.Combine(Path.GetTempPath(), "cwb-uifx-" + Guid.NewGuid().ToString("N"));
    try {
      Assert.Throws<InvalidDataException>(() => new UifFormatDescriptor().Extract(new MemoryStream(uif), dir, null, null));
      Assert.That(File.Exists(Path.Combine(dir, "image.iso")), Is.False);
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
  }

  [Test]
  public void GivenNoTrailer_WhenListed_ThenListFallsBack()
    => Assert.That(new UifFormatDescriptor().List(new MemoryStream(Encoding.ASCII.GetBytes(new string('x', 200))), null).Single().Name, Is.EqualTo("FULL.uif"));

  [Test]
  public void GivenATruncatedImage_WhenListed_ThenListFallsBack() {
    var uif = Write(Pattern(5000, 5));
    var cut = uif[..^70].Concat(uif[^64..]).ToArray(); // the trailer survives, the table does not
    Assert.That(new UifFormatDescriptor().List(new MemoryStream(cut), null).Single().Name, Is.EqualTo("FULL.uif"));
  }

  // ── the password key ──────────────────────────────────────────────────

  [Test]
  public void GivenAPassword_WhenTheKeyIsDerived_ThenItIsTheSumOfItsFourZeroPaddedWords() {
    // "secret" zero-padded: the first word is the only non-zero one, so the key is the password itself.
    Assert.That(UifImage.PasswordKey("secret")[..8], Is.EqualTo("secret\0\0"u8.ToArray()));
    // 9 bytes: the 9th lands in the second word, which is added to the first.
    var key = UifImage.PasswordKey("ABCDEFGHI");
    var expected = BinaryPrimitives.ReadUInt64LittleEndian("ABCDEFGH"u8) + (ulong)'I';
    Assert.That(BinaryPrimitives.ReadUInt64LittleEndian(key), Is.EqualTo(expected));
  }

  [Test]
  public void GivenAPasswordLongerThan31Characters_WhenTheKeyIsDerived_ThenOnlyTheFirst31Count()
    => Assert.That(UifImage.PasswordKey(new string('p', 31) + "extra"), Is.EqualTo(UifImage.PasswordKey(new string('p', 31))));

  // ── writer ────────────────────────────────────────────────────────────

  [TestCase(0)]
  [TestCase(1)]
  [TestCase(2047)]
  [TestCase(2048)]
  [TestCase(2049)]
  [TestCase(128 * 2048)]
  [TestCase(128 * 2048 + 1)]
  [TestCase(300 * 2048 + 77)]
  public void GivenAnImageOfAnyLength_WhenWrittenAndRead_ThenItRoundTrips(int length) {
    var image = Pattern(length, length);
    Assert.That(Extract(Write(image)), Is.EqualTo(image));
  }

  [Test]
  public void GivenZeroStretches_WhenWritten_ThenTheyBecomeZeroFillEntriesAndRunsBreakAt128Sectors() {
    var image = new byte[300 * 2048];
    Pattern(10 * 2048, 6).CopyTo(image, 0);
    Pattern(2048, 7).CopyTo(image, 200 * 2048);
    var uif = Write(image);
    var read = UifImage.Read(new MemoryStream(uif));
    Assert.That(read.Blocks.Select(b => (b.Sector, b.Sectors, b.Type)), Is.EqualTo(new[] {
      (0u, 10u, 5u), (10u, 118u, 3u), (128u, 72u, 3u), (200u, 1u, 5u), (201u, 55u, 3u), (256u, 44u, 3u),
    }));
    Assert.That(Extract(uif), Is.EqualTo(image));
  }

  [Test]
  public void GivenAWrittenImage_WhenItsTrailerIsRead_ThenItCarriesTheMd5OfEverythingBeforeIt() {
    var uif = Write(Pattern(9000, 8));
    var read = UifImage.Read(new MemoryStream(uif));
    Assert.That(read.Hash, Is.EqualTo(MD5.HashData(uif.AsSpan(0, uif.Length - UifImage.TrailerSize))));
    Assert.That((read.Version, read.ImageType, read.SectorSize), Is.EqualTo(((ushort)1, (ushort)8, 2048u)));
  }

  [Test]
  public void GivenAPassword_WhenWrittenAndReadWithIt_ThenItRoundTrips() {
    var image = Pattern(5 * 2048 + 13, 14);
    var uif = Write(image, "Uif-Secret_31");
    Assert.That(uif.AsSpan().IndexOf("bsdr"u8), Is.GreaterThan(0));
    Assert.That(Extract(uif, "Uif-Secret_31"), Is.EqualTo(image));
  }

  [Test]
  public void GivenAProtectedImage_WhenReadWithoutAPassword_ThenListShowsEncryptedEntriesAndExtractAsks() {
    var uif = Write(Pattern(3 * 2048, 15), "pw");
    var entries = new UifFormatDescriptor().List(new MemoryStream(uif), null);
    Assert.That(entries.Select(e => e.Name), Is.EqualTo(new[] { "metadata.ini", "image.iso" }));
    Assert.That(entries[1].IsEncrypted, Is.True);
    Assert.That(entries[1].OriginalSize, Is.EqualTo(3 * 2048));
    var e = Assert.Throws<ArchivePasswordException>(() => ExtractAll(uif));
    Assert.That(e!.PasswordWasSupplied, Is.False);
  }

  [Test]
  public void GivenAProtectedImage_WhenReadWithTheWrongPassword_ThenThePasswordIsRejected() {
    var uif = Write(Pattern(2048, 16), "right");
    var e = Assert.Throws<ArchivePasswordException>(() => UifImage.Read(new MemoryStream(uif), "wrong"));
    Assert.That(e!.PasswordWasSupplied, Is.True);
    Assert.That(new UifFormatDescriptor().List(new MemoryStream(uif), "wrong").Single().Name, Is.EqualTo("FULL.uif"));
  }

  [Test]
  public void GivenAPasswordMagicIsoCannotStore_WhenWritten_ThenItIsRefused() {
    Assert.Throws<ArgumentException>(() => Write([1, 2, 3], new string('x', 32)));
    Assert.Throws<ArgumentException>(() => Write([1, 2, 3], "\u20AC"));
    Assert.Throws<ArgumentException>(() => Write([1, 2, 3], ""));
  }

  [Test]
  public void GivenAnExtractedImage_WhenCreatedThroughTheDescriptor_ThenMetadataIsSkippedAndTheImageRoundTrips() {
    var image = Pattern(4 * 2048, 17);
    using var ms = new MemoryStream();
    new UifFormatDescriptor().Create(ms, [ArchiveInputInfo.InMemory("metadata.ini", "[Uif]\n"u8), ArchiveInputInfo.InMemory("image.iso", image)],
      new FormatCreateOptions { Password = "pw" });
    Assert.That(Extract(ms.ToArray(), "pw"), Is.EqualTo(image));
  }

  [Test]
  public void GivenTwoImages_WhenCreated_ThenItIsRefused()
    => Assert.Throws<ArgumentException>(() => new UifFormatDescriptor().Create(new MemoryStream(),
      [ArchiveInputInfo.InMemory("a.iso", new byte[] { 1 }), ArchiveInputInfo.InMemory("b.iso", new byte[] { 2 })], new FormatCreateOptions()));
}

/// <summary>
/// Images MagicISO 5.5 build 0281 wrote (<c>ReferenceVectors/make-uif.sh</c>): every one decodes to the
/// files MagicISO was given, whose SHA-256 <c>uif-sources.sha256</c> records. uif2iso 0.1.7c decodes
/// the ISO, BIN/CUE and CloneCD ones to the same bytes; MagicISO's own Decompress UIF Image does so for all.
/// </summary>
[TestFixture]
public class UifReferenceVectorTests {
  internal const string Password = "Uif-Secret_31";

  internal static string VectorPath(string name)
    => Path.Combine(Documentation.FilesystemReadmeIsCurrentTests.RepositoryRoot(), "Compression.Tests", "Uif", "ReferenceVectors", name);

  private static Dictionary<string, string> Sources()
    => File.ReadAllLines(VectorPath("uif-sources.sha256")).Where(l => l.Length > 0)
      .Select(l => l.Split("  ")).ToDictionary(p => p[2], p => p[0]);

  private static Dictionary<string, string> ExtractSha(string uif, string? password) {
    var dir = Path.Combine(Path.GetTempPath(), "cwb-uifv-" + Guid.NewGuid().ToString("N"));
    try {
      using (var fs = File.OpenRead(VectorPath(uif)))
        new UifFormatDescriptor().Extract(fs, dir, password, null);
      return Directory.GetFiles(dir).Where(f => Path.GetFileName(f) != "metadata.ini")
        .ToDictionary(f => Path.GetFileName(f)!, f => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(f))));
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
  }

  [TestCase("plain.uif", null)]
  [TestCase("password.uif", Password)]
  public void GivenAnIsoImage_WhenExtracted_ThenItIsTheIsoMagicIsoWasGiven(string uif, string? password)
    => Assert.That(ExtractSha(uif, password), Is.EqualTo(new Dictionary<string, string> { ["image.iso"] = Sources()["plain.iso"] }));

  [TestCase("mixed.uif", null)]
  [TestCase("mixed-password.uif", Password)]
  public void GivenABinCueImage_WhenExtracted_ThenTheBinAndCueComeBackUnderTheirNames(string uif, string? password) {
    var s = Sources();
    Assert.That(ExtractSha(uif, password), Is.EqualTo(new Dictionary<string, string> { ["mixed.bin"] = s["mixed.bin"], ["mixed.cue"] = s["mixed.cue"] }));
  }

  [Test]
  public void GivenACloneCdImage_WhenExtracted_ThenTheImgCcdAndSubComeBack() {
    var s = Sources();
    Assert.That(ExtractSha("clonecd.uif", null), Is.EqualTo(new Dictionary<string, string> {
      ["clone.img"] = s["clone.img"], ["clone.ccd"] = s["clone.ccd"], ["clone.sub"] = s["clone.sub"],
    }));
  }

  [Test]
  public void GivenANeroImage_WhenExtracted_ThenItIsTheNrgMagicIsoWasGiven()
    => Assert.That(ExtractSha("nero.uif", null), Is.EqualTo(new Dictionary<string, string> { ["nero.nrg"] = Sources()["nero.nrg"] }));

  [Test]
  public void GivenAMagicIsoImage_WhenRead_ThenTheTrailerHashIsTheMd5OfEverythingBeforeIt() {
    var bytes = File.ReadAllBytes(VectorPath("plain.uif"));
    Assert.That(UifImage.Read(new MemoryStream(bytes)).Hash, Is.EqualTo(MD5.HashData(bytes.AsSpan(0, bytes.Length - UifImage.TrailerSize))));
  }

  [Test]
  public void GivenAProtectedMagicIsoImage_WhenReadWithTheWrongPassword_ThenThePasswordIsRejected() {
    using var fs = File.OpenRead(VectorPath("mixed-password.uif"));
    Assert.That(Assert.Throws<ArchivePasswordException>(() => UifImage.Read(fs, "nope"))!.PasswordWasSupplied, Is.True);
  }

  [Test]
  public void GivenTheMagicIsoIso_WhenOurWriterCompressesIt_ThenTheBlockLayoutMatchesMagicIsos() {
    using var fs = File.OpenRead(VectorPath("plain.uif"));
    var theirs = UifImage.Read(fs);
    using var iso = new MemoryStream();
    theirs.CopyTo(fs, iso);
    using var ours = new MemoryStream();
    iso.Position = 0;
    UifWriter.Write(ours, iso, iso.Length);
    ours.Position = 0;
    var read = UifImage.Read(ours);
    Assert.That(read.Blocks.Select(b => (b.Sector, b.Sectors, b.Type)), Is.EqualTo(theirs.Blocks.Select(b => (b.Sector, b.Sectors, b.Type))));
  }
}

/// <summary>
/// uif2iso 0.1.7c (Luigi Auriemma, GPL, built from its published source) and MagicISO's console tool
/// (miso.exe 3.1.113 from MagicISO 5.5 build 0281, under wine) as oracles. Real images: two published
/// on archive.org ("Quake Shareware" and "Doom II German") from <c>CWB_UIF_DIR</c>. Our writer: uif2iso
/// (plain and password-protected) and miso (plain; it opens no protected image) decode what it writes to
/// the original ISO. uif2iso comes from <c>CWB_UIF2ISO</c>, miso from <c>CWB_MISO</c> (a WSL command line,
/// e.g. <c>env WINEPREFIX=/path wine '/path/miso.exe'</c>); skipped when unset unless <c>CWB_REQUIRE_UIF=1</c>.
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public class UifExternalConformanceTests {
  private static string Require(string variable) {
    var value = Environment.GetEnvironmentVariable(variable);
    if (!string.IsNullOrEmpty(value) && FsInteropToolbox.WslAvailable) return value;
    var hint = $"UIF oracle not configured: set {variable}.";
    if (Environment.GetEnvironmentVariable("CWB_REQUIRE_UIF") == "1") Assert.Fail(hint);
    Assert.Ignore(hint);
    return "";
  }

  /// <summary>A path for the shell, quoted: the sample names contain spaces.</summary>
  private static string Quote(string path) => OperatingSystem.IsWindows() ? FsInteropToolbox.WinToWsl(path) : $"'{path}'";

  private static string TempDir() {
    var tmp = Path.Combine(Path.GetTempPath(), "cwb-uifo-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmp);
    return tmp;
  }

  [TestCase("Quake Shareware.UIF")]
  [TestCase("Doom II German.UIF")]
  public void GivenARealMagicIsoImage_WhenExtracted_ThenItEqualsUif2isosIso(string name) {
    var dir = Require("CWB_UIF_DIR");
    var tool = Require("CWB_UIF2ISO");
    var uif = Path.Combine(dir, name);
    var tmp = TempDir();
    try {
      var theirs = Path.Combine(tmp, "theirs.iso");
      // uif2iso asks before overwriting and picks the extension itself; feed it "y" and a fresh name.
      var r = FsInteropToolbox.RunWsl($"yes | '{tool}' {Quote(uif)} {Quote(theirs)}");
      TestContext.Out.WriteLine($"{r.ExitCode}\n{r.StdOut}\n{r.StdErr}");
      Assert.That(File.Exists(theirs), Is.True, "uif2iso wrote an ISO");

      var ours = Path.Combine(tmp, "ours");
      using (var fs = File.OpenRead(uif))
        new UifFormatDescriptor().Extract(fs, ours, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(ours, "image.iso")), Is.EqualTo(File.ReadAllBytes(theirs)));
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [TestCase(null)]
  [TestCase(UifReferenceVectorTests.Password)]
  public void GivenOurUif_WhenUif2isoConvertsIt_ThenItIsTheOriginalIso(string? password) {
    var tool = Require("CWB_UIF2ISO");
    var tmp = TempDir();
    try {
      var iso = UifTests.Pattern(200 * 2048 + 5, 21);
      Array.Clear(iso, 60 * 2048, 30 * 2048);
      var uif = Path.Combine(tmp, "ours.uif");
      File.WriteAllBytes(uif, UifTests.Write(iso, password));
      var theirs = Path.Combine(tmp, "theirs.iso");
      var r = FsInteropToolbox.RunWsl($"printf '%s\\ny\\n' '{password}' | '{tool}' {Quote(uif)} {Quote(theirs)}");
      TestContext.Out.WriteLine($"{r.ExitCode}\n{r.StdOut}\n{r.StdErr}");
      Assert.That(File.ReadAllBytes(theirs), Is.EqualTo(iso));
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test]
  public void GivenOurUif_WhenMagicIsoConvertsIt_ThenItIsTheOriginalIso() {
    var miso = Require("CWB_MISO");
    var tmp = TempDir();
    try {
      var iso = UifTests.Pattern(150 * 2048 + 700, 22);
      Array.Clear(iso, 10 * 2048, 40 * 2048);
      File.WriteAllBytes(Path.Combine(tmp, "ours.uif"), UifTests.Write(iso));
      // miso takes paths relative to its working directory (absolute Unix paths are not Windows paths).
      var r = FsInteropToolbox.RunWsl($"cd {Quote(tmp)} && {miso} theirs.iso -conv iso ours.uif -py");
      TestContext.Out.WriteLine($"{r.ExitCode}\n{r.StdOut}\n{r.StdErr}");
      Assert.That(File.ReadAllBytes(Path.Combine(tmp, "theirs.iso")), Is.EqualTo(iso));
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }
}
