#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Core.Dictionary.Aplib;
using Compression.Core.Dictionary.Lzma;
using Compression.Core.ExecutableUnpacking;

namespace FileFormat.ExePackers;

/// <summary>
/// Unpack handler for PECompact 2 (Bitsum) executables packed with its LZMA codec —
/// the layout every sample of the public <c>chesvectain/PackingData</c> PECompact
/// corpus uses. The original image's sections come back at their original RVAs with
/// the x86 call/jump filter reversed.
/// </summary>
/// <remarks>
/// <para>
/// The layout below was measured by comparing the packed corpus samples with the
/// unpacked originals the same corpus ships; Bitsum publishes no specification. It is
/// written up in <c>docs/PECOMPACT-ON-DISK.md</c>.
/// </para>
/// <para>
/// The first section opens with a 9-byte header — <c>u32</c> uncompressed size, the
/// LZMA properties byte and the <c>u32</c> dictionary size — followed by one LZMA1
/// stream holding every packed section back to back, each with its trailing zero bytes
/// dropped. A 35-byte entry sequence ending in <c>"PECompact2\0"</c> is written over
/// that stream at the entry point (the first byte of the section, or the original entry
/// point inside it); the bytes it covers are kept elsewhere in the file. Its leading
/// <c>B8 imm32</c> names an address 0x24 bytes past a nine-dword parameter block whose
/// first two fields give the RVA and the inflated size of an aPLib-compressed data block.
/// That block carries the section table, the filter parameters, the RVA of the covered
/// bytes and the exception lists the filter needs.
/// </para>
/// <para>
/// What the emitted image is not: PECompact rebuilds the import tables, keeps part of
/// the resources uncompressed in its own <c>.rsrc</c> and reprocesses relocations, so
/// those areas do not come back byte-identical; a diagnostic says so rather than a
/// rebuilt executable being claimed.
/// </para>
/// </remarks>
public sealed class PeCompactExecutablePackerHandler : IExecutablePackerHandler {
  public string Id => "pecompact";
  public string DisplayName => "PECompact 2 LZMA-packed PE";

  private const string PackerLabel = "PECompact";

  public ExecutableUnpackCapabilities Capabilities =>
    ExecutableUnpackCapabilities.CanDetect |
    ExecutableUnpackCapabilities.CanLocatePayload |
    ExecutableUnpackCapabilities.CanDecompressPayload |
    ExecutableUnpackCapabilities.SupportsPe |
    ExecutableUnpackCapabilities.SupportsX86;

  public DetectionResult Detect(ReadOnlySpan<byte> image) {
    if (!PackerScanner.IsPe(image))
      return new(false, this.Id, 0, [new(ExecutableDiagnosticCode.NotPackedExecutable, $"{PackerLabel}: not a valid PE.", true)]);

    var hasMarker = PackerScanner.IndexOfBounded(image, "PEC2"u8, 0x10000) >= 0
      || PackerScanner.IndexOfBounded(image, "PECompact2"u8, 0x10000) >= 0;
    var hasSection = PackerScanner.GetPeSections(image).Any(s => s.Name.StartsWith(".pec", StringComparison.OrdinalIgnoreCase));
    return hasMarker || hasSection
      ? new(true, this.Id, hasMarker && hasSection ? 1.0 : 0.85, [])
      : new(false, this.Id, 0, [new(ExecutableDiagnosticCode.NotPackedExecutable, $"{PackerLabel}: no 'PEC2' marker or .pec section found.", true)]);
  }

  public PackedExecutable Parse(ReadOnlySpan<byte> image, DetectionResult detection) {
    var info = ExecutableContainerParsers.ParseBestEffort(image);
    return new(
      this.Id,
      image.ToArray(),
      detection,
      info,
      this.Capabilities,
      new Dictionary<string, string> {
        ["packer"] = PackerLabel,
        ["container"] = info.Container.ToString(),
        ["architecture"] = info.Architecture.ToString(),
      });
  }

