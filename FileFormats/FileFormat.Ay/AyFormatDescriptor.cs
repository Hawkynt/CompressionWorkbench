#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Ay8910;
using Codec.Pcm;
using Compression.Registry;

namespace FileFormat.Ay;

/// <summary>
/// Surfaces a ZX Spectrum / Amstrad CPC AY music file (<c>.ay</c>) as a metadata-rich
/// pseudo-archive. An AY file carries Z80 memory blocks that an AY-3-8910/12 player drives; there
/// is no audio to decode, so each song's loaded memory blocks are surfaced verbatim as Kind
/// <c>Stream</c> blobs.
/// <para>The header (magic <c>ZXAYEMUL</c>) is <b>big-endian</b> and pointer-chased: every
/// 16-bit pointer is a <b>signed self-relative offset</b> measured from the byte position of the
/// pointer field itself. Strings are NUL-terminated at the pointed location. The header points to
/// an author string, a misc string and a song table; each song entry points to a song-name string
/// and a song-data structure; the song-data structure points to a memory-block list whose entries
/// are (u16 address, u16 length, u16 self-relative data offset), terminated by a zero address.</para>
/// <para>Every pointer dereference is bounds-checked against the file; an out-of-range pointer is
/// skipped rather than throwing, so a truncated or corrupt file degrades gracefully to whatever
/// could be parsed (down to FULL-only).</para>
/// Per song, memory blocks are surfaced as <c>songs/NN_&lt;name&gt;.bin</c>.
/// </summary>
public sealed class AyFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {

  public string Id => "Ay";
  public string DisplayName => "ZX Spectrum AY";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".ay";
  public IReadOnlyList<string> Extensions => [".ay"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("ZXAYEMUL"u8.ToArray(), Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "ZX Spectrum AY music file; full file + header metadata + per-song memory blocks.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password)
    => AudioPseudoArchive.List(BuildEntries(stream));

  public void Extract(Stream stream, string outputDir, string? password, string[]? files)
    => AudioPseudoArchive.Extract(BuildEntries(stream), outputDir, files);

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password)
    => AudioPseudoArchive.ExtractEntry(BuildEntries(input), entryName, output);

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.ay", "Container", blob),
    };

    var renderNote = new StringBuilder();
    AddRenderedChannels(blob, entries, renderNote);

    try {
      Parse(blob, entries, renderNote.ToString());
    } catch {
      // Any parse failure degrades to FULL-only.
    }

