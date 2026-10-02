using Compression.Core.Layout;
using Compression.Lib.Layout;
using Compression.NativeUI.Controls;
using Compression.NativeUI.Maintenance;
using Compression.NativeUI.Theming;
using Compression.NativeUI.ViewModels;
using Compression.Registry;
using Compression.Registry.Layout;
using Hawkynt.NativeForms;
using Color = System.Drawing.Color;

namespace Compression.NativeUI.Views;

/// <summary>
/// The Defragment tab: a contextual "Disk Tools" tab that appears while there is something to
/// maintain — the open archive or image, or an archive file selected in a folder — and, while it is
/// selected, gives the whole client area to the block map. Every operation, defragmentation strategy,
/// layout option and view mode is a ribbon item; each is enabled exactly when the target supports it.
/// </summary>
/// <remarks>
/// The tab holds one <see cref="MaintenanceSession"/> while it is open, so a nested archive is
/// extracted once and written back after each change, and the work itself goes through
/// <see cref="MaintenancePresenter"/>. Leaving the tab hides the block view and shows the browser
/// again exactly as it was; nothing about it is rebuilt.
/// </remarks>
internal sealed partial class MainForm {

  private readonly RibbonContextualTabGroup _diskTools = new("Disk Tools", Color.FromArgb(0x3A, 0x8E, 0x5C)) { Visible = false };
  private readonly DefragmentView _defragView = new() { Visible = false };
  private RibbonTab _defragTab = null!;

  private readonly Dictionary<MaintenanceVerb, RibbonToggleButton> _verbToggles = [];
  private readonly Dictionary<DefragStrategy, RibbonToggleButton> _strategyToggles = [];
  private readonly Dictionary<BlockMapView, RibbonToggleButton> _viewToggles = [];
  private RibbonToggleButton _packAtEndToggle = null!;
  private RibbonToggleButton _filesPanelToggle = null!;
  private RibbonToggleButton _legendToggle = null!;
  private RibbonButton _startButton = null!;
  private RibbonButton _stopButton = null!;
  private RibbonButton _analyzeButton = null!;
  private RibbonButton _editProfilesButton = null!;

  // Ribbon fields, drawn by the ribbon: one row tall on every backend, and still usable from a
  // collapsed group's popup.
  private readonly RibbonSpinner _interleaveSpinner = new("Interleave") {
    Minimum = MaintenanceInput.MinInterleave, Maximum = MaintenanceInput.MaxInterleave, Value = 1, FieldWidth = 56,
  };
  private readonly RibbonComboBox _metadataZoneCombo = new("Metadata") { FieldWidth = 112 };
  private readonly RibbonComboBox _layoutProfileCombo = new("Profile") { FieldWidth = 112 };
  private readonly RibbonSpinner _holeSizeSpinner = new("Hole KiB") { Minimum = 1, Maximum = 1L << 32, Value = 64, Increment = 64, FieldWidth = 80 };
  private readonly RibbonComboBox _holePlacementCombo = new("Hole at") { FieldWidth = 80 };
  private readonly RibbonSpinner _holeOffsetSpinner = new("Offset KiB") { Minimum = 0, Maximum = 1L << 32, Value = 0, Increment = 64, FieldWidth = 80 };
  private readonly RibbonSpinner _seedSpinner = new("Seed") { Minimum = int.MinValue, Maximum = int.MaxValue, Value = 1, FieldWidth = 80 };
  private readonly RibbonComboBox _optimizeMethodCombo = new("Method") { FieldWidth = 112 };
  private readonly List<LayoutProfileEntry?> _layoutProfileEntries = [];

  private MaintenanceSession? _maintenanceSession;
  private MaintenancePresenter? _presenter;
  private MaintenanceVerb? _pendingVerb;
  private CancellationTokenSource? _maintenanceCancellation;
  private bool _maintenanceCommitStarted;
  private bool _defragActive;
  private bool _syncingDefragRibbon;
  private int _analysisGeneration;