  public UnpackResult Unpack(PackedExecutable packed, UnpackOptions options) {
    var image = packed.OriginalImage;
    var diagnostics = new List<ExecutableDiagnostic>();
    var artifacts = new List<UnpackArtifact> {
      new("metadata.json", BuildMetadataJson(packed), "stored"),
      new("original_packed.bin", image, "stored"),
    };
    var caps = ExecutableUnpackCapabilities.CanDetect | ExecutableUnpackCapabilities.SupportsPe;
    if (packed.ImageInfo?.Architecture == CpuArchitecture.X86)
      caps |= ExecutableUnpackCapabilities.SupportsX86;

    if (image.LongLength > options.MaximumInputSize) {
      diagnostics.Add(new(ExecutableDiagnosticCode.PayloadNotFound, "Input exceeds configured executable unpacking size limit.", true));
      return Finish(this.Id, packed, ExecutableUnpackLevel.DetectionOnly, caps, artifacts, diagnostics);
    }

    var located = PeCompactFormat.Locate(image, packed.ImageInfo, out var failure);
    if (located is null) {
      diagnostics.Add(new(ExecutableDiagnosticCode.UnsupportedPackerVersion, $"{PackerLabel} detected but {failure}.", true));
      return Finish(this.Id, packed, ExecutableUnpackLevel.DetectionOnly, caps, artifacts, diagnostics);
    }

    artifacts.Add(new("compressed_payload.bin", located.CompressedStream, "lzma"));
    caps |= ExecutableUnpackCapabilities.CanLocatePayload;

    var payload = PeCompactFormat.Decompress(located, options.MaximumDecompressedSize, out failure);
    if (payload is null) {
      diagnostics.Add(new(ExecutableDiagnosticCode.DecompressionFailed, $"{PackerLabel} payload located but {failure}.", true));
      return Finish(this.Id, packed, ExecutableUnpackLevel.PayloadLocated, caps, artifacts, diagnostics);
    }

    artifacts.Add(new("decompressed_payload.bin", payload, "lzma"));
    caps |= ExecutableUnpackCapabilities.CanDecompressPayload;

    var memory = PeCompactFormat.BuildMemoryImage(image, located, payload, diagnostics);
    if (memory is not null) {
      artifacts.Add(new("memory_image.bin", memory, "stored"));
      foreach (var section in located.Table.Sections)
        artifacts.Add(new($"sections/section@0x{section.Rva:X}.bin", memory.AsSpan((int)section.Rva, (int)section.Size).ToArray(), "stored"));
    }

    diagnostics.Add(new(ExecutableDiagnosticCode.RunnableRebuildNotGuaranteed,
      $"{PackerLabel} payload decompressed: {located.Table.Sections.Count} section(s) laid out at their RVAs" +
      (located.Table.Filters.Count > 0 ? $", x86 call/jump filter reversed over {located.Table.Filters.Count} region(s)" : "") +
      ". PECompact rebuilds the import tables, keeps part of the resources uncompressed in its own section and " +
      "reprocesses relocations, so those areas are not the original bytes."));
    return Finish(this.Id, packed, ExecutableUnpackLevel.PayloadDecompressed, caps, artifacts, diagnostics);
  }

  private static UnpackResult Finish(
    string id,
    PackedExecutable packed,
    ExecutableUnpackLevel level,
    ExecutableUnpackCapabilities caps,
    List<UnpackArtifact> artifacts,
    List<ExecutableDiagnostic> diagnostics) {
    var result = new UnpackResult(level, caps, artifacts, diagnostics);
    artifacts.Add(new("diagnostics.json", ExecutableDiagnosticsJson.Build(id, packed.ImageInfo, result), "stored"));
    return result with { Artifacts = artifacts };
  }

