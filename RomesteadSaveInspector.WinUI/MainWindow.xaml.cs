using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using WinRT.Interop;

namespace RomesteadSaveInspector.WinUI;

public sealed partial class MainWindow : Window
{
    private readonly string _appRoot;
    private readonly string _libDir;
    private readonly string _inputDir;
    private readonly string _outputDir;
    private readonly string _backupDir;
    private string _language = "zh-Hans";

    public ObservableCollection<KeyValueRow> WorldRows { get; } = new();
    public ObservableCollection<KeyValueRow> PlayerTotals { get; } = new();
    public ObservableCollection<PlayerItemRow> PlayerItems { get; } = new();

    public MainWindow()
    {
        InitializeComponent();
        LanguageBox.SelectedIndex = 0;
        ApplyLanguage();

        _appRoot = AppPaths.AppRoot;
        _libDir = AppPaths.LibDir;
        _inputDir = AppPaths.InputDir;
        _outputDir = AppPaths.OutputDir;
        _backupDir = AppPaths.BackupDir;

        AppPaths.EnsureStandardDirectories();

        AppLogger.LogWritten += OnLogWritten;
        AppLogger.Info($"App root: {_appRoot}");
        AppLogger.Info("Runtime folders are resolved relative to the application EXE folder.");
        AppLogger.Info("WinUI debug panel is enabled by default. config.ini is no longer used.");
        SetStatus(L("status"), L("ready"));

        TryResizeWindow(1360, 1000);
        CheckFiles(silent: true);
    }

    private string L(string key) => Localization.T(_language, key);

    private string LF(string key, params object[] args) => string.Format(CultureInfo.InvariantCulture, L(key), args);

    private void ApplyLanguage()
    {
        Title = L("app.title");
        TitleText.Text = L("app.title");
        SubtitleText.Text = L("app.subtitle");
        LanguageLabel.Text = L("language");
        InformationTitle.Text = L("information");
        InfoVersionText.Text = $"{L("info.version")}: R1.0";
        InfoDateText.Text = $"{L("info.date")}: 26/5/27";
        InfoGameVersionText.Text = $"{L("info.game.version")}: 0.25.1_1";
        InfoAuthorText.Text = $"{L("info.author")}: Maple5335";
        Step1Title.Text = L("step1.title");
        Step1Description.Text = L("step1.desc");
        OpenLibButton.Content = L("open.lib");
        Step2Title.Text = L("step2.title");
        Step2Description.Text = L("step2.desc");
        OpenProfilesButton.Content = L("open.profiles");
        OpenInputButton.Content = L("open.input");
        Step3Title.Text = L("step3.title");
        UserNameLabel.Text = L("username");
        PlayerNameBox.PlaceholderText = L("username.placeholder");
        CheckFilesButton.Content = L("check.files");
        InspectButton.Content = L("inspect");
        SaveButton.Content = L("save.items");
        WorldInfoTitle.Text = L("world.info");
        PlayerTotalsTitle.Text = L("player.totals");
        PlayerItemsTitle.Text = L("player.items");
        HeaderSection.Text = L("section");
        HeaderSlot.Text = L("slot");
        HeaderBaseDataId.Text = L("item.id");
        HeaderStackCount.Text = L("count");
        DebugLogTitle.Text = L("debug.log");
        if (StatusBar.Message == "Ready." || StatusBar.Message == "准备就绪。" || StatusBar.Message == "")
        {
            StatusBar.Title = L("status");
            StatusBar.Message = L("ready");
        }
    }