  /// <summary>The last analysis started, so a test can wait for the map it draws.</summary>
  internal Task? AnalysisTask { get; private set; }

  /// <summary>The last operation started, completed once its outcome has been shown.</summary>
  internal Task? MaintenanceTask { get; private set; }

  // ── construction ────────────────────────────────────────────────────────────────────────────

  private void BuildDefragmentTab() {
    this._defragTab = new RibbonTab("Defragment");
    this._defragTab.Groups.AddRange(this.OperationGroup(), this.StrategyGroup(), this.OptionsGroup(), this.ViewGroup());

    this._ribbon.Tabs.Add(this._defragTab);
    this._diskTools.Add(this._defragTab);
    this._ribbon.ContextualTabGroups.Add(this._diskTools);

    this._ribbon.SelectedIndexChanged += (_, _) => this.OnRibbonTabChanged();
    this._model.MaintenanceRequested += (_, verb) => this.OpenDefragmentTab(verb);
    this._model.LocationChanged += (_, _) => this.UpdateDiskTools();
    CommandManager.RequerySuggested += (_, _) => this.UpdateDiskTools();
    this.FormClosing += this.OnClosingWhileMaintaining;
    this.UpdateDiskTools();
  }

  private RibbonGroup OperationGroup() {
    (MaintenanceVerb Verb, string Text, string Icon)[] verbs = [
      (MaintenanceVerb.Defragment, "Defragment", IconKeys.Defragment),
      (MaintenanceVerb.Optimize, "Optimize", IconKeys.Analyze),
      (MaintenanceVerb.Shrink, "Shrink", IconKeys.Defragment),
      (MaintenanceVerb.Compact, "Compact", IconKeys.Defragment),
      (MaintenanceVerb.WipeEmpty, "Clear", IconKeys.Remove),
      (MaintenanceVerb.Purge, "Purge", IconKeys.Remove),
      (MaintenanceVerb.Scramble, "Scramble", IconKeys.Defragment),
    ];

    var group = new RibbonGroup("Operation");
    foreach (var (verb, text, icon) in verbs) {
      var toggle = this.RibbonToggle(text, icon, RibbonItemSize.Small, Keys.None, text, @checked: false, on => this.ChooseVerb(verb, on));
      this._verbToggles[verb] = toggle;
      group.Items.Add(toggle);
    }

    this._startButton = this.RibbonAction("Start", IconKeys.Test, this.StartMaintenance, RibbonItemSize.Large, Keys.None, "Run the chosen operation");
    this._stopButton = this.RibbonAction("Stop", IconKeys.Remove, this.StopMaintenance, RibbonItemSize.Small, Keys.None, "Stop the running operation");
    this._analyzeButton = this.RibbonAction("Analyze", IconKeys.Refresh, this.AnalyzeTarget, RibbonItemSize.Small, Keys.None, "Read the image's layout again");
    group.Items.AddRange(this._startButton, this._stopButton);
    return group;
  }

  private RibbonGroup StrategyGroup() {
    (DefragStrategy Strategy, string Text)[] strategies = [
      (DefragStrategy.Consolidate, "Consolidate"),
      (DefragStrategy.Defrag, "Defrag"),
      (DefragStrategy.Reorder, "Re-order"),
      (DefragStrategy.SortEntries, "Sort Entries"),
      (DefragStrategy.CarveHole, "Carve Hole"),
    ];

    var group = new RibbonGroup("Defrag Mode");
    foreach (var (strategy, text) in strategies) {
      var toggle = this.RibbonToggle(text, IconKeys.Defragment, RibbonItemSize.Small, Keys.None, text, @checked: false, on => this.ChooseStrategy(strategy, on));
      this._strategyToggles[strategy] = toggle;
      group.Items.Add(toggle);
    }

    this._packAtEndToggle = this.RibbonToggle("Pack at End", IconKeys.Defragment, RibbonItemSize.Small, Keys.None, "Consolidate at the back instead of the front",
      @checked: false, on => this.WithPresenter(p => p.PackAtEnd = on));
    group.Items.Add(this._packAtEndToggle);

    this._holePlacementCombo.Items.AddRange(["End", "Offset"]);
    this._holePlacementCombo.SelectedIndex = 0;
    this.Field(this._holeSizeSpinner, "Size of the hole to carve, in KiB.", () => this.WithPresenter(p => p.HoleSizeText = HoleSizeText()));
    this.Field(this._holePlacementCombo, "End: after the last live extent. Offset: at the offset beside.",
      () => this.WithPresenter(p => p.HoleAtText = HoleAtText()));
    this.Field(this._holeOffsetSpinner, "Where the hole starts, in KiB from the start of the image.",
      () => this.WithPresenter(p => p.HoleAtText = HoleAtText()));
    group.Items.AddRange(this._holeSizeSpinner, this._holePlacementCombo, this._holeOffsetSpinner);
    return group;
  }

