using System.Reflection;
using System.Security.Cryptography;
using Compression.Registry;
using FileFormat.Pea;

namespace Compression.Tests.Pea;

/// <summary>
/// PEA: archives PeaZip 11.4 wrote with every compression level and most check algorithms
/// (Pea/ReferenceVectors), our writer, and PeaZip's own pea (headless) as the oracle for what we
/// write.
/// </summary>
[TestFixture]
public class PeaTests {

  private static readonly byte[] Fox = System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog.\n", 300)));
  private static readonly byte[] Hello = "hello pea\n"u8.ToArray();
  private static readonly byte[] Nested = "nested\n"u8.ToArray();
  private static readonly DateTime SourceTime = new(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);

  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_pea_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(this._tmpDir, true); } catch { /* best effort */ }
  }

  [TestCase("store.pea")]    // PCOMPRESS0, CRC32 volume/object, no stream check
  [TestCase("deflate3.pea")] // PCOMPRESS1, Adler-32 volume, CRC-64 objects, SHA-256 stream
  [TestCase("deflate6.pea")] // PCOMPRESS2, CRC32, CRC32, SHA-256
  [TestCase("deflate9.pea")] // PCOMPRESS3, SHA-1 volume, MD5 objects, SHA-512 stream
  [TestCase("sha3.pea")]     // SHA3-256 volume, SHA3-512 objects, BLAKE2b stream
  [TestCase("misc.pea")]     // RIPEMD-160 volume, Whirlpool objects, BLAKE2s stream
  [Category("ThemVsUs")]
  public void GivenArchiveWrittenByPeaZip_WhenRead_ThenEveryObjectAndCheckVerifies(string vector) {
    var entries = PeaArchive.Read(new MemoryStream(LoadVector(vector)));

    Assert.That(entries.Select(e => (e.Path, e.IsDirectory)), Is.EquivalentTo(new[] {
      ("hello.txt", false), ("fox.txt", false), ("empty.txt", false), ("sub", true), ("sub/n.txt", false) }));
    Assert.That(entries.Single(e => e.Path == "hello.txt").Data, Is.EqualTo(Hello));
    Assert.That(entries.Single(e => e.Path == "fox.txt").Data, Is.EqualTo(Fox));
    Assert.That(entries.Single(e => e.Path == "empty.txt").Data, Is.Empty);
    Assert.That(entries.Single(e => e.Path == "sub/n.txt").Data, Is.EqualTo(Nested));
    Assert.That(entries.Single(e => e.Path == "fox.txt").LastModified, Is.EqualTo(SourceTime));
  }

  [Test, Category("ThemVsUs")]
  public void GivenMultiBlockArchiveWrittenByPeaZip_WhenRead_ThenAll3MiBDecode() {
    var big = PeaArchive.Read(new MemoryStream(LoadVector("big.pea"))).Single();
    var expected = Enumerable.Range(0, 3_000_000).Select(i => (byte)((i * 131 + (i >> 7)) % 256)).ToArray();
    Assert.That(big.Path, Is.EqualTo("big.bin"));
    Assert.That(big.Data, Is.EqualTo(expected));
  }

  // ── volume sets ─────────────────────────────────────────────────────

  [TestCase("vol", 5000)] // PCOMPRESS0, CRC32 volume checks: two full volumes and a short one
  [TestCase("volz", 200)] // PCOMPRESS2, SHA-1 volume checks
  [Category("ThemVsUs")]
  public void GivenVolumeSetWrittenByPeaZip_WhenReadVolumes_ThenEveryObjectAndCheckVerifies(string set, int volumeSize) {
    var volumes = LoadVolumes(set);
    Assert.That(volumes.SkipLast(1).Select(v => v.Length), Is.All.EqualTo(volumeSize));

    var entries = PeaArchive.ReadVolumes(volumes);

    AssertSmallTree(entries);
  }

  [TestCase(1)]
  [TestCase(3)]
  [Category("ThemVsUs")]
  public void GivenVolumeSetOnDisk_WhenAnyVolumeIsOpened_ThenTheWholeSetIsRead(int opened) {
    var volumes = LoadVolumes("vol");
    var setBase = Path.Combine(this._tmpDir, "vol");
    for (var i = 0; i < volumes.Count; ++i) File.WriteAllBytes(PeaArchive.VolumePath(setBase, i + 1), volumes[i]);

    using var stream = File.OpenRead(PeaArchive.VolumePath(setBase, opened));
    var entries = new PeaFormatDescriptor().List(stream, null);

    Assert.That(entries.Single(e => e.Name == "fox.txt").OriginalSize, Is.EqualTo(Fox.Length));
  }

  [Test, Category("Exception")]
  public void GivenVolumeSetWithMiddleVolumeMissing_WhenRead_ThenRefused() {
    var volumes = LoadVolumes("vol");
    Assert.That(() => PeaArchive.ReadVolumes([volumes[0], volumes[2]]), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("Exception")]
  public void GivenVolumeSetMissingItsLastVolumeOnDisk_WhenRead_ThenRefused() {
    var volumes = LoadVolumes("vol");
    var setBase = Path.Combine(this._tmpDir, "vol");
    for (var i = 0; i < volumes.Count - 1; ++i) File.WriteAllBytes(PeaArchive.VolumePath(setBase, i + 1), volumes[i]);
    using var stream = File.OpenRead(PeaArchive.VolumePath(setBase, 1));
    Assert.That(() => PeaArchive.Read(stream), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("Exception")]
  public void GivenFlippedByteInSecondVolume_WhenRead_ThenItsVolumeCheckRefusesIt() {
    var volumes = LoadVolumes("vol");
    volumes[1][100] ^= 0x01;
    Assert.That(() => PeaArchive.ReadVolumes(volumes), Throws.InstanceOf<InvalidDataException>().With.Message.Contains("volume 2"));
  }

  [TestCase("/d/a.000001.pea", "/d/a")]
  [TestCase("/d/a.b.000042.PEA", "/d/a.b")]
  [TestCase("/d/a.1234567.pea", "/d/a")]
  [TestCase("/d/a.pea", null)]
  [TestCase("/d/a.00001.pea", null)]
  [Category("EdgeCase")]
  public void GivenPath_WhenSetBaseIsAsked_ThenOnlyVolumeNamesHaveOne(string path, string? expected)
    => Assert.That(PeaArchive.VolumeSetBase(path), Is.EqualTo(expected));

  [TestCase(1, "x.000001.pea")]
  [TestCase(999999, "x.999999.pea")]
  [TestCase(1000000, "x.1000000.pea")]
  [Category("EdgeCase")]
  public void GivenVolumeNumber_WhenNamed_ThenSixDigitsAtLeast(int number, string expected)
    => Assert.That(PeaArchive.VolumePath("x", number), Is.EqualTo(expected));

  [TestCase(0, PeaArchive.Check.Crc32, 3000)]
  [TestCase(2, PeaArchive.Check.Sha1, 100)]
  [TestCase(2, PeaArchive.Check.None, 150)]
  [TestCase(0, PeaArchive.Check.Whirlpool, 1)] // below check + 10: raised to that, as pea does
  [Category("RoundTrip")]
  public void GivenVolumeSize_WhenWritten_ThenEveryVolumeButTheLastIsThatSizeAndTheSetRoundTrips(int compression, PeaArchive.Check volume, int volumeSize) {
    var inputs = SmallInputs();
    var (first, rest) = WriteSet(inputs, compression, volume, volumeSize);
    var volumes = new List<byte[]> { first };
    volumes.AddRange(rest);
    var expectedSize = Math.Max(volumeSize, VolumeTagSize(volume) + 10);

    Assert.That(volumes, Has.Count.GreaterThan(1));
    Assert.That(volumes.SkipLast(1).Select(v => v.Length), Is.All.EqualTo(expectedSize));
    Assert.That(volumes[^1].Length, Is.InRange(VolumeTagSize(volume) + 1, expectedSize));
    var entries = PeaArchive.ReadVolumes(volumes);
    foreach (var input in inputs)
      Assert.That(entries.Single(e => e.Path == input.Path).Data, Is.EqualTo(input.Data), input.Path);
  }

  [Test, Category("EdgeCase")]
  public void GivenArchiveFillingItsVolumesExactly_WhenWritten_ThenNoEmptyVolumeFollows() {
    using var single = new MemoryStream();
    PeaArchive.Write(single, SmallInputs(), 0, volumeCheck: PeaArchive.Check.Crc32);
    var archiveBytes = (int)single.Length - 4;
    // volumes that each hold an exact share of the archive, plus their checks
    var divisor = Enumerable.Range(2, 50).First(d => archiveBytes % d == 0 && archiveBytes / d > 10);
    var (first, rest) = WriteSet(SmallInputs(), 0, PeaArchive.Check.Crc32, archiveBytes / divisor + 4);

    Assert.That(rest, Has.Count.EqualTo(divisor - 1));
    Assert.That(rest[^1].Length, Is.EqualTo(archiveBytes / divisor + 4));
    Assert.That(PeaArchive.ReadVolumes([first, .. rest]).Count, Is.EqualTo(PeaArchive.Read(new MemoryStream(single.ToArray())).Count));
  }

  [Test, Category("HappyPath")]
  public void GivenVolumeSizeOption_WhenCreatedThroughTheDescriptor_ThenVolumesLandBesideTheFirst() {
    var first = Path.Combine(this._tmpDir, "set.000001.pea");
    using (var f = File.Create(first))
      new PeaFormatDescriptor().Create(f, [ArchiveInputInfo.InMemory("fox.txt", Fox)],
        new FormatCreateOptions { MethodName = "store", FormatSpecific = new Dictionary<string, string> { [FormatOptionKeys.VolumeSize] = "4k" } });

    var paths = PeaArchive.VolumePaths(first);
    Assert.That(paths.Select(Path.GetFileName), Is.EqualTo(new[] { "set.000001.pea", "set.000002.pea", "set.000003.pea", "set.000004.pea" }));
    Assert.That(paths.SkipLast(1).Select(p => new FileInfo(p).Length), Is.All.EqualTo(4096));
    using var read = File.OpenRead(first);
    Assert.That(new PeaFormatDescriptor().ExtractEntryToMemory(read, "fox.txt", null), Is.EqualTo(Fox));
  }

  [TestCase("set.pea")]
  [TestCase("set.000002.pea")]
  [Category("Exception")]
  public void GivenVolumeSizeOptionAndATargetNotNamedAsVolume1_WhenCreated_ThenRefused(string name) {
    using var f = File.Create(Path.Combine(this._tmpDir, name));
    Assert.That(() => new PeaFormatDescriptor().Create(f, [ArchiveInputInfo.InMemory("a", [1])],
      new FormatCreateOptions { FormatSpecific = new Dictionary<string, string> { [FormatOptionKeys.VolumeSize] = "100" } }),
      Throws.ArgumentException);
  }

  [Test, Category("Exception")]
  public void GivenVolumeSizeOptionAndAMemoryStream_WhenCreated_ThenRefused()
    => Assert.That(() => new PeaFormatDescriptor().Create(new MemoryStream(), [ArchiveInputInfo.InMemory("a", [1])],
      new FormatCreateOptions { FormatSpecific = new Dictionary<string, string> { [FormatOptionKeys.VolumeSize] = "100" } }),
      Throws.ArgumentException);

  [Test, Category("ExternalInterop")]
  public void GivenOurVolumeSet_WhenPeaZipExtractsIt_ThenEveryFileMatches() {
    var pea = RequirePea();
    var setBase = Path.Combine(this._tmpDir, "ours");
    using (var f = File.Create(PeaArchive.VolumePath(setBase, 1)))
      PeaArchive.Write(f, Inputs(), 0, PeaArchive.Check.Md5, PeaArchive.Check.Sha256, PeaArchive.Check.Sha1,
        volumeSize: 1_000_000, openVolume: n => File.Create(PeaArchive.VolumePath(setBase, n)));
    Assert.That(File.Exists(PeaArchive.VolumePath(setBase, 3)), "the set should span several volumes");
    var outDir = Path.Combine(this._tmpDir, "out");
    Directory.CreateDirectory(outDir);

    var r = FsInteropToolbox.RunWsl($"timeout 120 xvfb-run -a '{pea}' UNPEA '{FsInteropToolbox.WinToWsl(PeaArchive.VolumePath(setBase, 1))}' '{FsInteropToolbox.WinToWsl(outDir)}/' RESETDATE RESETATTR EXTRACT2DIR HIDDEN '' ''");
    Assert.That(r.ExitCode, Is.EqualTo(0), $"pea UNPEA failed:\n{r.StdOut}\n{r.StdErr}");
    var root = Directory.GetDirectories(outDir).Single();
    foreach (var input in Inputs())
      Assert.That(File.ReadAllBytes(Path.Combine(root, input.Path.Replace('/', Path.DirectorySeparatorChar))), Is.EqualTo(input.Data), input.Path);
  }

  private static (byte[] First, List<byte[]> Others) WriteSet(List<PeaArchive.Input> inputs, int compression, PeaArchive.Check volume, long volumeSize,
      PeaArchive.Check stream = PeaArchive.Check.Sha256, string? password = null) {
    var rest = new SortedDictionary<int, MemoryStream>();
    using var first = new MemoryStream();
    PeaArchive.Write(first, inputs, compression, PeaArchive.Check.Crc32, stream, volume,
      volumeSize, n => rest[n] = new MemoryStream(), password);
    Assert.That(rest.Keys, Is.EqualTo(Enumerable.Range(2, rest.Count)), "volumes open in order from 2");
    return (first.ToArray(), rest.Values.Select(m => m.ToArray()).ToList());
  }

  private static int VolumeTagSize(PeaArchive.Check check) => check switch {
    PeaArchive.Check.None => 0,
    PeaArchive.Check.Crc32 or PeaArchive.Check.Adler32 => 4,
    PeaArchive.Check.Sha1 => 20,
    PeaArchive.Check.Whirlpool => 64,
    _ => throw new ArgumentOutOfRangeException(nameof(check)),
  };

  private static List<PeaArchive.Input> SmallInputs() => [
    new("hello.txt", Hello, SourceTime),
    new("dir/fox.txt", Fox, SourceTime),
  ];

  private static void AssertSmallTree(List<PeaEntry> entries) {
    Assert.That(entries.Select(e => (e.Path, e.IsDirectory)), Is.EquivalentTo(new[] {
      ("hello.txt", false), ("fox.txt", false), ("empty.txt", false), ("sub", true), ("sub/n.txt", false) }));
    Assert.That(entries.Single(e => e.Path == "hello.txt").Data, Is.EqualTo(Hello));
    Assert.That(entries.Single(e => e.Path == "fox.txt").Data, Is.EqualTo(Fox));
    Assert.That(entries.Single(e => e.Path == "empty.txt").Data, Is.Empty);
    Assert.That(entries.Single(e => e.Path == "sub/n.txt").Data, Is.EqualTo(Nested));
  }

  private static List<byte[]> LoadVolumes(string set) {
    var volumes = new List<byte[]>();
    for (var n = 1; ; ++n) {
      var name = $"{set}.{n:D6}.pea";
      if (Assembly.GetExecutingAssembly().GetManifestResourceInfo($"PeaVectors.{name}") is null) break;
      volumes.Add(LoadVector(name));
    }
    Assert.That(volumes, Is.Not.Empty, $"no embedded volumes for {set}");
    return volumes;
  }

  [Test, Category("Exception")]
  public void GivenFlippedDataByte_WhenRead_ThenAChecksumRefusesIt() {
    var bytes = LoadVector("store.pea");
    var at = bytes.AsSpan().IndexOf("The quick"u8);
    bytes[at + 5] ^= 0x01;
    Assert.That(() => PeaArchive.Read(new MemoryStream(bytes)), Throws.InstanceOf<InvalidDataException>());
  }

  [TestCase(0x44)] // TRIATS
  [TestCase(0x4C)] // HRISAT
  [Category("Exception")]
  public void GivenCascadeEncryptedStreamHeader_WhenRead_ThenRefusedAsUnsupported(int algo) {
    var bytes = LoadVector("deflate6.pea");
    bytes[18] = (byte)algo;
    Assert.That(() => PeaArchive.Read(new MemoryStream(bytes), "password"), Throws.InstanceOf<NotSupportedException>());
  }

  // ── encryption ──────────────────────────────────────────────────────

  [TestCase("eax256.pea")] // AES-256 EAX, PCOMPRESS2
  [TestCase("tf256.pea")]  // Twofish-256 EAX, PCOMPRESS0
  [TestCase("sp256.pea")]  // Serpent-256 EAX, PCOMPRESS1, CRC-64 objects
  [TestCase("eax.pea")]    // AES-128 EAX
  [TestCase("tf.pea")]     // Twofish-128 EAX
  [TestCase("sp.pea")]     // Serpent-128 EAX
  [TestCase("hmac.pea")]   // AES-128 CTR + HMAC-SHA-1
  [Category("ThemVsUs")]
  public void GivenEncryptedArchiveWrittenByPeaZip_WhenReadWithItsPassword_ThenEveryObjectDecrypts(string vector) {
    var entries = PeaArchive.Read(new MemoryStream(LoadVector(vector)), "password");

    AssertSmallTree(entries);
    Assert.That(entries, Is.All.Matches<PeaEntry>(e => e.IsEncrypted));
  }

  [TestCase("eax256.pea", "Password")]
  [TestCase("tf256.pea", "passwor")]
  [TestCase("sp256.pea", "password ")]
  [TestCase("eax.pea", "x")]
  [TestCase("hmac.pea", "wrong")]
  [Category("Exception")]
  public void GivenEncryptedArchiveWrittenByPeaZip_WhenReadWithAWrongPassword_ThenRefusedBeforeDecrypting(string vector, string password)
    => Assert.That(() => PeaArchive.Read(new MemoryStream(LoadVector(vector)), password),
      Throws.InstanceOf<ArchivePasswordException>().With.Property(nameof(ArchivePasswordException.PasswordWasSupplied)).True);

  [TestCase(null)]
  [TestCase("")]
  [Category("Exception")]
  public void GivenEncryptedArchive_WhenReadWithoutAPassword_ThenAPasswordIsAskedFor(string? password)
    => Assert.That(() => PeaArchive.Read(new MemoryStream(LoadVector("eax256.pea")), password),
      Throws.InstanceOf<ArchivePasswordException>().With.Property(nameof(ArchivePasswordException.PasswordWasSupplied)).False);

  [Test, Category("Exception")]
  public void GivenEncryptedArchiveWithAFlippedCiphertextByte_WhenRead_ThenTheTagRefusesIt() {
    var bytes = LoadVector("eax256.pea");
    bytes[60] ^= 0x01;
    // keep the volume check right, so the stream tag is what catches it
    BitConverterLe(Compression.Core.Checksums.Crc32.Compute(bytes.AsSpan(0, bytes.Length - 4))).CopyTo(bytes, bytes.Length - 4);
    Assert.That(() => PeaArchive.Read(new MemoryStream(bytes), "password"),
      Throws.InstanceOf<InvalidDataException>().With.Message.Contains("authentication"));
  }

  [TestCase(PeaArchive.Check.Aes256Eax, 2)]
  [TestCase(PeaArchive.Check.Twofish256Eax, 0)]
  [TestCase(PeaArchive.Check.Serpent256Eax, 3)]
  [TestCase(PeaArchive.Check.AesEax, 1)]
  [TestCase(PeaArchive.Check.TwofishEax, 2)]
  [TestCase(PeaArchive.Check.SerpentEax, 2)]
  [TestCase(PeaArchive.Check.AesHmac, 0)]
  [Category("RoundTrip")]
  public void GivenPassword_WhenWrittenAndRead_ThenEveryByteRoundTripsAndTheWrongPasswordIsRefused(PeaArchive.Check encryption, int compression) {
    using var ms = new MemoryStream();
    PeaArchive.Write(ms, Inputs(), compression, streamCheck: encryption, password: "pässwörd");

    var entries = PeaArchive.Read(new MemoryStream(ms.ToArray()), "pässwörd");
    foreach (var input in Inputs())
      Assert.That(entries.Single(e => e.Path == input.Path).Data, Is.EqualTo(input.Data), input.Path);
    Assert.That(ms.ToArray()[18], Is.EqualTo((byte)encryption));
    Assert.That(ms.ToArray().AsSpan().IndexOf("The quick"u8), Is.EqualTo(-1), "plain text must not show");
    Assert.That(() => PeaArchive.Read(new MemoryStream(ms.ToArray()), "passwörd"), Throws.InstanceOf<ArchivePasswordException>());
  }

  [Test, Category("RoundTrip")]
  public void GivenPasswordAndVolumeSize_WhenWritten_ThenTheEncryptedSetRoundTrips() {
    var (first, others) = WriteSet(SmallInputs(), 0, PeaArchive.Check.Crc32, 1000, PeaArchive.Check.Aes256Eax, "secret");
    var entries = PeaArchive.ReadVolumes([first, .. others], "secret");
    Assert.That(entries.Single(e => e.Path == "dir/fox.txt").Data, Is.EqualTo(Fox));
  }

  [Test, Category("EdgeCase")]
  public void GivenTheSamePasswordTwice_WhenWritten_ThenTheSaltDiffers() {
    using var a = new MemoryStream();
    using var b = new MemoryStream();
    PeaArchive.Write(a, SmallInputs(), streamCheck: PeaArchive.Check.Aes256Eax, password: "p");
    PeaArchive.Write(b, SmallInputs(), streamCheck: PeaArchive.Check.Aes256Eax, password: "p");
    Assert.That(a.ToArray().AsSpan(22, 12).SequenceEqual(b.ToArray().AsSpan(22, 12)), Is.False);
  }

  [TestCase(PeaArchive.Check.Aes256Eax, null)]
  [TestCase(PeaArchive.Check.Sha256, "p")]
  [Category("Exception")]
  public void GivenPasswordAndStreamCheckThatDisagree_WhenWritten_ThenRefused(PeaArchive.Check streamCheck, string? password)
    => Assert.That(() => PeaArchive.Write(new MemoryStream(), SmallInputs(), streamCheck: streamCheck, password: password), Throws.ArgumentException);

  [Test, Category("Exception")]
  public void GivenEncryptionAsObjectCheck_WhenWritten_ThenRefused()
    => Assert.That(() => PeaArchive.Write(new MemoryStream(), SmallInputs(), objectCheck: PeaArchive.Check.AesEax), Throws.ArgumentException);

  [TestCase(null, PeaArchive.Check.Aes256Eax)]
  [TestCase("tf256", PeaArchive.Check.Twofish256Eax)]
  [TestCase("Serpent", PeaArchive.Check.Serpent256Eax)]
  [TestCase("HMAC", PeaArchive.Check.AesHmac)]
  [Category("HappyPath")]
  public void GivenPassword_WhenCreatedThroughTheDescriptor_ThenTheChosenEncryptionIsWritten(string? method, PeaArchive.Check expected) {
    using var ms = new MemoryStream();
    new PeaFormatDescriptor().Create(ms, [ArchiveInputInfo.InMemory("a.txt", Hello)], new FormatCreateOptions { Password = "pw", EncryptionMethod = method });
    Assert.That(ms.ToArray()[18], Is.EqualTo((byte)expected));
    Assert.That(new PeaFormatDescriptor().ExtractEntryToMemory(new MemoryStream(ms.ToArray()), "a.txt", "pw"), Is.EqualTo(Hello));
  }

  [Test, Category("Exception")]
  public void GivenUnknownEncryptionMethod_WhenCreated_ThenRefused()
    => Assert.That(() => new PeaFormatDescriptor().Create(new MemoryStream(), [ArchiveInputInfo.InMemory("a", [1])],
      new FormatCreateOptions { Password = "pw", EncryptionMethod = "zipcrypto" }), Throws.InstanceOf<NotSupportedException>());

  [TestCase(PeaArchive.Check.Aes256Eax, "EAX256")]
  [TestCase(PeaArchive.Check.Twofish256Eax, "TF256")]
  [TestCase(PeaArchive.Check.Serpent256Eax, "SP256")]
  [TestCase(PeaArchive.Check.AesEax, "EAX")]
  [TestCase(PeaArchive.Check.TwofishEax, "TF")]
  [TestCase(PeaArchive.Check.SerpentEax, "SP")]
  [TestCase(PeaArchive.Check.AesHmac, "HMAC")]
  [Category("ExternalInterop")]
  public void GivenOurEncryptedArchive_WhenPeaZipExtractsIt_ThenEveryFileMatchesAndAWrongPasswordFails(PeaArchive.Check encryption, string peaName) {
    var pea = RequirePea();
    var path = Path.Combine(this._tmpDir, $"enc_{peaName}.pea");
    using (var f = File.Create(path))
      PeaArchive.Write(f, Inputs(), 2, streamCheck: encryption, password: "s3cret pass");

    var wrongDir = Path.Combine(this._tmpDir, "wrong");
    Directory.CreateDirectory(wrongDir);
    // pea stops at its wrong-password error (and waits there for a click, hence the short timeout)
    var wrong = FsInteropToolbox.RunWsl($"timeout 15 xvfb-run -a '{pea}' UNPEA '{FsInteropToolbox.WinToWsl(path)}' '{FsInteropToolbox.WinToWsl(wrongDir)}/' RESETDATE RESETATTR EXTRACT2DIR HIDDEN 'not it' NOKEYFILE");
    Assert.That(wrong.ExitCode, Is.Not.Zero, "pea must not accept a wrong password");
    Assert.That(Directory.EnumerateFiles(wrongDir, "*", SearchOption.AllDirectories).Where(p => new FileInfo(p).Length > 0), Is.Empty,
      $"a wrong password must not extract anything (pea exit {wrong.ExitCode})");

    var outDir = Path.Combine(this._tmpDir, "out");
    Directory.CreateDirectory(outDir);
    var r = FsInteropToolbox.RunWsl($"timeout 120 xvfb-run -a '{pea}' UNPEA '{FsInteropToolbox.WinToWsl(path)}' '{FsInteropToolbox.WinToWsl(outDir)}/' RESETDATE RESETATTR EXTRACT2DIR HIDDEN 's3cret pass' NOKEYFILE");
    Assert.That(r.ExitCode, Is.EqualTo(0), $"pea UNPEA failed:\n{r.StdOut}\n{r.StdErr}");
    var root = Directory.GetDirectories(outDir).Single();
    foreach (var input in Inputs())
      Assert.That(File.ReadAllBytes(Path.Combine(root, input.Path.Replace('/', Path.DirectorySeparatorChar))), Is.EqualTo(input.Data), input.Path);
  }

  private static byte[] BitConverterLe(uint v) => [(byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24)];

  [Test, Category("Exception")]
  public void GivenNotPea_WhenRead_ThenRefused()
    => Assert.That(() => PeaArchive.Read(new MemoryStream(new byte[64])), Throws.InstanceOf<InvalidDataException>());

  // ── our writer ──────────────────────────────────────────────────────

  private static List<PeaArchive.Input> Inputs() => [
    new("hello.txt", Hello, SourceTime),
    new("dir/fox.txt", Fox, SourceTime),
    new("dir/deep/empty.bin", [], SourceTime),
    new("big.bin", Enumerable.Range(0, 2_500_000).Select(i => (byte)(i * 7 + (i >> 9))).ToArray(), SourceTime),
  ];

  [TestCase(0, PeaArchive.Check.Crc32, PeaArchive.Check.None, PeaArchive.Check.Crc32)]
  [TestCase(1, PeaArchive.Check.Crc64, PeaArchive.Check.Sha256, PeaArchive.Check.Adler32)]
  [TestCase(2, PeaArchive.Check.Crc32, PeaArchive.Check.Sha256, PeaArchive.Check.Crc32)]
  [TestCase(3, PeaArchive.Check.Md5, PeaArchive.Check.Sha512, PeaArchive.Check.Sha1)]
  [Category("RoundTrip")]
  public void GivenFiles_WhenWrittenAndRead_ThenEveryByteRoundTrips(int compression, PeaArchive.Check obj, PeaArchive.Check stream, PeaArchive.Check volume) {
    using var ms = new MemoryStream();
    PeaArchive.Write(ms, Inputs(), compression, obj, stream, volume);
    var entries = PeaArchive.Read(new MemoryStream(ms.ToArray()));
    foreach (var input in Inputs())
      Assert.That(entries.Single(e => e.Path == input.Path).Data, Is.EqualTo(input.Data), input.Path);
    Assert.That(entries.Where(e => e.IsDirectory).Select(e => e.Path), Is.EquivalentTo(new[] { "dir", "dir/deep" }));
  }

  [Test, Category("ExternalInterop")]
  public void GivenOurArchive_WhenPeaZipExtractsIt_ThenEveryFileMatches() {
    var pea = RequirePea();
    var path = Path.Combine(this._tmpDir, "ours.pea");
    using (var f = File.Create(path))
      PeaArchive.Write(f, Inputs());
    var outDir = Path.Combine(this._tmpDir, "out");
    Directory.CreateDirectory(outDir);

    var r = FsInteropToolbox.RunWsl($"timeout 120 xvfb-run -a '{pea}' UNPEA '{FsInteropToolbox.WinToWsl(path)}' '{FsInteropToolbox.WinToWsl(outDir)}/' RESETDATE RESETATTR EXTRACT2DIR HIDDEN '' ''");
    Assert.That(r.ExitCode, Is.EqualTo(0), $"pea UNPEA failed:\n{r.StdOut}\n{r.StdErr}");
    var root = Path.Combine(outDir, "ours.pea");
    foreach (var input in Inputs())
      Assert.That(File.ReadAllBytes(Path.Combine(root, input.Path.Replace('/', Path.DirectorySeparatorChar))), Is.EqualTo(input.Data), input.Path);
  }

  private static string RequirePea() {
    if (OperatingSystem.IsWindows() && !FsInteropToolbox.WslAvailable) Assert.Ignore("WSL is not available.");
    var pea = Environment.GetEnvironmentVariable("PEAZIP_PEA") ?? "/opt/cwb_peazip/peazip_portable-11.4.0.LINUX.GTK2.x86_64/pea";
    var probe = FsInteropToolbox.RunWsl($"test -x '{pea}' && command -v xvfb-run");
    if (probe.ExitCode != 0) Assert.Ignore($"PeaZip's pea is not at {pea} (set PEAZIP_PEA), or xvfb-run is missing.");
    return pea;
  }

  private static byte[] LoadVector(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"PeaVectors.{name}")
      ?? throw new InvalidOperationException($"missing embedded vector {name}");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }
}