  private static byte[] BuildMetadataJson(PackedExecutable packed) {
    var sb = new StringBuilder();
    sb.Append("{\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"packer\": \"{packed.PackerId}\",\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"container\": \"{(packed.ImageInfo?.Container.ToString() ?? "unknown").ToLowerInvariant()}\",\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"architecture\": \"{(packed.ImageInfo?.Architecture.ToString() ?? "unknown").ToLowerInvariant()}\",\n");
    sb.Append("  \"compressionCore\": \"lzma\",\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"imageSize\": {packed.OriginalImage.LongLength}\n");
    sb.Append("}\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}

/// <summary>
/// The PECompact 2 LZMA container: the entry sequence, the aPLib-compressed data block
/// with the section table, the payload stream and the call/jump filter. Split out from
/// the handler so each step can be exercised directly.
/// </summary>
internal static class PeCompactFormat {
  /// <summary>The fixed bytes between the entry's <c>B8 imm32</c> and the marker string.</summary>
  private static ReadOnlySpan<byte> EntryBody =>
    [0x50, 0x64, 0xFF, 0x35, 0, 0, 0, 0, 0x64, 0x89, 0x25, 0, 0, 0, 0, 0x33, 0xC0, 0x89, 0x08];

  private static ReadOnlySpan<byte> EntryMarker => "PECompact2\0"u8;

  /// <summary>The parameter block sits this far ahead of the address the entry names.</summary>
  internal const int ParameterBlockDistance = 0x24;

  /// <summary>Size of the payload stream header: u32 size, properties byte, u32 dictionary.</summary>
  internal const int StreamHeaderSize = 9;

  private const uint RecordTag = 0xFFFF0000;
  private const int RecordSize = 0x1C;
  private const int MaximumDataBlockSize = 1 << 20;
  private const int MaximumRecords = 256;

  /// <summary>Record types, the low word of each record's last field.</summary>
  private const ushort SectionRecord = 0x0002;
  private const ushort FilterRecord = 0x0008;
  private const ushort StreamRecord = 0x0010;
  private const ushort ExceptionRecord = 0x0200;

  internal sealed record Section(uint Rva, uint PayloadOffset, uint Size);

  /// <summary>
  /// A region the call/jump filter ran over. <see cref="LastSite"/> is the offset of the
  /// last byte that may hold a converted opcode, so a conversion's operand can reach four
  /// bytes past it.
  /// </summary>
  internal sealed record Filter(uint Rva, uint LastSite, byte CallMarker, byte JumpMarker);

  internal sealed record ExceptionList(uint Rva, byte Opcode, byte Marker);

  internal sealed record Table(
    uint BaseRva,
    uint BaseOffset,
    uint CompressedSize,
    uint UncompressedSize,
    uint EntryRva,
    uint CoveredBytesRva,
    IReadOnlyList<Section> Sections,
    IReadOnlyList<Filter> Filters,
    IReadOnlyList<ExceptionList> Exceptions);

  internal sealed record Located(
    Table Table,
    int StreamOffset,
    byte[] CompressedStream,
    uint ImageSize);

  /// <summary>
  /// Follows the entry sequence to the data block, reads its table and restores the
  /// stream bytes the entry sequence covers. Returns <see langword="null"/> with the
  /// reason when any step does not match the known layout.
  /// </summary>
  internal static Located? Locate(byte[] image, ExecutableImageInfo? info, out string failure) {
    failure = "the image is not a PE";
    if (info is not { Container: ExecutableContainerKind.Pe })
      return null;

    var entryOffset = RvaToOffset(info, info.EntryPoint, 1);
    if (entryOffset < 0 || entryOffset + 5 + EntryBody.Length + EntryMarker.Length > image.Length) {
      failure = "the entry point has no file bytes";
      return null;
    }

    var entry = image.AsSpan(entryOffset);
    if (entry[0] != 0xB8 || !entry.Slice(5, EntryBody.Length).SequenceEqual(EntryBody)) {
      failure = "the entry point does not carry the PECompact 2 entry sequence";
      return null;
    }

    var stubLength = 5 + EntryBody.Length + EntryMarker.Length;
    if (!entry.Slice(5 + EntryBody.Length, EntryMarker.Length).SequenceEqual(EntryMarker)) {
      failure = "the entry sequence does not end in the \"PECompact2\" marker";
      return null;
    }

    var named = BinaryPrimitives.ReadUInt32LittleEndian(entry[1..]);
    var parameterOffset = RvaToOffset(info, unchecked((ulong)named - info.PreferredBaseAddress), 4) - ParameterBlockDistance;
    if (parameterOffset < 0 || parameterOffset + 8 > image.Length) {
      failure = "the address named by the entry sequence lies outside the file";
      return null;
    }

    var dataRva = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(parameterOffset));
    var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(parameterOffset + 4));
    var dataOffset = RvaToOffset(info, dataRva, 1);
    if (dataOffset < 0 || dataSize == 0 || dataSize > MaximumDataBlockSize) {
      failure = "the parameter block does not point at a data block";
      return null;
    }

    byte[] block;
    try {
      // One byte of headroom: the decoder stops at its output bound before it reads the
      // end marker, and a stream that ends exactly at the recorded size must still show it.
      block = AplibBuildingBlock.DecompressRaw(image.AsSpan(dataOffset), (int)dataSize + 1, out var ended, out _);
      if (!ended || block.Length != dataSize) {
        failure = "the data block is not an aPLib stream of the recorded size";
        return null;
      }
    } catch (InvalidDataException) {
      failure = "the data block is not an aPLib stream";
      return null;
    }

    var table = ReadTable(block, out failure);
    if (table is null)
      return null;
    if (table.EntryRva != info.EntryPoint) {
      failure = $"the data block records the entry sequence at RVA 0x{table.EntryRva:X}, not at the entry point";
      return null;
    }

    var streamOffset = RvaToOffset(info, table.BaseRva, StreamHeaderSize);
    var coveredOffset = RvaToOffset(info, table.CoveredBytesRva, stubLength);
    if (streamOffset < 0 || coveredOffset < 0 || entryOffset < streamOffset) {
      failure = "the payload stream or the bytes the entry sequence covers lie outside the file";
      return null;
    }

    // The stream as the packer wrote it, before the entry sequence went over it.
    var streamEnd = (int)Math.Min(image.LongLength, (long)streamOffset + StreamHeaderSize + table.CompressedSize + 64);
    var stream = image.AsSpan(streamOffset, streamEnd - streamOffset).ToArray();
    var patchAt = entryOffset - streamOffset;
    if (patchAt + stubLength > stream.Length) {
      failure = "the entry sequence lies past the end of the payload stream";
      return null;
    }

    image.AsSpan(coveredOffset, stubLength).CopyTo(stream.AsSpan(patchAt));
    var imageSize = (uint)info.Regions.Select(r => r.VirtualAddress + Math.Max(r.VirtualSize, r.FileSize)).DefaultIfEmpty(0UL).Max();
    return new(table, streamOffset, stream, imageSize);
  }

  /// <summary>
  /// Reads the table out of the inflated data block. The table is a run of seven-dword
  /// records <c>{ tag, 0xFFFF0000, f0, f1, f2, f3, type:u16 extra:u8 extra:u8 }</c> (the
  /// first record of a group has zeros in place of the tag pair):
  /// <list type="bullet">
  /// <item>sections, type 2: <c>{ rva, baseOffset + payloadOffset, size, 0 }</c>, highest RVA first;</item>
  /// <item>the stream, type 0x10: <c>{ baseRva, baseOffset, compressedSize, uncompressedSize }</c>;</item>
  /// <item>filtered regions, type 8: <c>{ rva, rva, lastSite, 0 }</c>, extras = call and jump marker;</item>
  /// <item>exception lists, type 0x200: <c>{ listRva, 0, 0, 0 }</c>, extras = opcode and marker.</item>
  /// </list>
  /// The two dwords ahead of the first section record are the entry RVA and the RVA of the
  /// bytes the entry sequence covers. The stream record anchors the search.
  /// </summary>
  internal static Table? ReadTable(ReadOnlySpan<byte> block, out string failure) {
    failure = "the data block holds no section table";
    for (var header = 0; header + RecordSize <= block.Length; ++header) {
      if (U32(block, header + 4) != RecordTag || U32(block, header + 0x18) != StreamRecord ||
          U32(block, header + 8) != U32(block, header + 12) || U32(block, header + 0x14) == 0)
        continue;

      var sections = new List<Section>();
      var first = header;
      for (var record = header - RecordSize; record >= 0 && sections.Count < MaximumRecords; record -= RecordSize) {
        if (U32(block, record + 0x14) != 0 || U32(block, record + 0x18) != SectionRecord)
          break;
        sections.Add(new(U32(block, record + 8), U32(block, record + 12), U32(block, record + 16)));
        first = record;
      }

      if (sections.Count == 0 || first < 8) {
        failure = "the section table has no section records";
        return null;
      }

      var filters = new List<Filter>();
      var exceptions = new List<ExceptionList>();
      for (var record = header + RecordSize; record + RecordSize <= block.Length && filters.Count + exceptions.Count < MaximumRecords; record += RecordSize) {
        var type = BinaryPrimitives.ReadUInt16LittleEndian(block[(record + 0x18)..]);
        if (type == FilterRecord && U32(block, record + 8) == U32(block, record + 12) && U32(block, record + 0x14) == 0)
          filters.Add(new(U32(block, record + 8), U32(block, record + 16), block[record + 0x1A], block[record + 0x1B]));
        else if (type == ExceptionRecord && U32(block, record + 8) != 0 && (block[record + 0x1A] is 0xE8 or 0xE9))
          exceptions.Add(new(U32(block, record + 8), block[record + 0x1A], block[record + 0x1B]));
        else
          break;
      }

      sections.Sort((a, b) => a.PayloadOffset.CompareTo(b.PayloadOffset));
      failure = "";
      return new(
        BaseRva: U32(block, header + 8),
        BaseOffset: U32(block, header + 12),
        CompressedSize: U32(block, header + 0x10),
        UncompressedSize: U32(block, header + 0x14),
        EntryRva: U32(block, first - 8),
        CoveredBytesRva: U32(block, first - 4),
        Sections: sections,
        Filters: filters,
        Exceptions: exceptions);
    }

    return null;
  }

  /// <summary>Inflates the payload stream; <see langword="null"/> when it does not decode to the recorded size.</summary>
  internal static byte[]? Decompress(Located located, long maximumDecompressedSize, out string failure) {
    var stream = located.CompressedStream;
    var size = BinaryPrimitives.ReadUInt32LittleEndian(stream);
    if (size != located.Table.UncompressedSize) {
      failure = $"the stream header records 0x{size:X} bytes where the section table records 0x{located.Table.UncompressedSize:X}";
      return null;
    }
    if (size > maximumDecompressedSize || size > int.MaxValue) {
      failure = $"the payload's 0x{size:X} bytes exceed the configured decompressed-size limit";
      return null;
    }

    try {
      using var input = new MemoryStream(stream, StreamHeaderSize, stream.Length - StreamHeaderSize, writable: false);
      var data = new LzmaDecoder(input, stream.AsSpan(4, 5).ToArray(), size).Decode();
      if (data.Length == size) {
        failure = "";
        return data;
      }
      failure = $"the LZMA stream ended after 0x{data.Length:X} of 0x{size:X} bytes";
    } catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException or ArgumentException or EndOfStreamException or IndexOutOfRangeException) {
      failure = $"the LZMA stream does not decode ({ex.Message})";
    }

    return null;
  }

