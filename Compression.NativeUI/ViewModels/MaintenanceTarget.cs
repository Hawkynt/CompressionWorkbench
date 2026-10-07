namespace Compression.NativeUI.ViewModels;

/// <summary>What a maintenance target is to the shell: the volume it has open, or something selected.</summary>
internal enum MaintenanceTargetKind {
  /// <summary>The archive or image the shell is inside.</summary>
  OpenVolume,

  /// <summary>A selected file — on disk, or an entry of the open archive — whose content proved it a container.</summary>
  Selected,
}

/// <summary>
/// One thing the Defragment tab can work on, as the Target field lists it.
/// </summary>
/// <param name="Kind">Whether it is the open volume or a selection.</param>
/// <param name="Key">Identifies the target across requeries.</param>
/// <param name="FormatId">The format its content was detected as.</param>
/// <param name="Name">The file name the user knows it by.</param>
/// <param name="Entry">The selected entry, for <see cref="MaintenanceTargetKind.Selected"/>.</param>
internal sealed record MaintenanceTarget(MaintenanceTargetKind Kind, string Key, string FormatId, string Name, ArchiveEntryViewModel? Entry) {
  /// <summary>How the Target field shows it.</summary>
  public string Label => this.Kind == MaintenanceTargetKind.OpenVolume
    ? $"Open volume ({this.Name})"
    : $"Selected: {this.Name} ({this.FormatId})";
}

/// <summary>A command asking for the Defragment tab.</summary>
/// <param name="Verb">The operation to preselect, or null for none.</param>
/// <param name="PreferSelection">Whether the command came from the selection (the list's context menu), so a proven selected container is the target rather than the open volume.</param>
internal sealed record MaintenanceRequest(Maintenance.MaintenanceVerb? Verb, bool PreferSelection);
