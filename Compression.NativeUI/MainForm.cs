using Compression.Lib;
using Compression.Mounting;
using Compression.Registry;
using Hawkynt.NativeForms;

namespace Compression.NativeUI;

internal sealed class MainForm : Form {
  private readonly IFilesystemMountBackend[] _backends;
  private readonly MountBackendRegistry _mountBackends;
  private readonly IMountLauncher? _mountLauncher;

  private readonly FilePicker _imagePicker = new() {
    Bounds = new(132, 24, 544, 28),
    Filter = "All files|*.*",
    PlaceholderText = "Archive, filesystem or disk image",
    Title = "Select a source to mount",
  };

  private readonly ComboBox _accessPicker = new() { Bounds = new(132, 68, 190, 28) };
  private readonly ComboBox _backendPicker = new() { Bounds = new(410, 68, 266, 28) };
  private readonly TextBox _targetBox = new() {
    Bounds = new(132, 112, 544, 28),
    PlaceholderText = "Drive letter or mountpoint",
  };
  private readonly Button _probeButton = new() { Bounds = new(132, 156, 118, 32), Text = "Probe" };
  private readonly Button _mountButton = new() { Bounds = new(262, 156, 118, 32), Text = "Mount", Enabled = false };
  private readonly Button _unmountButton = new() { Bounds = new(392, 156, 118, 32), Text = "Unmount", Enabled = false };
  private readonly TextBox _detailsBox = new() { Bounds = new(24, 224, 652, 280), Multiline = true, ReadOnly = true };
  private readonly Label _statusLabel = new() {
    Bounds = new(24, 520, 652, 28),
    Text = "Select a source and probe its mount capabilities.",
  };

  private FilesystemDriverProfile? _driverProfile;
  private IReadOnlyList<string> _mountLayers = [];
  private string? _formatId;
  private bool _sourceCanWrite;
  private IMountSession? _mountSession;
  private bool _busy;

  public MainForm(IEnumerable<IFilesystemMountBackend> backends, IMountLauncher? mountLauncher = null) {
    ArgumentNullException.ThrowIfNull(backends);
    this._backends = backends.ToArray();
    this._mountBackends = new(this._backends);
    this._mountLauncher = mountLauncher;

    this.Text = "CompressionWorkbench — Mount";
    this.Bounds = new(0, 0, 720, 600);
    this.StartPosition = FormStartPosition.CenterScreen;
    this.MinimumSize = new(720, 600);

    this.Controls.AddRange(
      new Label { Bounds = new(24, 28, 96, 24), Text = "Source" }, this._imagePicker,
      new Label { Bounds = new(24, 72, 96, 24), Text = "Access" }, this._accessPicker,
      new Label { Bounds = new(338, 72, 64, 24), Text = "Backend" }, this._backendPicker,
      new Label { Bounds = new(24, 116, 96, 24), Text = "Target" }, this._targetBox,
      this._probeButton, this._mountButton, this._unmountButton,
      new Label { Bounds = new(24, 200, 160, 24), Text = "Resolved capabilities" },
      this._detailsBox, this._statusLabel
    );

    this._accessPicker.DisplaySelector = static item => item switch {
      MountAccessMode.ReadOnly => "Read-only",
      MountAccessMode.ReadWrite => "Read-write",
      _ => string.Empty,
    };
    this._accessPicker.Items.Add(MountAccessMode.ReadOnly);
    this._accessPicker.Items.Add(MountAccessMode.ReadWrite);
    this._accessPicker.SelectedIndex = 0;

    this._backendPicker.DisplaySelector = static item => item is IFilesystemMountBackend backend
      ? backend.GetProfile().DisplayName
      : string.Empty;
    foreach (var backend in this._backends)
      this._backendPicker.Items.Add(backend);

    if (this._backends.Length > 0)
      this._backendPicker.SelectedIndex = 0;
    else {
      this._backendPicker.Enabled = false;
      this._backendPicker.PlaceholderText = "No mount backend registered";
    }

    this._imagePicker.PathChanged += (_, _) => this.ResetProbe();
    this._accessPicker.SelectedIndexChanged += (_, _) => this.RefreshPlan();
    this._backendPicker.SelectedIndexChanged += (_, _) => this.RefreshPlan();
    this._targetBox.TextChanged += (_, _) => this.RefreshMountButton();
    this._probeButton.Click += (_, _) => this.Probe();
    this._mountButton.Click += async (_, _) => await this.MountAsync();
    this._unmountButton.Click += async (_, _) => await this.UnmountAsync();
    this.FormClosing += (_, _) => this.CleanupActiveMount();
  }

