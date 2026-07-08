#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Linq;
using System.Text;
using Compression.Core.Deflate;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.ExePackers;

/// <summary>
/// Static unpacker for Origami .NET assembly wrappers. Origami stores a raw
/// Deflate payload XORed with the managed entry point method name, then patches
/// the loader IL with the payload pointer and payload length.
/// </summary>
public sealed class OrigamiFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Origami";
  public string DisplayName => "Origami .NET executable wrapper";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".exe";
  public IReadOnlyList<string> Extensions => [];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("deflate-xor", "XOR + raw Deflate"), new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Origami .NET executable wrapper - statically extracts the XORed raw-Deflate payload and reconstructed original assembly.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream)
      .Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Data.LongLength, e.CompressedSize,
        e.Method, false, false, null))
      .ToList();

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  internal static List<(string Name, byte[] Data, long CompressedSize, string Method)> BuildArtifacts(byte[] bytes) {
    var payload = LocatePayload(bytes);
    if (payload == null)
      throw new InvalidDataException("origami: managed Origami payload metadata was not found.");

    var encrypted = bytes[payload.PayloadFileOffset..(payload.PayloadFileOffset + payload.PayloadSize)];
    var compressed = Xor(encrypted, payload.Key);
    var reconstructed = DeflateDecompressor.Decompress(compressed);

    return [
      ("metadata.ini", BuildMetadata(bytes, payload, reconstructed.Length), encrypted.Length, "stored"),
      ("diagnostics.json", BuildDiagnosticsJson(payload, reconstructed.Length), encrypted.Length, "stored"),
      ("original_packed.bin", bytes, bytes.Length, "stored"),
      ("encrypted_payload.bin", encrypted, encrypted.Length, "xor"),
      ("compressed_payload.deflate", compressed, compressed.Length, "deflate"),
      ("reconstructed/original_assembly.bin", reconstructed, compressed.Length, "stored"),
    ];
  }

  internal static OrigamiPayloadInfo? LocatePayload(byte[] bytes) {
    if (!PeNetImage.TryParse(bytes, out var image))
      return null;
    if (!TryReadClrInfo(bytes, image, out var metadataOffset, out var entryPointToken))
      return null;
    if ((entryPointToken & 0xFF000000u) != 0x06000000u)
      return null;

    if (!CliMetadata.TryParse(bytes, metadataOffset, out var metadata))
      return null;

    var row = (int)(entryPointToken & 0x00FFFFFFu);
    if (row <= 0 || row > metadata.Methods.Count)
      return null;

    var method = metadata.Methods[row - 1];
    var key = metadata.GetString(method.NameIndex);
    if (string.IsNullOrEmpty(key) || key.Length < 16)
      return null;

    if (!image.TryRvaToFileOffset(method.Rva, out var methodOffset))
      return null;

    var body = ReadMethodBody(bytes, methodOffset);
    if (body == null)
      return null;

    if (!TryReadPatchedPayloadOperands(body.Value.Code, image, bytes.Length, out var payloadRva, out var payloadSize))
      return null;

    if (!image.TryRvaToFileOffset(payloadRva, out var payloadOffset))
      return null;
    if (payloadSize <= 0 || payloadOffset < 0 || payloadOffset + payloadSize > bytes.Length)
      return null;

    return new(key, payloadRva, payloadOffset, payloadSize, method.Rva, entryPointToken);
  }

  private static List<(string Name, byte[] Data, long CompressedSize, string Method)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return BuildArtifacts(ms.ToArray());
  }

  private static byte[] Xor(ReadOnlySpan<byte> data, string key) {
    var keyBytes = Encoding.UTF8.GetBytes(key);
    if (keyBytes.Length == 0)
      throw new InvalidDataException("origami: empty payload key.");

    var result = data.ToArray();
    for (var i = 0; i < result.Length; i++)
      result[i] ^= keyBytes[i % keyBytes.Length];
    return result;
  }

  private static bool TryReadClrInfo(byte[] bytes, PeNetImage image, out int metadataOffset, out uint entryPointToken) {
    metadataOffset = 0;
    entryPointToken = 0;
    if (image.Cor20HeaderRva == 0 || !image.TryRvaToFileOffset(image.Cor20HeaderRva, out var cliOffset))
      return false;
    if (cliOffset < 0 || cliOffset + 24 > bytes.Length)
      return false;

    var mdRva = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(cliOffset + 8));
    entryPointToken = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(cliOffset + 20));
    return image.TryRvaToFileOffset(mdRva, out metadataOffset);
  }

  private static MethodBody? ReadMethodBody(byte[] bytes, int offset) {
    if (offset < 0 || offset >= bytes.Length)
      return null;
    var first = bytes[offset];
    if ((first & 0x3) == 0x2) {
      var size = first >> 2;
      if (offset + 1 + size > bytes.Length)
        return null;
      return new(bytes.AsMemory(offset + 1, size));
    }

    if ((first & 0x3) != 0x3 || offset + 12 > bytes.Length)
      return null;
    var flagsAndSize = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
    var headerSize = (flagsAndSize >> 12) * 4;
    if (headerSize < 12 || offset + headerSize > bytes.Length)
      return null;
    var codeSize = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 4));
    if (codeSize < 0 || offset + headerSize + codeSize > bytes.Length)
      return null;
    return new(bytes.AsMemory(offset + headerSize, codeSize));
  }

  private static bool TryReadPatchedPayloadOperands(
    ReadOnlyMemory<byte> code,
    PeNetImage image,
    int imageSize,
    out uint payloadRva,
    out int payloadSize
  ) {
    payloadRva = 0;
    payloadSize = 0;
    var span = code.Span;
    for (var i = 0; i + 8 < span.Length; i++) {
      if (span[i] != 0x21)
        continue;

      var operand = BinaryPrimitives.ReadUInt64LittleEndian(span[(i + 1)..]);
      if (!TryNormalizePayloadRva(operand, image, out var candidateRva))
        continue;

      var size = FindLikelyPayloadSize(span, i + 9, imageSize);
      if (size <= 0)
        continue;

      payloadRva = candidateRva;
      payloadSize = size;
      return true;
    }

    return false;
  }

  private static bool TryNormalizePayloadRva(ulong operand, PeNetImage image, out uint rva) {
    rva = 0;
    if (operand <= uint.MaxValue && image.ContainsRva((uint)operand)) {
      rva = (uint)operand;
      return true;
    }

    if (operand >= image.ImageBase) {
      var relative = operand - image.ImageBase;
      if (relative <= uint.MaxValue && image.ContainsRva((uint)relative)) {
        rva = (uint)relative;
        return true;
      }
    }

    return false;
  }

  private static int FindLikelyPayloadSize(ReadOnlySpan<byte> code, int start, int imageSize) {
    for (var i = Math.Max(0, start); i + 4 < code.Length; i++) {
      if (code[i] != 0x20)
        continue;
      var value = BinaryPrimitives.ReadInt32LittleEndian(code[(i + 1)..]);
      if (value > 8 && value <= imageSize)
        return value;
    }

    return 0;
  }

  private static byte[] BuildMetadata(byte[] image, OrigamiPayloadInfo payload, int reconstructedSize) {
    var sb = new StringBuilder();
    sb.AppendLine("[origami]");
    sb.Append(CultureInfo.InvariantCulture, $"image_size = {image.Length}\n");
    sb.Append(CultureInfo.InvariantCulture, $"entry_point_token = 0x{payload.EntryPointToken:X8}\n");
    sb.Append(CultureInfo.InvariantCulture, $"entry_point_rva = 0x{payload.EntryPointRva:X8}\n");
    sb.Append(CultureInfo.InvariantCulture, $"payload_rva = 0x{payload.PayloadRva:X8}\n");
    sb.Append(CultureInfo.InvariantCulture, $"payload_offset = 0x{payload.PayloadFileOffset:X}\n");
    sb.Append(CultureInfo.InvariantCulture, $"encrypted_size = {payload.PayloadSize}\n");
    sb.Append(CultureInfo.InvariantCulture, $"reconstructed_size = {reconstructedSize}\n");
    sb.AppendLine("compression = raw_deflate");
    sb.AppendLine("transform = xor_entry_point_method_name");
    sb.AppendLine("capability_level = RebuiltExecutable");
    sb.AppendLine("note = Origami loader IL is parsed statically; no input code is executed.");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static byte[] BuildDiagnosticsJson(OrigamiPayloadInfo payload, int reconstructedSize) =>
    Encoding.UTF8.GetBytes(
      $$"""
      {
        "packer": "origami",
        "container": "pe-clr",
        "capabilityLevel": "RebuiltExecutable",
        "canRebuildExecutable": true,
        "entryPointToken": "0x{{payload.EntryPointToken:X8}}",
        "payloadRva": "{{payload.PayloadRva}}",
        "payloadOffset": {{payload.PayloadFileOffset}},
        "encryptedSize": {{payload.PayloadSize}},
        "reconstructedSize": {{reconstructedSize}},
        "warnings": [
          "Origami wraps managed assemblies; reconstructed/original_assembly.bin is the original managed assembly bytes, not a regenerated native PE stub."
        ],
        "outputs": [
          "encrypted_payload.bin",
          "compressed_payload.deflate",
          "reconstructed/original_assembly.bin",
          "metadata.ini",
          "diagnostics.json"
        ]
      }
      """);

  internal sealed record OrigamiPayloadInfo(
    string Key,
    uint PayloadRva,
    int PayloadFileOffset,
    int PayloadSize,
    uint EntryPointRva,
    uint EntryPointToken
  );

  private readonly record struct MethodBody(ReadOnlyMemory<byte> Code);

  private sealed record PeSection(uint VirtualAddress, uint VirtualSize, uint PointerToRawData, uint SizeOfRawData);

  private sealed class PeNetImage {
    public required ulong ImageBase { get; init; }
    public required uint Cor20HeaderRva { get; init; }
    public required IReadOnlyList<PeSection> Sections { get; init; }

    public static bool TryParse(byte[] bytes, out PeNetImage image) {
      image = null!;
      if (bytes.Length < 0x100 || bytes[0] != 'M' || bytes[1] != 'Z')
        return false;

      var peOffset = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0x3C));
      if (peOffset < 0 || peOffset + 0x18 > bytes.Length)
        return false;
      if (!bytes.AsSpan(peOffset, 4).SequenceEqual("PE\0\0"u8))
        return false;

      var coff = peOffset + 4;
      var sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(coff + 2));
      var optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(coff + 16));
      var optional = coff + 20;
      if (optionalSize < 0x60 || optional + optionalSize > bytes.Length)
        return false;

      var magic = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(optional));
      var isPe32Plus = magic == 0x20B;
      if (!isPe32Plus && magic != 0x10B)
        return false;

      var imageBase = isPe32Plus
        ? BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(optional + 24))
        : BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(optional + 28));
      var dataDirectoryOffset = optional + (isPe32Plus ? 112 : 96);
      if (dataDirectoryOffset + 15 * 8 > optional + optionalSize)
        return false;

      var cor20Rva = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(dataDirectoryOffset + 14 * 8));
      if (cor20Rva == 0)
        return false;

      var sectionOffset = optional + optionalSize;
      if (sectionOffset + sectionCount * 40 > bytes.Length)
        return false;

      var sections = new List<PeSection>(sectionCount);
      for (var i = 0; i < sectionCount; i++) {
        var s = sectionOffset + i * 40;
        sections.Add(new(
          BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(s + 12)),
          BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(s + 8)),
          BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(s + 20)),
          BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(s + 16))
        ));
      }

      image = new PeNetImage {
        ImageBase = imageBase,
        Cor20HeaderRva = cor20Rva,
        Sections = sections,
      };
      return true;
    }

    public bool ContainsRva(uint rva) =>
      this.Sections.Any(s => rva >= s.VirtualAddress && rva < s.VirtualAddress + Math.Max(s.VirtualSize, s.SizeOfRawData));

    public bool TryRvaToFileOffset(uint rva, out int fileOffset) {
      foreach (var s in this.Sections) {
        var span = Math.Max(s.VirtualSize, s.SizeOfRawData);
        if (rva < s.VirtualAddress || rva >= s.VirtualAddress + span)
          continue;
        var offset = s.PointerToRawData + (rva - s.VirtualAddress);
        if (offset > int.MaxValue)
          break;
        fileOffset = (int)offset;
        return true;
      }

      fileOffset = 0;
      return false;
    }
  }

  private sealed class CliMetadata {
    private readonly byte[] _bytes;
    private readonly int _stringsOffset;
    private readonly int _stringsSize;

    public required IReadOnlyList<MethodDefRow> Methods { get; init; }

    private CliMetadata(byte[] bytes, int stringsOffset, int stringsSize) {
      this._bytes = bytes;
      this._stringsOffset = stringsOffset;
      this._stringsSize = stringsSize;
    }

    public string GetString(uint index) {
      if (index == 0 || index >= this._stringsSize)
        return "";
      var start = this._stringsOffset + (int)index;
      var end = start;
      while (end < this._stringsOffset + this._stringsSize && this._bytes[end] != 0)
        end++;
      return Encoding.UTF8.GetString(this._bytes, start, end - start);
    }

    public static bool TryParse(byte[] bytes, int metadataOffset, out CliMetadata metadata) {
      metadata = null!;
      if (metadataOffset < 0 || metadataOffset + 20 > bytes.Length)
        return false;
      if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(metadataOffset)) != 0x424A5342)
        return false;

      var versionLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(metadataOffset + 12));
      var pos = Align4(metadataOffset + 16 + versionLength);
      if (pos + 2 > bytes.Length)
        return false;
      pos += 2;
      if (pos + 2 > bytes.Length)
        return false;
      var streamCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(pos));
      pos += 2;

      var tablesOffset = 0;
      var tablesSize = 0;
      var stringsOffset = 0;
      var stringsSize = 0;
      for (var i = 0; i < streamCount; i++) {
        if (pos + 8 > bytes.Length)
          return false;
        var offset = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(pos));
        var size = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(pos + 4));
        pos += 8;
        var nameStart = pos;
        while (pos < bytes.Length && bytes[pos] != 0)
          pos++;
        if (pos >= bytes.Length)
          return false;
        var name = Encoding.ASCII.GetString(bytes, nameStart, pos - nameStart);
        pos = Align4(pos + 1);

        if (offset < 0 || size < 0 || metadataOffset + offset + size > bytes.Length)
          return false;
        if (name is "#~" or "#-") {
          tablesOffset = metadataOffset + offset;
          tablesSize = size;
        } else if (name == "#Strings") {
          stringsOffset = metadataOffset + offset;
          stringsSize = size;
        }
      }

      if (tablesOffset == 0 || stringsOffset == 0)
        return false;

      return TryReadTables(bytes, tablesOffset, tablesSize, stringsOffset, stringsSize, out metadata);
    }

    private static bool TryReadTables(
      byte[] bytes,
      int tablesOffset,
      int tablesSize,
      int stringsOffset,
      int stringsSize,
      out CliMetadata metadata
    ) {
      metadata = null!;
      if (tablesOffset + 24 > bytes.Length)
        return false;

      var heapSizes = bytes[tablesOffset + 6];
      var valid = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(tablesOffset + 8));
      var rowCounts = new uint[64];
      var pos = tablesOffset + 24;
      for (var table = 0; table < 64; table++) {
        if (((valid >> table) & 1) == 0)
          continue;
        if (pos + 4 > tablesOffset + tablesSize)
          return false;
        rowCounts[table] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pos));
        pos += 4;
      }

      var stringsIndexSize = (heapSizes & 0x01) != 0 ? 4 : 2;
      var guidIndexSize = (heapSizes & 0x02) != 0 ? 4 : 2;
      var blobIndexSize = (heapSizes & 0x04) != 0 ? 4 : 2;

      var methodDefOffset = pos;
      for (var table = 0; table < 6; table++) {
        if (rowCounts[table] == 0)
          continue;
        methodDefOffset += checked((int)(rowCounts[table] *
          (uint)TableRowSize(table, rowCounts, stringsIndexSize, guidIndexSize, blobIndexSize)));
      }

      var methodRows = rowCounts[6];
      var methodRowSize = TableRowSize(6, rowCounts, stringsIndexSize, guidIndexSize, blobIndexSize);
      if (methodDefOffset < 0 || methodDefOffset + methodRows * methodRowSize > tablesOffset + tablesSize)
        return false;

      var methods = new List<MethodDefRow>((int)methodRows);
      var cursor = methodDefOffset;
      var paramIndexSize = SimpleIndexSize(rowCounts[8]);
      for (var i = 0; i < methodRows; i++) {
        var rva = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(cursor));
        var nameIndex = ReadIndex(bytes, cursor + 8, stringsIndexSize);
        methods.Add(new(rva, nameIndex));
        cursor += 8 + stringsIndexSize + blobIndexSize + paramIndexSize;
      }

      metadata = new CliMetadata(bytes, stringsOffset, stringsSize) { Methods = methods };
      return true;
    }

    private static int TableRowSize(int table, uint[] rows, int str, int guid, int blob) => table switch {
      0 => 2 + str + guid * 3,
      1 => CodedIndexSize(rows, 2, 0, 26, 35, 1) + str + str,
      2 => 4 + str + str + CodedIndexSize(rows, 2, 2, 1, 27) + SimpleIndexSize(rows[4]) + SimpleIndexSize(rows[6]),
      3 => SimpleIndexSize(rows[4]),
      4 => 2 + str + blob,
      5 => SimpleIndexSize(rows[6]),
      6 => 4 + 2 + 2 + str + blob + SimpleIndexSize(rows[8]),
      _ => throw new InvalidDataException($"origami: unsupported metadata table {table} before MethodDef."),
    };

    private static int SimpleIndexSize(uint rows) => rows < 0x10000 ? 2 : 4;

    private static int CodedIndexSize(uint[] rows, int tagBits, params int[] tables) {
      uint maxRows = 0;
      foreach (var table in tables)
        if (rows[table] > maxRows)
          maxRows = rows[table];
      return maxRows < (1u << (16 - tagBits)) ? 2 : 4;
    }

    private static uint ReadIndex(byte[] bytes, int offset, int size) =>
      size == 2
        ? BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset))
        : BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));

    private static int Align4(int value) => (value + 3) & ~3;
  }

  internal readonly record struct MethodDefRow(uint Rva, uint NameIndex);
}
