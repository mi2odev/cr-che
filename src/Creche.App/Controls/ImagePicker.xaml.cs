using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Creche.App.Services;
using Microsoft.Win32;

namespace Creche.App.Controls;

/// <summary>
/// Image upload: click to browse or drag a file in. The picture is resized and copied into the application's
/// images folder; <see cref="FileName"/> holds the stored file name.
/// </summary>
public partial class ImagePicker : UserControl
{
    public static readonly DependencyProperty FileNameProperty = DependencyProperty.Register(
        nameof(FileName), typeof(string), typeof(ImagePicker),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((ImagePicker)d).Refresh()));

    public static readonly DependencyProperty PreviewSizeProperty = DependencyProperty.Register(
        nameof(PreviewSize), typeof(double), typeof(ImagePicker), new PropertyMetadata(132.0));

    public static readonly DependencyProperty MaxPixelsProperty = DependencyProperty.Register(
        nameof(MaxPixels), typeof(int), typeof(ImagePicker), new PropertyMetadata(512));

    public ImagePicker()
    {
        InitializeComponent();
        Refresh();
    }

    public string? FileName
    {
        get => (string?)GetValue(FileNameProperty);
        set => SetValue(FileNameProperty, value);
    }

    public double PreviewSize
    {
        get => (double)GetValue(PreviewSizeProperty);
        set => SetValue(PreviewSizeProperty, value);
    }

    /// <summary>Longest side of the stored picture, in pixels.</summary>
    public int MaxPixels
    {
        get => (int)GetValue(MaxPixelsProperty);
        set => SetValue(MaxPixelsProperty, value);
    }

    private void Refresh()
    {
        var image = ImageStore.Load(FileName, 400);
        preview.Background = image is null ? null : new ImageBrush(image) { Stretch = Stretch.UniformToFill };
        preview.Visibility = image is null ? Visibility.Collapsed : Visibility.Visible;
        placeholder.Visibility = image is null ? Visibility.Visible : Visibility.Collapsed;
        removeButton.Visibility = image is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Import(string path)
    {
        try
        {
            FileName = ImageStore.Import(path, MaxPixels);
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            App.Toasts.Error(Loc.T("Image.Invalid"));
        }
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = ImageStore.DialogFilter, CheckFileExists = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) Import(dialog.FileName);
        e.Handled = true;
    }

    private void OnRemove(object sender, RoutedEventArgs e) => FileName = null;

    protected override void OnDragEnter(DragEventArgs e) => OnDragOver(e);

    protected override void OnDragOver(DragEventArgs e)
    {
        bool ok = DroppedImage(e) is not null;
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        dropZone.BorderBrush = (Brush)FindResource(ok ? "Accent" : "Border");
        e.Handled = true;
    }

    protected override void OnDragLeave(DragEventArgs e) => dropZone.BorderBrush = (Brush)FindResource("Border");

    protected override void OnDrop(DragEventArgs e)
    {
        dropZone.BorderBrush = (Brush)FindResource("Border");
        if (DroppedImage(e) is { } path) Import(path);
        e.Handled = true;
    }

    private static string? DroppedImage(DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files) return null;
        return ImageStore.IsImage(files[0]) ? files[0] : null;
    }
}
