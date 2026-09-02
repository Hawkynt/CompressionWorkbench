#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Core.Deflate;
using Compression.Core.ExecutableUnpacking;

namespace FileFormat.ExePackers;

/// <summary>
/// Static unpacker for MoleBox 2.x (Teggo) — a bundler that packs an
/// application, and optionally a tree of data files, into one executable.
/// </summary>
/// <remarks>
/// <para>
/// MoleBox keeps the original image's section table: every original section
/// header survives with its virtual address and virtual size intact, only the
/// name is overwritten with <c>'0' + index</c>, a NUL, and the tail of the
/// original name (<c>.text</c> becomes <c>0\0ext</c>), and its raw data is
/// replaced by a compressed and/or encrypted form. Three loader sections are
/// appended after the originals. Sections whose raw size was already zero are
/// dropped.
/// </para>
/// <para>
/// The recovery chain below was derived by disassembling the loader in the
/// corpus samples; no MoleBox or third-party source was consulted. Everything
/// it needs is in the file, addressed relative to the packed entry point
/// (<c>EP</c>):
/// </para>
/// <list type="number">
///   <item><description>
///     <b>Loader blob.</b> At <c>EP+0x5F</c> sits <c>{u32 uncompressedSize, u32
///     compressedSize, u32 checksum}</c> in the clear. From the next 4-byte
///     boundary to the end of that section the bytes are XORed, dword-wise,
///     with a linear-congruential keystream — <c>seed = seed * 0x19660D +
///     0x3C6EF375</c>, seeded with the virtual address of the header — and the
///     result is an LZSS stream in Okumura's classic parameterisation (4096-byte
///     ring pre-filled with spaces, write pointer at <c>N-F</c>, minimum match
///     3, maximum 18, LSB-first flag bytes, match encoded as 12 bits of ring
///     position plus 4 bits of length). It decodes to a blob that the loader
///     maps back at <c>EP+0x5F</c>.
///   </description></item>
///   <item><description>
///     <b>Configuration.</b> 64 bytes at <c>EP+0x0B</c>, IDEA in ECB mode under
///     the 16-byte key that sits in the clear at <c>EP+0x7C</c> inside the blob.
///     The record holds the original entry point (as a virtual address), the
///     original image base, a bitmask of compressed sections, a bitmask of
///     encrypted sections and the address of the section-key blob.
///   </description></item>
///   <item><description>
///     <b>Section key.</b> 0x80 dwords at the address the configuration names,
///     XORed with the same style of keystream but with the additive constant
///     <c>0x3C6EF35F</c>; the 16-byte section key is at offset 0x20 of the
///     result.
///   </description></item>
///   <item><description>
///     <b>Sections.</b> Per original section, in section-table order: if its
///     encrypted bit is set, IDEA-ECB decrypt (a trailing partial block is left
///     as is); if its compressed bit is set, read <c>{u32 uncompressedSize, u32
///     compressedSize}</c> and inflate the zlib stream that follows. Sections in
///     neither mask are stored verbatim.
///   </description></item>
/// </list>
/// <para>
/// Verified against the 130 MoleBox samples of the chesvectain/PackingData
/// corpus, 104 of which have their pre-pack original available: every one of
/// the 414 recoverable sections comes back byte-identical to the original
/// file's raw data, and the original entry point and image base match in all
/// 103 samples that decode. The 104th, <c>molebox_Snap2HTML.exe</c>, is a
/// broken pack — the three appended section headers overran the first section's
/// raw data — and is reported as a decode failure rather than silently
/// mis-decoded.
/// </para>
/// <para>
/// What cannot be recovered: sections the packer dropped because their raw size
/// was zero (<c>.reloc</c> in 41 of the 104, plus a few <c>BSS</c>/<c>.tls</c>),
/// and the original PE headers, which MoleBox rewrites. The reconstruction is
/// therefore an RVA-correct memory image wrapped in a synthetic PE, with the
/// true entry point reported in <c>metadata.json</c> — not a byte-identical
/// re-serialization of the pre-pack file.
/// </para>
/// <para>
/// The corpus samples carry no bundled file tree: the loader supports one (its
/// box payload is a trailer at the end of the file guarded by magic
/// <c>0xCAFEBABE</c>), but none of the 130 samples has one, and the
/// configuration's payload descriptors are zero throughout. When a trailer is
/// present it is emitted as an artifact for further analysis.
/// </para>
/// </remarks>
public sealed class MoleboxExecutablePackerHandler : MinorExecutablePackerHandlerBase {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public override string Id => "molebox";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public override string DisplayName => "Molebox";

