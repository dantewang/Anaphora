using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;

namespace Anaphora.App.Editor;

internal sealed partial class MainWindow : Window, IFileAccess
{
    private static readonly FilePickerFileType RotationFiles = new("轴（JSON）") { Patterns = ["*.json"] };

    public MainWindow()
    {
        InitializeComponent();
    }

    public MainWindow(MainViewModel model)
        : this()
    {
        DataContext = model;
        model.AttachFiles(this);
    }

    /// <summary>Closing only hides: the app lives in the tray, and unsaved edits stay in memory.</summary>
    public bool ReallyClose { get; set; }

    public async Task<string?> OpenTextAsync()
    {
        IReadOnlyList<IStorageFile> picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "导入轴",
            AllowMultiple = false,
            FileTypeFilter = [RotationFiles],
        });

        if (picked.Count == 0)
        {
            return null;
        }

        await using Stream stream = await picked[0].OpenReadAsync();
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    public async Task<bool> SaveTextAsync(string suggestedName, string text)
    {
        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出轴",
            SuggestedFileName = suggestedName,
            DefaultExtension = "json",
            FileTypeChoices = [RotationFiles],
        });

        if (file is null)
        {
            return false;
        }

        await using Stream stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(text);
        return true;
    }

    public async Task<string?> ReadClipboardAsync() =>
        Clipboard is { } clipboard ? await clipboard.TryGetTextAsync() : null;

    public async Task WriteClipboardAsync(string text)
    {
        if (Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (!ReallyClose)
        {
            e.Cancel = true;
            Hide();
        }
    }

    /// <summary>Records a key combination instead of typing it. Backspace or Delete clears it.</summary>
    private void OnHotkeyKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel model)
        {
            return;
        }

        // Which box: the Tag says which setting this one edits.
        bool move = (sender as Control)?.Tag as string == "move";
        void Set(string value)
        {
            if (move)
            {
                model.Settings.MoveHotkey = value;
            }
            else
            {
                model.Settings.ResetHotkey = value;
            }
        }

        e.Handled = true;
        if (e.Key is Key.Back or Key.Delete)
        {
            Set(string.Empty);
            return;
        }

        string? key = e.Key switch
        {
            >= Key.A and <= Key.Z => e.Key.ToString(),
            >= Key.D0 and <= Key.D9 => ((char)('0' + (e.Key - Key.D0))).ToString(),
            >= Key.F1 and <= Key.F24 => e.Key.ToString(),
            _ => null,
        };

        if (key is null)
        {
            // A modifier on its own, or a key RegisterHotKey is not offered here.
            return;
        }

        var parts = new List<string>();
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Meta))
        {
            parts.Add("Win");
        }

        parts.Add(key);
        Set(string.Join('+', parts));
    }
}

/// <summary>Checks an int against the converter parameter; a checked radio button writes the parameter back.</summary>
internal sealed class IntEqualsConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int number && Parse(parameter) == number;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && Parse(parameter) is int number ? number : BindingOperations.DoNothing;

    private static int? Parse(object? parameter) =>
        int.TryParse(parameter?.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out int number) ? number : null;
}
