using System.Windows.Input;

namespace Compression.UI.ViewModels;

/// <summary>
/// Simple ICommand implementation for MVVM binding.
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
    _execute = execute;
    _canExecute = canExecute;
  }

  public event EventHandler? CanExecuteChanged {
    add => CommandManager.RequerySuggested += value;
    remove => CommandManager.RequerySuggested -= value;
  }

  public bool CanExecute(object? parameter) => !_isRunning && (_canExecute?.Invoke(parameter) ?? true);

  public async void Execute(object? parameter) {
    if (_isRunning) return;
    _isRunning = true;
    CommandManager.InvalidateRequerySuggested();
    try { await _execute(parameter); }
    finally {
      _isRunning = false;
      CommandManager.InvalidateRequerySuggested();
    }
  }
}
