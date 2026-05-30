using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Threading.Tasks;
using WinRT.Interop;
using RomesteadSaveInspector.Database;

namespace RomesteadSaveInspector.WinUI;

public sealed record UserSettingsDto(string Language, double UiScale, bool DebugMode);

public sealed partial class MainWindow : Window
{
    private readonly string _appRoot;
    private readonly string _libDir;
    private readonly string _inputDir;
    private readonly string _outputDir;
    private readonly string _backupDir;
    private string _gameDir = "";
    private string _language = "zh-Hans";
    private double _uiScale = 0.94;
    private bool _debugMode;
    private string _settingsPath = "";
    private string _moneySourceFile = "";
    private string _moneySaveFileName = "";
    private string _moneyPlayerName = "";
    private int _moneyPlayerId;
    private string _originalMoneyText = "";
    private bool _isSaving;
    private bool _isInspecting;
    private const int MaxVisibleLogLines = 600;
    private readonly object _logLock = new();
    private readonly Queue<string> _pendingLogLines = new();
    private readonly Queue<string> _visibleLogLines = new();
    private bool _logFlushScheduled;


    public ObservableCollection<KeyValueRow> WorldRows { get; } = new();
    public ObservableCollection<KeyValueRow> PlayerTotals { get; } = new();
    public ObservableCollection<PlayerItemRow> PlayerItems { get; } = new();
    public ObservableCollection<PlayerSkillRow> PlayerSkills { get; } = new();
    public ObservableCollection<CitizenRow> Citizens { get; } = new();
    public ObservableCollection<CitizenTraitDatabaseRow> FilteredCitizenTraitRows { get; } = new();
    public ObservableCollection<ItemAuraDatabaseRow> FilteredItemAuraRows { get; } = new();
    public ObservableCollection<ItemDatabaseRow> FilteredItemDatabaseRows { get; } = new();
    private readonly List<ItemDatabaseRow> _allItemDatabaseRows = new();
    private readonly List<CitizenTraitDatabaseRow> _allCitizenTraitRows = new();
    private readonly List<ItemAuraDatabaseRow> _allItemAuraRows = new();
    private readonly Dictionary<object, Dictionary<string, object?>> _layoutMetrics = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ColumnDefinition, GridLength> _columnWidthMetrics = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<RowDefinition, GridLength> _rowHeightMetrics = new(ReferenceEqualityComparer.Instance);

    public MainWindow()
    {
        InitializeComponent();
        MainRoot.DataContext = this;
        MainRoot.Loaded += (_, _) => ApplyUiScale();

        _appRoot = AppPaths.AppRoot;
        _libDir = AppPaths.LibDir;
        _inputDir = AppPaths.InputDir;
        _outputDir = AppPaths.OutputDir;
        _backupDir = AppPaths.BackupDir;
        _settingsPath = Path.Combine(_appRoot, "settings.json");

        AppPaths.EnsureStandardDirectories();
        LoadUserSettings();
        RuntimeGameDatabase.Initialize(AppPaths.DataDir, _gameDir);
        GameDirBox.Text = _gameDir;
        ApplyLanguage();
        ApplyDebugModeVisibility();
        LoadItemDatabaseRows();
        LoadCitizenTraitRows();
        LoadItemAuraRows();
        ApplyUiScale();

        AppLogger.LogWritten += OnLogWritten;
        AppLogger.Info($"App root: {_appRoot}");
        AppLogger.Info("Runtime folders are resolved relative to the application EXE folder.");
        AppLogger.Info("WinUI debug panel is enabled by default. config.ini is no longer used.");
        AppLogger.Info($"UI density: {_uiScale.ToString(CultureInfo.InvariantCulture)}");
        SetStatus(L("status"), L("ready"));

        TryResizeWindow(1500, 980);
        CheckFiles(silent: true);
    }

    private string L(string key) => Localization.T(_language, key);

    private string LF(string key, params object[] args) => string.Format(CultureInfo.InvariantCulture, L(key), args);

