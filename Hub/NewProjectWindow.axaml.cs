using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace EmpireHub;

// Asks for a name, a location and an editor version, then creates the project. Closes with the new folder's path,
// or null when cancelled.
public partial class NewProjectWindow : Window
{
    public NewProjectWindow() : this(new List<EditorInstall>(), "") { }   // for the XAML previewer

    public NewProjectWindow(IReadOnlyList<EditorInstall> installs, string location)
    {
        InitializeComponent();
        LocationBox.Text = location;
        VersionBox.ItemsSource = installs;
        VersionBox.SelectedIndex = installs.Count > 0 ? 0 : -1;
        VersionBox.IsEnabled = installs.Count > 0;
        Validate();
        Opened += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    private void OnInputChanged(object? sender, TextChangedEventArgs e) => Validate();

    private bool Validate()
    {
        if (NameBox is null || LocationBox is null || CreateButton is null) return false;   // during InitializeComponent

        string name = NameBox.Text ?? "";
        string location = LocationBox.Text ?? "";
        string? error = ProjectTemplate.Validate(name, location);
        PathPreview.Text = error is null ? Path.Combine(location, name) : "";
        ErrorText.Text = error ?? "";
        CreateButton.IsEnabled = error is null;
        return error is null;
    }

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        IStorageFolder? start = null;
        if (Directory.Exists(LocationBox.Text))
            start = await StorageProvider.TryGetFolderFromPathAsync(LocationBox.Text);

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Where should the project go?",
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            LocationBox.Text = path;
    }

    private void OnCreate(object? sender, RoutedEventArgs e)
    {
        if (!Validate()) return;
        try
        {
            string version = (VersionBox.SelectedItem as EditorInstall)?.Version ?? "";
            Close(ProjectTemplate.Create(NameBox.Text!, LocationBox.Text!, version.Length == 0 ? null : version));
        }
        catch (Exception ex)
        {
            ErrorText.Text = $"Could not create the project: {ex.Message}";
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
