#pragma warning disable CS1591
namespace FileFormat.OneNote;

/// <summary>
/// Specifies one note variant values.
/// </summary>
public enum OneNoteVariant {
  /// <summary>
  /// Specifies an unknown or unrecognized value.
  /// </summary>
  Unknown,
  /// <summary>
  /// Specifies the one note 2007 option.
  /// </summary>
  OneNote2007,
  /// <summary>
  /// Specifies the one note 2010 plus option.
  /// </summary>
  OneNote2010Plus,
}

/// <summary>Identifies the revision-store file type declared by its header.</summary>
public enum OneNoteFileType {
  /// <summary>The header does not identify a supported OneNote revision store.</summary>
  Unknown,
  /// <summary>A notebook section (<c>.one</c>) file.</summary>
  Section,
  /// <summary>A notebook table-of-contents (<c>.onetoc2</c>) file.</summary>
  TableOfContents,
}

/// <summary>
/// Represents an one note detector.
/// </summary>
public static class OneNoteDetector {

  // {7B5C52E4-D88C-4DA7-AEB1-5378D02996D3} — OneNote 2010+ section file GUID.
  // Stored as Microsoft GUID layout: Data1 (uint32 LE), Data2/3 (uint16 LE), Data4 (8 bytes raw).
  internal static readonly byte[] Guid2010Plus = [
    0xE4, 0x52, 0x5C, 0x7B,
    0x8C, 0xD8,
    0xA7, 0x4D,
    0xAE, 0xB1, 0x53, 0x78, 0xD0, 0x29, 0x96, 0xD3,
  ];

  // {109ADD3F-911B-49F5-A5D0-1791EDC8AED8} — OneNote 2007 section file GUID.
  internal static readonly byte[] Guid2007 = [
    0x3F, 0xDD, 0x9A, 0x10,
    0x1B, 0x91,
    0xF5, 0x49,
    0xA5, 0xD0, 0x17, 0x91, 0xED, 0xC8, 0xAE, 0xD8,
  ];

  // {43FF2FA1-EFD9-4C76-9EE2-10EA5722765F} — MS-ONESTORE Header.guidFileType for .onetoc2.
  internal static readonly byte[] GuidTableOfContents = [
    0xA1, 0x2F, 0xFF, 0x43,
    0xD9, 0xEF,
    0x76, 0x4C,
    0x9E, 0xE2, 0x10, 0xEA, 0x57, 0x22, 0x76, 0x5F,
  ];

  /// <summary>Detects the .one or .onetoc2 file type from the leading GUID.</summary>
  public static OneNoteFileType DetectFileType(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanSeek) throw new ArgumentException("Stream must be seekable.", nameof(stream));
    var origin = stream.Position;
    try {
      stream.Seek(0, SeekOrigin.Begin);
      Span<byte> head = stackalloc byte[16];
      var read = 0;
      while (read < head.Length) {
        var n = stream.Read(head[read..]);
        if (n <= 0) break;
        read += n;
      }
      if (read != head.Length) return OneNoteFileType.Unknown;
      if (head.SequenceEqual(Guid2010Plus)) return OneNoteFileType.Section;
      if (head.SequenceEqual(GuidTableOfContents)) return OneNoteFileType.TableOfContents;
      return OneNoteFileType.Unknown;
    } finally {
      stream.Seek(origin, SeekOrigin.Begin);
    }
  }

  /// <summary>
  /// Performs the detect operation.
  /// </summary>
  public static OneNoteVariant Detect(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanSeek) throw new ArgumentException("Stream must be seekable.", nameof(stream));

    var origin = stream.Position;
    try {
      stream.Seek(0, SeekOrigin.Begin);
      Span<byte> head = stackalloc byte[16];
      var read = 0;
      while (read < 16) {
        var n = stream.Read(head[read..]);
        if (n <= 0) break;
        read += n;
      }
      if (read < 16) return OneNoteVariant.Unknown;

      if (head.SequenceEqual(Guid2010Plus) || head.SequenceEqual(GuidTableOfContents)) return OneNoteVariant.OneNote2010Plus;
      if (head.SequenceEqual(Guid2007)) return OneNoteVariant.OneNote2007;
      return OneNoteVariant.Unknown;
    } finally {
      stream.Seek(origin, SeekOrigin.Begin);
    }
  }
}
