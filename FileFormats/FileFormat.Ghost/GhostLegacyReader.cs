#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace FileFormat.Ghost;

/// <summary>
/// Pre-3.0 Ghost dump-file reader (Binary Research Ghost 1.6 / 2.0 / 2.04 era,
/// DOS-only, 1996-1998). Surfaces Stage-1 R/O metadata.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope.</b> Ghost 1.6 / 2.0.4 (DOS) write disk dumps as a sequence of
/// fixed 512-byte header records ("dump heads") of three known types,
/// each followed by a fixed-size body. There is no record framing magic
/// (no 0x012F18D8 record markers — those were introduced with Ghost 3.0)
/// and no compression in pre-3.0 images. The on-disk surface is exactly
/// what the binary's <c>WriteDumpHeader</c> / <c>ReadDumpHeader2</c>
/// emit and consume.
/// </para>
/// <para>
/// <b>Header layout</b> (reverse-engineered from Ghost 1.6 GHOST.EXE
/// binary inspection — see <see cref="GhostLegacyConstants"/> for the
/// reverse-engineered constants):
/// </para>
/// <list type="bullet">
///   <item><description>Bytes 0..1 — <c>FE EF</c> magic (identical to modern Ghost).</description></item>
///   <item><description>Byte 2 — dump head type (1 = disk descriptor / first record, 2 = partition record, 3 = boot record / trailer).</description></item>
///   <item><description>Bytes 3..511 — zero-filled.</description></item>
///   <item><description>After the 512-byte header: a type-specific payload (2048 bytes for type 1, partition data for types 2/3).</description></item>
/// </list>
/// <para>
/// <b>What this Stage-1 reader surfaces.</b>
/// </para>
/// <list type="bullet">
///   <item><description><c>metadata.ini</c> with the head type, image size, parse status, and reference to the binary inspection that established the layout.</description></item>
///   <item><description><c>dump-head.bin</c> — verbatim copy of the 512-byte file header.</description></item>
///   <item><description><c>dump-body.bin</c> — verbatim copy of the rest of the file (the disk image payload).</description></item>
/// </list>
/// <para>
/// <b>What's deliberately NOT done.</b> The pre-3.0 binary itself stores
/// uncompressed FAT directory + data sectors in a Binary-Research-internal
/// layout that requires DOS-specific FAT walk semantics (the binary
/// references the FAT root-dir / cluster chain directly). Without a real
/// Ghost-1.6-produced corpus we cannot validate cluster-level extraction,
/// so we surface the dump body verbatim and leave file listing to the
/// vendor tool (Ghost 1.6 / Ghost Explorer).
/// </para>
/// <para>
/// <b>References.</b> All constants below are taken from the Ghost 1.6
/// GHOST.EXE binary (archive.org item <c>ghost16</c>, MD5
/// <c>64cef43d0eb8d456de990cc95353fa05</c>). The function entry points,
/// magic bytes, and head-type dispatch were located by reverse-engineering
/// the DOS MZ executable's <c>WriteDumpHeader</c> /
/// <c>ReadDumpHeader2</c> code via offset-tracing of the DS-relative
/// string references (DS = 0x1ca4 in the binary).
/// </para>
/// </remarks>
public sealed class GhostLegacyReader {

  private readonly byte[] _data;

  /// <summary>True when the bytes were detected as a pre-3.0 Ghost dump.</summary>
  public bool IsRecognised { get; private set; }

  /// <summary>Head type byte at offset 2 of the file (1, 2, or 3).</summary>
  public byte HeadType { get; private set; }

  /// <summary>First 16 bytes of the file (for diagnostics).</summary>
  public byte[] LeadingBytes { get; private set; } = [];

  /// <summary>Synthesised entries: metadata + raw dump head + raw dump body.</summary>
  public IReadOnlyList<GhostEntry> Entries { get; private set; } = [];

  /// <summary>
  /// Initializes a new instance of <see cref="GhostLegacyReader"/>.
  /// </summary>
  public GhostLegacyReader(ReadOnlySpan<byte> data) {
    this._data = data.ToArray();
    this.Parse();
  }

  private void Parse() {
    if (this._data.Length < 16) {
      this.Entries = [];
      return;
    }

    this.LeadingBytes = this._data.AsSpan(0, Math.Min(16, this._data.Length)).ToArray();
    this.IsRecognised = LooksLikeLegacyHeader(this._data);

    if (!this.IsRecognised) {
      this.Entries = [];
      return;
    }

    this.HeadType = this._data.Length >= 3 ? this._data[2] : (byte)0;

    var entries = new List<GhostEntry>();
    var meta = this.BuildMetadata();
    entries.Add(new GhostEntry { Name = "metadata.ini", Size = meta.Length, Data = meta });

    // Surface the 512-byte dump head verbatim.
    var headSize = Math.Min(GhostLegacyConstants.DumpHeadSize, this._data.Length);
    var head = this._data.AsSpan(0, headSize).ToArray();
    entries.Add(new GhostEntry { Name = "dump-head.bin", Size = head.Length, Data = head });

    // Surface the rest of the file as the dump body.
    if (this._data.Length > GhostLegacyConstants.DumpHeadSize) {
      var bodyLen = this._data.Length - GhostLegacyConstants.DumpHeadSize;
      var body = this._data.AsSpan(GhostLegacyConstants.DumpHeadSize, bodyLen).ToArray();
      entries.Add(new GhostEntry { Name = "dump-body.bin", Size = body.Length, Data = body });
    }

    this.Entries = entries;
  }

