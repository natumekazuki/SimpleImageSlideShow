using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Timers;
using SimpleImageSlideShow.Services;
using Microsoft.Maui.Controls;

namespace SimpleImageSlideShow;

public partial class MainPage : ContentPage
{
    private readonly IFolderPicker _folderPicker;
    private readonly List<string> _images = new();
    private readonly Random _random = new();
    private int _index = 0;
    private int _nextPattern = 0;
    private List<ImageSource>? _nextSources;
    private System.Timers.Timer? _timer;
    private bool _initialized;

    public MainPage(IFolderPicker folderPicker)
    {
        InitializeComponent();
        _folderPicker = folderPicker;
    }

    public MainPage() : this((IFolderPicker)MauiProgram.Services.GetService(typeof(IFolderPicker))!)
    {
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_initialized) return;
        _initialized = true;

        var folder = await _folderPicker.PickFolderAsync();
        if (string.IsNullOrEmpty(folder))
        {
            await DisplayAlert("Folder", "Folder not selected", "OK");
            return;
        }

        _images.AddRange(Directory.GetFiles(folder, "*.*", SearchOption.AllDirectories)
            .Where(IsImageFile));

        if (_images.Count == 0)
        {
            await DisplayAlert("Images", "No images found", "OK");
            return;
        }

        PrepareNext();
        ShowNext();

        _timer = new System.Timers.Timer(5000);
        _timer.Elapsed += (s, e) => MainThread.BeginInvokeOnMainThread(() =>
        {
            ShowNext();
            PrepareNext();
        });
        _timer.Start();
    }

    private static bool IsImageFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext == ".jpg" || ext == ".jpeg" || ext == ".png" || ext == ".bmp" || ext == ".gif";
    }

    private void PrepareNext()
    {
        _nextPattern = _random.Next(0, 3); // 0 single, 1 two, 2 four
        int count = _nextPattern switch { 0 => 1, 1 => 2, _ => 4 };
        var sources = new List<ImageSource>();
        for (int i = 0; i < count; i++)
        {
            if (_index >= _images.Count) _index = 0;
            var file = _images[_index++];
            sources.Add(ImageSource.FromFile(file));
        }
        _nextSources = sources;
    }

    private void ShowNext()
    {
        if (_nextSources == null)
            return;

        SlideShowGrid.Children.Clear();
        SlideShowGrid.RowDefinitions.Clear();
        SlideShowGrid.ColumnDefinitions.Clear();

        switch (_nextPattern)
        {
            case 0:
                var single = new Image { Source = _nextSources[0], Aspect = Aspect.AspectFit };
                SlideShowGrid.Add(single);
                break;
            case 1:
                SlideShowGrid.ColumnDefinitions.Add(new ColumnDefinition());
                SlideShowGrid.ColumnDefinitions.Add(new ColumnDefinition());
                for (int i = 0; i < 2; i++)
                {
                    var img = new Image { Source = _nextSources[i], Aspect = Aspect.AspectFit };
                    SlideShowGrid.Add(img, i, 0);
                }
                break;
            default:
                SlideShowGrid.RowDefinitions.Add(new RowDefinition());
                SlideShowGrid.RowDefinitions.Add(new RowDefinition());
                SlideShowGrid.ColumnDefinitions.Add(new ColumnDefinition());
                SlideShowGrid.ColumnDefinitions.Add(new ColumnDefinition());
                for (int r = 0; r < 2; r++)
                {
                    for (int c = 0; c < 2; c++)
                    {
                        var idx = r * 2 + c;
                        var img = new Image { Source = _nextSources[idx], Aspect = Aspect.AspectFit };
                        SlideShowGrid.Add(img, c, r);
                    }
                }
                break;
        }
    }
}
