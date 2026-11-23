using System.Buffers.Binary;
using System.Text;
using Compression.Core.Checksums;
using FileFormat.ExePackers;
using FileFormat.Zlib;

namespace Compression.Tests.ExePackers;

/// <summary>
/// The MoleBox 2.x file box (docs/MOLEBOX-ON-DISK.md). Structure tests build boxes the way the
/// measured layout prescribes onto the synthetic packed PE of
/// <see cref="MoleboxExecutablePackerHandlerTests"/>; the oracle test reads real MoleBox-packed
/// programs and compares every file with an independent extractor's output
/// (<c>CWB_MOLEBOX_SAMPLES</c>, see <see cref="Given_RealMoleboxPrograms_When_Extracted_Then_EveryFileEqualsTheReferenceExtraction"/>).
/// </summary>
[TestFixture]
public class MoleboxBoxTests {
  private const string BoxName = "Game.exe.BOX";

  private sealed record Item(string Name, byte[] Data, bool Compress = true, uint Flags = 0x33);

  // ── box writer (test only): the layout the reader is checked against ──

  private static byte[] Encrypt(byte[] data) =>
    MoleboxIdea.ProcessEcb(data, MoleboxIdea.ExpandKey(MoleboxExecutablePackerHandlerTests.SectionKey));

  private static (uint Offset, uint Flags) WriteItem(MemoryStream ms, Item item) {
    var blocks = new List<(byte[] Bytes, bool Stored)>();
    for (var at = 0; at < item.Data.Length; at += 0x10000) {
      var chunk = item.Data.AsSpan(at, Math.Min(0x10000, item.Data.Length - at)).ToArray();
      var stored = !item.Compress;
      var body = stored ? chunk : ZlibStream.Compress(chunk);
      blocks.Add(((item.Flags & 2) != 0 ? Encrypt(body) : body, stored));
    }
    foreach (var (bytes, stored) in blocks)
      ms.Write(BitConverter.GetBytes((uint)bytes.Length | (stored ? 0x80000000u : 0)));
    var offset = (uint)ms.Position;
    foreach (var (bytes, _) in blocks)
      ms.Write(bytes);
    // Packers leave a few bytes between files.
    ms.Write([0x5A, 0xA5, 0x3C]);
    return (offset, item.Flags);
  }

  private static byte[] BuildBoxed(IReadOnlyList<Item> items, Item? main = null, uint magic = 0xCAFEBABE, Func<byte[], byte[]>? tamperDirectory = null, bool withSlot = true, bool undecodableConfiguration = false) {
    var pe = MoleboxExecutablePackerHandlerTests.BuildMoleboxPe(
      MoleboxExecutablePackerHandlerTests.BuildSectionBody(0x800, 0x11), MoleboxExecutablePackerHandlerTests.BuildSectionBody(0x400, 0x77), BoxName,
      undecodableConfiguration);
    using var ms = new MemoryStream();
    ms.Write(pe);
    var (mainOffset, mainFlags) = main != null ? WriteItem(ms, main) : (0u, 0u);
    var records = items.Select(i => (i.Name, Placed: WriteItem(ms, i), i.Data.Length)).ToList();

    var names = new MemoryStream();
    var nameOffsets = new List<uint>();
    foreach (var item in items) {
      nameOffsets.Add((uint)names.Position);
      names.Write(Encoding.Latin1.GetBytes(item.Name.Replace('/', '\\')));
      names.WriteByte(0);
    }
    var directory = new MemoryStream();
    directory.Write(names.ToArray());
    for (var i = 0; i < records.Count; ++i) {
      directory.Write(BitConverter.GetBytes(nameOffsets[i]));
      directory.Write(BitConverter.GetBytes(records[i].Placed.Offset));
      directory.Write(BitConverter.GetBytes((uint)records[i].Length));
      directory.Write(BitConverter.GetBytes(records[i].Placed.Flags));
    }
    while (directory.Length % 8 != 0)
      directory.WriteByte(0);
    var plain = directory.ToArray();
    var md5 = Md5.Compute(plain);
    if (tamperDirectory != null)
      plain = tamperDirectory(plain);
    var directoryOffset = (uint)ms.Position;
    ms.Write(Encrypt(plain));

    if (withSlot) {
      ms.Write(BitConverter.GetBytes(mainOffset));
      ms.Write(BitConverter.GetBytes((uint)(main?.Data.Length ?? 0)));
      ms.Write(BitConverter.GetBytes(mainFlags));
    }
    ms.Write(BitConverter.GetBytes((uint)plain.Length));
    ms.Write(BitConverter.GetBytes((uint)plain.Length));
    ms.Write(BitConverter.GetBytes(directoryOffset));
    ms.Write(BitConverter.GetBytes((uint)names.Length));
    ms.Write(BitConverter.GetBytes((uint)items.Count));
    ms.Write(BitConverter.GetBytes((uint)(ms.Length + 24)));
    ms.Write(md5);
    ms.Write(BitConverter.GetBytes(magic));
    return ms.ToArray();
  }