  /// <summary>
  /// Lays the payload out at the section RVAs, reverses the call/jump filter over the
  /// code region and applies the exception lists. Returns <see langword="null"/> when the
  /// table describes something the image cannot hold.
  /// </summary>
  internal static byte[]? BuildMemoryImage(byte[] image, Located located, byte[] payload, List<ExecutableDiagnostic> diagnostics) {
    var table = located.Table;
    var memory = new byte[located.ImageSize];

    var firstLength = (long)table.Sections[0].PayloadOffset - table.BaseOffset;
    if (!Place(memory, payload, table.BaseRva, 0, firstLength))
      return Fail(diagnostics, "the leading payload chunk does not fit the image");
    foreach (var section in table.Sections)
      if (!Place(memory, payload, section.Rva, (long)section.PayloadOffset - table.BaseOffset, section.Size))
        return Fail(diagnostics, $"the section record for RVA 0x{section.Rva:X} does not fit the payload or the image");

    foreach (var filter in table.Filters) {
      // Opcodes may sit at every offset up to LastSite inclusive, their operands four bytes beyond.
      if ((ulong)filter.Rva + filter.LastSite + 5 > (ulong)memory.Length)
        return Fail(diagnostics, $"the filtered region at RVA 0x{filter.Rva:X} lies outside the image");
      ReverseCallFilter(memory.AsSpan((int)filter.Rva, (int)filter.LastSite + 5), filter.CallMarker, filter.JumpMarker);
    }

    var info = ExecutableContainerParsers.ParseBestEffort(image);
    foreach (var list in table.Exceptions) {
      var offset = RvaToOffset(info, list.Rva, 4);
      if (offset < 0)
        return Fail(diagnostics, $"the exception list at RVA 0x{list.Rva:X} lies outside the file");
      for (; offset + 4 <= image.Length; offset += 4) {
        var rva = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(offset));
        if (rva == 0)
          break;
        if (rva + 2UL > (ulong)memory.Length)
          return Fail(diagnostics, $"an exception entry names RVA 0x{rva:X}, outside the image");
        memory[rva] = list.Opcode;
        memory[rva + 1] = list.Marker;
      }
    }

