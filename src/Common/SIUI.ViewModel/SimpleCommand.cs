﻿using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Utils;

namespace SIUI.ViewModel;

/// <summary>
/// Упрощённая реализация команды
/// </summary>
public class SimpleCommand : ICommand, INotifyPropertyChanged
{
    private bool _canBeExecuted = true;

    private readonly Action<object?>? _action = null;

    /// <summary>
    /// Можно ли выполнить команду в настоящий момент
    /// </summary>
    public bool CanBeExecuted
    {
        get => _canBeExecuted;
        set
        {
            if (_canBeExecuted != value)
            {
                _canBeExecuted = value;

                if (CanExecuteChanged != null)
                {
                    if (SynchronizationContext.Current == null)
                    {
                        Task.Factory.StartNew(
                            () => CanExecuteChanged(this, EventArgs.Empty),
                            CancellationToken.None,
                            TaskCreationOptions.None,
                            UI.Scheduler);
                    }
                    else
                    {
                        CanExecuteChanged(this, EventArgs.Empty);
                    }
                }

                OnPropertyChanged();
            }
        }
    }

    public bool CanExecute(object? parameter) => _canBeExecuted;

    /// <summary>
    /// Возможность выполнения команды изменилась
    /// </summary>
    public event EventHandler? CanExecuteChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Execute(object? parameter) => _action?.Invoke(parameter);

    public SimpleCommand(Action<object?> action) => _action = action;
}