  private static byte[] Pattern(int length, int seed) {
    var data = new byte[length];
    var x = (uint)seed * 2654435761u;
    for (var i = 0; i < length; ++i) {
      x = x * 1103515245 + 12345;
      data[i] = i % 7 == 0 ? (byte)(x >> 24) : (byte)(i * seed);
    }
    return data;
  }

  private static readonly Item[] Files = [
    new("readme.txt", Encoding.ASCII.GetBytes("MoleBox box test\r\n")),
    new("data/level1.dat", Pattern(150_000, 3)),
    new("data/music.ogg", Pattern(70_000, 9), Compress: false),
    new("empty.bin", []),
  ];

  private static Dictionary<string, byte[]> ExtractAll(byte[] file) {
    var dir = Path.Combine(Path.GetTempPath(), "cwb-molebox-" + Guid.NewGuid().ToString("N"));
    try {
      using (var ms = new MemoryStream(file))
        new MoleboxFormatDescriptor().Extract(ms, dir, null, null);
      return Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
        .ToDictionary(p => Path.GetRelativePath(dir, p).Replace('\\', '/'), File.ReadAllBytes);
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
  }

  [Test]
  [Category("HappyPath")]
  public void Given_ABoxWithCompressedStoredAndMultiBlockFiles_When_Extracted_Then_EveryFileComesBackByteForByte() {
    var boxed = BuildBoxed(Files);
    var extracted = ExtractAll(boxed);
    Assert.That(extracted.Keys, Is.EquivalentTo(Files.Select(f => f.Name)));
    foreach (var f in Files)
      Assert.That(extracted[f.Name], Is.EqualTo(f.Data), f.Name);
  }

  [Test]
  [Category("HappyPath")]
  public void Given_ABox_When_Listed_Then_NamesSizesAndMethodsAreReported() {
    using var ms = new MemoryStream(BuildBoxed(Files));
    var list = new MoleboxFormatDescriptor().List(ms, null);
    Assert.That(list.Select(e => (e.Name, e.OriginalSize)), Is.EqualTo(Files.Select(f => (f.Name, (long)f.Data.Length))));
    Assert.That(list.Single(e => e.Name == "data/music.ogg").CompressedSize, Is.EqualTo(70_000), "stored blocks keep their length");
    Assert.That(list.All(e => e.IsEncrypted), Is.True);
  }

  [Test]
  [Category("HappyPath")]
  public void Given_ABoxKeepingTheProgram_When_Listed_Then_TheProgramComesFirstUnderTheBoxName() {
    var program = Pattern(90_000, 5);
    var boxed = BuildBoxed(Files, main: new Item("ignored", program));
    var extracted = ExtractAll(boxed);
    using var ms = new MemoryStream(boxed);
    Assert.That(new MoleboxFormatDescriptor().List(ms, null)[0].Name, Is.EqualTo("Game.exe"));
    Assert.That(extracted["Game.exe"], Is.EqualTo(program));
  }

  [Test]
  [Category("EdgeCase")]
  public void Given_AnotherTrailerMagic_When_Read_Then_TheBoxIsStillFound() {
    // The last dword differs between MoleBox builds; the length field and the MD5 identify the box.
    var extracted = ExtractAll(BuildBoxed(Files, magic: 0x92A8EA49));
    Assert.That(extracted["data/level1.dat"], Is.EqualTo(Files[1].Data));
  }

  [Test]
  [Category("EdgeCase")]
  public void Given_DataAppendedAfterTheBox_When_Read_Then_TheBoxIsFoundWhereItsTrailerEnds() {
    var boxed = BuildBoxed(Files);
    var appended = boxed.Concat(Pattern(300_000, 13)).Concat(Encoding.ASCII.GetBytes("A249C920E12E2CFC93C8E4B5")).ToArray();
    var extracted = ExtractAll(appended);
    Assert.That(extracted["data/level1.dat"], Is.EqualTo(Files[1].Data));
    Assert.That(extracted.Keys, Is.EquivalentTo(Files.Select(f => f.Name)));
    var path = Path.Combine(Path.GetTempPath(), "cwb-molebox-" + Guid.NewGuid().ToString("N") + ".exe");
    try {
      File.WriteAllBytes(path, appended);
      Assert.That(Compression.Lib.FormatDetector.Detect(path).ToString(), Is.EqualTo("Molebox"));
    } finally {
      File.Delete(path);
    }
  }

  [Test]
  [Category("EdgeCase")]
  public void Given_ABuildWhoseConfigurationRecordDoesNotDecode_When_Read_Then_TheKeyBlobAtItsFixedPlaceOpensTheBox() {
    var boxed = BuildBoxed(Files, undecodableConfiguration: true);
    var extracted = ExtractAll(boxed);
    Assert.That(extracted["data/level1.dat"], Is.EqualTo(Files[1].Data));
    Assert.That(MoleboxBox.TryOpen(boxed)!.BoxName, Is.EqualTo(BoxName));
  }

  [Test]
  [Category("EdgeCase")]
  public void Given_ABuildWithoutTheProgramCopySlot_When_Read_Then_TheDirectoryFieldsFollowItDirectly() {
    var extracted = ExtractAll(BuildBoxed(Files, withSlot: false));
    Assert.That(extracted.Keys, Is.EquivalentTo(Files.Select(f => f.Name)));
    Assert.That(extracted["data/music.ogg"], Is.EqualTo(Files[2].Data));
  }

  [Test]
  [Category("EdgeCase")]
  public void Given_ABoxWithoutAProgramCopy_When_TheUnusedHeaderSlotHoldsNoise_Then_ItIsIgnored() {
    var boxed = BuildBoxed(Files);
    var slot = boxed.Length - 24 - 32;
    BinaryPrimitives.WriteUInt32LittleEndian(boxed.AsSpan(slot), 0x0C28E39A);
    BinaryPrimitives.WriteUInt32LittleEndian(boxed.AsSpan(slot + 4), 0x11B7E14F);
    BinaryPrimitives.WriteUInt32LittleEndian(boxed.AsSpan(slot + 8), 0x74665576);
    using var ms = new MemoryStream(boxed);
    Assert.That(new MoleboxFormatDescriptor().List(ms, null).Select(e => e.Name), Is.EqualTo(Files.Select(f => f.Name)));
  }

  [Test]
  [Category("Exception")]
  public void Given_ATamperedDirectory_When_Listed_Then_OnlyAPartialMetadataEntryIsReturned() {
    var boxed = BuildBoxed(Files, tamperDirectory: d => { d[^9] ^= 1; return d; });
    using var ms = new MemoryStream(boxed);
    var list = new MoleboxFormatDescriptor().List(ms, null);
    Assert.That(list.Select(e => e.Name), Is.EqualTo(new[] { "metadata.ini" }));
    using var again = new MemoryStream(boxed);
    Assert.That(() => new MoleboxFormatDescriptor().Extract(again, Path.GetTempPath(), null, null), Throws.InstanceOf<InvalidDataException>());
  }

  [Test]
  [Category("Exception")]
  public void Given_ADamagedBlock_When_Extracted_Then_ItIsRefused() {
    var boxed = BuildBoxed(Files);
    var box = MoleboxBox.TryOpen(boxed)!;
    var entry = box.Entries.Single(e => e.Name == "data/level1.dat");
    boxed[entry.DataOffset + 100] ^= 0x40;
    Assert.That(() => MoleboxBox.TryOpen(boxed)!.Extract(entry), Throws.InstanceOf<InvalidDataException>());
  }

  [Test]
  [Category("Exception")]
  public void Given_APathLeavingTheBox_When_Listed_Then_TheBoxIsRefused() {
    var boxed = BuildBoxed([new Item("../evil.txt", [1, 2, 3])]);
    using var ms = new MemoryStream(boxed);
    Assert.That(new MoleboxFormatDescriptor().List(ms, null).Select(e => e.Name), Is.EqualTo(new[] { "metadata.ini" }));
  }

  [Test]
  [Category("Exception")]
  public void Given_APackedProgramWithoutABox_When_Checked_Then_NoBoxIsFound() {
    var pe = MoleboxExecutablePackerHandlerTests.BuildMoleboxPe(
      MoleboxExecutablePackerHandlerTests.BuildSectionBody(0x800, 0x11), MoleboxExecutablePackerHandlerTests.BuildSectionBody(0x400, 0x77));
    using var ms = new MemoryStream(pe);
    Assert.That(MoleboxBox.HasBoxTrailer(ms), Is.False);
    Assert.That(MoleboxBox.TryOpen(pe), Is.Null);
  }

  [Test]
  [Category("Exception")]
  public void Given_ATruncatedBox_When_Checked_Then_TheTrailerNoLongerMatches() {
    var boxed = BuildBoxed(Files);
    var cut = boxed[..^10];
    using var ms = new MemoryStream(cut);
    Assert.That(MoleboxBox.HasBoxTrailer(ms), Is.False);
    Assert.That(MoleboxBox.TryOpen(cut), Is.Null);
  }

  [Test]
  [Category("HappyPath")]
  public void Given_ABoxedProgramOnDisk_When_Detected_Then_ItIsMolebox() {
    var path = Path.Combine(Path.GetTempPath(), "cwb-molebox-" + Guid.NewGuid().ToString("N") + ".exe");
    try {
      File.WriteAllBytes(path, BuildBoxed(Files));
      Assert.That(Compression.Lib.FormatDetector.Detect(path).ToString(), Is.EqualTo("Molebox"));
    } finally {
      File.Delete(path);
    }
  }

  /// <summary>
  /// Real MoleBox-packed programs (not committed): <c>CWB_MOLEBOX_SAMPLES</c> names a directory
  /// with one folder per program holding <c>packed.bin</c> and, under <c>ref/</c>, the files an
  /// independent MoleBox extractor produced from it (path separators flattened to <c>__</c>).
  /// Every box file must equal its reference byte for byte.
  /// </summary>
  [Test]
  [Category("ExternalInterop")]
  public void Given_RealMoleboxPrograms_When_Extracted_Then_EveryFileEqualsTheReferenceExtraction() {
    var root = Environment.GetEnvironmentVariable("CWB_MOLEBOX_SAMPLES");
    if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
      Assert.Ignore("Set CWB_MOLEBOX_SAMPLES to a directory of real MoleBox samples.");
    var compared = 0;
    foreach (var sample in Directory.GetDirectories(root).Order(StringComparer.Ordinal)) {
      var packedPath = Path.Combine(sample, "packed.bin");
      var refDir = Path.Combine(sample, "ref");
      if (!File.Exists(packedPath) || !Directory.Exists(refDir))
        continue;
      var box = MoleboxBox.TryOpen(File.ReadAllBytes(packedPath));
      Assert.That(box, Is.Not.Null, $"{Path.GetFileName(sample)}: box not read");
      var byName = box!.Entries.Where(e => !e.IsMainExecutable).ToDictionary(e => e.Name.ToLowerInvariant());
      foreach (var reference in Directory.GetFiles(refDir)) {
        var name = Path.GetFileName(reference).Replace("__", "/").ToLowerInvariant();
        if (!byName.TryGetValue(name, out var entry))
          continue;
        Assert.That(box.Extract(entry), Is.EqualTo(File.ReadAllBytes(reference)), $"{Path.GetFileName(sample)}: {name}");
        ++compared;
      }
    }
    Assert.That(compared, Is.GreaterThan(0), "no reference files matched a box entry");
  }

  /// <summary>
  /// Real programs without a reference extraction (<c>CWB_MOLEBOX_SAMPLES</c>, folders listed in
  /// <c>expect-box.txt</c> there): the box must open and every file pass the checks the format
  /// stores (directory MD5, per-block Adler-32 and lengths).
  /// </summary>
  [Test]
  [Category("ExternalInterop")]
  public void Given_RealMoleboxProgramsWithoutReference_When_Extracted_Then_EveryStoredCheckPasses() {
    var root = Environment.GetEnvironmentVariable("CWB_MOLEBOX_SAMPLES");
    var list = string.IsNullOrEmpty(root) ? null : Path.Combine(root, "expect-box.txt");
    if (list == null || !File.Exists(list))
      Assert.Ignore("Set CWB_MOLEBOX_SAMPLES to a directory of real MoleBox samples with expect-box.txt.");
    var files = 0;
    foreach (var name in File.ReadAllLines(list).Where(l => l.Trim().Length > 0)) {
      var box = MoleboxBox.TryOpen(File.ReadAllBytes(Path.Combine(root!, name.Trim(), "packed.bin")));
      Assert.That(box, Is.Not.Null, $"{name}: box not read");
      foreach (var entry in box!.Entries) {
        Assert.That(box.Extract(entry), Has.Length.EqualTo(entry.Size), $"{name}: {entry.Name}");
        ++files;
      }
    }
    Assert.That(files, Is.GreaterThan(0));
  }
}