  /// <summary>
  /// Performs the is packer section operation.
  /// </summary>
  protected override bool IsPackerSection(string name) =>
    name.Contains("mole", StringComparison.OrdinalIgnoreCase) ||
    name.Contains("mbx", StringComparison.OrdinalIgnoreCase) ||
    int.TryParse(name, out _);

  /// <summary>
  /// Gets the literal signature.
  /// </summary>
  protected override ReadOnlySpan<byte> LiteralSignature => "Molebox"u8;

  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public override ExecutableUnpackCapabilities Capabilities =>
    ExecutableUnpackCapabilities.CanDetect |
    ExecutableUnpackCapabilities.CanLocatePayload |
    ExecutableUnpackCapabilities.CanDecompressPayload |
    ExecutableUnpackCapabilities.CanBuildMemoryImage |
    ExecutableUnpackCapabilities.CanRebuildExecutable |
    ExecutableUnpackCapabilities.SupportsPe |
    ExecutableUnpackCapabilities.SupportsX86;

  /// <summary>Number of loader sections MoleBox appends after the original ones.</summary>
  private const int LoaderSectionCount = 3;

  private const uint LoaderBlobOffset = 0x5F;
  private const uint ConfigurationOffset = 0x0B;
  private const uint ConfigurationKeyOffset = 0x7C;
  private const int ConfigurationSize = 64;
  private const uint LoaderKeystreamConstant = 0x3C6EF375;
  private const uint SectionKeyKeystreamConstant = 0x3C6EF35F;
  private const uint KeystreamMultiplier = 0x19660D;
  private const int SectionKeyBlobDwords = 0x80;
  private const int SectionKeyOffsetInBlob = 0x20;
  private const uint BoxTrailerMagic = 0xCAFEBABE;
  private const int BoxTrailerSize = 0x14;

  private readonly record struct MoleboxConfiguration(
    uint OriginalEntryPointVa,
    uint OriginalImageBase,
    uint CompressedMask,
    uint EncryptedMask,
    uint SectionKeyBlobVa);

  private readonly record struct RecoveredSection(string Name, ExecutableRegion Region, byte[]? Data, string Method);

  /// <summary>
  /// Performs the unpack operation.
  /// </summary>
  public override UnpackResult Unpack(PackedExecutable packed, UnpackOptions options) =>
    this.TryExtract(packed, options, out var result)
      ? result
      : base.Unpack(packed, options);

  private bool TryExtract(PackedExecutable packed, UnpackOptions options, out UnpackResult result) {
    result = null!;
    var image = packed.OriginalImage;
    if (packed.ImageInfo is not { Container: ExecutableContainerKind.Pe } info)
      return false;
    if (info.Regions.Count <= LoaderSectionCount || info.EntryPoint == 0)
      return false;

    var entryRva = (uint)info.EntryPoint;
    var imageBase = (uint)info.PreferredBaseAddress;
    var diagnostics = new List<ExecutableDiagnostic>();

    byte[] blob;
    uint blobBaseRva;
    MoleboxConfiguration configuration;
    ushort[] sectionSchedule;
    try {
      if (!TryDecodeLoaderBlob(info, entryRva, imageBase, options, out blob, out blobBaseRva))
        return false;
      if (!TryReadConfiguration(image, info, blob, blobBaseRva, entryRva, out configuration))
        return false;
      // The configuration is only believable if it agrees with the packed image
      // about where the image is based; a wrong key decodes to noise instead.
      if (configuration.OriginalImageBase != imageBase)
        return false;
      if (!TryReadSectionKey(blob, blobBaseRva, imageBase, configuration, out var sectionKey))
        return false;
      sectionSchedule = MoleboxIdea.InvertKey(MoleboxIdea.ExpandKey(sectionKey));
    } catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException) {
      return false;
    }