    private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LanguageBox?.SelectedItem is ComboBoxItem item && item.Tag is string code)
        {
            _language = code;
            ApplyLanguage();
        }
    }

    private void OnLogWritten(string line)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            LogBox.Text += line + Environment.NewLine;
            LogBox.Select(LogBox.Text.Length, 0);
        });
    }

    private void TryResizeWindow(int width, int height)
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(id);
            appWindow.Resize(new Windows.Graphics.SizeInt32(width, height));
        }
        catch (Exception ex)
        {
            AppLogger.Error("Could not resize window; continuing with default size.", ex);
        }
    }

    private void SetStatus(string title, string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        StatusBar.Title = title;
        StatusBar.Message = message;
        StatusBar.Severity = severity;
        StatusBar.IsOpen = true;
        AppLogger.Info($"{title}: {message}");
    }

    private async void HelpDll_Click(object sender, RoutedEventArgs e)
    {
        await ShowHelpAsync(L("help.dll.title"), L("help.dll.text"));
    }

    private async void HelpSaveFiles_Click(object sender, RoutedEventArgs e)
    {
        await ShowHelpAsync(L("help.files.title"), L("help.files.text"));
    }

    private async void HelpWorkflow_Click(object sender, RoutedEventArgs e)
    {
        await ShowHelpAsync(L("help.workflow.title"), L("help.workflow.text"));
    }

    private async Task ShowHelpAsync(string title, string content)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = content, TextWrapping = TextWrapping.Wrap, MaxWidth = 560 },
            CloseButtonText = L("help.close"),
            XamlRoot = Content.XamlRoot
        };
        await dialog.ShowAsync();
    }

    private void OpenLib_Click(object sender, RoutedEventArgs e) => OpenDirectory(_libDir, create: true);
    private void OpenInput_Click(object sender, RoutedEventArgs e) => OpenDirectory(_inputDir, create: true);

    private void OpenProfiles_Click(object sender, RoutedEventArgs e)
    {
        var profileDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Roaming", "Romestead", "EA", "profiles");
        OpenDirectory(profileDir, create: true);
    }

    private static void OpenDirectory(string path, bool create)
    {
        path = Path.GetFullPath(path);

        if (!Directory.Exists(path))
        {
            if (create) Directory.CreateDirectory(path);
            else return;
        }

        // Open the exact target directory. Using FileName=path avoids Explorer falling back
        // to Documents when an unquoted argument is misread.
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true,
            Verb = "open"
        });
    }

    private void CheckFiles_Click(object sender, RoutedEventArgs e) => CheckFiles(silent: false);

    private bool CheckFiles(bool silent)
    {
        var missing = new List<string>();
        foreach (var dll in new[] { "CandideServer.dll", "Shared.dll", "CandideCreator.Shared.dll", "MonoGame.Framework.dll" })
        {
            if (!File.Exists(Path.Combine(_libDir, dll))) missing.Add("lib/" + dll);
        }

        // Save profiles often contain nested folders. Accept files anywhere under input/.
        var inputFiles = Directory.Exists(_inputDir)
            ? Directory.EnumerateFiles(_inputDir, "*", SearchOption.AllDirectories)
                .Where(f => !Path.GetFileName(f).StartsWith("README", StringComparison.OrdinalIgnoreCase))
                .ToList()
            : new List<string>();

        var gameState = FindInputFileByBaseName(inputFiles, "game_state");
        var worldDesc = FindInputFileByBaseName(inputFiles, "world_desc");
        var charFiles = inputFiles
            .Where(f => Path.GetExtension(f).Equals(".char", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Required save files: game_state, world_desc, and at least one player .char file.
        // Accept them anywhere under input/ so users can copy an entire profile folder.
        if (gameState == null) missing.Add("game_state not found under input");
        if (worldDesc == null) missing.Add("world_desc not found under input");
        if (charFiles.Count == 0) missing.Add("no .char player file found under input");

        AppLogger.Info($"检查文件。Root={_appRoot}");
        AppLogger.Info($"lib={_libDir}");
        AppLogger.Info($"input={_inputDir}");
        AppLogger.Info($"input files found={inputFiles.Count}");
        foreach (var file in inputFiles.Take(20))
            AppLogger.Info("  input: " + Path.GetRelativePath(_inputDir, file));
        if (inputFiles.Count > 20) AppLogger.Info($"  ... and {inputFiles.Count - 20} more files");

        if (missing.Count == 0)
        {
            if (!silent)
            {
                var firstChar = charFiles.Count > 0 ? Path.GetRelativePath(_inputDir, charFiles[0]) : "";
                var details = $"Root: {_appRoot}\ngame_state: {Path.GetRelativePath(_inputDir, gameState!)}\nworld_desc: {Path.GetRelativePath(_inputDir, worldDesc!)}\nplayer file: {firstChar}";
                if (charFiles.Count > 1)
                {
                    details += $"\nplayer files: {charFiles.Count}";
                }
                SetStatus(L("check.ok.title"), L("check.ok.msg") + "\n" + details, InfoBarSeverity.Success);
            }
            return true;
        }

        if (!silent)
        {
            var foundPreview = inputFiles.Count == 0
                ? "No files found under input."
                : "Found input files: " + string.Join(", ", inputFiles.Take(8).Select(f => Path.GetRelativePath(_inputDir, f)));
            var message = string.Join("; ", missing) + "\nRoot: " + _appRoot + "\nInput: " + _inputDir + "\n" + foundPreview;
            SetStatus(L("check.missing.title"), message, InfoBarSeverity.Warning);
        }
        return false;
    }

    private static string? FindInputFileByBaseName(IEnumerable<string> files, string baseName)
    {
        return files.FirstOrDefault(f =>
        {
            var name = Path.GetFileName(f);
            return name.Equals(baseName, StringComparison.OrdinalIgnoreCase)
                   || name.StartsWith(baseName + ".", StringComparison.OrdinalIgnoreCase)
                   || Path.GetFileNameWithoutExtension(name).Equals(baseName, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static string? FindInputFileByName(IEnumerable<string> files, string fileName)
    {
        return files.FirstOrDefault(f => Path.GetFileName(f).Equals(fileName, StringComparison.OrdinalIgnoreCase));
    }

    private async void Inspect_Click(object sender, RoutedEventArgs e)
    {
        await RunInspectionAsync();
    }

    private async Task RunInspectionAsync()
    {
        try
        {
            SetStatus(L("inspect.running.title"), L("inspect.running.msg"), InfoBarSeverity.Informational);
            WorldRows.Clear();
            PlayerTotals.Clear();
            PlayerItems.Clear();

            var player = PlayerNameBox.Text.Trim();
            var options = new InspectorCore.Options
            {
                InputPath = _inputDir,
                LibDir = _libDir,
                OutputDir = _outputDir,
                PlayerFilter = string.IsNullOrWhiteSpace(player) ? null : player,
                Limit = 2000,
                NoFiles = false
            };

            var code = await Task.Run(() => InspectorCore.Run(options));
            if (code != 0)
            {
                SetStatus(L("inspect.failed.title"), L("inspect.failed.msg"), InfoBarSeverity.Error);
                return;
            }

            LoadOutputFiles();
            if (PlayerItems.Count == 0)
                SetStatus(L("inspect.none.title"), L("inspect.none.msg"), InfoBarSeverity.Warning);
            else
                SetStatus(L("inspect.done.title"), LF("inspect.done.msg", PlayerItems.Count), InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Inspection failed.", ex);
            SetStatus(L("inspect.failed.title"), ex.Message, InfoBarSeverity.Error);
        }
    }

    private void LoadOutputFiles()
    {
        LoadSummary();
        LoadPlayerTotals();
        LoadPlayerItems();
    }

    private void LoadSummary()
    {
        WorldRows.Clear();
        var summaryPath = Path.Combine(_outputDir, "summary.json");
        if (File.Exists(summaryPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(summaryPath, Encoding.UTF8));
                var root = doc.RootElement;
                AddJsonNumber(root, "TotalFilesScanned", L("summary.files"));
                AddJsonNumber(root, "TotalGameStates", L("summary.states"));
                AddJsonNumber(root, "TotalPlayerSaves", L("summary.playersaves"));
            }
            catch (Exception ex)
            {
                AppLogger.Error("Could not read summary.json.", ex);
            }
        }

        var statePath = Path.Combine(_outputDir, "state_entries.csv");
        if (File.Exists(statePath))
        {
            var rows = ReadCsv(statePath).ToList();
            foreach (var name in new[] { "Towns", "Buildings", "Citizens", "FoodSources", "ItemInstances", "WorldItems", "PlayerCharacters", "Inventories", "Entities", "Worlds" })
            {
                var row = rows.FirstOrDefault(r => Get(r, "Name").Equals(name, StringComparison.OrdinalIgnoreCase));
                if (row != null) WorldRows.Add(new KeyValueRow(name, Get(row, "Description")));
            }
        }

        var savesPath = Path.Combine(_outputDir, "player_saves.csv");
        if (File.Exists(savesPath))
        {
            foreach (var row in ReadCsv(savesPath))
            {
                WorldRows.Add(new KeyValueRow(L("summary.player"), $"{Get(row, "PlayerName")}  Money={Get(row, "Money")}  File={Get(row, "SaveFileName")}"));
            }
        }
    }

    private void AddJsonNumber(JsonElement root, string propertyName, string label)
    {
        if (root.TryGetProperty(propertyName, out var p))
            WorldRows.Add(new KeyValueRow(label, p.ToString()));
    }

    private void LoadPlayerTotals()
    {
        PlayerTotals.Clear();
        var path = Path.Combine(_outputDir, "player_item_totals.csv");
        if (!File.Exists(path)) return;
        foreach (var row in ReadCsv(path).OrderBy(r => Get(r, "BaseDataId")))
        {
            PlayerTotals.Add(new KeyValueRow(Get(row, "BaseDataId"), Get(row, "TotalStackCount")));
        }
    }

    private void LoadPlayerItems()
    {
        PlayerItems.Clear();
        var path = Path.Combine(_outputDir, "player_items.csv");
        if (!File.Exists(path)) return;
        foreach (var row in ReadCsv(path).OrderBy(r => Get(r, "Section")).ThenBy(r => ToInt(Get(r, "SlotIndex"))))
        {
            PlayerItems.Add(new PlayerItemRow
            {
                SourceFile = Get(row, "SourceFile"),
                SaveFileName = Get(row, "SaveFileName"),
                PlayerName = Get(row, "PlayerName"),
                PlayerId = ToInt(Get(row, "PlayerId")),
                Section = Get(row, "Section"),
                SlotIndex = ToInt(Get(row, "SlotIndex")),
                InstanceId = Get(row, "InstanceId"),
                BaseDataId = Get(row, "BaseDataId"),
                StackCount = ToInt(Get(row, "StackCount")),
                OriginalStackCount = ToInt(Get(row, "StackCount"))
            });
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var invalid = PlayerItems.Where(i => !i.TryGetStackCount(out _)).ToList();
            if (invalid.Count > 0)
            {
                SetStatus(L("save.invalid.title"), L("save.invalid.msg"), InfoBarSeverity.Error);
                return;
            }

            var changed = PlayerItems.Where(i => i.TryGetStackCount(out var n) && n != i.OriginalStackCount).ToList();
            if (changed.Count == 0)
            {
                SetStatus(L("save.nochange.title"), L("save.nochange.msg"), InfoBarSeverity.Informational);
                return;
            }

            var updates = changed.Select(i => new InspectorCore.PlayerItemStackUpdate(
                i.SourceFile,
                i.SaveFileName,
                i.PlayerName,
                i.PlayerId,
                i.Section,
                i.SlotIndex,
                i.InstanceId,
                i.BaseDataId,
                i.GetStackCountOrThrow())).ToList();

            SetStatus(L("save.running.title"), L("save.running.msg"), InfoBarSeverity.Informational);
            var result = await Task.Run(() => InspectorCore.SavePlayerItemStacks(_libDir, _inputDir, _backupDir, _outputDir, updates));
            if (result.SavedFiles <= 0 || result.ChangedItems <= 0)
            {
                SetStatus(L("save.incomplete.title"), result.Message, InfoBarSeverity.Warning);
                return;
            }

            SetStatus(L("save.done.title"), result.Message, InfoBarSeverity.Success);
            await RunInspectionAsync();
        }
        catch (Exception ex)
        {
            AppLogger.Error("Save failed.", ex);
            SetStatus(L("save.invalid.title"), ex.Message, InfoBarSeverity.Error);
        }
    }

    private static int ToInt(string value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
    private static string Get(Dictionary<string, string> row, string key) => row.TryGetValue(key, out var value) ? value : "";

    private static IEnumerable<Dictionary<string, string>> ReadCsv(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var headerLine = reader.ReadLine();
        if (headerLine == null) yield break;
        var headers = ParseCsvLine(headerLine).ToArray();
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var fields = ParseCsvLine(line).ToArray();
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < headers.Length; i++)
                dict[headers[i]] = i < fields.Length ? fields[i] : "";
            yield return dict;
        }
    }

    private static IEnumerable<string> ParseCsvLine(string line)
    {
        var sb = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        sb.Append('"');
                        i++;
                    }
                    else inQuotes = false;
                }
                else sb.Append(ch);
            }
            else
            {
                if (ch == ',')
                {
                    yield return sb.ToString();
                    sb.Clear();
                }
                else if (ch == '"') inQuotes = true;
                else sb.Append(ch);
            }
        }
        yield return sb.ToString();
    }
}