  /// <summary>
  /// The layout options of a defragmentation, then — below Edit Profiles, shown only while their
  /// operation is picked — the one option Scramble, Optimize or Compact takes. Showing only the one
  /// that applies keeps the tab narrow.
  /// </summary>
  private RibbonGroup OptionsGroup() {
    // Short, because the field is narrow; the tooltip says what each placement means.
    this._metadataZoneCombo.Items.AddRange(["Unchanged", "Front", "Back", "Middle", "Before data"]);
    this._metadataZoneCombo.SelectedIndex = 0;
    this.RefreshLayoutProfiles();

    this.Field(this._interleaveSpinner,
      $"Block interleave: 1 = contiguous, 2 = every other block, N = each file's Kth block at start + K×N. {MaintenanceInput.MinInterleave}–{MaintenanceInput.MaxInterleave}.",
      () => this.WithPresenter(p => p.InterleaveText = InterleaveText()));
    this.Field(this._metadataZoneCombo, "Where filesystem metadata and directory entries are placed: unchanged, at the front (fast access), at the back (data first), in the middle (least seeking) or just before the file content (read-ahead).",
      () => this.WithPresenter(p => p.MetadataZone = this.SelectedMetadataZone()));
    this.Field(this._layoutProfileCombo, "A zone-based layout template: files go into named byte ranges with per-zone sort orders.",
      this.OnLayoutProfileChanged);
    this._editProfilesButton = this.RibbonAction("Edit Profiles", IconKeys.Properties, () => {
      new LayoutProfileEditor().ShowDialog(this);
      this.RefreshLayoutProfiles();
    }, RibbonItemSize.Small, Keys.None, "Create, change or delete layout profiles");

    this.Field(this._seedSpinner, "Seeds Scramble's shuffle. The same seed deals the same layout every run.",
      () => this.WithPresenter(p => p.SeedText = SeedText()));
    this.Field(this._optimizeMethodCombo, "How Optimize improves the container: compress (best compression, same format), repack (same entries, dead space dropped) or canonicalize (normal form). Only what the format offers is listed.",
      () => this.WithPresenter(p => p.OptimizeMethod = this.SelectedOptimizeMethod()));

    var group = new RibbonGroup("Options");
    group.Items.AddRange(this._interleaveSpinner, this._metadataZoneCombo, this._layoutProfileCombo,
      this._editProfilesButton, this._seedSpinner, this._optimizeMethodCombo);
    return group;
  }

