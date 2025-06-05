using SimpleImageSlideShow.Services;

namespace SimpleImageSlideShow;

public partial class MainPage : ContentPage
{
    private readonly IFolderPicker _folderPicker;
    private readonly List<string> _images = new();
    private readonly Queue<string> _landscapeQueue = new();
    private readonly Queue<string> _portraitQueue = new();
    private readonly Random _random = new();
    private int _index = 0;
    private int _nextPattern = 0;
    private List<ImageSource>? _nextSources;
    private List<bool>? _nextOrientations;
    private record Slot(int Row, int Column, int RowSpan, int ColumnSpan, bool Landscape);
    private record Pattern(int Rows, int Columns, Slot[] Slots);

    private static readonly Pattern[] Patterns = new[]
    {
        new Pattern(1,1,new[]{ new Slot(0,0,1,1,true)}),
        new Pattern(1,2,new[]{ new Slot(0,0,1,1,true), new Slot(0,1,1,1,true)}),
        new Pattern(2,2,new[]{
            new Slot(0,0,1,1,true), new Slot(0,1,1,1,true),
            new Slot(1,0,1,1,true), new Slot(1,1,1,1,true)}),
        new Pattern(2,2,new[]{
            new Slot(0,0,2,1,false), new Slot(0,1,1,1,true), new Slot(1,1,1,1,true)})
    };

    private static readonly int[] PatternWeights = {3,3,3,1};

    // 1. メンバー初期化子やクラス本体に直接ロジックを書かない。メソッド化する。
    // 2. 画像シャッフルはSortではなくFisher-YatesアルゴリズムやOrderByで行う。
    // 3. sourcesリストの宣言漏れを修正。
    // 4. SlideShowGridや他のUI要素はOnAppearingやコンストラクタで初期化。
    // 5. using Microsoft.Maui.Controls.Image; でImageの曖昧さを解消。

    // 例: シャッフルとグリッド描画をメソッドにまとめる

    private void ShuffleImages()
    {
        // OrderByでシャッフル
        var shuffled = _images.OrderBy(x => _random.Next()).ToList();
        _images.Clear();
        _images.AddRange(shuffled);
    }

    private void ShowPatternedGrid()
    {
        _nextPattern = ChoosePattern();
        var pattern = Patterns[_nextPattern];
        var sources = new List<ImageSource>();
        var orientations = new List<bool>();

        foreach (var slot in pattern.Slots)
        {
            var path = GetNextImage(slot.Landscape);
            sources.Add(ImageSource.FromFile(path));
            orientations.Add(slot.Landscape);
        }
        _nextSources = sources;
        _nextOrientations = orientations;

        SlideShowGrid.Children.Clear();
        SlideShowGrid.RowDefinitions.Clear();
        SlideShowGrid.ColumnDefinitions.Clear();

        for (int r = 0; r < pattern.Rows; r++)
            SlideShowGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Star });
        for (int c = 0; c < pattern.Columns; c++)
            SlideShowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star });

        for (int i = 0; i < pattern.Slots.Length; i++)
        {
            var slot = pattern.Slots[i];
            var img = new Microsoft.Maui.Controls.Image { Source = _nextSources[i], Aspect = Aspect.AspectFit };
            SlideShowGrid.Add(img, slot.Column, slot.Row);
            Grid.SetRowSpan(img, slot.RowSpan);
            Grid.SetColumnSpan(img, slot.ColumnSpan);
        }
    }

    // 使い方例: OnAppearingで呼び出す
    protected override void OnAppearing()
    {
        base.OnAppearing();
        ShuffleImages();
        ShowPatternedGrid();
    }

    private int ChoosePattern()
    {
        int total = PatternWeights.Sum();
        int r = _random.Next(total);
        int sum = 0;
        for (int i = 0; i < PatternWeights.Length; i++)
        {
            sum += PatternWeights[i];
            if (r < sum) return i;
        }
        return PatternWeights.Length - 1;
    }

    private string GetNextImage(bool landscape)
    {
        var desiredQueue = landscape ? _landscapeQueue : _portraitQueue;

        while (desiredQueue.Count == 0)
        {
            if (_index >= _images.Count) _index = 0;
            var file = _images[_index++];
            bool isLand = IsLandscape(file);
            (isLand ? _landscapeQueue : _portraitQueue).Enqueue(file);
        }

        var path = desiredQueue.Dequeue();
        desiredQueue.Enqueue(path);
        return path;
    }

    private static bool IsLandscape(string path)
    {
        try
        {
            using var img = System.Drawing.Image.FromFile(path);
            return img.Width >= img.Height;
        }
        catch { }

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
                var single = new Microsoft.Maui.Controls.Image { Source = _nextSources[0], Aspect = Aspect.AspectFit };
                SlideShowGrid.Add(single);
                break;
            case 1:
                SlideShowGrid.ColumnDefinitions.Add(new ColumnDefinition());
                SlideShowGrid.ColumnDefinitions.Add(new ColumnDefinition());
                for (int i = 0; i < 2; i++)
                {
                    var img = new Microsoft.Maui.Controls.Image { Source = _nextSources[i], Aspect = Aspect.AspectFit };
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
                        var img = new Microsoft.Maui.Controls.Image { Source = _nextSources[idx], Aspect = Aspect.AspectFit };
                        SlideShowGrid.Add(img, c, r);
                    }
                }
                break;
        }
    }
}