public sealed class KeyValueRow
{
    public KeyValueRow()
    {
    }

    public KeyValueRow(string key, string value)
    {
        Key = key;
        Value = value;
    }

    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

public sealed class PlayerItemRow : INotifyPropertyChanged
{
    private string _stackCountText = "0";

    public string SourceFile { get; set; } = "";
    public string SaveFileName { get; set; } = "";
    public string PlayerName { get; set; } = "";
    public int PlayerId { get; set; }
    public string Section { get; set; } = "";
    public int SlotIndex { get; set; }
    public string InstanceId { get; set; } = "";
    public string BaseDataId { get; set; } = "";
    public int StackCount
    {
        get => ToInt(_stackCountText);
        set => StackCountText = value.ToString(CultureInfo.InvariantCulture);
    }
    public int OriginalStackCount { get; set; }

    public string StackCountText
    {
        get => _stackCountText;
        set
        {
            if (_stackCountText == value) return;
            _stackCountText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StackCountText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StackCount)));
        }
    }

    public bool TryGetStackCount(out int value)
    {
        return int.TryParse(StackCountText, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value >= 0;
    }

    public int GetStackCountOrThrow()
    {
        if (!TryGetStackCount(out var value)) throw new InvalidOperationException($"Invalid StackCount for {BaseDataId}: {StackCountText}");
        return value;
    }

    private static int ToInt(string value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
    public event PropertyChangedEventHandler? PropertyChanged;
}