    private void LoadUserSettings()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                SaveUserSettings();
                return;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(_settingsPath, Encoding.UTF8));
            var root = doc.RootElement;
            if ((root.TryGetProperty("language", out var language) || root.TryGetProperty("Language", out language)) && language.ValueKind == JsonValueKind.String)
            {
                var value = language.GetString();
                if (!string.IsNullOrWhiteSpace(value)) _language = value!;
            }
            if ((root.TryGetProperty("uiScale", out var scale) || root.TryGetProperty("UiScale", out scale)) && scale.TryGetDouble(out var parsed))
            {
                _uiScale = ClampUiScale(parsed);
            }
            if ((root.TryGetProperty("gameDirectory", out var gameDirectory) || root.TryGetProperty("GameDirectory", out gameDirectory)) && gameDirectory.ValueKind == JsonValueKind.String)
            {
                _gameDir = NormalizeDirectoryText(gameDirectory.GetString() ?? string.Empty);
            }
            _debugMode = ReadDebugMode(root);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Could not load settings.json; using defaults.", ex);
            _language = "zh-Hans";
            _uiScale = 0.94;
            _debugMode = false;
        }
    }

    private void SaveUserSettings()
    {
        try
        {
            Directory.CreateDirectory(_appRoot);
            var settings = new Dictionary<string, object?>
            {
                ["language"] = _language,
                ["uiScale"] = _uiScale,
                ["gameDirectory"] = _gameDir,
                ["debug-mode"] = _debugMode
            };
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsPath, json, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Could not save settings.json.", ex);
        }
    }

    private static bool ReadDebugMode(JsonElement root)
    {
        if (root.TryGetProperty("debug-mode", out var debug) || root.TryGetProperty("debugMode", out debug) || root.TryGetProperty("DebugMode", out debug))
        {
            return debug.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => bool.TryParse(debug.GetString(), out var parsed) && parsed,
                _ => false
            };
        }
        return false;
    }

    private void ApplyDebugModeVisibility()
    {
        if (WorldRoundtripButton != null)
        {
            WorldRoundtripButton.Visibility = _debugMode ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private static double ClampUiScale(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return 0.94;
        var allowed = new[] { 0.76, 0.82, 0.88, 0.94, 1.00 };
        var clamped = Math.Clamp(value, allowed[0], allowed[^1]);
        return allowed.OrderBy(v => Math.Abs(v - clamped)).First();
    }

    private void ApplyUiScale()
    {
        _uiScale = ClampUiScale(_uiScale);

        // UI density works like an operating-system display-size setting:
        // layout spacing, control sizes and fixed rows/columns change noticeably,
        // while text size only changes slightly so it stays readable.
        var layoutScale = _uiScale;
        var textScale = Math.Clamp(1.0 + ((_uiScale - 1.0) * 0.35), 0.96, 1.05);
        ApplyLayoutDensity(MainRoot, layoutScale, textScale);

        DebugLogRow.Height = new GridLength(Math.Round(128 * layoutScale));
    }

    private void ApplyLayoutDensity(DependencyObject root, double layoutScale, double textScale)
    {
        if (root == null) return;
        CaptureAndScaleElement(root, layoutScale, textScale);

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            ApplyLayoutDensity(VisualTreeHelper.GetChild(root, i), layoutScale, textScale);
        }
    }

    private void CaptureAndScaleElement(object target, double layoutScale, double textScale)
    {
        if (target is Grid grid)
        {
            foreach (var column in grid.ColumnDefinitions)
            {
                if (!_columnWidthMetrics.ContainsKey(column)) _columnWidthMetrics[column] = column.Width;
                column.Width = ScaleGridLength(_columnWidthMetrics[column], layoutScale);
            }

            foreach (var row in grid.RowDefinitions)
            {
                if (!_rowHeightMetrics.ContainsKey(row)) _rowHeightMetrics[row] = row.Height;
                row.Height = ScaleGridLength(_rowHeightMetrics[row], layoutScale);
            }
        }

        // Keep fonts readable. Do not scale them as aggressively as spacing.
        ScaleProperty(target, "FontSize", textScale, minDouble: 11, maxDouble: 28);

        // Android-style density: compact/comfortable layout without shrinking the canvas.
        ScaleProperty(target, "Margin", layoutScale);
        ScaleProperty(target, "Padding", layoutScale);
        ScaleProperty(target, "MinWidth", layoutScale);
        ScaleProperty(target, "MinHeight", layoutScale);
        ScaleProperty(target, "Spacing", layoutScale);
        ScaleProperty(target, "ColumnSpacing", layoutScale);
        ScaleProperty(target, "RowSpacing", layoutScale);
        ScaleProperty(target, "CornerRadius", layoutScale);

        // Avoid scaling Width/Height/MaxWidth/MaxHeight globally. Scaling those made
        // the layout feel distorted and could waste screen space in list views.
    }

    private static GridLength ScaleGridLength(GridLength original, double scale)
    {
        return original.GridUnitType == GridUnitType.Pixel
            ? new GridLength(Math.Max(0, original.Value * scale))
            : original;
    }

    private void ScaleProperty(object target, string propertyName, double scale, double? minDouble = null, double? maxDouble = null)
    {
        var property = target.GetType().GetProperty(propertyName);
        if (property == null || !property.CanRead || !property.CanWrite) return;

        if (!_layoutMetrics.TryGetValue(target, out var values))
        {
            values = new Dictionary<string, object?>();
            _layoutMetrics[target] = values;
        }

        if (!values.ContainsKey(propertyName))
        {
            try
            {
                values[propertyName] = property.GetValue(target);
            }
            catch
            {
                return;
            }
        }

        var original = values[propertyName];
        object? scaled = original switch
        {
            double d when !double.IsNaN(d) && !double.IsInfinity(d) => ClampScaledDouble(d * scale, minDouble, maxDouble),
            Thickness t => new Thickness(t.Left * scale, t.Top * scale, t.Right * scale, t.Bottom * scale),
            CornerRadius c => new CornerRadius(c.TopLeft * scale, c.TopRight * scale, c.BottomRight * scale, c.BottomLeft * scale),
            _ => null
        };

        if (scaled == null) return;

        try
        {
            property.SetValue(target, scaled);
        }
        catch
        {
            // Some WinUI generated objects expose a property but do not allow setting it at runtime. Ignore safely.
        }
    }

    private static double ClampScaledDouble(double value, double? min, double? max)
    {
        if (min.HasValue && value < min.Value) return min.Value;
        if (max.HasValue && value > max.Value) return max.Value;
        return value;
    }

    private void ApplyLanguage()
    {
        Title = L("app.title");
        TitleText.Text = L("app.title");
        SubtitleText.Text = L("app.subtitle");
        SettingsButton.Content = L("settings");
        Step1Title.Text = L("step1.title");
        Step1Description.Text = L("step1.desc");
        OpenLibButton.Content = L("open.lib");
        Step2Title.Text = L("step2.title");
        Step2Description.Text = L("step2.desc");
        GameDirLabel.Text = L("game.dir.label");
        GameDirBox.PlaceholderText = L("game.dir.placeholder");
        CheckGameDirButton.Content = L("game.dir.detect");
        OpenProfilesButton.Content = L("open.profiles");
        OpenInputButton.Content = L("open.input");
        Step3Title.Text = L("step3.title");
        UserNameLabel.Text = L("username");
        PlayerNameBox.PlaceholderText = L("username.placeholder");
        CheckFilesButton.Content = L("check.files");
        InspectButton.Content = L("inspect");
        SaveButton.Content = L("save.items");
        WorldRoundtripButton.Content = L("world.roundtrip.test");
        ApplyDebugModeVisibility();
        PlayerEditorTab.Header = L("tab.player.editor");
        PlayerSkillsTab.Header = L("tab.player.skills");
        CitizensTab.Header = L("tab.citizens");
        CitizenTraitsTab.Header = L("tab.citizen.traits.database");
        ItemAurasTab.Header = L("tab.item.auras.database");
        ItemDatabaseTab.Header = L("tab.item.database");
        ItemDatabaseTitle.Text = L("item.database.title");
        ItemDatabaseSearchLabel.Text = L("search");
        ItemDatabaseSearchBox.PlaceholderText = L("item.database.search.placeholder");
        DbHeaderId.Text = L("item.id");
        DbHeaderName.Text = L("item.name");
        DbHeaderCategory.Text = L("category");
        DbHeaderMax.Text = L("max.stack");
        DbHeaderSafety.Text = L("safety");
        DbHeaderFlags.Text = L("flags");
        HeaderItemName.Text = L("item.name");
        HeaderMaxStack.Text = L("max.stack");
        WorldInfoTitle.Text = L("world.info");
        PlayerTotalsTitle.Text = L("player.totals");
        PlayerItemsTitle.Text = L("player.items");
        PlayerMoneyTitle.Text = L("player.money");
        PlayerMoneyLabel.Text = L("money");
        HeaderSection.Text = L("section");
        HeaderSlot.Text = L("slot");
        HeaderBaseDataId.Text = L("item.id");
        HeaderStackCount.Text = L("count");
        PlayerSkillsTitle.Text = L("player.skills");
        SkillHeaderId.Text = L("skill.id");
        SkillHeaderName.Text = L("skill.name");
        SkillHeaderLevel.Text = L("skill.level");
        SkillHeaderExperience.Text = L("skill.experience");
        SkillHeaderRequired.Text = L("skill.required");
        SkillHeaderValue.Text = L("skill.value");
        CitizensTitle.Text = L("citizens.title");
        CitizenHeaderKind.Text = L("citizen.kind");
        CitizenHeaderName.Text = L("citizen.name");
        CitizenHeaderJob.Text = L("citizen.current.job");
        CitizenHeaderJobLevel.Text = L("citizen.job.level");
        CitizenHeaderEfficiency.Text = L("citizen.efficiency");
        CitizenHeaderExpertise.Text = L("citizen.expertise");
        CitizenHeaderLoyalty.Text = L("citizen.loyalty");
        CitizenHeaderTraits.Text = L("citizen.traits");
        CitizenTraitsTitle.Text = L("citizen.traits.database.title");
        CitizenTraitSearchLabel.Text = L("search");
        CitizenTraitSearchBox.PlaceholderText = L("citizen.traits.search.placeholder");
        TraitDbHeaderId.Text = L("trait.id");
        TraitDbHeaderName.Text = L("trait.name");
        TraitDbHeaderType.Text = L("trait.type");
        TraitDbHeaderEffects.Text = L("trait.effects");
        ItemAurasTitle.Text = L("item.auras.database.title");
        ItemAuraSearchLabel.Text = L("search");
        ItemAuraSearchBox.PlaceholderText = L("item.auras.search.placeholder");
        ItemAuraDbHeaderId.Text = L("aura.id");
        ItemAuraDbHeaderName.Text = L("aura.name");
        ItemAuraDbHeaderCategory.Text = L("aura.category");
        ItemAuraDbHeaderTier.Text = L("aura.tier");
        ItemAuraDbHeaderEffects.Text = L("trait.effects");
        HeaderItemAuras.Text = L("item.auras");
        DebugLogTitle.Text = L("debug.log");
        if (StatusBar.Message == "Ready." || StatusBar.Message == "准备就绪。" || StatusBar.Message == "")
        {
            StatusBar.Title = L("status");
            StatusBar.Message = L("ready");
        }
        RefreshItemDatabaseRows();
        RefreshCitizenTraitRows();
        RefreshItemAuraRows();
        foreach (var row in PlayerItems) row.ApplyDatabase(_language);
        foreach (var row in PlayerSkills) row.ApplyLanguage(_language);
        foreach (var row in Citizens) row.ApplyLanguage(_language);
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        await ShowSettingsAsync();
    }

    private async Task ShowSettingsAsync()
    {
        var languageBox = CreateLanguageComboBox();
        languageBox.SelectionChanged += (_, _) =>
        {
            if (languageBox.SelectedItem is ComboBoxItem item && item.Tag is string code)
            {
                _language = code;
                ApplyLanguage();
                SaveUserSettings();
            }
        };

        var scaleBox = CreateScaleComboBox();
        scaleBox.SelectionChanged += (_, _) =>
        {
            if (scaleBox.SelectedItem is ComboBoxItem item && item.Tag is string value && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var scale))
            {
                _uiScale = ClampUiScale(scale);
                ApplyUiScale();
                SaveUserSettings();
                AppLogger.Info($"UI density changed: {_uiScale.ToString(CultureInfo.InvariantCulture)}");
            }
        };

        var generalPanel = new StackPanel { Spacing = 12, Padding = new Thickness(12) };
        generalPanel.Children.Add(new TextBlock { Text = L("settings.language.label"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        generalPanel.Children.Add(languageBox);

        var displayPanel = new StackPanel { Spacing = 12, Padding = new Thickness(12) };
        displayPanel.Children.Add(new TextBlock { Text = L("settings.display.scale"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        displayPanel.Children.Add(scaleBox);

        var latestButton = new Button { Content = L("settings.about.latest"), MinWidth = 220, Padding = new Thickness(12, 8, 12, 8) };
        latestButton.Click += (_, _) => OpenUrl("https://github.com/WanFeng2025/RomesteadSaveModifier/releases");
        var aboutPanel = new StackPanel { Spacing = 8, Padding = new Thickness(12) };
        aboutPanel.Children.Add(new TextBlock { Text = $"{L("info.version")}: R1.1.33" });
        aboutPanel.Children.Add(new TextBlock { Text = $"{L("info.date")}: 5月30日" });
        aboutPanel.Children.Add(new TextBlock { Text = $"{L("info.game.version")}: 0.25.1_4" });
        aboutPanel.Children.Add(new TextBlock { Text = $"{L("info.author")}: Maple5335" });
        aboutPanel.Children.Add(new TextBlock { Text = "" });
        aboutPanel.Children.Add(latestButton);

        var tabs = new TabView
        {
            IsAddTabButtonVisible = false,
            Width = 680,
            Height = 430
        };
        tabs.TabItems.Add(new TabViewItem { Header = L("settings.display"), Content = displayPanel });
        tabs.TabItems.Add(new TabViewItem { Header = L("settings.general"), Content = generalPanel });
        tabs.TabItems.Add(new TabViewItem { Header = L("settings.about"), Content = aboutPanel });

        var dialog = new ContentDialog
        {
            Title = L("settings"),
            Content = tabs,
            CloseButtonText = L("help.close"),
            XamlRoot = Content.XamlRoot
        };
        await dialog.ShowAsync();
    }

    private ComboBox CreateLanguageComboBox()
    {
        var combo = new ComboBox { MinWidth = 260 };
        AddLanguageOption(combo, "zh-Hans", "简体中文");
        AddLanguageOption(combo, "zh-Hant", "繁體中文");
        AddLanguageOption(combo, "de", "Deutsch");
        AddLanguageOption(combo, "es", "Español");
        AddLanguageOption(combo, "fr", "Français");
        AddLanguageOption(combo, "pl", "Polski");
        AddLanguageOption(combo, "pt-BR", "Português (Brasil)");
        AddLanguageOption(combo, "ru", "Русский");
        AddLanguageOption(combo, "ja", "日本語");
        AddLanguageOption(combo, "tr", "Türkçe");
        AddLanguageOption(combo, "ko", "한국어");
        AddLanguageOption(combo, "en", "English");
        SelectComboBoxByTag(combo, _language);
        return combo;
    }

    private static void AddLanguageOption(ComboBox combo, string tag, string label)
    {
        combo.Items.Add(new ComboBoxItem { Tag = tag, Content = label });
    }

    private ComboBox CreateScaleComboBox()
    {
        var combo = new ComboBox { MinWidth = 220 };
        foreach (var option in new[]
        {
            (Scale: 0.76, Key: "density.tiny"),
            (Scale: 0.82, Key: "density.extraSmall"),
            (Scale: 0.88, Key: "density.compact"),
            (Scale: 0.94, Key: "density.small"),
            (Scale: 1.00, Key: "density.default"),
        })
        {
            combo.Items.Add(new ComboBoxItem
            {
                Tag = option.Scale.ToString(CultureInfo.InvariantCulture),
                Content = L(option.Key)
            });
        }
        SelectComboBoxByTag(combo, _uiScale.ToString(CultureInfo.InvariantCulture));
        return combo;
    }

    private static void SelectComboBoxByTag(ComboBox combo, string tag)
    {
        for (var i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item && string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedIndex = i;
                return;
            }
        }
        combo.SelectedIndex = combo.Items.Count > 0 ? 0 : -1;
    }

    private static void OpenUrl(string url)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }

    private void OnLogWritten(string line)
    {
        var shouldSchedule = false;
        lock (_logLock)
        {
            _pendingLogLines.Enqueue(line);
            if (!_logFlushScheduled)
            {
                _logFlushScheduled = true;
                shouldSchedule = true;
            }
        }

        if (shouldSchedule) DispatcherQueue.TryEnqueue(FlushLogLines);
    }

    private void FlushLogLines()
    {
        var incoming = new List<string>();
        lock (_logLock)
        {
            while (_pendingLogLines.Count > 0) incoming.Add(_pendingLogLines.Dequeue());
            _logFlushScheduled = false;
        }

        if (incoming.Count == 0) return;

        foreach (var item in incoming)
        {
            _visibleLogLines.Enqueue(item);
            while (_visibleLogLines.Count > MaxVisibleLogLines) _visibleLogLines.Dequeue();
        }

        if (LogBox == null) return;
        LogBox.Text = string.Join(Environment.NewLine, _visibleLogLines) + Environment.NewLine;
        LogBox.Select(LogBox.Text.Length, 0);
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

    private void GameDirBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            sender.ItemsSource = BuildGameDirectorySuggestions(sender.Text);
        }
    }

    private void GameDirBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is not string selected) return;
        if (string.Equals(selected, L("game.dir.custom"), StringComparison.OrdinalIgnoreCase))
        {
            sender.Text = string.Empty;
            return;
        }
        sender.Text = selected;
    }

    private void GameDirBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (args.ChosenSuggestion is string selected && !string.Equals(selected, L("game.dir.custom"), StringComparison.OrdinalIgnoreCase))
        {
            sender.Text = selected;
        }
    }

    private async void CheckGameDir_Click(object sender, RoutedEventArgs e)
    {
        await DetectGameDirectoryAsync();
    }

    private Task DetectGameDirectoryAsync()
    {
        var path = NormalizeDirectoryText(GameDirBox.Text);
        if (string.IsNullOrWhiteSpace(path))
        {
            SetStatus(L("game.dir.missing.title"), L("game.dir.empty"), InfoBarSeverity.Warning);
            return Task.CompletedTask;
        }

        var missing = GetMissingRequiredDlls(path).ToList();
        if (missing.Count == 0)
        {
            _gameDir = Path.GetFullPath(path);
            GameDirBox.Text = _gameDir;
            SaveUserSettings();
            var refreshed = RuntimeGameDatabase.TryRefreshFromGameDirectory(_gameDir, writeFiles: true);
            RuntimeGameDatabase.LoadDataFiles();
            LoadItemDatabaseRows();
            LoadCitizenTraitRows();
            LoadItemAuraRows();
            SetStatus(L("game.dir.ok.title"), refreshed ? LF("game.dir.ok.msg", _gameDir) + "\n" + L("database.refreshed") : LF("game.dir.ok.msg", _gameDir), InfoBarSeverity.Success);
            return Task.CompletedTask;
        }

        SetStatus(L("game.dir.missing.title"), LF("game.dir.missing.msg", path, string.Join(", ", missing)), InfoBarSeverity.Warning);
        return Task.CompletedTask;
    }

    private IReadOnlyList<string> BuildGameDirectorySuggestions(string? text)
    {
        var defaults = new[]
        {
            @"C:\Program Files (x86)\Steam\steamapps\common\romestead",
            @"D:\SteamLibrary\steamapps\common\romestead"
        };
        var current = NormalizeDirectoryText(text ?? string.Empty);
        var list = defaults
            .Where(p => string.IsNullOrWhiteSpace(current) || p.Contains(current, StringComparison.OrdinalIgnoreCase) || current.Contains("romestead", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (!string.IsNullOrWhiteSpace(current) && !list.Any(p => string.Equals(p, current, StringComparison.OrdinalIgnoreCase)))
        {
            list.Insert(0, current);
        }
        list.Add(L("game.dir.custom"));
        return list;
    }

    private static readonly string[] RequiredGameDlls =
    [
        "CandideServer.dll",
        "Shared.dll",
        "CandideCreator.Shared.dll",
        "MonoGame.Framework.dll"
    ];

    private string GetDependencyDirForRun()
    {
        var currentGameDir = NormalizeDirectoryText(GameDirBox.Text);
        if (IsDependencyDirComplete(currentGameDir))
        {
            _gameDir = Path.GetFullPath(currentGameDir);
            return _gameDir;
        }
        if (IsDependencyDirComplete(_gameDir)) return Path.GetFullPath(_gameDir);
        return _libDir;
    }

    private static bool IsDependencyDirComplete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return false;
        return RequiredGameDlls.All(dll => File.Exists(Path.Combine(path, dll)));
    }

    private static IEnumerable<string> GetMissingRequiredDlls(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return RequiredGameDlls;
        return RequiredGameDlls.Where(dll => !File.Exists(Path.Combine(path, dll))).ToList();
    }

    private static string NormalizeDirectoryText(string? text)
    {
        return (text ?? string.Empty).Trim().Trim('"').TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
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
        var dependencyDir = GetDependencyDirForRun();
        if (!IsDependencyDirComplete(dependencyDir))
        {
            foreach (var dll in RequiredGameDlls)
            {
                missing.Add("DLL: " + dll);
            }
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
        AppLogger.Info($"gameDir={NormalizeDirectoryText(GameDirBox.Text)}");
        AppLogger.Info($"dependencyDir={dependencyDir}");
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
                var details = $"Root: {_appRoot}\nDLL source: {dependencyDir}\ngame_state: {Path.GetRelativePath(_inputDir, gameState!)}\nworld_desc: {Path.GetRelativePath(_inputDir, worldDesc!)}\nplayer file: {firstChar}";
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
            var message = string.Join("; ", missing) + "\nRoot: " + _appRoot + "\nDLL source: " + dependencyDir + "\nInput: " + _inputDir + "\n" + foundPreview;
            SetStatus(L("check.missing.title"), message, InfoBarSeverity.Warning);
        }
        return false;
    }


    private void SetLongOperationUi(bool busy)
    {
        if (SaveButton != null) SaveButton.IsEnabled = !busy;
        if (InspectButton != null) InspectButton.IsEnabled = !busy;
        if (CheckFilesButton != null) CheckFilesButton.IsEnabled = !busy;
        if (WorldRoundtripButton != null) WorldRoundtripButton.IsEnabled = !busy;
        if (CheckGameDirButton != null) CheckGameDirButton.IsEnabled = !busy;
    }

    private void MarkSavedState(
        IEnumerable<PlayerItemRow> itemRows,
        IEnumerable<PlayerSkillRow> skillRows,
        IEnumerable<CitizenRow> citizenRows,
        InspectorCore.PlayerMoneyUpdate? moneyUpdate)
    {
        foreach (var row in itemRows)
        {
            row.AcceptSavedState(_language);
        }

        foreach (var row in skillRows)
        {
            row.AcceptSavedState();
        }

        foreach (var row in citizenRows)
        {
            row.AcceptSavedState(_language);
        }

        if (moneyUpdate != null)
        {
            _originalMoneyText = (PlayerMoneyBox.Text ?? string.Empty).Trim();
        }
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
    private async void WorldRoundtrip_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!CheckFiles(silent: false)) return;

            SetStatus(L("world.roundtrip.running.title"), L("world.roundtrip.running.msg"), InfoBarSeverity.Informational);
            var dependencyDir = GetDependencyDirForRun();
            var result = await Task.Run(() => InspectorCore.TestWorldRoundtrip(dependencyDir, _inputDir, _outputDir));
            SetStatus(
                result.Passed ? L("world.roundtrip.ok.title") : L("world.roundtrip.failed.title"),
                result.Message,
                result.Passed ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        }
        catch (Exception ex)
        {
            AppLogger.Error("World roundtrip test failed.", ex);
            SetStatus(L("world.roundtrip.failed.title"), ex.Message, InfoBarSeverity.Error);
        }
    }


    private async Task RunInspectionAsync(bool automaticAfterSave = false)
    {
        if (_isInspecting || _isSaving)
        {
            SetStatus(L("inspect.running.title"), L("busy.operation.msg"), InfoBarSeverity.Informational);
            return;
        }

        _isInspecting = true;
        SetLongOperationUi(true);
        try
        {
            SetStatus(
                automaticAfterSave ? L("save.refresh.running.title") : L("inspect.running.title"),
                automaticAfterSave ? L("save.refresh.running.msg") : L("inspect.running.msg"),
                InfoBarSeverity.Informational);

            // Capture every UI value before entering the worker thread. The worker thread must
            // not read WinUI controls directly, otherwise it can freeze or throw COMException.
            var player = PlayerNameBox.Text.Trim();
            var language = _language;
            var outputDir = _outputDir;
            var summaryFilesLabel = L("summary.files");
            var summaryStatesLabel = L("summary.states");
            var summaryPlayerSavesLabel = L("summary.playersaves");
            var summaryPlayerLabel = L("summary.player");

            var options = new InspectorCore.Options
            {
                InputPath = _inputDir,
                LibDir = GetDependencyDirForRun(),
                OutputDir = _outputDir,
                PlayerFilter = string.IsNullOrWhiteSpace(player) ? null : player,
                Limit = 2000,
                NoFiles = false
            };

            var result = await Task.Run(() =>
            {
                var code = InspectorCore.Run(options);
                if (code != 0) return InspectionBuildResult.Failed(code);
                var snapshot = BuildOutputSnapshot(outputDir, language, summaryFilesLabel, summaryStatesLabel, summaryPlayerSavesLabel, summaryPlayerLabel);

                // If a user-name filter accidentally filters out the detected .char file,
                // fall back to a full scan. This avoids the confusing state where Check files
                // finds a .char but Inspect reports zero player item rows.
                if (snapshot.PlayerItems.Count == 0 && !string.IsNullOrWhiteSpace(player))
                {
                    AppLogger.Info($"Inspection with player filter '{player}' found 0 player item rows; retrying without player filter.");
                    options.PlayerFilter = null;
                    code = InspectorCore.Run(options);
                    if (code != 0) return InspectionBuildResult.Failed(code);
                    snapshot = BuildOutputSnapshot(outputDir, language, summaryFilesLabel, summaryStatesLabel, summaryPlayerSavesLabel, summaryPlayerLabel);
                }

                AppLogger.Info($"Inspection snapshot: worldRows={snapshot.WorldRows.Count}, playerTotals={snapshot.PlayerTotals.Count}, playerItems={snapshot.PlayerItems.Count}, skills={snapshot.PlayerSkills.Count}, citizens={snapshot.Citizens.Count}, hasMoney={snapshot.Money != null}");
                return InspectionBuildResult.Succeeded(snapshot);
            });

            if (result.ExitCode != 0 || result.Snapshot == null)
            {
                SetStatus(L("inspect.failed.title"), L("inspect.failed.msg"), InfoBarSeverity.Error);
                return;
            }

            await ApplyInspectionSnapshotAsync(result.Snapshot);
            DispatcherQueue.TryEnqueue(ApplyUiScale);

            if (PlayerItems.Count == 0)
            {
                SetStatus(L("inspect.none.title"), L("inspect.none.msg"), InfoBarSeverity.Warning);
            }
            else
            {
                SetStatus(
                    automaticAfterSave ? L("save.refresh.done.title") : L("inspect.done.title"),
                    automaticAfterSave ? LF("save.refresh.done.msg", PlayerItems.Count) : LF("inspect.done.msg", PlayerItems.Count),
                    InfoBarSeverity.Success);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("Inspection failed.", ex);
            SetStatus(L("inspect.failed.title"), ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _isInspecting = false;
            SetLongOperationUi(false);
        }
    }

    private sealed class InspectionBuildResult
    {
        public int ExitCode { get; private init; }
        public InspectionSnapshot? Snapshot { get; private init; }

        public static InspectionBuildResult Failed(int exitCode) => new() { ExitCode = exitCode };
        public static InspectionBuildResult Succeeded(InspectionSnapshot snapshot) => new() { ExitCode = 0, Snapshot = snapshot };
    }

    private sealed class InspectionSnapshot
    {
        public List<KeyValueRow> WorldRows { get; } = new();
        public List<KeyValueRow> PlayerTotals { get; } = new();
        public List<PlayerItemRow> PlayerItems { get; } = new();
        public List<PlayerSkillRow> PlayerSkills { get; } = new();
        public List<CitizenRow> Citizens { get; } = new();
        public MoneyEditorSnapshot? Money { get; set; }
    }

    private sealed record MoneyEditorSnapshot(
        string SourceFile,
        string SaveFileName,
        string PlayerName,
        int PlayerId,
        string MoneyText);

    private static InspectionSnapshot BuildOutputSnapshot(
        string outputDir,
        string language,
        string summaryFilesLabel,
        string summaryStatesLabel,
        string summaryPlayerSavesLabel,
        string summaryPlayerLabel)
    {
        var snapshot = new InspectionSnapshot();

        var summaryPath = Path.Combine(outputDir, "summary.json");
        if (File.Exists(summaryPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(summaryPath, Encoding.UTF8));
                var root = doc.RootElement;
                AddJsonNumberTo(snapshot.WorldRows, root, "TotalFilesScanned", summaryFilesLabel);
                AddJsonNumberTo(snapshot.WorldRows, root, "TotalGameStates", summaryStatesLabel);
                AddJsonNumberTo(snapshot.WorldRows, root, "TotalPlayerSaves", summaryPlayerSavesLabel);
            }
            catch (Exception ex)
            {
                AppLogger.Error("Could not read summary.json.", ex);
            }
        }

        var statePath = Path.Combine(outputDir, "state_entries.csv");
        if (File.Exists(statePath))
        {
            var rows = ReadCsv(statePath).ToList();
            foreach (var name in new[] { "Towns", "Buildings", "Citizens", "FoodSources", "ItemInstances", "WorldItems", "PlayerCharacters", "Inventories", "Entities", "Worlds" })
            {
                var row = rows.FirstOrDefault(r => Get(r, "Name").Equals(name, StringComparison.OrdinalIgnoreCase));
                if (row != null) snapshot.WorldRows.Add(new KeyValueRow(name, Get(row, "Description")));
            }
        }

        var savesPath = Path.Combine(outputDir, "player_saves.csv");
        if (File.Exists(savesPath))
        {
            var firstPlayer = true;
            foreach (var row in ReadCsv(savesPath))
            {
                snapshot.WorldRows.Add(new KeyValueRow(summaryPlayerLabel, $"{Get(row, "PlayerName")}  Money={Get(row, "Money")}  File={Get(row, "SaveFileName")}"));
                if (firstPlayer)
                {
                    snapshot.Money = new MoneyEditorSnapshot(
                        Get(row, "SourceFile"),
                        Get(row, "SaveFileName"),
                        Get(row, "PlayerName"),
                        ToInt(Get(row, "PlayerId")),
                        Get(row, "Money"));
                    firstPlayer = false;
                }
            }
        }

        var totalsPath = Path.Combine(outputDir, "player_item_totals.csv");
        if (File.Exists(totalsPath))
        {
            foreach (var row in ReadCsv(totalsPath).OrderBy(r => Get(r, "BaseDataId")))
            {
                snapshot.PlayerTotals.Add(new KeyValueRow(Get(row, "BaseDataId"), Get(row, "TotalStackCount")));
            }
        }

        var playerItemsPath = Path.Combine(outputDir, "player_items.csv");
        if (File.Exists(playerItemsPath))
        {
            foreach (var row in ReadCsv(playerItemsPath).OrderBy(r => Get(r, "Section")).ThenBy(r => ToInt(Get(r, "SlotIndex"))))
            {
                var itemRow = new PlayerItemRow
                {
                    SourceFile = Get(row, "SourceFile"),
                    SaveFileName = Get(row, "SaveFileName"),
                    PlayerName = Get(row, "PlayerName"),
                    PlayerId = ToInt(Get(row, "PlayerId")),
                    Section = Get(row, "Section"),
                    SlotIndex = ToInt(Get(row, "SlotIndex")),
                    InstanceId = Get(row, "InstanceId"),
                    OriginalBaseDataId = Get(row, "BaseDataId"),
                    BaseDataId = Get(row, "BaseDataId"),
                    StackCount = ToInt(Get(row, "StackCount")),
                    OriginalStackCount = ToInt(Get(row, "StackCount")),
                    OriginalAuraIds = Get(row, "AuraIds"),
                    AuraIdsEdit = Get(row, "AuraIds")
                };
                itemRow.ApplyDatabase(language);
                snapshot.PlayerItems.Add(itemRow);
            }
        }

        var playerSkillsPath = Path.Combine(outputDir, "player_skills.csv");
        if (File.Exists(playerSkillsPath))
        {
            foreach (var row in ReadCsv(playerSkillsPath).OrderBy(r => Get(r, "SkillId"), StringComparer.OrdinalIgnoreCase))
            {
                var skillRow = new PlayerSkillRow
                {
                    SourceFile = Get(row, "SourceFile"),
                    SaveFileName = Get(row, "SaveFileName"),
                    PlayerName = Get(row, "PlayerName"),
                    PlayerId = ToInt(Get(row, "PlayerId")),
                    SkillId = Get(row, "SkillId"),
                    NameEn = Get(row, "Name"),
                    DescriptionEn = Get(row, "Description"),
                    Level = ToInt(Get(row, "Level")),
                    OriginalLevel = ToInt(Get(row, "Level")),
                    CurrentExperience = ToFloat(Get(row, "CurrentExperience")),
                    OriginalCurrentExperience = ToFloat(Get(row, "CurrentExperience")),
                    ExperienceRequiredToLevelUp = ToFloat(Get(row, "ExperienceRequiredToLevelUp")),
                    CurrentValue = ToFloat(Get(row, "CurrentValue"))
                };
                skillRow.ApplyLanguage(language);
                snapshot.PlayerSkills.Add(skillRow);
            }
        }

        var citizensPath = Path.Combine(outputDir, "citizens.csv");
        if (File.Exists(citizensPath))
        {
            foreach (var row in ReadCsv(citizensPath).OrderBy(r => Get(r, "Kind"), StringComparer.OrdinalIgnoreCase).ThenBy(r => Get(r, "Name"), StringComparer.OrdinalIgnoreCase))
            {
                var citizen = new CitizenRow
                {
                    SourceFile = Get(row, "SourceFile"),
                    Kind = Get(row, "Kind"),
                    CitizenId = Get(row, "CitizenId"),
                    EntityId = Get(row, "EntityId"),
                    Name = Get(row, "Name"),
                    CitizenBaseId = Get(row, "CitizenBaseId"),
                    Status = Get(row, "Status"),
                    CurrentJob = Get(row, "CurrentJob"),
                    CurrentJobLevel = Get(row, "CurrentJobLevel"),
                    CurrentJobExperience = Get(row, "CurrentJobExperience"),
                    Efficiency = Get(row, "Efficiency"),
                    Expertise = Get(row, "Expertise"),
                    BaseEfficiency = Get(row, "BaseEfficiency"),
                    BaseExpertise = Get(row, "BaseExpertise"),
                    Happiness = Get(row, "Happiness"),
                    FoodCost = Get(row, "FoodCost"),
                    LoyaltyGain = Get(row, "LoyaltyGain"),
                    ExperienceGain = Get(row, "ExperienceGain"),
                    Loyalty = Get(row, "Loyalty"),
                    LoyaltyLevel = Get(row, "LoyaltyLevel"),
                    CurrentHunger = Get(row, "CurrentHunger"),
                    Personality = Get(row, "Personality"),
                    Background = Get(row, "Background"),
                    TraitIds = Get(row, "TraitIds"),
                    AuraIds = Get(row, "AuraIds"),
                    JobCount = Get(row, "JobCount"),
                    TraitCount = Get(row, "TraitCount")
                };
                citizen.InitializeEditFields();
                citizen.ApplyLanguage(language);
                snapshot.Citizens.Add(citizen);
            }
        }

        return snapshot;
    }

    private async Task ApplyInspectionSnapshotAsync(InspectionSnapshot snapshot)
    {
        WorldRows.Clear();
        PlayerTotals.Clear();
        PlayerItems.Clear();
        PlayerSkills.Clear();
        Citizens.Clear();
        ClearMoneyEditor();

        await AddRowsResponsiveAsync(WorldRows, snapshot.WorldRows);
        await AddRowsResponsiveAsync(PlayerTotals, snapshot.PlayerTotals);
        await AddRowsResponsiveAsync(PlayerItems, snapshot.PlayerItems);
        await AddRowsResponsiveAsync(PlayerSkills, snapshot.PlayerSkills);
        await AddRowsResponsiveAsync(Citizens, snapshot.Citizens);

        if (snapshot.Money != null)
        {
            ApplyMoneyEditorSnapshot(snapshot.Money);
        }
    }

    private static async Task AddRowsResponsiveAsync<T>(ObservableCollection<T> target, IReadOnlyList<T> rows, int batchSize = 64)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            target.Add(rows[i]);
            if ((i + 1) % batchSize == 0)
            {
                await Task.Yield();
            }
        }
    }

    private void ApplyMoneyEditorSnapshot(MoneyEditorSnapshot money)
    {
        _moneySourceFile = money.SourceFile;
        _moneySaveFileName = money.SaveFileName;
        _moneyPlayerName = money.PlayerName;
        _moneyPlayerId = money.PlayerId;
        _originalMoneyText = money.MoneyText;
        PlayerMoneyBox.Text = _originalMoneyText;
    }

    private void ClearMoneyEditor()
    {
        _moneySourceFile = string.Empty;
        _moneySaveFileName = string.Empty;
        _moneyPlayerName = string.Empty;
        _moneyPlayerId = 0;
        _originalMoneyText = string.Empty;
        if (PlayerMoneyBox != null)
        {
            PlayerMoneyBox.Text = string.Empty;
        }
    }

    private bool TryGetMoneyUpdate(out InspectorCore.PlayerMoneyUpdate? update, out string message)
    {
        update = null;
        message = string.Empty;

        var text = (PlayerMoneyBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(_moneySourceFile))
        {
            return true;
        }

        if (!ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var money))
        {
            message = L("money.invalid");
            return false;
        }

        if (string.Equals(text, _originalMoneyText, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        update = new InspectorCore.PlayerMoneyUpdate(_moneySourceFile, _moneySaveFileName, _moneyPlayerName, _moneyPlayerId, money);
        return true;
    }

    private static void AddJsonNumberTo(List<KeyValueRow> rows, JsonElement root, string propertyName, string label)
    {
        if (root.TryGetProperty(propertyName, out var p))
            rows.Add(new KeyValueRow(label, p.ToString()));
    }

    // Kept for compatibility with older code paths; normal inspection now builds a snapshot
    // on a worker thread and applies it to the UI in small batches.
    private void LoadOutputFiles()
    {
        var snapshot = BuildOutputSnapshot(_outputDir, _language, L("summary.files"), L("summary.states"), L("summary.playersaves"), L("summary.player"));
        WorldRows.Clear();
        PlayerTotals.Clear();
        PlayerItems.Clear();
        PlayerSkills.Clear();
        Citizens.Clear();
        ClearMoneyEditor();
        foreach (var row in snapshot.WorldRows) WorldRows.Add(row);
        foreach (var row in snapshot.PlayerTotals) PlayerTotals.Add(row);
        foreach (var row in snapshot.PlayerItems) PlayerItems.Add(row);
        foreach (var row in snapshot.PlayerSkills) PlayerSkills.Add(row);
        foreach (var row in snapshot.Citizens) Citizens.Add(row);
        if (snapshot.Money != null) ApplyMoneyEditorSnapshot(snapshot.Money);
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_isSaving || _isInspecting)
        {
            SetStatus(L("save.running.title"), L("busy.operation.msg"), InfoBarSeverity.Informational);
            return;
        }

        _isSaving = true;
        SetLongOperationUi(true);
        try
        {
            foreach (var row in PlayerItems) row.ApplyDatabase(_language);
            foreach (var row in PlayerSkills) row.ApplyLanguage(_language);

            var invalid = PlayerItems.Where(i => !i.TryValidateForSave(out _)).ToList();
            if (invalid.Count > 0)
            {
                var row = invalid[0];
                row.TryValidateForSave(out var message);
                SetStatus(L("save.invalid.title"), message, InfoBarSeverity.Error);
                return;
            }

            var invalidSkill = PlayerSkills.FirstOrDefault(i => !i.TryValidateForSave(out _));
            if (invalidSkill != null)
            {
                invalidSkill.TryValidateForSave(out var skillMessage);
                SetStatus(L("save.invalid.title"), skillMessage, InfoBarSeverity.Error);
                return;
            }

            var invalidCitizen = Citizens.Where(i => i.IsChanged).FirstOrDefault(i => !i.TryValidateForSave(out _));
            if (invalidCitizen != null)
            {
                invalidCitizen.TryValidateForSave(out var citizenMessage);
                SetStatus(L("save.invalid.title"), citizenMessage, InfoBarSeverity.Error);
                return;
            }

            if (!TryGetMoneyUpdate(out var moneyUpdate, out var moneyMessage))
            {
                SetStatus(L("save.invalid.title"), moneyMessage, InfoBarSeverity.Error);
                return;
            }

            var changed = PlayerItems.Where(i => i.IsChanged).ToList();
            var changedSkills = PlayerSkills.Where(i => i.IsChanged).ToList();
            var changedCitizens = Citizens.Where(i => i.IsChanged).ToList();
            if (changed.Count == 0 && changedSkills.Count == 0 && changedCitizens.Count == 0 && moneyUpdate == null)
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
                i.OriginalBaseDataId,
                i.BaseDataId,
                i.GetStackCountOrThrow(),
                i.GetAuraIdsForSave())).ToList();

            var moneyUpdates = moneyUpdate == null ? Array.Empty<InspectorCore.PlayerMoneyUpdate>() : new[] { moneyUpdate };
            var skillUpdates = changedSkills.Select(i => new InspectorCore.PlayerSkillUpdate(
                i.SourceFile,
                i.SaveFileName,
                i.PlayerName,
                i.PlayerId,
                i.SkillId,
                i.GetLevelOrThrow(),
                i.GetCurrentExperienceOrThrow())).ToList();

            var citizenUpdates = changedCitizens.Select(i => new InspectorCore.CitizenUpdate(
                i.SourceFile,
                i.Kind,
                i.CitizenId,
                i.EntityId,
                i.GetCurrentJobLevelOrThrow(),
                i.GetCurrentJobExperienceOrThrow(),
                i.GetEfficiencyOrThrow(),
                i.GetExpertiseOrThrow(),
                i.GetLoyaltyOrThrow(),
                i.GetLoyaltyLevelOrThrow(),
                i.GetTraitIdsForSave())).ToList();

            SetStatus(L("save.running.title"), L("save.running.msg"), InfoBarSeverity.Informational);
            var playerHasChanges = updates.Count > 0 || moneyUpdates.Length > 0 || skillUpdates.Count > 0;
            var citizenHasChanges = citizenUpdates.Count > 0;
            var messages = new List<string>();
            var totalSaved = 0;
            var totalChanged = 0;

            // WinUI controls can only be accessed on the UI thread. Resolve the DLL source
            // path before entering Task.Run; otherwise reading GameDirBox.Text from the
            // worker thread throws COMException 0x8001010E.
            var dependencyDirForSave = GetDependencyDirForRun();

            if (playerHasChanges)
            {
                var playerResult = await Task.Run(() => InspectorCore.SavePlayerChanges(dependencyDirForSave, _inputDir, _backupDir, _outputDir, updates, moneyUpdates, skillUpdates));
                totalSaved += playerResult.SavedFiles;
                totalChanged += playerResult.ChangedItems;
                messages.Add(playerResult.Message);
            }

            if (citizenHasChanges)
            {
                var citizenResult = await Task.Run(() => InspectorCore.SaveCitizenChanges(dependencyDirForSave, _inputDir, _backupDir, _outputDir, citizenUpdates));
                totalSaved += citizenResult.SavedFiles;
                totalChanged += citizenResult.ChangedItems;
                messages.Add(citizenResult.Message);
            }

            if (totalSaved <= 0 || totalChanged <= 0)
            {
                SetStatus(L("save.incomplete.title"), string.Join(Environment.NewLine, messages), InfoBarSeverity.Warning);
                return;
            }

            MarkSavedState(changed, changedSkills, changedCitizens, moneyUpdate);
            var doneMessage = string.Join(Environment.NewLine, messages);
            if (!string.IsNullOrWhiteSpace(doneMessage)) doneMessage += Environment.NewLine;
            doneMessage += L("save.done.auto.reload");
            AppLogger.Info("Save completed. Starting automatic background re-inspection.");
            SetStatus(L("save.done.title"), doneMessage, InfoBarSeverity.Success);

            // Keep the post-save refresh behavior, but do not run it while _isSaving is true.
            // The refresh itself runs the heavy scan and CSV parsing off the UI thread, then
            // applies UI rows in small batches so the window remains responsive.
            _isSaving = false;
            SetLongOperationUi(false);
            await RunInspectionAsync(automaticAfterSave: true);
            return;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Save failed.", ex);
            SetStatus(L("save.invalid.title"), ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _isSaving = false;
            SetLongOperationUi(false);
        }
    }



    private void LoadItemAuraRows()
    {
        _allItemAuraRows.Clear();
        foreach (var aura in ItemAuraCatalog.All.OrderBy(a => a.Id, StringComparer.OrdinalIgnoreCase))
        {
            _allItemAuraRows.Add(new ItemAuraDatabaseRow(aura, _language));
        }
        RefreshItemAuraRows();
        AppLogger.Info($"Item aura database loaded: {_allItemAuraRows.Count} auras.");
    }

    private void RefreshItemAuraRows()
    {
        if (FilteredItemAuraRows == null) return;
        var query = ItemAuraSearchBox?.Text ?? "";
        FilteredItemAuraRows.Clear();
        IEnumerable<ItemAuraDatabaseRow> rows = _allItemAuraRows;
        if (!string.IsNullOrWhiteSpace(query))
        {
            var q = query.Trim();
            rows = rows.Where(r =>
                r.Id.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                r.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                r.Category.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                r.RawCategory.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                r.Effects.Contains(q, StringComparison.OrdinalIgnoreCase));
        }
        foreach (var row in rows.OrderBy(r => r.Id, StringComparer.OrdinalIgnoreCase))
            FilteredItemAuraRows.Add(row);
        if (ItemAuraCountText != null)
            ItemAuraCountText.Text = LF("item.auras.database.count", FilteredItemAuraRows.Count, ItemAuraCatalog.All.Count);
    }

    private void ItemAuraSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshItemAuraRows();
    }

    private void LoadCitizenTraitRows()
    {
        _allCitizenTraitRows.Clear();
        foreach (var trait in CitizenTraitCatalog.All.OrderBy(t => t.Id, StringComparer.OrdinalIgnoreCase))
        {
            _allCitizenTraitRows.Add(new CitizenTraitDatabaseRow(trait.Id, trait.Category, _language));
        }
        RefreshCitizenTraitRows();
        AppLogger.Info($"Citizen trait database loaded: {_allCitizenTraitRows.Count} traits.");
    }

    private void RefreshCitizenTraitRows()
    {
        if (FilteredCitizenTraitRows == null) return;
        var query = CitizenTraitSearchBox?.Text ?? "";
        FilteredCitizenTraitRows.Clear();
        IEnumerable<CitizenTraitDatabaseRow> rows = _allCitizenTraitRows;
        if (!string.IsNullOrWhiteSpace(query))
        {
            var q = query.Trim();
            rows = rows.Where(r =>
                r.Id.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                r.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                r.NameEn.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                r.Category.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                r.Effects.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var row in rows.Take(1000))
        {
            row.RefreshLanguage(_language);
            FilteredCitizenTraitRows.Add(row);
        }

        if (CitizenTraitCountText != null)
            CitizenTraitCountText.Text = LF("citizen.traits.database.count", FilteredCitizenTraitRows.Count, CitizenTraitCatalog.All.Count);

        DispatcherQueue.TryEnqueue(ApplyUiScale);
    }

    private void CitizenTraitSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshCitizenTraitRows();
    }

    private List<string> GetCitizenTraitSuggestionTexts(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new List<string>();
        var q = text.Trim();
        return CitizenTraitCatalog.All
            .Select(t => new CitizenTraitDatabaseRow(t.Id, t.Category, _language))
            .Where(r => r.Id.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        r.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        r.NameEn.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        r.Category.Contains(q, StringComparison.OrdinalIgnoreCase))
            .Take(12)
            .Select(r => $"{r.Id} — {r.Name}")
            .ToList();
    }

    private static string ExtractCitizenTraitIdFromSuggestion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var value = text.Trim();
        var dash = value.IndexOf('—');
        if (dash > 0) value = value[..dash].Trim();
        return CitizenTraitCatalog.NormalizeId(value);
    }

    private void ApplyCitizenTraitIdToRow(CitizenRow row, string? value)
    {
        if (!row.CanEditCitizen) return;
        row.TraitIdsEdit = CitizenTraitCatalog.NormalizeList(value ?? row.TraitIdsEdit, keepUnknown: true);
        row.ApplyLanguage(_language);
    }

    private void CitizenTraitBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (sender.DataContext is not CitizenRow row) return;
        if (!row.CanEditCitizen) return;
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            var token = CitizenTraitCatalog.GetActiveToken(sender.Text);
            sender.ItemsSource = GetCitizenTraitSuggestionTexts(token);
            row.TraitIdsEdit = sender.Text;
        }
    }

    private void CitizenTraitBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (sender.DataContext is not CitizenRow row) return;
        if (!row.CanEditCitizen) return;

        // Suggestions are displayed as "id — localized name" for readability, but the
        // editable value must remain pure official IDs only. AutoSuggestBox may apply
        // SelectedItem.ToString() to Text after this event, so commit once now and once
        // again on the UI queue.
        var id = ExtractCitizenTraitIdFromSuggestion(args.SelectedItem?.ToString());
        var newText = CitizenTraitCatalog.ReplaceActiveToken(sender.Text, id);
        var cleanText = CitizenTraitCatalog.NormalizeList(newText, keepUnknown: false);

        sender.Text = cleanText;
        ApplyCitizenTraitIdToRow(row, cleanText);

        DispatcherQueue.TryEnqueue(() =>
        {
            var finalText = CitizenTraitCatalog.NormalizeList(row.TraitIdsEdit, keepUnknown: false);
            sender.Text = finalText;
            row.TraitIdsEdit = finalText;
            row.ApplyLanguage(_language);
        });
    }

    private void CitizenTraitBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (sender.DataContext is not CitizenRow row) return;
        if (!row.CanEditCitizen) return;

        var id = args.ChosenSuggestion != null
            ? ExtractCitizenTraitIdFromSuggestion(args.ChosenSuggestion.ToString())
            : CitizenTraitCatalog.NormalizeList(args.QueryText, keepUnknown: true);
        var newText = args.ChosenSuggestion != null ? CitizenTraitCatalog.ReplaceActiveToken(sender.Text, id) : id;
        var cleanText = CitizenTraitCatalog.NormalizeList(newText, keepUnknown: args.ChosenSuggestion == null);

        sender.Text = cleanText;
        ApplyCitizenTraitIdToRow(row, cleanText);

        DispatcherQueue.TryEnqueue(() =>
        {
            var finalText = CitizenTraitCatalog.NormalizeList(row.TraitIdsEdit, keepUnknown: args.ChosenSuggestion == null);
            sender.Text = finalText;
            row.TraitIdsEdit = finalText;
            row.ApplyLanguage(_language);
        });
    }

    private void LoadItemDatabaseRows()
    {
        _allItemDatabaseRows.Clear();
        foreach (var item in RomesteadItemDatabase.Items.OrderBy(i => i.Id, StringComparer.OrdinalIgnoreCase))
        {
            _allItemDatabaseRows.Add(new ItemDatabaseRow(item, _language));
        }
        RefreshItemDatabaseRows();
        AppLogger.Info($"Item database loaded: {RomesteadItemDatabase.ItemCount} items.");
    }

    private void RefreshItemDatabaseRows()
    {
        if (FilteredItemDatabaseRows == null) return;
        var query = ItemDatabaseSearchBox?.Text ?? "";
        FilteredItemDatabaseRows.Clear();
        IEnumerable<ItemDatabaseRow> rows = _allItemDatabaseRows;
        if (!string.IsNullOrWhiteSpace(query))
        {
            var q = query.Trim();
            rows = rows.Where(r =>
                r.Id.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                r.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                r.NameEn.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                r.Category.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                r.Flags.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                r.Safety.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        var list = rows.Take(2000).ToList();
        foreach (var row in list)
        {
            row.RefreshLanguage(_language);
            FilteredItemDatabaseRows.Add(row);
        }

        if (ItemDatabaseCountText != null)
            ItemDatabaseCountText.Text = LF("item.database.count", FilteredItemDatabaseRows.Count, RomesteadItemDatabase.ItemCount);

        DispatcherQueue.TryEnqueue(ApplyUiScale);
    }

    private void ItemDatabaseSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshItemDatabaseRows();
    }


    private List<string> GetItemAuraSuggestionTexts(string? text, string? itemId = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return new List<string>();
        var source = ItemAuraCatalog.GetCompatibleForItem(itemId);
        var q = text.Trim();
        return source.Where(a => a.Id.Contains(q, StringComparison.OrdinalIgnoreCase) || a.Name(_language).Contains(q, StringComparison.OrdinalIgnoreCase) || a.Effects.Contains(q, StringComparison.OrdinalIgnoreCase))
            .Take(16)
            .Select(a => ItemAuraCatalog.DisplayLabel(a, _language))
            .ToList();
    }

    private static string ExtractItemAuraIdFromSuggestion(string? text)
    {
        return ItemAuraCatalog.NormalizeId(text);
    }

    private async void EditItemAuras_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not PlayerItemRow row) return;
        if (!row.CanEditAuras)
        {
            SetStatus(L("save.invalid.title"), L("item.auras.not.editable"), InfoBarSeverity.Informational);
            return;
        }

        var auraBox = new AutoSuggestBox
        {
            Text = row.AuraIdsEdit,
            PlaceholderText = L("item.auras.editor.placeholder"),
            MinWidth = 640
        };
        auraBox.TextChanged += (box, args) =>
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            {
                var token = ItemAuraCatalog.GetActiveToken(box.Text);
                box.ItemsSource = GetItemAuraSuggestionTexts(token, row.BaseDataId);
            }
        };
        auraBox.SuggestionChosen += (box, args) =>
        {
            var id = ExtractItemAuraIdFromSuggestion(args.SelectedItem?.ToString());
            box.Text = ItemAuraCatalog.ReplaceActiveToken(box.Text, id);
        };
        auraBox.QuerySubmitted += (box, args) =>
        {
            var id = args.ChosenSuggestion != null ? ExtractItemAuraIdFromSuggestion(args.ChosenSuggestion.ToString()) : ItemAuraCatalog.NormalizeId(args.QueryText);
            box.Text = ItemAuraCatalog.ReplaceActiveToken(box.Text, id);
        };

        var help = new TextBlock
        {
            Text = L("item.auras.editor.help"),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75
        };
        var compatible = new TextBlock
        {
            Text = string.Join(Environment.NewLine, ItemAuraCatalog.GetCompatibleForItem(row.BaseDataId).Select(a => ItemAuraCatalog.DisplayLabel(a, _language))),
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            Opacity = 0.8,
            MaxHeight = 260
        };
        var scroll = new ScrollViewer { Content = compatible, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 260 };
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(help);
        panel.Children.Add(auraBox);
        panel.Children.Add(scroll);

        var dialog = new ContentDialog
        {
            Title = L("item.auras.editor.title"),
            Content = panel,
            PrimaryButtonText = L("ok"),
            CloseButtonText = L("cancel"),
            XamlRoot = MainRoot.XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;
        var clean = ItemAuraCatalog.NormalizeList(auraBox.Text, keepUnknown: false);
        var invalid = ItemAuraCatalog.SplitIds(auraBox.Text).FirstOrDefault(id => ItemAuraCatalog.Find(id) == null);
        if (!string.IsNullOrWhiteSpace(invalid))
        {
            SetStatus(L("save.invalid.title"), LF("item.auras.invalid", invalid), InfoBarSeverity.Error);
            return;
        }
        row.AuraIdsEdit = clean;
        row.ApplyDatabase(_language);
    }

    private List<string> GetItemSuggestionTexts(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new List<string>();
        return RomesteadItemDatabase.Search(text, _language)
            .Take(12)
            .Select(i => $"{i.Id} — {i.GetName(_language)}")
            .ToList();
    }

    private static string ExtractItemIdFromSuggestion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var value = text.Trim();
        var dash = value.IndexOf('—');
        if (dash > 0) value = value[..dash].Trim();
        return value;
    }

    private void ApplyItemIdToRow(PlayerItemRow row, string? itemId, bool setDefaultCount)
    {
        row.BaseDataId = ExtractItemIdFromSuggestion(itemId);
        row.ApplyDatabase(_language);
        if (!string.IsNullOrWhiteSpace(row.BaseDataId) && setDefaultCount && row.GetStackCountOrDefault() <= 0)
            row.StackCount = Math.Min(1, Math.Max(1, row.MaxStackCount));
        row.ClampStackCountToDatabaseLimit();
    }

    private void ItemIdBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (sender.DataContext is not PlayerItemRow row) return;
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            sender.ItemsSource = GetItemSuggestionTexts(sender.Text);
            ApplyItemIdToRow(row, sender.Text, setDefaultCount: false);
        }
    }

    private void ItemIdBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (sender.DataContext is not PlayerItemRow row) return;
        var id = ExtractItemIdFromSuggestion(args.SelectedItem?.ToString());
        sender.Text = id;
        ApplyItemIdToRow(row, id, setDefaultCount: true);
    }

    private void ItemIdBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (sender.DataContext is not PlayerItemRow row) return;
        var id = args.ChosenSuggestion != null ? ExtractItemIdFromSuggestion(args.ChosenSuggestion.ToString()) : ExtractItemIdFromSuggestion(args.QueryText);
        sender.Text = id;
        ApplyItemIdToRow(row, id, setDefaultCount: true);
    }

    private void StackCountBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox box && box.DataContext is PlayerItemRow row)
        {
            row.ClampStackCountToDatabaseLimit();
        }
    }

    private void PlayerMoneyBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox box) return;
        var text = (box.Text ?? "").Trim();
        if (text.Length == 0) return;
        if (!ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            // Leave the text as-is so the user can correct it; validation happens on Save.
            return;
        }
    }

    private static int ToInt(string value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
    private static float ToFloat(string value) => float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 0f;
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

public static class ItemDatabaseUiText
{
    public static string LocalizedSafety(RomesteadItemRecord item, string language)
    {
        var normalized = RomesteadItemRecord.NormalizeLanguage(language);
        return normalized switch
        {
            "zh-hans" => string.IsNullOrWhiteSpace(item.SafetyZhHans) ? item.Safety : item.SafetyZhHans,
            "zh-hant" => string.IsNullOrWhiteSpace(item.SafetyZhHant) ? item.Safety : item.SafetyZhHant,
            _ => item.Safety
        };
    }
}

public sealed class ItemDatabaseRow : INotifyPropertyChanged
{
    private RomesteadItemRecord? _record;

    public ItemDatabaseRow()
    {
    }

    public ItemDatabaseRow(RomesteadItemRecord record, string language)
    {
        _record = record;
        Id = record.Id;
        NameEn = record.NameEn;
        Flags = record.Flags;
        EditorMaxStackSize = record.EditorMaxStackSize;
        RefreshLanguage(language);
    }

    public string Id { get; set; } = "";
    public string NameEn { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Category { get; set; } = "";
    public string Safety { get; set; } = "";
    public string Flags { get; set; } = "";
    public int EditorMaxStackSize { get; set; }

    public void RefreshLanguage(string language)
    {
        if (_record == null) return;
        DisplayName = _record.GetName(language);
        Category = _record.GetCategory(language);
        Safety = ItemDatabaseUiText.LocalizedSafety(_record, language);
        OnChanged(nameof(DisplayName));
        OnChanged(nameof(Category));
        OnChanged(nameof(Safety));
    }

    private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    public event PropertyChangedEventHandler? PropertyChanged;
}


public sealed record CitizenTraitDefinition(string Id, string Category);
public readonly record struct CitizenTraitEffect(string StatId, decimal Additive, decimal Multiplier, decimal BaseMultiplier = 0m, decimal AdditiveMultiplier = 0m, decimal BonusMultiplier = 0m);

public static class CitizenTraitCatalog
{
    private static readonly IReadOnlyList<CitizenTraitDefinition> EmbeddedAll = new List<CitizenTraitDefinition>
    {
        new("citizen_aura:buff:efficiency", "Trait"),
        new("citizen_aura:debuff:efficiency", "Trait"),
        new("citizen_aura:buff:expertise", "Trait"),
        new("citizen_aura:debuff:expertise", "Trait"),
        new("citizen_aura:buff:experience", "Trait"),
        new("citizen_aura:debuff:experience", "Trait"),
        new("citizen_aura:buff:loyalty", "Trait"),
        new("citizen_aura:debuff:loyalty", "Trait"),
        new("citizen_aura:buff:happiness", "Trait"),
        new("citizen_aura:debuff:happiness", "Trait"),
        new("citizen_aura:debuff:food", "Trait"),
        new("citizen_aura:debuff:anxiety", "Trait"),
        new("citizen_aura:loyalty:1", "Loyalty"),
        new("citizen_aura:loyalty:2", "Loyalty"),
        new("citizen_aura:loyalty:3", "Loyalty"),
        new("citizen_aura:loyalty:4", "Loyalty"),
        new("citizen_aura:wine:0", "Food/Drink"),
        new("citizen_aura:gift:0", "Gift"),
        new("citizen_aura:gift:1", "Gift"),
        new("citizen_aura:gift:2", "Gift"),
        new("citizen_aura:gift:bad", "Gift"),
        new("citizen_aura:gift:food_cost_reduction", "Gift"),
        new("citizen_aura:injury:broken_arm", "Injury"),
        new("citizen_aura:injury:broken_leg", "Injury"),
        new("citizen_aura:injury:broken_rib", "Injury"),
        new("citizen_aura:injury:concussion", "Injury"),
        new("citizen_aura:injury:infected_wound", "Injury"),
        new("citizen_aura:injury:tetanus", "Injury"),
        new("citizen_aura:injury:tinnitus", "Injury"),
        new("citizen_aura:injury:wounded_eye", "Injury"),
        new("citizen_aura:buff:cornucopia", "Buff"),
        new("citizen_aura:buff:venus_embrace", "Buff"),
        new("citizen_aura:buff:venus_citizen_expertise", "Buff"),
        new("citizen_aura:buff:town_defence", "Buff"),
    };

    public static IReadOnlyList<CitizenTraitDefinition> All => RuntimeGameDatabase.CitizenTraits.Count > 0
        ? RuntimeGameDatabase.CitizenTraits.Select(t => new CitizenTraitDefinition(t.Id, t.Category)).ToList()
        : EmbeddedAll;

    private static HashSet<string> ValidIds => All.Select(i => i.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static bool IsValid(string? id) => ValidIds.Contains(NormalizeId(id));

    public static string NormalizeId(string? rawAuraId)
    {
        var id = (rawAuraId ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(id)) return "";
        if (id.StartsWith("citizen_aura:", StringComparison.OrdinalIgnoreCase)) return id;
        if (id.StartsWith("trait:", StringComparison.OrdinalIgnoreCase)) return "citizen_aura:" + id[6..];
        if (id is "efficiency" or "expertise" or "experience" or "loyalty" or "happiness") return "citizen_aura:buff:" + id;
        if (id is "food") return "citizen_aura:debuff:food";
        return id;
    }

    public static string NormalizeList(string value, bool keepUnknown)
    {
        var ids = SplitTraitIds(value)
            .Select(NormalizeId)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Where(v => keepUnknown || IsValid(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return string.Join(";", ids);
    }

    public static IEnumerable<string> SplitTraitIds(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) yield break;
        var matches = Regex.Matches(value, @"citizen_aura:[A-Za-z0-9_:\-]+|trait:[A-Za-z0-9_:\-]+|[A-Za-z]+(?=\s|;|,|\||$)");
        if (matches.Count > 0)
        {
            foreach (Match m in matches) yield return m.Value.Trim();
            yield break;
        }
        foreach (var part in value.Split(new[] { ';', ',', '|', '\n', '\r', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            yield return part;
    }

    public static string GetActiveToken(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var lastSemi = text.LastIndexOf(';');
        var lastComma = text.LastIndexOf(',');
        var lastPipe = text.LastIndexOf('|');
        var pos = Math.Max(lastSemi, Math.Max(lastComma, lastPipe));
        return (pos >= 0 ? text[(pos + 1)..] : text).Trim();
    }

    public static string ReplaceActiveToken(string text, string id)
    {
        id = NormalizeId(id);
        var existing = string.IsNullOrWhiteSpace(text) ? "" : text.Trim();
        if (string.IsNullOrWhiteSpace(existing)) return NormalizeList(id, keepUnknown: true);

        var currentIds = SplitTraitIds(existing).Select(NormalizeId).Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        var hasDelimiter = existing.Contains(';') || existing.Contains(',') || existing.Contains('|') || existing.Contains('\n') || existing.Contains('\r');

        // If the field already contains a complete official trait and the user selects another trait,
        // treat it as adding a new trait. This automatically inserts the missing semicolon.
        if (!hasDelimiter && currentIds.Count == 1 && IsValid(currentIds[0]) && !currentIds[0].Equals(id, StringComparison.OrdinalIgnoreCase))
            return NormalizeList(currentIds[0] + ";" + id, keepUnknown: true);

        var lastSemi = existing.LastIndexOf(';');
        var lastComma = existing.LastIndexOf(',');
        var lastPipe = existing.LastIndexOf('|');
        var pos = Math.Max(lastSemi, Math.Max(lastComma, lastPipe));
        var prefix = pos >= 0 ? existing[..(pos + 1)] : "";
        var joined = prefix + id;
        return NormalizeList(joined, keepUnknown: true);
    }

    public static IEnumerable<CitizenTraitEffect> GetEffects(string? rawAuraId)
    {
        var auraId = NormalizeId(rawAuraId);
        static CitizenTraitEffect E(string statId, decimal additive = 0m, decimal multiplier = 0m, decimal baseMultiplier = 0m, decimal additiveMultiplier = 0m, decimal bonusMultiplier = 0m)
            => new(statId, additive, multiplier, baseMultiplier, additiveMultiplier, bonusMultiplier);

        var runtimeTrait = RuntimeGameDatabase.CitizenTraits.FirstOrDefault(t => string.Equals(t.Id, auraId, StringComparison.OrdinalIgnoreCase));
        if (runtimeTrait != null && runtimeTrait.Effects.Count > 0)
        {
            foreach (var effect in runtimeTrait.Effects)
                yield return new CitizenTraitEffect(effect.StatId, effect.Additive, effect.Multiplier, effect.BaseMultiplier, effect.AdditiveMultiplier, effect.BonusMultiplier);
            yield break;
        }

        foreach (var effect in auraId switch
        {
            "citizen_aura:debuff:food" => new[] { E("Citizen_FoodCost", additive: 10m) },
            "citizen_aura:debuff:expertise" => new[] { E("Citizen_Expertise", additive: -2m) },
            "citizen_aura:debuff:efficiency" => new[] { E("Citizen_Efficiency", additive: -4m) },
            "citizen_aura:debuff:experience" => new[] { E("Citizen_ExperienceGain", additive: -0.3m) },
            "citizen_aura:debuff:loyalty" => new[] { E("Citizen_LoyaltyGain", additive: -0.5m) },
            "citizen_aura:debuff:happiness" => new[] { E("Citizen_Happiness", additive: -3m) },
            "citizen_aura:debuff:anxiety" => new[] { E("Citizen_Happiness", additive: -2m), E("Citizen_LoyaltyGain", additive: -0.2m) },
            "citizen_aura:buff:expertise" => new[] { E("Citizen_Expertise", additive: 3m) },
            "citizen_aura:buff:efficiency" => new[] { E("Citizen_Efficiency", additive: 4m) },
            "citizen_aura:buff:experience" => new[] { E("Citizen_ExperienceGain", additive: 0.3m) },
            "citizen_aura:buff:loyalty" => new[] { E("Citizen_LoyaltyGain", additive: 0.3m) },
            "citizen_aura:buff:happiness" => new[] { E("Citizen_Happiness", additive: 3m) },
            "citizen_aura:loyalty:1" => new[] { E("Citizen_Happiness", additive: 1m), E("Citizen_ExperienceGain", additive: 0.1m) },
            "citizen_aura:loyalty:2" => new[] { E("Citizen_Happiness", additive: 2m), E("Citizen_Expertise", additive: 0.5m), E("Citizen_ExperienceGain", additive: 0.15m) },
            "citizen_aura:loyalty:3" => new[] { E("Citizen_Happiness", additive: 2.5m), E("Citizen_Expertise", additive: 1m), E("Citizen_ExperienceGain", additive: 0.2m) },
            "citizen_aura:loyalty:4" => new[] { E("Citizen_Happiness", additive: 3m), E("Citizen_Expertise", additive: 1m), E("Citizen_ExperienceGain", additive: 0.3m) },
            "citizen_aura:wine:0" => new[] { E("Citizen_Happiness", additive: 2m), E("Citizen_LoyaltyGain", additive: 0.05m), E("Citizen_Efficiency", additive: -1m) },
            "citizen_aura:gift:0" => new[] { E("Citizen_Happiness", additive: 1m) },
            "citizen_aura:gift:1" => new[] { E("Citizen_Happiness", additive: 1m), E("Citizen_LoyaltyGain", additive: 0.5m) },
            "citizen_aura:gift:2" => new[] { E("Citizen_Happiness", additive: 2m) },
            "citizen_aura:gift:bad" => new[] { E("Citizen_Happiness", additive: -1m) },
            "citizen_aura:gift:food_cost_reduction" => new[] { E("Citizen_FoodCost", multiplier: -0.2m) },
            "citizen_aura:injury:broken_arm" => new[] { E("Citizen_Efficiency", multiplier: -0.5m) },
            "citizen_aura:injury:broken_leg" => new[] { E("Citizen_Efficiency", multiplier: -0.3m) },
            "citizen_aura:injury:concussion" => new[] { E("Citizen_ExperienceGain", multiplier: -0.5m), E("Citizen_Expertise", multiplier: -0.5m) },
            "citizen_aura:injury:tetanus" => new[] { E("Citizen_Happiness", multiplier: -0.25m) },
            "citizen_aura:injury:tinnitus" => new[] { E("Citizen_Happiness", multiplier: -0.5m) },
            "citizen_aura:injury:wounded_eye" => new[] { E("Citizen_Efficiency", multiplier: -0.5m), E("Citizen_Expertise", multiplier: -0.25m) },
            "citizen_aura:injury:broken_rib" => new[] { E("Citizen_Efficiency", multiplier: -0.25m), E("Citizen_ExperienceGain", multiplier: -0.25m) },
            "citizen_aura:injury:infected_wound" => new[] { E("Citizen_FoodCost", additive: 1m), E("Citizen_Happiness", multiplier: -0.25m) },
            "citizen_aura:buff:cornucopia" => new[] { E("Citizen_FoodCost", multiplier: -1m) },
            "citizen_aura:buff:venus_embrace" => new[] { E("Citizen_Efficiency", additive: 2m) },
            "citizen_aura:buff:venus_citizen_expertise" => new[] { E("Citizen_Expertise", additive: 1m) },
            "citizen_aura:buff:town_defence" => new[] { E("Citizen_Happiness", additive: 1m) },
            _ => Array.Empty<CitizenTraitEffect>()
        })
            yield return effect;
    }
}

public sealed class CitizenTraitDatabaseRow : INotifyPropertyChanged
{
    public string Id { get; }
    public string RawCategory { get; }
    public string Name { get; private set; } = "";
    public string NameEn { get; private set; } = "";
    public string Category { get; private set; } = "";
    public string Effects { get; private set; } = "";

    public CitizenTraitDatabaseRow(string id, string category, string language)
    {
        Id = id;
        RawCategory = category;
        RefreshLanguage(language);
    }

    public void RefreshLanguage(string language)
    {
        NameEn = GameTextDb.GetAny(Id, "en", "*citizenaura:name");
        Name = GameTextDb.GetAny(Id, language, "*citizenaura:name");
        if (string.IsNullOrWhiteSpace(Name)) Name = NameEn;
        if (string.IsNullOrWhiteSpace(Name)) Name = Id;
        Category = LocalizeCategory(RawCategory, language);
        Effects = string.Join("; ", CitizenTraitCatalog.GetEffects(Id).Select(e => FormatEffect(e, language)));
        if (string.IsNullOrWhiteSpace(Effects)) Effects = "0";
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    private static string LocalizeCategory(string category, string language)
    {
        var normalized = RomesteadItemRecord.NormalizeLanguage(language);
        var zhHans = normalized == "zh-hans";
        var zhHant = normalized == "zh-hant";
        return category switch
        {
            "Trait" => zhHant ? "特質" : zhHans ? "特质" : "Trait",
            "Loyalty" => zhHant ? "忠誠" : zhHans ? "忠诚" : "Loyalty",
            "Food/Drink" => zhHant ? "飲食" : zhHans ? "饮食" : "Food/Drink",
            "Gift" => zhHant ? "禮物" : zhHans ? "礼物" : "Gift",
            "Injury" => zhHant ? "傷病" : zhHans ? "伤病" : "Injury",
            "Buff" => zhHant ? "增益" : zhHans ? "增益" : "Buff",
            _ => category
        };
    }

    private static string FormatEffect(CitizenTraitEffect effect, string language)
    {
        var label = CitizenRow.GetCitizenStatLabelStatic(effect.StatId, language);
        var parts = new List<string>();
        if (effect.Additive != 0) parts.Add(FormatSignedDecimal(effect.Additive));
        if (effect.Multiplier != 0) parts.Add(FormatSignedPercent(effect.Multiplier));
        if (effect.BaseMultiplier != 0) parts.Add(FormatSignedPercent(effect.BaseMultiplier));
        if (effect.AdditiveMultiplier != 0) parts.Add(FormatSignedPercent(effect.AdditiveMultiplier));
        if (effect.BonusMultiplier != 0) parts.Add(FormatSignedPercent(effect.BonusMultiplier));
        if (parts.Count == 0) parts.Add("0");
        return $"{label}: {string.Join(" / ", parts)}";
    }

    private static string FormatSignedDecimal(decimal value)
    {
        var text = value % 1 == 0 ? ((long)value).ToString(CultureInfo.InvariantCulture) : value.ToString("0.###", CultureInfo.InvariantCulture);
        return value > 0 ? "+" + text : text;
    }

    private static string FormatSignedPercent(decimal value)
    {
        var percent = value * 100m;
        var text = percent % 1 == 0 ? ((long)percent).ToString(CultureInfo.InvariantCulture) : percent.ToString("0.###", CultureInfo.InvariantCulture);
        return (percent > 0 ? "+" : "") + text + "%";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class CitizenRow : INotifyPropertyChanged
{
    public string SourceFile { get; set; } = "";
    public string Kind { get; set; } = "";
    public string CitizenId { get; set; } = "";
    public string EntityId { get; set; } = "";
    public string Name { get; set; } = "";
    public string CitizenBaseId { get; set; } = "";
    public string Status { get; set; } = "";
    public string CurrentJob { get; set; } = "";
    public string CurrentJobLevel { get; set; } = "";
    public string CurrentJobExperience { get; set; } = "";
    public string Efficiency { get; set; } = "";
    public string Expertise { get; set; } = "";
    public string BaseEfficiency { get; set; } = "";
    public string BaseExpertise { get; set; } = "";
    public string Happiness { get; set; } = "";
    public string FoodCost { get; set; } = "";
    public string LoyaltyGain { get; set; } = "";
    public string ExperienceGain { get; set; } = "";
    public string Loyalty { get; set; } = "";
    public string LoyaltyLevel { get; set; } = "";
    public string CurrentHunger { get; set; } = "";
    public string Personality { get; set; } = "";
    public string Background { get; set; } = "";
    public string TraitIds { get; set; } = "";
    public string AuraIds { get; set; } = "";
    public string JobCount { get; set; } = "";
    public string TraitCount { get; set; } = "";

    public string OriginalCurrentJobLevel { get; set; } = "";
    public string OriginalCurrentJobExperience { get; set; } = "";
    public string OriginalEfficiency { get; set; } = "";
    public string OriginalExpertise { get; set; } = "";
    public string OriginalLoyalty { get; set; } = "";
    public string OriginalLoyaltyLevel { get; set; } = "";
    public string OriginalTraitIds { get; set; } = "";

    public string CurrentJobLevelEdit { get; set; } = "";
    public string CurrentJobExperienceEdit { get; set; } = "";
    public string EfficiencyEdit { get; set; } = "";
    public string ExpertiseEdit { get; set; } = "";
    public string LoyaltyEdit { get; set; } = "";
    public string LoyaltyLevelEdit { get; set; } = "";
    public string TraitIdsEdit { get; set; } = "";

    public string KindPrimary { get; set; } = "";
    public string KindSecondary { get; set; } = "";
    public string NamePrimary { get; set; } = "";
    public string NameSecondary { get; set; } = "";
    public string JobPrimary { get; set; } = "";
    public string JobSecondary { get; set; } = "";
    public string JobLevelPrimary { get; set; } = "";
    public string JobLevelSecondary { get; set; } = "";
    public string EfficiencyPrimary { get; set; } = "";
    public string EfficiencySecondary { get; set; } = "";
    public string ExpertisePrimary { get; set; } = "";
    public string ExpertiseSecondary { get; set; } = "";
    public string LoyaltyPrimary { get; set; } = "";
    public string LoyaltySecondary { get; set; } = "";
    public string TraitsPrimary { get; set; } = "";
    public string TraitsSecondary { get; set; } = "";
    public string TraitTooltip { get; set; } = "";
    public bool CanEditCitizen => !Kind.Contains("Wild", StringComparison.OrdinalIgnoreCase);
    public double PrimaryFontSize { get; set; } = 14;
    public double SecondaryFontSize { get; set; } = 11;

    public void InitializeEditFields()
    {
        OriginalCurrentJobLevel = NormalizeNumberText(FirstNonEmpty(CurrentJobLevel, "0"));
        OriginalCurrentJobExperience = NormalizeNumberText(FirstNonEmpty(CurrentJobExperience, "0"));
        OriginalEfficiency = NormalizeNumberText(FirstNonEmpty(Efficiency, BaseEfficiency, "0"));
        OriginalExpertise = NormalizeNumberText(FirstNonEmpty(Expertise, BaseExpertise, "0"));
        OriginalLoyalty = NormalizeNumberText(FirstNonEmpty(Loyalty, "0"));
        OriginalLoyaltyLevel = NormalizeNumberText(FirstNonEmpty(LoyaltyLevel, "0"));
        OriginalTraitIds = NormalizeTraitIdList(FirstNonEmpty(TraitIds, AuraIds));

        CurrentJobLevelEdit = OriginalCurrentJobLevel;
        CurrentJobExperienceEdit = OriginalCurrentJobExperience;
        EfficiencyEdit = OriginalEfficiency;
        ExpertiseEdit = OriginalExpertise;
        LoyaltyEdit = OriginalLoyalty;
        LoyaltyLevelEdit = OriginalLoyaltyLevel;
        TraitIdsEdit = OriginalTraitIds;
    }

    public bool IsChanged =>
        CanEditCitizen &&
        (!SameText(CurrentJobLevelEdit, OriginalCurrentJobLevel) ||
        !SameText(CurrentJobExperienceEdit, OriginalCurrentJobExperience) ||
        !SameText(EfficiencyEdit, OriginalEfficiency) ||
        !SameText(ExpertiseEdit, OriginalExpertise) ||
        !SameText(LoyaltyEdit, OriginalLoyalty) ||
        !SameText(LoyaltyLevelEdit, OriginalLoyaltyLevel) ||
        !SameText(NormalizeTraitIdList(TraitIdsEdit), OriginalTraitIds));

    public bool TryValidateForSave(out string message)
    {
        message = "";
        if (!CanEditCitizen)
        {
            message = $"Wild citizen {FirstNonEmpty(Name, CitizenId)} cannot be edited.";
            return !IsChanged;
        }
        if (!TryParseInt(CurrentJobLevelEdit, 0, 10, out _)) { message = $"Citizen {FirstNonEmpty(Name, CitizenId)} job level must be 0-10."; return false; }
        if (!TryParseFloat(CurrentJobExperienceEdit, 0, 999999999, out _)) { message = $"Citizen {FirstNonEmpty(Name, CitizenId)} job experience must be >= 0."; return false; }
        if (!TryParseFloat(EfficiencyEdit, -999999, 999999, out _)) { message = $"Citizen {FirstNonEmpty(Name, CitizenId)} efficiency must be a number."; return false; }
        if (!TryParseFloat(ExpertiseEdit, -999999, 999999, out _)) { message = $"Citizen {FirstNonEmpty(Name, CitizenId)} expertise must be a number."; return false; }
        if (!TryParseFloat(LoyaltyEdit, 0, 999999999, out _)) { message = $"Citizen {FirstNonEmpty(Name, CitizenId)} loyalty must be >= 0."; return false; }
        if (!TryParseInt(LoyaltyLevelEdit, 0, 4, out _)) { message = $"Citizen {FirstNonEmpty(Name, CitizenId)} loyalty level must be 0-4."; return false; }
        var traitIds = CitizenTraitCatalog.SplitTraitIds(TraitIdsEdit).Select(CitizenTraitCatalog.NormalizeId).Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        var invalidTrait = traitIds.FirstOrDefault(v => !CitizenTraitCatalog.IsValid(v));
        if (!string.IsNullOrWhiteSpace(invalidTrait)) { message = $"Citizen {FirstNonEmpty(Name, CitizenId)} trait ID does not exist in the game database: {invalidTrait}"; return false; }
        TraitIdsEdit = CitizenTraitCatalog.NormalizeList(TraitIdsEdit, keepUnknown: false);
        message = "OK";
        return true;
    }


    public void AcceptSavedState(string language)
    {
        OriginalCurrentJobLevel = NormalizeNumberText(CurrentJobLevelEdit);
        OriginalCurrentJobExperience = NormalizeNumberText(CurrentJobExperienceEdit);
        OriginalEfficiency = NormalizeNumberText(EfficiencyEdit);
        OriginalExpertise = NormalizeNumberText(ExpertiseEdit);
        OriginalLoyalty = NormalizeNumberText(LoyaltyEdit);
        OriginalLoyaltyLevel = NormalizeNumberText(LoyaltyLevelEdit);
        OriginalTraitIds = NormalizeTraitIdList(TraitIdsEdit);

        CurrentJobLevel = OriginalCurrentJobLevel;
        CurrentJobExperience = OriginalCurrentJobExperience;
        Efficiency = OriginalEfficiency;
        Expertise = OriginalExpertise;
        Loyalty = OriginalLoyalty;
        LoyaltyLevel = OriginalLoyaltyLevel;
        TraitIds = OriginalTraitIds;
        TraitIdsEdit = OriginalTraitIds;
        ApplyLanguage(language);
        NotifyDisplayFields();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChanged)));
    }

    public int GetCurrentJobLevelOrThrow() => ParseIntOrThrow(CurrentJobLevelEdit);
    public float GetCurrentJobExperienceOrThrow() => ParseFloatOrThrow(CurrentJobExperienceEdit);
    public float GetEfficiencyOrThrow() => ParseFloatOrThrow(EfficiencyEdit);
    public float GetExpertiseOrThrow() => ParseFloatOrThrow(ExpertiseEdit);
    public float GetLoyaltyOrThrow() => ParseFloatOrThrow(LoyaltyEdit);
    public int GetLoyaltyLevelOrThrow() => ParseIntOrThrow(LoyaltyLevelEdit);
    public string GetTraitIdsForSave() => NormalizeTraitIdList(TraitIdsEdit);

    private static bool SameText(string? a, string? b) => string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool TryParseInt(string text, int min, int max, out int value)
    {
        value = 0;
        if (!int.TryParse(NormalizeNumberText(text), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return false;
        return value >= min && value <= max;
    }

    private static bool TryParseFloat(string text, float min, float max, out float value)
    {
        value = 0;
        if (!float.TryParse(NormalizeNumberText(text), NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return false;
        return value >= min && value <= max;
    }

    private static int ParseIntOrThrow(string text) => int.Parse(NormalizeNumberText(text), CultureInfo.InvariantCulture);
    private static float ParseFloatOrThrow(string text) => float.Parse(NormalizeNumberText(text), CultureInfo.InvariantCulture);

    private static string NormalizeTraitIdList(string value)
    {
        return CitizenTraitCatalog.NormalizeList(value, keepUnknown: true);
    }

    public void ApplyLanguage(string language)
    {
        var normalized = RomesteadItemRecord.NormalizeLanguage(language);
        var zhHans = normalized == "zh-hans";
        var zhHant = normalized == "zh-hant";
        var zh = zhHans || zhHant;

        PrimaryFontSize = zh ? 15 : 13;
        SecondaryFontSize = 11;

        var typeId = string.IsNullOrWhiteSpace(Kind) ? "" : $"type:{Kind}";
        var citizenId = FirstNonEmpty(CitizenId, EntityId, CitizenBaseId);
        var idLine = string.IsNullOrWhiteSpace(citizenId) ? "" : $"id:{citizenId}";

        var localizedName = ResolveGameText(Name, language);
        var localizedJob = ResolveJob(CurrentJob, language);
        var localizedTraits = ResolveTraitList(TraitIds, AuraIds, language);

        if (zh)
        {
            KindPrimary = LocalizeKind(Kind, zhHant);
            KindSecondary = JoinSmall(typeId, idLine);

            NamePrimary = FirstNonEmpty(localizedName, Name, zhHant ? "未命名" : "未命名");
            NameSecondary = JoinSmall(IdLine(Name), idLine);

            JobPrimary = FirstNonEmpty(localizedJob, LocalizeJob(CurrentJob, zhHant), zhHant ? "無工作" : "无工作");
            JobSecondary = JoinSmall(IdLine(CurrentJob), LocalizeStatusLine(Status, zhHant));

            JobLevelPrimary = PrefixValue(zhHant ? "等級" : "等级", CurrentJobLevelEdit);
            JobLevelSecondary = PrefixValue(zhHant ? "經驗" : "经验", CurrentJobExperienceEdit);

            var efficiencyValue = NormalizeNumberText(FirstNonEmpty(EfficiencyEdit, Efficiency, BaseEfficiency, "0"));
            var expertiseValue = NormalizeNumberText(FirstNonEmpty(ExpertiseEdit, Expertise, BaseExpertise, "0"));
            EfficiencyPrimary = FormatStatDisplay(GameTextDb.Get("Citizen_Efficiency*stats:name", language), efficiencyValue);
            EfficiencySecondary = "id:Citizen_Efficiency";

            ExpertisePrimary = FormatStatDisplay(GameTextDb.Get("Citizen_Expertise*stats:name", language), expertiseValue);
            ExpertiseSecondary = "id:Citizen_Expertise";

            // Loyalty level is the main value. Raw loyalty progress is secondary.
            LoyaltyPrimary = PrefixValue(zhHant ? "忠誠等級" : "忠诚等级", LoyaltyLevelEdit);
            LoyaltySecondary = PrefixValue(zhHant ? "忠誠度" : "忠诚度", LoyaltyEdit);

            localizedTraits = ResolveTraitList(TraitIdsEdit, AuraIds, language);
            TraitsPrimary = FirstNonEmpty(localizedTraits, zhHant ? "無特質" : "无特质");
            TraitsSecondary = JoinSmall(IdLine(FirstNonEmpty(TraitIds, AuraIds)), LocalizePersonalityLine(Personality, Background, zhHant));
            TraitTooltip = BuildTraitTooltip(language);
        }
        else
        {
            KindPrimary = FirstNonEmpty(typeId, Kind);
            KindSecondary = idLine;

            NamePrimary = FirstNonEmpty(localizedName, Name, "Unnamed");
            NameSecondary = JoinSmall(IdLine(Name), idLine);

            JobPrimary = FirstNonEmpty(CurrentJob, localizedJob);
            JobSecondary = Status;

            JobLevelPrimary = PrefixValue("level", CurrentJobLevelEdit);
            JobLevelSecondary = PrefixValue("xp", CurrentJobExperienceEdit);

            var efficiencyValue = NormalizeNumberText(FirstNonEmpty(EfficiencyEdit, Efficiency, BaseEfficiency, "0"));
            var expertiseValue = NormalizeNumberText(FirstNonEmpty(ExpertiseEdit, Expertise, BaseExpertise, "0"));
            EfficiencyPrimary = FormatStatDisplay("Citizen_Efficiency", efficiencyValue);
            EfficiencySecondary = "";

            ExpertisePrimary = FormatStatDisplay("Citizen_Expertise", expertiseValue);
            ExpertiseSecondary = "";

            LoyaltyPrimary = PrefixValue("level", LoyaltyLevelEdit);
            LoyaltySecondary = PrefixValue("loyalty", LoyaltyEdit);

            TraitsPrimary = FirstNonEmpty(TraitIdsEdit, TraitIds, AuraIds);
            TraitsSecondary = "";
            TraitTooltip = BuildTraitTooltip(language);
        }

        NotifyDisplayFields();
    }

    private string BuildTraitTooltip(string language)
    {
        var ids = SplitIds(FirstNonEmpty(TraitIdsEdit, TraitIds, AuraIds)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var normalized = RomesteadItemRecord.NormalizeLanguage(language);
        var zhHans = normalized == "zh-hans";
        var zhHant = normalized == "zh-hant";
        var zh = zhHans || zhHant;

        var lines = new List<string>();
        if (ids.Count == 0)
        {
            lines.Add(zhHant ? "無特質" : zhHans ? "无特质" : "No traits");
        }
        else
        {
            foreach (var id in ids)
            {
                var name = ResolveTraitText(id, language);
                var details = GetTraitDetailLines(id, language).ToList();
                lines.Add(name);
                if (details.Count == 0)
                {
                    lines.Add(zhHant ? "  具體效果: —" : zhHans ? "  具体效果: —" : "  Effect: —");
                }
                else
                {
                    foreach (var detail in details) lines.Add("  " + detail);
                }
            }
        }

        var common = GetGeneralCitizenAttributeLines(language).ToList();
        if (common.Count > 0)
        {
            lines.Add(zhHant ? "目前屬性" : zhHans ? "目前属性" : "Current attributes");
            foreach (var line in common) lines.Add("  " + line);
        }

        return string.Join(Environment.NewLine, lines);
    }

    private IEnumerable<string> GetTraitDetailLines(string id, string language)
    {
        var effects = GetCitizenAuraEffectsForDisplay(id).ToList();
        if (effects.Count == 0)
        {
            var raw = (id ?? "").Trim().ToLowerInvariant();
            var normalized = RomesteadItemRecord.NormalizeLanguage(language);
            var zhHans = normalized == "zh-hans";
            var zhHant = normalized == "zh-hant";
            if (raw.Contains("injury"))
            {
                yield return zhHant ? "受傷狀態" : zhHans ? "受伤状态" : "Injury status";
            }
            yield break;
        }

        foreach (var effect in effects)
        {
            yield return FormatTraitEffect(effect, language);
        }
    }

    private static IEnumerable<CitizenTraitEffect> GetCitizenAuraEffectsForDisplay(string? rawAuraId)
    {
        return CitizenTraitCatalog.GetEffects(rawAuraId);
    }

    private string FormatTraitEffect(CitizenTraitEffect effect, string language)
    {
        var label = GetCitizenStatLabelStatic(effect.StatId, language);
        var parts = new List<string>();
        if (effect.Additive != 0) parts.Add(FormatSignedDecimal(effect.Additive));
        if (effect.Multiplier != 0) parts.Add(FormatSignedPercent(effect.Multiplier));
        if (effect.BaseMultiplier != 0) parts.Add(FormatSignedPercent(effect.BaseMultiplier));
        if (effect.AdditiveMultiplier != 0) parts.Add(FormatSignedPercent(effect.AdditiveMultiplier));
        if (effect.BonusMultiplier != 0) parts.Add(FormatSignedPercent(effect.BonusMultiplier));
        if (parts.Count == 0) parts.Add("0");
        return $"{label}: {string.Join(" / ", parts)}";
    }

    public static string GetCitizenStatLabelStatic(string statId, string language)
    {
        var normalized = RomesteadItemRecord.NormalizeLanguage(language);
        var zhHans = normalized == "zh-hans";
        var zhHant = normalized == "zh-hant";
        var fallback = statId switch
        {
            "Citizen_Efficiency" => zhHant ? "效率" : zhHans ? "效率" : "Efficiency",
            "Citizen_Expertise" => zhHant ? "專業知識" : zhHans ? "专业知识" : "Expertise",
            "Citizen_Happiness" => zhHant ? "幸福度" : zhHans ? "幸福度" : "Happiness",
            "Citizen_FoodCost" => zhHant ? "食物消耗" : zhHans ? "食物消耗" : "Food cost",
            "Citizen_LoyaltyGain" => zhHant ? "忠誠度收益" : zhHans ? "忠诚度收益" : "Loyalty gain",
            "Citizen_ExperienceGain" => zhHant ? "經驗值收益" : zhHans ? "经验值收益" : "Experience gain",
            _ => statId
        };
        var text = GameTextDb.Get(statId + "*stats:name", language);
        return string.IsNullOrWhiteSpace(text) ? fallback : text;
    }

    private static string FormatSignedDecimal(decimal value)
    {
        var text = value % 1 == 0
            ? ((long)value).ToString(CultureInfo.InvariantCulture)
            : value.ToString("0.###", CultureInfo.InvariantCulture);
        return value > 0 ? "+" + text : text;
    }

    private static string FormatSignedPercent(decimal value)
    {
        var percent = value * 100m;
        var text = percent % 1 == 0
            ? ((long)percent).ToString(CultureInfo.InvariantCulture)
            : percent.ToString("0.###", CultureInfo.InvariantCulture);
        return (percent > 0 ? "+" : "") + text + "%";
    }

    private static string NormalizeCitizenAuraId(string? rawAuraId)
    {
        return CitizenTraitCatalog.NormalizeId(rawAuraId);
    }

    private IEnumerable<string> GetGeneralCitizenAttributeLines(string language)
    {
        var items = new (string Key, string En, string Hans, string Hant, string Value)[]
        {
            ("Citizen_Efficiency*stats:name", "Efficiency", "效率", "效率", NormalizeNumberText(FirstNonEmpty(EfficiencyEdit, Efficiency, BaseEfficiency, "0"))),
            ("Citizen_Expertise*stats:name", "Expertise", "专业知识", "專業知識", NormalizeNumberText(FirstNonEmpty(ExpertiseEdit, Expertise, BaseExpertise, "0"))),
            ("Citizen_Happiness*stats:name", "Happiness", "幸福度", "幸福度", NormalizeNumberText(Happiness)),
            ("Citizen_FoodCost*stats:name", "Food cost", "食物消耗", "食物消耗", NormalizeNumberText(FoodCost)),
            ("Citizen_LoyaltyGain*stats:name", "Loyalty gain", "忠诚度收益", "忠誠度收益", NormalizeNumberText(LoyaltyGain)),
            ("Citizen_ExperienceGain*stats:name", "Experience gain", "经验值收益", "經驗值收益", NormalizeNumberText(ExperienceGain)),
        };
        var normalized = RomesteadItemRecord.NormalizeLanguage(language);
        var zhHans = normalized == "zh-hans";
        var zhHant = normalized == "zh-hant";
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.Value)) continue;
            var text = GameTextDb.Get(item.Key, language);
            if (string.IsNullOrWhiteSpace(text)) text = zhHant ? item.Hant : zhHans ? item.Hans : item.En;
            yield return FormatStatDisplay(text, item.Value);
        }
    }

    private static string NormalizeNumberText(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";

        var v = ToHalfWidthDigits(value.Trim()).Trim();
        if (string.IsNullOrWhiteSpace(v)) return "";

        if (TryFormatDecimal(v, out var formatted)) return formatted;

        var exact = NormalizeNumberToken(v);
        if (!string.IsNullOrWhiteSpace(exact)) return exact;

        // Some values may arrive as "Level IV", "等级：叁", "Citizen_Expertise: II", etc.
        // For numeric display fields we always show ordinary Arabic numerals.
        foreach (Match match in Regex.Matches(v, @"[-+]?\d+(?:[\.,]\d+)?|[ivxlcdmIVXLCDM]+|[ⅠⅡⅢⅣⅤⅥⅦⅧⅨⅩⅪⅫ]+|[零〇一二三四五六七八九十百千万壹贰貳叁參肆伍陆陸柒捌玖拾佰仟萬]+"))
        {
            var token = match.Value;
            if (TryFormatDecimal(token.Replace(',', '.'), out formatted)) return formatted;
            var mapped = NormalizeNumberToken(token);
            if (!string.IsNullOrWhiteSpace(mapped)) return mapped;
        }

        return v;
    }

    private static bool TryFormatDecimal(string value, out string formatted)
    {
        formatted = "";
        if (decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var dec) ||
            decimal.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out dec))
        {
            formatted = dec % 1 == 0
                ? ((long)dec).ToString(CultureInfo.InvariantCulture)
                : dec.ToString("0.###", CultureInfo.InvariantCulture);
            return true;
        }
        return false;
    }

    private static string NormalizeNumberToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return "";
        var t = ToHalfWidthDigits(token.Trim()).Trim();
        if (string.IsNullOrWhiteSpace(t)) return "";

        var lower = t.ToLowerInvariant();
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["zero"] = "0", ["one"] = "1", ["two"] = "2", ["three"] = "3", ["four"] = "4", ["five"] = "5",
            ["six"] = "6", ["seven"] = "7", ["eight"] = "8", ["nine"] = "9", ["ten"] = "10",
            ["i"] = "1", ["ii"] = "2", ["iii"] = "3", ["iv"] = "4", ["v"] = "5", ["vi"] = "6", ["vii"] = "7", ["viii"] = "8", ["ix"] = "9", ["x"] = "10",
            ["Ⅰ"] = "1", ["Ⅱ"] = "2", ["Ⅲ"] = "3", ["Ⅳ"] = "4", ["Ⅴ"] = "5", ["Ⅵ"] = "6", ["Ⅶ"] = "7", ["Ⅷ"] = "8", ["Ⅸ"] = "9", ["Ⅹ"] = "10",
            ["一"] = "1", ["二"] = "2", ["三"] = "3", ["四"] = "4", ["五"] = "5", ["六"] = "6", ["七"] = "7", ["八"] = "8", ["九"] = "9", ["十"] = "10",
            ["壹"] = "1", ["贰"] = "2", ["貳"] = "2", ["叁"] = "3", ["參"] = "3", ["肆"] = "4", ["伍"] = "5", ["陆"] = "6", ["陸"] = "6", ["柒"] = "7", ["捌"] = "8", ["玖"] = "9", ["拾"] = "10"
        };
        if (map.TryGetValue(lower, out var mapped)) return mapped;
        if (map.TryGetValue(t, out mapped)) return mapped;

        var chinese = TryParseChineseInteger(t);
        if (chinese.HasValue) return chinese.Value.ToString(CultureInfo.InvariantCulture);

        var roman = TryParseRomanInteger(t);
        if (roman.HasValue) return roman.Value.ToString(CultureInfo.InvariantCulture);

        return "";
    }

    private static string ToHalfWidthDigits(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var chars = value.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (chars[i] >= '０' && chars[i] <= '９') chars[i] = (char)('0' + (chars[i] - '０'));
            else if (chars[i] == '＋') chars[i] = '+';
            else if (chars[i] == '－' || chars[i] == '−') chars[i] = '-';
            else if (chars[i] == '．') chars[i] = '.';
        }
        return new string(chars);
    }

    private static int? TryParseRomanInteger(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var roman = value.Trim().ToUpperInvariant()
            .Replace("Ⅰ", "I").Replace("Ⅱ", "II").Replace("Ⅲ", "III").Replace("Ⅳ", "IV").Replace("Ⅴ", "V")
            .Replace("Ⅵ", "VI").Replace("Ⅶ", "VII").Replace("Ⅷ", "VIII").Replace("Ⅸ", "IX").Replace("Ⅹ", "X");
        if (!Regex.IsMatch(roman, "^[IVXLCDM]+$")) return null;

        var values = new Dictionary<char, int> { ['I'] = 1, ['V'] = 5, ['X'] = 10, ['L'] = 50, ['C'] = 100, ['D'] = 500, ['M'] = 1000 };
        var total = 0;
        var previous = 0;
        for (int i = roman.Length - 1; i >= 0; i--)
        {
            if (!values.TryGetValue(roman[i], out var current)) return null;
            if (current < previous) total -= current;
            else { total += current; previous = current; }
        }
        return total > 0 ? total : null;
    }

    private static int? TryParseChineseInteger(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var s = value.Trim();
        var digit = new Dictionary<char, int>
        {
            ['零'] = 0, ['〇'] = 0, ['一'] = 1, ['二'] = 2, ['三'] = 3, ['四'] = 4, ['五'] = 5, ['六'] = 6, ['七'] = 7, ['八'] = 8, ['九'] = 9,
            ['壹'] = 1, ['贰'] = 2, ['貳'] = 2, ['叁'] = 3, ['參'] = 3, ['肆'] = 4, ['伍'] = 5, ['陆'] = 6, ['陸'] = 6, ['柒'] = 7, ['捌'] = 8, ['玖'] = 9
        };
        var unit = new Dictionary<char, int> { ['十'] = 10, ['拾'] = 10, ['百'] = 100, ['佰'] = 100, ['千'] = 1000, ['仟'] = 1000, ['万'] = 10000, ['萬'] = 10000 };
        if (s.All(ch => digit.ContainsKey(ch)))
        {
            var result = 0;
            foreach (var ch in s) result = result * 10 + digit[ch];
            return result;
        }
        if (!s.All(ch => digit.ContainsKey(ch) || unit.ContainsKey(ch))) return null;

        var total = 0;
        var section = 0;
        var number = 0;
        foreach (var ch in s)
        {
            if (digit.TryGetValue(ch, out var d))
            {
                number = d;
            }
            else if (unit.TryGetValue(ch, out var u))
            {
                if (u == 10000)
                {
                    section = (section + number) * u;
                    total += section;
                    section = 0;
                }
                else
                {
                    section += (number == 0 ? 1 : number) * u;
                }
                number = 0;
            }
        }
        return total + section + number;
    }

    private static string FirstNonEmpty(params string[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";
    private static string IdLine(string value) => string.IsNullOrWhiteSpace(value) ? "" : $"id:{value}";
    private static string JoinSmall(params string[] values) => string.Join("  ", values.Where(v => !string.IsNullOrWhiteSpace(v)));
    private static string PrefixValue(string prefix, string value)
    {
        var normalized = NormalizeNumberText(value);
        return string.IsNullOrWhiteSpace(normalized) ? "" : $"{prefix}: {normalized}";
    }

    private static string FormatStatDisplay(string label, string value)
    {
        var title = string.IsNullOrWhiteSpace(label) ? "—" : label;
        var normalized = NormalizeNumberText(value);
        return string.IsNullOrWhiteSpace(normalized) ? $"{title}: —" : $"{title}: {normalized}";
    }

    private static string ResolveGameText(string idOrKey, string language)
    {
        if (string.IsNullOrWhiteSpace(idOrKey)) return string.Empty;
        var key = idOrKey.Trim();
        return GameTextDb.GetAny(key, language, "*name", "*citizenaura:name", "*job:name", "*stats:name");
    }

    private static string ResolveJob(string jobId, string language)
    {
        if (string.IsNullOrWhiteSpace(jobId)) return string.Empty;
        var raw = jobId.Trim();
        var value = GameTextDb.GetAny(raw, language, "*job:name");
        if (!string.IsNullOrWhiteSpace(value)) return value;
        if (!raw.Contains(':'))
        {
            value = GameTextDb.GetAny("job:" + raw, language, "*job:name");
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return string.Empty;
    }

    private static string ResolveTraitList(string traitIds, string auraIds, string language)
    {
        var ids = SplitIds(FirstNonEmpty(traitIds, auraIds)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (ids.Count == 0) return string.Empty;
        var values = new List<string>();
        foreach (var id in ids)
        {
            values.Add(ResolveTraitText(id, language));
        }
        var normalized = RomesteadItemRecord.NormalizeLanguage(language);
        var separator = normalized == "zh-hans" || normalized == "zh-hant" ? "、" : ", ";
        return string.Join(separator, values.Where(v => !string.IsNullOrWhiteSpace(v)));
    }

    private static string ResolveTraitText(string id, string language)
    {
        if (string.IsNullOrWhiteSpace(id)) return string.Empty;
        var raw = id.Trim();
        var text = GameTextDb.GetAny(raw, language, "*citizenaura:name");
        if (!string.IsNullOrWhiteSpace(text)) return text;

        if (!raw.Contains(':'))
        {
            foreach (var prefix in new[] { "citizen_aura:buff:", "citizen_aura:debuff:", "citizen_aura:trait:", "trait:" })
            {
                text = GameTextDb.GetAny(prefix + raw, language, "*citizenaura:name");
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }
        }

        var normalized = RomesteadItemRecord.NormalizeLanguage(language);
        if (normalized == "zh-hans" && TraitRawZhHans.TryGetValue(raw, out var hans)) return hans;
        if (normalized == "zh-hant" && TraitRawZhHant.TryGetValue(raw, out var hant)) return hant;
        return HumanizeId(raw);
    }

    private static string LocalizeKind(string value, bool traditional)
    {
        var v = (value ?? "").Trim().ToLowerInvariant();
        if (v.Contains("wild")) return traditional ? "野外村民" : "野外村民";
        if (v.Contains("citizen")) return traditional ? "村民" : "村民";
        return HumanizeId(value);
    }

    private static string LocalizeJob(string value, bool traditional)
    {
        var v = (value ?? "").Trim().ToLowerInvariant();
        var map = traditional ? JobZhHant : JobZhHans;
        if (map.TryGetValue(v, out var text)) return text;
        if (!v.Contains(':') && map.TryGetValue("job:" + v, out text)) return text;
        if (v.StartsWith("job:"))
        {
            var parts = v.Split(':', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && map.TryGetValue("job:" + parts[1], out text)) return text;
        }
        return HumanizeId(value);
    }

    private static string LocalizeStatusLine(string status, bool traditional)
    {
        if (string.IsNullOrWhiteSpace(status)) return "";
        var v = status.Trim().ToLowerInvariant();
        var text = v switch
        {
            "idle" or "status:idle" => traditional ? "狀態: 空閒" : "状态: 空闲",
            "working" or "status:working" => traditional ? "狀態: 工作中" : "状态: 工作中",
            "unemployed" or "status:unemployed" => traditional ? "狀態: 無業" : "状态: 无业",
            _ => traditional ? $"狀態: {HumanizeId(status)}" : $"状态: {HumanizeId(status)}"
        };
        return text;
    }

    private static string LocalizePersonalityLine(string personality, string background, bool traditional)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(personality)) parts.Add((traditional ? "性格" : "性格") + ": " + HumanizeId(personality));
        if (!string.IsNullOrWhiteSpace(background)) parts.Add((traditional ? "背景" : "背景") + ": " + HumanizeId(background));
        return string.Join("  ", parts);
    }

    private static string LocalizeTraitList(string value, bool traditional)
    {
        if (string.IsNullOrWhiteSpace(value)) return traditional ? "無特質" : "无特质";
        return string.Join("、", SplitIds(value).Select(v => LocalizeTrait(v, traditional)).Where(v => !string.IsNullOrWhiteSpace(v)));
    }

    private static string HumanizeTraitList(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        return string.Join(", ", SplitIds(value).Select(HumanizeId));
    }

    private static string LocalizeTrait(string value, bool traditional)
    {
        var v = (value ?? "").Trim().ToLowerInvariant();
        if (TraitZhHans.TryGetValue(v, out var hans)) return traditional ? ToTraditionalTrait(hans) : hans;
        return HumanizeId(value);
    }

    private static IEnumerable<string> SplitIds(string value)
    {
        return (value ?? "").Split(new[] { ';', ',', '|', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string HumanizeId(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var tail = value.Trim();
        if (tail.Contains(':')) tail = tail.Split(':', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? tail;
        tail = tail.Replace('_', ' ').Replace('-', ' ');
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(tail);
    }

    private static string ToTraditionalTrait(string hans)
    {
        return hans
            .Replace("专", "專")
            .Replace("业", "業")
            .Replace("强", "強")
            .Replace("迟", "遲")
            .Replace("逊", "遜")
            .Replace("乐", "樂")
            .Replace("气", "氣")
            .Replace("优", "優")
            .Replace("负", "負")
            .Replace("稳", "穩");
    }

    private static readonly Dictionary<string, string> JobZhHans = new(StringComparer.OrdinalIgnoreCase)
    {
        ["job:farmer"] = "农民",
        ["job:leatherworker"] = "皮匠",
        ["job:lumberjack"] = "伐木工",
        ["job:carpenter"] = "木匠",
        ["job:unemployed"] = "无业",
        ["job:baker"] = "面包师",
        ["job:blacksmith"] = "铁匠",
        ["job:merchant"] = "商人",
        ["job:digger"] = "掘土工",
        ["job:miner"] = "矿工",
        ["job:potter"] = "陶工",
        ["job:sculptor"] = "雕刻师",
        ["job:philosopher"] = "学者"
    };

    private static readonly Dictionary<string, string> JobZhHant = new(StringComparer.OrdinalIgnoreCase)
    {
        ["job:farmer"] = "農民",
        ["job:leatherworker"] = "皮匠",
        ["job:lumberjack"] = "伐木工",
        ["job:carpenter"] = "木匠",
        ["job:unemployed"] = "無業",
        ["job:baker"] = "麵包師",
        ["job:blacksmith"] = "鐵匠",
        ["job:merchant"] = "商人",
        ["job:digger"] = "掘土工",
        ["job:miner"] = "礦工",
        ["job:potter"] = "陶工",
        ["job:sculptor"] = "雕刻師",
        ["job:philosopher"] = "學者"
    };

    private static readonly Dictionary<string, string> TraitZhHans = new(StringComparer.OrdinalIgnoreCase)
    {
        ["trait:hard_worker"] = "勤劳",
        ["trait:lazy"] = "懒惰",
        ["trait:efficient"] = "高效率",
        ["trait:expert"] = "专业",
        ["trait:slow"] = "迟缓",
        ["trait:strong"] = "强壮",
        ["trait:weak"] = "虚弱",
        ["trait:happy"] = "乐观",
        ["trait:unhappy"] = "消极",
        ["trait:loyal"] = "忠诚",
        ["trait:disloyal"] = "低忠诚",
        ["trait:hungry"] = "饥饿",
        ["trait:injured"] = "受伤"
    };


    private static readonly Dictionary<string, string> TraitRawZhHans = new(StringComparer.OrdinalIgnoreCase)
    {
        ["efficiency"] = "效率",
        ["experience"] = "经验",
        ["expertise"] = "专业知识",
        ["loyalty"] = "忠诚",
        ["happiness"] = "幸福",
        ["food"] = "食物",
        ["food_cost"] = "食物消耗",
        ["town_defence"] = "城镇防御",
        ["anxiety"] = "焦虑",
        ["injury"] = "受伤",
        ["gift"] = "礼物",
        ["cornucopia"] = "丰饶号角",
        ["venus_citizen_expertise"] = "维纳斯专业知识",
        ["venus_embrace"] = "维纳斯的拥抱"
    };

    private static readonly Dictionary<string, string> TraitRawZhHant = new(StringComparer.OrdinalIgnoreCase)
    {
        ["efficiency"] = "效率",
        ["experience"] = "經驗",
        ["expertise"] = "專業知識",
        ["loyalty"] = "忠誠",
        ["happiness"] = "幸福",
        ["food"] = "食物",
        ["food_cost"] = "食物消耗",
        ["town_defence"] = "城鎮防禦",
        ["anxiety"] = "焦慮",
        ["injury"] = "受傷",
        ["gift"] = "禮物",
        ["cornucopia"] = "豐饒號角",
        ["venus_citizen_expertise"] = "維納斯專業知識",
        ["venus_embrace"] = "維納斯的擁抱"
    };

    private void NotifyDisplayFields()
    {
        foreach (var name in new[]
        {
            nameof(KindPrimary), nameof(KindSecondary), nameof(NamePrimary), nameof(NameSecondary),
            nameof(JobPrimary), nameof(JobSecondary), nameof(JobLevelPrimary), nameof(JobLevelSecondary),
            nameof(EfficiencyPrimary), nameof(EfficiencySecondary), nameof(ExpertisePrimary), nameof(ExpertiseSecondary),
            nameof(LoyaltyPrimary), nameof(LoyaltySecondary), nameof(TraitsPrimary), nameof(TraitsSecondary),
            nameof(TraitTooltip), nameof(PrimaryFontSize), nameof(SecondaryFontSize)
        })
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class PlayerSkillRow : INotifyPropertyChanged
{
    private string _levelText = "0";
    private string _experienceText = "0";

    public string SourceFile { get; set; } = "";
    public string SaveFileName { get; set; } = "";
    public string PlayerName { get; set; } = "";
    public int PlayerId { get; set; }
    public string SkillId { get; set; } = "";
    public string NameEn { get; set; } = "";
    public string DescriptionEn { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";

    public int Level
    {
        get => ToInt(_levelText);
        set => LevelText = value.ToString(CultureInfo.InvariantCulture);
    }

    public int OriginalLevel { get; set; }

    public string LevelText
    {
        get => _levelText;
        set
        {
            if (_levelText == value) return;
            _levelText = value;
            OnChanged(nameof(LevelText));
            OnChanged(nameof(Level));
            OnChanged(nameof(IsChanged));
        }
    }

    public float CurrentExperience
    {
        get => ToFloat(_experienceText);
        set => CurrentExperienceText = value.ToString(CultureInfo.InvariantCulture);
    }

    public float OriginalCurrentExperience { get; set; }

    public string CurrentExperienceText
    {
        get => _experienceText;
        set
        {
            if (_experienceText == value) return;
            _experienceText = value;
            OnChanged(nameof(CurrentExperienceText));
            OnChanged(nameof(CurrentExperience));
            OnChanged(nameof(IsChanged));
        }
    }

    public float ExperienceRequiredToLevelUp { get; set; }
    public float CurrentValue { get; set; }

    public bool IsChanged =>
        (TryGetLevel(out var level) && level != OriginalLevel)
        || (TryGetCurrentExperience(out var exp) && Math.Abs(exp - OriginalCurrentExperience) > 0.0001f);

    public void ApplyLanguage(string language)
    {
        DisplayName = NameEn;
        Description = DescriptionEn;
        OnChanged(nameof(DisplayName));
        OnChanged(nameof(Description));
        OnChanged(nameof(IsChanged));
    }

    public bool TryValidateForSave(out string message)
    {
        message = "";
        if (!TryGetLevel(out var level))
        {
            message = $"Invalid level for {SkillId}.";
            return false;
        }
        if (level < 0 || level > 100)
        {
            message = $"Skill level for {SkillId} must be between 0 and 100.";
            return false;
        }
        if (!TryGetCurrentExperience(out var exp))
        {
            message = $"Invalid experience for {SkillId}.";
            return false;
        }
        if (exp < 0)
        {
            message = $"Skill experience for {SkillId} cannot be negative.";
            return false;
        }
        return true;
    }


    public void AcceptSavedState()
    {
        OriginalLevel = GetLevelOrThrow();
        OriginalCurrentExperience = GetCurrentExperienceOrThrow();
        OnChanged(nameof(IsChanged));
    }

    public bool TryGetLevel(out int value) => int.TryParse(LevelText, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    public bool TryGetCurrentExperience(out float value) => float.TryParse(CurrentExperienceText, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    public int GetLevelOrThrow()
    {
        if (!TryGetLevel(out var value)) throw new InvalidOperationException($"Invalid level for {SkillId}: {LevelText}");
        return value;
    }

    public float GetCurrentExperienceOrThrow()
    {
        if (!TryGetCurrentExperience(out var value)) throw new InvalidOperationException($"Invalid experience for {SkillId}: {CurrentExperienceText}");
        return value;
    }

    private static int ToInt(string value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
    private static float ToFloat(string value) => float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 0f;
    private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class PlayerItemRow : INotifyPropertyChanged
{
    private string _stackCountText = "0";
    private string _baseDataId = "";

    public string SourceFile { get; set; } = "";
    public string SaveFileName { get; set; } = "";
    public string PlayerName { get; set; } = "";
    public int PlayerId { get; set; }
    public string Section { get; set; } = "";
    public int SlotIndex { get; set; }
    public string InstanceId { get; set; } = "";

    public string OriginalBaseDataId { get; set; } = "";

    public string BaseDataId
    {
        get => _baseDataId;
        set
        {
            var next = value?.Trim() ?? "";
            if (_baseDataId == next) return;
            _baseDataId = next;
            OnChanged(nameof(BaseDataId));
            OnChanged(nameof(IsChanged));
        }
    }

    public string ItemName { get; set; } = "";
    public string Category { get; set; } = "";
    public int MaxStackCount { get; set; } = 0;
    public string Safety { get; set; } = "";

    public int StackCount
    {
        get => ToInt(_stackCountText);
        set => StackCountText = value.ToString(CultureInfo.InvariantCulture);
    }

    public int OriginalStackCount { get; set; }
    private string _auraIdsEdit = "";
    public string OriginalAuraIds { get; set; } = "";
    public string AuraIdsEdit
    {
        get => _auraIdsEdit;
        set
        {
            var next = value?.Trim() ?? "";
            if (_auraIdsEdit == next) return;
            _auraIdsEdit = next;
            OnChanged(nameof(AuraIdsEdit));
            OnChanged(nameof(AuraSummary));
            OnChanged(nameof(AuraTooltip));
            OnChanged(nameof(IsChanged));
        }
    }
    public string AuraSummary { get; private set; } = "";
    public string AuraTooltip { get; private set; } = "";
    public bool CanEditAuras => ItemAuraCatalog.IsAuraEditableItem(BaseDataId);

    public string StackCountText
    {
        get => _stackCountText;
        set
        {
            if (_stackCountText == value) return;
            _stackCountText = value;
            OnChanged(nameof(StackCountText));
            OnChanged(nameof(StackCount));
            OnChanged(nameof(IsChanged));
        }
    }

    public bool IsChanged =>
        !string.Equals((OriginalBaseDataId ?? "").Trim(), (BaseDataId ?? "").Trim(), StringComparison.OrdinalIgnoreCase)
        || (TryGetStackCount(out var n) && n != OriginalStackCount)
        || !ItemAuraCatalog.NormalizeList(OriginalAuraIds, keepUnknown: true).Equals(ItemAuraCatalog.NormalizeList(AuraIdsEdit, keepUnknown: true), StringComparison.OrdinalIgnoreCase);

    public void ApplyDatabase(string language)
    {
        if (string.IsNullOrWhiteSpace(BaseDataId))
        {
            ItemName = "";
            Category = "";
            Safety = "";
            MaxStackCount = 0;
        }
        else
        {
            var item = RomesteadItemDatabase.Find(BaseDataId);
            if (item == null)
            {
                ItemName = "Unknown item ID";
                Category = "";
                Safety = "";
                MaxStackCount = 0;
            }
            else
            {
                ItemName = item.GetName(language);
                Category = item.GetCategory(language);
                Safety = ItemDatabaseUiText.LocalizedSafety(item, language);
                MaxStackCount = item.EditorMaxStackSize;
            }
        }

        OnChanged(nameof(ItemName));
        OnChanged(nameof(Category));
        UpdateAuraDisplay(language);
        OnChanged(nameof(Safety));
        OnChanged(nameof(MaxStackCount));
        OnChanged(nameof(CanEditAuras));
        OnChanged(nameof(IsChanged));
    }


    private void UpdateAuraDisplay(string language)
    {
        var ids = ItemAuraCatalog.SplitIds(AuraIdsEdit).ToList();
        if (ids.Count == 0)
        {
            AuraSummary = "";
            AuraTooltip = "";
        }
        else
        {
            var names = ids.Select(id =>
            {
                var aura = ItemAuraCatalog.Find(id);
                return aura == null ? id : ItemAuraCatalog.DisplayLabel(aura, language);
            }).ToList();
            AuraSummary = string.Join("; ", names);
            AuraTooltip = string.Join(Environment.NewLine, ids.Select(id =>
            {
                var aura = ItemAuraCatalog.Find(id);
                return aura == null ? id : $"{ItemAuraCatalog.DisplayLabel(aura, language)}\n{ItemAuraCatalog.LocalizeEffects(aura.Effects, language)}";
            }));
        }
        OnChanged(nameof(AuraSummary));
        OnChanged(nameof(AuraTooltip));
    }

    public string GetAuraIdsForSave()
    {
        return ItemAuraCatalog.NormalizeList(AuraIdsEdit, keepUnknown: false);
    }

    public void ClampStackCountToDatabaseLimit()
    {
        if (!TryGetStackCount(out var value)) return;
        if (string.IsNullOrWhiteSpace(BaseDataId))
        {
            if (value != 0) StackCount = 0;
            return;
        }

        if (value <= 0) { StackCount = 1; return; }
        if (IsEquipmentSection(Section) && IsEquipmentSlotItemId(BaseDataId))
        {
            if (value != 1) StackCount = 1;
            return;
        }
        if (MaxStackCount <= 0) return;
        if (value > MaxStackCount) StackCount = MaxStackCount;
    }

    public bool TryValidateForSave(out string message)
    {
        message = "";
        var id = (BaseDataId ?? "").Trim();
        if (!TryGetStackCount(out var value))
        {
            message = $"Invalid count at {Section} slot {SlotIndex}.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            if (value != 0)
            {
                message = $"Empty item ID must have count 0 at {Section} slot {SlotIndex}.";
                return false;
            }
            return true;
        }

        var item = RomesteadItemDatabase.Find(id);
        if (item == null)
        {
            message = $"Unknown item ID: {id}. Item type must be an English ID from the item database.";
            return false;
        }

        if (value <= 0)
        {
            message = $"Count must be greater than 0 for {id}.";
            return false;
        }

        if (value > item.EditorMaxStackSize)
        {
            message = $"Count for {id} exceeds max stack {item.EditorMaxStackSize}.";
            return false;
        }

        if (IsEquipmentSection(Section))
        {
            if (!IsEquipmentSlotItemId(id))
            {
                message = $"{Section} slot {SlotIndex} only accepts equipment IDs such as weapon, armor, trinket, axe, pickaxe, back, or torch. {id} is not equipment.";
                return false;
            }
            if (value != 1)
            {
                message = $"{Section} slot {SlotIndex} must have count 1 for equipment item {id}.";
                return false;
            }
        }

        var auraIds = ItemAuraCatalog.SplitIds(AuraIdsEdit).ToList();
        if (auraIds.Count > 0)
        {
            if (!ItemAuraCatalog.IsAuraEditableItem(id))
            {
                message = $"Auras can only be edited for equipment items. {id} is not supported.";
                return false;
            }
            var category = ItemAuraCatalog.GetItemAuraCategory(id);
            foreach (var auraId in auraIds)
            {
                var aura = ItemAuraCatalog.Find(auraId);
                if (aura == null)
                {
                    message = $"Unknown item aura ID: {auraId}.";
                    return false;
                }
                if (!string.IsNullOrWhiteSpace(category) && !aura.Category.Equals(category, StringComparison.OrdinalIgnoreCase))
                {
                    message = $"Aura {auraId} is for {aura.Category}, but {id} expects {category}.";
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsEquipmentSection(string? section)
    {
        var value = (section ?? string.Empty).Trim();
        return value.Equals("Equipment", StringComparison.OrdinalIgnoreCase) || value.Equals("SecondaryEquipment", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsEquipmentSlotItemId(string? id)
    {
        var value = (id ?? string.Empty).Trim().ToLowerInvariant();
        return value.StartsWith("weapon:") || value.StartsWith("armor:") || value.StartsWith("trinket:") ||
               value.StartsWith("axe:") || value.StartsWith("pickaxe:") || value.StartsWith("back:") || value.StartsWith("torch:");
    }


    public void AcceptSavedState(string language)
    {
        OriginalBaseDataId = (BaseDataId ?? string.Empty).Trim();
        OriginalStackCount = GetStackCountOrDefault();
        OriginalAuraIds = GetAuraIdsForSave();
        AuraIdsEdit = OriginalAuraIds;
        ApplyDatabase(language);
        OnChanged(nameof(IsChanged));
    }

    public bool TryGetStackCount(out int value)
    {
        return int.TryParse(StackCountText, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    public int GetStackCountOrDefault()
    {
        return TryGetStackCount(out var value) ? value : 0;
    }

    public int GetStackCountOrThrow()
    {
        if (!TryGetStackCount(out var value)) throw new InvalidOperationException($"Invalid StackCount for {BaseDataId}: {StackCountText}");
        return value;
    }

    private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private void PlayerMoneyBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox box) return;
        var text = (box.Text ?? "").Trim();
        if (text.Length == 0) return;
        if (!ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            // Leave the text as-is so the user can correct it; validation happens on Save.
            return;
        }
    }

    private static int ToInt(string value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
    public event PropertyChangedEventHandler? PropertyChanged;
}
