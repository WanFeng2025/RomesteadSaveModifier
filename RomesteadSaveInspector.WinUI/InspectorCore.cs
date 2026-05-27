using System.Collections;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;

namespace RomesteadSaveInspector.WinUI;

internal static class InspectorCore
{
    private const string GameStateFileName = "game_state";

    private static readonly Dictionary<string, Assembly> LoadedAssemblies = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> DllBySimpleName = new(StringComparer.OrdinalIgnoreCase);

    public static int Run(Options options)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }
        try
        {
            ArgumentNullException.ThrowIfNull(options);
            if (options.ShowHelp)
            {
                PrintUsage();
                return 0;
            }

            var appRoot = FindAppRoot();
            var libDir = ResolveDirectory(options.LibDir, appRoot, "lib");
            var inputTarget = ResolveInputTarget(options.InputPath, appRoot);
            var outputDir = ResolveDirectory(options.OutputDir, appRoot, "output", create: true);

            AppLogger.Info("Romestead Save Inspector v2 - read only");
            AppLogger.Info($"App root : {appRoot}");
            AppLogger.Info($"Lib dir  : {libDir}");
            AppLogger.Info($"Input    : {inputTarget}");
            AppLogger.Info($"Output   : {outputDir}");
            AppLogger.Info("");

            LoadGameAssemblies(libDir);

            var scanFiles = GetInputFiles(inputTarget).ToList();
            if (scanFiles.Count == 0) throw new FileNotFoundException($"No files found in input target: {inputTarget}");
            AppLogger.Info($"Scanning files: {scanFiles.Count}");

            var result = new FullInspectionResult();
            foreach (var file in scanFiles)
            {
                InspectFile(file, options, result);
            }

            result.TotalFilesScanned = scanFiles.Count;
            result.TotalGameStates = result.GameStates.Count;
            result.TotalPlayerSaves = result.PlayerSaves.Count;

            PrintSummary(result, options);

            if (!options.NoFiles)
            {
                WriteOutputs(result, outputDir, options);
                AppLogger.Info("");
                AppLogger.Info("Wrote output files:");
                AppLogger.Info("- summary.json");
                AppLogger.Info("- files_scanned.csv");
                AppLogger.Info("- state_entries.csv");
                AppLogger.Info("- world_item_instances.csv");
                AppLogger.Info("- world_item_totals.csv");
                AppLogger.Info("- world_inventories.csv");
                AppLogger.Info("- world_inventory_slots.csv");
                AppLogger.Info("- player_saves.csv");
                AppLogger.Info("- player_items.csv");
                AppLogger.Info("- player_item_totals.csv");
            }

            AppLogger.Info("");
            AppLogger.Info("Done. No input file was modified.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR:");
            Console.Error.WriteLine(ex.ToString());
            AppLogger.Error("Inspector core failed.", ex);
            return 1;
        }
    }



    public static SaveResult SavePlayerItemStacks(string libDir, string inputDir, string backupDir, string outputDir, IReadOnlyList<PlayerItemStackUpdate> updates)
    {
        ArgumentNullException.ThrowIfNull(updates);
        if (updates.Count == 0) return new SaveResult(0, 0, "", "", 0, "没有可保存的物品行。");

        LoadGameAssemblies(libDir);
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(backupDir);
        Directory.CreateDirectory(outputDir);

        var grouped = updates
            .Where(u => !string.IsNullOrWhiteSpace(u.SourceFile) && !string.IsNullOrWhiteSpace(u.InstanceId))
            .GroupBy(u => u.SourceFile, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (grouped.Count == 0) return new SaveResult(0, 0, "", "", 0, "没有找到可根据 InstanceId 保存的玩家物品行。");

        var backupInfo = BackupPlayerDataFiles(inputDir, backupDir);
        var savedFiles = 0;
        var changedItems = 0;

        foreach (var group in grouped)
        {
            var playerFile = ResolveInputFile(inputDir, group.Key);
            if (playerFile == null)
            {
                AppLogger.Error($"保存跳过：找不到玩家文件 {group.Key}");
                continue;
            }

            var bytes = File.ReadAllBytes(playerFile);
            if (!TryDeserializePlayerSaveWithFormat(bytes, out var playerSave, out var wasGzip, out var note) || playerSave == null)
            {
                AppLogger.Error($"保存跳过：无法反序列化玩家文件 {playerFile}. {note}");
                continue;
            }

            var changedThisFile = 0;
            foreach (var update in group)
            {
                var item = FindPlayerItem(playerSave, update.Section, update.SlotIndex, update.InstanceId);
                if (item == null)
                {
                    AppLogger.Error($"保存跳过物品：{Path.GetFileName(playerFile)} 找不到 InstanceId={update.InstanceId} Section={update.Section} Slot={update.SlotIndex}");
                    continue;
                }

                var currentBaseId = ReadMember(item, "BaseDataId")?.ToString() ?? "";
                if (!string.IsNullOrWhiteSpace(update.BaseDataId) && !currentBaseId.Equals(update.BaseDataId, StringComparison.OrdinalIgnoreCase))
                {
                    AppLogger.Error($"保存跳过物品：InstanceId={update.InstanceId} BaseDataId 不匹配，当前={currentBaseId}，更新={update.BaseDataId}");
                    continue;
                }

                var current = ToInt(ReadMember(item, "StackCount"));
                if (current == update.StackCount) continue;
                SetMember(item, "StackCount", update.StackCount);
                changedThisFile++;
                changedItems++;
                AppLogger.Info($"修改物品：{Path.GetFileName(playerFile)} [{update.Section}:{update.SlotIndex}] {currentBaseId} {current} -> {update.StackCount}");
            }

            if (changedThisFile == 0)
            {
                AppLogger.Info($"{Path.GetFileName(playerFile)} 没有实际变化，未写回。");
                continue;
            }

            var playerType = FindType("Shared.Models.Player.PlayerCharacterSaveModel")
                ?? throw new InvalidOperationException("Could not find Shared.Models.Player.PlayerCharacterSaveModel.");
            using var serialized = new MemoryStream();
            if (wasGzip)
            {
                using (var gzip = new GZipStream(serialized, CompressionLevel.Optimal, leaveOpen: true))
                {
                    SerializeWithType(gzip, playerType, playerSave);
                }
            }
            else
            {
                SerializeWithType(serialized, playerType, playerSave);
            }

            var temp = playerFile + ".tmp";
            var modifiedBytes = serialized.ToArray();
            File.WriteAllBytes(temp, modifiedBytes);
            File.Move(temp, playerFile, overwrite: true);

            var outputCopy = Path.Combine(outputDir, Path.GetFileName(playerFile));
            File.WriteAllBytes(outputCopy, modifiedBytes);

            savedFiles++;
            AppLogger.Info($"已保存玩家文件：{playerFile} ({changedThisFile} 项变化，格式={note})");
            AppLogger.Info($"已复制修改后玩家文件到 output：{outputCopy}");
        }

        return new SaveResult(savedFiles, changedItems, backupInfo.BackupDirectory, outputDir, backupInfo.BackupIndex, $"保存完成：{savedFiles} 个文件，{changedItems} 个物品变化。备份批次 n={backupInfo.BackupIndex}。修改后文件已复制到 output。");
    }

    private static void InspectFile(string file, Options options, FullInspectionResult result)
    {
        var fileName = Path.GetFileName(file);
        var scanInfo = new FileScanInfo(file, fileName, new FileInfo(file).Length, false, false, "");
        AppLogger.Info($"- {fileName}");

        var bytes = File.ReadAllBytes(file);

        // game_state is large and gzip-compressed. Other files can still be tried safely.
        if (fileName.Equals(GameStateFileName, StringComparison.OrdinalIgnoreCase) || fileName.StartsWith("game_state", StringComparison.OrdinalIgnoreCase) || fileName.Equals("world_state", StringComparison.OrdinalIgnoreCase) || fileName.StartsWith("world_state", StringComparison.OrdinalIgnoreCase))
        {
            if (TryDeserializeGameState(bytes, out var state, out var gameStateNote) && state != null)
            {
                var gameState = InspectGameState(file, state, options);
                result.GameStates.Add(gameState);
                scanInfo = scanInfo with { DetectedGameState = true, Note = AppendNote(scanInfo.Note, gameStateNote) };
            }
            else
            {
                scanInfo = scanInfo with { Note = AppendNote(scanInfo.Note, gameStateNote) };
            }
        }

        // Player character saves are separate from game_state. Try every input file so users can drop a whole save folder in input/.
        if (TryDeserializePlayerSave(bytes, out var playerSave, out var playerNote) && playerSave != null)
        {
            var info = InspectPlayerSave(file, playerSave);
            if (options.PlayerFilter == null || PlayerMatches(info, options.PlayerFilter))
                result.PlayerSaves.Add(info);
            scanInfo = scanInfo with { DetectedPlayerSave = true, Note = AppendNote(scanInfo.Note, playerNote) };
        }
        else if (!string.IsNullOrWhiteSpace(playerNote))
        {
            scanInfo = scanInfo with { Note = AppendNote(scanInfo.Note, playerNote) };
        }

        result.FilesScanned.Add(scanInfo);
    }

    private static bool PlayerMatches(PlayerSaveInfo info, string text)
    {
        return info.PlayerName.Contains(text, StringComparison.OrdinalIgnoreCase)
               || info.SaveFileName.Contains(text, StringComparison.OrdinalIgnoreCase)
               || info.SourceFileName.Contains(text, StringComparison.OrdinalIgnoreCase)
               || info.PlayerId.ToString(CultureInfo.InvariantCulture).Contains(text, StringComparison.OrdinalIgnoreCase)
               || info.EntityId.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    private static string AppendNote(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a)) return b ?? "";
        if (string.IsNullOrWhiteSpace(b)) return a;
        return a + " | " + b;
    }

    private static GameStateInspectionResult InspectGameState(string sourceFile, object state, Options options)
    {
        var result = new GameStateInspectionResult { SourceFile = sourceFile, SourceFileName = Path.GetFileName(sourceFile) };
        var stateEntries = EnumerateStateEntries(state).ToList();

        foreach (var entry in stateEntries)
        {
            result.StateEntries.Add(new StateEntryInfo(
                result.SourceFileName,
                entry.Name,
                GetFriendlyTypeName(entry.Value),
                TryGetCount(entry.Value),
                DescribeValue(entry.Value)));
        }

        var itemInstancesValue = stateEntries.FirstOrDefault(e => e.Name == "ItemInstances").Value;
        var inventoriesValue = stateEntries.FirstOrDefault(e => e.Name == "Inventories").Value;
        var worldItemsValue = stateEntries.FirstOrDefault(e => e.Name == "WorldItems").Value;

        var itemMap = new Dictionary<string, ItemInstanceInfo>(StringComparer.OrdinalIgnoreCase);
        if (itemInstancesValue != null)
        {
            foreach (var (key, value) in EnumerateDictionary(itemInstancesValue))
            {
                if (value == null) continue;
                var info = ReadItemInstance(result.SourceFileName, key?.ToString() ?? ReadMember(value, "Id")?.ToString() ?? "", value, "WorldGameState", "");
                if (options.Filter == null || info.BaseDataId.Contains(options.Filter, StringComparison.OrdinalIgnoreCase))
                    result.ItemInstances.Add(info);

                var mapKey = info.InstanceId;
                if (!string.IsNullOrWhiteSpace(mapKey)) itemMap[mapKey] = info;
            }
        }

        result.ItemTotals = result.ItemInstances
            .GroupBy(i => i.BaseDataId, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ItemTotalInfo(result.SourceFileName, g.Key, g.Sum(i => i.StackCount), g.Count()))
            .OrderByDescending(x => x.TotalStackCount)
            .ThenBy(x => x.BaseDataId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (inventoriesValue != null)
        {
            foreach (var invObj in EnumerateSequence(inventoriesValue))
            {
                if (invObj == null) continue;
                var inv = ReadInventory(result.SourceFileName, invObj, itemMap, options.Filter);
                result.Inventories.Add(inv.Inventory);
                result.InventorySlots.AddRange(inv.Slots);
            }
        }

        result.WorldItemCount = TryGetCount(worldItemsValue);
        result.TotalStateEntries = result.StateEntries.Count;
        result.TotalItemInstances = TryGetCount(itemInstancesValue) ?? result.ItemInstances.Count;
        result.TotalInventories = TryGetCount(inventoriesValue) ?? result.Inventories.Count;
        return result;
    }

    private static PlayerSaveInfo InspectPlayerSave(string sourceFile, object save)
    {
        var character = ReadMember(save, "CharacterModel");
        var sourceName = Path.GetFileName(sourceFile);
        var saveFileName = ReadMember(save, "SaveFileName")?.ToString() ?? "";
        var playerName = ReadMember(character, "Name")?.ToString() ?? "";
        var playerId = ToInt(ReadMember(character, "PlayerId"));
        var entityId = ReadMember(character, "EntityId")?.ToString() ?? "";
        var worldId = ReadMember(character, "WorldId")?.ToString() ?? "";
        var money = ToUlong(ReadMember(save, "Money"));
        var timePlayed = ToDouble(ReadMember(character, "TimePlayed"));

        var info = new PlayerSaveInfo
        {
            SourceFile = sourceFile,
            SourceFileName = sourceName,
            SaveFileName = saveFileName,
            PlayerName = playerName,
            PlayerId = playerId,
            EntityId = entityId,
            WorldId = worldId,
            Money = money,
            TimePlayed = timePlayed
        };

        AddPlayerItems(info, "Inventory", ReadMember(save, "Inventory"));
        AddPlayerItems(info, "Equipment", ReadMember(save, "Equipment"));
        AddPlayerItems(info, "SecondaryEquipment", ReadMember(save, "SecondaryEquipment"));
        info.InventorySlots = info.Items.Count(i => i.Section == "Inventory");
        info.EquipmentSlots = info.Items.Count(i => i.Section == "Equipment");
        info.SecondaryEquipmentSlots = info.Items.Count(i => i.Section == "SecondaryEquipment");
        return info;
    }

    private static void AddPlayerItems(PlayerSaveInfo info, string section, object? sequence)
    {
        var arr = EnumerateSequence(sequence).ToList();
        for (var i = 0; i < arr.Count; i++)
        {
            var item = arr[i];
            if (item == null) continue;
            var key = ReadMember(item, "Id")?.ToString() ?? "";
            var baseInfo = ReadItemInstance(info.SourceFileName, key, item, section, info.PlayerName);
            info.Items.Add(new PlayerItemInfo(
                info.SourceFileName,
                info.SaveFileName,
                info.PlayerName,
                info.PlayerId,
                section,
                i,
                baseInfo.InstanceId,
                baseInfo.BaseDataId,
                baseInfo.StackCount,
                baseInfo.AuraCount,
                baseInfo.HasUsableInfo,
                baseInfo.TypeName));
        }
    }

    private static ItemInstanceInfo ReadItemInstance(string sourceFileName, string key, object item, string sourceKind, string ownerName)
    {
        var instanceId = ReadMember(item, "Id")?.ToString() ?? key;
        var baseDataId = ReadMember(item, "BaseDataId")?.ToString() ?? "";
        var stackCount = ToInt(ReadMember(item, "StackCount"));
        var inventoryId = ReadMember(item, "InventoryId")?.ToString();
        var auraCount = TryGetCount(ReadMember(item, "Auras")) ?? 0;
        var usable = ReadMember(item, "UsableInfo") != null;
        return new ItemInstanceInfo(sourceFileName, sourceKind, ownerName, instanceId, baseDataId, stackCount, inventoryId ?? "", auraCount, usable, GetFriendlyTypeName(item));
    }

    private static (InventoryInfo Inventory, List<InventorySlotInfo> Slots) ReadInventory(
        string sourceFileName,
        object invObj,
        Dictionary<string, ItemInstanceInfo> itemMap,
        string? filter)
    {
        var id = ReadMember(invObj, "Id")?.ToString() ?? "";
        var name = ReadMember(invObj, "Name")?.ToString() ?? "";
        var type = ReadMember(invObj, "InventoryType")?.ToString() ?? "";
        var owner = ReadMember(invObj, "OwnerEntityId")?.ToString() ?? "";
        var filterFlags = ReadMember(invObj, "FilterItemFlags")?.ToString() ?? "";
        var slotsObj = ReadMember(invObj, "InventorySlots");
        var slots = EnumerateSequence(slotsObj).ToList();
        var filled = 0;
        var slotInfos = new List<InventorySlotInfo>();

        for (var i = 0; i < slots.Count; i++)
        {
            var slotGuid = slots[i]?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(slotGuid)) continue;
            filled++;

            itemMap.TryGetValue(slotGuid, out var item);
            if (filter != null && item != null && !item.BaseDataId.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;

            slotInfos.Add(new InventorySlotInfo(
                sourceFileName,
                id,
                name,
                i,
                slotGuid,
                item?.BaseDataId ?? "<unresolved>",
                item?.StackCount ?? 0));
        }

        return (new InventoryInfo(sourceFileName, id, name, type, owner, filterFlags, slots.Count, filled), slotInfos);
    }



    private static bool TryDeserializePlayerSaveWithFormat(byte[] bytes, out object? playerSave, out bool wasGzip, out string note)
    {
        playerSave = null;
        wasGzip = false;
        note = "";
        var playerType = FindType("Shared.Models.Player.PlayerCharacterSaveModel");
        if (playerType == null)
        {
            note = "player no: PlayerCharacterSaveModel type not found";
            return false;
        }

        foreach (var mode in new[] { "raw", "gzip" })
        {
            try
            {
                using var stream = mode == "gzip" ? OpenBytesAsGzip(bytes) : new MemoryStream(bytes, writable: false);
                playerSave = DeserializeWithType(stream, playerType);
                if (playerSave != null && ReadMember(playerSave, "CharacterModel") != null)
                {
                    wasGzip = mode == "gzip";
                    note = "player " + mode;
                    return true;
                }
            }
            catch
            {
                // Try the next mode.
            }
        }

        note = "player no";
        return false;
    }

    private static object? FindPlayerItem(object playerSave, string section, int slotIndex, string instanceId)
    {
        var sectionObject = ReadMember(playerSave, section);
        var arr = EnumerateSequence(sectionObject).ToList();
        if (slotIndex >= 0 && slotIndex < arr.Count)
        {
            var item = arr[slotIndex];
            if (item != null)
            {
                var id = ReadMember(item, "Id")?.ToString() ?? "";
                if (id.Equals(instanceId, StringComparison.OrdinalIgnoreCase)) return item;
            }
        }

        foreach (var candidateSection in new[] { "Inventory", "Equipment", "SecondaryEquipment" })
        {
            foreach (var item in EnumerateSequence(ReadMember(playerSave, candidateSection)))
            {
                if (item == null) continue;
                var id = ReadMember(item, "Id")?.ToString() ?? "";
                if (id.Equals(instanceId, StringComparison.OrdinalIgnoreCase)) return item;
            }
        }

        return null;
    }

    private static (string BackupDirectory, int BackupIndex) BackupPlayerDataFiles(string inputDir, string backupDir)
    {
        Directory.CreateDirectory(backupDir);
        var today = DateTime.Now.ToString("yyMMdd", CultureInfo.InvariantCulture);
        var prefix = "backup" + today + "-";
        var maxIndex = 0;
        foreach (var file in Directory.EnumerateFiles(backupDir, prefix + "*-*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(file);
            var rest = name[prefix.Length..];
            var dash = rest.IndexOf('-');
            if (dash > 0 && int.TryParse(rest[..dash], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                maxIndex = Math.Max(maxIndex, n);
        }

        var index = maxIndex + 1;
        var playerFiles = Directory.EnumerateFiles(inputDir, "*.char", SearchOption.AllDirectories)
            .Where(f => !IsInsideDirectory(f, backupDir))
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var source in playerFiles)
        {
            var originalName = Path.GetFileNameWithoutExtension(source);
            var ext = Path.GetExtension(source);
            var backupName = $"backup{today}-{index}-{originalName}{ext}";
            var dest = Path.Combine(backupDir, backupName);
            File.Copy(source, dest, overwrite: false);
            AppLogger.Info($"已备份玩家文件：{source} -> {dest}");
        }

        if (playerFiles.Count == 0)
            AppLogger.Info("未在 input 中找到 .char 玩家文件；没有创建玩家文件备份。 ");

        return (backupDir, index);
    }

    private static bool IsInsideDirectory(string file, string dir)
    {
        var fullFile = Path.GetFullPath(file).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullDir = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullFile.StartsWith(fullDir, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveInputFile(string inputDir, string sourceFile)
    {
        if (Path.IsPathRooted(sourceFile) && File.Exists(sourceFile)) return sourceFile;
        var direct = Path.Combine(inputDir, sourceFile);
        if (File.Exists(direct)) return direct;
        return Directory.EnumerateFiles(inputDir, "*", SearchOption.AllDirectories)
            .FirstOrDefault(f => Path.GetFileName(f).Equals(sourceFile, StringComparison.OrdinalIgnoreCase));
    }

    private static void SerializeWithType(Stream stream, Type type, object? source)
    {
        var gameSaveManagerType = FindType("CandideServer.Saving.GameSaveManager")
            ?? throw new InvalidOperationException("Could not find CandideServer.Saving.GameSaveManager. Is CandideServer.dll in lib/?");

        var reflection = GetStaticField(gameSaveManagerType, "GameStateSaveReflection")
            ?? throw new InvalidOperationException("GameSaveManager.GameStateSaveReflection is null.");
        var serializer = GetStaticField(gameSaveManagerType, "GameStateSaveSerializer")
            ?? throw new InvalidOperationException("GameSaveManager.GameStateSaveSerializer is null.");

        var serializeMethod = serializer.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(m =>
                m.Name == "Serialize" &&
                m.IsGenericMethodDefinition &&
                m.GetParameters().Length == 4 &&
                m.GetParameters()[0].ParameterType.Name == "CandideReflection" &&
                m.GetParameters()[1].ParameterType == typeof(Stream) &&
                m.GetParameters()[3].ParameterType == typeof(bool));

        if (serializeMethod == null)
            throw new MissingMethodException(serializer.GetType().FullName, "Serialize<T>(CandideReflection, Stream, T, bool)");

        var generic = serializeMethod.MakeGenericMethod(type);
        var disposable = generic.Invoke(serializer, new object?[] { reflection, stream, source, true }) as IDisposable;
        disposable?.Dispose();
    }

    private static bool TryDeserializeGameState(byte[] bytes, out object? state, out string note)
    {
        state = null;
        note = "";
        try
        {
            using var stream = OpenBytesPossiblyGzipped(bytes, out var wasGzip);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            memory.Position = 0;
            state = DeserializeWithType(memory, GetGameStateListType());
            note = wasGzip ? $"game_state gzip bytes={memory.Length}" : $"game_state raw bytes={memory.Length}";
            return state != null;
        }
        catch (Exception ex)
        {
            note = "game_state no: " + ex.GetType().Name;
            return false;
        }
    }

    private static bool TryDeserializePlayerSave(byte[] bytes, out object? playerSave, out string note)
    {
        var ok = TryDeserializePlayerSaveWithFormat(bytes, out playerSave, out _, out note);
        return ok;
    }

    private static Type GetGameStateListType()
    {
        var tupleType = typeof(ValueTuple<,>).MakeGenericType(typeof(string), typeof(object));
        return typeof(List<>).MakeGenericType(tupleType);
    }

    private static object? DeserializeWithType(Stream stream, Type type)
    {
        var gameSaveManagerType = FindType("CandideServer.Saving.GameSaveManager")
            ?? throw new InvalidOperationException("Could not find CandideServer.Saving.GameSaveManager. Is CandideServer.dll in lib/?");

        var reflection = GetStaticField(gameSaveManagerType, "GameStateSaveReflection")
            ?? throw new InvalidOperationException("GameSaveManager.GameStateSaveReflection is null.");
        var deserializer = GetStaticField(gameSaveManagerType, "Deserializer")
            ?? throw new InvalidOperationException("GameSaveManager.Deserializer is null.");

        InvokeIfExists(deserializer, "Clear");

        var deserializeMethod = deserializer.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(m =>
                m.Name == "Deserialize" &&
                !m.IsGenericMethodDefinition &&
                m.GetParameters().Length >= 3 &&
                m.GetParameters()[0].ParameterType.Name == "CandideReflection" &&
                m.GetParameters()[1].ParameterType == typeof(Stream) &&
                m.GetParameters()[2].ParameterType == typeof(Type));

        if (deserializeMethod == null)
            throw new MissingMethodException(deserializer.GetType().FullName, "Deserialize(CandideReflection, Stream, Type, bool)");

        var parameters = deserializeMethod.GetParameters();
        if (parameters.Length == 3)
            return deserializeMethod.Invoke(deserializer, new object[] { reflection, stream, type });
        return deserializeMethod.Invoke(deserializer, new object[] { reflection, stream, type, false });
    }

    private static Stream OpenBytesPossiblyGzipped(byte[] bytes, out bool wasGzip)
    {
        wasGzip = bytes.Length >= 2 && bytes[0] == 0x1F && bytes[1] == 0x8B;
        var ms = new MemoryStream(bytes, writable: false);
        return wasGzip ? new GZipStream(ms, CompressionMode.Decompress) : ms;
    }

    private static Stream OpenBytesAsGzip(byte[] bytes)
    {
        var ms = new MemoryStream(bytes, writable: false);
        return new GZipStream(ms, CompressionMode.Decompress);
    }

    private static IEnumerable<string> GetInputFiles(string inputTarget)
    {
        if (File.Exists(inputTarget))
        {
            yield return Path.GetFullPath(inputTarget);
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(inputTarget, "*", SearchOption.AllDirectories)
                     .Where(f => !Path.GetFileName(f).StartsWith("README", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
        {
            yield return Path.GetFullPath(file);
        }
    }

    private static string FindAppRoot()
    {
        var candidates = new List<string> { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && dir.Parent != null; i++)
        {
            dir = dir.Parent;
            candidates.Add(dir.FullName);
        }
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Directory.Exists(Path.Combine(candidate, "lib")) || Directory.Exists(Path.Combine(candidate, "input")))
                return candidate;
        }
        return Directory.GetCurrentDirectory();
    }

    private static string ResolveDirectory(string? specified, string appRoot, string defaultName, bool create = false)
    {
        var path = string.IsNullOrWhiteSpace(specified) ? Path.Combine(appRoot, defaultName) : Path.GetFullPath(specified);
        if (create && !Directory.Exists(path)) Directory.CreateDirectory(path);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException($"Directory not found: {path}");
        return path;
    }

    private static string ResolveInputTarget(string? specified, string appRoot)
    {
        if (!string.IsNullOrWhiteSpace(specified))
        {
            var p = Path.GetFullPath(specified);
            if (!File.Exists(p) && !Directory.Exists(p)) throw new FileNotFoundException("Input path not found", p);
            return p;
        }
        var inputDir = Path.Combine(appRoot, "input");
        if (Directory.Exists(inputDir)) return inputDir;
        throw new DirectoryNotFoundException($"Input folder not found: {inputDir}");
    }

    private static void LoadGameAssemblies(string libDir)
    {
        var dlls = Directory.GetFiles(libDir, "*.dll", SearchOption.TopDirectoryOnly);
        if (dlls.Length == 0)
        {
            throw new InvalidOperationException("No DLL files found in lib/. Copy the game's DLL files into lib/, preferably every *.dll from the Romestead game directory.");
        }

        foreach (var dll in dlls)
            DllBySimpleName[Path.GetFileNameWithoutExtension(dll)] = dll;

        AssemblyLoadContext.Default.Resolving += (_, assemblyName) =>
        {
            var name = assemblyName.Name;
            if (name == null) return null;
            if (LoadedAssemblies.TryGetValue(name, out var loaded)) return loaded;
            if (!DllBySimpleName.TryGetValue(name, out var dllPath)) return null;
            var asm = AssemblyLoadContext.Default.LoadFromAssemblyPath(dllPath);
            LoadedAssemblies[name] = asm;
            return asm;
        };

        foreach (var preferred in new[] { "CandideCreator.Shared", "Shared", "CandideServer", "MonoGame.Framework" })
        {
            if (DllBySimpleName.TryGetValue(preferred, out var path)) LoadAssembly(path);
        }
        foreach (var dll in dlls.OrderBy(Path.GetFileName)) TryLoadAssembly(dll);
        AppLogger.Info($"Loaded/registered DLLs: {DllBySimpleName.Count}");
    }

    private static Assembly LoadAssembly(string path)
    {
        var simpleName = Path.GetFileNameWithoutExtension(path);
        if (LoadedAssemblies.TryGetValue(simpleName, out var existing)) return existing;
        var asm = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(path));
        LoadedAssemblies[simpleName] = asm;
        return asm;
    }

    private static void TryLoadAssembly(string path)
    {
        try { LoadAssembly(path); }
        catch { }
    }

    private static IEnumerable<(string Name, object? Value)> EnumerateStateEntries(object state)
    {
        if (state is not IEnumerable enumerable) throw new InvalidOperationException($"State root is not enumerable: {state.GetType().FullName}");
        foreach (var entry in enumerable)
        {
            if (entry == null) continue;
            var t = entry.GetType();
            var name = t.GetField("Item1")?.GetValue(entry)?.ToString()
                       ?? t.GetProperty("Name")?.GetValue(entry)?.ToString()
                       ?? "<unknown>";
            var value = t.GetField("Item2")?.GetValue(entry)
                        ?? t.GetProperty("Value")?.GetValue(entry);
            yield return (name, value);
        }
    }

    private static IEnumerable<object?> EnumerateSequence(object? sequence)
    {
        if (sequence is null) yield break;
        if (sequence is string) yield break;
        if (sequence is IEnumerable enumerable)
        {
            foreach (var item in enumerable) yield return item;
        }
    }

    private static IEnumerable<(object? Key, object? Value)> EnumerateDictionary(object? dictionary)
    {
        if (dictionary is null) yield break;
        if (dictionary is IDictionary nonGeneric)
        {
            foreach (DictionaryEntry entry in nonGeneric) yield return (entry.Key, entry.Value);
            yield break;
        }
        if (dictionary is IEnumerable enumerable)
        {
            foreach (var entry in enumerable)
            {
                if (entry == null) continue;
                var t = entry.GetType();
                var key = t.GetProperty("Key")?.GetValue(entry) ?? t.GetField("Key")?.GetValue(entry);
                var value = t.GetProperty("Value")?.GetValue(entry) ?? t.GetField("Value")?.GetValue(entry);
                yield return (key, value);
            }
        }
    }

    private static object? ReadMember(object? obj, string name)
    {
        if (obj == null) return null;
        var type = obj.GetType();
        var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field != null) return field.GetValue(obj);
        var prop = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (prop != null && prop.GetIndexParameters().Length == 0) return prop.GetValue(obj);
        return null;
    }

    private static void SetMember(object? obj, string name, object? value)
    {
        if (obj == null) throw new ArgumentNullException(nameof(obj));
        var type = obj.GetType();
        var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field != null)
        {
            field.SetValue(obj, Convert.ChangeType(value, field.FieldType, CultureInfo.InvariantCulture));
            return;
        }
        var prop = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (prop != null && prop.CanWrite && prop.GetIndexParameters().Length == 0)
        {
            prop.SetValue(obj, Convert.ChangeType(value, prop.PropertyType, CultureInfo.InvariantCulture));
            return;
        }
        throw new MissingMemberException(type.FullName, name);
    }

    private static Type? FindType(string fullName)
    {
        foreach (var asm in LoadedAssemblies.Values.Distinct())
        {
            var t = asm.GetType(fullName, throwOnError: false);
            if (t != null) return t;
        }
        return AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(fullName, throwOnError: false)).FirstOrDefault(t => t != null);
    }

    private static object? GetStaticField(Type type, string fieldName)
    {
        return type.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
    }

    private static void InvokeIfExists(object target, string methodName)
    {
        target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, Type.EmptyTypes)?.Invoke(target, null);
    }

    private static int ToInt(object? value)
    {
        if (value == null) return 0;
        try { return Convert.ToInt32(value, CultureInfo.InvariantCulture); }
        catch { return 0; }
    }

    private static ulong ToUlong(object? value)
    {
        if (value == null) return 0;
        try { return Convert.ToUInt64(value, CultureInfo.InvariantCulture); }
        catch { return 0; }
    }

    private static double ToDouble(object? value)
    {
        if (value == null) return 0;
        try { return Convert.ToDouble(value, CultureInfo.InvariantCulture); }
        catch { return 0; }
    }

    private static int? TryGetCount(object? value)
    {
        if (value == null) return null;
        if (value is ICollection c) return c.Count;
        var countProp = value.GetType().GetProperty("Count", BindingFlags.Instance | BindingFlags.Public);
        if (countProp?.PropertyType == typeof(int)) return (int?)countProp.GetValue(value);
        if (value.GetType().IsArray) return ((Array)value).Length;
        return null;
    }

    private static string GetFriendlyTypeName(object? value)
    {
        if (value == null) return "<null>";
        var type = value as Type ?? value.GetType();
        if (!type.IsGenericType) return type.FullName ?? type.Name;
        var args = string.Join(", ", type.GetGenericArguments().Select(a => a.Name));
        var name = type.Name;
        var tick = name.IndexOf('`');
        if (tick >= 0) name = name[..tick];
        return $"{type.Namespace}.{name}<{args}>";
    }

    private static string DescribeValue(object? value)
    {
        if (value == null) return "";
        var count = TryGetCount(value);
        if (count.HasValue) return $"Count={count.Value}";
        if (value is string s) return s.Length > 100 ? s[..100] + "..." : s;
        if (value.GetType().IsPrimitive || value is decimal || value is Guid || value is DateTime || value.GetType().IsEnum)
            return value.ToString() ?? "";
        return "";
    }

    private static void PrintSummary(FullInspectionResult result, Options options)
    {
        AppLogger.Info("");
        AppLogger.Info($"Files scanned      : {result.TotalFilesScanned}");
        AppLogger.Info($"GameState files    : {result.TotalGameStates}");
        AppLogger.Info($"Player save files  : {result.TotalPlayerSaves}");

        foreach (var gs in result.GameStates)
        {
            AppLogger.Info("");
            AppLogger.Info($"GameState: {gs.SourceFileName}");
            AppLogger.Info($"- State entries : {gs.TotalStateEntries}");
            AppLogger.Info($"- ItemInstances : {gs.TotalItemInstances}");
            AppLogger.Info($"- Inventories   : {gs.TotalInventories}");
            AppLogger.Info($"- WorldItems    : {(gs.WorldItemCount?.ToString() ?? "<not found>")}");
            AppLogger.Info("- Top world/station item totals:");
            foreach (var item in gs.ItemTotals.Take(options.Limit))
                AppLogger.Info($"  {item.BaseDataId,-45} total={item.TotalStackCount,6} stacks={item.Stacks}");
        }

        AppLogger.Info("");
        AppLogger.Info(options.PlayerFilter == null ? "Player saves:" : $"Player saves matching '{options.PlayerFilter}':");
        foreach (var p in result.PlayerSaves.Take(options.Limit))
        {
            AppLogger.Info($"- {p.PlayerName,-24} playerId={p.PlayerId} money={p.Money} file={p.SourceFileName} saveName={p.SaveFileName}");
            var items = p.Items.Where(i => options.Filter == null || i.BaseDataId.Contains(options.Filter, StringComparison.OrdinalIgnoreCase)).Take(options.Limit).ToList();
            foreach (var item in items)
                AppLogger.Info($"    [{item.Section}:{item.SlotIndex}] {item.BaseDataId,-38} x{item.StackCount}");
        }
    }

    private static void WriteOutputs(FullInspectionResult result, string outputDir, Options options)
    {
        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(Path.Combine(outputDir, "summary.json"), JsonSerializer.Serialize(result, jsonOptions), Encoding.UTF8);

        WriteCsv(Path.Combine(outputDir, "files_scanned.csv"), result.FilesScanned,
            ["Path", "FileName", "SizeBytes", "DetectedGameState", "DetectedPlayerSave", "Note"],
            f => [f.Path, f.FileName, f.SizeBytes.ToString(CultureInfo.InvariantCulture), f.DetectedGameState.ToString(), f.DetectedPlayerSave.ToString(), f.Note]);

        var gameStates = result.GameStates;
        WriteCsv(Path.Combine(outputDir, "state_entries.csv"), gameStates.SelectMany(g => g.StateEntries),
            ["SourceFile", "Name", "TypeName", "Count", "Description"],
            e => [e.SourceFile, e.Name, e.TypeName, e.Count?.ToString(CultureInfo.InvariantCulture) ?? "", e.Description]);

        WriteCsv(Path.Combine(outputDir, "world_item_instances.csv"), FilterWorldItems(gameStates.SelectMany(g => g.ItemInstances), options.Filter),
            ["SourceFile", "SourceKind", "OwnerName", "InstanceId", "BaseDataId", "StackCount", "InventoryId", "AuraCount", "HasUsableInfo", "TypeName"],
            i => [i.SourceFile, i.SourceKind, i.OwnerName, i.InstanceId, i.BaseDataId, i.StackCount.ToString(CultureInfo.InvariantCulture), i.InventoryId, i.AuraCount.ToString(CultureInfo.InvariantCulture), i.HasUsableInfo.ToString(), i.TypeName]);

        WriteCsv(Path.Combine(outputDir, "world_item_totals.csv"), gameStates.SelectMany(g => g.ItemTotals).Where(i => options.Filter == null || i.BaseDataId.Contains(options.Filter, StringComparison.OrdinalIgnoreCase)),
            ["SourceFile", "BaseDataId", "TotalStackCount", "Stacks"],
            i => [i.SourceFile, i.BaseDataId, i.TotalStackCount.ToString(CultureInfo.InvariantCulture), i.Stacks.ToString(CultureInfo.InvariantCulture)]);

        WriteCsv(Path.Combine(outputDir, "world_inventories.csv"), gameStates.SelectMany(g => g.Inventories),
            ["SourceFile", "InventoryId", "Name", "InventoryType", "OwnerEntityId", "FilterItemFlags", "TotalSlots", "FilledSlots"],
            i => [i.SourceFile, i.InventoryId, i.Name, i.InventoryType, i.OwnerEntityId, i.FilterItemFlags, i.TotalSlots.ToString(CultureInfo.InvariantCulture), i.FilledSlots.ToString(CultureInfo.InvariantCulture)]);

        WriteCsv(Path.Combine(outputDir, "world_inventory_slots.csv"), gameStates.SelectMany(g => g.InventorySlots),
            ["SourceFile", "InventoryId", "InventoryName", "SlotIndex", "ItemInstanceId", "BaseDataId", "StackCount"],
            s => [s.SourceFile, s.InventoryId, s.InventoryName, s.SlotIndex.ToString(CultureInfo.InvariantCulture), s.ItemInstanceId, s.BaseDataId, s.StackCount.ToString(CultureInfo.InvariantCulture)]);

        var players = FilterPlayers(result.PlayerSaves, options.PlayerFilter).ToList();
        WriteCsv(Path.Combine(outputDir, "player_saves.csv"), players,
            ["SourceFile", "SaveFileName", "PlayerName", "PlayerId", "EntityId", "WorldId", "Money", "TimePlayed", "FilledInventorySlots", "FilledEquipmentSlots", "FilledSecondaryEquipmentSlots"],
            p => [p.SourceFileName, p.SaveFileName, p.PlayerName, p.PlayerId.ToString(CultureInfo.InvariantCulture), p.EntityId, p.WorldId, p.Money.ToString(CultureInfo.InvariantCulture), p.TimePlayed.ToString(CultureInfo.InvariantCulture), p.InventorySlots.ToString(CultureInfo.InvariantCulture), p.EquipmentSlots.ToString(CultureInfo.InvariantCulture), p.SecondaryEquipmentSlots.ToString(CultureInfo.InvariantCulture)]);

        var playerItems = players.SelectMany(p => p.Items).Where(i => options.Filter == null || i.BaseDataId.Contains(options.Filter, StringComparison.OrdinalIgnoreCase)).ToList();
        WriteCsv(Path.Combine(outputDir, "player_items.csv"), playerItems,
            ["SourceFile", "SaveFileName", "PlayerName", "PlayerId", "Section", "SlotIndex", "InstanceId", "BaseDataId", "StackCount", "AuraCount", "HasUsableInfo", "TypeName"],
            i => [i.SourceFile, i.SaveFileName, i.PlayerName, i.PlayerId.ToString(CultureInfo.InvariantCulture), i.Section, i.SlotIndex.ToString(CultureInfo.InvariantCulture), i.InstanceId, i.BaseDataId, i.StackCount.ToString(CultureInfo.InvariantCulture), i.AuraCount.ToString(CultureInfo.InvariantCulture), i.HasUsableInfo.ToString(), i.TypeName]);

        var playerTotals = playerItems
            .GroupBy(i => new { i.SourceFile, i.SaveFileName, i.PlayerName, i.PlayerId, i.BaseDataId })
            .Select(g => new PlayerItemTotalInfo(g.Key.SourceFile, g.Key.SaveFileName, g.Key.PlayerName, g.Key.PlayerId, g.Key.BaseDataId, g.Sum(i => i.StackCount), g.Count()))
            .OrderBy(i => i.PlayerName, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(i => i.TotalStackCount)
            .ToList();

        WriteCsv(Path.Combine(outputDir, "player_item_totals.csv"), playerTotals,
            ["SourceFile", "SaveFileName", "PlayerName", "PlayerId", "BaseDataId", "TotalStackCount", "Stacks"],
            i => [i.SourceFile, i.SaveFileName, i.PlayerName, i.PlayerId.ToString(CultureInfo.InvariantCulture), i.BaseDataId, i.TotalStackCount.ToString(CultureInfo.InvariantCulture), i.Stacks.ToString(CultureInfo.InvariantCulture)]);
    }

    private static IEnumerable<ItemInstanceInfo> FilterWorldItems(IEnumerable<ItemInstanceInfo> items, string? filter)
    {
        return filter == null ? items : items.Where(i => i.BaseDataId.Contains(filter, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<PlayerSaveInfo> FilterPlayers(IEnumerable<PlayerSaveInfo> players, string? filter)
    {
        return filter == null ? players : players.Where(p => PlayerMatches(p, filter));
    }

    private static void WriteCsv<T>(string path, IEnumerable<T> rows, string[] headers, Func<T, string[]> selector)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        writer.WriteLine(string.Join(",", headers.Select(CsvEscape)));
        foreach (var row in rows) writer.WriteLine(string.Join(",", selector(row).Select(CsvEscape)));
    }

    private static string CsvEscape(string? value)
    {
        value ??= "";
        if (value.Contains('"')) value = value.Replace("\"", "\"\"");
        if (value.Contains(',') || value.Contains('\n') || value.Contains('\r') || value.Contains('"')) return $"\"{value}\"";
        return value;
    }

    private static void PrintUsage()
    {
        AppLogger.Info("RomesteadSaveInspector v2 - read-only save recognizer");
        AppLogger.Info("");
        AppLogger.Info("Usage:");
        AppLogger.Info("  dotnet run -- [path-to-save-folder-or-file] [options]");
        AppLogger.Info("  RomesteadSaveInspector.exe [path-to-save-folder-or-file] [options]");
        AppLogger.Info("");
        AppLogger.Info("Put game_state and player/character save files into input/. This version scans all files in input/.");
        AppLogger.Info("");
        AppLogger.Info("Options:");
        AppLogger.Info("  --filter <text>       Only print/export matching item BaseDataId values, e.g. flint, wheat, meat");
        AppLogger.Info("  --player <text>       Only export player saves matching name/save filename/player id");
        AppLogger.Info("  --limit <n>           Console print limit per section. Default: 200");
        AppLogger.Info("  --lib <dir>           Directory containing game DLLs. Default: ./lib");
        AppLogger.Info("  --output <dir>        Output directory. Default: ./output");
        AppLogger.Info("  --no-files            Print to console only; do not write CSV/JSON files");
        AppLogger.Info("  --help                Show this help");
    }

    public sealed record PlayerItemStackUpdate(string SourceFile, string SaveFileName, string PlayerName, int PlayerId, string Section, int SlotIndex, string InstanceId, string BaseDataId, int StackCount);

    public sealed record SaveResult(int SavedFiles, int ChangedItems, string BackupDirectory, string OutputDirectory, int BackupIndex, string Message);

    public sealed class Options
    {
        public string? InputPath { get; set; }
        public string? Filter { get; set; }
        public string? PlayerFilter { get; set; }
        public string? LibDir { get; set; }
        public string? OutputDir { get; set; }
        public int Limit { get; set; } = 200;
        public bool NoFiles { get; set; }
        public bool ShowHelp { get; set; }

        public static Options Parse(string[] args)
        {
            var o = new Options();
            for (var i = 0; i < args.Length; i++)
            {
                var a = args[i];
                switch (a)
                {
                    case "--help" or "-h" or "/?": o.ShowHelp = true; break;
                    case "--filter" or "-f": o.Filter = RequireValue(args, ref i, a); break;
                    case "--player" or "-p": o.PlayerFilter = RequireValue(args, ref i, a); break;
                    case "--limit" or "-n":
                        if (!int.TryParse(RequireValue(args, ref i, a), out var n) || n < 1) throw new ArgumentException("--limit must be a positive integer.");
                        o.Limit = n;
                        break;
                    case "--lib": o.LibDir = RequireValue(args, ref i, a); break;
                    case "--output" or "-o": o.OutputDir = RequireValue(args, ref i, a); break;
                    case "--no-files": o.NoFiles = true; break;
                    default:
                        if (a.StartsWith("-")) throw new ArgumentException($"Unknown option: {a}");
                        if (o.InputPath != null) throw new ArgumentException($"Unexpected extra argument: {a}");
                        o.InputPath = a;
                        break;
                }
            }
            return o;
        }

        private static string RequireValue(string[] args, ref int i, string option)
        {
            if (i + 1 >= args.Length) throw new ArgumentException($"{option} requires a value.");
            return args[++i];
        }
    }

    private sealed class FullInspectionResult
    {
        public int TotalFilesScanned { get; set; }
        public int TotalGameStates { get; set; }
        public int TotalPlayerSaves { get; set; }
        public List<FileScanInfo> FilesScanned { get; set; } = new();
        public List<GameStateInspectionResult> GameStates { get; set; } = new();
        public List<PlayerSaveInfo> PlayerSaves { get; set; } = new();
    }

    private sealed class GameStateInspectionResult
    {
        public string SourceFile { get; set; } = "";
        public string SourceFileName { get; set; } = "";
        public int TotalStateEntries { get; set; }
        public int TotalItemInstances { get; set; }
        public int TotalInventories { get; set; }
        public int? WorldItemCount { get; set; }
        public List<StateEntryInfo> StateEntries { get; set; } = new();
        public List<ItemInstanceInfo> ItemInstances { get; set; } = new();
        public List<ItemTotalInfo> ItemTotals { get; set; } = new();
        public List<InventoryInfo> Inventories { get; set; } = new();
        public List<InventorySlotInfo> InventorySlots { get; set; } = new();
    }

    private sealed class PlayerSaveInfo
    {
        public string SourceFile { get; set; } = "";
        public string SourceFileName { get; set; } = "";
        public string SaveFileName { get; set; } = "";
        public string PlayerName { get; set; } = "";
        public int PlayerId { get; set; }
        public string EntityId { get; set; } = "";
        public string WorldId { get; set; } = "";
        public ulong Money { get; set; }
        public double TimePlayed { get; set; }
        public int InventorySlots { get; set; }
        public int EquipmentSlots { get; set; }
        public int SecondaryEquipmentSlots { get; set; }
        public List<PlayerItemInfo> Items { get; set; } = new();
    }

    private sealed record FileScanInfo(string Path, string FileName, long SizeBytes, bool DetectedGameState, bool DetectedPlayerSave, string Note);
    private sealed record StateEntryInfo(string SourceFile, string Name, string TypeName, int? Count, string Description);
    private sealed record ItemInstanceInfo(string SourceFile, string SourceKind, string OwnerName, string InstanceId, string BaseDataId, int StackCount, string InventoryId, int AuraCount, bool HasUsableInfo, string TypeName);
    private sealed record ItemTotalInfo(string SourceFile, string BaseDataId, int TotalStackCount, int Stacks);
    private sealed record InventoryInfo(string SourceFile, string InventoryId, string Name, string InventoryType, string OwnerEntityId, string FilterItemFlags, int TotalSlots, int FilledSlots);
    private sealed record InventorySlotInfo(string SourceFile, string InventoryId, string InventoryName, int SlotIndex, string ItemInstanceId, string BaseDataId, int StackCount);
    private sealed record PlayerItemInfo(string SourceFile, string SaveFileName, string PlayerName, int PlayerId, string Section, int SlotIndex, string InstanceId, string BaseDataId, int StackCount, int AuraCount, bool HasUsableInfo, string TypeName);
    private sealed record PlayerItemTotalInfo(string SourceFile, string SaveFileName, string PlayerName, int PlayerId, string BaseDataId, int TotalStackCount, int Stacks);
}
