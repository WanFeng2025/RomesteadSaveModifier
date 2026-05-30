using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace RomesteadSaveInspector.WinUI;

public sealed class ItemAuraRecord
{
    public string Id { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Format { get; set; } = string.Empty;
    public int Tier { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Effects { get; set; } = string.Empty;

    public string Name(string language)
    {
        var key = Format;
        if (!string.IsNullOrWhiteSpace(key))
        {
            var value = RomesteadSaveInspector.Database.RuntimeGameDatabase.GetText(key, language);
            if (!string.IsNullOrWhiteSpace(value) && !value.Equals(key, StringComparison.OrdinalIgnoreCase))
                return CleanModifierText(value);

            var fallback = FallbackModifierName(key, language);
            if (!string.IsNullOrWhiteSpace(fallback)) return fallback;
        }
        return Humanize(Id);
    }

    private static string CleanModifierText(string value)
    {
        return (value ?? string.Empty)
            .Replace("{0}", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();
    }

    private static string FallbackModifierName(string key, string language)
    {
        var normalized = RomesteadSaveInspector.Database.RomesteadItemRecord.NormalizeLanguage(language);
        var zhHans = normalized == "zh-hans";
        var zhHant = normalized == "zh-hant";
        if (!zhHans && !zhHant) return string.Empty;

        return key switch
        {
            "ItemModifier_Rusted" => zhHant ? "生銹的" : "生锈的",
            "ItemModifier_Blunt" => zhHant ? "鈍" : "钝",
            "ItemModifier_Damaged" => zhHant ? "破損的" : "破损的",
            "ItemModifier_Sharpened" => zhHant ? "鋒利的" : "锋利的",
            "ItemModifier_Swift" => "迅捷的",
            "ItemModifier_Precise" => zhHant ? "精准的" : "精准的",
            "ItemModifier_Reaching" => zhHant ? "遠觸的" : "远触的",
            "ItemModifier_Forceful" => zhHant ? "強力的" : "强力的",
            "ItemModifier_Pristine" => "完美的",
            "ItemModifier_Masterwork" => zhHant ? "大師級" : "大师级",
            "ItemModifier_Legendary" => zhHant ? "傳奇級" : "传奇级",
            "ItemModifier_Rotted" => zhHant ? "腐朽的" : "腐朽的",
            "ItemModifier_Weak" => zhHant ? "脆弱的" : "脆弱的",
            "ItemModifier_Dried" => zhHant ? "乾裂的" : "干裂的",
            "ItemModifier_Bulwark" => zhHant ? "堡壘級" : "堡垒级",
            "ItemModifier_AntiHex" => zhHant ? "抗魔的" : "抗魔的",
            "ItemModifier_Hardened" => zhHant ? "硬化的" : "硬化的",
            "ItemModifier_Angry" => zhHant ? "憤怒的" : "愤怒的",
            "ItemModifier_Violent" => zhHant ? "暴力的" : "暴力的",
            "ItemModifier_Pointy" => zhHant ? "尖銳的" : "尖锐的",
            "ItemModifier_Piercing" => zhHant ? "穿刺的" : "穿刺的",
            "ItemModifier_Blessed" => zhHant ? "賜福的" : "赐福的",
            "ItemModifier_Sacred" => zhHant ? "神聖的" : "神圣的",
            "ItemModifier_Heavy" => zhHant ? "沉重的" : "沉重的",
            "ItemModifier_Enduring" => zhHant ? "耐用的" : "耐用的",
            "ItemModifier_Surefooted" => zhHant ? "扎實的" : "扎实的",
            "ItemModifier_Lively" => zhHant ? "靈動的" : "灵动的",
            "ItemModifier_Energetic" => zhHant ? "奮發的" : "奋发的",
            "ItemModifier_Hasty" => zhHant ? "急速的" : "急速的",
            "ItemModifier_Hearty" => zhHant ? "健康的" : "健康的",
            "ItemModifier_Resilient" => zhHant ? "強固的" : "强固的",
            "ItemModifier_Guarding" => zhHant ? "守護的" : "守护的",
            "ItemModifier_Defending" => zhHant ? "防禦的" : "防御的",
            "ItemModifier_Protecting" => zhHant ? "庇護的" : "庇护的",
            "ItemModifier_Warding" => zhHant ? "護佑的" : "护佑的",
            "ItemModifier_Lucky" => zhHant ? "幸運的" : "幸运的",
            "ItemModifier_Exact" => zhHant ? "精確的" : "精确的",
            "ItemModifier_Assassins" => zhHant ? "刺客的" : "刺客的",
            "ItemModifier_Grasping" => zhHant ? "抓取的" : "抓取的",
            "ItemModifier_Fast" => zhHant ? "快速的" : "快速的",
            "ItemModifier_Quick" => zhHant ? "迅速的" : "迅速的",
            _ => string.Empty
        };
    }

    public string CategoryName(string language)
    {
        var zhHans = RomesteadSaveInspector.Database.RomesteadItemRecord.NormalizeLanguage(language) == "zh-hans";
        var zhHant = RomesteadSaveInspector.Database.RomesteadItemRecord.NormalizeLanguage(language) == "zh-hant";
        return Category.ToLowerInvariant() switch
        {
            "melee" => zhHant ? "近戰" : zhHans ? "近战" : "Melee",
            "ranged" => zhHant ? "遠程" : zhHans ? "远程" : "Ranged",
            "armor" => zhHant ? "護甲" : zhHans ? "护甲" : "Armor",
            "trinket" => zhHant ? "飾品" : zhHans ? "饰品" : "Trinket",
            _ => Category
        };
    }

    private static string Humanize(string id)
    {
        var tail = id.Split(':').LastOrDefault() ?? id;
        return string.Join(' ', tail.Replace('-', ' ').Replace('_', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(s => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s)));
    }
}

public sealed class ItemAuraDatabaseRow
{
    private readonly string _language;
    public ItemAuraDatabaseRow(ItemAuraRecord aura, string language)
    {
        Id = aura.Id;
        Category = aura.CategoryName(language);
        RawCategory = aura.Category;
        Name = ItemAuraCatalog.DisplayLabel(aura, language);
        Tier = aura.Tier.ToString(CultureInfo.InvariantCulture);
        Type = aura.Type;
        Effects = ItemAuraCatalog.LocalizeEffects(aura.Effects, language);
        _language = language;
    }
    public string Id { get; }
    public string Name { get; }
    public string Category { get; }
    public string RawCategory { get; }
    public string Tier { get; }
    public string Type { get; }
    public string Effects { get; }
}

public static class ItemAuraCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private static readonly List<ItemAuraRecord> Embedded = new()
    {
        new ItemAuraRecord { Id = "item_aura:melee:rusted", Category = "melee", Format = "ItemModifier_Rusted", Tier = -1, Type = "auratype:stats_change", Effects = "MeleeDamageModifier: Additive=-0.25; AttackSpeed: Additive=-0.05" },
        new ItemAuraRecord { Id = "item_aura:melee:blunt", Category = "melee", Format = "ItemModifier_Blunt", Tier = -1, Type = "auratype:stats_change", Effects = "MeleeDamageModifier: Additive=-0.15" },
        new ItemAuraRecord { Id = "item_aura:melee:damaged", Category = "melee", Format = "ItemModifier_Damaged", Tier = -1, Type = "auratype:stats_change", Effects = "MeleeDamageModifier: Additive=-0.25" },
        new ItemAuraRecord { Id = "item_aura:melee:sharpened", Category = "melee", Format = "ItemModifier_Sharpened", Tier = 0, Type = "auratype:stats_change", Effects = "MeleeDamageModifier: Additive=0.05; AttackSpeed: Additive=-0.05" },
        new ItemAuraRecord { Id = "item_aura:melee:swift", Category = "melee", Format = "ItemModifier_Swift", Tier = 0, Type = "auratype:stats_change", Effects = "MeleeDamageModifier: Additive=-0.05; AttackSpeed: Additive=0.1" },
        new ItemAuraRecord { Id = "item_aura:melee:precise", Category = "melee", Format = "ItemModifier_Precise", Tier = 0, Type = "auratype:stats_change", Effects = "MeleeDamageModifier: Additive=0.05; AttackSpeed: Additive=-0.05; AttackRangeModifier: Additive=-0.05; CritChance: Additive=0.1" },
        new ItemAuraRecord { Id = "item_aura:melee:reaching", Category = "melee", Format = "ItemModifier_Reaching", Tier = 0, Type = "auratype:stats_change", Effects = "MeleeDamageModifier: Additive=-0.05; AttackRangeModifier: Additive=0.15" },
        new ItemAuraRecord { Id = "item_aura:melee:forceful", Category = "melee", Format = "ItemModifier_Forceful", Tier = 0, Type = "auratype:stats_change", Effects = "MeleeDamageModifier: Additive=0.1; AttackSpeed: Additive=-0.05; AttackRangeModifier: Additive=-0.05; Knockback: Additive=0.1" },
        new ItemAuraRecord { Id = "item_aura:melee:pristine", Category = "melee", Format = "ItemModifier_Pristine", Tier = 0, Type = "auratype:stats_change", Effects = "MeleeDamageModifier: Additive=0.1; AttackSpeed: Additive=0.05; Knockback: Additive=-0.05" },
        new ItemAuraRecord { Id = "item_aura:melee:masterwork", Category = "melee", Format = "ItemModifier_Masterwork", Tier = 0, Type = "auratype:stats_change", Effects = "MeleeDamageModifier: Additive=0.15; CritChance: Additive=0.03" },
        new ItemAuraRecord { Id = "item_aura:melee:legendary", Category = "melee", Format = "ItemModifier_Legendary", Tier = 1, Type = "auratype:stats_change", Effects = "MeleeDamageModifier: Additive=0.15; AttackRangeModifier: Additive=0.1; CritChance: Additive=0.05; Knockback: Additive=0.05" },
        new ItemAuraRecord { Id = "item_aura:ranged:rotted", Category = "ranged", Format = "ItemModifier_Rotted", Tier = -1, Type = "auratype:stats_change", Effects = "RangedDamageModifier: Additive=-0.25; AttackSpeed: Additive=-0.05" },
        new ItemAuraRecord { Id = "item_aura:ranged:weak", Category = "ranged", Format = "ItemModifier_Weak", Tier = 0, Type = "auratype:stats_change", Effects = "RangedDamage: Additive=-0.15" },
        new ItemAuraRecord { Id = "item_aura:ranged:sharpened", Category = "ranged", Format = "ItemModifier_Sharpened", Tier = 0, Type = "auratype:stats_change", Effects = "RangedDamageModifier: Additive=0.1; AttackSpeed: Additive=-0.05" },
        new ItemAuraRecord { Id = "item_aura:ranged:swift", Category = "ranged", Format = "ItemModifier_Swift", Tier = 0, Type = "auratype:stats_change", Effects = "RangedDamageModifier: Additive=-0.05; AttackSpeed: Additive=0.1" },
        new ItemAuraRecord { Id = "item_aura:ranged:forceful", Category = "ranged", Format = "ItemModifier_Forceful", Tier = 0, Type = "auratype:stats_change", Effects = "RangedDamageModifier: Additive=0.05; AttackSpeed: Additive=-0.05; Knockback: Additive=0.1" },
        new ItemAuraRecord { Id = "item_aura:ranged:pristine", Category = "ranged", Format = "ItemModifier_Pristine", Tier = 0, Type = "auratype:stats_change", Effects = "RangedDamageModifier: Additive=0.1; Knockback: Additive=-0.05" },
        new ItemAuraRecord { Id = "item_aura:ranged:masterwork", Category = "ranged", Format = "ItemModifier_Masterwork", Tier = 0, Type = "auratype:stats_change", Effects = "RangedDamageModifier: Additive=0.15; AttackSpeed: Additive=0.05" },
        new ItemAuraRecord { Id = "item_aura:ranged:legendary", Category = "ranged", Format = "ItemModifier_Legendary", Tier = 1, Type = "auratype:stats_change", Effects = "RangedDamageModifier: Additive=0.15; AttackSpeed: Additive=0.1; CritChance: Additive=0.05" },
        new ItemAuraRecord { Id = "item_aura:armor:dried", Category = "armor", Format = "ItemModifier_Dried", Tier = -1, Type = "auratype:stats_change", Effects = "Armor: Additive=-2, AdditiveMultiplier=-0.02" },
        new ItemAuraRecord { Id = "item_aura:armor:rusted", Category = "armor", Format = "ItemModifier_Rusted", Tier = -1, Type = "auratype:stats_change", Effects = "Armor: Additive=-2, AdditiveMultiplier=-0.02" },
        new ItemAuraRecord { Id = "item_aura:armor:damaged", Category = "armor", Format = "ItemModifier_Damaged", Tier = -1, Type = "auratype:stats_change", Effects = "Armor: Additive=-2" },
        new ItemAuraRecord { Id = "item_aura:armor:weak", Category = "armor", Format = "ItemModifier_Weak", Tier = -1, Type = "auratype:stats_change", Effects = "Armor: Additive=-1" },
        new ItemAuraRecord { Id = "item_aura:armor:pristine", Category = "armor", Format = "ItemModifier_Pristine", Tier = 0, Type = "auratype:stats_change", Effects = "MeleeDamageModifier: Additive=0.05; SlashingResistance: Additive=0.005; PiercingResistance: Additive=0.005; BludgeoningResistance: Additive=0.005" },
        new ItemAuraRecord { Id = "item_aura:armor:masterwork", Category = "armor", Format = "ItemModifier_Masterwork", Tier = 0, Type = "auratype:stats_change", Effects = "EnergyRegeneration: Additive=1; SlashingResistance: Additive=0.01; PiercingResistance: Additive=0.01; BludgeoningResistance: Additive=0.01" },
        new ItemAuraRecord { Id = "item_aura:armor:legendary", Category = "armor", Format = "ItemModifier_Legendary", Tier = 1, Type = "auratype:stats_change", Effects = "EnergyRegeneration: Additive=2; SlashingResistance: Additive=0.015; PiercingResistance: Additive=0.015; BludgeoningResistance: Additive=0.015" },
        new ItemAuraRecord { Id = "item_aura:armor:bulwark", Category = "armor", Format = "ItemModifier_Bulwark", Tier = 0, Type = "auratype:stats_change", Effects = "EnergyRegeneration: Additive=-2; KnockbackResistance: Additive=0.1" },
        new ItemAuraRecord { Id = "item_aura:armor:anti-hex", Category = "armor", Format = "ItemModifier_AntiHex", Tier = 0, Type = "auratype:stats_change", Effects = "MagicResistance: Additive=5; EnergyRegeneration: Additive=-2" },
        new ItemAuraRecord { Id = "item_aura:armor:hardened", Category = "armor", Format = "ItemModifier_Hardened", Tier = 0, Type = "auratype:stats_change", Effects = "Armor: Additive=1; EnergyRegeneration: Additive=-1" },
        new ItemAuraRecord { Id = "item_aura:trinket:angry", Category = "trinket", Format = "ItemModifier_Angry", Tier = 0, Type = "auratype:stats_change", Effects = "MeleeDamageModifier: Additive=0.02" },
        new ItemAuraRecord { Id = "item_aura:trinket:violent", Category = "trinket", Format = "ItemModifier_Violent", Tier = 0, Type = "auratype:stats_change", Effects = "MeleeDamageModifier: Additive=0.04" },
        new ItemAuraRecord { Id = "item_aura:trinket:pointy", Category = "trinket", Format = "ItemModifier_Pointy", Tier = 0, Type = "auratype:stats_change", Effects = "RangedDamageModifier: Additive=0.02" },
        new ItemAuraRecord { Id = "item_aura:trinket:piercing", Category = "trinket", Format = "ItemModifier_Piercing", Tier = 0, Type = "auratype:stats_change", Effects = "RangedDamageModifier: Additive=0.04" },
        new ItemAuraRecord { Id = "item_aura:trinket:blessed", Category = "trinket", Format = "ItemModifier_Blessed", Tier = 0, Type = "auratype:stats_change", Effects = "MagicDamageModifier: Additive=0.02" },
        new ItemAuraRecord { Id = "item_aura:trinket:sacred", Category = "trinket", Format = "ItemModifier_Sacred", Tier = 0, Type = "auratype:stats_change", Effects = "MagicDamageModifier: Additive=0.04" },
        new ItemAuraRecord { Id = "item_aura:trinket:heavy", Category = "trinket", Format = "ItemModifier_Heavy", Tier = 0, Type = "auratype:stats_change", Effects = "ThrowingDamageModifier: Additive=0.02" },
        new ItemAuraRecord { Id = "item_aura:trinket:forceful", Category = "trinket", Format = "ItemModifier_Forceful", Tier = 0, Type = "auratype:stats_change", Effects = "ThrowingDamageModifier: Additive=0.04" },
        new ItemAuraRecord { Id = "item_aura:trinket:enduring", Category = "trinket", Format = "ItemModifier_Enduring", Tier = 0, Type = "auratype:stats_change", Effects = "" },
        new ItemAuraRecord { Id = "item_aura:trinket:surefooted", Category = "trinket", Format = "ItemModifier_Surefooted", Tier = 0, Type = "auratype:stats_change", Effects = "" },
        new ItemAuraRecord { Id = "item_aura:trinket:lively", Category = "trinket", Format = "ItemModifier_Lively", Tier = 0, Type = "auratype:stats_change", Effects = "EnergyRegeneration: Multiplier=0.02" },
        new ItemAuraRecord { Id = "item_aura:trinket:energetic", Category = "trinket", Format = "ItemModifier_Energetic", Tier = 0, Type = "auratype:stats_change", Effects = "EnergyRegeneration: Multiplier=0.04; Energy: Additive=5" },
        new ItemAuraRecord { Id = "item_aura:trinket:hasty", Category = "trinket", Format = "ItemModifier_Hasty", Tier = 0, Type = "auratype:stats_change", Effects = "MovementSpeed: Additive=0.01" },
        new ItemAuraRecord { Id = "item_aura:trinket:swift", Category = "trinket", Format = "ItemModifier_Swift", Tier = 0, Type = "auratype:stats_change", Effects = "MovementSpeed: Additive=0.02" },
        new ItemAuraRecord { Id = "item_aura:trinket:hearty", Category = "trinket", Format = "ItemModifier_Hearty", Tier = 0, Type = "auratype:stats_change", Effects = "Health: Additive=3" },
        new ItemAuraRecord { Id = "item_aura:trinket:resilient", Category = "trinket", Format = "ItemModifier_Resilient", Tier = 0, Type = "auratype:stats_change", Effects = "Health: Additive=5" },
        new ItemAuraRecord { Id = "item_aura:trinket:guarding", Category = "trinket", Format = "ItemModifier_Guarding", Tier = 0, Type = "auratype:stats_change", Effects = "Armor: Additive=1" },
        new ItemAuraRecord { Id = "item_aura:trinket:defending", Category = "trinket", Format = "ItemModifier_Defending", Tier = 0, Type = "auratype:stats_change", Effects = "Armor: Additive=2" },
        new ItemAuraRecord { Id = "item_aura:trinket:protecting", Category = "trinket", Format = "ItemModifier_Protecting", Tier = 0, Type = "auratype:stats_change", Effects = "MagicResistance: Additive=1" },
        new ItemAuraRecord { Id = "item_aura:trinket:warding", Category = "trinket", Format = "ItemModifier_Warding", Tier = 0, Type = "auratype:stats_change", Effects = "MagicResistance: Additive=2" },
        new ItemAuraRecord { Id = "item_aura:trinket:lucky", Category = "trinket", Format = "ItemModifier_Lucky", Tier = 0, Type = "auratype:stats_change", Effects = "CritChance: Additive=0.01" },
        new ItemAuraRecord { Id = "item_aura:trinket:precise", Category = "trinket", Format = "ItemModifier_Precise", Tier = 0, Type = "auratype:stats_change", Effects = "CritChance: Additive=0.02" },
        new ItemAuraRecord { Id = "item_aura:trinket:exact", Category = "trinket", Format = "ItemModifier_Exact", Tier = 0, Type = "auratype:stats_change", Effects = "CritDamage: Additive=0.05" },
        new ItemAuraRecord { Id = "item_aura:trinket:assassins", Category = "trinket", Format = "ItemModifier_Assassins", Tier = 0, Type = "auratype:stats_change", Effects = "CritDamage: Additive=0.1" },
        new ItemAuraRecord { Id = "item_aura:trinket:grasping", Category = "trinket", Format = "ItemModifier_Grasping", Tier = 0, Type = "auratype:stats_change", Effects = "AttackRangeModifier: Additive=0.05" },
        new ItemAuraRecord { Id = "item_aura:trinket:reaching", Category = "trinket", Format = "ItemModifier_Reaching", Tier = 0, Type = "auratype:stats_change", Effects = "AttackRangeModifier: Additive=0.1" },
        new ItemAuraRecord { Id = "item_aura:trinket:fast", Category = "trinket", Format = "ItemModifier_Fast", Tier = 0, Type = "auratype:stats_change", Effects = "AttackSpeed: Additive=0.02" },
        new ItemAuraRecord { Id = "item_aura:trinket:quick", Category = "trinket", Format = "ItemModifier_Quick", Tier = 0, Type = "auratype:stats_change", Effects = "AttackSpeed: Additive=0.04" },
    };
    private static List<ItemAuraRecord>? _loaded;

    public static IReadOnlyList<ItemAuraRecord> All => _loaded ??= Load();

    private static List<ItemAuraRecord> Load()
    {
        try
        {
            var path = Path.Combine(AppPaths.DataDir, "item_auras.json");
            if (File.Exists(path))
            {
                var list = JsonSerializer.Deserialize<List<ItemAuraRecord>>(File.ReadAllText(path, Encoding.UTF8), JsonOptions);
                if (list != null && list.Count > 0)
                    return list.Where(a => !string.IsNullOrWhiteSpace(a.Id)).OrderBy(a => a.Id, StringComparer.OrdinalIgnoreCase).ToList();
            }
        }
        catch { }
        return Embedded.OrderBy(a => a.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static ItemAuraRecord? Find(string? id)
    {
        var normalized = NormalizeId(id);
        return All.FirstOrDefault(a => a.Id.Equals(normalized, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsChineseLanguage(string? language)
    {
        var normalized = RomesteadSaveInspector.Database.RomesteadItemRecord.NormalizeLanguage(language);
        return normalized is "zh-hans" or "zh-hant";
    }

    public static string DisplayLabel(ItemAuraRecord aura, string? language)
    {
        var name = aura.Name(language ?? string.Empty);
        return IsChineseLanguage(language) ? $"{aura.Id} - {name}" : $"{aura.Id} - {name}";
    }

    public static IEnumerable<ItemAuraRecord> Search(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Enumerable.Empty<ItemAuraRecord>();
        var q = text.Trim();
        return All.Where(a => a.Id.Contains(q, StringComparison.OrdinalIgnoreCase) || a.Category.Contains(q, StringComparison.OrdinalIgnoreCase) || a.Format.Contains(q, StringComparison.OrdinalIgnoreCase) || a.Effects.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    public static IEnumerable<ItemAuraRecord> GetCompatibleForItem(string? itemId)
    {
        var category = GetItemAuraCategory(itemId);
        if (string.IsNullOrWhiteSpace(category)) return All;
        return All.Where(a => a.Category.Equals(category, StringComparison.OrdinalIgnoreCase));
    }

    public static string GetItemAuraCategory(string? itemId)
    {
        var id = (itemId ?? string.Empty).Trim().ToLowerInvariant();
        if (id.StartsWith("weapon:") || id.StartsWith("axe:") || id.StartsWith("pickaxe:"))
        {
            if (id.Contains(":bow") || id.Contains(":crossbow")) return "ranged";
            return "melee";
        }
        if (id.StartsWith("armor:") || id.StartsWith("back:") || id.StartsWith("torch:")) return "armor";
        if (id.StartsWith("trinket:")) return "trinket";
        return string.Empty;
    }

    public static bool IsAuraEditableItem(string? itemId)
    {
        return !string.IsNullOrWhiteSpace(GetItemAuraCategory(itemId));
    }

    public static string NormalizeId(string? raw)
    {
        var value = (raw ?? string.Empty).Trim();
        var dash = value.IndexOf('—');
        if (dash > 0) value = value[..dash].Trim();
        var spacedDash = value.IndexOf(" - ", StringComparison.Ordinal);
        if (spacedDash > 0) value = value[..spacedDash].Trim();
        return value.ToLowerInvariant();
    }

    public static IEnumerable<string> SplitIds(string? text)
    {
        return (text ?? string.Empty).Replace('\r', ';').Replace('\n', ';').Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(NormalizeId).Where(v => !string.IsNullOrWhiteSpace(v));
    }

    public static string NormalizeList(string? text, bool keepUnknown)
    {
        var ids = new List<string>();
        foreach (var token in SplitIds(text))
        {
            if (!keepUnknown && Find(token) == null) continue;
            if (!ids.Contains(token, StringComparer.OrdinalIgnoreCase)) ids.Add(token);
        }
        return string.Join(';', ids);
    }

    public static string GetActiveToken(string? text)
    {
        var s = text ?? string.Empty;
        var semi = s.LastIndexOf(';');
        return semi >= 0 ? s[(semi + 1)..].Trim() : s.Trim();
    }

    public static string ReplaceActiveToken(string? existing, string id)
    {
        var text = existing ?? string.Empty;
        var semi = text.LastIndexOf(';');
        var prefix = semi >= 0 ? text[..(semi + 1)] : string.Empty;
        var normalized = NormalizeId(id);
        return NormalizeList(prefix + normalized, keepUnknown: true);
    }

    public static string LocalizeEffects(string? effects, string language)
    {
        var s = effects ?? string.Empty;
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        var zhHans = RomesteadSaveInspector.Database.RomesteadItemRecord.NormalizeLanguage(language) == "zh-hans";
        var zhHant = RomesteadSaveInspector.Database.RomesteadItemRecord.NormalizeLanguage(language) == "zh-hant";
        if (!zhHans && !zhHant) return s;
        var map = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)
        {
            ["MeleeDamageModifier"] = zhHant ? "近戰傷害" : "近战伤害",
            ["RangedDamageModifier"] = zhHant ? "遠程傷害" : "远程伤害",
            ["MagicDamageModifier"] = zhHant ? "魔法傷害" : "魔法伤害",
            ["Armor"] = zhHant ? "護甲" : "护甲",
            ["Health"] = zhHant ? "生命值" : "生命值",
            ["AttackSpeed"] = zhHant ? "攻擊速度" : "攻击速度",
            ["AttackRangeModifier"] = zhHant ? "攻擊範圍" : "攻击范围",
            ["CritChance"] = zhHant ? "暴擊率" : "暴击率",
            ["MovementSpeed"] = zhHant ? "移動速度" : "移动速度",
            ["Energy"] = zhHant ? "精力" : "精力",
            ["KnockbackModifier"] = zhHant ? "擊退" : "击退",
            ["Knockback"] = zhHant ? "擊退" : "击退",
            ["BlockStrength"] = zhHant ? "格擋強度" : "格挡强度",
            ["Additive"] = zhHant ? "加成" : "加成",
            ["Multiplier"] = zhHant ? "倍率" : "倍率",
            ["BaseMultiplier"] = zhHant ? "基礎倍率" : "基础倍率",
            ["AdditiveMultiplier"] = zhHant ? "附加倍率" : "附加倍率",
            ["BonusMultiplier"] = zhHant ? "獎勵倍率" : "奖励倍率"
        };
        foreach (var (k,v) in map) s = RegexReplaceWord(s, k, v);
        return s;
    }

    private static string RegexReplaceWord(string input, string word, string replacement)
    {
        return System.Text.RegularExpressions.Regex.Replace(input, $"(?<![A-Za-z0-9_]){System.Text.RegularExpressions.Regex.Escape(word)}(?![A-Za-z0-9_])", replacement);
    }
}
