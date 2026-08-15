#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Core.Dictionary.Aplib;
using Compression.Core.ExecutableUnpacking;

namespace FileFormat.ExePackers;

/// <summary>
/// JDPack 1.x (<c>.jdpack</c> section) — a Win32 PE packer that leaves the victim's
/// section table in place and replaces individual section byte ranges with compressed
/// blobs, unpacking each of them back over its own virtual address at start-up.
/// </summary>
/// <remarks>
/// <para>
/// No published specification exists for the container, so the layout below was mapped
/// from the packed samples themselves — the loader stub carries its own depacker, and a
/// stub is by construction a complete description of the format it consumes. Nothing was
/// taken from third-party unpacker sources.
/// </para>
/// <para>Container layout, as the stub implements it:</para>
/// <list type="number">
///   <item>The stub occupies the last section and starts with a fixed prologue
///     (<c>pushal; call $+5; pop ebp; mov edx,ebp; sub ebp,K</c>). <c>K</c> turns every
///     <c>ebp</c>-relative displacement in the stub body into a section offset:
///     <c>offset = displacement + 6 − K</c>.</item>
///   <item>The stub body is stored under a rolling XOR: <c>plain[i] = cipher[i] ^ cipher[i−1]</c>,
///     seeded from a byte stored past the encrypted range. The <c>mov ecx,len / lea esi,start /
///     mov al,seed / …</c> loop at the top of the stub names the range and the seed.</item>
///   <item>The decrypted body holds a blob directory: a <c>uint32</c> count followed by
///     <c>count</c> pairs of <c>(destination RVA, compressed size)</c>, plus a single
///     <c>uint8</c> stream key and the original entry point RVA. Every one of those is
///     addressed <c>ebp</c>-relatively from a distinctive instruction sequence, which is how
///     they are located here.</item>
///   <item>Each blob is de-obfuscated by XOR-ing every byte with the stream key, then
///     decompressed as an aPLib stream in the simplified dialect the stub implements
///     (<see cref="AplibDialect.NoLastWasMatch"/>), and written back over its own RVA.</item>
/// </list>
/// <para>
/// Blobs that begin at a section start reproduce that section's original on-disk bytes
/// exactly. Blobs that begin mid-<c>.rsrc</c> do not, because JDPack also rewrites the
/// resource directory and moves resource data entries around; the decompressed bytes are
/// the real resource payloads, just no longer at their original file positions. That is
/// why the rebuilt image is offered as a reconstruction and not as the original file.
/// </para>
/// </remarks>
public sealed class JdpackExecutablePackerHandler : MinorExecutablePackerHandlerBase {
  public override string Id => "jdpack";
  public override string DisplayName => "JDPack";
  protected override bool IsPackerSection(string name) => name.Contains("jd", StringComparison.OrdinalIgnoreCase);
  protected override ReadOnlySpan<byte> LiteralSignature => "JDPack"u8;

  public override ExecutableUnpackCapabilities Capabilities =>
    ExecutableUnpackCapabilities.CanDetect |
    ExecutableUnpackCapabilities.CanLocatePayload |
    ExecutableUnpackCapabilities.CanDecompressPayload |
    ExecutableUnpackCapabilities.CanRebuildExecutable |
    ExecutableUnpackCapabilities.SupportsPe |
    ExecutableUnpackCapabilities.SupportsX86;