  private RibbonGroup ViewGroup() {
    (BlockMapView View, string Text)[] views = [
      (BlockMapView.LinearBlocks, "Blocks"),
      (BlockMapView.CircularPlatter, "Circle"),
      (BlockMapView.CylinderStack, "3D Stack"),
    ];

    var group = new RibbonGroup("View");
    foreach (var (view, text) in views) {
      var toggle = this.RibbonToggle(text, IconKeys.ViewImage, RibbonItemSize.Small, Keys.None, text, @checked: view == BlockMapView.LinearBlocks, _ => this.ChooseView(view));
      this._viewToggles[view] = toggle;
      group.Items.Add(toggle);
    }

    this._filesPanelToggle = this.RibbonToggle("Files", IconKeys.ViewText, RibbonItemSize.Small, Keys.None, "Show the files beside the map", @checked: true,
      on => { if (!this._syncingDefragRibbon) this._defragView.FilesPanelVisible = on; });
    this._legendToggle = this.RibbonToggle("Legend", IconKeys.Properties, RibbonItemSize.Small, Keys.None, "Show the colour key under the map", @checked: true,
      on => { if (!this._syncingDefragRibbon) this._defragView.LegendVisible = on; });
    // Analyze reads the map again, so it sits with the rest of what the map shows.
    group.Items.AddRange(this._filesPanelToggle, this._legendToggle, this._analyzeButton);
    return group;
  }

  /// <summary>Wires a ribbon field: its tooltip, kept in <see cref="ToolStripItem.Tag"/>, and what a change does.</summary>
  private void Field(RibbonFieldItem field, string tip, Action changed) {
    field.Tag = tip;
    field.ToolTipText = tip;
    void OnChanged(object? sender, EventArgs e) {
      if (this._syncingDefragRibbon) return;
      changed();
      this.SyncDefragmentRibbon();
    }

    switch (field) {
      case RibbonSpinner spinner: spinner.ValueChanged += OnChanged; break;
      case RibbonComboBox combo: combo.SelectedIndexChanged += OnChanged; break;
    }
  }

  // The presenter reads typed values; the fields can only hold valid ones, so these never fail to parse.
  private string InterleaveText() => ((int)this._interleaveSpinner.Value).ToString(System.Globalization.CultureInfo.InvariantCulture);
  private string HoleSizeText() => $"{(long)this._holeSizeSpinner.Value}k";
  private string HoleAtText() => this._holePlacementCombo.SelectedIndex == 1
    ? ((long)this._holeOffsetSpinner.Value * 1024).ToString(System.Globalization.CultureInfo.InvariantCulture)
    : "auto";
  private string SeedText() => ((int)this._seedSpinner.Value).ToString(System.Globalization.CultureInfo.InvariantCulture);

  // ── the tab and the client area ─────────────────────────────────────────────────────────────

  /// <summary>Shows the contextual tab while there is something to maintain, and follows the target.</summary>
  private void UpdateDiskTools() {
    var running = this._presenter?.IsRunning == true || this._maintenanceCancellation is not null;
    var visible = running || this._model.HasMaintenanceTarget || (this._defragActive && this._maintenanceSession is not null);

    if (!visible && this._ribbon.SelectedTab == this._defragTab) this._ribbon.SelectedIndex = 1;
    this._diskTools.Visible = visible;

    // On the tab, a different target selected meanwhile — Back, Forward, a typed address — is taken
    // up; a target that merely vanished (a refresh that dropped the selection) keeps the session.
    if (this._defragActive && !running && this._model.MaintenanceTargetKey is { } key && key != this._maintenanceSession?.Key)
      this.OpenSessionForTarget();
  }

  /// <summary>Selects the Defragment tab, preselecting <paramref name="verb"/> when the target supports it.</summary>
  internal void OpenDefragmentTab(MaintenanceVerb? verb) {
    this.UpdateDiskTools();
    if (!this._diskTools.Visible) return;

    this._pendingVerb = verb;
    if (this._ribbon.SelectedTab == this._defragTab) this.ApplyPendingVerb();
    else this._ribbon.SelectedIndex = this._ribbon.Tabs.IndexOf(this._defragTab);
  }

  private void OnRibbonTabChanged() {
    var onTab = this._ribbon.SelectedTab == this._defragTab;
    if (onTab == this._defragActive) return;

    this._defragActive = onTab;
    if (onTab) {
      // A session still running an operation is kept, whatever is selected now: its file is in use.
      var running = this._maintenanceCancellation is not null;
      if (this._maintenanceSession is null || !running && this._model.MaintenanceTargetKey is { } key && key != this._maintenanceSession.Key)
        this.OpenSessionForTarget();
      this.ApplyPendingVerb();
    } else if (this._maintenanceCancellation is null) {
      this.CloseSession();
    }

    // The browser is hidden, not torn down, so it comes back exactly as it was left.
    this._split.Visible = !onTab;
    this._defragView.Visible = onTab;
    this.LayoutChildren();
    this.UpdateDiskTools();
  }

