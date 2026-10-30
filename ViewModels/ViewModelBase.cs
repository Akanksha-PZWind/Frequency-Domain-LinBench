using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace LinBench.Core.ViewModels
{
    /// <summary>
    /// Helper to run code on the WPF UI thread. ObservableCollection changes and
    /// ICommand.CanExecuteChanged MUST happen on the UI thread, otherwise WPF throws
    /// (which was silently swallowed before and kept "Run analysis" disabled).
    /// </summary>
    public static class Ui
    {
        public static void Run(Action action)
        {
            var d = Application.Current?.Dispatcher;
            if (d == null || d.CheckAccess()) action();
            else d.Invoke(action);
        }

        public static void Post(Action action)
        {
            var d = Application.Current?.Dispatcher;
            if (d == null || d.CheckAccess()) action();
            else d.BeginInvoke(action);
        }
    }

    public abstract class ViewModelBase : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
        {
            if (Equals(storage, value))
                return false;

            storage = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        private readonly Predicate<object?>? _canExecute;

        public event EventHandler? CanExecuteChanged;

        public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public RelayCommand(Action execute, Func<bool>? canExecute = null)
            : this(_ => execute(), canExecute != null ? _ => canExecute() : null)
        {
        }

        public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

        public void Execute(object? parameter) => _execute(parameter);

        // CHANGED: always raise on the UI thread (safe to call from any thread)
        public void RaiseCanExecuteChanged() => Ui.Post(() => CanExecuteChanged?.Invoke(this, EventArgs.Empty));
    }

    public class AsyncRelayCommand : ICommand
    {
        private readonly Func<object?, Task> _execute;
        private readonly Predicate<object?>? _canExecute;
        private bool _isExecuting;

        public event EventHandler? CanExecuteChanged;

        public AsyncRelayCommand(Func<object?, Task> execute, Predicate<object?>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
            : this(_ => execute(), canExecute != null ? _ => canExecute() : null)
        {
        }

        public bool IsExecuting => _isExecuting;

        public bool CanExecute(object? parameter)
        {
            return !_isExecuting && (_canExecute?.Invoke(parameter) ?? true);
        }

        public async void Execute(object? parameter)
        {
            if (!CanExecute(parameter)) return;
            try
            {
                _isExecuting = true;
                RaiseCanExecuteChanged();
                await _execute(parameter);
            }
            catch (Exception ex)
            {
                // CHANGED: never let an async void command crash the app
                System.Diagnostics.Debug.WriteLine($"[AsyncRelayCommand] {ex}");
            }
            finally
            {
                _isExecuting = false;
                RaiseCanExecuteChanged();
            }
        }

        // CHANGED: always raise on the UI thread (safe to call from any thread)
        public void RaiseCanExecuteChanged() => Ui.Post(() => CanExecuteChanged?.Invoke(this, EventArgs.Empty));
    }
}