  public override UnpackResult Unpack(PackedExecutable packed, UnpackOptions options) {
    var image = packed.OriginalImage;
    var artifacts = new List<UnpackArtifact> {
      new("metadata.json", this.BuildMetadataJson(packed), "stored"),
      new("original_packed.bin", image, "stored"),
    };
    var diagnostics = new List<ExecutableDiagnostic>();
    var caps = ExecutableUnpackCapabilities.CanDetect | ExecutableUnpackCapabilities.SupportsPe;
    if (packed.ImageInfo?.Architecture == CpuArchitecture.X64) caps |= ExecutableUnpackCapabilities.SupportsX64;
    else caps |= ExecutableUnpackCapabilities.SupportsX86;

    var level = ExecutableUnpackLevel.DetectionOnly;
    var layout = PeLayout.TryRead(image);
    JdpackStub? stub = null;
    if (layout is not null) {
      // The stub is the only thing that names the compressed ranges, and it is not
      // necessarily in the section the name heuristic would pick, so look in all of them.
      foreach (var s in layout.Sections) {
        stub = JdpackStub.TryParse(image, s);
        if (stub is null) continue;
        layout.StubSectionIndex = s.Index;
        break;
      }
      var payload = layout.Sections.FirstOrDefault(s => s.RawSize > 0 && s.Index == layout.StubSectionIndex)
                 ?? layout.Sections.FirstOrDefault(s => s.RawSize > 0 && this.IsPackerSection(s.Name));
      if (payload is not null) {
        artifacts.Add(new("compressed_payload.bin", Slice(image, payload.RawOffset, payload.RawSize), "stored"));
        level = ExecutableUnpackLevel.PayloadLocated;
        caps |= ExecutableUnpackCapabilities.CanLocatePayload;
      }
    }

    if (layout is null || stub is null) {
      diagnostics.Add(new(ExecutableDiagnosticCode.UnsupportedCompressionMethod,
        "JDPack detected, but no decryptable JDPack loader stub was found, so the directory of " +
        "compressed ranges could not be read and nothing was decompressed.", true));
      return Finish(level, caps, artifacts, diagnostics, packed, this.Id);
    }

    var decoded = new List<(uint Rva, byte[] Data)>();
    var failures = 0;
    foreach (var (rva, compressedSize) in stub.Blobs) {
      var section = layout.FindByRva(rva);
      if (section is null) { ++failures; continue; }
      var start = section.RawOffset + (rva - section.VirtualAddress);
      if (start + compressedSize > (uint)image.Length) { ++failures; continue; }

      var blob = new byte[compressedSize];
      image.AsSpan((int)start, (int)compressedSize).CopyTo(blob);
      for (var i = 0; i < blob.Length; ++i) blob[i] ^= stub.StreamKey;

      try {
        var cap = (int)Math.Min(options.MaximumDecompressedSize, int.MaxValue);
        var data = AplibBuildingBlock.DecompressRaw(blob, cap, AplibDialect.NoLastWasMatch, out var endMarker, out var used);
        // A JDPack blob is sized to the byte by the packer: a decode that stops on the
        // end marker after consuming exactly that many bytes cannot be a coincidence,
        // and anything else means we mis-parsed the directory rather than decompressed.
        if (!endMarker || used != compressedSize) { ++failures; continue; }
        decoded.Add((rva, data));
      } catch (InvalidDataException) {
        ++failures;
      }
    }

    if (decoded.Count == 0) {
      diagnostics.Add(new(ExecutableDiagnosticCode.DecompressionFailed,
        $"JDPack blob directory listed {stub.Blobs.Count} compressed range(s), but none decoded as an aPLib stream.", true));
      return Finish(level, caps, artifacts, diagnostics, packed, this.Id);
    }

    foreach (var (rva, data) in decoded)
      artifacts.Add(new($"decompressed/rva_{rva:x8}.bin", data, "aplib"));
    level = ExecutableUnpackLevel.PayloadDecompressed;
    caps |= ExecutableUnpackCapabilities.CanDecompressPayload;
    if (failures > 0)
      diagnostics.Add(new(ExecutableDiagnosticCode.DecompressionFailed,
        $"{failures} of {stub.Blobs.Count} JDPack blob(s) did not decode; the rest were recovered.", true));

    var memoryImage = BuildMemoryImage(image, layout, decoded, options);
    if (memoryImage is not null) {
      artifacts.Add(new("memory_image.bin", memoryImage, "stored"));
      level = ExecutableUnpackLevel.RuntimeMemoryImage;

      var rebuilt = PeFromMemoryImage(image, layout, memoryImage, stub.OriginalEntryPointRva);
      if (rebuilt is not null) {
        artifacts.Add(new("reconstructed/reconstructed.exe", rebuilt, "stored"));
        level = ExecutableUnpackLevel.RebuiltExecutable;
        caps |= ExecutableUnpackCapabilities.CanRebuildExecutable;
      } else
        diagnostics.Add(new(ExecutableDiagnosticCode.ExecutableRebuildFailed,
          "The decompressed memory image could not be laid back out as a PE file.", true));
    }

    diagnostics.Add(new(ExecutableDiagnosticCode.RunnableRebuildNotGuaranteed,
      $"JDPack stream key 0x{stub.StreamKey:x2}, original entry point RVA 0x{stub.OriginalEntryPointRva:x}. " +
      "JDPack rewrites the resource directory, so the reconstruction restores the packed layout's resource " +
      "placement rather than the original file's; it is not expected to be byte-identical to the input of the packer.",
      false));

    return Finish(level, caps, artifacts, diagnostics, packed, this.Id);
  }

