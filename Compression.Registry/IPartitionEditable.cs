namespace Compression.Registry;

/// <summary>
/// Capability marker for archive/disk-container formats whose payload is a
/// raw block-device image (MBR/GPT partitioned) that the user can edit with a
/// partition editor.
/// </summary>
/// <remarks>
/// <para>
/// Format descriptors implementing this interface (e.g. VHD, VHDX, VMDK,
/// QCOW2, VDI) expose the inner guest disk as a readable + writable +
/// seekable <see cref="Stream"/>. Callers wrap that stream in
/// <c>PartitionEditor</c> (from <c>Compression.Core.DiskImage</c>) to
/// list/add/delete/format partitions on the guest disk.
/// </para>
/// <para>
/// The interface lives in <c>Compression.Registry</c> rather than
/// <c>Compression.Core</c> so that format descriptors can advertise the
/// capability without taking a dependency on the disk-image internals. The
/// guest-disk stream is the lowest-common-denominator contract — partition
/// editing logic stays in Core.
/// </para>
/// </remarks>
public interface IPartitionEditable {

  /// <summary>
  /// Opens the inner (guest) disk image as a <see cref="Stream"/> suitable
  /// for partition-table editing. The returned stream must support reading,
  /// writing, and seeking. The caller owns the returned stream and must
  /// dispose it; disposing it must <em>not</em> dispose the outer
  /// <paramref name="image"/> stream.
  /// </summary>
  /// <param name="image">The container file (VHD/VHDX/VMDK/QCOW2/VDI/…). Must
  /// be readable, writable, and seekable.</param>
  /// <returns>A stream over the raw guest disk bytes (where the MBR sits at
  /// offset 0).</returns>
  /// <exception cref="NotSupportedException">
  /// Thrown when the underlying container layout cannot expose a
  /// straightforward writable byte-stream view (e.g. compressed sparse
  /// layouts, snapshot chains).
  /// </exception>
  Stream OpenGuestDiskStream(Stream image);
}
