#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.ExePackers;

/// <summary>
/// Static unpacker for Silent_Packer ELF64 XOR section-insertion outputs.
/// This path reverses the loader metadata embedded in the added .dec section:
/// XOR key, encrypted .text virtual address, encrypted .text size, loader
/// virtual address and the relative jump back to the original entry point.
/// </summary>
public sealed class SilentPackerFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "SilentPacker";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Silent_Packer ELF XOR wrapper";
  /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".elf";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [];
  /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [new("xor", "XOR"), new("stored", "Stored")];
  /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
public string? TarCompressionFormatId => null;
  /// <summary>
  /// Gets the family.
  /// </summary>
public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>
  /// Gets the description.
  /// </summary>
public string Description =>
    "Silent_Packer ELF64 XOR section-insertion wrapper - statically decrypts .text and restores the original entry point.";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream)
      .Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Data.LongLength, e.CompressedSize,
        e.Method, false, false, null))
      .ToList();

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  internal static List<(string Name, byte[] Data, long CompressedSize, string Method)> BuildArtifacts(byte[] bytes) {
    var info = LocatePayload(bytes);
    if (info == null)
      throw new InvalidDataException("silent_packer: ELF64 XOR section-insertion metadata was not found.");

    var encryptedText = bytes.AsSpan(info.TextFileOffset, info.TextSize).ToArray();
    var decryptedText = Xor64(encryptedText, info.Key);
    var reconstructed = bytes.ToArray();
    decryptedText.CopyTo(reconstructed.AsSpan(info.TextFileOffset));
    BinaryPrimitives.WriteUInt64LittleEndian(reconstructed.AsSpan(0x18), info.OriginalEntryPoint);

    return [
      ("metadata.ini", BuildMetadata(bytes, info), encryptedText.Length, "stored"),
      ("diagnostics.json", BuildDiagnosticsJson(info), encryptedText.Length, "stored"),
      ("original_packed.bin", bytes, bytes.Length, "stored"),
      ("encrypted_text.bin", encryptedText, encryptedText.Length, "xor"),
      ("decrypted_text.bin", decryptedText, encryptedText.Length, "stored"),
      ("reconstructed/reconstructed.elf", reconstructed, encryptedText.Length, "stored"),
    ];
  }

  internal static SilentPackerPayloadInfo? LocatePayload(byte[] bytes) {
    if (!Elf64Image.TryParse(bytes, out var elf))
      return null;

    var loader = elf.Sections.FirstOrDefault(s => string.Equals(s.Name, ".dec", StringComparison.Ordinal));
    if (loader == null || elf.EntryPoint != loader.Address)
      return null;
    if (loader.FileOffset < 0 || loader.FileSize < 40 || loader.FileOffset + loader.FileSize > bytes.Length)
      return null;

    var infoOffset = loader.FileOffset + loader.FileSize - 32;
    var key = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(infoOffset));
    var textAddress = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(infoOffset + 8));
    var textSize64 = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(infoOffset + 16));
    var loaderAddress = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(infoOffset + 24));
    if (key == 0 || textSize64 == 0 || textSize64 > int.MaxValue || loaderAddress != loader.Address)
      return null;

    var text = elf.Sections.FirstOrDefault(s => s.Address == textAddress && s.FileSize >= (long)textSize64);
    if (text == null || text.FileOffset < 0 || text.FileOffset + (long)textSize64 > bytes.Length)
      return null;

    var jumpOffset = loader.FileOffset + loader.FileSize - 36;
    var relJump = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(jumpOffset));
    var originalEntry = (ulong)((long)loader.Address + loader.FileSize - 32 + relJump);
    if (originalEntry == 0 || originalEntry == elf.EntryPoint)
      return null;

    return new(
      key,
      textAddress,
      text.FileOffset,
      checked((int)textSize64),
      loader.Address,
      loader.FileOffset,
      loader.FileSize,
      elf.EntryPoint,
      originalEntry
    );
  }

  private static List<(string Name, byte[] Data, long CompressedSize, string Method)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return BuildArtifacts(ms.ToArray());
  }

  private static byte[] Xor64(ReadOnlySpan<byte> data, ulong key) {
    var result = data.ToArray();
    var rolling = key;
    for (var i = 0; i < result.Length; i++) {
      result[i] ^= (byte)rolling;
      rolling = (rolling >> 8) | (rolling << 56);
    }
    return result;
  }

  private static byte[] BuildMetadata(byte[] image, SilentPackerPayloadInfo info) {
    var sb = new StringBuilder();
    sb.AppendLine("[silent_packer]");
    sb.Append(CultureInfo.InvariantCulture, $"image_size = {image.Length}\n");
    sb.AppendLine("container = ELF64");
    sb.AppendLine("cipher = xor64");
    sb.AppendLine("method = section_insertion");
    sb.Append(CultureInfo.InvariantCulture, $"text_address = 0x{info.TextAddress:X}\n");
    sb.Append(CultureInfo.InvariantCulture, $"text_offset = 0x{info.TextFileOffset:X}\n");
    sb.Append(CultureInfo.InvariantCulture, $"text_size = {info.TextSize}\n");
    sb.Append(CultureInfo.InvariantCulture, $"loader_address = 0x{info.LoaderAddress:X}\n");
    sb.Append(CultureInfo.InvariantCulture, $"loader_offset = 0x{info.LoaderFileOffset:X}\n");
    sb.Append(CultureInfo.InvariantCulture, $"packed_entry_point = 0x{info.PackedEntryPoint:X}\n");
    sb.Append(CultureInfo.InvariantCulture, $"original_entry_point = 0x{info.OriginalEntryPoint:X}\n");
    sb.AppendLine("capability_level = RebuiltExecutable");
    sb.AppendLine("note = Static XOR reversal restores encrypted .text and the original ELF entry point; the loader section is retained as inert extra data.");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static byte[] BuildDiagnosticsJson(SilentPackerPayloadInfo info) =>
    Encoding.UTF8.GetBytes(
      $$"""
      {
        "packer": "silent_packer",
        "container": "elf64",
        "architecture": "x86_64",
        "capabilityLevel": "RebuiltExecutable",
        "canRebuildExecutable": true,
        "cipher": "xor64",
        "method": "section_insertion",
        "textAddress": "{{info.TextAddress}}",
        "textOffset": {{info.TextFileOffset}},
        "textSize": {{info.TextSize}},
        "packedEntryPoint": "{{info.PackedEntryPoint}}",
        "originalEntryPoint": "{{info.OriginalEntryPoint}}",
        "warnings": [
          "Only Silent_Packer ELF64 XOR section-insertion output is currently reconstructed; AES, PE, code-cave and Silvio infection variants are not claimed by this unpacker."
        ],
        "outputs": [
          "encrypted_text.bin",
          "decrypted_text.bin",
          "reconstructed/reconstructed.elf",
          "metadata.ini",
          "diagnostics.json"
        ]
      }
      """);

  internal sealed record SilentPackerPayloadInfo(
    ulong Key,
    ulong TextAddress,
    int TextFileOffset,
    int TextSize,
    ulong LoaderAddress,
    int LoaderFileOffset,
    int LoaderFileSize,
    ulong PackedEntryPoint,
    ulong OriginalEntryPoint
  );

  private sealed record ElfSection(string Name, ulong Address, int FileOffset, int FileSize);

  private sealed class Elf64Image {
    public required ulong EntryPoint { get; init; }
    public required IReadOnlyList<ElfSection> Sections { get; init; }

    public static bool TryParse(byte[] bytes, out Elf64Image image) {
      image = null!;
      if (bytes.Length < 0x40 || bytes[0] != 0x7F || bytes[1] != 'E' || bytes[2] != 'L' || bytes[3] != 'F')
        return false;
      if (bytes[4] != 2 || bytes[5] != 1)
        return false;

      var entry = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(0x18));
      var sectionHeaderOffset = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(0x28));
      var sectionHeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(0x3A));
      var sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(0x3C));
      var sectionStringIndex = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(0x3E));
      if (sectionHeaderOffset > int.MaxValue || sectionHeaderSize < 64 ||
          sectionCount == 0 || sectionStringIndex >= sectionCount)
        return false;

      var shoff = (int)sectionHeaderOffset;
      if (shoff < 0 || shoff + sectionCount * sectionHeaderSize > bytes.Length)
        return false;

      var stringHeader = shoff + sectionStringIndex * sectionHeaderSize;
      var stringOffset64 = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(stringHeader + 24));
      var stringSize64 = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(stringHeader + 32));
      if (stringOffset64 > int.MaxValue || stringSize64 > int.MaxValue ||
          stringOffset64 + stringSize64 > (ulong)bytes.Length)
        return false;
      var stringsOffset = (int)stringOffset64;
      var stringsSize = (int)stringSize64;

      var sections = new List<ElfSection>(sectionCount);
      for (var i = 0; i < sectionCount; i++) {
        var s = shoff + i * sectionHeaderSize;
        var nameIndex = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(s));
        var address = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(s + 16));
        var offset64 = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(s + 24));
        var size64 = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(s + 32));
        if (offset64 > int.MaxValue || size64 > int.MaxValue)
          return false;
        sections.Add(new(
          ReadString(bytes, stringsOffset, stringsSize, nameIndex),
          address,
          (int)offset64,
          (int)size64
        ));
      }

      image = new Elf64Image { EntryPoint = entry, Sections = sections };
      return true;
    }

    private static string ReadString(byte[] bytes, int offset, int size, uint index) {
      if (index >= size)
        return "";
      var start = offset + (int)index;
      var end = start;
      while (end < offset + size && bytes[end] != 0)
        end++;
      return Encoding.ASCII.GetString(bytes, start, end - start);
    }
  }
}
