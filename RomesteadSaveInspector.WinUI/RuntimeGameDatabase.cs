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
                PatchKnownOfficialItems(ItemRecords);
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
        PatchKnownOfficialItems(ItemRecords);
        Directory.CreateDirectory(DataDirectory);
        File.WriteAllText(ItemDatabasePath, JsonSerializer.Serialize(ItemRecords.OrderBy(i => i.Id, StringComparer.OrdinalIgnoreCase).ToList(), JsonOptions), Encoding.UTF8);
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
                var equipment = ReadMemberObject(item, "Equippable");
                var usable = ReadMemberObject(item, "Usable");
                var tradable = ReadMemberObject(item, "Tradable");
                var random = ReadMemberObject(item, "RandomlyGenerated");
                var light = ReadMemberObject(item, "LightSource");
                var citizenConsumable = ReadMemberObject(item, "CitizenConsumable");
                var fuel = ReadMemberObject(item, "Fuel");
                var editorMax = ShouldForceOne(prefix, id) ? 1 : Math.Max(1, gameMax);
                var equipmentType = ReadMemberString(equipment, "EquipmentType");
                var category = BuildCategory(prefix, equipmentType);

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
                    EquipmentType = equipmentType,
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

    private static void PatchKnownOfficialItems(List<RomesteadItemRecord> items)
    {
        var existing = new HashSet<string>(items.Select(i => i.Id), StringComparer.OrdinalIgnoreCase);
        void Add(RomesteadItemRecord r)
        {
            if (!existing.Contains(r.Id))
            {
                items.Add(r);
                existing.Add(r.Id);
            }
        }

        Add(new RomesteadItemRecord { Id = "trinket:phoenix_feather", Name = "Phoenix Feather", NameEn = "Phoenix Feather", NameZhHans = "凤凰羽毛", NameZhHant = "鳳凰羽毛", Description = "The appearance of the Giant Phoenix marks the end of a Great Year. Its appearance here is nothing but symbolic for the times ahead.", DescriptionEn = "The appearance of the Giant Phoenix marks the end of a Great Year. Its appearance here is nothing but symbolic for the times ahead.", DescriptionZhHans = "巨型凤凰的出现标志着一个大年的终结。它在此现身，只是未来时代的象征。", DescriptionZhHant = "巨型鳳凰的出現標誌著一個大年的終結。它在此現身，只是未來時代的象徵。", Icon = "phoenix_feather", Category = "Trinket", CategoryEn = "Trinket", CategoryZhHans = "饰品", CategoryZhHant = "飾品", Prefix = "trinket", Flags = "", EquipmentType = "Trinket", EquipmentMaterial = "", SpellId = "", ConstructionId = "", Safety = "EquipmentNeedsStats", SafetyZhHans = "装备需属性初始化", SafetyZhHant = "裝備需屬性初始化", Source = "ItemData/ItemIds patch", GameMaxStackSize = 1, EditorMaxStackSize = 1, SourceLine = 6067, Tier = 4, Price = 5000, Unique = false, HasEquippable = true, HasUsable = false, HasTradable = true, HasRandomlyGenerated = false, HasLightSource = false, HasCitizenConsumable = false, HasFuel = false });
        Add(new RomesteadItemRecord { Id = "trinket:pyzifax", Name = "Cape of Pyzifax Banner", NameEn = "Cape of Pyzifax Banner", NameZhHans = "皮兹法克斯战旗披风", NameZhHant = "皮茲法克斯戰旗披風", Description = "Pyzifax the Conqueror rose as a threat before the fall of Rome. Even then, he was a force to be reckoned with.", DescriptionEn = "Pyzifax the Conqueror rose as a threat before the fall of Rome. Even then, he was a force to be reckoned with.", DescriptionZhHans = "征服者皮兹法克斯在罗马陷落之前便已崛起为威胁。即便在那时，他也已经是不容小觑的存在。", DescriptionZhHant = "征服者皮茲法克斯在羅馬陷落之前便已崛起為威脅。即便在那時，他也已經是不容小覷的存在。", Icon = "pyzifax_cape", Category = "Back", CategoryEn = "Back", CategoryZhHans = "背部装备", CategoryZhHant = "背部裝備", Prefix = "trinket", Flags = "", EquipmentType = "Back", EquipmentMaterial = "", SpellId = "", ConstructionId = "", Safety = "EquipmentNeedsStats", SafetyZhHans = "装备需属性初始化", SafetyZhHant = "裝備需屬性初始化", Source = "ItemData/ItemIds patch", GameMaxStackSize = 1, EditorMaxStackSize = 1, SourceLine = 6091, Tier = 4, Price = 600, Unique = false, HasEquippable = true, HasUsable = false, HasTradable = true, HasRandomlyGenerated = false, HasLightSource = false, HasCitizenConsumable = false, HasFuel = false });
        Add(new RomesteadItemRecord { Id = "trinket:barkback", Name = "Bark-back", NameEn = "Bark-back", NameZhHans = "树皮背披", NameZhHant = "樹皮背披", Icon = "bark_back", Category = "Back", CategoryEn = "Back", CategoryZhHans = "背部装备", CategoryZhHant = "背部裝備", Prefix = "trinket", Safety = "EquipmentNeedsStats", SafetyZhHans = "装备需属性初始化", SafetyZhHant = "裝備需屬性初始化", Source = "ItemData/ItemIds patch", GameMaxStackSize = 1, EditorMaxStackSize = 1, SourceLine = 6116, Tier = 2, Price = 250, HasEquippable = true, HasTradable = true, EquipmentType = "Back" });
        Add(new RomesteadItemRecord { Id = "trinket:fallen_Cape", Name = "Cape of the Fallen", NameEn = "Cape of the Fallen", NameZhHans = "亡灵披风", NameZhHant = "亡靈披風", Description = "Fashioned with their remains, the cloak carries the Fallen's natural resistance against unholy magic.", DescriptionEn = "Fashioned with their remains, the cloak carries the Fallen's natural resistance against unholy magic.", DescriptionZhHans = "以亡灵残骸制成的披风，保留着它们对邪秽魔法的天然抗性。", DescriptionZhHant = "以亡靈殘骸製成的披風，保留著它們對邪穢魔法的天然抗性。", Icon = "fallen_cape", Category = "Back", CategoryEn = "Back", CategoryZhHans = "背部装备", CategoryZhHant = "背部裝備", Prefix = "trinket", Safety = "EquipmentNeedsStats", SafetyZhHans = "装备需属性初始化", SafetyZhHant = "裝備需屬性初始化", Source = "ItemData/ItemIds patch", GameMaxStackSize = 1, EditorMaxStackSize = 1, SourceLine = 6156, Tier = 2, Price = 250, HasEquippable = true, HasTradable = true, EquipmentType = "Back" });
        Add(new RomesteadItemRecord { Id = "trinket:bedouin_cloak", Name = "Bedouin Cloak", NameEn = "Bedouin Cloak", NameZhHans = "贝都因斗篷", NameZhHant = "貝都因斗篷", Description = "Bedouins are dwellers of the great deserts, and their cloaks prove to serve them well during the blazing sun.", DescriptionEn = "Bedouins are dwellers of the great deserts, and their cloaks prove to serve them well during the blazing sun.", DescriptionZhHans = "贝都因人居于辽阔沙漠，他们的斗篷足以证明其在烈日下的实用价值。", DescriptionZhHant = "貝都因人居於遼闊沙漠，他們的斗篷足以證明其在烈日下的實用價值。", Icon = "bedouin_cloak", Category = "Back", CategoryEn = "Back", CategoryZhHans = "背部装备", CategoryZhHant = "背部裝備", Prefix = "trinket", Safety = "EquipmentNeedsStats", SafetyZhHans = "装备需属性初始化", SafetyZhHant = "裝備需屬性初始化", Source = "ItemData/ItemIds patch", GameMaxStackSize = 1, EditorMaxStackSize = 1, SourceLine = 6188, Tier = 2, Price = null, HasEquippable = true, HasTradable = false, EquipmentType = "Back" });
        Add(new RomesteadItemRecord { Id = "placeable:beehive", Name = "Beehive", NameEn = "Beehive", NameZhHans = "蜂箱", NameZhHant = "蜂箱", Description = "A structure that produces honey at a steady pace.", DescriptionEn = "A structure that produces honey at a steady pace.", DescriptionZhHans = "一种能够稳定产出蜂蜜的建筑。", DescriptionZhHant = "一種能夠穩定產出蜂蜜的建築。", Icon = "item:beehive", Category = "Placeable", CategoryEn = "Placeable", CategoryZhHans = "可放置物", CategoryZhHant = "可放置物", Prefix = "placeable", SpellId = "spell:place-construction", ConstructionId = "beehive:0", Safety = "AdvancedOrRisky", SafetyZhHans = "高级/风险", SafetyZhHant = "進階/風險", Source = "ItemData/ItemIds patch", GameMaxStackSize = 20, EditorMaxStackSize = 20, SourceLine = 10460, Tier = 1, Price = 250, HasUsable = true, HasTradable = true });
    }

    private static (string en, string zhHans, string zhHant) BuildCategory(string prefix, string? equipmentType = null)
    {
        if (string.Equals(equipmentType, "Back", StringComparison.OrdinalIgnoreCase)) return ("Back", "背部装备", "背部裝備");
        if (string.Equals(equipmentType, "Trinket", StringComparison.OrdinalIgnoreCase)) return ("Trinket", "饰品", "飾品");
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