  private static UnpackResult Finish(
    ExecutableUnpackLevel level,
    ExecutableUnpackCapabilities caps,
    List<UnpackArtifact> artifacts,
    List<ExecutableDiagnostic> diagnostics,
    PackedExecutable packed,
    string id) {
    var result = new UnpackResult(level, caps, artifacts, diagnostics);
    artifacts.Add(new("diagnostics.json", ExecutableDiagnosticsJson.Build(id, packed.ImageInfo, result), "stored"));
    return result with { Artifacts = artifacts };
  }

  private static byte[] Slice(byte[] image, uint offset, uint size) {
    if (offset >= (uint)image.Length) return [];
    var length = (int)Math.Min(size, (uint)image.Length - offset);
    return image.AsSpan((int)offset, length).ToArray();
  }

  /// <summary>
  /// Replays what the stub does at run time: map every section's raw bytes at its RVA,
  /// then overlay the decompressed blobs. The result is the image as the original entry
  /// point would have seen it.
  /// </summary>
  private static byte[]? BuildMemoryImage(byte[] image, PeLayout layout, List<(uint Rva, byte[] Data)> decoded, UnpackOptions options) {
    var span = layout.SizeOfImage;
    foreach (var (rva, data) in decoded) span = Math.Max(span, rva + (uint)data.Length);
    foreach (var s in layout.Sections) span = Math.Max(span, s.VirtualAddress + Math.Max(s.VirtualSize, s.RawSize));
    if (span == 0 || span > options.MaximumDecompressedSize || span > int.MaxValue) return null;

    var memory = new byte[span];
    foreach (var s in layout.Sections) {
      if (s.RawSize == 0 || s.RawOffset >= (uint)image.Length) continue;
      var length = (int)Math.Min(s.RawSize, (uint)image.Length - s.RawOffset);
      length = (int)Math.Min((uint)length, span - s.VirtualAddress);
      if (length > 0) image.AsSpan((int)s.RawOffset, length).CopyTo(memory.AsSpan((int)s.VirtualAddress));
    }
    foreach (var (rva, data) in decoded) {
      var length = (int)Math.Min((uint)data.Length, span - rva);
      if (length > 0) data.AsSpan(0, length).CopyTo(memory.AsSpan((int)rva));
    }
    return memory;
  }