    var originalNames = ReadOriginalSectionNames(image, info, entryRva);
    var recovered = new List<RecoveredSection>();
    var failures = 0;
    for (var index = 0; index < info.Regions.Count - LoaderSectionCount; ++index) {
      var region = info.Regions[index];
      var name = index < originalNames.Count ? originalNames[index] : region.Name;
      if (region.FileBytes is not { Length: > 0 }) {
        recovered.Add(new(name, region, null, "dropped"));
        continue;
      }

      var data = region.FileBytes;
      var method = "stored";
      try {
        if ((configuration.EncryptedMask >> index & 1) != 0) {
          data = MoleboxIdea.ProcessEcb(data, sectionSchedule);
          method = "idea";
        }
        if ((configuration.CompressedMask >> index & 1) != 0) {
          data = Inflate(data, options);
          method = method == "idea" ? "idea+zlib" : "zlib";
        }
      } catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException) {
        diagnostics.Add(new(ExecutableDiagnosticCode.DecompressionFailed,
          $"Molebox: section {index} ('{name}') failed to decode: {ex.Message}", true));
        ++failures;
        recovered.Add(new(name, region, null, "failed"));
        continue;
      }
      recovered.Add(new(name, region, data, method));
    }

    if (recovered.All(s => s.Data is null))
      return false;

    var artifacts = new List<UnpackArtifact> {
      new("metadata.json", this.BuildMoleboxMetadataJson(packed, configuration, recovered), "stored"),
      new("original_packed.bin", image, "stored"),
    };
    foreach (var (index, section) in recovered.Index())
      if (section.Data is { } data)
        artifacts.Add(new($"sections/{index:000}_{Sanitize(section.Name)}.bin", data, section.Method));

    var caps =
      ExecutableUnpackCapabilities.CanDetect |
      ExecutableUnpackCapabilities.CanLocatePayload |
      ExecutableUnpackCapabilities.CanDecompressPayload |
      ExecutableUnpackCapabilities.SupportsPe;
    if (info.Architecture == CpuArchitecture.X86) caps |= ExecutableUnpackCapabilities.SupportsX86;
    var level = ExecutableUnpackLevel.PayloadDecompressed;

    var dropped = recovered.Where(s => s.Method == "dropped" && s.Region.VirtualSize > 0).Select(s => s.Name).ToList();
    if (dropped.Count > 0)
      diagnostics.Add(new(ExecutableDiagnosticCode.RunnableRebuildNotGuaranteed,
        $"Molebox: {string.Join(", ", dropped)} carried no raw data in the packed image — the packer drops such sections, so their original bytes are not recoverable."));
    if (failures > 0)
      diagnostics.Add(new(ExecutableDiagnosticCode.DecompressionFailed,
        $"Molebox: {failures} of {recovered.Count} original sections could not be decoded.", true));

    if (TryReadBoxTrailer(image, out var trailer))
      artifacts.Add(new("box_trailer.bin", trailer, "stored"));
    else
      diagnostics.Add(new(ExecutableDiagnosticCode.PayloadNotFound,
        "Molebox: no bundled-file trailer (magic 0xCAFEBABE) is present — this sample packs an executable only, with no virtual file tree."));

    var patched = info with {
      EntryPoint = configuration.OriginalEntryPointVa - configuration.OriginalImageBase,
      Regions = recovered
        .Select(s => s.Data is null
          ? s.Region with { Name = s.Name, FileSize = 0, FileBytes = null, MemoryBytes = null }
          : s.Region with { Name = s.Name, FileSize = (ulong)s.Data.Length, FileBytes = s.Data, MemoryBytes = s.Data })
        .ToList(),
    };
    var (flatImage, _, buildDiagnostics) = ExecutableMemoryImageBuilder.Build(patched, options: options);
    diagnostics.AddRange(buildDiagnostics);
    if (flatImage != null) {
      artifacts.Add(new("memory_image.bin", flatImage, "stored"));
      level = ExecutableUnpackLevel.RuntimeMemoryImage;
      caps |= ExecutableUnpackCapabilities.CanBuildMemoryImage;

      try {
        artifacts.Add(new("reconstructed/reconstructed.exe", PeRebuilder.RebuildSynthetic(patched, flatImage), "stored"));
        level = ExecutableUnpackLevel.RebuiltExecutable;
        caps |= ExecutableUnpackCapabilities.CanRebuildExecutable;
        diagnostics.Add(new(ExecutableDiagnosticCode.RunnableRebuildNotGuaranteed,
          $"Molebox: the rebuilt PE wraps the recovered RVA-mapped memory image; MoleBox rewrites the original headers, so this is not a " +
          $"byte-identical re-serialization of the pre-pack file. The recovered original entry-point RVA " +
          $"(0x{configuration.OriginalEntryPointVa - configuration.OriginalImageBase:X}) is reported in metadata.json."));
      } catch (Exception ex) when (ex is InvalidDataException or ArgumentException or OverflowException) {
        diagnostics.Add(new(ExecutableDiagnosticCode.ExecutableRebuildFailed, $"Molebox: PE reconstruction failed: {ex.Message}", options.StrictRebuild));
      }
    }

    var built = new UnpackResult(level, caps, artifacts, diagnostics);
    artifacts.Add(new("diagnostics.json", ExecutableDiagnosticsJson.Build(this.Id, packed.ImageInfo, built), "stored"));
    result = built with { Artifacts = artifacts };
    return true;
  }

  /// <summary>
  /// Decodes the loader blob at <c>EP+0x5F</c>: LCG-XOR from the next 4-byte
  /// boundary after its header to the end of the carrying section, then LZSS.
  /// </summary>
  private static bool TryDecodeLoaderBlob(
      ExecutableImageInfo info, uint entryRva, uint imageBase, UnpackOptions options,
      out byte[] blob, out uint blobBaseRva) {
    blob = [];
    blobBaseRva = entryRva + LoaderBlobOffset;

    var region = FindRegion(info, blobBaseRva);
    if (region is null || region.FileBytes is not { Length: > 0 } raw)
      return false;

    var headerOffset = (int)(blobBaseRva - region.VirtualAddress);
    if (headerOffset < 0 || headerOffset + 12 > raw.Length)
      return false;

    var buffer = raw.ToArray();
    // The loader truncates the start of the keystreamed region to a 4-byte
    // boundary, so it can begin inside the last field of the header — which is
    // harmless, since only the two size fields ahead of it are ever read.
    var keystreamStartRva = blobBaseRva + 12 & ~3u;
    var keystreamOffset = (int)(keystreamStartRva - region.VirtualAddress);
    if (keystreamOffset < 0 || keystreamOffset > buffer.Length)
      return false;
    ApplyKeystream(buffer.AsSpan(keystreamOffset), imageBase + blobBaseRva, LoaderKeystreamConstant);

    var uncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(headerOffset, 4));
    var compressedSize = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(headerOffset + 4, 4));
    if (uncompressedSize == 0 || uncompressedSize > options.MaximumDecompressedSize)
      return false;
    if (compressedSize == 0 || headerOffset + 12 + compressedSize > (uint)buffer.Length)
      return false;

    blob = DecodeLzss(buffer.AsSpan(headerOffset + 12, (int)compressedSize), (int)uncompressedSize);
    return blob.Length > 0;
  }

  private static bool TryReadConfiguration(
      byte[] image, ExecutableImageInfo info, byte[] blob, uint blobBaseRva, uint entryRva,
      out MoleboxConfiguration configuration) {
    configuration = default;

    if (!TryGetBlobOffset(blob, blobBaseRva, entryRva + ConfigurationKeyOffset, 16, out var keyOffset))
      return false;
    if (!TryGetFileOffset(info, entryRva + ConfigurationOffset, ConfigurationSize, out var recordOffset) ||
        recordOffset + ConfigurationSize > image.Length)
      return false;

    // The record is stored the other way round from the sections: the packer ran
    // the cipher's decryption over it, so reading it back applies encryption.
    var schedule = MoleboxIdea.ExpandKey(blob.AsSpan(keyOffset, 16));
    var record = MoleboxIdea.ProcessEcb(image.AsSpan(recordOffset, ConfigurationSize), schedule);
    configuration = new(
      BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(0)),
      BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(4)),
      BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(16)),
      BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(20)),
      BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(44)));
    return true;
  }

  private static bool TryReadSectionKey(byte[] blob, uint blobBaseRva, uint imageBase, MoleboxConfiguration configuration, out byte[] key) {
    key = [];
    if (configuration.SectionKeyBlobVa <= imageBase)
      return false;
    var keyBlobRva = configuration.SectionKeyBlobVa - imageBase;
    if (!TryGetBlobOffset(blob, blobBaseRva, keyBlobRva, SectionKeyBlobDwords * 4, out var offset))
      return false;

    ApplyKeystream(blob.AsSpan(offset, SectionKeyBlobDwords * 4), imageBase + keyBlobRva, SectionKeyKeystreamConstant);
    key = blob.AsSpan(offset + SectionKeyOffsetInBlob, 16).ToArray();
    return true;
  }

  /// <summary>
  /// XORs whole dwords with the loader's linear-congruential keystream. The seed
  /// is the virtual address of the region being covered, which is why every
  /// blob needs its own call.
  /// </summary>
  private static void ApplyKeystream(Span<byte> data, uint seed, uint additive) {
    var state = seed;
    for (var offset = 0; offset + 4 <= data.Length; offset += 4) {
      state = state * KeystreamMultiplier + additive;
      var span = data.Slice(offset, 4);
      BinaryPrimitives.WriteUInt32LittleEndian(span, BinaryPrimitives.ReadUInt32LittleEndian(span) ^ state);
    }
  }

  /// <summary>
  /// LZSS in Okumura's published parameterisation: 4096-byte ring buffer
  /// pre-filled with spaces, write pointer starting at <c>N - F</c>, flag bits
  /// consumed least-significant first, a set flag meaning one literal byte and a
  /// clear one meaning <c>{ring position (12 bits), length - 3 (4 bits)}</c>.
  /// </summary>
  private static byte[] DecodeLzss(ReadOnlySpan<byte> source, int uncompressedSize) {
    const int ringSize = 4096;
    const int maximumMatch = 18;
    const int threshold = 2;

    var ring = new byte[ringSize];
    Array.Fill(ring, (byte)' ');
    var write = ringSize - maximumMatch;
    var output = new byte[uncompressedSize];
    var produced = 0;
    var read = 0;
    var flags = 0u;

    while (produced < uncompressedSize) {
      flags >>= 1;
      if ((flags & 0x100) == 0) {
        if (read >= source.Length)
          break;
        flags = (uint)source[read++] | 0xFF00u;
      }

      if ((flags & 1) != 0) {
        if (read >= source.Length)
          break;
        var literal = source[read++];
        output[produced++] = literal;
        ring[write] = literal;
        write = write + 1 & ringSize - 1;
        continue;
      }

      if (read + 1 >= source.Length)
        break;
      var low = source[read++];
      var high = source[read++];
      var position = low | (high & 0xF0) << 4;
      var length = (high & 0x0F) + threshold;
      for (var i = 0; i <= length && produced < uncompressedSize; ++i) {
        var value = ring[position + i & ringSize - 1];
        output[produced++] = value;
        ring[write] = value;
        write = write + 1 & ringSize - 1;
      }
    }

    return produced == uncompressedSize ? output : output.AsSpan(0, produced).ToArray();
  }

  /// <summary>Inflates a section body: <c>{u32 uncompressedSize, u32 compressedSize}</c> then a zlib stream.</summary>
  private static byte[] Inflate(byte[] data, UnpackOptions options) {
    if (data.Length < 10)
      throw new InvalidDataException("compressed section is too small to carry its header");
    var uncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0, 4));
    var compressedSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4, 4));
    if (uncompressedSize == 0 || uncompressedSize > options.MaximumDecompressedSize)
      throw new InvalidDataException($"implausible uncompressed size {uncompressedSize}");
    if (compressedSize < 2 || 8 + (long)compressedSize > data.Length)
      throw new InvalidDataException($"compressed size {compressedSize} runs past the section");

    var cmf = data[8];
    var flg = data[9];
    if ((cmf & 0x0F) != 8 || ((cmf << 8) | flg) % 31 != 0)
      throw new InvalidDataException("section body does not start with a zlib header");

    var inflated = DeflateDecompressor.Decompress(data.AsSpan(10, (int)compressedSize - 2));
    if (inflated.Length < uncompressedSize)
      throw new InvalidDataException($"zlib stream produced {inflated.Length} of {uncompressedSize} bytes");
    return inflated.Length == uncompressedSize ? inflated : inflated.AsSpan(0, (int)uncompressedSize).ToArray();
  }

  /// <summary>
  /// Reads the copy of the original section table the loader keeps just below
  /// the entry point (count at <c>EP-0x3FA</c>, headers at <c>EP-0x3F6</c>) for
  /// the original section names. Purely cosmetic — the recovery does not depend
  /// on it — so a miss is not an error.
  /// </summary>
  private static IReadOnlyList<string> ReadOriginalSectionNames(byte[] image, ExecutableImageInfo info, uint entryRva) {
    const uint countOffset = 0x3FA;
    const uint tableOffset = 0x3F6;
    if (entryRva < countOffset)
      return [];
    if (!TryGetFileOffset(info, entryRva - countOffset, 4, out var countFileOffset) || countFileOffset + 4 > image.Length)
      return [];

    var count = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(countFileOffset, 4));
    if (count == 0 || count > 96)
      return [];
    if (!TryGetFileOffset(info, entryRva - tableOffset, (int)count * 40, out var tableFileOffset) ||
        tableFileOffset + count * 40 > (uint)image.Length)
      return [];

    var names = new List<string>((int)count);
    for (var i = 0; i < count; ++i) {
      var raw = image.AsSpan(tableFileOffset + i * 40, 8);
      var end = raw.IndexOf((byte)0);
      var name = Encoding.ASCII.GetString(end < 0 ? raw : raw[..end]);
      if (name.Length == 0 || name.Any(c => c < 0x20 || c > 0x7E))
        return [];
      names.Add(name);
    }
    return names;
  }

  /// <summary>Locates the bundled-file trailer MoleBox writes at the very end of a boxed executable.</summary>
  private static bool TryReadBoxTrailer(byte[] image, out byte[] trailer) {
    trailer = [];
    if (image.Length < BoxTrailerSize)
      return false;
    var start = image.Length - BoxTrailerSize;
    if (BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(start, 4)) != BoxTrailerMagic)
      return false;
    trailer = image.AsSpan(start, BoxTrailerSize).ToArray();
    return true;
  }

  private static ExecutableRegion? FindRegion(ExecutableImageInfo info, uint rva) {
    foreach (var region in info.Regions) {
      var span = Math.Max(region.VirtualSize, region.FileSize);
      if (rva >= region.VirtualAddress && rva < region.VirtualAddress + span)
        return region;
    }
    return null;
  }

  private static bool TryGetFileOffset(ExecutableImageInfo info, uint rva, int length, out int offset) {
    offset = 0;
    var region = FindRegion(info, rva);
    if (region is null)
      return false;
    var delta = rva - region.VirtualAddress;
    if (delta + (ulong)length > region.FileSize || region.FileOffset + delta > int.MaxValue)
      return false;
    offset = (int)(region.FileOffset + delta);
    return true;
  }

  private static bool TryGetBlobOffset(byte[] blob, uint blobBaseRva, uint rva, int length, out int offset) {
    offset = 0;
    if (rva < blobBaseRva)
      return false;
    var delta = rva - blobBaseRva;
    if (delta + (uint)length > (uint)blob.Length)
      return false;
    offset = (int)delta;
    return true;
  }

  private static string Sanitize(string value) {
    var sb = new StringBuilder(value.Length);
    foreach (var c in value)
      sb.Append(char.IsLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '_');
    return sb.Length == 0 ? "section" : sb.ToString();
  }

  private byte[] BuildMoleboxMetadataJson(PackedExecutable packed, MoleboxConfiguration configuration, IReadOnlyList<RecoveredSection> sections) {
    var sb = new StringBuilder();
    sb.Append("{\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"packer\": \"{this.Id}\",\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"container\": \"{(packed.ImageInfo?.Container.ToString() ?? "unknown").ToLowerInvariant()}\",\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"architecture\": \"{(packed.ImageInfo?.Architecture.ToString() ?? "unknown").ToLowerInvariant()}\",\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"originalImageBase\": {configuration.OriginalImageBase},\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"originalEntryPointRva\": {configuration.OriginalEntryPointVa - configuration.OriginalImageBase},\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"compressedSectionMask\": {configuration.CompressedMask},\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"encryptedSectionMask\": {configuration.EncryptedMask},\n");
    sb.Append("  \"sections\": [\n");
    for (var i = 0; i < sections.Count; ++i) {
      var section = sections[i];
      sb.Append(CultureInfo.InvariantCulture,
        $"    {{ \"index\": {i}, \"name\": \"{section.Name}\", \"virtualAddress\": {section.Region.VirtualAddress}, \"recoveredSize\": {section.Data?.Length ?? 0}, \"method\": \"{section.Method}\" }}");
      sb.Append(i + 1 < sections.Count ? ",\n" : "\n");
    }
    sb.Append("  ],\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"imageSize\": {packed.OriginalImage.LongLength}\n");
    sb.Append("}\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
