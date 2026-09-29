namespace Compression.NativeUI.ViewModels;

/// <summary>
/// The command contract the view models bind against. NativeForms ships its own
/// <see cref="Hawkynt.NativeForms.ComponentModel.RelayCommand"/>, but it is parameterless and
/// re-queries only when told to; the view models were written against a parameterised command whose
/// enabled state is recomputed by the shell, so that shape is kept here.
/// </summary>
internal interface ICommand {
  event EventHandler? CanExecuteChanged;
  bool CanExecute(object? parameter);
  void Execute(object? parameter);
}

/// <summary>
/// Stands in for WPF's <c>CommandManager</c>. WPF re-queried every command after any input event;
/// NativeForms has no such ambient signal, so the shell raises <see cref="InvalidateRequerySuggested"/>
/// explicitly at the same moments — selection changes, archive state changes, and command completion.
/// </summary>
internal static class CommandManager {
  public static event EventHandler? RequerySuggested;

  public static void InvalidateRequerySuggested() => RequerySuggested?.Invoke(null, EventArgs.Empty);
}

/// <summary>
/// Simple <see cref="ICommand"/> implementation for MVVM binding.
/// </summary>
internal sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand {
  public event EventHandler? CanExecuteChanged {
    add => CommandManager.RequerySuggested += value;
    remove => CommandManager.RequerySuggested -= value;
  }

  public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
  public void Execute(object? parameter) => execute(parameter);
}

/// <summary>
/// Async-aware relay command that prevents re-entrance.
/// </summary>
internal sealed class AsyncRelayCommand : ICommand {
  private readonly Func<object?, Task> _execute;
  private readonly Func<object?, bool>? _canExecute;
  private bool _isRunning;

  internal AsyncRelayCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null) {
    this._execute = execute;
    this._canExecute = canExecute;
  }

  public event EventHandler? CanExecuteChanged {
    add => CommandManager.RequerySuggested += value;
    remove => CommandManager.RequerySuggested -= value;
  }

  public bool CanExecute(object? parameter) => !this._isRunning && (this._canExecute?.Invoke(parameter) ?? true);

  public async void Execute(object? parameter) {
    if (this._isRunning) return;
    this._isRunning = true;
    CommandManager.InvalidateRequerySuggested();
    try { await this._execute(parameter); }
    finally {
      this._isRunning = false;
      CommandManager.InvalidateRequerySuggested();
    }
  }
}