  /// <summary>
  /// Writes the decompressed memory image back out as a PE: the packed file's headers with
  /// the loader stub's section dropped, the original entry point restored, and every
  /// remaining section re-serialised from the memory image.
  /// </summary>
  private static byte[]? PeFromMemoryImage(byte[] image, PeLayout layout, byte[] memory, uint originalEntryPointRva) {
    var keep = layout.Sections.Where(s => s.Index != layout.StubSectionIndex).ToList();
    if (keep.Count == 0) return null;

    var fileAlignment = layout.FileAlignment is >= 0x200 and <= 0x10000 ? layout.FileAlignment : 0x200u;
    var headersSize = Align(layout.SectionTableOffset + (uint)(keep.Count * 40), fileAlignment);
    if (headersSize > (uint)image.Length) return null;

    var rawSizes = new uint[keep.Count];
    var rawOffsets = new uint[keep.Count];
    var cursor = headersSize;
    for (var i = 0; i < keep.Count; ++i) {
      var s = keep[i];
      var content = Math.Max(s.VirtualSize, s.RawSize);
      if (s.VirtualAddress + content > (uint)memory.Length) content = (uint)memory.Length - Math.Min(s.VirtualAddress, (uint)memory.Length);
      rawSizes[i] = Align(content, fileAlignment);
      rawOffsets[i] = rawSizes[i] == 0 ? 0 : cursor;
      cursor += rawSizes[i];
      if (cursor > int.MaxValue) return null;
    }

    var output = new byte[cursor];
    image.AsSpan(0, (int)headersSize).CopyTo(output);
    // Beyond the surviving section table the packed headers may still describe the stub;
    // blank the tail so no stale section entry is left behind.
    output.AsSpan((int)(layout.SectionTableOffset + keep.Count * 40), (int)(headersSize - layout.SectionTableOffset - (uint)(keep.Count * 40))).Clear();

    BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(layout.CoffOffset + 2), (ushort)keep.Count);
    BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(layout.OptionalHeaderOffset + 16), originalEntryPointRva);
    var lastSection = keep[^1];
    BinaryPrimitives.WriteUInt32LittleEndian(
      output.AsSpan(layout.OptionalHeaderOffset + 56),
      Align(lastSection.VirtualAddress + Math.Max(lastSection.VirtualSize, 1u), layout.SectionAlignment == 0 ? 0x1000u : layout.SectionAlignment));
    BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(layout.OptionalHeaderOffset + 60), headersSize);
    BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(layout.OptionalHeaderOffset + 64), 0); // checksum is no longer valid

    for (var i = 0; i < keep.Count; ++i) {
      var entry = (int)layout.SectionTableOffset + i * 40;
      var s = keep[i];
      image.AsSpan((int)layout.SectionTableOffset + s.Index * 40, 40).CopyTo(output.AsSpan(entry));
      BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(entry + 16), rawSizes[i]);
      BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(entry + 20), rawOffsets[i]);
      if (rawSizes[i] == 0) continue;
      var available = (int)Math.Min(rawSizes[i], (uint)memory.Length - Math.Min(s.VirtualAddress, (uint)memory.Length));
      if (available > 0) memory.AsSpan((int)s.VirtualAddress, available).CopyTo(output.AsSpan((int)rawOffsets[i]));
    }
    return output;
  }

  private static uint Align(uint value, uint alignment) =>
    alignment == 0 ? value : (value + alignment - 1) / alignment * alignment;

  // ── PE layout ───────────────────────────────────────────────────────────────

  internal sealed record PeSection(int Index, string Name, uint VirtualAddress, uint VirtualSize, uint RawOffset, uint RawSize);

  internal sealed class PeLayout {
    public required int CoffOffset { get; init; }
    public required int OptionalHeaderOffset { get; init; }
    public required uint SectionTableOffset { get; init; }
    public required uint FileAlignment { get; init; }
    public required uint SectionAlignment { get; init; }
    public required uint SizeOfImage { get; init; }
    public required IReadOnlyList<PeSection> Sections { get; init; }
    public int StubSectionIndex { get; set; } = -1;

    public PeSection? FindByRva(uint rva) {
      foreach (var s in this.Sections) {
        var span = Math.Max(s.VirtualSize, s.RawSize);
        if (rva >= s.VirtualAddress && rva < s.VirtualAddress + span) return s;
      }
      return null;
    }

    public static PeLayout? TryRead(byte[] image) {
      if (!PackerScanner.IsPe(image)) return null;
      var eLfanew = (int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(0x3C));
      var coff = eLfanew + 4;
      if (coff + 20 > image.Length) return null;
      var sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(coff + 2));
      var optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(coff + 16));
      var optional = coff + 20;
      var table = (uint)(eLfanew + 24 + optionalSize);
      if (optional + 68 > image.Length || table + (uint)sectionCount * 40 > (uint)image.Length || sectionCount == 0) return null;

      var sections = new List<PeSection>(sectionCount);
      for (var i = 0; i < sectionCount; ++i) {
        var off = (int)table + i * 40;
        var nameSpan = image.AsSpan(off, 8);
        var nul = nameSpan.IndexOf((byte)0);
        var name = Encoding.ASCII.GetString(nul < 0 ? nameSpan : nameSpan[..nul]);
        sections.Add(new(
          i,
          name,
          BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(off + 12)),
          BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(off + 8)),
          BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(off + 20)),
          BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(off + 16))));
      }

      return new() {
        CoffOffset = coff,
        OptionalHeaderOffset = optional,
        SectionTableOffset = table,
        SectionAlignment = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(optional + 32)),
        FileAlignment = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(optional + 36)),
        SizeOfImage = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(optional + 56)),
        Sections = sections,
      };
    }
  }

  // ── Loader stub ─────────────────────────────────────────────────────────────

  internal sealed class JdpackStub {
    public required byte StreamKey { get; init; }
    public required uint OriginalEntryPointRva { get; init; }
    public required IReadOnlyList<(uint Rva, uint CompressedSize)> Blobs { get; init; }

    /// <summary><c>pushal; call $+5; pop ebp; mov edx,ebp; sub ebp,imm32</c>.</summary>
    private static ReadOnlySpan<byte> Prologue => [0x60, 0xE8, 0x00, 0x00, 0x00, 0x00, 0x5D, 0x8B, 0xD5, 0x81, 0xED];

    /// <summary>
    /// <c>mov ecx,len; lea esi,[ebp+start]; mov al,[ebp+seed]; mov bl,[esi]; xor al,bl;
    /// mov [esi],al; mov [ebp+seed],bl; inc esi; loop</c> — the rolling-XOR unwrap of the stub body.
    /// </summary>
    private const string DecryptLoop = "B9 ???????? 8D B5 ???????? 8A 85 ???????? 8A 1E 32 C3 88 06 88 9D ???????? 46 E2 EB";

    /// <summary><c>mov esi,[ebp+count]; mov eax,ebp; push esi; push eax; mov ecx,[eax+count+8]</c>.</summary>
    private const string BlobDirectory = "8B B5 ???????? 8B C5 56 50 8B 88 ????????";

    /// <summary><c>lodsb; xor al,[ebp+key]; stosb; loop</c> — the per-blob stream key.</summary>
    private const string StreamKeyRef = "AC 32 85 ???????? AA E2 F6";

    /// <summary><c>mov eax,[ebp+oep]; add eax,edx; mov [esp+0x1c],eax; popal; push eax; ret</c>.</summary>
    private const string EntryPointRef = "8B 85 ???????? 03 C2 89 44 24 1C 61 50 C3";

    private const int MaximumBlobCount = 64;

    public static JdpackStub? TryParse(byte[] image, PeSection section) {
      if (section.RawSize < 0x40 || section.RawOffset >= (uint)image.Length) return null;
      var length = (int)Math.Min(section.RawSize, (uint)image.Length - section.RawOffset);
      var body = image.AsSpan((int)section.RawOffset, length).ToArray();
      if (!body.AsSpan().StartsWith(Prologue)) return null;

      // Every displacement in the stub is ebp-relative and ebp is (stub base + 6 − K).
      var k = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(Prologue.Length));
      uint ToOffset(uint displacement) => unchecked(displacement + 6 - k);

      if (!TryMatch(body, DecryptLoop, 0, Math.Min(body.Length, 0x200), out var decrypt)) return null;
      var encryptedLength = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(decrypt[0]));
      var encryptedStart = ToOffset(BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(decrypt[1])));
      var seedOffset = ToOffset(BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(decrypt[2])));
      if (encryptedStart >= (uint)body.Length || seedOffset >= (uint)body.Length) return null;
      if (encryptedLength == 0 || encryptedStart + encryptedLength > (uint)body.Length) return null;

      var seed = body[seedOffset];
      for (var i = encryptedStart; i < encryptedStart + encryptedLength; ++i) {
        var cipher = body[i];
        body[i] = (byte)(cipher ^ seed);
        seed = cipher;
      }

      if (!TryMatch(body, BlobDirectory, (int)encryptedStart, (int)(encryptedStart + encryptedLength), out var directory)) return null;
      if (!TryMatch(body, StreamKeyRef, (int)encryptedStart, (int)(encryptedStart + encryptedLength), out var key)) return null;
      if (!TryMatch(body, EntryPointRef, (int)encryptedStart, (int)(encryptedStart + encryptedLength), out var entry)) return null;

      var countOffset = ToOffset(BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(directory[0])));
      var sizeFieldOffset = ToOffset(BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(directory[1])));
      var keyOffset = ToOffset(BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(key[0])));
      var entryOffset = ToOffset(BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(entry[0])));
      // The directory begins one dword past the count; the size field is the second
      // dword of its first record. Cross-checking the two anchors rejects a stray match.
      if (sizeFieldOffset != countOffset + 8) return null;
      if (countOffset + 4 > (uint)body.Length || keyOffset >= (uint)body.Length || entryOffset + 4 > (uint)body.Length) return null;

      var count = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan((int)countOffset));
      if (count == 0 || count > MaximumBlobCount) return null;
      var tableOffset = countOffset + 4;
      if (tableOffset + count * 8 > (uint)body.Length) return null;

      var blobs = new List<(uint, uint)>((int)count);
      for (var i = 0u; i < count; ++i) {
        var record = (int)(tableOffset + i * 8);
        var rva = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(record));
        var size = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(record + 4));
        // The directory is a fixed-capacity array; trailing records are left zeroed and
        // the stub skips them, so a zero record ends the list.
        if (rva == 0 || size == 0) break;
        blobs.Add((rva, size));
      }
      if (blobs.Count == 0) return null;

      return new() {
        StreamKey = body[keyOffset],
        OriginalEntryPointRva = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan((int)entryOffset)),
        Blobs = blobs,
      };
    }

    /// <summary>
    /// Finds <paramref name="pattern"/> — hex bytes, with <c>????????</c> standing for a
    /// captured little-endian dword — and reports the offset of each capture.
    /// </summary>
    private static bool TryMatch(byte[] data, string pattern, int from, int to, out int[] captures) {
      var tokens = pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries);
      var mask = new List<int>(tokens.Length * 2);
      var captureAt = new List<int>();
      foreach (var token in tokens)
        if (token[0] == '?') {
          captureAt.Add(mask.Count);
          for (var i = 0; i < 4; ++i) mask.Add(-1);
        } else
          mask.Add(byte.Parse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture));

      captures = [];
      var last = Math.Min(to, data.Length) - mask.Count;
      for (var start = Math.Max(0, from); start <= last; ++start) {
        var hit = true;
        for (var i = 0; i < mask.Count && hit; ++i)
          if (mask[i] >= 0 && data[start + i] != mask[i]) hit = false;
        if (!hit) continue;
        captures = [.. captureAt.Select(c => start + c)];
        return true;
      }
      return false;
    }
  }
}