    return memory;
  }

  /// <summary>
  /// Turns the filter's form back into relative operands. A converted <c>E8</c> or
  /// <c>E9</c> is followed by its marker byte and the target's 24-bit big-endian offset
  /// within the region; the original operand is that offset minus the opcode's own
  /// offset. Converted sites are skipped whole.
  /// </summary>
  internal static void ReverseCallFilter(Span<byte> code, byte callMarker, byte jumpMarker) {
    for (var i = 0; i + 5 <= code.Length; ) {
      var opcode = code[i];
      if (!((opcode == 0xE8 && code[i + 1] == callMarker) || (opcode == 0xE9 && code[i + 1] == jumpMarker))) {
        ++i;
        continue;
      }

      var target = (uint)((code[i + 2] << 16) | (code[i + 3] << 8) | code[i + 4]);
      BinaryPrimitives.WriteUInt32LittleEndian(code[(i + 1)..], unchecked(target - (uint)i));
      i += 5;
    }
  }

  private static bool Place(byte[] memory, byte[] payload, uint rva, long payloadOffset, long length) {
    if (length < 0 || payloadOffset < 0 || payloadOffset + length > payload.Length || rva + length > memory.Length)
      return false;
    payload.AsSpan((int)payloadOffset, (int)length).CopyTo(memory.AsSpan((int)rva));
    return true;
  }

  private static byte[]? Fail(List<ExecutableDiagnostic> diagnostics, string message) {
    diagnostics.Add(new(ExecutableDiagnosticCode.MemoryImageBuildFailed, $"PECompact payload decompressed but {message}; no memory image is emitted."));
    return null;
  }

  private static uint U32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);

  /// <summary>Maps an RVA to a file offset with at least <paramref name="length"/> file bytes behind it, or -1.</summary>
  private static int RvaToOffset(ExecutableImageInfo info, ulong rva, int length) {
    foreach (var region in info.Regions) {
      if (rva < region.VirtualAddress || rva - region.VirtualAddress + (ulong)length > region.FileSize)
        continue;
      var offset = region.FileOffset + (rva - region.VirtualAddress);
      return offset <= int.MaxValue ? (int)offset : -1;
    }

    return -1;
  }
}
