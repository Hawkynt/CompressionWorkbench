#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using FileFormat.Cpio;

namespace Compression.Tests.Cpio;

/// <summary>
/// Covers the four historical cpio header variants — 7th Edition binary in
/// both byte orders, POSIX portable ASCII ("odc"), SVR4 new ASCII and SVR4 CRC
/// — across reading, writing, the in-place modifier and the descriptor.
/// </summary>
/// <remarks>
/// The expected bytes here were taken from <c>cpio(5)</c> and confirmed against
/// archives produced by libarchive 3.7.2 and GNU cpio 2.15;
/// <see cref="CpioLibarchiveInteropTests"/> runs the same comparison live
/// wherever those tools are installed.
/// </remarks>
[TestFixture]
public class CpioVariantTests {

  private static readonly CpioArchiveFormat[] AllFormats = [
    CpioArchiveFormat.NewAscii,
    CpioArchiveFormat.NewCrc,
    CpioArchiveFormat.PortableAscii,
    CpioArchiveFormat.BinaryLittleEndian,
    CpioArchiveFormat.BinaryBigEndian,
  ];

  // ── Round-trip, one equivalence class per variant ──────────────────

  [Test, Category("RoundTrip"), Category("HappyPath")]
  public void RoundTrip_EveryVariant_PreservesNamesDataAndVariant(
    [ValueSource(nameof(AllFormats))] CpioArchiveFormat format
  ) {
    byte[] binary = [0x00, 0xFF, 0x71, 0xC7, 0x30, 0x37];
    var archive = Build(format,
      w => {
        w.AddDirectory("docs");
        w.AddFile("docs/readme.txt", "the quick brown fox"u8);
        w.AddFile("empty.dat", []);
        w.AddFile("binary.bin", binary);
      });

    var entries = ReadAll(archive);

    Assert.That(entries.Select(x => x.Entry.Name),
      Is.EqualTo(new[] { "docs", "docs/readme.txt", "empty.dat", "binary.bin" }));
    Assert.Multiple(() => {
      Assert.That(entries.All(x => x.Entry.Format == format), Is.True, "every entry must report the variant it was written in");
      Assert.That(entries[0].Entry.IsDirectory, Is.True);
      Assert.That(entries[1].Data, Is.EqualTo("the quick brown fox"u8.ToArray()));
      Assert.That(entries[2].Data, Is.Empty);
      Assert.That(entries[3].Data, Is.EqualTo(binary));
    });
  }

  [Test, Category("HappyPath")]
  public void Writer_EmitsTheMagicOfTheRequestedVariant() {
    Assert.Multiple(() => {
      Assert.That(Magic(CpioArchiveFormat.NewAscii, 6), Is.EqualTo("070701"u8.ToArray()));
      Assert.That(Magic(CpioArchiveFormat.NewCrc, 6), Is.EqualTo("070702"u8.ToArray()));
      Assert.That(Magic(CpioArchiveFormat.PortableAscii, 6), Is.EqualTo("070707"u8.ToArray()));
      Assert.That(Magic(CpioArchiveFormat.BinaryLittleEndian, 2), Is.EqualTo(new byte[] { 0xC7, 0x71 }));
      Assert.That(Magic(CpioArchiveFormat.BinaryBigEndian, 2), Is.EqualTo(new byte[] { 0x71, 0xC7 }));
    });

    static byte[] Magic(CpioArchiveFormat format, int length)
      => Build(format, w => w.AddFile("x", [1])).Take(length).ToArray();
  }