  private string ImagePath => string.IsNullOrWhiteSpace(this._imagePicker.SelectedPath)
    ? this._imagePicker.Text.Trim()
    : this._imagePicker.SelectedPath;

  private MountAccessMode AccessMode
    => this._accessPicker.SelectedItem is MountAccessMode mode ? mode : MountAccessMode.ReadOnly;

  private IFilesystemMountBackend? SelectedBackend
    => this._backendPicker.SelectedItem as IFilesystemMountBackend;

  private void ResetProbe() {
    if (this._mountSession is not null) return;
    this._driverProfile = null;
    this._mountLayers = [];
    this._formatId = null;
    this._sourceCanWrite = false;
    this._detailsBox.Text = string.Empty;
    this._statusLabel.Text = "Source changed; probe again.";
    this.RefreshMountButton();
  }

  private void Probe() {
    if (this._mountSession is not null || this._busy) return;
    var path = this.ImagePath;
    if (!File.Exists(path)) {
      this.ShowProbeFailure("The selected source does not exist.");
      return;
    }

    try {
      FormatRegistration.EnsureInitialized();
      var detected = FormatDetector.DetectByExtension(path);
      if (detected == FormatDetector.Format.Unknown) {
        this.ShowProbeFailure("No registered format could be detected for this file.");
        return;
      }

      var formatId = detected.ToString();
      var descriptor = FormatRegistry.GetById(formatId);
      if (descriptor is null) {
        this.ShowProbeFailure($"Detected format '{formatId}' has no registered descriptor.");
        return;
      }

      using var probeSource = OpenProbeSource(path, out var sourceCanWrite);
      var probe = MountNamespaceResolver.Probe(formatId, probeSource);
      this._formatId = formatId;
      this._driverProfile = probe.Profile;
      this._mountLayers = probe.Layers;
      this._sourceCanWrite = sourceCanWrite;
      this._statusLabel.Text = $"Detected {descriptor.DisplayName}: {probe.Profile.ProfileName}.";
      this.RefreshPlan();
    } catch (Exception ex) {
      this.ShowProbeFailure($"Probe failed: {ex.GetType().Name}: {ex.Message}");
    }
  }

  private void RefreshPlan() {
    if (this._driverProfile is not { } profile || this._formatId is null) {
      this.RefreshMountButton();
      return;
    }

    var lines = new List<string> {
      $"Format: {this._formatId}",
      $"Resolved profile: {profile.FormatId} / {profile.ProfileName}",
      $"Mutation model: {profile.MutationModel}",
      $"Backing source writable: {this._sourceCanWrite}",
      $"Can mount: {profile.CanMount}",
      $"Can mount writable: {profile.CanMountWritable}",
      $"Driver capabilities: {profile.Capabilities}",
    };

    if (this._mountLayers.Count > 0) {
      lines.Add(string.Empty);
      lines.Add("Userspace resolution chain:");
      lines.AddRange(this._mountLayers.Select(static layer => $"- {layer}"));
    }

    if (profile.Limitations.Count > 0) {
      lines.Add(string.Empty);
      lines.Add("Namespace limitations:");
      lines.AddRange(profile.Limitations.Select(static limitation => $"- {limitation}"));
    }

    if (this.SelectedBackend is { } backend) {
      var backendProfile = backend.GetProfile();
      var plan = this._mountBackends.ResolveFilesystem(backendProfile.Id, profile, this.AccessMode, this._sourceCanWrite);
      lines.Add(string.Empty);
      lines.Add($"Selected backend: {backendProfile.DisplayName}");
      lines.Add($"Selected access: {this.AccessMode}");
      lines.Add($"Supported: {plan.IsSupported}");
      if (plan.Reasons.Count > 0) {
        lines.Add("Reasons:");
        lines.AddRange(plan.Reasons.Select(static reason => $"- {reason.Message}"));
      }
      if (plan.Limitations.Count > 0) {
        lines.Add("Backend/profile limitations:");
        lines.AddRange(plan.Limitations.Select(static limitation => $"- {limitation}"));
      }
    } else {
      lines.Add(string.Empty);
      lines.Add("No mount backend is registered in this build yet.");
    }

    if (this._mountLauncher is null) {
      lines.Add(string.Empty);
      lines.Add("No mount launcher is composed yet; probing and capability resolution are available, mounting stays disabled.");
    }

    this._detailsBox.Text = string.Join(Environment.NewLine, lines);
    this.RefreshMountButton();
  }

