#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Dtb;

/// <summary>
/// WORM writer for the Flattened Device Tree Blob (FDT v17) format. Produces a
/// minimal valid DTB where every input becomes a leaf property on the root node.
/// The root node carries spec-required <c>#address-cells = &lt;2&gt;</c> and
/// <c>#size-cells = &lt;2&gt;</c> properties so the blob round-trips through
/// <c>fdtdump</c> / <c>dtc</c> consumers without warnings.
/// </summary>
/// <remarks>
/// Layout per Devicetree Specification v0.4:
/// <list type="bullet">
///   <item>40-byte BE header: magic, totalsize, off_dt_struct, off_dt_strings,
///         off_mem_rsvmap, version=17, last_comp_version=16, boot_cpuid_phys=0,
///         size_dt_strings, size_dt_struct.</item>
///   <item>Memory reservation block: one terminating <c>{0, 0}</c> 16-byte entry.</item>
///   <item>Structure block: <c>FDT_BEGIN_NODE "" \0 (padding)</c>
///         + per-property <c>FDT_PROP len nameoff data (padding)</c>
///         + <c>FDT_END_NODE</c> + <c>FDT_END</c>.</item>
///   <item>Strings block: NUL-terminated property names.</item>
/// </list>
/// All values are big-endian per the spec. Structure-block tokens and property
/// payloads are 4-byte aligned.
/// </remarks>
public sealed class DtbWriter {

  /// <summary>
  /// Writes a minimal FDT blob to <paramref name="output"/> whose root node
  /// contains one property per input. Each input's archive-name leaf is used as
  /// the property name; the raw bytes become the property value. Names are
  /// deduplicated in the strings block, but each occurrence still gets its own
  /// FDT_PROP record (multiple identical property names on one node are
  /// technically nonconforming, but matching the input list verbatim is the
  /// honest WORM behaviour).
  /// </summary>
  public static void Write(Stream output, IReadOnlyList<(string Name, byte[] Data)> inputs) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);

    // Build the strings block first so we can compute name offsets up front.
    using var strings = new MemoryStream();
    var stringOffsets = new Dictionary<string, uint>(StringComparer.Ordinal);

    uint InternName(string name) {
      if (stringOffsets.TryGetValue(name, out var off)) return off;
      off = (uint)strings.Length;
      stringOffsets[name] = off;
      var bytes = Encoding.ASCII.GetBytes(name);
      strings.Write(bytes, 0, bytes.Length);
      strings.WriteByte(0);
      return off;
    }

    // Pre-intern the spec-required root-node properties so they appear early in
    // the strings block (fdtdump-friendly).
    var addrCellsOff = InternName("#address-cells");
    var sizeCellsOff = InternName("#size-cells");

    // Build the structure block.
    using var structBlk = new MemoryStream();

    void WriteToken(uint token) {
      Span<byte> tk = stackalloc byte[4];
      BinaryPrimitives.WriteUInt32BigEndian(tk, token);
      structBlk.Write(tk);
    }

    void AlignStruct() {
      while ((structBlk.Length & 3) != 0) structBlk.WriteByte(0);
    }

    void WriteProp(uint nameOff, ReadOnlySpan<byte> data) {
      WriteToken(DtbReader.FDT_PROP);
      Span<byte> hdr = stackalloc byte[8];
      BinaryPrimitives.WriteUInt32BigEndian(hdr[..4], (uint)data.Length);
      BinaryPrimitives.WriteUInt32BigEndian(hdr[4..], nameOff);
      structBlk.Write(hdr);
      if (data.Length > 0) structBlk.Write(data);
      AlignStruct();
    }

    // FDT_BEGIN_NODE for root ("" name, NUL-terminated, padded).
    WriteToken(DtbReader.FDT_BEGIN_NODE);
    structBlk.WriteByte(0);
    AlignStruct();

    // Root: #address-cells = <2>, #size-cells = <2> (big-endian u32).
    Span<byte> twoCells = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(twoCells, 2);
    WriteProp(addrCellsOff, twoCells);
    WriteProp(sizeCellsOff, twoCells);

    // One FDT_PROP per input, in order.
    foreach (var (name, data) in inputs) {
      var safe = SanitisePropertyName(name);
      var off = InternName(safe);
      WriteProp(off, data);
    }

    WriteToken(DtbReader.FDT_END_NODE);
    WriteToken(DtbReader.FDT_END);

    // Assemble final blob.
    const int HeaderSize = 40;
    const int MemRsvmapSize = 16; // one terminator {0, 0}
    var structOff = HeaderSize + MemRsvmapSize;
    var structSize = (uint)structBlk.Length;
    var stringsOff = (uint)(structOff + structSize);
    var stringsSize = (uint)strings.Length;
    var totalSize = stringsOff + stringsSize;

    Span<byte> header = stackalloc byte[HeaderSize];
    BinaryPrimitives.WriteUInt32BigEndian(header[0..4], DtbReader.Magic);
    BinaryPrimitives.WriteUInt32BigEndian(header[4..8], totalSize);
    BinaryPrimitives.WriteUInt32BigEndian(header[8..12], (uint)structOff);
    BinaryPrimitives.WriteUInt32BigEndian(header[12..16], stringsOff);
    BinaryPrimitives.WriteUInt32BigEndian(header[16..20], HeaderSize); // off_mem_rsvmap
    BinaryPrimitives.WriteUInt32BigEndian(header[20..24], 17);          // version
    BinaryPrimitives.WriteUInt32BigEndian(header[24..28], 16);          // last_comp_version
    BinaryPrimitives.WriteUInt32BigEndian(header[28..32], 0);           // boot_cpuid_phys
    BinaryPrimitives.WriteUInt32BigEndian(header[32..36], stringsSize);
    BinaryPrimitives.WriteUInt32BigEndian(header[36..40], structSize);

    output.Write(header);
    // 16-byte memory reservation terminator (both 64-bit fields zero).
    Span<byte> rsv = stackalloc byte[MemRsvmapSize];
    output.Write(rsv);
    structBlk.Position = 0;
    structBlk.CopyTo(output);
    strings.Position = 0;
    strings.CopyTo(output);
  }

  /// <summary>
  /// Coerces an input archive name into a property name valid per
  /// devicetree-specification §2.2.4 (ASCII subset of property-name chars).
  /// Reserved chars are replaced with <c>_</c>; the leaf of any path is used.
  /// </summary>
  public static string SanitisePropertyName(string archiveName) {
    var leaf = archiveName;
    var slash = leaf.LastIndexOfAny(['/', '\\']);
    if (slash >= 0) leaf = leaf[(slash + 1)..];
    if (string.IsNullOrEmpty(leaf)) return "_";

    var sb = new StringBuilder(leaf.Length);
    foreach (var c in leaf) {
      var keep =
        c is >= '0' and <= '9'
        || c is >= 'a' and <= 'z'
        || c is >= 'A' and <= 'Z'
        || c is ',' or '.' or '_' or '+' or '?' or '#' or '-';
      sb.Append(keep ? c : '_');
    }
    return sb.Length == 0 ? "_" : sb.ToString();
  }
}