  /// <summary>
  /// True when the bytes look like a pre-3.0 Ghost dump: FE EF magic at
  /// offset 0, a recognised head type at offset 2, and NO occurrence of
  /// the modern Ghost 0x012F18D8 record magic anywhere in the file.
  /// </summary>
  public static bool LooksLikeLegacyHeader(ReadOnlySpan<byte> data) {
    if (data.Length < GhostLegacyConstants.DumpHeadSize) return false;
    if (data[0] != 0xFE || data[1] != 0xEF) return false;
    if (!IsKnownHeadType(data[2])) return false;

    // Modern Ghost 3.0+ images have the 0x012F18D8 record magic somewhere
    // in the body. Pre-3.0 images do not. Scan for it; if found, this is
    // a modern image masquerading via the same FE EF magic — let the
    // modern parser handle it.
    if (HasModernRecordMagic(data)) return false;

    return true;
  }

  private static bool IsKnownHeadType(byte t) =>
    t == GhostLegacyConstants.HeadTypeFirst
    || t == GhostLegacyConstants.HeadTypePartition
    || t == GhostLegacyConstants.HeadTypeBoot;

  private static bool HasModernRecordMagic(ReadOnlySpan<byte> data) {
    // Modern Ghost records start with [4B type][4B magic 0x012F18D8].
    // Scan a reasonable prefix (we don't need to find every record — one
    // is enough to know this is a modern image).
    var end = Math.Min(data.Length - 4, GhostLegacyConstants.ModernRecordMagicScanLimit);
    for (var i = GhostLegacyConstants.DumpHeadSize; i < end; i++) {
      var m = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i, 4));
      if (m == GhostConstants.RecordMagic) return true;
    }
    return false;
  }

  private byte[] BuildMetadata() {
    var b = new StringBuilder();
    b.Append("format=Symantec / Norton Ghost backup image (pre-3.0 / DOS-era)\n");
    b.Append("generation_hint=PreModern1And2\n");
    b.Append("parse_status=ok\n");
    b.Append("stage=1\n");
    b.Append("note=Pre-3.0 (Ghost 1.x / 2.x DOS) dump head + body surfaced verbatim. Recovery path: Ghost Explorer (ghostexp.exe) or Ghost32.exe.\n");
    b.Append(CultureInfo.InvariantCulture, $"image_size={this._data.Length}\n");
    b.Append(CultureInfo.InvariantCulture, $"dump_head_type=0x{this.HeadType:X2}\n");
    b.Append("dump_head_type_label=");
    b.Append(this.HeadType switch {
      GhostLegacyConstants.HeadTypeFirst => "first_record(disk_descriptor)\n",
      GhostLegacyConstants.HeadTypePartition => "partition_record\n",
      GhostLegacyConstants.HeadTypeBoot => "boot_record\n",
      _ => "unknown\n"
    });
    b.Append("leading_bytes_hex=");
    foreach (var x in this.LeadingBytes) b.Append(CultureInfo.InvariantCulture, $"{x:X2}");
    b.Append('\n');
    b.Append(CultureInfo.InvariantCulture, $"dump_head_size={GhostLegacyConstants.DumpHeadSize}\n");
    b.Append("re_source=Ghost 1.6 GHOST.EXE (archive.org ghost16, MD5 64cef43d0eb8d456de990cc95353fa05) — WriteDumpHeader at file_off 0x897d, ReadDumpHeader2 at file_off 0x8a6f; DS=0x1ca4.\n");
    b.Append("re_method=binary inspection of WriteDumpHeader / ReadDumpHeader2 functions — magic bytes (FE EF) and head-type-byte at offset 2 confirmed from the encoder and decoder both.\n");
    return Encoding.UTF8.GetBytes(b.ToString());
  }
}

/// <summary>
/// On-disk constants for the pre-3.0 Ghost dump format (Binary Research
/// Ghost 1.6 / 2.x DOS era, 1996-1998). Reverse-engineered from the Ghost
/// 1.6 GHOST.EXE binary's <c>WriteDumpHeader</c> and
/// <c>ReadDumpHeader2</c> functions.
/// </summary>
public static class GhostLegacyConstants {

  /// <summary>The first byte of the magic — same as modern Ghost.</summary>
  public const byte MagicByte0 = 0xFE;

  /// <summary>The second byte of the magic — same as modern Ghost.</summary>
  public const byte MagicByte1 = 0xEF;

  /// <summary>The dump head is exactly 512 bytes (256 words zeroed by the
  /// binary's <c>rep stosw cx=0x100</c> at file_offset 0x899d).</summary>
  public const int DumpHeadSize = 512;

  /// <summary>The head-type byte sits at offset 2 of the 512-byte head
  /// (written by <c>mov es:[bx+2], al</c> at file_offset 0x89c2 and read
  /// by <c>mov al, es:[bx+2]</c> at file_offset 0x8ac8).</summary>
  public const int HeadTypeOffset = 2;

  /// <summary>Head type 1 — disk descriptor / first record. The binary's
  /// CopyDiskToFile calls <c>WriteDumpHeader(1)</c> at file_offset 0x84df
  /// then writes 2048 bytes of disk descriptor (1192 bytes from the
  /// disk_drive_data struct + 856 bytes zero-pad).</summary>
  public const byte HeadTypeFirst = 0x01;

  /// <summary>Head type 2 — partition data record (written by
  /// <c>WriteDumpHeader(2)</c> at file_offset 0x8794).</summary>
  public const byte HeadTypePartition = 0x02;

  /// <summary>Head type 3 — boot record / trailer (written by
  /// <c>WriteDumpHeader(3)</c> at file_offset 0x86a8).</summary>
  public const byte HeadTypeBoot = 0x03;

  /// <summary>How far into the file we scan for the modern 0x012F18D8
  /// record magic before deciding this is a pre-3.0 image. 64 KiB is
  /// enough to cover even a heavily padded modern first record.</summary>
  public const int ModernRecordMagicScanLimit = 65536;
}