  private MountPlan? CurrentPlan() {
    if (this._driverProfile is not { } profile || this.SelectedBackend is not { } backend) return null;
    return this._mountBackends.ResolveFilesystem(backend.GetProfile().Id, profile, this.AccessMode, this._sourceCanWrite);
  }

  private void RefreshMountButton() {
    var plan = this.CurrentPlan();
    this._mountButton.Enabled = !this._busy
      && this._mountSession is null
      && this._mountLauncher is not null
      && plan?.IsSupported == true
      && !string.IsNullOrWhiteSpace(this._targetBox.Text);
    this._unmountButton.Enabled = !this._busy && this._mountSession?.IsMounted == true;
  }

  private async Task MountAsync() {
    if (this._mountSession is not null || this._busy || this._mountLauncher is null) return;
    var plan = this.CurrentPlan();
    var formatId = this._formatId;
    var target = this._targetBox.Text.Trim();
    if (plan?.IsSupported != true || formatId is null || target.Length == 0) return;

    try {
      this.SetBusy(true, $"Mounting at {target}...");
      this._mountSession = await this._mountLauncher.MountAsync(this.ImagePath, formatId, plan, target);
      this._statusLabel.Text = $"Mounted at {this._mountSession.Target} via {this._mountSession.BackendId}.";
    } catch (Exception ex) {
      this._statusLabel.Text = $"Mount failed: {ex.GetType().Name}: {ex.Message}";
    } finally {
      this.SetBusy(false, this._statusLabel.Text);
    }
  }

  private async Task UnmountAsync() {
    if (this._mountSession is not { } session || this._busy) return;
    try {
      this.SetBusy(true, $"Unmounting {session.Target}...");
      if (session.IsMounted) await session.FlushAsync();
      if (session.IsMounted) await session.UnmountAsync();
      await session.DisposeAsync();
      this._mountSession = null;
      this._statusLabel.Text = "Unmounted.";
    } catch (Exception ex) {
      this._statusLabel.Text = $"Unmount failed: {ex.GetType().Name}: {ex.Message}";
    } finally {
      this.SetBusy(false, this._statusLabel.Text);
    }
  }

  private void CleanupActiveMount() {
    if (this._mountSession is not { } session) return;
    try {
      if (session.IsMounted) session.FlushAsync().AsTask().GetAwaiter().GetResult();
      if (session.IsMounted) session.UnmountAsync().AsTask().GetAwaiter().GetResult();
      session.DisposeAsync().AsTask().GetAwaiter().GetResult();
    } catch {
      // Best-effort process teardown; forced-unmount policy remains backend-specific.
    } finally {
      this._mountSession = null;
    }
  }

  private void SetBusy(bool busy, string status) {
    this._busy = busy;
    this._probeButton.Enabled = !busy && this._mountSession is null;
    this._imagePicker.Enabled = !busy && this._mountSession is null;
    this._accessPicker.Enabled = !busy && this._mountSession is null;
    this._backendPicker.Enabled = !busy && this._mountSession is null && this._backends.Length > 0;
    this._targetBox.Enabled = !busy && this._mountSession is null;
    this._statusLabel.Text = status;
    this.RefreshMountButton();
  }

  private void ShowProbeFailure(string message) {
    this._driverProfile = null;
    this._mountLayers = [];
    this._formatId = null;
    this._sourceCanWrite = false;
    this._detailsBox.Text = message;
    this._statusLabel.Text = message;
    this.RefreshMountButton();
  }

  private static FileStream OpenProbeSource(string path, out bool canWrite) {
    try {
      var source = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
      canWrite = true;
      return source;
    } catch (UnauthorizedAccessException) {
      canWrite = false;
      return new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    } catch (IOException) {
      canWrite = false;
      return new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    }
  }
}