  /// <summary>
  /// The header field offsets, taken from <c>cpio(5)</c>: 76 bytes of octal for
  /// odc, 26 bytes of 16-bit words for binary, 110 hexadecimal for SVR4. Getting
  /// one of them wrong still round-trips through our own reader, which is exactly
  /// why the layout is asserted against the wire and not against ourselves.
  /// </summary>
  [Test, Category("HappyPath")]
  public void PortableAsciiHeader_MatchesTheDocumentedFieldLayout() {
    var archive = Build(CpioArchiveFormat.PortableAscii, w => w.AddFile("payload.txt", "hello cpio\n"u8));
    var header = Encoding.ASCII.GetString(archive, 0, 76);

    Assert.Multiple(() => {
      Assert.That(header[..6], Is.EqualTo("070707"), "c_magic");
      Assert.That(header.Substring(36, 6), Is.EqualTo("000001"), "c_nlink");
      Assert.That(header.Substring(59, 6), Is.EqualTo("000014"), "c_namesize — 12 bytes including the NUL");
      Assert.That(header.Substring(65, 11), Is.EqualTo("00000000013"), "c_filesize — 11 bytes");
      // odc pads nothing: the name follows the header and the data follows the name.
      Assert.That(Encoding.ASCII.GetString(archive, 76, 12), Is.EqualTo("payload.txt\0"));
      Assert.That(Encoding.ASCII.GetString(archive, 88, 11), Is.EqualTo("hello cpio\n"));
    });
  }

  [Test, Category("HappyPath")]
  public void BinaryHeader_StoresLongFieldsAsHighWordFirst() {
    var archive = Build(CpioArchiveFormat.BinaryLittleEndian, w => w.AddFile("payload.txt", "hello cpio\n"u8));

    Assert.Multiple(() => {
      Assert.That(archive.AsSpan(20, 2).ToArray(), Is.EqualTo(new byte[] { 0x0C, 0x00 }), "c_namesize, 16-bit little-endian");
      // c_filesize is two 16-bit words, most significant first: 0x0000 then 0x000B.
      Assert.That(archive.AsSpan(22, 4).ToArray(), Is.EqualTo(new byte[] { 0x00, 0x00, 0x0B, 0x00 }), "c_filesize");
      Assert.That(Encoding.ASCII.GetString(archive, 26, 12), Is.EqualTo("payload.txt\0"));
    });
  }

  [Test, Category("HappyPath")]
  public void NewCrcHeader_StoresTheUnsignedByteSumOfThePayload() {
    var archive = Build(CpioArchiveFormat.NewCrc, w => w.AddFile("payload.txt", "hello cpio\n"u8));
    var expected = 0u;
    foreach (var b in "hello cpio\n"u8)
      expected += b;

    Assert.That(expected, Is.EqualTo(1001u), "cross-check against the sum GNU cpio writes");
    Assert.That(Encoding.ASCII.GetString(archive, 102, 8), Is.EqualTo("000003E9"));
    Assert.That(ReadAll(archive)[0].Entry.Checksum, Is.EqualTo(expected));
  }

  // ── Boundaries ─────────────────────────────────────────────────────

  [Test, Category("Boundary"), Category("RoundTrip")]
  public void EmptyArchive_IsJustTheTrailerAndReadsBackEmpty(
    [ValueSource(nameof(AllFormats))] CpioArchiveFormat format
  ) {
    var archive = Build(format, _ => { });

    Assert.That(ReadAll(archive), Is.Empty);
    Assert.That(archive.Length, Is.EqualTo(CpioLayoutProbe.TrailerSize(format)),
      "an empty archive is exactly one trailer entry, with no padding beyond the variant's own alignment");
    Assert.That(CpioReader.PeekFormat(new MemoryStream(archive)), Is.EqualTo(format));
  }