    return entries;
  }

  /// <summary>
  /// Plays the first song through the Z80 + AY-3-8910 player and surfaces rendered
  /// <c>LEFT.wav</c> / <c>RIGHT.wav</c> (Kind <c>Channel</c>, 44.1 kHz, 30 s cap). Any failure
  /// (unparsable song, no init) degrades silently to FULL + metadata only.
  /// </summary>
  private static void AddRenderedChannels(byte[] blob, List<AudioPseudoArchive.Entry> entries, StringBuilder note) {
    try {
      var player = new AyPlayer(blob, songIndex: 0);
      const double seconds = 30.0;
      var stereo = player.Render(seconds);
      var (left, right) = DeinterleaveStereo(stereo);
      entries.Add(new("LEFT.wav", "Channel", PcmCodec.ToWavBlob(left, 1, Ay8910Chip.OutputSampleRate, 16), "pcm"));
      entries.Add(new("RIGHT.wav", "Channel", PcmCodec.ToWavBlob(right, 1, Ay8910Chip.OutputSampleRate, 16), "pcm"));
      note.AppendLine("rendered=LEFT.wav,RIGHT.wav");
      note.AppendLine("rendered_seconds=30");
      note.AppendLine("rendered_rate=44100");
      note.AppendLine("rendered_chip=AY-3-8910");
      note.AppendLine("rendered_stereo=ABC");
    } catch {
      // Undecodable — FULL + metadata only.
    }
  }

  private static (byte[] Left, byte[] Right) DeinterleaveStereo(short[] stereo) {
    var frames = stereo.Length / 2;
    var left = new byte[frames * 2];
    var right = new byte[frames * 2];
    for (var f = 0; f < frames; ++f) {
      BinaryPrimitives.WriteInt16LittleEndian(left.AsSpan(f * 2), stereo[f * 2]);
      BinaryPrimitives.WriteInt16LittleEndian(right.AsSpan(f * 2), stereo[f * 2 + 1]);
    }
    return (left, right);
  }

  private static void Parse(byte[] blob, List<AudioPseudoArchive.Entry> entries, string renderNote) {
    if (blob.Length < 0x14)
      return;

    var fileVersion = blob[0x08];
    var playerVersion = blob[0x09];
    var pAuthor = ReadPointer(blob, 0x0C);
    var pMisc = ReadPointer(blob, 0x0E);
    var numSongs = blob[0x10] + 1;
    var firstSong = blob[0x11] + 1;
    var pSongs = ReadPointer(blob, 0x12);

    var sb = new StringBuilder();
    sb.AppendLine("[ay]");
    sb.AppendLine($"file_version={fileVersion}");
    sb.AppendLine($"player_version={playerVersion}");
    sb.AppendLine($"num_songs={numSongs}");
    sb.AppendLine($"first_song={firstSong}");
    AppendField(sb, "author", ReadNulString(blob, pAuthor));
    AppendField(sb, "misc", ReadNulString(blob, pMisc));

    if (pSongs >= 0 && pSongs + numSongs * 4 <= blob.Length) {
      for (var i = 0; i < numSongs; ++i) {
        var entryPos = pSongs + i * 4;
        var pName = ReadPointer(blob, entryPos);
        var pData = ReadPointer(blob, entryPos + 2);
        var name = ReadNulString(blob, pName);
        sb.AppendLine($"song{i}_name={name}");
        ExtractSongBlocks(blob, pData, i, name, entries);
      }
    }

    if (renderNote.Length > 0)
      sb.Append(renderNote);

    entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(sb.ToString())));
  }

  /// <summary>
  /// Dereferences a song-data structure and walks its memory-block list. The structure's last
  /// pointer (at +4) is <c>pAddresses</c>, the block list; each block is
  /// (u16 address, u16 length, u16 self-relative data offset), terminated by address == 0.
  /// </summary>
  private static void ExtractSongBlocks(byte[] blob, int pData, int songIndex, string name, List<AudioPseudoArchive.Entry> entries) {
    // Song-data structure: u8 chanA,B,C,D | u16 noise | u16 pPoints | u16 pAddresses.
    if (pData < 0 || pData + 14 > blob.Length)
      return;

    var pAddresses = ReadPointer(blob, pData + 12);
    if (pAddresses < 0)
      return;

    var safeName = Sanitize(name);
    var block = 0;
    var pos = pAddresses;
    while (pos + 6 <= blob.Length) {
      var address = BinaryPrimitives.ReadUInt16BigEndian(blob.AsSpan(pos));
      var length = BinaryPrimitives.ReadUInt16BigEndian(blob.AsSpan(pos + 2));
      if (address == 0 && length == 0)
        break;

      var dataOffset = ReadPointer(blob, pos + 4);
      if (dataOffset >= 0 && length > 0 && dataOffset + length <= blob.Length) {
        var payload = blob[dataOffset..(dataOffset + length)];
        var label = string.IsNullOrEmpty(safeName) ? $"song{songIndex:D2}" : $"{songIndex:D2}_{safeName}";
        entries.Add(new($"songs/{label}_{address:X4}.bin", "Stream", payload));
      }

      ++block;
      pos += 6;
      if (block > 256)
        break; // defensive cap against a cyclic/garbage list.
    }
  }

  /// <summary>
  /// Reads a big-endian signed self-relative pointer at <paramref name="position"/> and returns
  /// the absolute file offset it targets, or -1 when the field or target is out of range.
  /// </summary>
  private static int ReadPointer(byte[] blob, int position) {
    if (position < 0 || position + 2 > blob.Length)
      return -1;
    var rel = (short)BinaryPrimitives.ReadUInt16BigEndian(blob.AsSpan(position));
    if (rel == 0)
      return -1; // null pointer
    var target = position + rel;
    return target >= 0 && target < blob.Length ? target : -1;
  }

  private static string ReadNulString(byte[] blob, int offset) {
    if (offset < 0 || offset >= blob.Length)
      return string.Empty;
    var end = offset;
    while (end < blob.Length && blob[end] != 0)
      ++end;
    return Encoding.Latin1.GetString(blob, offset, end - offset).Trim();
  }

  private static string Sanitize(string name) {
    var chars = name.Where(c => char.IsLetterOrDigit(c) || c is '_' or '-').ToArray();
    return new string(chars);
  }

  private static void AppendField(StringBuilder sb, string key, string value) {
    value = value.Trim();
    if (value.Length > 0)
      sb.AppendLine($"{key}={value}");
  }
}
