using System.Text;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using FlashProof.ViewModels;

namespace FlashProof;

public partial class MainWindow : Window, IUserFiles
{
    public MainWindow() => InitializeComponent();

    /// <summary>During a test the window asks before closing. Windows shutting down isn't asked about: the test then ends as "app closed".</summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (e.CloseReason != WindowCloseReason.OSShutdown && DataContext is MainViewModel main && !main.CanClose())
        {
            e.Cancel = true;
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            Activate();
        }
        base.OnClosing(e);
    }

    public async Task CopyAsync(string text)
    {
        if (Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text);
    }

    /// <summary>Saves where the user chooses, as UTF-8 text.</summary>
    public async Task<string?> SaveAsync(string suggestedName, string text)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = suggestedName,
            DefaultExtension = "txt",
            FileTypeChoices = [new FilePickerFileType("Text") { Patterns = ["*.txt"] }],
        });
        if (file is null)
            return null;
        await using var stream = await file.OpenWriteAsync();
        await using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true))
            await writer.WriteAsync(text);
        if (stream.CanSeek)
            stream.SetLength(stream.Position); // a shorter report over a longer file leaves nothing behind
        return file.Name;
    }
}
