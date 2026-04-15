#pragma warning disable CS1591

using System.Collections.ObjectModel;

namespace Compression.UI.ViewModels;

/// <summary>
/// Data model for a hierarchical tree view showing:
/// disk image -> partition table -> partition -> filesystem -> files.
/// <para>
/// This is infrastructure for the planned "UI tree view" feature (TODO 6c).
/// The <see cref="DiskImageTreeNode"/> hierarchy can be bound to a WPF <c>TreeView</c>
/// via <c>HierarchicalDataTemplate</c>. A future implementation would:
/// <list type="number">
///   <item>Open a disk image (VHD/VMDK/QCOW2/VDI/raw .img)</item>
///   <item>Use <c>PartitionTableDetector</c> from Compression.Core to enumerate partitions</item>
///   <item>For each partition, detect the filesystem format (NTFS, FAT, ext4, ...)</item>
///   <item>List the filesystem contents as child nodes</item>
///   <item>Allow extraction of individual files from within the nested hierarchy</item>
/// </list>
/// </para>
/// </summary>
internal sealed class DiskImageTreeViewModel : ViewModelBase {

  /// <summary>Root nodes of the tree (typically one per disk image).</summary>
  public ObservableCollection<DiskImageTreeNode> RootNodes { get; } = [];

  private DiskImageTreeNode? _selectedNode;

  /// <summary>Currently selected node in the tree.</summary>
  public DiskImageTreeNode? SelectedNode {
    get => _selectedNode;
    set => SetField(ref _selectedNode, value);
  }

  // TODO (6c): Populate the tree from AutoExtractor.ExtractionResult
  // Example integration point:
  //   var result = new AutoExtractor().Extract(diskImageBytes);
  //   var root = new DiskImageTreeNode { Name = "disk.vhd", NodeType = TreeNodeType.DiskImage };
  //   foreach (var partition in result.PartitionTable?.Partitions ?? [])
  //     root.Children.Add(BuildPartitionNode(partition, result));
  //   RootNodes.Add(root);
}

/// <summary>
/// Represents a single node in the disk image hierarchy tree.
/// </summary>
internal sealed class DiskImageTreeNode : ViewModelBase {

  private string _name = "";
  private TreeNodeType _nodeType;
  private long _size;
  private string _formatDescription = "";
  private bool _isExpanded;

  /// <summary>Display name of this node.</summary>
  public string Name { get => _name; set => SetField(ref _name, value); }

  /// <summary>Type of node (disk, partition, filesystem, directory, file).</summary>
  public TreeNodeType NodeType { get => _nodeType; set => SetField(ref _nodeType, value); }

  /// <summary>Size in bytes (for files/partitions).</summary>
  public long Size { get => _size; set => SetField(ref _size, value); }

  /// <summary>Format description (e.g. "NTFS", "FAT32", "GPT Partition 1").</summary>
  public string FormatDescription { get => _formatDescription; set => SetField(ref _formatDescription, value); }

  /// <summary>Whether this node is expanded in the tree view.</summary>
  public bool IsExpanded { get => _isExpanded; set => SetField(ref _isExpanded, value); }

  /// <summary>Child nodes.</summary>
  public ObservableCollection<DiskImageTreeNode> Children { get; } = [];
}

/// <summary>
/// Classification of a node in the disk image hierarchy.
/// </summary>
internal enum TreeNodeType {
  /// <summary>A virtual disk image file (VHD, VMDK, QCOW2, etc.).</summary>
  DiskImage,

  /// <summary>A partition table (MBR or GPT).</summary>
  PartitionTable,

  /// <summary>A single partition within a partition table.</summary>
  Partition,

  /// <summary>A filesystem detected on a partition or disk.</summary>
  Filesystem,

  /// <summary>A directory within a filesystem.</summary>
  Directory,

  /// <summary>A file within a filesystem.</summary>
  File,
}
