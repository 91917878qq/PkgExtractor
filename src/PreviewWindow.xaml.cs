using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using PkgExtractor.Core;

namespace PkgExtractor;

public partial class PreviewWindow : Window
{
    private readonly List<PreviewPackage> _packages;
    private readonly ExtractionOptions _options;
    private readonly ObservableCollection<TreeNode> _roots = new();

    public bool SavedAnything { get; private set; }
    public int SavedFileCount { get; private set; }
    public string? LastOutputDirectory { get; private set; }

    private ImageSource? _imageCache;
    private string? _imageCacheKey;

    private string? _tempVideoPath;
    private string? _tempVideoKey;
    private bool _sliderDragging;
    private System.Windows.Threading.DispatcherTimer? _tick;

    public PreviewWindow(List<PreviewPackage> packages, ExtractionOptions options, string suggestedOutput)
    {
        InitializeComponent();

        _packages = packages;
        _options = options;

        Tree.ItemsSource = _roots;
        OutputBox.Text = suggestedOutput;

        var total = packages.Sum(p => p.Nodes.Count);
        TreeTitle.Text = $"内容 · {packages.Count} 个包 / {total} 个文件";
        PathText.Text = string.Join("   ", packages.Select(p => p.PackageName));

        BuildTree();
        UpdateSelectionBadge();

        TreeNode.AnySelectionChanged += OnAnySelectionChanged;
        Closed += (_, _) =>
        {
            TreeNode.AnySelectionChanged -= OnAnySelectionChanged;
            StopMedia();
            TryDeleteTemp();
        };
    }