  private void OpenSessionForTarget() {
    this.CloseSession();
    this._defragView.Clear();

    this._maintenanceSession = this._model.OpenMaintenanceSession();
    if (this._maintenanceSession is { } session) {
      this._presenter = new MaintenancePresenter(session.ImagePath, session.FormatId) {
        InterleaveText = this.InterleaveText(),
        HoleSizeText = this.HoleSizeText(),
        HoleAtText = this.HoleAtText(),
        SeedText = this.SeedText(),
        MetadataZone = this.SelectedMetadataZone(),
      };
      this.OnLayoutProfileChanged();
      this._defragView.Log($"{session.DisplayName} — {session.FormatId}");
      this.AnalyzeTarget();
    }

    this.SyncDefragmentRibbon();
  }

  private void CloseSession() {
    this._presenter = null;
    this._maintenanceSession?.Dispose();
    this._maintenanceSession = null;
  }

  private void ApplyPendingVerb() {
    if (this._pendingVerb is { } verb && this._presenter is { } presenter && presenter.Capabilities.Verb(verb).Supported)
      presenter.Verb = verb;
    this._pendingVerb = null;
    this.SyncDefragmentRibbon();
  }

  // ── choices ─────────────────────────────────────────────────────────────────────────────────

  private void WithPresenter(Action<MaintenancePresenter> change) {
    if (this._syncingDefragRibbon || this._presenter is not { } presenter) return;
    change(presenter);
    this.SyncDefragmentRibbon();
  }

  /// <summary>The operations behave as radio buttons: clicking one picks it, clicking it again keeps it.</summary>
  private void ChooseVerb(MaintenanceVerb verb, bool on) => this.WithPresenter(p => {
    if (on) p.Verb = verb;
  });

  private void ChooseStrategy(DefragStrategy strategy, bool on) => this.WithPresenter(p => {
    if (on) p.Strategy = strategy;
  });

  private void ChooseView(BlockMapView view) {
    if (this._syncingDefragRibbon) return;
    this._defragView.Map.ViewMode = view;
    this.SyncDefragmentRibbon();
  }

  private OptimizeMethod SelectedOptimizeMethod()
    => this._presenter?.Capabilities.OptimizeMethods is { } methods && this._optimizeMethodCombo.SelectedIndex is var i && i >= 0 && i < methods.Count
      ? methods[i]
      : OptimizeMethod.Compress;

  private MetadataZone SelectedMetadataZone() => this._metadataZoneCombo.SelectedIndex switch {
    1 => MetadataZone.Front,
    2 => MetadataZone.Back,
    3 => MetadataZone.Middle,
    4 => MetadataZone.BeforeContent,
    _ => MetadataZone.Unchanged,
  };

  private void RefreshLayoutProfiles() {
    var index = this._layoutProfileCombo.SelectedIndex;
    var previous = index > 0 && index < this._layoutProfileEntries.Count ? this._layoutProfileEntries[index]?.FilePath : null;

    this._syncingDefragRibbon = true;
    try {
      this._layoutProfileEntries.Clear();
      var names = new List<string> { "(none)" };
      this._layoutProfileEntries.Add(null);
      try {
        foreach (var entry in LayoutProfileStore.List()) {
          names.Add($"{entry.Name} [{(entry.Origin == ProfileOrigin.Builtin ? "Built-in" : "User")}]");
          this._layoutProfileEntries.Add(entry);
        }
      } catch {
        // A broken profile folder leaves "(none)"; the editor reports what is wrong with it.
      }

      var keep = this._layoutProfileEntries.FindIndex(e => e is not null && string.Equals(e.FilePath, previous, StringComparison.OrdinalIgnoreCase));
      this._layoutProfileCombo.Items.Clear();
      this._layoutProfileCombo.Items.AddRange(names);
      this._layoutProfileCombo.SelectedIndex = Math.Max(0, keep);
    } finally {
      this._syncingDefragRibbon = false;
    }

    this.OnLayoutProfileChanged();
  }

