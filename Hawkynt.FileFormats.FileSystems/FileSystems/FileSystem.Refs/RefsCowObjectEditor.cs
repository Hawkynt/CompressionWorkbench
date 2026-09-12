#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.Refs;

internal sealed record RefsCowObjectMutation(
  string Path,
  ulong ObjectId,
  RefsCowTreeResult ObjectTree,
  RefsCowTreeResult ObjectTableTree,
  bool UsesBackingRow);

/// <summary>
/// Applies an immutable row edit to one filesystem object's B+ tree, then
/// cascades the replacement object-root reference into Object Table and rebuilds
/// checkpoint root #0. The caller registers <see cref="RefsCowObjectMutation.ObjectTableTree"/>
/// with the native transaction publisher.
/// </summary>
internal sealed class RefsCowObjectEditor {
  private readonly RefsMetadataReader _metadata;
  private readonly RefsWritableNamespace _namespace;
  private readonly RefsCowBTree _tree;

  public RefsCowObjectEditor(RefsMetadataReader metadata, RefsCowBTree tree) {
    ArgumentNullException.ThrowIfNull(metadata);
    ArgumentNullException.ThrowIfNull(tree);
    this._metadata = metadata;
    this._tree = tree;
    this._namespace = new RefsWritableNamespace(metadata);
  }

  public RefsCowObjectMutation UpdateStorageValue(
      string path,
      Func<RefsWritableStorageLocation, byte[]> buildValue,
      bool caseSensitiveDirectory = false) {
    ArgumentNullException.ThrowIfNull(buildValue);
    var location = this._namespace.ResolveStorage(path);
    var value = buildValue(location)
      ?? throw new InvalidOperationException("ReFS storage-value editor returned null.");
    if (value.Length > ushort.MaxValue)
      throw new InvalidOperationException("ReFS outer B+ row value exceeds its 16-bit length field.");

    var oldRow = location.StorageRow;
    if (value.AsSpan().SequenceEqual(oldRow.Value))
      throw new InvalidOperationException("ReFS storage-value mutation produced no byte change.");
    var replacementRow = new RefsTreeRow(oldRow.Key.ToArray(), value, oldRow.Flags);
    var objectTree = this._tree.Upsert(
      location.StorageRoot,
      virtualAddresses: true,
      replacementRow,
      caseSensitiveDirectory);
    var objectTableTree = this.ReplaceObjectRoot(location.StorageObjectId, objectTree.RootReference);
    return new RefsCowObjectMutation(
      path,
      location.StorageObjectId,
      objectTree,
      objectTableTree,
      location.UsesBackingRow);
  }

  public RefsCowTreeResult ReplaceObjectRoot(
      ulong objectId,
      ReadOnlySpan<byte> replacementReference) {
    if (replacementReference.Length != this._metadata.PageReferenceSize)
      throw new ArgumentException(
        $"ReFS object root reference must be exactly {this._metadata.PageReferenceSize} bytes.", nameof(replacementReference));
    var parsed = RefsPageReference.Parse(replacementReference);
    if (parsed.Lcns.Count == 0)
      throw new InvalidDataException($"Replacement ReFS object root for OID 0x{objectId:X} has no page address.");

    RefsBTreeRow? match = null;
    foreach (var row in this._metadata.WalkRoot(0)) {
      if (row.Key.Length < 16 || row.Value.Length < 0x20 + this._metadata.PageReferenceSize) continue;
      if (BinaryPrimitives.ReadUInt64LittleEndian(row.Key.AsSpan(8, 8)) != objectId) continue;
      if (match != null)
        throw new InvalidDataException($"ReFS Object Table contains duplicate OID 0x{objectId:X}.");
      match = row;
    }
    if (match == null)
      throw new InvalidDataException($"ReFS Object Table does not contain OID 0x{objectId:X}.");

    var value = match.Value.ToArray();
    replacementReference.CopyTo(value.AsSpan(0x20, checked((int)this._metadata.PageReferenceSize)));
    return this._tree.Upsert(
      this._metadata.Roots[0],
      virtualAddresses: true,
      new RefsTreeRow(match.Key.ToArray(), value, match.Flags));
  }
}