    private void OnAnySelectionChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(UpdateSelectionBadge));
            return;
        }

        UpdateSelectionBadge();
    }

    private void TryDeleteTemp()
    {
        if (_tempVideoPath is null) return;

        try
        {
            if (File.Exists(_tempVideoPath)) File.Delete(_tempVideoPath);
        }
        catch { /* the player may still hold the handle; it lives in %TEMP% anyway */ }

        _tempVideoPath = null;
        _tempVideoKey = null;
    }

    // ---------------- chrome ----------------

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        try { DragMove(); } catch { /* ignore */ }
    }

    private void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ---------------- tree ----------------

    private void BuildTree()
    {
        foreach (var package in _packages)
        {
            var root = new TreeNode(package.PackageName, null!, true)
            {
                IsPackageRoot = true,
                SizeHint = $"{package.Nodes.Count} 项",
            };

            var dirs = new Dictionary<string, TreeNode>(StringComparer.OrdinalIgnoreCase);

            foreach (var node in package.Nodes)
            {
                var parts = node.EntryPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
                TreeNode parent = root;

                for (var i = 0; i < parts.Length - 1; i++)
                {
                    var key = string.Join('/', parts.Take(i + 1));
                    if (!dirs.TryGetValue(key, out var dir))
                    {
                        dir = new TreeNode(parts[i], null!, true) { IsDirectory = true };
                        dirs[key] = dir;
                        parent.Children.Add(dir);
                        dir.Parent = parent;
                    }
                    parent = dir;
                }

                var leaf = new TreeNode(parts.Length > 0 ? parts[^1] : node.EntryPath, node, node.Selected)
                {
                    SizeHint = node.SizeText,
                };
                leaf.Parent = parent;
                parent.Children.Add(leaf);
            }

            SortRecursive(root);
            ApplyCheckedDeep(root, true);
            _roots.Add(root);
        }
    }

    /// <summary>Explicitly writes the check state down the whole subtree (bypasses the
    /// early-return in the setter) so that <see cref="PreviewNode.Selected"/> becomes the
    /// single source of truth for every leaf.</summary>
    private static void ApplyCheckedDeep(TreeNode node, bool value)
    {
        node.SetCheckedSilent(value);

        foreach (var child in node.Children)
            ApplyCheckedDeep(child, value);
    }

    private static void SortRecursive(TreeNode node)
    {
        var sorted = node.Children
            .OrderByDescending(c => c.IsDirectory)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        node.Children.Clear();
        foreach (var c in sorted) node.Children.Add(c);

        foreach (var c in node.Children)
            if (c.IsDirectory) SortRecursive(c);
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var root in _roots) ApplyCheckedDeep(root, true);
        UpdateSelectionBadge();
    }

    private void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var root in _roots) ApplyCheckedDeep(root, false);
        UpdateSelectionBadge();
    }

    private void NodeCheck_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        UpdateSelectionBadge();
    }

    private void UpdateSelectionBadge()
    {
        var selected = 0;
        var total = 0;

        foreach (var package in _packages)
        {
            total += package.Nodes.Count;
            selected += package.Nodes.Count(n => n.Selected);
        }

        SelBadge.Text = $"已选 {selected} / {total}";
        SaveBtn.Content = selected > 0 ? $"保存选中 ({selected})" : "保存选中";
        SaveBtn.IsEnabled = selected > 0;
    }

    // ---------------- preview ----------------

    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not TreeNode node || node.Node is null)
        {
            PreviewTitle.Text = e.NewValue is TreeNode { IsDirectory: true } d
                ? d.Name
                : "选择左侧文件查看预览";
            ResetPreview();
            return;
        }

        ShowPreview(node.Node);
    }

    private void ResetPreview()
    {
        StopMedia();
        PreviewImage.Source = null;
        PreviewImage.Visibility = Visibility.Collapsed;
        TextScroll.Visibility = Visibility.Collapsed;
        PreviewPlaceholder.Visibility = Visibility.Visible;
        PreviewPlaceholder.Text = "无预览";
        PreviewMeta.Text = "";
    }

    // ---------------- media playback ----------------

    private void StopMedia()
    {
        MediaBar.Visibility = Visibility.Collapsed;

        try
        {
            PreviewMedia.Stop();
            PreviewMedia.Close();
            PreviewMedia.Source = null;
        }
        catch { /* ignore */ }

        PreviewMedia.Visibility = Visibility.Collapsed;

        _tick?.Stop();
        _tick = null;
    }

    /// <summary>Writes the video bytes to a temp file and plays it. Frames are shown 1:1,
    /// so the playback speed matches the file's real frame timing.</summary>
    private void ShowVideo(PreviewNode node)
    {
        var data = node.Data;
        if (data is not { Length: > 0 }) return;

        var key = node.PackagePath + "|" + node.EntryPath;

        if (_tempVideoKey != key || _tempVideoPath is null || !File.Exists(_tempVideoPath))
        {
            StopMedia();

            var ext = string.IsNullOrEmpty(node.OutputExtension) ? ".mp4" : node.OutputExtension;
            var temp = Path.Combine(Path.GetTempPath(), "PkgExtractor-preview" + ext);

            try
            {
                File.WriteAllBytes(temp, data);
            }
            catch (Exception ex)
            {
                PreviewPlaceholder.Visibility = Visibility.Visible;
                PreviewPlaceholder.Text = "🎬 视频文件";
                PreviewMeta.Text = Mp4Exporter.Describe(data) + "\n无法预览：" + ex.Message;
                return;
            }

            _tempVideoPath = temp;
            _tempVideoKey = key;
        }

        PreviewPlaceholder.Visibility = Visibility.Collapsed;
        PreviewImage.Visibility = Visibility.Collapsed;
        TextScroll.Visibility = Visibility.Collapsed;
        PreviewMedia.Visibility = Visibility.Visible;
        MediaBar.Visibility = Visibility.Visible;

        var info = Mp4Probe.Probe(data);

        PreviewMedia.Source = new Uri(_tempVideoPath, UriKind.Absolute);
        PreviewMedia.Volume = 0;
        PreviewMedia.Play();

        PlayBtn.Content = "暂停";

        PosSlider.Minimum = 0;
        PosSlider.Maximum = info is { DurationSeconds: > 0 } ? info.DurationSeconds : 1;
        PosSlider.Value = 0;

        var meta = Mp4Probe.Probe(data) is { } i
            ? $"{Mp4Exporter.Describe(data)} · {PreviewNode.FormatSize(data.Length)}"
            : $"{PreviewNode.FormatSize(data.Length)} · 视频";

        // Explain the common "plays too fast / too slow" confusion in the tool itself.
        if (info is { FrameRate: > 0 } && Math.Abs(info.FrameRate - Math.Round(info.FrameRate)) < 0.01)
            meta += $"\n按原速播放（{info.FrameRate:0.##} fps，{info.SampleCount} 帧 / {info.DurationSeconds:0.###}s），导出时不重新编码，速度与源文件一致。";

        PreviewMeta.Text = meta;

        _tick?.Stop();
        _tick = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };
        _tick.Tick += (_, _) =>
        {
            try
            {
                if (!_sliderDragging && PreviewMedia.NaturalDuration.HasTimeSpan)
                {
                    var total = PreviewMedia.NaturalDuration.TimeSpan.TotalSeconds;
                    if (total > 0)
                    {
                        PosSlider.Maximum = total;
                        PosSlider.Value = Math.Clamp(PreviewMedia.Position.TotalSeconds, 0, total);
                    }
                }

                TimeText.Text = $"{Fmt(PreviewMedia.Position)} / " +
                                (PreviewMedia.NaturalDuration.HasTimeSpan
                                    ? Fmt(PreviewMedia.NaturalDuration.TimeSpan)
                                    : "--:--");
            }
            catch { /* ignore */ }
        };
        _tick.Start();
    }

    private static string Fmt(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (PreviewMedia.IsLoaded is false) return;

            if (PlayBtn.Content as string == "暂停")
            {
                PreviewMedia.Pause();
                PlayBtn.Content = "播放";
            }
            else
            {
                PreviewMedia.Play();
                PlayBtn.Content = "暂停";
            }
        }
        catch { /* ignore */ }
    }

    private void PosSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (PreviewMedia.Source is null) return;

        _sliderDragging = true;
        try
        {
            PreviewMedia.Position = TimeSpan.FromSeconds(e.NewValue);
        }
        catch { /* ignore */ }
        finally
        {
            _sliderDragging = false;
        }
    }

    private void ShowPreview(PreviewNode node)
    {
        PreviewTitle.Text = node.EntryPath;

        if (node.Kind != "video") StopMedia();

        PreviewPlaceholder.Visibility = Visibility.Collapsed;
        PreviewImage.Visibility = Visibility.Collapsed;
        TextScroll.Visibility = Visibility.Collapsed;

        var ext = node.OutputExtension.ToLowerInvariant();
        var data = node.Data;

        if (data is not { Length: > 0 })
        {
            ResetPreview();
            PreviewTitle.Text = node.EntryPath;
            PreviewPlaceholder.Visibility = Visibility.Visible;
            PreviewPlaceholder.Text = "空文件";
            return;
        }

        // Image / video poster
        if (node.Kind is "image")
        {
            try
            {
                var key = node.PackagePath + "|" + node.EntryPath;
                if (_imageCacheKey != key || _imageCache is null)
                {
                    _imageCache = DecodeImage(data);
                    _imageCacheKey = key;
                }

                if (_imageCache is not null)
                {
                    PreviewImage.Source = _imageCache;
                    PreviewImage.Visibility = Visibility.Visible;
                    PreviewMeta.Text = $"{FormatSize(data.Length)} · {DescribeImage(_imageCache)}";
                    return;
                }
            }
            catch (Exception ex)
            {
                PreviewMeta.Text = "无法解码图片：" + ex.Message;
            }
        }
        else if (node.Kind == "video")
        {
            ShowVideo(node);
            return;
        }
        else if (node.Kind == "gif")
        {
            PreviewPlaceholder.Visibility = Visibility.Visible;
            PreviewPlaceholder.Text = "🎞 动画 GIF";
            PreviewMeta.Text = $"{FormatSize(data.Length)} · GIF · 保存后可查看动画";
            return;
        }
        else if (node.Kind == "audio")
        {
            PreviewPlaceholder.Visibility = Visibility.Visible;
            PreviewPlaceholder.Text = "🎵 音频文件";
            PreviewMeta.Text = $"{FormatSize(data.Length)} · {ext.TrimStart('.').ToUpperInvariant()}";
            return;
        }

        // Text-ish content
        if (IsTextLike(ext) || node.Kind == "json")
        {
            try
            {
                var text = DecodeText(data);
                PreviewText.Text = text.Length > 200_000 ? text[..200_000] + "\n\n…（已截断）" : text;
                TextScroll.Visibility = Visibility.Visible;
                PreviewMeta.Text = $"{FormatSize(data.Length)} · 文本";
                return;
            }
            catch { /* fall through */ }
        }

        PreviewPlaceholder.Visibility = Visibility.Visible;
        PreviewPlaceholder.Text = "无法预览此类型";
        PreviewMeta.Text = $"{FormatSize(data.Length)} · {ext.TrimStart('.').ToUpperInvariant()}";

        if (node.Warning is not null)
            PreviewMeta.Text += "\n" + node.Warning;
    }

    private static ImageSource? DecodeImage(byte[] data)
    {
        using var ms = new MemoryStream(data, writable: false);

        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        bmp.StreamSource = ms;
        bmp.EndInit();
        bmp.Freeze();

        return bmp;
    }

    private static string DescribeImage(ImageSource src) => src is BitmapSource b
        ? $"{b.PixelWidth} × {b.PixelHeight}"
        : "";

    private static bool IsTextLike(string ext) => ext is
        ".json" or ".txt" or ".frag" or ".vert" or ".js" or ".css" or ".html" or ".xml"
        or ".md" or ".ini" or ".cfg" or ".yaml" or ".yml" or ".glsl" or ".shader" or ".hlsl";

    private static string DecodeText(byte[] data)
    {
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
            return System.Text.Encoding.UTF8.GetString(data, 3, data.Length - 3);

        return System.Text.Encoding.UTF8.GetString(data);
    }

    // ---------------- save ----------------

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "选择保存目录" };
        try
        {
            if (!string.IsNullOrWhiteSpace(OutputBox.Text) && Directory.Exists(OutputBox.Text))
                dlg.InitialDirectory = OutputBox.Text;
        }
        catch { /* ignore */ }

        if (dlg.ShowDialog(this) == true)
            OutputBox.Text = dlg.FolderName;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var output = OutputBox.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(output))
        {
            MessageBox.Show(this, "请先选择保存目录。", "PKG Extractor",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SaveBtn.IsEnabled = false;
        SaveBtn.Content = "保存中…";

        var selectedPackages = new List<(PreviewPackage Package, List<PreviewNode> Nodes)>();
        foreach (var package in _packages)
        {
            var nodes = package.Nodes.Where(n => n.Selected).ToList();
            if (nodes.Count > 0) selectedPackages.Add((package, nodes));
        }

        if (selectedPackages.Count == 0)
        {
            SaveBtn.IsEnabled = true;
            UpdateSelectionBadge();
            return;
        }

        var written = 0;

        try
        {
            written = await Task.Run(() => PreviewService.Save(_packages, output, _options));

            SavedAnything = true;
            SavedFileCount = written;
            LastOutputDirectory = output;

            MessageBox.Show(this, $"已保存 {written} 个文件。", "PKG Extractor",
                MessageBoxButton.OK, MessageBoxImage.Information);

            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "保存失败：" + ex.Message, "PKG Extractor",
                MessageBoxButton.OK, MessageBoxImage.Error);
            SaveBtn.IsEnabled = true;
            UpdateSelectionBadge();
        }
    }

    private static string FormatSize(long bytes) => PreviewNode.FormatSize(bytes);
}

