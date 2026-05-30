using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;

namespace RomesteadSaveInspector.Database;

public sealed class RuntimeGameTextEntry
{
    public string Key { get; set; } = string.Empty;
    public string En { get; set; } = string.Empty;
    public string ZhHans { get; set; } = string.Empty;
    public string ZhHant { get; set; } = string.Empty;
}

public sealed class RuntimeCitizenTraitRecord
{
    public string Id { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public List<RuntimeCitizenTraitEffect> Effects { get; set; } = new();
}

public sealed class RuntimeCitizenTraitEffect
{
    public string StatId { get; set; } = string.Empty;
    public decimal Additive { get; set; }
    public decimal Multiplier { get; set; }
    public decimal BaseMultiplier { get; set; }
    public decimal AdditiveMultiplier { get; set; }
    public decimal BonusMultiplier { get; set; }
}

public static class RuntimeGameDatabase
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private static readonly Dictionary<string, RuntimeGameTextEntry> TextByKey = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<RomesteadItemRecord> ItemRecords = new();
    private static readonly List<RuntimeCitizenTraitRecord> CitizenTraitRecords = new();
    private static bool _assemblyResolverInstalled;

    public static string DataDirectory { get; private set; } = string.Empty;
    public static string LastLoadedGameDirectory { get; private set; } = string.Empty;
    public static IReadOnlyList<RomesteadItemRecord> Items => ItemRecords;
    public static IReadOnlyList<RuntimeCitizenTraitRecord> CitizenTraits => CitizenTraitRecords;
    public static IReadOnlyDictionary<string, RuntimeGameTextEntry> Texts => TextByKey;

    public static string ItemDatabasePath => Path.Combine(DataDirectory, "romestead_items.json");
    public static string GameTextDatabasePath => Path.Combine(DataDirectory, "game_text.json");
    public static string CitizenTraitsDatabasePath => Path.Combine(DataDirectory, "citizen_traits.json");

    public static void Initialize(string dataDirectory, string? gameDirectory = null)
    {
        DataDirectory = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(DataDirectory);
        LoadDataFiles();
        if (!string.IsNullOrWhiteSpace(gameDirectory) && Directory.Exists(gameDirectory))
        {
            TryRefreshFromGameDirectory(gameDirectory, writeFiles: true);
            LoadDataFiles();
        }
    }

    public static void LoadDataFiles()
    {
        ItemRecords.Clear();
        TextByKey.Clear();
        CitizenTraitRecords.Clear();

        if (File.Exists(ItemDatabasePath))
        {
            var items = JsonSerializer.Deserialize<List<RomesteadItemRecord>>(File.ReadAllText(ItemDatabasePath, Encoding.UTF8), JsonOptions);
            if (items != null)
            {
                ItemRecords.AddRange(items.Where(i => !string.IsNullOrWhiteSpace(i.Id)));
            }
        }

        if (File.Exists(GameTextDatabasePath))
        {
            var texts = JsonSerializer.Deserialize<List<RuntimeGameTextEntry>>(File.ReadAllText(GameTextDatabasePath, Encoding.UTF8), JsonOptions);
            if (texts != null)
            {
                foreach (var entry in texts.Where(t => !string.IsNullOrWhiteSpace(t.Key)))
                {
                    TextByKey[entry.Key.Trim()] = entry;
                }
            }
        }

        if (File.Exists(CitizenTraitsDatabasePath))
        {
            var traits = JsonSerializer.Deserialize<List<RuntimeCitizenTraitRecord>>(File.ReadAllText(CitizenTraitsDatabasePath, Encoding.UTF8), JsonOptions);
            if (traits != null)
            {
                CitizenTraitRecords.AddRange(traits.Where(t => !string.IsNullOrWhiteSpace(t.Id)));
            }
        }
    }