  private void OnLayoutProfileChanged() {
    if (this._presenter is not { } presenter) return;

    var index = this._layoutProfileCombo.SelectedIndex;
    if (index <= 0 || index >= this._layoutProfileEntries.Count || this._layoutProfileEntries[index] is not { } entry) {
      presenter.LayoutProfile = null;
      return;
    }

    try {
      presenter.LayoutProfile = LayoutProfileStore.Load(entry);
    } catch (Exception ex) {
      presenter.LayoutProfile = null;
      MessageBox.Show(this, $"Failed to load layout profile '{entry.Name}':\n{ex.Message}", "Layout profile", MessageBoxButtons.OK, MessageBoxIcon.Error);
      this._layoutProfileCombo.SelectedIndex = 0;
    }
  }

  // ── keeping the ribbon honest ───────────────────────────────────────────────────────────────

  /// <summary>
  /// Enables, checks and explains every item from the presenter: an item is enabled only when the
  /// target supports it and the current choices make it apply, and its tooltip says why when not.
  /// </summary>
  private void SyncDefragmentRibbon() {
    if (this._syncingDefragRibbon) return;

    this._syncingDefragRibbon = true;
    try {
      var p = this._presenter;
      var caps = p?.Capabilities;
      var running = p?.IsRunning == true || this._maintenanceCancellation is not null;
      var idle = p is not null && !running;
      var defrag = p?.Verb == MaintenanceVerb.Defragment;
      const string NoTarget = "Open or select an image to maintain.";

      foreach (var (verb, toggle) in this._verbToggles) {
        var capability = caps?.Verb(verb) ?? Capability.No(NoTarget);
        toggle.Enabled = idle && capability.Supported;
        toggle.Checked = p?.Verb == verb;
        toggle.ToolTipText = $"{toggle.Text}: {capability.Reason}";
      }

      foreach (var (strategy, toggle) in this._strategyToggles) {
        var capability = caps?.Strategy(strategy) ?? Capability.No(NoTarget);
        toggle.Enabled = idle && defrag && capability.Supported;
        toggle.Checked = defrag && p!.Strategy == strategy;
        toggle.ToolTipText = $"{toggle.Text}: {(capability.Supported && !defrag ? "Applies to Defragment." : capability.Reason)}";
      }

      var consolidate = defrag && p!.Strategy == DefragStrategy.Consolidate;
      var endChoosable = caps is not null && caps.PackAtEnd.Supported && caps.PackAtStart.Supported;
      this._packAtEndToggle.Enabled = idle && consolidate && endChoosable;
      this._packAtEndToggle.Checked = consolidate && p!.EngineMode == DefragMode.ConsolidateAtEnd;
      this._packAtEndToggle.ToolTipText = $"Pack at End: {(caps is null ? NoTarget : !consolidate ? "Applies to Consolidate." : caps.PackAtEnd.Reason)}";

      var carve = defrag && p!.Strategy == DefragStrategy.CarveHole;
      foreach (var field in new RibbonFieldItem[] { this._holeSizeSpinner, this._holePlacementCombo, this._holeOffsetSpinner }) {
        field.Visible = carve;
        field.Enabled = idle;
      }
      this._holeOffsetSpinner.Enabled = idle && this._holePlacementCombo.SelectedIndex == 1;

      // The layout options shape extent moves; a directory sort moves nothing.
      var extents = defrag && p!.MovesExtents;
      Option(this._interleaveSpinner, idle && extents, caps?.Interleave, extents, appliesTo: "extent moves");
      Option(this._metadataZoneCombo, idle && extents, caps?.MetadataZone, extents, appliesTo: "extent moves");
      Option(this._layoutProfileCombo, idle && extents, caps?.LayoutProfile, extents, appliesTo: "extent moves");
      this._editProfilesButton.Enabled = idle && extents && caps?.LayoutProfile.Supported == true;

      var scramble = p?.Verb == MaintenanceVerb.Scramble;
      this._seedSpinner.Visible = scramble;
      Option(this._seedSpinner, idle && scramble, scramble ? Capability.Yes("Seeds Scramble's shuffle.") : null, scramble, appliesTo: "Scramble");
      var optimize = p?.Verb == MaintenanceVerb.Optimize;
      this._optimizeMethodCombo.Visible = optimize;
      var methods = caps?.OptimizeMethods ?? [];
      if (!this._optimizeMethodCombo.Items.Select(i => i).SequenceEqual(methods.Select(m => m.ToString()))) {
        this._optimizeMethodCombo.Items.Clear();
        this._optimizeMethodCombo.Items.AddRange(methods.Select(m => m.ToString()));
      }
      this._optimizeMethodCombo.SelectedIndex = p is null ? -1 : methods.ToList().IndexOf(p.OptimizeMethod);
      this._optimizeMethodCombo.Enabled = idle && optimize && methods.Count > 1;
      this._optimizeMethodCombo.ToolTipText = $"Method: {(caps is null ? NoTarget : caps.Verb(MaintenanceVerb.Optimize).Reason)} {this._optimizeMethodCombo.Tag}";

      var blocker = p?.StartBlocker ?? (p is null ? NoTarget : null);
      this._startButton.Enabled = idle && blocker is null;
      this._startButton.ToolTipText = blocker ?? $"{MaintenancePresenter.Label(p!.Verb!.Value)} {this._maintenanceSession?.DisplayName}";
      this._stopButton.Enabled = running && p?.CanCancel == true && !this._maintenanceCommitStarted && this._maintenanceCancellation is { IsCancellationRequested: false };
      this._analyzeButton.Enabled = idle;

      foreach (var (view, toggle) in this._viewToggles) toggle.Checked = this._defragView.Map.ViewMode == view;
      this._filesPanelToggle.Checked = this._defragView.FilesPanelVisible;
      this._legendToggle.Checked = this._defragView.LegendVisible;
    } finally {
      this._syncingDefragRibbon = false;
    }
  }

