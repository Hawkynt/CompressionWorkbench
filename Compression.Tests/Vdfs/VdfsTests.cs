using System.Buffers.Binary;
using Compression.Registry;
using FileSystem.Vdfs;

namespace Compression.Tests.Vdfs;

[TestFixture]
public class VdfsTests {

  private static byte[] BuildImage(params (string Name, byte[] Data)[] files) => BuildImage(false, files);

  private static byte[] BuildImage(bool zipped, params (string Name, byte[] Data)[] files) {
    var w = new VdfsWriter { Zipped = zipped, Timestamp = new DateTime(2024, 5, 6, 7, 8, 10) };
    foreach (var (name, data) in files)
      w.AddFile(name, data);
    return w.Build();
  }

  private static VdfsReader Read(byte[] image) => new(new MemoryStream(image));

  private static byte[] Pattern(int length) {
    var data = new byte[length];
    for (var i = 0; i < length; ++i) data[i] = (byte)(i * 31 + i / 97);
    return data;
  }

  // -------------------------------------------------------------------------
  // Descriptor
  // -------------------------------------------------------------------------

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var desc = new VdfsFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("Vdfs"));
    Assert.That(desc.DefaultExtension, Is.EqualTo(".vdf"));
    Assert.That(desc.MagicSignatures[0].Bytes, Is.EqualTo("PSVDSC_V2.00"u8.ToArray()));
    Assert.That(desc.MagicSignatures[0].Offset, Is.EqualTo(256), "the signature follows the 256-byte comment");
    Assert.That(desc.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(desc.Methods.Select(m => m.Name), Is.EquivalentTo(new[] { "stored", "zipped" }));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Capabilities() {
    var desc = new VdfsFormatDescriptor();
    Assert.That(desc.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    Assert.That(desc.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
    Assert.That(desc.Capabilities.HasFlag(FormatCapabilities.SupportsDirectories), Is.True);
    Assert.That(desc, Is.InstanceOf<IArchiveDefragmentable>());
    Assert.That(desc, Is.InstanceOf<IFilesystemExtentMap>());
  }

  // -------------------------------------------------------------------------
  // Layout
  // -------------------------------------------------------------------------

  [Test, Category("Spec")]
  public void Writer_Header_FollowsTheReferenceLayout() {
    // Given a stored volume of two files
    var img = BuildImage(("a.txt", "AAA"u8.ToArray()), ("b.txt", "BB"u8.ToArray()));

    // Then the comment is 0x1A-padded, the Gothic 2 signature sits at 256, and the six
    // header fields give 2 entries, 2 files, a DOS time, 5 data bytes, catalog at 296, 0x50
    Assert.That(img[0], Is.EqualTo(0x1A));
    Assert.That(img.AsSpan(256, 16).ToArray(), Is.EqualTo("PSVDSC_V2.00\n\r\n\r"u8.ToArray()));
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan(272)), Is.EqualTo(2u));
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan(276)), Is.EqualTo(2u));
    Assert.That(VdfsLayout.FromDosTime(BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan(280))),
      Is.EqualTo(new DateTime(2024, 5, 6, 7, 8, 10)));
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan(284)), Is.EqualTo(5u));
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan(288)), Is.EqualTo(296u));
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan(292)), Is.EqualTo(0x50u));
    Assert.That(img.Length, Is.EqualTo(296 + 2 * 80 + 5));
  }

  [Test, Category("Spec")]
  public void Writer_Catalog_DirectoriesFirst_LastFlagged_ChildIndexInOffset() {
    // Given files in the root and in a subdirectory
    var img = BuildImage(("z.txt", "Z"u8.ToArray()), ("dir/inner.txt", "I"u8.ToArray()), ("a.txt", "A"u8.ToArray()));

    // Then the root block lists DIR first, then A.TXT and Z.TXT, the last one flagged,
    // and DIR's offset names index 3, where its own one-entry block starts
    string Name(int i) => System.Text.Encoding.ASCII.GetString(img, 296 + i * 80, 64).TrimEnd(' ');
    uint Field(int i, int at) => BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan(296 + i * 80 + at));
    Assert.That(Enumerable.Range(0, 4).Select(Name), Is.EqualTo(new[] { "DIR", "A.TXT", "Z.TXT", "INNER.TXT" }));
    Assert.That(Field(0, 72), Is.EqualTo(0x80000000u));
    Assert.That(Field(0, 64), Is.EqualTo(3u));
    Assert.That(Field(1, 72), Is.EqualTo(0u));
    Assert.That(Field(2, 72), Is.EqualTo(0x40000000u));
    Assert.That(Field(3, 72), Is.EqualTo(0x40000000u));
    Assert.That(Field(1, 76), Is.EqualTo(0x20u), "files carry FILE_ATTRIBUTE_ARCHIVE like the reference tools");
  }

  [Test, Category("EdgeCase")]
  public void Writer_EmptyDirectory_IsLeftOut() {
    // An empty directory's child index would point into another block, so none is written
    var w = new VdfsWriter();
    w.AddFile("a.txt", [1]);
    var img = w.Build();
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan(272)), Is.EqualTo(1u));
  }

  [Test, Category("Boundary")]
  public void Writer_NameOf64Bytes_Fits_65Throws() {
    var w = new VdfsWriter();
    w.AddFile(new string('N', 64), [1]);
    Assert.That(Read(w.Build()).Entries.Single().Name, Is.EqualTo(new string('N', 64)));

    var tooLong = new VdfsWriter();
    tooLong.AddFile(new string('N', 65), [1]);
    Assert.Throws<ArgumentException>(() => tooLong.Build());
  }

  [Test, Category("EdgeCase")]
  public void Writer_SamePathTwice_LaterWins() {
    var w = new VdfsWriter();
    w.AddFile("x.txt", "old"u8.ToArray());
    w.AddFile("X.TXT", "new"u8.ToArray());
    using var r = Read(w.Build());
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo("new"u8.ToArray()));
  }

  // -------------------------------------------------------------------------
  // Round trips
  // -------------------------------------------------------------------------

  [Test, Category("RoundTrip")]
  public void RoundTrip_SingleFile_NameUpperCased() {
    var data = "Hello VDFS!"u8.ToArray();
    using var r = Read(BuildImage(("hello.txt", data)));
    var files = r.Entries.Where(e => !e.IsDirectory).ToList();
    Assert.That(files, Has.Count.EqualTo(1));
    Assert.That(files[0].Name, Is.EqualTo("HELLO.TXT"));
    Assert.That(r.Extract(files[0]), Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_KeepsCase_WhenAsked() {
    var w = new VdfsWriter { UpperCaseNames = false };
    w.AddFile("MixedCase.txt", [7]);
    using var r = Read(w.Build());
    Assert.That(r.Entries.Single().Name, Is.EqualTo("MixedCase.txt"));
  }

  [TestCase(false)]
  [TestCase(true)]
  [Category("RoundTrip")]
  public void RoundTrip_NestedDirectories(bool zipped) {
    // Given files at three depths
    var a = Pattern(300);
    var b = Pattern(20000);
    var c = "root"u8.ToArray();
    using var r = Read(BuildImage(zipped, ("_work/data/a.bin", a), ("_work/data/scripts/b.d", b), ("c.txt", c)));

    // Then the directories list and every file decodes under its full path
    Assert.That(r.Entries.Where(e => e.IsDirectory).Select(e => e.Name),
      Is.EquivalentTo(new[] { "_WORK", "_WORK/DATA", "_WORK/DATA/SCRIPTS" }));
    var files = r.Entries.Where(e => !e.IsDirectory).ToDictionary(e => e.Name);
    Assert.That(r.Extract(files["_WORK/DATA/A.BIN"]), Is.EqualTo(a));
    Assert.That(r.Extract(files["_WORK/DATA/SCRIPTS/B.D"]), Is.EqualTo(b));
    Assert.That(r.Extract(files["C.TXT"]), Is.EqualTo(c));
    Assert.That(files.Values.All(f => f.IsZipped == zipped), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Zipped_Volume_IsFlagged_AndSmaller() {
    var data = new byte[50000];
    Array.Fill(data, (byte)0xAB);
    var stored = BuildImage(false, ("world.mrm", data));
    var zipped = BuildImage(true, ("world.mrm", data));

    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(zipped.AsSpan(292)), Is.EqualTo(0xA0u));
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(zipped.AsSpan(284)), Is.EqualTo(50000u),
      "the header's data size is the uncompressed total, as VDFSTool writes it");
    Assert.That(zipped.Length, Is.LessThan(stored.Length / 10));
    using var r = Read(zipped);
    Assert.That(r.IsZippedVolume, Is.True);
    Assert.That(r.Entries[0].StoredSize, Is.LessThan(r.Entries[0].Size));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }

  [Test, Category("EdgeCase")]
  public void Zipped_Volume_KeepsSoundRaw_AndEmptyFilesEmpty() {
    // Given a zipped volume with sound, an empty file, and ordinary data
    var wav = Pattern(4096);
    var ogg = Pattern(2048);
    var other = Pattern(1000);
    using var r = Read(BuildImage(true, ("sound/theme.wav", wav), ("sound/voice.ogg", ogg), ("empty.txt", []), ("other.bin", other)));
    var files = r.Entries.Where(e => !e.IsDirectory).ToDictionary(e => e.Name);

    // Then sound is stored raw as Union stores it, the empty file is empty, the rest zipped
    Assert.That(files["SOUND/THEME.WAV"].IsZipped, Is.False);
    Assert.That(files["SOUND/THEME.WAV"].StoredSize, Is.EqualTo(4096));
    Assert.That(files["SOUND/VOICE.OGG"].IsZipped, Is.False);
    Assert.That(files["EMPTY.TXT"].Size, Is.EqualTo(0));
    Assert.That(files["OTHER.BIN"].IsZipped, Is.True);
    Assert.That(r.Extract(files["SOUND/THEME.WAV"]), Is.EqualTo(wav));
    Assert.That(r.Extract(files["SOUND/VOICE.OGG"]), Is.EqualTo(ogg));
    Assert.That(r.Extract(files["EMPTY.TXT"]), Is.Empty);
    Assert.That(r.Extract(files["OTHER.BIN"]), Is.EqualTo(other));
  }

  [TestCase(null, "Stored")]
  [TestCase("zipped", "Zipped")]
  [Category("RoundTrip")]
  public void Create_ViaDescriptor_KeepsDirectories_AndHonoursMethod(string? method, string expectedMethod) {
    var desc = new VdfsFormatDescriptor();
    using var ms = new MemoryStream();
    desc.Create(ms, [
      ArchiveInputInfo.InMemory("textures/stone.tex", Pattern(16384)),
      new ArchiveInputInfo("", "textures", true),
    ], new FormatCreateOptions(method));

    ms.Position = 0;
    var entries = desc.List(ms, null);
    var file = entries.Single(e => !e.IsDirectory);
    Assert.That(file.Name, Is.EqualTo("TEXTURES/STONE.TEX"));
    Assert.That(file.Method, Is.EqualTo(expectedMethod));
    Assert.That(entries.Any(e => e.IsDirectory && e.Name == "TEXTURES"), Is.True);
    ms.Position = 0;
    Assert.That(desc.ExtractEntryToMemory(ms, "textures/stone.tex", null), Is.EqualTo(Pattern(16384)));
  }

  // -------------------------------------------------------------------------
  // Add / Remove through the descriptor
  // -------------------------------------------------------------------------

  [Test, Category("RoundTrip")]
  public void AddRemove_RoundTrips() {
    using var ms = new MemoryStream();
    ms.Write(BuildImage(("keep.txt", "keep"u8.ToArray())));
    var desc = new VdfsFormatDescriptor();

    desc.Add(ms, [ArchiveInputInfo.InMemory("new.txt", "added"u8.ToArray())]);
    ms.Position = 0;
    var names = desc.List(ms, null).Select(e => e.Name).ToList();
    Assert.That(names, Is.EquivalentTo(new[] { "KEEP.TXT", "NEW.TXT" }));

    desc.Remove(ms, ["keep.txt"]);
    ms.Position = 0;
    using var r = new VdfsReader(ms, leaveOpen: true);
    Assert.That(r.Entries.Select(e => e.Name), Is.EquivalentTo(new[] { "NEW.TXT" }));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo("added"u8.ToArray()));
  }

  [Test, Category("RoundTrip")]
  public void Add_ReplacesExistingFile() {
    using var ms = new MemoryStream();
    ms.Write(BuildImage(("file.txt", "original"u8.ToArray())));
    new VdfsFormatDescriptor().Add(ms, [ArchiveInputInfo.InMemory("FILE.txt", "replaced"u8.ToArray())]);

    ms.Position = 0;
    using var r = new VdfsReader(ms, leaveOpen: true);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo("replaced"u8.ToArray()));
  }

  // -------------------------------------------------------------------------
  // Defragment
  // -------------------------------------------------------------------------

  [TestCase(false)]
  [TestCase(true)]
  [Category("RoundTrip")]
  public void Defragment_AfterRemove_PreservesFiles_AndCloses_TheGap(bool zipped) {
    // Given a volume with a hole where a removed file was
    using var ms = new MemoryStream();
    ms.Write(BuildImage(zipped, ("a.txt", Pattern(3000)), ("b.txt", Pattern(5000)), ("c.txt", Pattern(700))));
    VdfsInPlaceModifier.RemoveFile(ms, "a.txt");

    // When it is defragmented
    new VdfsFormatDescriptor().Defragment(ms, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

    // Then both survivors decode and the first payload sits right behind the catalog
    ms.Position = 0;
    using var r = new VdfsReader(ms, leaveOpen: true);
    var files = r.Entries.ToDictionary(e => e.Name);
    Assert.That(r.Extract(files["B.TXT"]), Is.EqualTo(Pattern(5000)));
    Assert.That(r.Extract(files["C.TXT"]), Is.EqualTo(Pattern(700)));
    ms.Position = 0;
    var used = new VdfsFormatDescriptor().EnumerateExtents(ms).Where(e => e.Kind == DefragBlockKind.Used).ToList();
    Assert.That(used.Min(e => e.Offset), Is.EqualTo(296 + 2 * 80));
  }

  [Test, Category("RoundTrip")]
  public void Defragment_NoArg_PreservesFiles() {
    using var ms = new MemoryStream();
    ms.Write(BuildImage(("x.dat", new byte[50])));
    new VdfsFormatDescriptor().Defragment(ms);
    ms.Position = 0;
    using var r = new VdfsReader(ms, leaveOpen: true);
    Assert.That(r.Entries.Single().Name, Is.EqualTo("X.DAT"));
  }

  // -------------------------------------------------------------------------
  // Extent map
  // -------------------------------------------------------------------------

  [Test, Category("HappyPath")]
  public void ExtentMap_ReservesHeaderAndCatalog_UsesStoredLength() {
    var img = BuildImage(true, ("a.txt", Pattern(9000)), ("b.txt", "world"u8.ToArray()));
    using var ms = new MemoryStream(img);
    var extents = new VdfsFormatDescriptor().EnumerateExtents(ms).ToList();

    var meta = extents.Where(e => e.Kind == DefragBlockKind.MetadataReserved).ToList();
    Assert.That(meta.Sum(e => e.Length), Is.EqualTo(296 + 2 * 80));
    Assert.That(meta.Any(e => e.Offset == 0 && e.Length == 296), Is.True, "the header is not reserved");

    // A zipped payload occupies its whole stream, not its uncompressed size
    var used = extents.Where(e => e.Kind == DefragBlockKind.Used).ToList();
    Assert.That(used, Has.Count.EqualTo(2));
    Assert.That(used.Sum(e => e.Length), Is.EqualTo(img.Length - 296 - 160));
  }

  // -------------------------------------------------------------------------
  // Damaged input
  // -------------------------------------------------------------------------

  [Test, Category("ErrorHandling")]
  public void Reader_TooSmall_Throws() {
    Assert.Throws<InvalidDataException>(() => _ = Read(new byte[10]));
    Assert.Throws<InvalidDataException>(() => _ = Read(new byte[295]));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_NoSignatureAt256_Throws() {
    // Including this repository's old layout, which put the signature at offset 0
    var old = new byte[400];
    "PSVDSC_V2.00\n\r\n\r"u8.CopyTo(old);
    Assert.Throws<InvalidDataException>(() => _ = Read(old));
  }

  [Test, Category("ErrorHandling")]
  public void List_NoSignature_FallsBackToFullImage_NeverThrows() {
    using var ms = new MemoryStream(new byte[500]);
    var entries = new VdfsFormatDescriptor().List(ms, null);
    Assert.That(entries.Select(e => e.Name), Is.EqualTo(new[] { "FULL.vdf", "metadata.ini" }));
    Assert.That(entries[0].OriginalSize, Is.EqualTo(500));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_EntryCountPastTheEnd_ReadsWhatIsThere_AndSaysPartial() {
    // Given a volume whose header claims far more entries than the file holds
    var img = BuildImage(("a.txt", "A"u8.ToArray()), ("b.txt", "B"u8.ToArray()));
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(272), 1_000_000);

    using var r = Read(img);
    Assert.That(r.IsPartial, Is.True);
    var listed = new VdfsFormatDescriptor().List(new MemoryStream(img), null);
    Assert.That(listed.Select(e => e.Name), Has.Member("metadata.ini"));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_DirectoryPointingAtItself_DoesNotLoop() {
    // Given a directory entry whose child index names its own block
    var img = BuildImage(("dir/a.txt", "A"u8.ToArray()));
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(296 + 64), 0);

    // Then the walk stops and reports the catalog as damaged
    using var r = Read(img);
    Assert.That(r.IsPartial, Is.True);
  }

  [Test, Category("ErrorHandling")]
  public void Reader_PayloadPastTheEnd_IsDropped_AndSaysPartial() {
    var img = BuildImage(("a.txt", Pattern(100)), ("b.txt", Pattern(10)));
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(296 + 64), (uint)img.Length);
    using var r = Read(img);
    Assert.That(r.IsPartial, Is.True);
    Assert.That(r.Entries.Select(e => e.Name), Is.EqualTo(new[] { "B.TXT" }));
  }

  [Test, Category("ErrorHandling")]
  public void Modify_DamagedCatalog_IsRefused() {
    var img = BuildImage(("a.txt", "A"u8.ToArray()));
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(272), 5);
    using var ms = new MemoryStream();
    ms.Write(img);
    Assert.Throws<InvalidDataException>(() => VdfsInPlaceModifier.AddFile(ms, "b.txt", [1]));
  }

  [Test, Category("ErrorHandling")]
  public void Zipped_CorruptBlock_ExtractRefuses() {
    // Given a zipped file whose first block's deflate data is damaged (header 296, one
    // catalog entry, the stream header, the first block header)
    var img = BuildImage(true, ("a.bin", Pattern(5000)));
    var blockData = 296 + 80 + 12 + 12;
    for (var i = 4; i < 40; ++i) img[blockData + i] ^= 0xFF;
    using var damaged = Read(img);
    Assert.Throws<InvalidDataException>(() => damaged.Extract(damaged.Entries[0]));
  }
}