/// <summary>Tree node backing the preview TreeView (package → folder → file).</summary>
public sealed class TreeNode : INotifyPropertyChanged
{
    private bool? _isChecked = true;
    private bool _suppress;

    /// <summary>Raised whenever a leaf's selection actually changes, so the window can
    /// refresh its badge without relying on CheckBox.Click (which UIA toggling skips).</summary>
    public static event Action? AnySelectionChanged;

    public TreeNode(string name, PreviewNode? node, bool isChecked)
    {
        Name = name;
        Node = node;
        _isChecked = isChecked;
    }

    public string Name { get; }
    public PreviewNode? Node { get; }
    public TreeNode? Parent { get; set; }
    public bool IsDirectory { get; set; }
    public bool IsPackageRoot { get; set; }
    public string SizeHint { get; set; } = "";
    public ObservableCollection<TreeNode> Children { get; } = new();

    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded != value) { _isExpanded = value; OnPropertyChanged(); } }
    }
    private bool _isExpanded = true;

    public bool? IsChecked
    {
        get => Node is not null ? Node.Selected : _isChecked;
        set
        {
            // Leaf: PreviewNode.Selected is the single source of truth.
            if (Node is not null)
            {
                var wanted = value == true;

                if (Node.Selected != wanted)
                {
                    Node.Selected = wanted;
                    OnPropertyChanged();
                    AnySelectionChanged?.Invoke();
                }

                Parent?.RefreshFromChildren();
                return;
            }

            // Directory / package root.
            if (_isChecked == value) return;
            _isChecked = value;
            OnPropertyChanged();

            if (_suppress) return;

            _suppress = true;
            try
            {
                if (value is not null)
                    foreach (var child in Children)
                        child.IsChecked = value;

                Parent?.RefreshFromChildren();
            }
            finally
            {
                _suppress = false;
            }
        }
    }

    /// <summary>Writes the check state without cascading and without raising a parent refresh.</summary>
    internal void SetCheckedSilent(bool value)
    {
        if (Node is not null)
        {
            Node.Selected = value;
        }
        else
        {
            _isChecked = value;
            foreach (var child in Children)
                child.SetCheckedSilent(value);
        }

        OnPropertyChanged(nameof(IsChecked));
    }

    private void RefreshFromChildren()
    {
        if (Children.Count == 0) return;

        var all = Children.All(c => c.IsChecked == true);
        var none = Children.All(c => c.IsChecked == false);

        var next = all ? true : none ? false : (bool?)null;

        if (_isChecked != next)
        {
            _isChecked = next;
            OnPropertyChanged(nameof(IsChecked));
        }

        Parent?.RefreshFromChildren();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
