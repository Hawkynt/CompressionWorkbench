using System.Buffers.Binary;
using Compression.Core.ExecutableUnpacking;
using Compression.Lib;
using Compression.Tests.Support;
using FileFormat.ExePackers;

namespace Compression.Tests.ExePackers;

/// <summary>
/// PECompact 2 (LZMA codec). The format steps are covered on byte arrays; the end-to-end
/// proof runs on real samples of the public chesvectain/PackingData corpus, whose
/// unpacked originals are the oracle: the code and data sections must come back
/// byte-identical at their original RVAs.
/// </summary>
[TestFixture]
public class PeCompactHandlerTests {

  // ---- call/jump filter ---------------------------------------------------------------

  [Test, Category("HappyPath")]
  public void ReverseCallFilter_GivenAMarkedCall_WhenReversed_ThenTheOperandIsRelativeToTheOpcode() {
    // A call at offset 0x10 to offset 0x1234 is stored as E8 <marker> 00 12 34.
    var code = new byte[0x40];
    code[0x10] = 0xE8;
    code[0x11] = 0xAF;
    code[0x12] = 0x00;
    code[0x13] = 0x12;
    code[0x14] = 0x34;

    PeCompactFormat.ReverseCallFilter(code, 0xAF, 0x0E);

    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(code.AsSpan(0x11)), Is.EqualTo(0x1234u - 0x10u));
  }

  [Test, Category("HappyPath")]
  public void ReverseCallFilter_GivenABackwardJump_WhenReversed_ThenTheOperandIsNegative() {
    var code = new byte[0x200];
    code[0x180] = 0xE9;
    code[0x181] = 0x0E;
    code[0x184] = 0x20; // target 0x000020

    PeCompactFormat.ReverseCallFilter(code, 0xAF, 0x0E);

    Assert.That(BinaryPrimitives.ReadInt32LittleEndian(code.AsSpan(0x181)), Is.EqualTo(0x20 - 0x180));
  }

  [Test, Category("EdgeCase")]
  public void ReverseCallFilter_GivenOpcodesWithoutTheirMarker_WhenReversed_ThenNothingChanges() {
    // The call marker after E9 and the jump marker after E8 are not conversions.
    byte[] code = [0xE8, 0x0E, 1, 2, 3, 0xE9, 0xAF, 4, 5, 6, 0x90, 0x90];
    var before = (byte[])code.Clone();

    PeCompactFormat.ReverseCallFilter(code, 0xAF, 0x0E);

    Assert.That(code, Is.EqualTo(before).AsCollection);
  }

  [Test, Category("Boundary")]
  public void ReverseCallFilter_GivenAConversionEndingExactlyAtTheRegionEnd_WhenReversed_ThenItIsConverted() {
    byte[] code = [0x90, 0xE8, 0xAF, 0x00, 0x00, 0x08];

    PeCompactFormat.ReverseCallFilter(code, 0xAF, 0x0E);

    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(code.AsSpan(2)), Is.EqualTo(8u - 1u));
  }

  [Test, Category("Boundary")]
  public void ReverseCallFilter_GivenAMarkedOpcodeCutByTheRegionEnd_WhenReversed_ThenItIsLeftAlone() {
    byte[] code = [0x90, 0x90, 0xE8, 0xAF, 0x00, 0x00];
    var before = (byte[])code.Clone();

    PeCompactFormat.ReverseCallFilter(code, 0xAF, 0x0E);

    Assert.That(code, Is.EqualTo(before).AsCollection);
  }

  [Test, Category("EdgeCase")]
  public void ReverseCallFilter_GivenAConvertedSite_WhenReversed_ThenItsOperandIsNotRescanned() {
    // E8 AF E8 AF 00: the operand starts with another E8 AF pair, which must not be
    // treated as a second conversion once the first one has consumed it.
    byte[] code = [0xE8, 0xAF, 0xE8, 0xAF, 0x00, 0x90, 0x90, 0x90, 0x90, 0x90];

    PeCompactFormat.ReverseCallFilter(code, 0xAF, 0x0E);

    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(code.AsSpan(1)), Is.EqualTo(0xE8AF00u));
  }

  // ---- data-block table ---------------------------------------------------------------

  [Test, Category("HappyPath")]
  public void ReadTable_GivenSectionAndExceptionRecords_WhenRead_ThenEveryFieldIsReturned() {
    var block = BuildTableBlock(withExceptions: true);

    var table = PeCompactFormat.ReadTable(block, out var failure);

    Assert.That(table, Is.Not.Null, failure);
    Assert.Multiple(() => {
      Assert.That(table!.BaseRva, Is.EqualTo(0x1000u));
      Assert.That(table.UncompressedSize, Is.EqualTo(0x39117u));
      Assert.That(table.CompressedSize, Is.EqualTo(0x144AEu));
      Assert.That(table.Filters.Select(f => (f.Rva, f.LastSite, f.CallMarker, f.JumpMarker)), Is.EqualTo(new[] {
        (0x1000u, 0x27829u, (byte)0xAF, (byte)0x0E),
      }).AsCollection);
      Assert.That(table.EntryRva, Is.EqualTo(0x7F58u));
      Assert.That(table.CoveredBytesRva, Is.EqualTo(0x7059Fu));
      Assert.That(table.Sections.Select(s => (s.Rva, s.PayloadOffset, s.Size)), Is.EqualTo(new[] {
        (0x29000u, 0x2882Fu, 0xE280u),
        (0x38000u, 0x36AAFu, 0x3668u),
      }).AsCollection);
      Assert.That(table.Exceptions.Select(e => (e.Rva, e.Opcode, e.Marker)), Is.EqualTo(new[] {
        (0x704F5u, (byte)0xE8, (byte)0xAF),
      }).AsCollection);
    });
  }

  [Test, Category("EdgeCase")]
  public void ReadTable_GivenNoExceptionRecord_WhenRead_ThenTheExceptionListIsEmpty() {
    var table = PeCompactFormat.ReadTable(BuildTableBlock(withExceptions: false), out var failure);

    Assert.That(table, Is.Not.Null, failure);
    Assert.That(table!.Exceptions, Is.Empty);
  }

  [Test, Category("Exceptional")]
  public void ReadTable_GivenBytesWithoutAHeaderRecord_WhenRead_ThenNullWithAReason() {
    var block = Enumerable.Range(0, 0x400).Select(i => (byte)(i * 13)).ToArray();

    var table = PeCompactFormat.ReadTable(block, out var failure);

    Assert.Multiple(() => {
      Assert.That(table, Is.Null);
      Assert.That(failure, Is.Not.Empty);
    });
  }

  [Test, Category("Exceptional")]
  public void ReadTable_GivenAHeaderWithoutSectionRecords_WhenRead_ThenNull() {
    var block = BuildTableBlock(withExceptions: false);
    // Break the record in front of the header: its "2" field no longer matches.
    var header = FindHeader(block);
    block[header - RecordSize + 0x18] = 7;

    Assert.That(PeCompactFormat.ReadTable(block, out _), Is.Null);
  }

  [Test, Category("Exceptional")]
  public void ReadTable_GivenAnEmptyBlock_WhenRead_ThenNull() =>
    Assert.That(PeCompactFormat.ReadTable([], out _), Is.Null);

  // ---- handler on inputs that are not the known layout --------------------------------

  [Test, Category("Exceptional")]
  public void Unpack_GivenAMarkerOnlyImage_WhenUnpacked_ThenDetectionOnlyWithoutThrowing() {
    var image = MinimalPeWithMarker();
    var handler = ExecutablePackerHandlers.All.Single(h => h.Id == "pecompact");

    var detection = handler.Detect(image);
    var result = handler.Unpack(handler.Parse(image, detection), new());

    Assert.Multiple(() => {
      Assert.That(detection.IsMatch, Is.True);
      Assert.That(result.Level, Is.EqualTo(ExecutableUnpackLevel.DetectionOnly));
      Assert.That(result.Diagnostics.Any(d => d.Code == ExecutableDiagnosticCode.UnsupportedPackerVersion), Is.True);
    });
  }

  [Test, Category("Exceptional")]
  public void Detect_GivenNonPeBytes_WhenDetected_ThenNoMatch() {
    var handler = ExecutablePackerHandlers.All.Single(h => h.Id == "pecompact");
    Assert.That(handler.Detect("PECompact2 but not an executable"u8).IsMatch, Is.False);
  }

  // ---- real samples: the corpus originals are the oracle ------------------------------

  /// <summary>
  /// One sample per layout seen in the corpus: entry sequence at the original entry point
  /// (Contig, AccessEnum), at the start of the section (Autoruns, Bginfo), call exceptions
  /// (Contig, Autoruns), jump exceptions (grepWin), no exceptions (AccessEnum), code that
  /// is not the first section (PAGEANT) and LZMA with eight literal context bits (Bginfo).
  /// </summary>
  private static readonly string[] Samples = [
    "Contig.exe", "AccessEnum.exe", "Autoruns.exe", "grepWin-1.9.0_portable.exe", "PAGEANT_32.EXE", "Bginfo.exe",
  ];

  [Test, Category("ExternalInterop")]
  public void RealSample_GivenTheCorpusOriginal_WhenUnpacked_ThenCodeAndDataSectionsAreByteIdentical(
      [ValueSource(nameof(Samples))] string name) {
    var pair = ExecutablePackerToolCache.GetPackingDataPair("PECompact", "pecompact_" + name, name);
    if (pair is null)
      Assert.Ignore("Set CWB_PACKINGDATA to a chesvectain/PackingData checkout or CWB_DOWNLOAD_EXE_PACKER_TOOLS=1.");

    var (codeExact, dataExact, result) = CompareWithOriginal(File.ReadAllBytes(pair!.Value.Packed), File.ReadAllBytes(pair.Value.Original));

    Assert.Multiple(() => {
      Assert.That(result.Level, Is.EqualTo(ExecutableUnpackLevel.PayloadDecompressed),
        string.Join(" | ", result.Diagnostics.Select(d => d.Message)));
      Assert.That(codeExact, Is.True, "code section");
      Assert.That(dataExact, Is.True, "data section");
    });
  }

  /// <summary>
  /// The whole corpus when a local checkout is configured. Two samples (Hash.exe, ftp.exe)
  /// keep their data block in a form that is not an aPLib stream and are refused; the code
  /// section must match the original for all others but the three system binaries whose
  /// import address table lives inside the code section (PECompact rebuilds imports).
  /// </summary>
  [Test, Category("ExternalInterop")]
  public void RealCorpus_GivenEverySampleWithAnOriginal_WhenUnpacked_ThenCodeSectionsMatch() {
    var root = ExecutablePackerToolCache.GetPackingDataRoot();
    if (root is null)
      Assert.Ignore("Set CWB_PACKINGDATA to a chesvectain/PackingData checkout.");

    int samples = 0, decompressed = 0, compared = 0, exact = 0;
    var mismatches = new List<string>();
    var undecoded = new List<string>();
    foreach (var packedPath in Directory.EnumerateFiles(Path.Combine(root!, "PackingData", "PECompact"))) {
      byte[] packed;
      try { packed = File.ReadAllBytes(packedPath); } catch (IOException) { continue; } catch (UnauthorizedAccessException) { continue; }
      ++samples;
      var originalPath = Path.Combine(root!, "Notpacked", Path.GetFileName(packedPath)["pecompact_".Length..]);
      var result = ExecutablePackerHandlers.TryUnpack(packed);
      if (result?.Level >= ExecutableUnpackLevel.PayloadDecompressed)
        ++decompressed;
      else
        undecoded.Add($"{Path.GetFileName(packedPath)}: {result?.Diagnostics.FirstOrDefault(d => d.IsError)?.Message ?? "no handler"}");
      if (!File.Exists(originalPath))
        continue;

      ++compared;
      var (codeExact, _, _) = CompareWithOriginal(packed, File.ReadAllBytes(originalPath));
      if (codeExact) ++exact;
      else mismatches.Add(Path.GetFileName(packedPath));
    }

    TestContext.Out.WriteLine($"samples={samples} decompressed={decompressed} compared={compared} codeExact={exact} mismatches={string.Join(",", mismatches)} undecoded={string.Join(" | ", undecoded)}");
    Assert.Multiple(() => {
      Assert.That(decompressed, Is.GreaterThanOrEqualTo(samples - 2), string.Join(" | ", undecoded));
      Assert.That(exact, Is.GreaterThanOrEqualTo(compared - 5), string.Join(", ", mismatches));
    });
  }

  private static (bool CodeExact, bool DataExact, UnpackResult Result) CompareWithOriginal(byte[] packed, byte[] original) {
    var result = ExecutablePackerHandlers.TryUnpack(packed);
    Assert.That(result, Is.Not.Null);
    var memory = result!.Artifacts.FirstOrDefault(a => a.Name == "memory_image.bin")?.Data;
    if (memory is null)
      return (false, false, result);

    var info = ExecutableContainerParsers.Pe.Parse(original);
    var code = info.Regions.First(r => info.EntryPoint >= r.VirtualAddress && info.EntryPoint < r.VirtualAddress + r.VirtualSize);
    var data = info.Regions.FirstOrDefault(r => r.Name is ".data" or "DATA");
    return (SectionMatches(memory, code), data is null || SectionMatches(memory, data), result);
  }

  /// <summary>The section's mapped content, trailing zero padding excluded, against the memory image.</summary>
  private static bool SectionMatches(byte[] memory, ExecutableRegion region) {
    var bytes = region.FileBytes ?? [];
    var mapped = bytes.AsSpan(0, (int)Math.Min((ulong)bytes.Length, region.VirtualSize)).TrimEnd((byte)0);
    return region.VirtualAddress + (ulong)mapped.Length <= (ulong)memory.Length &&
      memory.AsSpan((int)region.VirtualAddress, mapped.Length).SequenceEqual(mapped);
  }

  // ---- fixtures -----------------------------------------------------------------------

  private const int RecordSize = 0x1C;

  /// <summary>
  /// A data block shaped like the one in pecompact_Contig.exe: a record-less lead-in with
  /// the entry and covered-bytes RVAs, two section records, the header record and,
  /// optionally, one call-exception record.
  /// </summary>
  private static byte[] BuildTableBlock(bool withExceptions) {
    const uint tag = 0xB4238990;
    var block = new List<byte>();
    void U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); block.AddRange(b.ToArray()); }

    block.AddRange(Enumerable.Repeat((byte)0x90, 0x20));
    U32(0x7F58);   // entry RVA
    U32(0x7059F);  // RVA of the bytes the entry sequence covers
    // First section record: its lead-in replaces the tag pair.
    U32(0); U32(0);
    U32(0x38000); U32(0x36AAF); U32(0x3668); U32(0); U32(2);
    U32(tag); U32(0xFFFF0000);
    U32(0x29000); U32(0x2882F); U32(0xE280); U32(0); U32(2);
    // Header record.
    U32(tag); U32(0xFFFF0000);
    U32(0x1000); U32(0x1000); U32(0x144AE); U32(0x39117); U32(0x10); U32(0); U32(0);
    U32(0x1000); U32(0x1000); U32(0x27829); U32(0);
    block.AddRange(new byte[] { 0x08, 0x00, 0xAF, 0x0E });
    if (withExceptions) {
      U32(tag); U32(0xFFFF0000);
      U32(0x704F5); U32(0); U32(0); U32(0);
      block.AddRange(new byte[] { 0x00, 0x02, 0xE8, 0xAF });
      U32(0); U32(0);
    }
    U32(0xDC);
    block.AddRange(Enumerable.Repeat((byte)0xCC, 0x40));
    return [.. block];
  }

  private static int FindHeader(byte[] block) {
    for (var i = 0; i + 0x18 < block.Length; ++i)
      if (BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(i + 4)) == 0xFFFF0000 &&
          BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(i + 0x18)) == 0x10)
        return i;
    throw new InvalidOperationException("fixture has no header record");
  }

  /// <summary>Headers and one section; the entry point holds no PECompact entry sequence.</summary>
  private static byte[] MinimalPeWithMarker() {
    var image = new byte[0x600];
    image[0] = (byte)'M'; image[1] = (byte)'Z';
    BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(0x3C), 0x80);
    "PE\0\0"u8.CopyTo(image.AsSpan(0x80));
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x84), 0x14C);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x86), 1);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x94), 0xE0);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x98), 0x10B);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x98 + 16), 0x1000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x98 + 28), 0x400000);
    var section = 0x98 + 0xE0;
    ".text\0\0\0"u8.CopyTo(image.AsSpan(section));
    "PEC2NO"u8.CopyTo(image.AsSpan(section + 24));
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(section + 8), 0x1000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(section + 12), 0x1000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(section + 16), 0x200);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(section + 20), 0x400);
    "PECompact2\0"u8.CopyTo(image.AsSpan(0x420));
    return image;
  }
}
