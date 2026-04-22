#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.ResourceDll;
using static Compression.Registry.FormatHelpers;
using static FileFormat.PeResources.PeResourceTypes;

namespace FileFormat.PeResources;

/// <summary>
/// Read-only archive view of any PE file. Every resource inside the target
/// <c>.dll</c>/<c>.exe</c>/<c>.ocx</c>/<c>.cpl</c>/<c>.sys</c> surfaces as an entry.
/// <c>RT_GROUP_ICON</c>/<c>RT_GROUP_CURSOR</c> entries are reassembled against their
/// child <c>RT_ICON</c>/<c>RT_CURSOR</c> members so extracted <c>.ico</c> / <c>.cur</c>
/// files are standalone on-disk-format files. <c>RT_BITMAP</c> payloads are wrapped with
/// a synthesised <c>BITMAPFILEHEADER</c> so extracted <c>.bmp</c> files open in any
/// image viewer. Other types come out as raw bytes with a type-appropriate extension.
/// </summary>
public sealed class PeResourcesFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "PeResources";
  public string DisplayName => "PE Resources (Windows binary)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".dll";
  public IReadOnlyList<string> Extensions => [".dll", ".exe", ".ocx", ".cpl", ".sys", ".mui", ".mun"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // 'MZ' DOS header — every PE starts with this, but many things do; confidence
    // stays modest since InnoSetup/Nsis/Sfx overlap.
    new([(byte)'M', (byte)'Z'], Confidence: 0.25),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Windows PE file as a resource archive; extracts icons, bitmaps, manifests, " +
    "version info, strings, and custom RT_RCDATA blobs.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var items = Materialize(new ResourceDllReader().ReadAll(stream));
    return items.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Data.Length, e.Data.Length, "Stored", false, false, null
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in Materialize(new ResourceDllReader().ReadAll(stream))) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  private readonly record struct Item(string Name, byte[] Data);

  /// <summary>
  /// Applies type-specific transformations: assembles group-icon/cursor trees,
  /// wraps RT_BITMAP in a file header, suppresses RT_ICON/RT_CURSOR members that
  /// a group resource already consumed, and names everything using the form
  /// <c>DIR/id[_lang]EXT</c>.
  /// </summary>
  private static List<Item> Materialize(List<ResourceDllReader.RawResource> raw) {
    var iconsById = raw.Where(r => r.TypeId == RtIcon && r.NameString == null)
                       .ToDictionary(r => r.NameId, r => r.Data);
    var cursorsById = raw.Where(r => r.TypeId == RtCursor && r.NameString == null)
                         .ToDictionary(r => r.NameId, r => r.Data);

    // Pre-compute which raw RT_ICON / RT_CURSOR ids will be folded into a group
    // so we can skip them and avoid double-listing the same image bytes.
    var consumedIconIds = new HashSet<ushort>();
    var consumedCursorIds = new HashSet<ushort>();
    foreach (var r in raw) {
      if (r.TypeName != null) continue;
      if (r.TypeId == RtGroupIcon) RecordConsumed(r.Data, consumedIconIds);
      else if (r.TypeId == RtGroupCursor) RecordConsumed(r.Data, consumedCursorIds);
    }

    var items = new List<Item>(raw.Count);
    foreach (var r in raw) {
      if (r.TypeName != null) {
        var (dir0, ext0) = ClassifyString(r.TypeName);
        items.Add(new Item(BuildName(dir0, r, ext0), r.Data));
        continue;
      }

      switch (r.TypeId) {
        case RtIcon when r.NameString == null && consumedIconIds.Contains(r.NameId): continue;
        case RtCursor when r.NameString == null && consumedCursorIds.Contains(r.NameId): continue;
        case RtGroupIcon: {
          var ico = IconAssembler.Assemble(r.Data, iconsById);
          if (ico == null) continue;
          var (dir, ext) = Classify(RtGroupIcon);
          items.Add(new Item(BuildName(dir, r, ext), ico));
          break;
        }
        case RtGroupCursor: {
          var cur = IconAssembler.Assemble(r.Data, cursorsById);
          if (cur == null) continue;
          var (dir, ext) = Classify(RtGroupCursor);
          items.Add(new Item(BuildName(dir, r, ext), cur));
          break;
        }
        case RtBitmap: {
          var (dir, ext) = Classify(RtBitmap);
          items.Add(new Item(BuildName(dir, r, ext), WrapBitmap(r.Data)));
          break;
        }
        default: {
          var (dir, ext) = Classify(r.TypeId);
          items.Add(new Item(BuildName(dir, r, ext), r.Data));
          break;
        }
      }
    }

    return items;
  }

  private static void RecordConsumed(byte[] groupPayload, HashSet<ushort> ids) {
    if (groupPayload.Length < 6) return;
    var count = BinaryPrimitives.ReadUInt16LittleEndian(groupPayload.AsSpan(4));
    const int headerSize = 6;
    const int entrySize = 14;
    if (groupPayload.Length < headerSize + count * entrySize) return;
    for (var i = 0; i < count; i++) {
      var entryOff = headerSize + i * entrySize;
      ids.Add(BinaryPrimitives.ReadUInt16LittleEndian(groupPayload.AsSpan(entryOff + 12)));
    }
  }

  /// <summary>
  /// RT_BITMAP resources omit the 14-byte <c>BITMAPFILEHEADER</c> that real <c>.bmp</c>
  /// files carry; the stored bytes begin at <c>BITMAPINFOHEADER</c>. Prepend a
  /// synthesised file header so extracted files open in standard viewers.
  /// </summary>
  private static byte[] WrapBitmap(byte[] dibBytes) {
    const int fileHeaderSize = 14;
    if (dibBytes.Length < 4) return dibBytes;
    // DIB header size is at byte 0-3 of BITMAPINFOHEADER; palette follows; pixel
    // data offset needs to account for both.
    var dibHeaderSize = BinaryPrimitives.ReadUInt32LittleEndian(dibBytes.AsSpan(0));
    if (dibHeaderSize is < 12 or > 124) return dibBytes;
    var bitCount = dibBytes.Length >= 16 ? BinaryPrimitives.ReadUInt16LittleEndian(dibBytes.AsSpan(14)) : (ushort)0;
    var colorsUsed = dibBytes.Length >= 36 ? (int)BinaryPrimitives.ReadUInt32LittleEndian(dibBytes.AsSpan(32)) : 0;
    var paletteEntries = colorsUsed != 0 ? colorsUsed : (bitCount <= 8 ? 1 << bitCount : 0);
    var bytesPerPaletteEntry = dibHeaderSize >= 40 ? 4 : 3;
    var offBits = fileHeaderSize + (int)dibHeaderSize + paletteEntries * bytesPerPaletteEntry;

    var result = new byte[fileHeaderSize + dibBytes.Length];
    result[0] = (byte)'B';
    result[1] = (byte)'M';
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(2), (uint)result.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(10), (uint)offBits);
    dibBytes.CopyTo(result, fileHeaderSize);
    return result;
  }

  private static string BuildName(string dir, ResourceDllReader.RawResource r, string ext) {
    var baseName = r.NameString ?? r.NameId.ToString();
    var langSuffix = r.LanguageId == 0 ? "" : $"_lang{r.LanguageId}";
    return $"{dir}/{SanitiseName(baseName)}{langSuffix}{ext}";
  }

  private static (string Dir, string Ext) ClassifyString(string typeName) {
    var safe = SanitiseName(typeName);
    return ($"TYPE_{safe}", ".bin");
  }

  private static string SanitiseName(string s) {
    var sb = new StringBuilder(s.Length);
    foreach (var c in s)
      sb.Append(char.IsLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '_');
    return sb.ToString();
  }
}
