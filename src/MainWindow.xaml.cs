using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using PkgExtractor.Core;

namespace PkgExtractor;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<QueueItem> _queue = new();
    private readonly ExtractionService _service = new();
    private CancellationTokenSource? _cts;
    private bool _busy;
    private readonly StringBuilder _log = new();

    public MainWindow()
    {
        InitializeComponent();

        FileList.ItemsSource = _queue;
        _queue.CollectionChanged += (_, _) => UpdateQueueState();

        var asm = typeof(MainWindow).Assembly.GetName().Version;
        VersionText.Text = $"v{asm?.Major ?? 0}.{asm?.Minor ?? 0}.{asm?.Build ?? 0}";

        OutputBox.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "PKG Extractor Output");

        UpdateQueueState();

        // Files passed on the command line (e.g. dropping files onto the exe) join the queue.
        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1)
            AddPaths(args.Skip(1));
    }

    // ---------------- chrome ----------------

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ---------------- drag & drop ----------------

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && !_busy
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;

        DropZone.BorderBrush = (Brush)FindResource("B.Accent");
        DropZone.Background = new SolidColorBrush(Color.FromArgb(0x1A, 0x6C, 0x8C, 0xFF));
    }

    private void Window_DragLeave(object sender, DragEventArgs e) => ResetDropZone();

    private void ResetDropZone()
    {
        DropZone.BorderBrush = (Brush)FindResource("B.Border");
        DropZone.Background = (Brush)FindResource("B.Surface");
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        ResetDropZone();

        if (_busy) return;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            AddPaths(paths);

        e.Handled = true;
    }

    private void DropZone_Click(object sender, MouseButtonEventArgs e)
    {
        if (!_busy) AddFiles_Click(sender, new RoutedEventArgs());
    }

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择 PKG / MPKG 文件",
            Filter = "Wallpaper Engine 包 (*.pkg;*.mpkg)|*.pkg;*.mpkg|所有文件 (*.*)|*.*",
            Multiselect = true,
            CheckFileExists = true,
        };

        if (dlg.ShowDialog(this) == true)
            AddPaths(dlg.FileNames);
    }

    private void AddPaths(IEnumerable<string> paths)
    {
        var added = 0;
        var skipped = 0;

        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                foreach (var file in SafeEnumeratePackages(path))
                {
                    if (AddOne(file)) added++; else skipped++;
                }
                continue;
            }

            if (!File.Exists(path)) { skipped++; continue; }
            if (AddOne(path)) added++; else skipped++;
        }

        UpdateQueueState();

        if (added > 0)
            AppendLog($"加入 {added} 个文件" + (skipped > 0 ? $"，跳过 {skipped} 个" : ""));
        else if (skipped > 0)
            AppendLog("没有可加入的 .pkg / .mpkg 文件");
    }

    private static IEnumerable<string> SafeEnumeratePackages(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories)
                .Where(IsPackagePath)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static bool IsPackagePath(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".pkg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".mpkg", StringComparison.OrdinalIgnoreCase);
    }

    private bool AddOne(string path)
    {
        if (!IsPackagePath(path)) return false;
        if (_queue.Any(q => string.Equals(q.FullPath, path, StringComparison.OrdinalIgnoreCase))) return false;

        FileInfo fi;
        try { fi = new FileInfo(path); }
        catch { return false; }

        var kind = fi.Extension.TrimStart('.').ToUpperInvariant();

        _queue.Add(new QueueItem
        {
            FullPath = fi.FullName,
            FileName = fi.Name,
            Size = fi.Length,
            KindText = kind,
            Status = "等待",
        });

        return true;
    }

    private void ClearQueue_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _queue.Clear();
        Progress.Value = 0;
        PercentText.Text = "";
        StatusText.Text = "就绪";
    }

    private void UpdateQueueState()
    {
        QueueTitle.Text = _queue.Count > 0 ? $"待解包队列 · {_queue.Count}" : "待解包队列";
        EmptyHint.Visibility = _queue.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------- output ----------------

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "选择输出目录" };
        try
        {
            var current = OutputBox.Text;
            if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current))
                dlg.InitialDirectory = current;
        }
        catch { /* ignore */ }

        if (dlg.ShowDialog(this) == true)
            OutputBox.Text = dlg.FolderName;
    }

    private void OpenOutput_Click(object sender, RoutedEventArgs e)
    {
        var dir = OutputBox.Text;

        try
        {
            if (string.IsNullOrWhiteSpace(dir)) return;
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppendLog($"打开目录失败：{ex.Message}");
        }
    }

    // ---------------- preview ----------------

    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        if (_queue.Count == 0)
        {
            AppendLog("队列为空，先拖入文件。");
            return;
        }

        var options = CurrentOptions();
        var paths = _queue.Select(q => q.FullPath).ToList();

        SetBusy(true);
        PreviewBtn.IsEnabled = false;
        _cts = new CancellationTokenSource();

        var progress = new Progress<PreviewProgress>(p =>
            StatusText.Text = $"[{p.Index}/{p.Count}] {p.Message}");

        AppendLog($"读取 {paths.Count} 个文件用于预览…");

        try
        {
            var packages = await PreviewService.LoadAsync(paths, options, progress, _cts.Token);

            Progress.Value = 100;
            PercentText.Text = "";

            var total = packages.Sum(p => p.Nodes.Count);
            StatusText.Text = $"预览就绪 · {packages.Count} 个包 / {total} 个文件";
            AppendLog(StatusText.Text);

            var wnd = new PreviewWindow(packages, options, options.OutputDirectory) { Owner = this };
            var ok = wnd.ShowDialog();

            if (wnd.SavedAnything)
            {
                AppendLog($"已保存 {wnd.SavedFileCount} 个文件 → {wnd.LastOutputDirectory}");

                foreach (var item in _queue) item.Status = "已完成";

                StatusText.Text = $"已保存 {wnd.SavedFileCount} 个文件 → {wnd.LastOutputDirectory}";
            }
            else
            {
                StatusText.Text = "预览已关闭";
                foreach (var item in _queue)
                    if (item.Status == "处理中") item.Status = "等待";
            }
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已取消";
            AppendLog("已取消。");
        }
        catch (Exception ex)
        {
            StatusText.Text = "预览失败";
            AppendLog("预览失败: " + ex.Message);
            MessageBox.Show(this, ex.Message, "读取失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            SetBusy(false);
            PreviewBtn.IsEnabled = true;
        }
    }

    private ExtractionOptions CurrentOptions() => new()
    {
        OutputDirectory = OutputBox.Text?.Trim() ?? "",
        ConvertTextures = ChkConvert.IsChecked == true,
        KeepOriginalTex = ChkKeepTex.IsChecked == true,
        ExtractAllMipmaps = ChkAllMips.IsChecked == true,
        SubfolderPerPackage = ChkSubfolder.IsChecked == true,
        Overwrite = ChkOverwrite.IsChecked == true,
    };

    // ---------------- MP4 export ----------------

    /// <summary>
    /// Copies the wallpaper video out of each queued package byte-for-byte. Nothing is
    /// re-encoded and nothing is cropped, so the exported file plays at exactly the same
    /// speed as the original wallpaper video.
    /// </summary>
    private async void ExportMp4_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        if (_queue.Count == 0)
        {
            AppendLog("队列为空，先拖入文件。");
            return;
        }

        var output = OutputBox.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(output))
        {
            AppendLog("请先选择输出目录。");
            return;
        }

        SetBusy(true);
        ExportMp4Btn.IsEnabled = false;
        _cts = new CancellationTokenSource();

        var paths = _queue.Select(q => q.FullPath).ToList();
        var subfolder = ChkSubfolder.IsChecked == true;
        var overwrite = ChkOverwrite.IsChecked == true;

        AppendLog($"导出 MP4 · {paths.Count} 个文件 → {output}");

        try
        {
            var done = 0;
            var skipped = new List<string>();
            var failed = new List<string>();

            for (var i = 0; i < paths.Count; i++)
            {
                _cts.Token.ThrowIfCancellationRequested();

                var path = paths[i];
                var name = Path.GetFileName(path);

                StatusText.Text = $"[{i + 1}/{paths.Count}] 导出 {name}";
                Progress.Value = (i + 1) * 100.0 / paths.Count;
                PercentText.Text = $"{Progress.Value:0}%";

                var outcome = await Task.Run(() =>
                {
                    try
                    {
                        var bytes = Mp4Exporter.ReadVideoBytes(path, out var entryPath, out var reason);
                        if (bytes is not { Length: > 0 }) return ("skip", "包内没有视频");

                        Directory.CreateDirectory(output);

                        var stem = Path.GetFileNameWithoutExtension(path);
                        if (subfolder) stem = ExtractionService.SanitizeName(stem);

                        var dir = subfolder
                            ? Path.Combine(output, ExtractionService.SanitizeName(stem))
                            : output;

                        Directory.CreateDirectory(dir);

                        var target = Path.Combine(dir, ExtractionService.SanitizeName(stem) + ".mp4");
                        if (!overwrite) target = ExtractionService.UniquePath(target, false);

                        File.WriteAllBytes(target, bytes);

                        var desc = Mp4Exporter.Describe(bytes);
                        return ("ok", $"{entryPath} → {target}  ({desc}, {FormatSize(bytes.Length)})");
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        return ("fail", ex.Message);
                    }
                }, _cts.Token);

                switch (outcome.Item1)
                {
                    case "ok":
                        done++;
                        AppendLog(outcome.Item2);
                        break;
                    case "skip":
                        skipped.Add($"{name}: {outcome.Item2}");
                        AppendLog($"跳过 {name} — {outcome.Item2}");
                        break;
                    default:
                        failed.Add($"{name}: {outcome.Item2}");
                        AppendLog($"失败 {name} — {outcome.Item2}");
                        break;
                }
            }

            Progress.Value = 100;
            PercentText.Text = "100%";
            StatusText.Text = $"MP4 导出完成 · {done} 个成功" +
                              (skipped.Count > 0 ? $" / {skipped.Count} 个无视频" : "") +
                              (failed.Count > 0 ? $" / {failed.Count} 个失败" : "");

            AppendLog(StatusText.Text);

            foreach (var item in _queue)
                item.Status = done > 0 ? "完成" : "无视频";

            if (done > 0)
            {
                try
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{output}\"") { UseShellExecute = true });
                }
                catch { /* ignore */ }
            }
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已取消";
            AppendLog("已取消。");
        }
        catch (Exception ex)
        {
            StatusText.Text = "导出失败";
            AppendLog("导出失败: " + ex);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            ExportMp4Btn.IsEnabled = true;
            SetBusy(false);
        }
    }

    // ---------------- extraction ----------------

    private async void Extract_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            _cts?.Cancel();
            StatusText.Text = "正在取消…";
            return;
        }

        if (_queue.Count == 0)
        {
            AppendLog("队列为空，先拖入文件。");
            return;
        }

        var output = OutputBox.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(output))
        {
            AppendLog("请先选择输出目录。");
            return;
        }

        var options = new ExtractionOptions
        {
            OutputDirectory = output,
            ConvertTextures = ChkConvert.IsChecked == true,
            KeepOriginalTex = ChkKeepTex.IsChecked == true,
            ExtractAllMipmaps = ChkAllMips.IsChecked == true,
            SubfolderPerPackage = ChkSubfolder.IsChecked == true,
            Overwrite = ChkOverwrite.IsChecked == true,
        };
        // (kept for clarity; CurrentOptions() mirrors the same mapping)

        SetBusy(true);
        _cts = new CancellationTokenSource();

        foreach (var item in _queue) item.Status = "等待";

        var paths = _queue.Select(q => q.FullPath).ToList();
        var progress = new Progress<ExtractionProgress>(p =>
        {
            Progress.Value = Math.Clamp(p.FilePercent, 0, 100);
            PercentText.Text = $"{p.FilePercent:0}%";

            var item = _queue.FirstOrDefault(q =>
                string.Equals(q.FileName, p.FileName, StringComparison.OrdinalIgnoreCase));
            if (item != null)
            {
                item.Status = p.Stage switch
                {
                    "完成" => "完成",
                    "失败" => "失败",
                    _ => "处理中",
                };
            }

            StatusText.Text = p.Message is null
                ? $"[{p.FileIndex + 1}/{p.FileCount}] {p.FileName} — {p.Stage}"
                : $"[{p.FileIndex + 1}/{p.FileCount}] {p.FileName} — {p.Message}";
        });

        AppendLog($"开始解包 {paths.Count} 个文件 → {output}");

        try
        {
            var result = await _service.ExtractAsync(paths, options, progress, _cts.Token);

            Progress.Value = 100;
            PercentText.Text = "100%";

            foreach (var item in _queue)
                if (item.Status == "处理中") item.Status = "完成";

            StatusText.Text =
                $"完成 · {result.PackageCount} 个包 / {result.Files.Count} 个文件 / " +
                $"{FormatSize(result.TotalBytes)} / 贴图转换 {result.ConvertedTexCount} / " +
                $"耗时 {result.Elapsed.TotalSeconds:0.0}s";

            AppendLog(StatusText.Text);

            foreach (var w in result.Warnings.Take(30))
                AppendLog("警告: " + w);

            foreach (var err in result.Errors.Take(30))
                AppendLog("错误: " + err);

            if (result.Warnings.Count > 30)
                AppendLog($"… 还有 {result.Warnings.Count - 30} 条警告");

            if (result.Errors.Count > 30)
                AppendLog($"… 还有 {result.Errors.Count - 30} 条错误");
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已取消";
            PercentText.Text = "";
            Progress.Value = 0;
            AppendLog("已取消。");

            foreach (var item in _queue)
                if (item.Status == "处理中") item.Status = "已取消";
        }
        catch (Exception ex)
        {
            StatusText.Text = "失败";
            PercentText.Text = "";
            AppendLog("失败: " + ex);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        ExtractBtn.Content = busy ? "取消" : "开始解包";
        OpenOutputBtn.IsEnabled = !busy;
        PreviewBtn.IsEnabled = !busy;
        ExportMp4Btn.IsEnabled = !busy;
        ToggleLog.IsEnabled = true;

        foreach (var item in new FrameworkElement[] { ChkConvert, ChkKeepTex, ChkAllMips, ChkSubfolder, ChkOverwrite })
            item.IsEnabled = !busy;
    }

    // ---------------- log ----------------

    private void ToggleLog_Click(object sender, RoutedEventArgs e)
    {
        var show = LogPanel.Visibility != Visibility.Visible;
        LogPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ToggleLog.Content = show ? "隐藏日志" : "显示日志";

        if (show)
            LogScroll.ScrollToEnd();
    }

    private void AppendLog(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        _log.AppendLine(line);

        if (_log.Length > 400_000)
            _log.Remove(0, _log.Length - 300_000);

        LogBox.Text = _log.ToString();
        LogScroll.ScrollToEnd();

        if (LogPanel.Visibility != Visibility.Visible && !_busy)
            ToggleLog.Content = "显示日志 ·";
    }

    private static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        var u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return $"{v:0.##} {units[u]}";
    }
}

public sealed class QueueItem : INotifyPropertyChanged
{
    private string _status = "等待";

    public string FullPath { get; init; } = "";
    public string FileName { get; init; } = "";
    public long Size { get; init; }
    public string KindText { get; init; } = "";

    public string SizeText => Size switch
    {
        < 1024 => $"{Size} B",
        < 1024 * 1024 => $"{Size / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{Size / 1024.0 / 1024:0.#} MB",
        _ => $"{Size / 1024.0 / 1024 / 1024:0.##} GB",
    };

    public string Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
