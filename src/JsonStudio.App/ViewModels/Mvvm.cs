using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using JsonStudio.Core.Editing;
using JsonStudio.Core.Model;

namespace JsonStudio.App.ViewModels;

/// <summary>Minimal INotifyPropertyChanged base.</summary>
public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

/// <summary>Minimal ICommand.</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute == null ? null : _ => canExecute())
    {
    }

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter)
    {
        if (CanExecute(parameter)) _execute(parameter);
    }
}

/// <summary>
/// A reference to a node that survives undo/redo (same id) and code edits (same path).
/// </summary>
public sealed class NodeRef
{
    private NodeRef(JsonNode node)
    {
        Id = node.Id;
        Path = NodePath.Segments(node);
    }

    public long Id { get; private set; }
    public List<PathSegment> Path { get; private set; }

    public static NodeRef Of(JsonNode node) => new(node);

    public JsonNode? Resolve(DocumentEditor editor)
    {
        var n = editor.FindById(Id) ?? NodePath.Resolve(editor.Root, Path);
        if (n != null)
        {
            Id = n.Id;
            Path = NodePath.Segments(n);
        }
        return n;
    }

    /// <summary>Re-reads the path of a node that is still in the tree.</summary>
    public void Refresh(DocumentEditor editor)
    {
        var n = editor.FindById(Id);
        if (n != null) Path = NodePath.Segments(n);
    }
}