  /// <summary>Enables an option field and explains it: supported, not applicable, or refused.</summary>
  private static void Option(RibbonFieldItem field, bool applies, Capability? capability, bool relevant, string appliesTo = "Defragment") {
    field.Enabled = applies && capability?.Supported == true;
    var reason = capability is null ? "Open or select an image to maintain."
      : !relevant ? $"Applies to {appliesTo}."
      : capability.Value.Reason;
    field.ToolTipText = $"{field.Text}: {reason} {field.Tag}";
  }

  // ── running ─────────────────────────────────────────────────────────────────────────────────

  private void AnalyzeTarget() {
    if (this._presenter is not { } presenter || presenter.IsRunning) return;

    var generation = ++this._analysisGeneration;
    this._defragView.StatusText = $"Reading the layout of {this._maintenanceSession?.DisplayName}…";
    this.AnalysisTask = Task.Run(presenter.Analyze).ContinueWith(t => this.BeginInvoke(() => {
      if (generation != this._analysisGeneration || this._presenter != presenter) return;
      this._defragView.ShowSnapshot(t.IsCompletedSuccessfully ? t.Result : BlockMapSnapshot.Empty($"The layout could not be read: {t.Exception?.GetBaseException().Message}"));
    }), TaskScheduler.Default);
  }