    public static bool TryRefreshFromGameDirectory(string? gameDirectory, bool writeFiles)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory) || !Directory.Exists(gameDirectory)) return false;
        var gameDir = Path.GetFullPath(gameDirectory.Trim().Trim('"'));
        LastLoadedGameDirectory = gameDir;

        var changed = false;
        var localeEntries = TryReadOfficialLocales(gameDir);
        if (localeEntries.Count > 0)
        {
            foreach (var entry in localeEntries.Values)
                TextByKey[entry.Key] = entry;
            if (writeFiles) WriteGameTextDatabase();
            changed = true;
        }

        var extractedItems = TryExtractItemsFromSharedDll(gameDir, localeEntries.Count > 0 ? localeEntries : TextByKey);
        if (extractedItems.Count > 0)
        {
            ItemRecords.Clear();
            ItemRecords.AddRange(extractedItems.OrderBy(i => i.Id, StringComparer.OrdinalIgnoreCase));
            if (writeFiles) WriteItemDatabase();
            changed = true;
        }

        return changed;
    }

    public static string GetText(string key, string? language)
    {
        if (string.IsNullOrWhiteSpace(key)) return string.Empty;
        if (!TextByKey.TryGetValue(key.Trim(), out var entry)) return string.Empty;
        return RomesteadItemRecord.NormalizeLanguage(language) switch
        {
            "zh-hans" => entry.ZhHans,
            "zh-hant" => entry.ZhHant,
            _ => entry.En
        };
    }

    private static void WriteItemDatabase()
    {
        Directory.CreateDirectory(DataDirectory);
        File.WriteAllText(ItemDatabasePath, JsonSerializer.Serialize(ItemRecords, JsonOptions), Encoding.UTF8);
    }

    private static void WriteGameTextDatabase()
    {
        Directory.CreateDirectory(DataDirectory);
        var texts = TextByKey.Values.OrderBy(t => t.Key, StringComparer.OrdinalIgnoreCase).ToList();
        File.WriteAllText(GameTextDatabasePath, JsonSerializer.Serialize(texts, JsonOptions), Encoding.UTF8);
    }

    private static Dictionary<string, RuntimeGameTextEntry> TryReadOfficialLocales(string gameDir)
    {
        var locDir = Path.Combine(gameDir, "Content", "localization");
        var en = ReadLocaleFile(Path.Combine(locDir, "locale_en"));
        var zhHans = ReadLocaleFile(Path.Combine(locDir, "locale_zh_CN"));
        var zhHant = ReadLocaleFile(Path.Combine(locDir, "locale_zh_TW"));
        var keys = new HashSet<string>(en.Keys, StringComparer.OrdinalIgnoreCase);
        keys.UnionWith(zhHans.Keys);
        keys.UnionWith(zhHant.Keys);

        var result = new Dictionary<string, RuntimeGameTextEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
        {
            result[key] = new RuntimeGameTextEntry
            {
                Key = key,
                En = en.TryGetValue(key, out var ev) ? ev : key,
                ZhHans = zhHans.TryGetValue(key, out var sv) ? sv : (en.TryGetValue(key, out ev) ? ev : key),
                ZhHant = zhHant.TryGetValue(key, out var tv) ? tv : (en.TryGetValue(key, out ev) ? ev : key)
            };
        }
        return result;
    }

    private static Dictionary<string, string> ReadLocaleFile(string path)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return dict;
        try
        {
            using var fs = File.OpenRead(path);
            using var reader = new BinaryReader(fs, Encoding.UTF8, leaveOpen: false);
            var count = reader.ReadInt32();
            for (var i = 0; i < count && fs.Position < fs.Length; i++)
            {
                var key = reader.ReadString();
                var value = reader.ReadString();
                if (!string.IsNullOrWhiteSpace(key)) dict[key] = value;
            }
        }
        catch
        {
            // Keep database refresh non-fatal. Existing database files stay usable.
        }
        return dict;
    }

    private static List<RomesteadItemRecord> TryExtractItemsFromSharedDll(string gameDir, IReadOnlyDictionary<string, RuntimeGameTextEntry> textDb)
    {
        var result = new List<RomesteadItemRecord>();
        var sharedPath = Path.Combine(gameDir, "Shared.dll");
        if (!File.Exists(sharedPath)) return result;

        try
        {
            InstallAssemblyResolver(gameDir);
            foreach (var dll in new[] { "CandideCreator.Shared.dll", "MonoGame.Framework.dll", "Shared.dll" })
            {
                var path = Path.Combine(gameDir, dll);
                if (File.Exists(path)) TryLoadAssembly(path);
            }

            var shared = TryLoadAssembly(sharedPath);
            var setupType = shared.GetType("Shared.Data.SharedDataSetup") ?? shared.GetType("Shared.Data.Items.SharedDataSetup");
            var setupItems = setupType?.GetMethod("SetupItems", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, Type.EmptyTypes);
            if (setupItems == null) return result;

            var itemsObj = setupItems.Invoke(null, null);
            if (itemsObj is not System.Collections.IEnumerable enumerable) return result;

            foreach (var item in enumerable)
            {
                var id = ReadMemberString(item, "Id");
                if (string.IsNullOrWhiteSpace(id)) continue;
                var nameKey = ReadMemberTextId(item, "Name");
                var descKey = ReadMemberTextId(item, "Description");
                var prefix = id.Contains(':') ? id.Split(':')[0] : id;
                var gameMax = ReadMemberInt(item, "MaxStackSize", 1);
                var category = BuildCategory(prefix);
                var equipment = ReadMemberObject(item, "Equippable");
                var usable = ReadMemberObject(item, "Usable");
                var tradable = ReadMemberObject(item, "Tradable");
                var random = ReadMemberObject(item, "RandomlyGenerated");
                var light = ReadMemberObject(item, "LightSource");
                var citizenConsumable = ReadMemberObject(item, "CitizenConsumable");
                var fuel = ReadMemberObject(item, "Fuel");
                var editorMax = ShouldForceOne(prefix, id) ? 1 : Math.Max(1, gameMax);

                string Localize(string? key, string lang, params string[] alternatives)
                {
                    foreach (var candidate in new[] { key }.Concat(alternatives).Where(v => !string.IsNullOrWhiteSpace(v)))
                    {
                        if (textDb.TryGetValue(candidate!, out var entry))
                        {
                            return RomesteadItemRecord.NormalizeLanguage(lang) switch
                            {
                                "zh-hans" => entry.ZhHans,
                                "zh-hant" => entry.ZhHant,
                                _ => entry.En
                            };
                        }
                    }
                    return string.Empty;
                }

                var nameEn = Localize(nameKey, "en", id + "*item:name", id + "*name");
                var nameZhHans = Localize(nameKey, "zh-hans", id + "*item:name", id + "*name");
                var nameZhHant = Localize(nameKey, "zh-hant", id + "*item:name", id + "*name");
                if (string.IsNullOrWhiteSpace(nameEn)) nameEn = !string.IsNullOrWhiteSpace(nameKey) ? nameKey : id;
                if (string.IsNullOrWhiteSpace(nameZhHans)) nameZhHans = nameEn;
                if (string.IsNullOrWhiteSpace(nameZhHant)) nameZhHant = nameEn;

                result.Add(new RomesteadItemRecord
                {
                    Id = id,
                    Name = nameEn,
                    NameEn = nameEn,
                    NameZhHans = nameZhHans,
                    NameZhHant = nameZhHant,
                    Description = Localize(descKey, "en", id + "*item:description", id + "*description"),
                    DescriptionEn = Localize(descKey, "en", id + "*item:description", id + "*description"),
                    DescriptionZhHans = Localize(descKey, "zh-hans", id + "*item:description", id + "*description"),
                    DescriptionZhHant = Localize(descKey, "zh-hant", id + "*item:description", id + "*description"),
                    Icon = ReadMemberString(item, "Icon"),
                    Category = category.en,
                    CategoryEn = category.en,
                    CategoryZhHans = category.zhHans,
                    CategoryZhHant = category.zhHant,
                    Prefix = prefix,
                    Flags = ReadMemberString(item, "Flags"),
                    EquipmentType = ReadMemberString(equipment, "Type"),
                    EquipmentMaterial = ReadMemberString(equipment, "Material"),
                    SpellId = ReadMemberString(usable, "SpellId"),
                    ConstructionId = ReadMemberString(usable, "ConstructionId"),
                    Safety = BuildSafety(prefix),
                    SafetyZhHans = BuildSafetyZh(prefix, false),
                    SafetyZhHant = BuildSafetyZh(prefix, true),
                    Source = "Shared.dll",
                    GameMaxStackSize = Math.Max(1, gameMax),
                    EditorMaxStackSize = editorMax,
                    SourceLine = 0,
                    Tier = ReadMemberNullableInt(item, "Tier"),
                    Price = ReadMemberNullableInt(tradable, "Price"),
                    Unique = ReadMemberBool(item, "Unique"),
                    HasEquippable = equipment != null,
                    HasUsable = usable != null,
                    HasTradable = tradable != null,
                    HasRandomlyGenerated = random != null,
                    HasLightSource = light != null,
                    HasCitizenConsumable = citizenConsumable != null,
                    HasFuel = fuel != null
                });
            }
        }
        catch
        {
            result.Clear();
        }
        return result;
    }

    private static void InstallAssemblyResolver(string dir)
    {
        if (_assemblyResolverInstalled) return;
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            var candidate = Path.Combine(dir, name.Name + ".dll");
            return File.Exists(candidate) ? TryLoadAssembly(candidate) : null;
        };
        _assemblyResolverInstalled = true;
    }

    private static Assembly TryLoadAssembly(string path)
    {
        var full = Path.GetFullPath(path);
        var loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => string.Equals(a.Location, full, StringComparison.OrdinalIgnoreCase));
        return loaded ?? AssemblyLoadContext.Default.LoadFromAssemblyPath(full);
    }

    private static object? ReadMemberObject(object? obj, string name)
    {
        if (obj == null) return null;
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var type = obj.GetType();
        try
        {
            var prop = type.GetProperty(name, flags);
            if (prop != null) return prop.GetValue(obj);
            var field = type.GetField(name, flags);
            if (field != null) return field.GetValue(obj);
        }
        catch { }
        return null;
    }

    private static string ReadMemberString(object? obj, string name)
    {
        var value = ReadMemberObject(obj, name);
        return value == null ? string.Empty : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static string ReadMemberTextId(object? obj, string name)
    {
        var value = ReadMemberObject(obj, name);
        if (value == null) return string.Empty;
        if (value is string s) return s;
        foreach (var candidate in new[] { "Id", "Key", "Value", "Text", "Name" })
        {
            var text = ReadMemberString(value, candidate);
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }
        return value.ToString() ?? string.Empty;
    }

    private static int ReadMemberInt(object? obj, string name, int defaultValue)
    {
        var raw = ReadMemberObject(obj, name);
        if (raw == null) return defaultValue;
        try { return Convert.ToInt32(raw, CultureInfo.InvariantCulture); } catch { return defaultValue; }
    }

    private static int? ReadMemberNullableInt(object? obj, string name)
    {
        var raw = ReadMemberObject(obj, name);
        if (raw == null) return null;
        try { return Convert.ToInt32(raw, CultureInfo.InvariantCulture); } catch { return null; }
    }

    private static bool ReadMemberBool(object? obj, string name)
    {
        var raw = ReadMemberObject(obj, name);
        if (raw == null) return false;
        try { return Convert.ToBoolean(raw, CultureInfo.InvariantCulture); } catch { return false; }
    }

    private static bool ShouldForceOne(string prefix, string id)
    {
        return prefix is "weapon" or "armor" or "axe" or "pickaxe" or "trinket" or "back" or "torch";
    }

    private static string BuildSafety(string prefix)
    {
        return prefix is "weapon" or "armor" or "axe" or "pickaxe" or "trinket" or "back" or "torch"
            ? "EquipmentNeedsStats"
            : prefix is "cheat" or "dev" or "tile" or "placeable" or "furniture" or "wardeclaration"
                ? "AdvancedOrRisky"
                : "Safe";
    }

    private static string BuildSafetyZh(string prefix, bool hant)
    {
        var safety = BuildSafety(prefix);
        return safety switch
        {
            "EquipmentNeedsStats" => hant ? "裝備需屬性初始化" : "装备需属性初始化",
            "AdvancedOrRisky" => hant ? "進階/風險" : "高级/风险",
            _ => "安全"
        };
    }

    private static (string en, string zhHans, string zhHant) BuildCategory(string prefix)
    {
        return prefix switch
        {
            "ammo" => ("Ammo", "弹药", "彈藥"),
            "armor" => ("Armor", "防具", "防具"),
            "weapon" => ("Weapon", "武器", "武器"),
            "material" => ("Material", "材料", "材料"),
            "seed" => ("Seed", "种子", "種子"),
            "food" => ("Food", "食物", "食物"),
            "potion" or "consumable" => ("Consumable", "消耗品", "消耗品"),
            "trinket" => ("Trinket", "饰品", "飾品"),
            "axe" => ("Axe", "斧头", "斧頭"),
            "pickaxe" => ("Pickaxe", "镐", "鎬"),
            "furniture" => ("Furniture", "家具", "家具"),
            "placeable" => ("Placeable", "可放置物", "可放置物"),
            _ => (CultureInfo.InvariantCulture.TextInfo.ToTitleCase(prefix.Replace('_', ' ')), prefix, prefix)
        };
    }
}
