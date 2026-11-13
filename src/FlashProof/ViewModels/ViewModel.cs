using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace FlashProof.ViewModels;

public abstract class ViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        Raise(name);
    }

    /// <summary>A property changed; an empty name means all of them.</summary>
    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>One screen of the window (design §13: one screen at a time).</summary>
public abstract class Screen : ViewModel
{
    /// <summary>The coordinator changed: refresh everything shown.</summary>
    public virtual void Update() => Raise(string.Empty);
}

/// <summary>A button's action. Whether it's enabled or shown is bound separately.</summary>
public sealed class Command(Action execute) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => execute();
}

/// <summary>What the window does for a screen: the clipboard and the save dialog (design §9: export).</summary>
public interface IUserFiles
{
    Task CopyAsync(string text);

    /// <summary>Asks the user where to save; returns the file's name, or null if they cancelled.</summary>
    Task<string?> SaveAsync(string suggestedName, string text);
}