  private void StartMaintenance() {
    if (this._presenter is not { } presenter || this._maintenanceSession is not { } session) return;
    if (presenter.StartBlocker is { } blocker) {
      this._model.StatusText = blocker;
      return;
    }

    if (presenter.ConfirmationFor() is { } question
        && MessageBox.Show(this, question, $"Confirm {presenter.Verb}", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
      return;

    ++this._analysisGeneration; // a pending analysis must not paint over the live run
    this._maintenanceCancellation = new();
    this._maintenanceCommitStarted = false;
    this._defragView.ProgressValue = 0;

    var run = presenter.RunAsync(
      progress => this.BeginInvoke(() => this.OnMaintenanceProgress(progress)),
      line => this.BeginInvoke(() => this._defragView.Log(line)),
      this._maintenanceCancellation.Token);
    this.SyncDefragmentRibbon();
    this.MaintenanceTask = run.ContinueWith(t => this.BeginInvoke(() => this.OnMaintenanceCompleted(presenter, session, t.Result)), TaskScheduler.Default);
  }

  private void OnMaintenanceProgress(MaintenanceProgress progress) {
    if (progress.CommitStarted) this._maintenanceCommitStarted = true;
    this._defragView.Apply(progress);
    this.SyncDefragmentRibbon();
  }

  private void OnMaintenanceCompleted(MaintenancePresenter presenter, MaintenanceSession session, MaintenanceOutcome outcome) {
    this._maintenanceCancellation?.Dispose();
    this._maintenanceCancellation = null;
    this._maintenanceCommitStarted = false;
    this._defragView.ProgressValue = 100;

    // Re-listing the archive rewrites the status line, so the outcome is reported after it.
    if (outcome.Mutated) session.NotifyMutated();

    var name = session.DisplayName;
    this._model.StatusText = outcome.Kind switch {
      MaintenanceOutcomeKind.Succeeded => $"{presenter.Verb} finished on {name}: {outcome.Summary}",
      MaintenanceOutcomeKind.Refused => $"{presenter.Verb} refused on {name}; the image was left unchanged. {outcome.Summary}",
      MaintenanceOutcomeKind.Cancelled => $"{presenter.Verb} stopped on {name}. {outcome.Summary}",
      _ => $"{presenter.Verb} failed on {name}: {outcome.Summary}",
    };

    if (outcome.Kind == MaintenanceOutcomeKind.Refused)
      MessageBox.Show(this, $"{outcome.Summary}\n\nThe image was left unchanged.", $"{presenter.Verb} not supported", MessageBoxButtons.OK, MessageBoxIcon.Information);
    else if (outcome.Kind == MaintenanceOutcomeKind.Failed)
      MessageBox.Show(this, outcome.Summary, $"{presenter.Verb} failed", MessageBoxButtons.OK, MessageBoxIcon.Error);

    if (this._defragActive && this._presenter == presenter) this.AnalyzeTarget();
    else if (!this._defragActive && this._maintenanceSession == session) this.CloseSession();

    this.UpdateDiskTools();
    this.SyncDefragmentRibbon();
  }

  private void StopMaintenance() {
    if (this._presenter is not { } presenter || this._maintenanceCancellation is not { IsCancellationRequested: false } cancellation) return;
    if (this._maintenanceCommitStarted) return;

    if (!presenter.IsStaged
        && MessageBox.Show(this,
          $"Stop {presenter.Verb}?\n\nThis operation moves data in place. Moves already completed are not rolled back; it stops at the next safe "
          + "boundary, so the layout may be partly changed although the contents stay intact.",
          "Stop in-place maintenance", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
      return;

    this._defragView.Log(presenter.IsStaged
      ? "Stop requested — the staged copy will be discarded; the image stays as it is."
      : "Stop requested — moves already completed stay where they are.");
    cancellation.Cancel();
    this.SyncDefragmentRibbon();
  }

  private void OnClosingWhileMaintaining(object? sender, FormClosingEventArgs e) {
    if (e.Cancel || this._maintenanceCancellation is null) return;

    // Closing mid-run would cut an operation off between two writes. Ask it to stop instead; the
    // window can close once it has.
    e.Cancel = true;
    if (!this._maintenanceCancellation.IsCancellationRequested && this._presenter?.CanCancel == true) this._maintenanceCancellation.Cancel();
    this._model.StatusText = "An operation is still running; it stops at the next safe point, then the window can close.";
  }
}