  [Test, Category("Boundary")]
  public void Reader_StopsAtTheTrailerAndIgnoresTheBlockPaddingBehindIt(
    [ValueSource(nameof(AllFormats))] CpioArchiveFormat format
  ) {
    // libarchive pads its output out to a 512-byte block; the trailer, not the
    // end of the file, is what ends the archive.
    var archive = Build(format, w => w.AddFile("only.txt", "data"u8));
    var padded = new byte[((archive.Length + 511) / 512) * 512];
    archive.CopyTo(padded, 0);

    var entries = ReadAll(padded);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Entry.Name, Is.EqualTo("only.txt"));
  }

  [Test, Category("Boundary"), Category("RoundTrip")]
  public void PayloadSizesAcrossEveryAlignmentBoundary_RoundTrip(
    [ValueSource(nameof(AllFormats))] CpioArchiveFormat format
  ) {
    foreach (var size in new[] { 0, 1, 2, 3, 4, 5, 7, 8, 9, 63, 64, 65 }) {
      var data = new byte[size];
      for (var i = 0; i < size; ++i)
        data[i] = (byte)(i * 7 + 1);

      var entries = ReadAll(Build(format, w => { w.AddFile("a", data); w.AddFile("b", "tail"u8); }));
      Assert.That(entries, Has.Count.EqualTo(2), $"{format}/{size}: alignment drift lost an entry");
      Assert.That(entries[0].Data, Is.EqualTo(data), $"{format}/{size}");
      Assert.That(entries[1].Data, Is.EqualTo("tail"u8.ToArray()), $"{format}/{size}: the following entry must stay aligned");
    }
  }

  [Test, Category("Boundary"), Category("RoundTrip")]
  public void NameLengthsAcrossEveryAlignmentBoundary_RoundTrip(
    [ValueSource(nameof(AllFormats))] CpioArchiveFormat format
  ) {
    foreach (var length in new[] { 1, 2, 3, 4, 5, 8, 254, 255, 256 }) {
      var name = new string('n', length);
      var entries = ReadAll(Build(format, w => { w.AddFile(name, "x"u8); w.AddFile("tail", "y"u8); }));
      Assert.That(entries[0].Entry.Name, Is.EqualTo(name), $"{format}/{length}");
      Assert.That(entries[1].Entry.Name, Is.EqualTo("tail"), $"{format}/{length}: the following entry must stay aligned");
    }
  }

  /// <summary>
  /// The binary header's <c>c_namesize</c> is 16 bits, so the longest name it
  /// can describe is 65534 characters plus the NUL. One more is not a long name,
  /// it is a silently truncated one.
  /// </summary>
  [Test, Category("Boundary")]
  public void BinaryVariant_AcceptsTheLongestRepresentableNameAndRefusesTheNext() {
    var longest = new string('n', ushort.MaxValue - 1);
    var entries = ReadAll(Build(CpioArchiveFormat.BinaryLittleEndian, w => w.AddFile(longest, "x"u8)));
    Assert.That(entries[0].Entry.Name, Is.EqualTo(longest));

    var tooLong = new string('n', ushort.MaxValue);
    Assert.Throws<ArgumentOutOfRangeException>(
      () => Build(CpioArchiveFormat.BinaryLittleEndian, w => w.AddFile(tooLong, "x"u8)));
  }

  [Test, Category("Boundary")]
  public void PortableAsciiVariant_RefusesAPathnameBeyondItsSixOctalDigits() {
    var tooLong = new string('n', 0x40000);
    Assert.Throws<ArgumentOutOfRangeException>(
      () => Build(CpioArchiveFormat.PortableAscii, w => w.AddFile(tooLong, "x"u8)));
  }

  // ── Exceptional cases ──────────────────────────────────────────────

  [Test, Category("EdgeCase")]
  public void Reader_RejectsAnUnknownMagic() {
    var archive = Build(CpioArchiveFormat.NewAscii, w => w.AddFile("a", "x"u8));
    archive[5] = (byte)'9'; // 070709 is no cpio variant

    var ex = Assert.Throws<InvalidDataException>(() => ReadAll(archive));
    Assert.That(ex!.Message, Does.Contain("magic"));
  }

  [Test, Category("EdgeCase")]
  public void Reader_RejectsATruncatedHeader(
    [ValueSource(nameof(AllFormats))] CpioArchiveFormat format
  ) {
    var archive = Build(format, w => w.AddFile("a", "payload"u8));
    var cut = archive.Take(CpioLayoutProbe.HeaderSize(format) - 2).ToArray();

    Assert.Throws<EndOfStreamException>(() => ReadAll(cut));
  }

  [Test, Category("EdgeCase")]
  public void Reader_RejectsATruncatedPayload(
    [ValueSource(nameof(AllFormats))] CpioArchiveFormat format
  ) {
    var archive = Build(format, w => w.AddFile("a", new byte[4096]));
    var cut = archive.Take(CpioLayoutProbe.HeaderSize(format) + 8).ToArray();

    Assert.Throws<EndOfStreamException>(() => ReadAll(cut));
  }

  [Test, Category("EdgeCase")]
  public void Reader_RejectsAPathnameThatIsNotNulTerminated() {
    var archive = Build(CpioArchiveFormat.NewAscii, w => w.AddFile("abc", "x"u8));
    archive[110 + 3] = (byte)'!'; // clobber the NUL the c_namesize counted

    Assert.Throws<InvalidDataException>(() => ReadAll(archive));
  }

  [Test, Category("EdgeCase")]
  public void Reader_RejectsANonOctalDigitInAPortableAsciiField() {
    var archive = Build(CpioArchiveFormat.PortableAscii, w => w.AddFile("a", "x"u8));
    archive[70] = (byte)'9'; // c_filesize is octal; 8 and 9 are not digits there

    Assert.Throws<InvalidDataException>(() => ReadAll(archive));
  }

  [Test, Category("EdgeCase")]
  public void Reader_RejectsACrcEntryWhosePayloadDoesNotMatchItsChecksum() {
    var archive = Build(CpioArchiveFormat.NewCrc, w => w.AddFile("a.txt", "corrupt me"u8));
    var payload = Array.IndexOf(archive, (byte)'c', 110);
    archive[payload] ^= 0x01;

    var ex = Assert.Throws<InvalidDataException>(() => ReadAll(archive));
    Assert.That(ex!.Message, Does.Contain("checksum"));
  }

  [Test, Category("EdgeCase")]
  public void Reader_AcceptsACrcEntryWhoseChecksumMatches() {
    var archive = Build(CpioArchiveFormat.NewCrc, w => w.AddFile("a.txt", "intact"u8));
    Assert.That(ReadAll(archive)[0].Data, Is.EqualTo("intact"u8.ToArray()));
  }

  [Test, Category("EdgeCase")]
  public void PeekFormat_ReportsNothingForBytesThatAreNotACpioHeader() {
    Assert.Multiple(() => {
      Assert.That(CpioReader.PeekFormat(new MemoryStream([])), Is.Null, "empty stream");
      Assert.That(CpioReader.PeekFormat(new MemoryStream([0x07])), Is.Null, "shorter than a magic");
      Assert.That(CpioReader.PeekFormat(new MemoryStream("PK\0\0"u8.ToArray())), Is.Null, "a zip, not a cpio");
    });
  }

  // ── Streaming writer parity ────────────────────────────────────────

  [Test, Category("ByteIdentity")]
  public void StreamingWrite_IsByteIdenticalToBufferedWrite(
    [ValueSource(nameof(AllFormats))] CpioArchiveFormat format
  ) {
    var payload = new byte[5000];
    Random.Shared.NextBytes(payload);

    var buffered = Build(format, w => w.AddFile("big.bin", payload));
    var streamed = Build(format, w => w.AddStreamingFile("big.bin", payload.Length, new MemoryStream(payload)));

    Assert.That(streamed, Is.EqualTo(buffered));
  }

  [Test, Category("EdgeCase")]
  public void StreamingWrite_OfACrcEntryNeedsASeekableSourceToPreScanTheChecksum() {
    using var target = new MemoryStream();
    using var writer = new CpioWriter(target, CpioArchiveFormat.NewCrc, leaveOpen: true);

    Assert.Throws<NotSupportedException>(
      () => writer.AddStreamingFile("a", 4, new NonSeekableStream("data"u8.ToArray())));
  }

  // ── The variant survives every operation ───────────────────────────

  [Test, Category("RoundTrip")]
  public void Modifier_AddAndRemove_KeepTheArchiveInItsOriginalVariant(
    [ValueSource(nameof(AllFormats))] CpioArchiveFormat format
  ) {
    using var archive = new MemoryStream();
    archive.Write(Build(format, w => { w.AddFile("keep.txt", "keep"u8); w.AddFile("drop.txt", "drop"u8); }));

    CpioModifier.AddFile(archive, "added.txt", "added"u8.ToArray());
    Assert.That(CpioModifier.RemoveFile(archive, "drop.txt"), Is.True);

    var entries = ReadAll(archive.ToArray());
    Assert.Multiple(() => {
      Assert.That(entries.Select(x => x.Entry.Name), Is.EqualTo(new[] { "keep.txt", "added.txt" }));
      Assert.That(entries[1].Data, Is.EqualTo("added"u8.ToArray()));
      Assert.That(entries.All(x => x.Entry.Format == format), Is.True,
        "an in-place edit must not rewrite the archive into a different variant");
    });
  }

  [Test, Category("RoundTrip")]
  public void Defragment_KeepsTheArchiveInItsOriginalVariant(
    [ValueSource(nameof(AllFormats))] CpioArchiveFormat format
  ) {
    using var archive = new MemoryStream();
    archive.Write(Build(format, w => { w.AddFile("a.txt", "alpha"u8); w.AddFile("b.txt", "beta"u8); }));

    ((IArchiveDefragmentable)new CpioFormatDescriptor()).Defragment(archive);

    var entries = ReadAll(archive.ToArray());
    Assert.Multiple(() => {
      Assert.That(entries.Select(x => x.Entry.Name), Is.EqualTo(new[] { "a.txt", "b.txt" }));
      Assert.That(entries[0].Data, Is.EqualTo("alpha"u8.ToArray()));
      Assert.That(entries.All(x => x.Entry.Format == format), Is.True,
        "defrag is maintenance, not a format conversion");
    });
  }

  [Test, Category("RoundTrip")]
  public void EnumerateLayout_ReportsOffsetsThatLandOnTheRealHeaders(
    [ValueSource(nameof(AllFormats))] CpioArchiveFormat format
  ) {
    using var archive = new MemoryStream();
    var bytes = Build(format, w => { w.AddFile("a.txt", "alpha"u8); w.AddFile("b.txt", "beta-payload"u8); });
    archive.Write(bytes);

    var blocks = ((IArchiveLayoutMap)new CpioFormatDescriptor()).EnumerateLayout(archive).ToList();
    var payloads = blocks.Where(b => b.Kind == DefragBlockKind.Used).ToList();

    Assert.That(payloads, Has.Count.EqualTo(2));
    Assert.Multiple(() => {
      Assert.That(Encoding.ASCII.GetString(bytes, (int)payloads[0].Offset, (int)payloads[0].Length), Is.EqualTo("alpha"));
      Assert.That(Encoding.ASCII.GetString(bytes, (int)payloads[1].Offset, (int)payloads[1].Length), Is.EqualTo("beta-payload"));
    });
  }

  // ── The descriptor's Format option ─────────────────────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_CreateHonoursTheFormatOption(
    [Values("newc", "crc", "odc", "bin", "bin-be")] string option
  ) {
    var expected = option switch {
      "newc" => CpioArchiveFormat.NewAscii,
      "crc" => CpioArchiveFormat.NewCrc,
      "odc" => CpioArchiveFormat.PortableAscii,
      "bin" => CpioArchiveFormat.BinaryLittleEndian,
      _ => CpioArchiveFormat.BinaryBigEndian,
    };

    var temp = Path.Combine(Path.GetTempPath(), $"cwb_cpio_opt_{Guid.NewGuid():N}.txt");
    File.WriteAllBytes(temp, "descriptor payload"u8.ToArray());
    try {
      using var output = new MemoryStream();
      var options = new FormatCreateOptions();
      options.FormatSpecific[CpioFormatDescriptor.FormatOptionKey] = option;
      ((IArchiveCreatable)new CpioFormatDescriptor()).Create(output, [new(temp, "payload.txt", false)], options);

      var entries = ReadAll(output.ToArray());
      Assert.That(entries, Has.Count.EqualTo(1));
      Assert.Multiple(() => {
        Assert.That(entries[0].Entry.Format, Is.EqualTo(expected));
        Assert.That(entries[0].Entry.Name, Is.EqualTo("payload.txt"));
        Assert.That(entries[0].Data, Is.EqualTo("descriptor payload"u8.ToArray()));
      });
    } finally {
      File.Delete(temp);
    }
  }

  [Test, Category("HappyPath")]
  public void Descriptor_PublishesTheVariantsItCanWrite() {
    var schema = ((IFormatOptionsSchema)new CpioFormatDescriptor()).OptionsSchema;
    var knob = schema.Single(o => o.Key == CpioFormatDescriptor.FormatOptionKey);

    Assert.Multiple(() => {
      Assert.That(knob.Kind, Is.EqualTo(FormatOptionKind.Enum));
      Assert.That(knob.Default, Is.EqualTo("newc"));
      Assert.That(knob.AllowedValues, Is.EquivalentTo(new[] { "newc", "crc", "odc", "bin", "bin-be" }));
    });
  }

  /// <summary>
  /// The <c>Format</c> key is shared with TAR and anything else that names a
  /// header flavour, and a conversion forwards the whole option map — so a
  /// value meant for another container has to land on the default rather than
  /// blowing up the conversion.
  /// </summary>
  [Test, Category("EdgeCase")]
  public void Descriptor_FallsBackToNewcForAVariantNameThatIsNotItsOwn(
    [Values("gnu", "ustar", "hpodc", "")] string foreignValue
  ) {
    var options = new FormatCreateOptions();
    options.FormatSpecific[CpioFormatDescriptor.FormatOptionKey] = foreignValue;

    using var output = new MemoryStream();
    ((IArchiveCreatable)new CpioFormatDescriptor()).Create(output, [], options);

    Assert.That(CpioReader.PeekFormat(new MemoryStream(output.ToArray())),
      Is.EqualTo(CpioArchiveFormat.NewAscii));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ListsEveryVariant(
    [ValueSource(nameof(AllFormats))] CpioArchiveFormat format
  ) {
    using var archive = new MemoryStream(Build(format, w => { w.AddDirectory("d"); w.AddFile("d/f.txt", "listing"u8); }));
    var listed = ((IArchiveFormatOperations)new CpioFormatDescriptor()).List(archive, null);

    Assert.That(listed.Select(x => x.Name), Is.EqualTo(new[] { "d", "d/f.txt" }));
    Assert.That(listed[1].OriginalSize, Is.EqualTo(7));
  }

  // ── Helpers ────────────────────────────────────────────────────────

  private static byte[] Build(CpioArchiveFormat format, Action<CpioWriter> fill) {
    using var ms = new MemoryStream();
    using (var writer = new CpioWriter(ms, format, leaveOpen: true)) {
      fill(writer);
      writer.Finish();
    }
    return ms.ToArray();
  }

  private static List<(CpioEntry Entry, byte[] Data)> ReadAll(byte[] archive) {
    using var ms = new MemoryStream(archive);
    using var reader = new CpioReader(ms);
    return reader.ReadAll();
  }

  /// <summary>
  /// The per-variant sizes this fixture asserts against, restated from
  /// <c>cpio(5)</c> rather than read out of the production constants — a test
  /// that borrows the number it is checking cannot catch a wrong one.
  /// </summary>
  private static class CpioLayoutProbe {
    public static int HeaderSize(CpioArchiveFormat format) => format switch {
      CpioArchiveFormat.PortableAscii => 76,
      CpioArchiveFormat.BinaryLittleEndian or CpioArchiveFormat.BinaryBigEndian => 26,
      _ => 110,
    };

    /// <summary>Header + "TRAILER!!!\0" + the padding the variant demands after the name.</summary>
    public static int TrailerSize(CpioArchiveFormat format) {
      var alignment = format switch {
        CpioArchiveFormat.PortableAscii => 1,
        CpioArchiveFormat.BinaryLittleEndian or CpioArchiveFormat.BinaryBigEndian => 2,
        _ => 4,
      };
      var unpadded = HeaderSize(format) + "TRAILER!!!\0".Length;
      return unpadded + (alignment - unpadded % alignment) % alignment;
    }
  }

  private sealed class NonSeekableStream(byte[] data) : Stream {
    private readonly MemoryStream _inner = new(data);
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position {
      get => throw new NotSupportedException();
      set => throw new NotSupportedException();
    }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => this._inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
  }
}
