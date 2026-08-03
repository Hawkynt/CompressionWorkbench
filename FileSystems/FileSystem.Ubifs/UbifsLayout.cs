#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.Ubifs;

/// <summary>
/// Finds every node in a UBIFS image and says which file's bytes it carries.
/// </summary>
/// <remarks>
/// <para>What this writer emits is a linear log of nodes and nothing else — no
/// index tree, no erase-block accounting, no journal heads. The reader replays
/// that log, taking the highest sequence number for each inode and block. So a
/// node's position is recorded nowhere at all: it is found by looking for the
/// magic at the head of it.</para>
///
/// <para>That is what makes a node movable without repointing anything. What it
/// does mean is that the bytes left behind have to go: a copy of a node still
/// carrying its magic is a second node, and the log would replay both.</para>
/// </remarks>
internal static class UbifsLayout {

  internal const uint NodeMagic = 0x06101831;

  private const int CommonHeaderSize = 24;
  // Common header: magic(4) crc(4) sqnum(8) len(4) type(1) group_type(1) pad(2).
  private const int LengthOffset = 16;
  private const int TypeOffset = 20;

  private const byte NodeTypeInode = 0;
  private const byte NodeTypeData = 1;
  private const byte NodeTypeDentry = 2;

  /// <summary>One node of the log.</summary>
  /// <param name="Offset">Where the node starts.</param>
  /// <param name="Length">How long it is, rounded to the eight bytes a node is aligned to.</param>
  /// <param name="Type">What kind of node it is.</param>
  /// <param name="InodeNumber">The inode it speaks for.</param>
  internal readonly record struct Node(long Offset, long Length, byte Type, uint InodeNumber);

  /// <summary>Every node in the image, in the order the log holds them.</summary>
  public static List<Node> Nodes(Stream image) {
    ArgumentNullException.ThrowIfNull(image);

    var nodes = new List<Node>();
    if (!image.CanSeek || image.Length < CommonHeaderSize) return nodes;

    var raw = new byte[image.Length];
    image.Position = 0;
    image.ReadExactly(raw);

    var at = 0;
    while (at + CommonHeaderSize <= raw.Length) {
      if (BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(at)) != NodeMagic) { ++at; continue; }

      var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(at + LengthOffset));
      if (length < CommonHeaderSize || at + length > raw.Length) { ++at; continue; }

      var type = raw[at + TypeOffset];
      var inode = type is NodeTypeInode or NodeTypeData or NodeTypeDentry
        ? BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(at + CommonHeaderSize))
        : 0u;

      // A node occupies whole eight-byte units; the log walks it that way.
      var padded = (length + 7) & ~7;
      nodes.Add(new Node(at, Math.Min(padded, raw.Length - at), type, inode));
      at += padded;
    }

    return nodes;
  }

  /// <summary>Whether a node carries a file's bytes rather than describing one.</summary>
  public static bool IsData(byte type) => type == NodeTypeData;
}
