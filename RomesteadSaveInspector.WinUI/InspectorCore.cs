using System.Collections;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using RomesteadSaveInspector.Database;

namespace RomesteadSaveInspector.WinUI;

internal static class InspectorCore
{
    private const string GameStateFileName = "game_state";
    private const int MaxSkillLevel = 100;

    private static readonly HashSet<string> RawPreservedWorldEntryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "WorldTiles",
        "BiomeIds",
        "Difficulties",
    };

    private static readonly SkillDefinition[] KnownSkills =
    [
        new("skill:crossbows", "Crossbows", "Increases damage done with crossbows by {0}", 0.3f),
        new("skill:swords", "Swords", "Increases damage done with swords by {0}", 0.3f),
        new("skill:shields", "Shields", "Reduces energy cost from blocking by {0}", -0.5f),
        new("skill:spears", "Spears", "Increases damage done with spears by {0}", 0.3f),
        new("skill:woodcutting", "Woodcutting", "Increases damage done to trees by {0}", 1f),
        new("skill:mining", "Mining", "Increases damage done to rocks and veins by {0}", 1f),
        new("skill:construction", "Construction", "Increases progress done by each hit when constructing by {0}", 0.8f),
        new("skill:throwing", "Throwing", "Increases damage done with thrown objects by {0}", 0.3f),
        new("skill:unarmed", "Unarmed", "Increases damage done with unarmed attacks by {0}", 0.5f),
        new("skill:tomes", "Scrolls", "Increases damage done with scrolls by {0}", 0.3f),
        new("skill:daggers", "Daggers", "Increases damage done with daggers by {0}", 0.3f),
        new("skill:sledgehammers", "Sledgehammers", "Increases damage done with sledgehammers by {0}", 0.3f),
        new("skill:bows", "Bows", "Increases damage done with bows by {0}", 0.3f),
    ];

    private static readonly HashSet<string> KnownSkillIds = KnownSkills.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static float ExperienceRequiredToLevelUpAtLevel(int level) => (float)Math.Round(100f * MathF.Pow(1.1f, level));

    private static readonly Dictionary<string, Assembly> LoadedAssemblies = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> DllBySimpleName = new(StringComparer.OrdinalIgnoreCase);
    private static bool SharedDataSetupAttempted;

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
            result.TotalWorldDescriptions = result.WorldDescriptions.Count;
            result.TotalPlayerSaves = result.PlayerSaves.Count;

            PrintSummary(result, options);

            if (!options.NoFiles)
            {
                WriteOutputs(result, outputDir, options);
                AppLogger.Info("");
                AppLogger.Info("Wrote output files:");
                AppLogger.Info("- summary.json");
                AppLogger.Info("- files_scanned.csv");
                AppLogger.Info("- world_desc.csv");
                AppLogger.Info("- world_desc_binding_report.json");
                AppLogger.Info("- world_desc_binding_report.csv");
                AppLogger.Info("- state_entries.csv");
                AppLogger.Info("- world_item_instances.csv");
                AppLogger.Info("- world_item_totals.csv");
                AppLogger.Info("- world_inventories.csv");
                AppLogger.Info("- world_inventory_slots.csv");
                AppLogger.Info("- citizens.csv");
                AppLogger.Info("- citizen_jobs.csv");
                AppLogger.Info("- citizen_entity_binding_report.json");
                AppLogger.Info("- citizen_entity_binding_report.csv");
                AppLogger.Info("- citizen_aura_runtime_report.csv");
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



    public static WorldRoundtripResult TestWorldRoundtrip(string libDir, string inputDir, string outputDir)
    {
        LoadGameAssemblies(libDir);
        Directory.CreateDirectory(outputDir);

        var gameStateFile = GetInputFiles(inputDir)
            .FirstOrDefault(f => Path.GetFileName(f).Equals(GameStateFileName, StringComparison.OrdinalIgnoreCase)
                              || Path.GetFileName(f).StartsWith(GameStateFileName + ".", StringComparison.OrdinalIgnoreCase)
                              || Path.GetFileNameWithoutExtension(f).Equals(GameStateFileName, StringComparison.OrdinalIgnoreCase));

        if (gameStateFile == null)
        {
            var message = "未在 input 中找到 game_state。";
            AppLogger.Error(message);
            return new WorldRoundtripResult(false, message, "", "", null, null, new[] { message });
        }

        var errors = new List<string>();
        var originalBytes = File.ReadAllBytes(gameStateFile);
        var originalCompressedBytes = originalBytes.LongLength;
        var originalDecompressedBytes = GetPossiblyGzippedPayloadLength(originalBytes);

        AppLogger.Info($"世界写入测试：读取 {gameStateFile}");
        AppLogger.Info($"世界写入测试：原始大小 compressed={originalCompressedBytes}, decompressed={originalDecompressedBytes}");

        if (!TryDeserializeGameStateWithFormat(originalBytes, out var originalState, out var originalWasGzip, out var originalNote) || originalState == null)
        {
            var message = "原始 game_state 反序列化失败：" + originalNote;
            AppLogger.Error(message);
            return new WorldRoundtripResult(false, message, gameStateFile, "", null, null, new[] { message });
        }

        var beforeInspection = InspectGameState(gameStateFile, originalState, new Options { Limit = 2000 });
        var before = CreateRoundtripMetrics(beforeInspection, originalCompressedBytes, originalDecompressedBytes, originalWasGzip, originalNote);

        byte[] serializerRoundtripBytes;
        try
        {
            serializerRoundtripBytes = SerializeGameStateToBytes(originalState, originalWasGzip);
        }
        catch (Exception ex)
        {
            var message = "无修改序列化 game_state 失败：" + ex.GetType().Name + " | " + ex.Message;
            AppLogger.Error(message, ex);
            return new WorldRoundtripResult(false, message, gameStateFile, "", before, null, new[] { message });
        }

        var unpatchedRoundtripPath = Path.Combine(outputDir, "game_state_roundtrip_unpatched_DO_NOT_USE");
        File.WriteAllBytes(unpatchedRoundtripPath, serializerRoundtripBytes);
        var unpatchedCompressedBytes = serializerRoundtripBytes.LongLength;
        var unpatchedDecompressedBytes = GetPossiblyGzippedPayloadLength(serializerRoundtripBytes);
        AppLogger.Info($"世界写入测试：未保留原始数组块的纯序列化文件 {unpatchedRoundtripPath}");
        AppLogger.Info($"世界写入测试：未保留原始数组块大小 compressed={unpatchedCompressedBytes}, decompressed={unpatchedDecompressedBytes}");

        object? unpatchedStateForEntryCompare = null;
        if (TryDeserializeGameStateWithFormat(serializerRoundtripBytes, out var unpatchedState, out _, out _) && unpatchedState != null)
        {
            unpatchedStateForEntryCompare = unpatchedState;
        }

        try
        {
            var unpatchedRows = BuildWorldStateEntrySizeCompare(originalState, unpatchedStateForEntryCompare, originalBytes, serializerRoundtripBytes);
            WriteWorldStateEntrySizeCompareReport(outputDir, unpatchedRows, "world_state_entry_size_compare_unpatched");
            AppLogger.Info($"未保留数组块诊断：{Path.Combine(outputDir, "world_state_entry_size_compare_unpatched.csv")}");
        }
        catch (Exception ex)
        {
            var message = "未保留数组块 StateEntry 大小诊断失败：" + ex.GetType().Name + " | " + ex.Message;
            AppLogger.Error(message, ex);
        }

        var roundtripBytes = serializerRoundtripBytes;
        var rawPreserveApplied = false;
        var rawPreserveNote = "";
        if (TryBuildRawPreservedWorldRoundtrip(originalState, originalBytes, serializerRoundtripBytes, originalWasGzip, allowSameLengthEntryDeltaPatch: false, allowUnsafeHeaderBypass: false, out var rawPreservedBytes, out rawPreserveNote))
        {
            roundtripBytes = rawPreservedBytes;
            rawPreserveApplied = true;
            AppLogger.Info("世界写入测试：已保留原始大型数组块。" + rawPreserveNote);
        }
        else
        {
            errors.Add("原始大型数组块保留失败：" + rawPreserveNote);
            AppLogger.Error("原始大型数组块保留失败：" + rawPreserveNote);
        }

        var rawPreservedRoundtripPath = Path.Combine(outputDir, "game_state_roundtrip_raw_preserve_test_DO_NOT_USE");
        File.WriteAllBytes(rawPreservedRoundtripPath, roundtripBytes);

        var roundtripPath = Path.Combine(outputDir, "game_state_roundtrip_test_DO_NOT_USE");
        File.WriteAllBytes(roundtripPath, roundtripBytes);

        var roundtripCompressedBytes = roundtripBytes.LongLength;
        var roundtripDecompressedBytes = GetPossiblyGzippedPayloadLength(roundtripBytes);
        AppLogger.Info($"世界写入测试：回写测试文件 {roundtripPath}");
        AppLogger.Info($"世界写入测试：回写大小 compressed={roundtripCompressedBytes}, decompressed={roundtripDecompressedBytes}");

        WorldRoundtripMetrics? after = null;
        object? roundtripStateForEntryCompare = null;
        if (!TryDeserializeGameStateWithFormat(roundtripBytes, out var roundtripState, out var roundtripWasGzip, out var roundtripNote) || roundtripState == null)
        {
            errors.Add("回写后的 game_state 无法反序列化：" + roundtripNote);
        }
        else
        {
            roundtripStateForEntryCompare = roundtripState;
            var afterInspection = InspectGameState(roundtripPath, roundtripState, new Options { Limit = 2000 });
            var note = roundtripNote + (rawPreserveApplied ? " | raw-preserved: " + rawPreserveNote : "");
            after = CreateRoundtripMetrics(afterInspection, roundtripCompressedBytes, roundtripDecompressedBytes, roundtripWasGzip, note);
            CompareMetric(errors, "State entries", before.StateEntries, after.StateEntries, mustEqual: true);
            CompareMetric(errors, "ItemInstances", before.ItemInstances, after.ItemInstances, mustEqual: true);
            CompareMetric(errors, "Inventories", before.Inventories, after.Inventories, mustEqual: true);
            CompareMetric(errors, "Citizens", before.Citizens, after.Citizens, mustEqual: true);
            CompareMetric(errors, "WorldItems", before.WorldItems, after.WorldItems, mustEqual: true);
        }

        try
        {
            var entryCompareRows = BuildWorldStateEntrySizeCompare(originalState, roundtripStateForEntryCompare, originalBytes, roundtripBytes);
            WriteWorldStateEntrySizeCompareReport(outputDir, entryCompareRows);
            AppLogger.Info($"世界状态块大小诊断：{Path.Combine(outputDir, "world_state_entry_size_compare.csv")}");
        }
        catch (Exception ex)
        {
            var message = "StateEntry 大小诊断失败：" + ex.GetType().Name + " | " + ex.Message;
            errors.Add(message);
            AppLogger.Error(message, ex);
            WriteWorldStateEntrySizeCompareErrorReport(outputDir, message);
        }

        if (roundtripDecompressedBytes <= 0)
        {
            errors.Add("回写后的解压大小为 0，文件无效。");
        }
        else
        {
            var delta = Math.Abs(roundtripDecompressedBytes - originalDecompressedBytes);
            var ratio = originalDecompressedBytes <= 0 ? 1 : (double)delta / originalDecompressedBytes;
            if (ratio > 0.05)
            {
                errors.Add($"解压后大小差异过大：原始 {originalDecompressedBytes:N0} bytes，回写 {roundtripDecompressedBytes:N0} bytes，差异 {ratio:P2}。");
            }
        }

        var passed = errors.Count == 0;
        var messageLines = new List<string>
        {
            passed ? "世界无修改写入测试通过（原始大型数组块保留模式）。" : "世界无修改写入测试失败，未开放 game_state 修改。",
            $"原始文件：{gameStateFile}",
            $"测试输出：{roundtripPath}",
            $"原始数组块保留输出：{rawPreservedRoundtripPath}",
            $"未保留数组块测试输出：{unpatchedRoundtripPath}",
            $"原始解压大小：{originalDecompressedBytes:N0} bytes",
            $"回写解压大小：{roundtripDecompressedBytes:N0} bytes",
            $"报告：{Path.Combine(outputDir, "world_roundtrip_report.csv")}",
            $"状态块大小报告：{Path.Combine(outputDir, "world_state_entry_size_compare.csv")}",
            $"未保留数组块诊断：{Path.Combine(outputDir, "world_state_entry_size_compare_unpatched.csv")}",
            $"原始数组块保留：{(rawPreserveApplied ? rawPreserveNote : "未成功")}",
            "说明：该按钮仍只做无修改测试，不会覆盖 input。v43 的村民保存会复用该原始数组块保留路线，并在覆盖前执行二次校验。"
        };
        if (!passed)
        {
            messageLines.Add("失败原因：");
            messageLines.AddRange(errors.Select(e => "- " + e));
        }

        var result = new WorldRoundtripResult(passed, string.Join(Environment.NewLine, messageLines), gameStateFile, roundtripPath, before, after, errors);
        WriteWorldRoundtripReport(outputDir, result);
        AppLogger.Info(result.Message);
        return result;
    }

    private static WorldRoundtripMetrics CreateRoundtripMetrics(GameStateInspectionResult inspection, long compressedBytes, long decompressedBytes, bool wasGzip, string note)
    {
        return new WorldRoundtripMetrics(
            compressedBytes,
            decompressedBytes,
            wasGzip,
            inspection.TotalStateEntries,
            inspection.TotalItemInstances,
            inspection.TotalInventories,
            inspection.TotalCitizens,
            inspection.WorldItemCount,
            note);
    }

    private static byte[] SerializeGameStateToBytes(object state, bool gzipOutput)
    {
        using var serialized = new MemoryStream();
        if (gzipOutput)
        {
            using (var gzip = new GZipStream(serialized, CompressionLevel.Optimal, leaveOpen: true))
            {
                SerializeWithType(gzip, GetGameStateListType(), state);
            }
        }
        else
        {
            SerializeWithType(serialized, GetGameStateListType(), state);
        }
        return serialized.ToArray();
    }

    private static bool TryBuildRawPreservedWorldRoundtrip(object originalState, byte[] originalFileBytes, byte[] serializedFileBytes, bool gzipOutput, bool allowSameLengthEntryDeltaPatch, bool allowUnsafeHeaderBypass, out byte[] patchedFileBytes, out string note)
    {
        patchedFileBytes = Array.Empty<byte>();
        note = "";

        try
        {
            var originalPayload = GetPossiblyGzippedPayloadBytes(originalFileBytes);
            var serializedPayload = GetPossiblyGzippedPayloadBytes(serializedFileBytes);

            if (!TryExtractTopLevelStateEntrySpansFromPayload(originalPayload, out var originalSpans, out var originalSpanNote))
            {
                note = "original parser: " + originalSpanNote;
                return false;
            }

            if (!TryExtractTopLevelStateEntrySpansFromPayload(serializedPayload, out var serializedSpans, out var serializedSpanNote))
            {
                note = "serialized parser: " + serializedSpanNote;
                return false;
            }

            var originalEntries = EnumerateStateEntries(originalState).ToList();
            if (originalEntries.Count != serializedSpans.Count || originalEntries.Count != originalSpans.Count)
            {
                note = $"entry count mismatch. entries={originalEntries.Count}, originalSpans={originalSpans.Count}, serializedSpans={serializedSpans.Count}";
                return false;
            }

            // Raw-preserved entry payloads can contain compact string-table indexes.
            // We accept either exact headers or append-only string-map growth where all old string
            // indexes remain stable. Reordering/rewriting the original string map is rejected.
            var originalHeaderLength = originalSpans.Count > 0 ? originalSpans[0].Offset : originalPayload.LongLength;
            var serializedHeaderLength = serializedSpans.Count > 0 ? serializedSpans[0].Offset : serializedPayload.LongLength;
            var rawPreserveHeaderStable = true;
            if (!TryValidateRawPreserveHeader(originalPayload, originalHeaderLength, serializedPayload, serializedHeaderLength, out var headerNote))
            {
                rawPreserveHeaderStable = false;
                if (allowSameLengthEntryDeltaPatch && TryBuildSameLengthEntryDeltaPatch(originalState, originalPayload, serializedPayload, originalSpans, serializedSpans, gzipOutput, headerNote, out patchedFileBytes, out note))
                {
                    return true;
                }

                if (allowUnsafeHeaderBypass)
                {
                    AppLogger.Warn("Native hybrid world-array raw-preserve: accepting header/string-map difference for custom world array payload preservation. " + headerNote);
                    headerNote = "header/string-map difference accepted for native hybrid custom world-array raw-preserve: " + headerNote;
                }
                else
                {
                    note = headerNote;
                    return false;
                }
            }

            var preserved = new List<string>();
            using var patchedPayload = new MemoryStream(serializedPayload.Length + originalPayload.Length);
            long cursor = 0;

            for (var i = 0; i < serializedSpans.Count; i++)
            {
                var serializedSpan = serializedSpans[i];
                if (serializedSpan.Offset < cursor)
                {
                    note = $"serialized span overlap at index {i}.";
                    return false;
                }

                WriteBytesRange(patchedPayload, serializedPayload, cursor, serializedSpan.Offset - cursor);

                var entryName = originalEntries[i].Name ?? "";
                var originalSpanForDecision = originalSpans[i];
                var preserveByName = RawPreservedWorldEntryNames.Contains(entryName);
                var preserveByCollapsedLargeWorldPayload = allowUnsafeHeaderBypass
                    && originalSpanForDecision.Length >= 5_000_000
                    && serializedSpan.Length <= 1024;

                if (preserveByName || preserveByCollapsedLargeWorldPayload)
                {
                    var originalSpan = originalSpans[i];
                    var reason = preserveByName ? entryName : "large-collapsed-world-payload";

                    // v65: when the serializer rebuilt the compact string map, copying an entire
                    // original top-level entry is not enough. The entry wrapper itself contains
                    // compact string IDs for the ValueTuple field names and for Item1 (the state
                    // entry name). If those IDs are left pointing at the old string map, the tool
                    // can deserialize the result but the game may load the large array under the
                    // wrong key and report a broken world. Rewrite only those wrapper string IDs
                    // to the serialized file's string map, while keeping the original large array
                    // value payload bytes.
                    if (allowUnsafeHeaderBypass && !rawPreserveHeaderStable && !ByteRangesEqual(originalPayload, 0, serializedPayload, 0, Math.Min(originalHeaderLength, serializedHeaderLength)))
                    {
                        if (!TryRewriteRawPreservedTopLevelEntryForSerializedStringMap(
                                originalPayload, originalSpan, originalHeaderLength,
                                serializedPayload, serializedHeaderLength,
                                out var rewrittenEntry, out var rewriteNote))
                        {
                            note = $"failed to rewrite compact string IDs for raw-preserved entry {i}:{reason}: {rewriteNote}";
                            return false;
                        }

                        patchedPayload.Write(rewrittenEntry, 0, rewrittenEntry.Length);
                        preserved.Add($"{i}:{reason}({originalSpan.Length:N0} bytes, remapped wrapper ids: {rewriteNote})");
                    }
                    else
                    {
                        WriteBytesRange(patchedPayload, originalPayload, originalSpan.Offset, originalSpan.Length);
                        preserved.Add($"{i}:{reason}({originalSpan.Length:N0} bytes)");
                    }
                }
                else
                {
                    WriteBytesRange(patchedPayload, serializedPayload, serializedSpan.Offset, serializedSpan.Length);
                }

                cursor = serializedSpan.Offset + serializedSpan.Length;
            }

            WriteBytesRange(patchedPayload, serializedPayload, cursor, serializedPayload.LongLength - cursor);

            if (preserved.Count == 0)
            {
                note = "no matching raw-preserved entries found.";
                return false;
            }

            patchedFileBytes = WrapPayloadWithCompression(patchedPayload.ToArray(), gzipOutput);
            note = "保留 " + string.Join(", ", preserved);
            if (!string.IsNullOrWhiteSpace(headerNote))
                note += " | " + headerNote;
            return true;
        }
        catch (Exception ex)
        {
            note = ex.GetType().Name + " | " + ex.Message;
            patchedFileBytes = Array.Empty<byte>();
            return false;
        }
    }


    private const int MaxSameLengthDeltaPatchBytes = 2048;

    private static bool TryBuildSameLengthEntryDeltaPatch(object originalState, byte[] originalPayload, byte[] serializedPayload, List<EntryRawSpan> originalSpans, List<EntryRawSpan> serializedSpans, bool gzipOutput, string rejectedHeaderNote, out byte[] patchedFileBytes, out string note)
    {
        patchedFileBytes = Array.Empty<byte>();
        note = "";
        try
        {
            var originalEntries = EnumerateStateEntries(originalState).ToList();
            if (originalEntries.Count != originalSpans.Count || originalEntries.Count != serializedSpans.Count)
            {
                note = "same-length delta patch rejected: entry count mismatch after header rejection. " + rejectedHeaderNote;
                return false;
            }

            var changedEntries = new List<(int Index, string Name, EntryRawSpan Original, EntryRawSpan Serialized, List<int> DiffOffsets)>();
            for (var i = 0; i < originalSpans.Count; i++)
            {
                var name = originalEntries[i].Name ?? "";
                if (RawPreservedWorldEntryNames.Contains(name)) continue;

                var originalSpan = originalSpans[i];
                var serializedSpan = serializedSpans[i];
                if (originalSpan.Length != serializedSpan.Length)
                {
                    note = $"same-length delta patch rejected: entry {i}:{name} length changed. original={originalSpan.Length:N0}, serialized={serializedSpan.Length:N0}. " + rejectedHeaderNote;
                    return false;
                }

                var diffs = new List<int>();
                for (long j = 0; j < originalSpan.Length; j++)
                {
                    var oi = checked((int)(originalSpan.Offset + j));
                    var si = checked((int)(serializedSpan.Offset + j));
                    if (originalPayload[oi] != serializedPayload[si])
                    {
                        diffs.Add(checked((int)j));
                        if (diffs.Count > MaxSameLengthDeltaPatchBytes)
                        {
                            note = $"same-length delta patch rejected: entry {i}:{name} changed too many bytes (> {MaxSameLengthDeltaPatchBytes}). " + rejectedHeaderNote;
                            return false;
                        }
                    }
                }

                if (diffs.Count > 0) changedEntries.Add((i, name, originalSpan, serializedSpan, diffs));
            }

            if (changedEntries.Count == 0)
            {
                note = "same-length delta patch rejected: no changed non-array entries found. " + rejectedHeaderNote;
                return false;
            }

            var unsupported = changedEntries.FirstOrDefault(e => !string.Equals(e.Name, "Citizens", StringComparison.OrdinalIgnoreCase));
            if (unsupported.Name != null)
            {
                note = $"same-length delta patch rejected: changed entry {unsupported.Index}:{unsupported.Name} is not currently supported. " + rejectedHeaderNote;
                return false;
            }

            var patchedPayload = (byte[])originalPayload.Clone();
            var totalDiffs = 0;
            foreach (var changed in changedEntries)
            {
                totalDiffs += changed.DiffOffsets.Count;
                foreach (var relative in changed.DiffOffsets)
                {
                    var targetIndex = checked((int)(changed.Original.Offset + relative));
                    var sourceIndex = checked((int)(changed.Serialized.Offset + relative));
                    patchedPayload[targetIndex] = serializedPayload[sourceIndex];
                }
            }

            patchedFileBytes = WrapPayloadWithCompression(patchedPayload, gzipOutput);
            note = $"same-length Citizens delta patch; kept original header/string map and original world arrays. changedEntries={changedEntries.Count}, changedBytes={totalDiffs:N0}. Original header rejection was: {rejectedHeaderNote}";
            AppLogger.Info("Raw-preserve fallback accepted: " + note);
            return true;
        }
        catch (Exception ex)
        {
            note = "same-length delta patch failed: " + ex.GetType().Name + " | " + ex.Message + ". " + rejectedHeaderNote;
            patchedFileBytes = Array.Empty<byte>();
            return false;
        }
    }

    private static void WriteBytesRange(MemoryStream target, byte[] source, long offset, long length)
    {
        if (length <= 0) return;
        if (offset < 0 || length < 0 || offset + length > source.LongLength)
            throw new ArgumentOutOfRangeException(nameof(offset), $"Invalid byte range offset={offset}, length={length}, source={source.LongLength}.");
        target.Write(source, checked((int)offset), checked((int)length));
    }

    private static bool ByteRangesEqual(byte[] left, long leftOffset, byte[] right, long rightOffset, long length)
    {
        if (length < 0 || leftOffset < 0 || rightOffset < 0) return false;
        if (leftOffset + length > left.LongLength || rightOffset + length > right.LongLength) return false;
        for (long i = 0; i < length; i++)
        {
            var leftIndex = checked((int)(leftOffset + i));
            var rightIndex = checked((int)(rightOffset + i));
            if (left[leftIndex] != right[rightIndex]) return false;
        }
        return true;
    }


    private static bool TryRewriteRawPreservedTopLevelEntryForSerializedStringMap(
        byte[] originalPayload,
        EntryRawSpan originalSpan,
        long originalHeaderLength,
        byte[] serializedPayload,
        long serializedHeaderLength,
        out byte[] rewrittenEntry,
        out string note)
    {
        rewrittenEntry = Array.Empty<byte>();
        note = "";
        try
        {
            if (!TryReadStringMapHeader(originalPayload, originalHeaderLength, out var originalHeader, out var originalHeaderNote) || !originalHeader.HasDirectStringMap)
            {
                note = "cannot parse original direct string map: " + originalHeaderNote;
                return false;
            }

            if (!TryReadStringMapHeader(serializedPayload, serializedHeaderLength, out var serializedHeader, out var serializedHeaderNote) || !serializedHeader.HasDirectStringMap)
            {
                note = "cannot parse serialized direct string map: " + serializedHeaderNote;
                return false;
            }

            var serializedStringToId = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < serializedHeader.Strings.Count; i++)
            {
                if (!serializedStringToId.ContainsKey(serializedHeader.Strings[i]))
                    serializedStringToId[serializedHeader.Strings[i]] = i + 1;
            }

            string? OriginalStringById(int id)
            {
                if (id == 0) return null;
                var idx = id - 1;
                return idx >= 0 && idx < originalHeader.Strings.Count ? originalHeader.Strings[idx] : null;
            }

            bool TryMapOriginalStringId(int originalId, out int serializedId, out string? value)
            {
                serializedId = 0;
                value = OriginalStringById(originalId);
                if (originalId == 0)
                {
                    serializedId = 0;
                    return true;
                }
                if (value == null) return false;
                return serializedStringToId.TryGetValue(value, out serializedId);
            }

            using var input = new MemoryStream(originalPayload, checked((int)originalSpan.Offset), checked((int)originalSpan.Length), writable: false);
            using var reader = new BinaryReader(input, Encoding.UTF8, leaveOpen: true);
            var fieldCount = Read7BitEncodedIntStrict(reader);
            if (fieldCount == 0)
            {
                rewrittenEntry = originalPayload.AsSpan(checked((int)originalSpan.Offset), checked((int)originalSpan.Length)).ToArray();
                note = "fieldCount=0";
                return true;
            }

            var declaredBodyLength = Read7BitEncodedIntStrict(reader);
            var bodyStart = input.Position;
            var bodyEnd = bodyStart + declaredBodyLength;
            if (bodyEnd > input.Length)
            {
                note = $"declared body length exceeds entry length. body={declaredBodyLength}, entry={originalSpan.Length}";
                return false;
            }

            using var bodyOut = new MemoryStream();
            var remapped = new List<string>();
            for (var f = 0; f < fieldCount; f++)
            {
                var typeCode = reader.ReadByte();
                var oldFieldNameId = Read7BitEncodedIntStrict(reader);
                if (!TryMapOriginalStringId(oldFieldNameId, out var newFieldNameId, out var fieldName))
                {
                    note = $"field name id {oldFieldNameId} is not present in serialized string map";
                    return false;
                }

                var valueLength = Read7BitEncodedIntStrict(reader);
                var valueBytes = reader.ReadBytes(valueLength);
                if (valueBytes.Length != valueLength)
                {
                    note = "unexpected end while reading field value";
                    return false;
                }

                if (oldFieldNameId != newFieldNameId)
                    remapped.Add($"field:{fieldName ?? "<null>"} {oldFieldNameId}->{newFieldNameId}");

                byte[] newValueBytes = valueBytes;
                if (typeCode == 34) // CompactStringSerializer payload: one 7-bit string id.
                {
                    using var valueInput = new MemoryStream(valueBytes, writable: false);
                    using var valueReader = new BinaryReader(valueInput, Encoding.UTF8, leaveOpen: true);
                    var oldStringId = Read7BitEncodedIntStrict(valueReader);
                    if (valueInput.Position == valueInput.Length)
                    {
                        if (!TryMapOriginalStringId(oldStringId, out var newStringId, out var stringValue))
                        {
                            note = $"compact string value id {oldStringId} is not present in serialized string map";
                            return false;
                        }
                        if (oldStringId != newStringId)
                        {
                            using var valueOutput = new MemoryStream();
                            Write7BitEncodedInt(valueOutput, newStringId);
                            newValueBytes = valueOutput.ToArray();
                            remapped.Add($"value:{fieldName ?? "<null>"}='{stringValue ?? "<null>"}' {oldStringId}->{newStringId}");
                        }
                    }
                }

                bodyOut.WriteByte(typeCode);
                Write7BitEncodedInt(bodyOut, newFieldNameId);
                Write7BitEncodedInt(bodyOut, newValueBytes.Length);
                bodyOut.Write(newValueBytes, 0, newValueBytes.Length);
            }

            if (reader.BaseStream.Position != bodyEnd)
            {
                note = $"field parser did not end at declared body end. pos={reader.BaseStream.Position}, end={bodyEnd}";
                return false;
            }

            using var output = new MemoryStream();
            Write7BitEncodedInt(output, fieldCount);
            var newBody = bodyOut.ToArray();
            Write7BitEncodedInt(output, newBody.Length);
            output.Write(newBody, 0, newBody.Length);
            rewrittenEntry = output.ToArray();
            note = remapped.Count == 0 ? "no wrapper id changes needed" : string.Join("; ", remapped.Take(8)) + (remapped.Count > 8 ? $"; ... +{remapped.Count - 8} more" : "");
            return true;
        }
        catch (Exception ex)
        {
            note = ex.GetType().Name + " | " + ex.Message;
            rewrittenEntry = Array.Empty<byte>();
            return false;
        }
    }

    private static void Write7BitEncodedInt(Stream stream, int value)
    {
        uint v = unchecked((uint)value);
        while (v >= 0x80)
        {
            stream.WriteByte((byte)(v | 0x80));
            v >>= 7;
        }
        stream.WriteByte((byte)v);
    }

    private const int MaxAllowedStringMapAppendBytes = 64 * 1024;
    private const int MaxLoggedAppendedStringMapValues = 12;

    private static bool TryValidateRawPreserveHeader(byte[] originalPayload, long originalHeaderLength, byte[] serializedPayload, long serializedHeaderLength, out string note)
    {
        note = "";

        if (originalHeaderLength < 0 || serializedHeaderLength < 0 ||
            originalHeaderLength > originalPayload.LongLength || serializedHeaderLength > serializedPayload.LongLength)
        {
            note = $"invalid raw-preserve header bounds. originalPrefix={originalHeaderLength:N0} bytes, serializedPrefix={serializedHeaderLength:N0} bytes.";
            return false;
        }

        if (originalHeaderLength == serializedHeaderLength &&
            ByteRangesEqual(originalPayload, 0, serializedPayload, 0, originalHeaderLength))
        {
            note = "header/string map exact";
            return true;
        }

        var delta = serializedHeaderLength - originalHeaderLength;
        if (delta < 0)
        {
            note = $"serialized header/string map shrank. originalPrefix={originalHeaderLength:N0} bytes, serializedPrefix={serializedHeaderLength:N0} bytes, delta={delta:N0} bytes. Refusing raw-preserve patch.";
            AppLogger.Warn("Raw-preserve header compatibility rejected: " + note);
            return false;
        }

        if (delta > MaxAllowedStringMapAppendBytes)
        {
            note = $"serialized header/string map grew too much. originalPrefix={originalHeaderLength:N0} bytes, serializedPrefix={serializedHeaderLength:N0} bytes, delta={delta:N0} bytes, limit={MaxAllowedStringMapAppendBytes:N0} bytes. Refusing raw-preserve patch.";
            AppLogger.Warn("Raw-preserve header compatibility rejected: " + note);
            return false;
        }

        // Balanced mode: allow new strings only when the serialized string map keeps the entire
        // original string map as an exact prefix and only appends new values at the end. In that
        // case old string-map indexes used by raw-preserved WorldTiles/BiomeIds/Difficulties stay
        // stable. This is stricter than R1.1.33's blind "small growth" rule, but less restrictive
        // than R1.1.34's byte-for-byte header requirement.
        if (!TryReadStringMapHeader(originalPayload, originalHeaderLength, out var originalHeader, out var originalMapNote))
        {
            note = "cannot parse original string map/header safely: " + originalMapNote;
            AppLogger.Warn("Raw-preserve header compatibility rejected: " + note);
            return false;
        }

        if (!TryReadStringMapHeader(serializedPayload, serializedHeaderLength, out var serializedHeader, out var serializedMapNote))
        {
            note = "cannot parse serialized string map/header safely: " + serializedMapNote;
            AppLogger.Warn("Raw-preserve header compatibility rejected: " + note);
            return false;
        }

        if (!originalHeader.HasDirectStringMap || !serializedHeader.HasDirectStringMap)
        {
            note = $"header/string map changed but direct string maps were not available for prefix validation. original={originalMapNote}, serialized={serializedMapNote}. Refusing raw-preserve patch.";
            AppLogger.Warn("Raw-preserve header compatibility rejected: " + note);
            return false;
        }

        if (!ByteRangesEqual(originalPayload, originalHeader.StringMapEndOffset, serializedPayload, serializedHeader.StringMapEndOffset, originalHeaderLength - originalHeader.StringMapEndOffset))
        {
            note = $"root/list header after string map changed. originalPrefix={originalHeaderLength:N0} bytes, serializedPrefix={serializedHeaderLength:N0} bytes. Refusing raw-preserve patch.";
            AppLogger.Warn("Raw-preserve header compatibility rejected: " + note);
            return false;
        }

        if (serializedHeader.Strings.Count < originalHeader.Strings.Count)
        {
            note = $"serialized string map has fewer strings. originalStrings={originalHeader.Strings.Count:N0}, serializedStrings={serializedHeader.Strings.Count:N0}. Refusing raw-preserve patch.";
            AppLogger.Warn("Raw-preserve header compatibility rejected: " + note);
            return false;
        }

        for (var i = 0; i < originalHeader.Strings.Count; i++)
        {
            if (!string.Equals(originalHeader.Strings[i], serializedHeader.Strings[i], StringComparison.Ordinal))
            {
                note = $"serialized string map reordered or rewrote existing entry at index {i}. Refusing raw-preserve patch because old array indexes would no longer be stable.";
                AppLogger.Warn("Raw-preserve header compatibility rejected: " + note);
                return false;
            }
        }

        var appended = serializedHeader.Strings.Skip(originalHeader.Strings.Count).ToList();
        if (appended.Count == 0)
        {
            note = $"header changed but no appended string-map values were detected. originalPrefix={originalHeaderLength:N0} bytes, serializedPrefix={serializedHeaderLength:N0} bytes. Refusing raw-preserve patch.";
            AppLogger.Warn("Raw-preserve header compatibility rejected: " + note);
            return false;
        }

        var appendedPreview = string.Join("; ", appended.Take(MaxLoggedAppendedStringMapValues));
        if (appended.Count > MaxLoggedAppendedStringMapValues) appendedPreview += $"; ... +{appended.Count - MaxLoggedAppendedStringMapValues} more";
        note = $"header/string map append-only compatible. originalPrefix={originalHeaderLength:N0} bytes, serializedPrefix={serializedHeaderLength:N0} bytes, delta={delta:N0} bytes, appendedStrings={appended.Count:N0}: {appendedPreview}";
        AppLogger.Info("Raw-preserve header compatibility accepted: " + note);
        return true;
    }

    private sealed record StringMapHeaderInfo(bool HasDirectStringMap, List<string> Strings, long StringMapEndOffset);

    private static bool TryReadStringMapHeader(byte[] payload, long preEntryHeaderLength, out StringMapHeaderInfo header, out string note)
    {
        header = new StringMapHeaderInfo(false, new List<string>(), 0);
        note = "";

        try
        {
            if (preEntryHeaderLength < 4 || preEntryHeaderLength > payload.LongLength)
            {
                note = $"invalid pre-entry header length {preEntryHeaderLength:N0}.";
                return false;
            }

            using var memory = new MemoryStream(payload, writable: false);
            using var reader = new BinaryReader(memory, Encoding.UTF8, leaveOpen: true);
            var magic = reader.ReadUInt32();

            if (magic == 6448483u)
            {
                header = new StringMapHeaderInfo(false, new List<string>(), memory.Position);
                note = "payload has no serialized string map.";
                return true;
            }

            if (magic != 23225699u)
            {
                note = "unexpected Candide header magic: " + magic.ToString(CultureInfo.InvariantCulture);
                return false;
            }

            var count = Read7BitEncodedIntStrict(reader);
            if (count < 0)
            {
                note = "negative string map count.";
                return false;
            }

            var strings = new List<string>(count);
            if (count > 0)
            {
                var elementTypeCode = reader.ReadByte();
                if (elementTypeCode != 33)
                {
                    // Type 34 is an integer-coded map in this serializer. It is valid, but we
                    // cannot prove append-only string compatibility from it, so header changes
                    // must be rejected for safety.
                    for (var i = 0; i < count; i++) _ = Read7BitEncodedIntStrict(reader);
                    header = new StringMapHeaderInfo(false, strings, memory.Position);
                    note = "string map element type is not direct string values: " + elementTypeCode.ToString(CultureInfo.InvariantCulture);
                    return true;
                }

                for (var i = 0; i < count; i++) strings.Add(reader.ReadString());
            }

            header = new StringMapHeaderInfo(true, strings, memory.Position);
            note = $"direct string map count={strings.Count:N0}, mapEnd={memory.Position:N0}.";
            return true;
        }
        catch (Exception ex)
        {
            note = ex.GetType().Name + " | " + ex.Message;
            header = new StringMapHeaderInfo(false, new List<string>(), 0);
            return false;
        }
    }

    private static byte[] WrapPayloadWithCompression(byte[] payload, bool gzip)
    {
        if (!gzip) return payload;
        using var output = new MemoryStream();
        using (var gzipStream = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzipStream.Write(payload, 0, payload.Length);
        }
        return output.ToArray();
    }

    private static long GetPossiblyGzippedPayloadLength(byte[] bytes)
    {
        try
        {
            using var stream = OpenBytesPossiblyGzipped(bytes, out _);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.Length;
        }
        catch
        {
            return -1;
        }
    }

    private static byte[] GetPossiblyGzippedPayloadBytes(byte[] bytes)
    {
        using var stream = OpenBytesPossiblyGzipped(bytes, out _);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static IReadOnlyList<WorldStateEntrySizeCompareRow> BuildWorldStateEntrySizeCompare(object originalState, object? roundtripState, byte[] originalBytes, byte[] roundtripBytes)
    {
        var originalEntries = EnumerateStateEntries(originalState).ToList();
        var roundtripEntries = roundtripState == null ? new List<(string Name, object? Value)>() : EnumerateStateEntries(roundtripState).ToList();

        var originalRawOk = TryExtractTopLevelStateEntrySpans(originalBytes, out var originalSpans, out var originalSpanNote);
        var roundtripRawOk = TryExtractTopLevelStateEntrySpans(roundtripBytes, out var roundtripSpans, out var roundtripSpanNote);

        var max = new[] { originalEntries.Count, roundtripEntries.Count, originalSpans.Count, roundtripSpans.Count }.Max();
        var rows = new List<WorldStateEntrySizeCompareRow>(max);

        for (var i = 0; i < max; i++)
        {
            var originalEntry = i < originalEntries.Count ? originalEntries[i] : default;
            var roundtripEntry = i < roundtripEntries.Count ? roundtripEntries[i] : default;
            var originalSpan = i < originalSpans.Count ? originalSpans[i] : null;
            var roundtripSpan = i < roundtripSpans.Count ? roundtripSpans[i] : null;

            var name = !string.IsNullOrWhiteSpace(originalEntry.Name)
                ? originalEntry.Name
                : (!string.IsNullOrWhiteSpace(roundtripEntry.Name) ? roundtripEntry.Name : "<unknown>");
            var typeName = originalEntry.Value != null
                ? GetFriendlyTypeName(originalEntry.Value)
                : (roundtripEntry.Value != null ? GetFriendlyTypeName(roundtripEntry.Value) : "");

            long? originalSize = originalSpan?.Length;
            long? roundtripSize = roundtripSpan?.Length;
            long? diffBytes = originalSize.HasValue && roundtripSize.HasValue ? roundtripSize.Value - originalSize.Value : null;
            double? diffPercent = originalSize.HasValue && originalSize.Value != 0 && diffBytes.HasValue
                ? diffBytes.Value / (double)originalSize.Value
                : null;

            var canDeserialize = i < originalEntries.Count;
            var canRoundtrip = i < roundtripEntries.Count && i < roundtripSpans.Count;
            var status = BuildEntrySizeStatus(originalSize, roundtripSize, diffBytes, canDeserialize, canRoundtrip, originalRawOk, roundtripRawOk);
            var note = string.Join(" | ", new[]
                {
                    originalRawOk ? "" : "original parser: " + originalSpanNote,
                    roundtripRawOk ? "" : "roundtrip parser: " + roundtripSpanNote,
                    originalSpan?.Note ?? "",
                    roundtripSpan?.Note ?? "",
                }
                .Where(x => !string.IsNullOrWhiteSpace(x)));

            rows.Add(new WorldStateEntrySizeCompareRow(
                i,
                name,
                typeName,
                TryGetCount(originalEntry.Value),
                originalSize,
                roundtripSize,
                diffBytes,
                diffPercent,
                canDeserialize,
                canRoundtrip,
                originalSpan?.Offset,
                roundtripSpan?.Offset,
                status,
                note));
        }

        return rows
            .OrderByDescending(r => Math.Abs(r.DiffBytes ?? 0))
            .ThenBy(r => r.Index)
            .ToList();
    }

    private static string BuildEntrySizeStatus(long? originalSize, long? roundtripSize, long? diffBytes, bool canDeserialize, bool canRoundtrip, bool originalRawOk, bool roundtripRawOk)
    {
        if (!originalRawOk || !roundtripRawOk) return "PARSER_FAILED";
        if (!canDeserialize) return "ORIGINAL_ENTRY_MISSING";
        if (!canRoundtrip) return "ROUNDTRIP_ENTRY_MISSING";
        if (!originalSize.HasValue || !roundtripSize.HasValue) return "SIZE_MISSING";
        if ((diffBytes ?? 0) == 0) return "OK";
        return "DIFF";
    }

    private static void WriteWorldStateEntrySizeCompareReport(string outputDir, IReadOnlyList<WorldStateEntrySizeCompareRow> rows)
    {
        WriteWorldStateEntrySizeCompareReport(outputDir, rows, "world_state_entry_size_compare");
    }

    private static void WriteWorldStateEntrySizeCompareReport(string outputDir, IReadOnlyList<WorldStateEntrySizeCompareRow> rows, string fileStem)
    {
        Directory.CreateDirectory(outputDir);
        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(Path.Combine(outputDir, fileStem + ".json"), JsonSerializer.Serialize(rows, jsonOptions), Encoding.UTF8);

        var csvRows = new List<string>
        {
            "Index,EntryName,TypeName,Count,OriginalBytes,RoundtripBytes,DiffBytes,DiffPercent,CanDeserialize,CanRoundtrip,OriginalOffset,RoundtripOffset,Status,Note"
        };

        foreach (var row in rows)
        {
            csvRows.Add(string.Join(',',
                CsvEscape(row.Index.ToString(CultureInfo.InvariantCulture)),
                CsvEscape(row.EntryName),
                CsvEscape(row.TypeName),
                CsvEscape(row.Count?.ToString(CultureInfo.InvariantCulture) ?? ""),
                CsvEscape(row.OriginalBytes?.ToString(CultureInfo.InvariantCulture) ?? ""),
                CsvEscape(row.RoundtripBytes?.ToString(CultureInfo.InvariantCulture) ?? ""),
                CsvEscape(row.DiffBytes?.ToString(CultureInfo.InvariantCulture) ?? ""),
                CsvEscape(row.DiffPercent.HasValue ? row.DiffPercent.Value.ToString("P2", CultureInfo.InvariantCulture) : ""),
                CsvEscape(row.CanDeserialize ? "true" : "false"),
                CsvEscape(row.CanRoundtrip ? "true" : "false"),
                CsvEscape(row.OriginalOffset?.ToString(CultureInfo.InvariantCulture) ?? ""),
                CsvEscape(row.RoundtripOffset?.ToString(CultureInfo.InvariantCulture) ?? ""),
                CsvEscape(row.Status),
                CsvEscape(row.Note)));
        }

        File.WriteAllLines(Path.Combine(outputDir, fileStem + ".csv"), csvRows, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static void WriteWorldStateEntrySizeCompareErrorReport(string outputDir, string message)
    {
        Directory.CreateDirectory(outputDir);
        File.WriteAllText(Path.Combine(outputDir, "world_state_entry_size_compare.json"), JsonSerializer.Serialize(new { Error = message }, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        File.WriteAllLines(Path.Combine(outputDir, "world_state_entry_size_compare.csv"), new[]
        {
            "Index,EntryName,TypeName,Count,OriginalBytes,RoundtripBytes,DiffBytes,DiffPercent,CanDeserialize,CanRoundtrip,OriginalOffset,RoundtripOffset,Status,Note",
            string.Join(',', CsvEscape(""), CsvEscape(""), CsvEscape(""), CsvEscape(""), CsvEscape(""), CsvEscape(""), CsvEscape(""), CsvEscape(""), CsvEscape("false"), CsvEscape("false"), CsvEscape(""), CsvEscape(""), CsvEscape("FAILED"), CsvEscape(message))
        }, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static bool TryExtractTopLevelStateEntrySpans(byte[] fileBytes, out List<EntryRawSpan> spans, out string note)
    {
        try
        {
            var payload = GetPossiblyGzippedPayloadBytes(fileBytes);
            return TryExtractTopLevelStateEntrySpansFromPayload(payload, out spans, out note);
        }
        catch (Exception ex)
        {
            note = ex.GetType().Name + " | " + ex.Message;
            spans = new List<EntryRawSpan>();
            return false;
        }
    }

    private static bool TryExtractTopLevelStateEntrySpansFromPayload(byte[] payload, out List<EntryRawSpan> spans, out string note)
    {
        spans = new List<EntryRawSpan>();
        note = "";
        try
        {
            using var memory = new MemoryStream(payload, writable: false);
            using var reader = new BinaryReader(memory, Encoding.UTF8, leaveOpen: true);

            var header = reader.ReadUInt32();
            if (header == 23225699u)
            {
                SkipSerializedStringMap(reader);
            }
            else if (header != 6448483u)
            {
                note = "Unexpected Candide header: " + header.ToString(CultureInfo.InvariantCulture);
                return false;
            }

            if (memory.Position >= memory.Length)
            {
                note = "Stream ended before root type code.";
                return false;
            }

            var rootTypeCode = reader.ReadByte();
            if (rootTypeCode != 49)
            {
                note = "Unexpected root type code: " + rootTypeCode.ToString(CultureInfo.InvariantCulture) + ". Expected array/list type code 49.";
                return false;
            }

            var count = Read7BitEncodedIntStrict(reader);
            if (count == 0)
            {
                note = "Root state list is empty.";
                return true;
            }

            var elementTypeCode = reader.ReadByte();
            if (elementTypeCode != 16 && elementTypeCode != 32)
            {
                note = "Unexpected state entry element type code: " + elementTypeCode.ToString(CultureInfo.InvariantCulture) + ". Expected object type code 16 or 32.";
                return false;
            }

            for (var i = 0; i < count; i++)
            {
                var start = memory.Position;
                var spanNote = SkipSerializedObjectPayload(reader);
                var end = memory.Position;
                spans.Add(new EntryRawSpan(i, start, end - start, spanNote));
            }

            if (memory.Position != memory.Length)
            {
                note = $"Parsed {spans.Count} entries, {memory.Length - memory.Position} trailing bytes left.";
            }
            return true;
        }
        catch (Exception ex)
        {
            note = ex.GetType().Name + " | " + ex.Message;
            spans.Clear();
            return false;
        }
    }

    private static void SkipSerializedStringMap(BinaryReader reader)
    {
        var count = Read7BitEncodedIntStrict(reader);
        if (count == 0) return;

        var elementTypeCode = reader.ReadByte();
        for (var i = 0; i < count; i++)
        {
            switch (elementTypeCode)
            {
                case 33:
                    _ = reader.ReadString();
                    break;
                case 34:
                    _ = Read7BitEncodedIntStrict(reader);
                    break;
                default:
                    throw new InvalidDataException("Unsupported string-map element type code: " + elementTypeCode.ToString(CultureInfo.InvariantCulture));
            }
        }
    }

    private static string SkipSerializedObjectPayload(BinaryReader reader)
    {
        var fieldCount = Read7BitEncodedIntStrict(reader);
        if (fieldCount == 0) return "fieldCount=0";
        var objectBytes = Read7BitEncodedIntStrict(reader);
        var start = reader.BaseStream.Position;
        var end = start + objectBytes;
        if (end > reader.BaseStream.Length)
        {
            throw new EndOfStreamException($"Object payload length {objectBytes} exceeds remaining stream length {reader.BaseStream.Length - start}.");
        }
        reader.BaseStream.Position = end;
        return "";
    }

    private static int Read7BitEncodedIntStrict(BinaryReader reader)
    {
        var result = 0;
        var shift = 0;
        for (var i = 0; i < 5; i++)
        {
            var b = reader.ReadByte();
            result |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
        }
        throw new FormatException("Invalid 7-bit encoded Int32.");
    }

    private static void CompareMetric(List<string> errors, string name, int? before, int? after, bool mustEqual)
    {
        if (!mustEqual) return;
        if (before != after) errors.Add($"{name} 数量不一致：原始 {before?.ToString(CultureInfo.InvariantCulture) ?? "null"}，回写 {after?.ToString(CultureInfo.InvariantCulture) ?? "null"}。");
    }

    private static void WriteWorldRoundtripReport(string outputDir, WorldRoundtripResult result)
    {
        Directory.CreateDirectory(outputDir);
        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(Path.Combine(outputDir, "world_roundtrip_report.json"), JsonSerializer.Serialize(result, jsonOptions), Encoding.UTF8);

        var rows = new List<string>
        {
            "Metric,Original,Roundtrip,Status"
        };
        AddRoundtripCsvRow(rows, "Passed", result.Passed ? "true" : "false", result.Passed ? "true" : "false", result.Passed ? "OK" : "FAILED");
        if (result.Before != null || result.After != null)
        {
            AddRoundtripCsvRow(rows, "CompressedBytes", result.Before?.CompressedBytes, result.After?.CompressedBytes);
            AddRoundtripCsvRow(rows, "DecompressedBytes", result.Before?.DecompressedBytes, result.After?.DecompressedBytes);
            AddRoundtripCsvRow(rows, "WasGzip", result.Before?.WasGzip, result.After?.WasGzip);
            AddRoundtripCsvRow(rows, "StateEntries", result.Before?.StateEntries, result.After?.StateEntries);
            AddRoundtripCsvRow(rows, "ItemInstances", result.Before?.ItemInstances, result.After?.ItemInstances);
            AddRoundtripCsvRow(rows, "Inventories", result.Before?.Inventories, result.After?.Inventories);
            AddRoundtripCsvRow(rows, "Citizens", result.Before?.Citizens, result.After?.Citizens);
            AddRoundtripCsvRow(rows, "WorldItems", result.Before?.WorldItems, result.After?.WorldItems);
            AddRoundtripCsvRow(rows, "Note", result.Before?.Note, result.After?.Note);
        }
        foreach (var error in result.Errors)
        {
            AddRoundtripCsvRow(rows, "Error", "", error, "FAILED");
        }
        File.WriteAllLines(Path.Combine(outputDir, "world_roundtrip_report.csv"), rows, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static void AddRoundtripCsvRow(List<string> rows, string metric, object? original, object? roundtrip, string? status = null)
    {
        var o = original?.ToString() ?? "";
        var r = roundtrip?.ToString() ?? "";
        var s = status ?? (string.Equals(o, r, StringComparison.Ordinal) ? "OK" : "DIFF");
        rows.Add(string.Join(',', CsvEscape(metric), CsvEscape(o), CsvEscape(r), CsvEscape(s)));
    }

    public static SaveResult SavePlayerItemStacks(string libDir, string inputDir, string backupDir, string outputDir, IReadOnlyList<PlayerItemStackUpdate> updates)
    {
        return SavePlayerChanges(libDir, inputDir, backupDir, outputDir, updates, Array.Empty<PlayerMoneyUpdate>(), Array.Empty<PlayerSkillUpdate>());
    }

    public static SaveResult SavePlayerChanges(string libDir, string inputDir, string backupDir, string outputDir, IReadOnlyList<PlayerItemStackUpdate> updates, IReadOnlyList<PlayerMoneyUpdate> moneyUpdates)
    {
        return SavePlayerChanges(libDir, inputDir, backupDir, outputDir, updates, moneyUpdates, Array.Empty<PlayerSkillUpdate>());
    }

    public static SaveResult SavePlayerChanges(string libDir, string inputDir, string backupDir, string outputDir, IReadOnlyList<PlayerItemStackUpdate> updates, IReadOnlyList<PlayerMoneyUpdate> moneyUpdates, IReadOnlyList<PlayerSkillUpdate> skillUpdates)
    {
        ArgumentNullException.ThrowIfNull(updates);
        ArgumentNullException.ThrowIfNull(moneyUpdates);
        ArgumentNullException.ThrowIfNull(skillUpdates);
        if (updates.Count == 0 && moneyUpdates.Count == 0 && skillUpdates.Count == 0) return new SaveResult(0, 0, "", "", 0, "没有可保存的变化。");

        LoadGameAssemblies(libDir);
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(backupDir);
        Directory.CreateDirectory(outputDir);

        var itemGroups = updates
            .Where(u => !string.IsNullOrWhiteSpace(u.SourceFile) && !string.IsNullOrWhiteSpace(u.Section))
            .GroupBy(u => u.SourceFile, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var moneyGroups = moneyUpdates
            .Where(u => !string.IsNullOrWhiteSpace(u.SourceFile))
            .GroupBy(u => u.SourceFile, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var skillGroups = skillUpdates
            .Where(u => !string.IsNullOrWhiteSpace(u.SourceFile) && !string.IsNullOrWhiteSpace(u.SkillId))
            .GroupBy(u => u.SourceFile, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var sourceFiles = itemGroups.Keys
            .Union(moneyGroups.Keys, StringComparer.OrdinalIgnoreCase)
            .Union(skillGroups.Keys, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (sourceFiles.Count == 0) return new SaveResult(0, 0, "", "", 0, "没有找到可保存的玩家文件。");

        var backupInfo = BackupPlayerDataFiles(inputDir, backupDir);
        var savedFiles = 0;
        var changedItems = 0;

        foreach (var sourceFile in sourceFiles)
        {
            var playerFile = ResolveInputFile(inputDir, sourceFile);
            if (playerFile == null)
            {
                AppLogger.Error($"保存跳过：找不到玩家文件 {sourceFile}");
                continue;
            }

            var bytes = File.ReadAllBytes(playerFile);
            if (!TryDeserializePlayerSaveWithFormat(bytes, out var playerSave, out var wasGzip, out var note) || playerSave == null)
            {
                AppLogger.Error($"保存跳过：无法反序列化玩家文件 {playerFile}. {note}");
                continue;
            }

            var changedThisFile = 0;

            if (moneyGroups.TryGetValue(sourceFile, out var moneyList) && moneyList.Count > 0)
            {
                var moneyUpdate = moneyList[^1];
                var oldMoney = ToUlong(ReadMember(playerSave, "Money"));
                if (oldMoney != moneyUpdate.Money)
                {
                    SetMember(playerSave, "Money", moneyUpdate.Money);
                    changedThisFile++;
                    changedItems++;
                    AppLogger.Info($"修改金钱：{Path.GetFileName(playerFile)} {oldMoney} -> {moneyUpdate.Money}");
                }
            }

            if (skillGroups.TryGetValue(sourceFile, out var skillList) && skillList.Count > 0)
            {
                var character = ReadMember(playerSave, "CharacterModel");
                foreach (var skillUpdate in skillList)
                {
                    var skillId = (skillUpdate.SkillId ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(skillId)) continue;
                    if (!KnownSkillIds.Contains(skillId))
                    {
                        AppLogger.Error($"保存跳过技能：未知技能 ID={skillId}。");
                        continue;
                    }
                    if (skillUpdate.Level < 0 || skillUpdate.Level > MaxSkillLevel)
                    {
                        AppLogger.Error($"保存跳过技能：{skillId} 等级必须在 0-{MaxSkillLevel} 之间。");
                        continue;
                    }
                    if (skillUpdate.CurrentExperience < 0)
                    {
                        AppLogger.Error($"保存跳过技能：{skillId} 经验不能小于 0。");
                        continue;
                    }

                    var skill = GetOrCreateCharacterSkill(character, skillId);
                    if (skill == null)
                    {
                        AppLogger.Error($"保存跳过技能：无法创建技能实例 {skillId}。");
                        continue;
                    }

                    var oldLevel = ToInt(ReadMember(skill, "Level"));
                    var oldExperience = ToFloat(ReadMember(skill, "CurrentExperience"));
                    if (oldLevel != skillUpdate.Level || Math.Abs(oldExperience - skillUpdate.CurrentExperience) > 0.0001f)
                    {
                        SetMember(skill, "Level", skillUpdate.Level);
                        SetMember(skill, "CurrentExperience", skillUpdate.CurrentExperience);
                        changedThisFile++;
                        changedItems++;
                        AppLogger.Info($"修改技能：{Path.GetFileName(playerFile)} {skillId} Level {oldLevel}->{skillUpdate.Level}, Exp {oldExperience.ToString(CultureInfo.InvariantCulture)}->{skillUpdate.CurrentExperience.ToString(CultureInfo.InvariantCulture)}");
                    }
                }
            }

            var itemUpdates = itemGroups.TryGetValue(sourceFile, out var itemList) ? itemList : new List<PlayerItemStackUpdate>();
            foreach (var update in itemUpdates)
            {
                var newBaseId = (update.NewBaseDataId ?? "").Trim();
                var originalBaseId = (update.OriginalBaseDataId ?? "").Trim();

                if (!string.IsNullOrWhiteSpace(newBaseId))
                {
                    var dbItem = RomesteadItemDatabase.Find(newBaseId);
                    if (dbItem == null)
                    {
                        AppLogger.Error($"保存跳过物品：未知物品 ID={newBaseId}。物品类型只能使用数据库中的英文 ID。");
                        continue;
                    }

                    if (update.StackCount <= 0)
                    {
                        AppLogger.Error($"保存跳过物品：{newBaseId} 数量必须大于 0。");
                        continue;
                    }

                    if (IsEquipmentSection(update.Section))
                    {
                        if (!IsEquipmentSlotItemId(newBaseId))
                        {
                            AppLogger.Error($"保存跳过物品：{update.Section}:{update.SlotIndex} 是装备槽，只允许 weapon/armor/trinket/axe/pickaxe/back/torch，不能放入 {newBaseId}。");
                            continue;
                        }
                        if (update.StackCount != 1)
                        {
                            AppLogger.Error($"保存跳过物品：{update.Section}:{update.SlotIndex} 装备槽数量必须为 1，当前 {newBaseId} x{update.StackCount}。");
                            continue;
                        }
                    }

                    if (update.StackCount > dbItem.EditorMaxStackSize)
                    {
                        AppLogger.Error($"保存跳过物品：{newBaseId} 数量 {update.StackCount} 超过上限 {dbItem.EditorMaxStackSize}。");
                        continue;
                    }

                    var auraIdsForValidation = NormalizeItemAuraList(update.AuraIds);
                    if (auraIdsForValidation.Count > 0 && !IsAuraEditableItemId(newBaseId))
                    {
                        AppLogger.Error($"保存跳过物品：{newBaseId} 不是支持 Aura 的装备类型。");
                        continue;
                    }
                    var hasInvalidAura = false;
                    foreach (var auraId in auraIdsForValidation)
                    {
                        if (!IsKnownItemAura(auraId))
                        {
                            AppLogger.Error($"保存跳过物品：未知装备 Aura ID={auraId}。");
                            hasInvalidAura = true;
                        }
                    }
                    if (hasInvalidAura) continue;
                }
                else if (update.StackCount != 0)
                {
                    AppLogger.Error($"保存跳过物品：空物品 ID 的数量必须为 0。Section={update.Section} Slot={update.SlotIndex}");
                    continue;
                }

                if (!TryGetPlayerSlot(playerSave, update.Section, update.SlotIndex, out var sectionObject, out var currentItem, out var slotError))
                {
                    AppLogger.Error($"保存跳过物品：{Path.GetFileName(playerFile)} {slotError}");
                    continue;
                }

                var currentId = currentItem == null ? "" : (ReadMember(currentItem, "BaseDataId")?.ToString() ?? "");
                var currentStack = currentItem == null ? 0 : ToInt(ReadMember(currentItem, "StackCount"));
                var currentInstanceId = currentItem == null ? "" : (ReadMember(currentItem, "Id")?.ToString() ?? "");
                var currentAuraIds = currentItem == null ? "" : string.Join(";", NormalizeItemAuraList(string.Join(";", ReadItemAuraIds(currentItem))));
                var requestedAuraIds = string.Join(";", NormalizeItemAuraList(update.AuraIds));

                if (!string.Equals(currentAuraIds, requestedAuraIds, StringComparison.OrdinalIgnoreCase))
                {
                    AppLogger.Info($"Aura 变更检测：{Path.GetFileName(playerFile)} [{update.Section}:{update.SlotIndex}] {newBaseId} Aura '{currentAuraIds}' -> '{requestedAuraIds}'");
                }

                if (!string.IsNullOrWhiteSpace(update.InstanceId) && !string.IsNullOrWhiteSpace(currentInstanceId) &&
                    !currentInstanceId.Equals(update.InstanceId, StringComparison.OrdinalIgnoreCase))
                {
                    AppLogger.Info($"保存提示：槽位物品 InstanceId 已变化，按槽位写入。文件={Path.GetFileName(playerFile)} Section={update.Section} Slot={update.SlotIndex}");
                }

                if (string.IsNullOrWhiteSpace(newBaseId))
                {
                    if (currentItem == null) continue;
                    SetPlayerSlot(sectionObject!, update.SlotIndex, null);
                    changedThisFile++;
                    changedItems++;
                    AppLogger.Info($"清空槽位：{Path.GetFileName(playerFile)} [{update.Section}:{update.SlotIndex}] {currentId} -> empty");
                    continue;
                }

                var needsReplace = currentItem == null || !currentId.Equals(newBaseId, StringComparison.OrdinalIgnoreCase);
                if (needsReplace)
                {
                    var newItem = CreateItemInstance(newBaseId, update.StackCount);
                    ApplyItemAurasToItem(newItem, requestedAuraIds);
                    SetPlayerSlot(sectionObject!, update.SlotIndex, newItem);
                    changedThisFile++;
                    changedItems++;
                    AppLogger.Info($"设置物品：{Path.GetFileName(playerFile)} [{update.Section}:{update.SlotIndex}] {currentId} x{currentStack} -> {newBaseId} x{update.StackCount}");
                    continue;
                }

                if (currentStack != update.StackCount)
                {
                    SetMember(currentItem, "StackCount", update.StackCount);
                    InvokeIfExists(currentItem, "CalculateStats");
                    changedThisFile++;
                    changedItems++;
                    AppLogger.Info($"修改数量：{Path.GetFileName(playerFile)} [{update.Section}:{update.SlotIndex}] {newBaseId} {currentStack} -> {update.StackCount}");
                }

                // Aura-only edits do not change the item ID or stack count, so they must be
                // applied explicitly here. Earlier versions only applied Auras when replacing
                // the whole item instance, which made the UI appear changed while saving 0 changes.
                if (!string.Equals(currentAuraIds, requestedAuraIds, StringComparison.OrdinalIgnoreCase))
                {
                    ApplyItemAurasToItem(currentItem, requestedAuraIds);
                    changedThisFile++;
                    changedItems++;
                    AppLogger.Info($"修改装备 Aura：{Path.GetFileName(playerFile)} [{update.Section}:{update.SlotIndex}] {newBaseId} '{currentAuraIds}' -> '{requestedAuraIds}'");
                }
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

        return new SaveResult(savedFiles, changedItems, backupInfo.BackupDirectory, outputDir, backupInfo.BackupIndex, $"保存完成：{savedFiles} 个文件，{changedItems} 项变化。备份批次 n={backupInfo.BackupIndex}。修改后世界文件已复制到 output/world。");
    }



    public static SaveResult SaveCitizenChanges(string libDir, string inputDir, string backupDir, string outputDir, IReadOnlyList<CitizenUpdate> citizenUpdates, bool unsafeCitizenStringWorldWrite = false, bool debugMode = false, bool outputCsv = false)
    {
        ArgumentNullException.ThrowIfNull(citizenUpdates);
        if (citizenUpdates.Count == 0) return new SaveResult(0, 0, "", "", 0, "没有可保存的村民变化。");

        LoadGameAssemblies(libDir);
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(backupDir);
        Directory.CreateDirectory(outputDir);

        var groups = citizenUpdates
            .Where(u => !string.IsNullOrWhiteSpace(u.SourceFile))
            .GroupBy(u => u.SourceFile, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        if (groups.Count == 0) return new SaveResult(0, 0, "", "", 0, "没有找到可保存的 game_state 文件。");

        var backupInfo = BackupInputDataFiles(inputDir, backupDir);
        var savedFiles = 0;
        var changedItems = 0;

        foreach (var (sourceFile, updates) in groups)
        {
            var gameStateFile = ResolveInputFile(inputDir, sourceFile);
            if (gameStateFile == null)
            {
                AppLogger.Error($"保存跳过：找不到 game_state 文件 {sourceFile}");
                continue;
            }

            var bytes = File.ReadAllBytes(gameStateFile);
            if (!TryDeserializeGameStateWithFormat(bytes, out var state, out var wasGzip, out var note) || state == null)
            {
                AppLogger.Error($"保存跳过：无法反序列化 game_state {gameStateFile}. {note}");
                continue;
            }

            var beforeInspection = InspectGameState(gameStateFile, state, new Options { Limit = 2000 });
            var expectedWorldDescGameId = TryGetSingleWorldDescGameIdFromInput(inputDir);
            if (expectedWorldDescGameId.HasValue)
            {
                var currentGameId = beforeInspection.GameId;
                if (!Guid.TryParse(currentGameId, out var currentGuid) || currentGuid == Guid.Empty)
                {
                    if (TrySetTopLevelGameStateGameId(state, expectedWorldDescGameId.Value))
                    {
                        AppLogger.Warn($"game_state.GameId 为空/全 0，已按 world_desc.GameId 修复：{expectedWorldDescGameId.Value:D}");
                        beforeInspection.GameId = expectedWorldDescGameId.Value.ToString("D");
                    }
                    else
                    {
                        throw new InvalidOperationException("world_desc/game_state 绑定失败：game_state.GameId 为空/全 0，但无法修复 GameId StateEntry。");
                    }
                }
                else if (currentGuid != expectedWorldDescGameId.Value)
                {
                    throw new InvalidOperationException($"world_desc/game_state 绑定失败：game_state.GameId={currentGuid:D} 与 world_desc.GameId={expectedWorldDescGameId.Value:D} 不一致。请确认 input 中的 game_state/world_desc 来自同一个世界。");
                }
            }

            var changedThisFile = 0;
            var traitsChangedThisFile = false;
            var traitDiffRows = new List<CitizenTraitDeepDiffRow>();
            foreach (var update in updates)
            {
                if (update.CurrentJobLevel < 1 || update.CurrentJobLevel > 10)
                {
                    AppLogger.Error($"保存跳过村民：CurrentJobLevel 必须为 1-10。CitizenId={update.CitizenId} Requested={update.CurrentJobLevel}");
                    continue;
                }

                if (update.LoyaltyLevel < 0 || update.LoyaltyLevel > 4)
                {
                    AppLogger.Error($"保存跳过村民：LoyaltyLevel 必须为 0-4。CitizenId={update.CitizenId} Requested={update.LoyaltyLevel}");
                    continue;
                }

                var citizen = FindCitizenObjectForUpdate(state, update);
                if (citizen == null)
                {
                    AppLogger.Error($"保存跳过村民：找不到 CitizenId={update.CitizenId} EntityId={update.EntityId} Kind={update.Kind}");
                    continue;
                }

                var changedCitizen = 0;
                var currentJob = ReadMember(citizen, "CurrentJob")?.ToString() ?? "";
                if (!string.IsNullOrWhiteSpace(currentJob))
                {
                    var jobProgress = FindCitizenJobProgress(citizen, currentJob);
                    if (jobProgress != null)
                    {
                        var oldLevel = ToInt(ReadMember(jobProgress, "Level"));
                        var oldExp = ToFloat(ReadMember(jobProgress, "Experience"));
                        if (oldLevel != update.CurrentJobLevel)
                        {
                            SetMember(jobProgress, "Level", update.CurrentJobLevel);
                            changedCitizen++;
                        }
                        if (Math.Abs(oldExp - update.CurrentJobExperience) > 0.0001f)
                        {
                            SetMember(jobProgress, "Experience", update.CurrentJobExperience);
                            changedCitizen++;
                        }
                    }
                    else
                    {
                        AppLogger.Info($"村民当前工作没有 JobExperience 记录，跳过工作等级/经验：CitizenId={update.CitizenId} CurrentJob={currentJob}");
                    }
                }

                if (SetCitizenBaseStat(citizen, "Citizen_Efficiency", update.Efficiency)) changedCitizen++;
                if (SetCitizenBaseStat(citizen, "Citizen_Expertise", update.Expertise)) changedCitizen++;

                var oldLoyalty = ToFloat(ReadMember(citizen, "Loyalty"));
                var oldLoyaltyLevel = ToInt(ReadMember(citizen, "LoyaltyLevel"));
                if (Math.Abs(oldLoyalty - update.Loyalty) > 0.0001f)
                {
                    SetMember(citizen, "Loyalty", update.Loyalty);
                    changedCitizen++;
                }
                if (oldLoyaltyLevel != update.LoyaltyLevel)
                {
                    SetMember(citizen, "LoyaltyLevel", update.LoyaltyLevel);
                    changedCitizen++;
                }

                var beforeTraitIdsForDiff = string.Join(";", ReadCurrentCitizenTraitIds(citizen).Select(NormalizeCitizenAuraIdForSave));
                var beforeAuraObjectsForDiff = DescribeCitizenAuraObjectsForReport(citizen);
                if (ApplyCitizenTraits(citizen, update.TraitIds))
                {
                    var afterTraitIdsForDiff = string.Join(";", ReadCurrentCitizenTraitIds(citizen).Select(NormalizeCitizenAuraIdForSave));
                    var afterAuraObjectsForDiff = DescribeCitizenAuraObjectsForReport(citizen);
                    traitDiffRows.Add(new CitizenTraitDeepDiffRow(
                        Path.GetFileName(gameStateFile),
                        update.CitizenId,
                        update.EntityId,
                        ReadStringId(ReadMember(citizen, "Name")),
                        update.TraitIds,
                        beforeTraitIdsForDiff,
                        afterTraitIdsForDiff,
                        beforeAuraObjectsForDiff,
                        afterAuraObjectsForDiff));
                    changedCitizen++;
                    traitsChangedThisFile = true;
                }

                if (changedCitizen > 0)
                {
                    changedThisFile += changedCitizen;
                    changedItems += changedCitizen;
                    AppLogger.Info($"修改村民：{Path.GetFileName(gameStateFile)} CitizenId={update.CitizenId} EntityId={update.EntityId} changes={changedCitizen}");
                }
            }

            if (changedThisFile == 0)
            {
                AppLogger.Info($"{Path.GetFileName(gameStateFile)} 没有实际村民变化，未写回。");
                continue;
            }

            byte[] serializerBytes;
            byte[] modifiedBytes;
            string rawPreserveNote;
            string serializationModeNote;

            if (traitsChangedThisFile)
            {
                AppLogger.Warn("村民特质/buff/debuff 已变更：将尝试使用游戏原生 ServerGameState 静态上下文重写 game_state。此路线不使用 raw-preserve 拼接，必须通过完整 StateEntry 校验后才会覆盖 input/game_state。");
                try
                {
                    serializerBytes = SerializeGameStateToBytesUsingNativeServerState(state, bytes, wasGzip, out serializationModeNote);
                }
                catch (Exception ex)
                {
                    if (outputCsv) WriteWorldWriteNativeFailureReport(outputDir, gameStateFile, ex);
                    throw new InvalidOperationException("原生世界写入失败：无法用 GameSaveManager/ServerGameState 静态上下文序列化修改后的 game_state。" + ex.Message, ex);
                }

                if (!TryBuildRawPreservedWorldRoundtrip(state, bytes, serializerBytes, wasGzip, allowSameLengthEntryDeltaPatch: false, allowUnsafeHeaderBypass: true, out modifiedBytes, out var nativeHybridRawPreserveNote))
                {
                    throw new InvalidOperationException("原生世界写入失败：无法在原生序列化结果中保留大型世界数组块。" + nativeHybridRawPreserveNote);
                }

                rawPreserveNote = "native-full-server-state-writer + hybrid raw-preserve; " + serializationModeNote + " | " + nativeHybridRawPreserveNote;
            }
            else
            {
                try
                {
                    serializerBytes = SerializeGameStateToBytes(state, wasGzip);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException("安全世界写入失败：修改后的 game_state 无法序列化。" + ex.Message, ex);
                }

                // Numeric-only citizen changes may use the same-length Citizens delta fallback when the full serializer changes the header.
                var allowSameLengthEntryDeltaPatch = true;
                if (!TryBuildRawPreservedWorldRoundtrip(state, bytes, serializerBytes, wasGzip, allowSameLengthEntryDeltaPatch, allowUnsafeHeaderBypass: false, out modifiedBytes, out rawPreserveNote))
                {
                    throw new InvalidOperationException("安全世界写入失败：无法保留原始大型世界数组块，已拒绝覆盖 input/game_state。原因：" + rawPreserveNote);
                }
            }

            string unpatchedOutput = "";
            if (debugMode)
            {
                var debugDir = Path.Combine(outputDir, "debug");
                Directory.CreateDirectory(debugDir);
                unpatchedOutput = Path.Combine(debugDir, Path.GetFileName(gameStateFile) + ".unpatched_DO_NOT_USE");
                File.WriteAllBytes(unpatchedOutput, serializerBytes);
            }

            if (outputCsv && traitDiffRows.Count > 0)
            {
                WriteCitizenTraitDeepDiffReport(outputDir, traitDiffRows);
            }

            var validationErrors = ValidateModifiedGameStateBytes(gameStateFile, bytes, modifiedBytes, state, beforeInspection, outputDir, rawPreserveNote, outputCsv);
            if (validationErrors.Count > 0)
            {
                throw new InvalidOperationException("安全世界写入校验失败，已拒绝覆盖 input/game_state。" + Environment.NewLine + string.Join(Environment.NewLine, validationErrors.Select(e => "- " + e)));
            }

            var modifiedBytesForWrite = modifiedBytes;
            var temp = gameStateFile + ".tmp";
            File.WriteAllBytes(temp, modifiedBytesForWrite);
            File.Move(temp, gameStateFile, overwrite: true);

            var worldOutputDir = Path.Combine(outputDir, "world");
            Directory.CreateDirectory(worldOutputDir);
            var outputCopy = Path.Combine(worldOutputDir, Path.GetFileName(gameStateFile));
            File.WriteAllBytes(outputCopy, modifiedBytesForWrite);
            WriteWorldFolderOutput(inputDir, worldOutputDir, gameStateFile, modifiedBytesForWrite);

            var bindingReport = outputCsv ? WriteWorldDescBindingReportForInput(inputDir, outputDir, "after-save") : null;
            if (bindingReport != null && !bindingReport.Passed)
            {
                throw new InvalidOperationException("world_desc/game_state 绑定校验失败，已拒绝覆盖 input/game_state。" + Environment.NewLine + string.Join(Environment.NewLine, bindingReport.Errors.Select(e => "- " + e)));
            }

            savedFiles++;
            AppLogger.Info($"已安全保存 game_state：{gameStateFile} ({changedThisFile} 项村民变化，格式={note}, raw-preserve={rawPreserveNote})");
            AppLogger.Info($"已复制修改后 game_state 到 output/world：{outputCopy}");
            if (debugMode && !string.IsNullOrWhiteSpace(unpatchedOutput)) AppLogger.Info($"未保留数组块的危险输出仅用于诊断，禁止使用：{unpatchedOutput}");
        }

        return new SaveResult(savedFiles, changedItems, backupInfo.BackupDirectory, outputDir, backupInfo.BackupIndex, $"村民保存完成：{savedFiles} 个 game_state，{changedItems} 项变化。备份批次 n={backupInfo.BackupIndex}。修改后世界文件已复制到 output/world。");
    }



    private static void WriteCitizenTraitDeepDiffReport(string outputDir, IReadOnlyList<CitizenTraitDeepDiffRow> rows)
    {
        try
        {
            Directory.CreateDirectory(outputDir);
            var report = new
            {
                Passed = true,
                Stage = "citizen-trait-deep-diff",
                Count = rows.Count,
                Note = "Compares the target CitizenModel trait/aura fields before and after the editor changes them. This is diagnostic and does not prove the game will load the save.",
                Rows = rows
            };
            File.WriteAllText(Path.Combine(outputDir, "citizen_trait_deep_diff.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
            WriteCsv(Path.Combine(outputDir, "citizen_trait_deep_diff.csv"), rows,
                ["SourceFile", "CitizenId", "EntityId", "Name", "RequestedTraitIds", "BeforeTraitIds", "AfterTraitIds", "BeforeAuraObjects", "AfterAuraObjects"],
                r => [r.SourceFile, r.CitizenId, r.EntityId, r.Name, r.RequestedTraitIds, r.BeforeTraitIds, r.AfterTraitIds, r.BeforeAuraObjects, r.AfterAuraObjects]);
            AppLogger.Info("已输出村民特质深度 diff：citizen_trait_deep_diff.json/csv");
        }
        catch (Exception ex)
        {
            AppLogger.Warn("输出 citizen_trait_deep_diff 失败：" + ex.Message);
        }
    }

    private static string DescribeCitizenAuraObjectsForReport(object citizen)
    {
        var parts = new List<string>();
        var wildList = ReadMember(citizen, "CitizenAuras");
        var wild = EnumerateSequence(wildList).Select(v => v?.ToString() ?? "").Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        if (wild.Count > 0) parts.Add("CitizenAuras=" + string.Join(";", wild));

        var auras = ReadMember(citizen, "Auras");
        var index = 0;
        foreach (var aura in EnumerateSequence(auras))
        {
            if (aura == null) continue;
            var fields = new List<string>();
            foreach (var name in new[] { "Id", "DataId", "Type", "IsBuff", "Duration", "Timer", "InstanceTypeId", "FromTown" })
            {
                var value = ReadMember(aura, name);
                fields.Add(name + "=" + (value?.ToString() ?? "<null>"));
            }
            var stats = ReadMember(aura, "Stats") ?? ReadMember(aura, "StatsToAdd");
            if (stats != null) fields.Add("Stats=" + TrimForReport(SafeMemberSummary(stats), 300));
            parts.Add("Auras[" + index.ToString(CultureInfo.InvariantCulture) + "]{" + string.Join(",", fields) + "}");
            index++;
        }
        return string.Join(" || ", parts);
    }


    private static void WriteWorldFolderOutput(string inputDir, string worldOutputDir, string gameStateFile, byte[] gameStateBytes)
    {
        try
        {
            Directory.CreateDirectory(worldOutputDir);

            var worldDescFile = FindSingleWorldDescFile(inputDir);
            if (worldDescFile == null)
            {
                AppLogger.Warn("output/world 已输出 game_state，但 input 中没有 world_desc，无法输出匹配 world_desc。");
                return;
            }

            var worldDescBytes = File.ReadAllBytes(worldDescFile);
            if (!TryDeserializeWorldDescriptionWithFormat(worldDescBytes, out var worldDesc, out var worldDescWasGzip, out var worldDescNote) || worldDesc == null)
            {
                File.Copy(worldDescFile, Path.Combine(worldOutputDir, "world_desc"), overwrite: true);
                AppLogger.Warn("output/world 已复制原始 world_desc，但 world_desc 反序列化失败，无法同步字段：" + worldDescNote);
                return;
            }

            string gameStateGameId = "";
            double? gameStateTimePlayed = null;
            if (TryDeserializeGameStateWithFormat(gameStateBytes, out var pairedState, out _, out _) && pairedState != null)
            {
                var inspection = InspectGameState(gameStateFile, pairedState, new Options { Limit = 2000 });
                gameStateGameId = inspection.GameId;
                gameStateTimePlayed = inspection.TimePlayed;
            }

            if (Guid.TryParse(gameStateGameId, out var parsedGameId) && parsedGameId != Guid.Empty)
                TrySetMember(worldDesc, "GameId", parsedGameId);
            if (gameStateTimePlayed.HasValue)
                TrySetMember(worldDesc, "TimePlayed", gameStateTimePlayed.Value);
            TrySetMember(worldDesc, "LastPlayed", DateTime.Now);
            TrySetMember(worldDesc, "Broken", false);
            TrySetMember(worldDesc, "LoadedFromBackup", false);

            File.WriteAllBytes(Path.Combine(worldOutputDir, "world_desc"), SerializeWorldDescriptionToBytes(worldDesc, worldDescWasGzip));
            AppLogger.Info("已输出匹配 world_desc 到 output/world：" + Path.Combine(worldOutputDir, "world_desc"));
        }
        catch (Exception ex)
        {
            AppLogger.Warn("输出 output/world/world_desc 失败：" + ex.Message);
        }
    }

    private static void WriteWorldWriteLabOutput(string inputDir, string outputDir, string gameStateFile, byte[] originalGameStateBytes, byte[] modifiedGameStateBytes, string rawPreserveNote, bool traitChanged)
    {
        try
        {
            var labRoot = Path.Combine(outputDir, "world_write_lab");
            var originalDir = Path.Combine(labRoot, "00_original_copy_pair");
            var modifiedDir = Path.Combine(labRoot, "01_saved_pair");
            Directory.CreateDirectory(originalDir);
            Directory.CreateDirectory(modifiedDir);

            File.WriteAllBytes(Path.Combine(originalDir, "game_state"), originalGameStateBytes);
            File.WriteAllBytes(Path.Combine(modifiedDir, "game_state"), modifiedGameStateBytes);

            var worldDesc = FindSingleWorldDescFile(inputDir);
            if (worldDesc != null)
            {
                File.Copy(worldDesc, Path.Combine(originalDir, "world_desc"), overwrite: true);
                var safeWorldDesc = Path.Combine(outputDir, "safe_world_pair", "world_desc");
                File.Copy(File.Exists(safeWorldDesc) ? safeWorldDesc : worldDesc, Path.Combine(modifiedDir, "world_desc"), overwrite: true);
            }

            var readme = "World write lab output. Test 00_original_copy_pair first. If it fails, the problem is replacement path or file pairing, not the editor. Then test 01_saved_pair. If only 01 fails, compare citizen_trait_deep_diff.json and game loading behavior.\r\n";
            File.WriteAllText(Path.Combine(labRoot, "README_TEST_ORDER.txt"), readme, Encoding.UTF8);

            var rows = new List<WorldWriteLabReportRow>
            {
                new("00_original_copy_pair", "original-copy", "Copy of input game_state/world_desc before editor write. This should load if copied into the correct save folder."),
                new("01_saved_pair", traitChanged ? "saved-trait-pair" : "saved-numeric-pair", "The editor output pair. If 00 loads and this fails, the editor changed something the game loader rejects.")
            };
            WriteCsv(Path.Combine(outputDir, "world_write_lab_report.csv"), rows,
                ["Directory", "Kind", "Note"], r => [r.Directory, r.Kind, r.Note]);
            var report = new { Passed = true, Stage = "world-write-lab", LabDirectory = labRoot, RawPreserveNote = rawPreserveNote, Rows = rows };
            File.WriteAllText(Path.Combine(outputDir, "world_write_lab_report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
            AppLogger.Info("已输出 world_write_lab 对照组目录：" + labRoot);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("输出 world_write_lab 失败：" + ex.Message);
        }
    }

    private static void WriteSafeWorldPairOutput(string inputDir, string outputDir, string gameStateFile, byte[] gameStateBytes, string rawPreserveNote)
    {
        try
        {
            Directory.CreateDirectory(outputDir);
            var pairDir = Path.Combine(outputDir, "safe_world_pair");
            Directory.CreateDirectory(pairDir);

            var pairGameStatePath = Path.Combine(pairDir, "game_state");
            File.WriteAllBytes(pairGameStatePath, gameStateBytes);

            string gameStateGameId = "";
            double? gameStateTimePlayed = null;
            string gameStateNote = "";
            if (TryDeserializeGameStateWithFormat(gameStateBytes, out var pairedState, out _, out gameStateNote) && pairedState != null)
            {
                var inspection = InspectGameState(gameStateFile, pairedState, new Options { Limit = 2000 });
                gameStateGameId = inspection.GameId;
                gameStateTimePlayed = inspection.TimePlayed;
            }

            var worldDescFile = FindSingleWorldDescFile(inputDir);
            var reportRows = new List<SafeWorldPairReportRow>();
            if (worldDescFile == null)
            {
                reportRows.Add(new SafeWorldPairReportRow("ERROR", "world_desc", "missing", "No world_desc was found in input, so only safe_world_pair/game_state was written."));
                WriteSafeWorldPairReport(outputDir, pairDir, reportRows, rawPreserveNote);
                AppLogger.Warn("safe_world_pair 已输出 game_state，但 input 中没有 world_desc，无法输出匹配 world_desc。");
                return;
            }

            var worldDescBytes = File.ReadAllBytes(worldDescFile);
            if (!TryDeserializeWorldDescriptionWithFormat(worldDescBytes, out var worldDesc, out var worldDescWasGzip, out var worldDescNote) || worldDesc == null)
            {
                reportRows.Add(new SafeWorldPairReportRow("ERROR", "world_desc", "deserialize", worldDescNote));
                WriteSafeWorldPairReport(outputDir, pairDir, reportRows, rawPreserveNote);
                AppLogger.Warn("safe_world_pair 已输出 game_state，但 world_desc 反序列化失败，无法输出匹配 world_desc：" + worldDescNote);
                return;
            }

            var originalWorldDescGameId = NormalizeGuidString(ReadMember(worldDesc, "GameId"));
            var originalWorldDescTimePlayed = ToDoubleOrNull(ReadMember(worldDesc, "TimePlayed"));

            if (Guid.TryParse(gameStateGameId, out var parsedGameId) && parsedGameId != Guid.Empty)
            {
                if (TrySetMember(worldDesc, "GameId", parsedGameId))
                {
                    reportRows.Add(new SafeWorldPairReportRow("INFO", "GameId", "synced", $"{originalWorldDescGameId} -> {parsedGameId:D}"));
                }
                else
                {
                    reportRows.Add(new SafeWorldPairReportRow("WARN", "GameId", "not-writable", $"Could not set world_desc.GameId to {parsedGameId:D}."));
                }
            }
            else
            {
                reportRows.Add(new SafeWorldPairReportRow("WARN", "GameId", "unknown", "Could not read a non-empty GameId from the generated game_state."));
            }

            if (gameStateTimePlayed.HasValue)
            {
                if (TrySetMember(worldDesc, "TimePlayed", gameStateTimePlayed.Value))
                {
                    var oldTime = originalWorldDescTimePlayed.HasValue ? originalWorldDescTimePlayed.Value.ToString(CultureInfo.InvariantCulture) : "";
                    reportRows.Add(new SafeWorldPairReportRow("INFO", "TimePlayed", "synced", $"{oldTime} -> {gameStateTimePlayed.Value.ToString(CultureInfo.InvariantCulture)}"));
                }
                else
                {
                    reportRows.Add(new SafeWorldPairReportRow("WARN", "TimePlayed", "not-writable", "Could not set world_desc.TimePlayed."));
                }
            }

            if (TrySetMember(worldDesc, "LastPlayed", DateTime.Now))
            {
                reportRows.Add(new SafeWorldPairReportRow("INFO", "LastPlayed", "updated", DateTime.Now.ToString("O", CultureInfo.InvariantCulture)));
            }

            if (TrySetMember(worldDesc, "Broken", false))
            {
                reportRows.Add(new SafeWorldPairReportRow("INFO", "Broken", "cleared", "world_desc.Broken=false"));
            }

            if (TrySetMember(worldDesc, "LoadedFromBackup", false))
            {
                reportRows.Add(new SafeWorldPairReportRow("INFO", "LoadedFromBackup", "cleared", "world_desc.LoadedFromBackup=false"));
            }

            var pairWorldDescPath = Path.Combine(pairDir, "world_desc");
            var generatedWorldDescBytes = SerializeWorldDescriptionToBytes(worldDesc, worldDescWasGzip);
            File.WriteAllBytes(pairWorldDescPath, generatedWorldDescBytes);

            foreach (var charFile in Directory.EnumerateFiles(inputDir, "*.char", SearchOption.TopDirectoryOnly))
            {
                var dest = Path.Combine(pairDir, Path.GetFileName(charFile));
                File.Copy(charFile, dest, overwrite: true);
            }

            File.WriteAllText(Path.Combine(pairDir, "README_USE_PAIR.txt"),
                "Use this folder as a matched save-file pair. Copy game_state and world_desc together into the same Romestead save directory.\r\n" +
                "Do not test output/game_state alone when debugging world load errors.\r\n" +
                "Any .char files in this folder are copied from input for convenience; they were not force-rewritten.\r\n",
                Encoding.UTF8);

            reportRows.Add(new SafeWorldPairReportRow("INFO", "safe_world_pair", "written", pairDir));
            WriteSafeWorldPairReport(outputDir, pairDir, reportRows, rawPreserveNote);
            AppLogger.Info("已输出成对世界文件：" + pairDir + "。测试时请同时复制 game_state 和 world_desc。");
        }
        catch (Exception ex)
        {
            AppLogger.Warn("输出 safe_world_pair 失败：" + ex.Message);
            try
            {
                var pairDir = Path.Combine(outputDir, "safe_world_pair");
                WriteSafeWorldPairReport(outputDir, pairDir, [new SafeWorldPairReportRow("ERROR", "safe_world_pair", ex.GetType().Name, ex.Message)], rawPreserveNote);
            }
            catch
            {
                // Do not hide the original save success/failure if this optional pair output report fails.
            }
        }
    }

    private static byte[] SerializeWorldDescriptionToBytes(object worldDesc, bool gzipOutput)
    {
        using var serialized = new MemoryStream();
        if (gzipOutput)
        {
            using (var gzip = new GZipStream(serialized, CompressionLevel.Optimal, leaveOpen: true))
            {
                SerializeWithType(gzip, worldDesc.GetType(), worldDesc);
            }
        }
        else
        {
            SerializeWithType(serialized, worldDesc.GetType(), worldDesc);
        }
        return serialized.ToArray();
    }

    private static string? FindSingleWorldDescFile(string inputDir)
    {
        var files = Directory.EnumerateFiles(inputDir, "*", SearchOption.TopDirectoryOnly)
            .Where(f =>
            {
                var name = Path.GetFileName(f);
                return name.Equals("world_desc", StringComparison.OrdinalIgnoreCase) ||
                       name.StartsWith("world_desc.", StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (files.Count == 0) return null;
        if (files.Count > 1)
        {
            AppLogger.Warn("input 中存在多个 world_desc 候选，output/world 使用第一个：" + string.Join(", ", files.Select(Path.GetFileName)));
        }
        return files[0];
    }

    private static void WriteSafeWorldPairReport(string outputDir, string pairDir, List<SafeWorldPairReportRow> rows, string rawPreserveNote)
    {
        Directory.CreateDirectory(outputDir);
        var report = new
        {
            Passed = rows.All(r => !string.Equals(r.Level, "ERROR", StringComparison.OrdinalIgnoreCase)),
            PairDirectory = pairDir,
            Files = new
            {
                GameState = Path.Combine(pairDir, "game_state"),
                WorldDesc = Path.Combine(pairDir, "world_desc")
            },
            RawPreserveNote = rawPreserveNote,
            Checks = rows
        };
        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(Path.Combine(outputDir, "safe_world_pair_report.json"), JsonSerializer.Serialize(report, jsonOptions), Encoding.UTF8);
        WriteCsv(Path.Combine(outputDir, "safe_world_pair_report.csv"), rows,
            ["Level", "Check", "Status", "Note"],
            r => [r.Level, r.Check, r.Status, r.Note]);
    }

    public static SaveResult ValidateCitizenChanges(string libDir, string inputDir, string outputDir, IReadOnlyList<CitizenUpdate> citizenUpdates, bool unsafeCitizenStringWorldWrite = false, bool debugMode = false, bool outputCsv = false)
    {
        ArgumentNullException.ThrowIfNull(citizenUpdates);
        if (citizenUpdates.Count == 0) return new SaveResult(0, 0, "", "", 0, "没有可预验证的村民变化。");

        var validationRoot = Path.Combine(outputDir, "_world_write_preflight_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture));
        var tempInput = Path.Combine(validationRoot, "input");
        var tempBackup = Path.Combine(validationRoot, "backup");
        var tempOutput = Path.Combine(validationRoot, "output");
        Directory.CreateDirectory(tempInput);
        Directory.CreateDirectory(tempBackup);
        Directory.CreateDirectory(tempOutput);

        try
        {
            foreach (var file in Directory.GetFiles(inputDir))
            {
                var name = Path.GetFileName(file);
                File.Copy(file, Path.Combine(tempInput, name), overwrite: true);
            }

            AppLogger.Info("开始预验证世界写入：在临时目录中模拟保存，不覆盖 input。Temp=" + validationRoot);
            try
            {
                var result = SaveCitizenChanges(libDir, tempInput, tempBackup, tempOutput, citizenUpdates, unsafeCitizenStringWorldWrite, debugMode: false, outputCsv: outputCsv);
                if (outputCsv) CopyWorldWriteReportsToOutputRoot(tempOutput, outputDir, validationRoot, success: true);
                AppLogger.Info("世界写入预验证通过：" + result.Message);
                return result;
            }
            catch
            {
                // Preflight writes diagnostics into the temporary output folder. Copy them back
                // before the temp directory is removed so users can always find world_write*.json/csv
                // under the normal output folder.
                if (outputCsv) CopyWorldWriteReportsToOutputRoot(tempOutput, outputDir, validationRoot, success: false);
                throw;
            }
        }
        finally
        {
            try { Directory.Delete(validationRoot, recursive: true); }
            catch (Exception ex) { AppLogger.Warn("清理世界写入预验证临时目录失败：" + ex.Message); }
        }
    }

    private static List<string> ValidateModifiedGameStateBytes(string gameStateFile, byte[] originalBytes, byte[] modifiedBytes, object stateForEntryNames, GameStateInspectionResult beforeInspection, string outputDir, string rawPreserveNote, bool outputCsv = false)
    {
        var errors = new List<string>();
        Directory.CreateDirectory(outputDir);

        var originalDecompressedBytes = GetPossiblyGzippedPayloadLength(originalBytes);
        var modifiedDecompressedBytes = GetPossiblyGzippedPayloadLength(modifiedBytes);
        if (originalDecompressedBytes <= 0 || modifiedDecompressedBytes <= 0)
        {
            errors.Add($"无法计算解压大小。original={originalDecompressedBytes}, modified={modifiedDecompressedBytes}");
        }
        else
        {
            var delta = Math.Abs(modifiedDecompressedBytes - originalDecompressedBytes);
            var ratio = originalDecompressedBytes <= 0 ? 1 : (double)delta / originalDecompressedBytes;
            if (ratio > 0.05)
                errors.Add($"解压后大小差异过大：原始 {originalDecompressedBytes:N0} bytes，修改后 {modifiedDecompressedBytes:N0} bytes，差异 {ratio:P2}。");
        }

        GameStateInspectionResult? afterInspection = null;
        object? modifiedStateForEntryCompare = null;
        string modifiedNote = "";
        if (!TryDeserializeGameStateWithFormat(modifiedBytes, out var modifiedState, out var modifiedWasGzip, out modifiedNote) || modifiedState == null)
        {
            errors.Add("修改后的 game_state 无法反序列化：" + modifiedNote);
        }
        else
        {
            modifiedStateForEntryCompare = modifiedState;
            afterInspection = InspectGameState(gameStateFile + ".modified", modifiedState, new Options { Limit = 2000 });
            if (beforeInspection.TotalStateEntries != afterInspection.TotalStateEntries)
                errors.Add($"StateEntries 数量不一致：原始 {beforeInspection.TotalStateEntries}，修改后 {afterInspection.TotalStateEntries}。");
            if (beforeInspection.TotalItemInstances != afterInspection.TotalItemInstances)
                errors.Add($"ItemInstances 数量不一致：原始 {beforeInspection.TotalItemInstances}，修改后 {afterInspection.TotalItemInstances}。");
            if (beforeInspection.TotalInventories != afterInspection.TotalInventories)
                errors.Add($"Inventories 数量不一致：原始 {beforeInspection.TotalInventories}，修改后 {afterInspection.TotalInventories}。");
            if (beforeInspection.TotalCitizens != afterInspection.TotalCitizens)
                errors.Add($"Citizens 数量不一致：原始 {beforeInspection.TotalCitizens}，修改后 {afterInspection.TotalCitizens}。");
            if (beforeInspection.WorldItemCount != afterInspection.WorldItemCount)
                errors.Add($"WorldItems 数量不一致：原始 {beforeInspection.WorldItemCount}，修改后 {afterInspection.WorldItemCount}。");
        }

        try
        {
            var rows = BuildWorldStateEntrySizeCompare(stateForEntryNames, modifiedStateForEntryCompare, originalBytes, modifiedBytes);
            if (outputCsv) WriteWorldStateEntrySizeCompareReport(outputDir, rows, "world_write_entry_size_compare");

            var largeWorldArrayLikeRows = rows
                .Where(r => (r.OriginalBytes ?? 0) >= 5_000_000)
                .OrderBy(r => r.Index)
                .ToList();
            var preservedLargeWorldArrayLikeRows = largeWorldArrayLikeRows
                .Where(r => r.Status == "OK" && (r.DiffBytes ?? 0) == 0)
                .ToList();

            if (preservedLargeWorldArrayLikeRows.Count >= 3)
            {
                AppLogger.Info("大型世界数组块校验：按大块原始字节保留通过。" + string.Join(", ", preservedLargeWorldArrayLikeRows.Take(3).Select(r => $"{r.Index}:{r.EntryName}={r.OriginalBytes:N0}")));
            }
            else
            {
                foreach (var entryName in RawPreservedWorldEntryNames)
                {
                    var row = rows.FirstOrDefault(r => string.Equals(r.EntryName, entryName, StringComparison.OrdinalIgnoreCase));
                    if (row == null)
                    {
                        errors.Add($"大型数组块缺失：{entryName}");
                        continue;
                    }
                    if (row.Status != "OK" || (row.DiffBytes ?? 0) != 0)
                        errors.Add($"大型数组块未被原样保留：{entryName} original={row.OriginalBytes}, modified={row.RoundtripBytes}, diff={row.DiffBytes}, status={row.Status}");
                }
            }
        }
        catch (Exception ex)
        {
            errors.Add("世界写入 StateEntry 校验失败：" + ex.GetType().Name + " | " + ex.Message);
        }

        var validation = new
        {
            Passed = errors.Count == 0,
            OriginalPath = gameStateFile,
            ModifiedCompressedBytes = modifiedBytes.LongLength,
            OriginalDecompressedBytes = originalDecompressedBytes,
            ModifiedDecompressedBytes = modifiedDecompressedBytes,
            RawPreserveNote = rawPreserveNote,
            ModifiedNote = modifiedNote,
            Before = new
            {
                beforeInspection.TotalStateEntries,
                beforeInspection.TotalItemInstances,
                beforeInspection.TotalInventories,
                beforeInspection.TotalCitizens,
                beforeInspection.WorldItemCount,
            },
            After = afterInspection == null ? null : new
            {
                afterInspection.TotalStateEntries,
                afterInspection.TotalItemInstances,
                afterInspection.TotalInventories,
                afterInspection.TotalCitizens,
                afterInspection.WorldItemCount,
            },
            Errors = errors,
        };

        File.WriteAllText(Path.Combine(outputDir, "world_write_validation_report.json"), JsonSerializer.Serialize(validation, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        File.WriteAllLines(Path.Combine(outputDir, "world_write_validation_report.csv"), new[]
        {
            "Passed,OriginalPath,OriginalDecompressedBytes,ModifiedDecompressedBytes,RawPreserveNote,ErrorCount,Errors",
            string.Join(',',
                CsvEscape(errors.Count == 0 ? "true" : "false"),
                CsvEscape(gameStateFile),
                CsvEscape(originalDecompressedBytes.ToString(CultureInfo.InvariantCulture)),
                CsvEscape(modifiedDecompressedBytes.ToString(CultureInfo.InvariantCulture)),
                CsvEscape(rawPreserveNote),
                CsvEscape(errors.Count.ToString(CultureInfo.InvariantCulture)),
                CsvEscape(string.Join(" | ", errors)))
        }, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        return errors;
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

        if (fileName.Equals("world_desc", StringComparison.OrdinalIgnoreCase) || fileName.StartsWith("world_desc.", StringComparison.OrdinalIgnoreCase))
        {
            if (TryDeserializeWorldDescriptionWithFormat(bytes, out var worldDesc, out var worldDescWasGzip, out var worldDescNote) && worldDesc != null)
            {
                var info = InspectWorldDescription(file, worldDesc, worldDescWasGzip, worldDescNote);
                result.WorldDescriptions.Add(info);
                scanInfo = scanInfo with { Note = AppendNote(scanInfo.Note, worldDescNote) };
            }
            else
            {
                scanInfo = scanInfo with { Note = AppendNote(scanInfo.Note, worldDescNote) };
                AppLogger.Error("world_desc recognition failed: " + worldDescNote);
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

        if (!scanInfo.DetectedGameState &&
            (fileName.Equals(GameStateFileName, StringComparison.OrdinalIgnoreCase) || fileName.StartsWith("game_state", StringComparison.OrdinalIgnoreCase)))
        {
            AppLogger.Error("game_state recognition failed: " + scanInfo.Note);
        }

        if (!scanInfo.DetectedPlayerSave && fileName.EndsWith(".char", StringComparison.OrdinalIgnoreCase))
        {
            AppLogger.Error("player .char recognition failed: " + scanInfo.Note);
        }
    }

    private static bool PlayerMatches(PlayerSaveInfo info, string text)
    {
        var needle = NormalizeSearchText(text);
        if (string.IsNullOrWhiteSpace(needle)) return true;

        foreach (var candidate in GetPlayerSearchCandidates(info))
        {
            if (NormalizeSearchText(candidate).Contains(needle, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static IEnumerable<string> GetPlayerSearchCandidates(PlayerSaveInfo info)
    {
        yield return info.PlayerName;
        yield return info.SaveFileName;
        yield return info.SourceFileName;
        yield return Path.GetFileNameWithoutExtension(info.SourceFileName);
        yield return info.SourceFile;
        yield return Path.GetFileName(info.SourceFile);
        yield return Path.GetFileNameWithoutExtension(info.SourceFile);
        yield return info.PlayerId.ToString(CultureInfo.InvariantCulture);
        yield return info.EntityId;
        yield return info.WorldId;
    }

    private static string NormalizeSearchText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        return text.Trim()
            .Normalize(NormalizationForm.FormKC)
            .Replace('\u3000', ' ');
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

        foreach (var entry in stateEntries.Where(e => (e.Name ?? string.Empty).Contains("Citizen", StringComparison.OrdinalIgnoreCase) || GetFriendlyTypeName(e.Value).Contains("Citizen", StringComparison.OrdinalIgnoreCase)))
        {
            AddCitizenDebugForEntry(result, entry.Name, entry.Value);
        }

        var itemInstancesValue = stateEntries.FirstOrDefault(e => e.Name == "ItemInstances").Value;
        var inventoriesValue = stateEntries.FirstOrDefault(e => e.Name == "Inventories").Value;
        var worldItemsValue = stateEntries.FirstOrDefault(e => e.Name == "WorldItems").Value;
        var citizensValue = stateEntries.FirstOrDefault(e => e.Name == "Citizens").Value;
        var wildCitizensValue = stateEntries.FirstOrDefault(e => e.Name == "WildCitizens").Value;

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

        AddCitizensFromValue(result, "Citizen", citizensValue);
        AddCitizensFromValue(result, "WildCitizen", wildCitizensValue);

        // Fallback for game versions where the field names or wrapper types differ.
        // We deliberately scan any non-empty state entry whose name/type contains "Citizen".
        // This keeps the Citizens tab useful even when ServerGameState changes slightly.
        if (result.Citizens.Count == 0)
        {
            foreach (var entry in stateEntries)
            {
                if (entry.Value == null) continue;
                var entryName = entry.Name ?? string.Empty;
                var typeName = GetFriendlyTypeName(entry.Value);
                if (!entryName.Contains("Citizen", StringComparison.OrdinalIgnoreCase) &&
                    !typeName.Contains("Citizen", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var kind = entryName.Contains("Wild", StringComparison.OrdinalIgnoreCase) ? "WildCitizen" : entryName;
                AddCitizensFromValue(result, kind, entry.Value);
            }
        }

        // Last-resort scan: look through all state entries and parse objects that look like CitizenModel/WildCitizenInstanceModel.
        if (result.Citizens.Count == 0)
        {
            foreach (var entry in stateEntries)
            {
                AddCitizenLikeObjectsFromValue(result, entry.Name, entry.Value, maxObjects: 2500);
            }
        }

        AddCitizenEntityDiagnostics(result, stateEntries);

        result.GameId = NormalizeGuidString(stateEntries.FirstOrDefault(e => string.Equals(e.Name, "GameId", StringComparison.OrdinalIgnoreCase)).Value);
        result.TimePlayed = ToDoubleOrNull(stateEntries.FirstOrDefault(e => string.Equals(e.Name, "TimePlayed", StringComparison.OrdinalIgnoreCase)).Value);
        result.ConfigDescription = DescribeValue(stateEntries.FirstOrDefault(e => string.Equals(e.Name, "Config", StringComparison.OrdinalIgnoreCase)).Value);
        result.WorldItemCount = TryGetCount(worldItemsValue);
        result.TotalStateEntries = result.StateEntries.Count;
        result.TotalItemInstances = TryGetCount(itemInstancesValue) ?? result.ItemInstances.Count;
        result.TotalInventories = TryGetCount(inventoriesValue) ?? result.Inventories.Count;
        result.TotalCitizens = result.Citizens.Count;
        return result;
    }

    private static void AddCitizensFromValue(GameStateInspectionResult result, string kind, object? value)
    {
        if (value == null) return;
        var added = new HashSet<string>(result.Citizens.Select(c => c.Kind + "|" + c.CitizenId + "|" + c.EntityId), StringComparer.OrdinalIgnoreCase);

        var dictionaryEntries = EnumerateDictionary(value).ToList();
        if (dictionaryEntries.Count > 0)
        {
            foreach (var (key, item) in dictionaryEntries)
            {
                AddSingleCitizenIfPossible(result, added, kind, key?.ToString() ?? string.Empty, item);
            }
            return;
        }

        var index = 0;
        foreach (var item in EnumerateSequence(value))
        {
            AddSingleCitizenIfPossible(result, added, kind, index.ToString(CultureInfo.InvariantCulture), item);
            index++;
        }
    }

    private static void AddCitizenLikeObjectsFromValue(GameStateInspectionResult result, string kind, object? value, int maxObjects)
    {
        if (value == null || maxObjects <= 0) return;
        var added = new HashSet<string>(result.Citizens.Select(c => c.Kind + "|" + c.CitizenId + "|" + c.EntityId), StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<object>();
        queue.Enqueue(value);
        var scanned = 0;

        while (queue.Count > 0 && scanned < maxObjects)
        {
            var current = queue.Dequeue();
            if (!visited.Add(current)) continue;
            scanned++;

            if (LooksLikeCitizen(current))
            {
                AddSingleCitizenIfPossible(result, added, kind, string.Empty, current);
                continue;
            }

            if (current is string) continue;

            foreach (var (_, child) in EnumerateDictionary(current))
            {
                if (child != null && child is not string) queue.Enqueue(child);
            }

            foreach (var child in EnumerateSequence(current))
            {
                if (child != null && child is not string) queue.Enqueue(child);
            }

            foreach (var child in EnumerateObjectChildren(current))
            {
                if (child != null && child is not string) queue.Enqueue(child);
            }
        }
    }

    private static void AddSingleCitizenIfPossible(GameStateInspectionResult result, HashSet<string> added, string kind, string key, object? item)
    {
        try
        {
            if (item == null) return;
            var citizenObject = UnwrapCitizenObject(item);
            if (citizenObject == null || !LooksLikeCitizen(citizenObject)) return;

            var citizen = ReadCitizen(result.SourceFileName, kind, key, citizenObject);
            var uniqueKey = citizen.Info.Kind + "|" + citizen.Info.CitizenId + "|" + citizen.Info.EntityId;
            if (!added.Add(uniqueKey)) return;

            result.Citizens.Add(citizen.Info);
            result.CitizenJobs.AddRange(citizen.Jobs);
        }
        catch (Exception ex)
        {
            result.CitizenDebug.Add(new CitizenDebugInfo(result.SourceFileName, kind, "", "", key, GetFriendlyTypeName(item), "error", "", "", "", "", SafeMemberSummary(item), ex.GetType().Name + ": " + ex.Message));
            AppLogger.Error($"Failed to read citizen candidate {kind}/{key}: {ex}");
        }
    }

    private static void AddCitizenDebugForEntry(GameStateInspectionResult result, string entryName, object? value)
    {
        try
        {
            var entryType = GetFriendlyTypeName(value);
            var entryCount = (TryGetCount(value)?.ToString(CultureInfo.InvariantCulture)) ?? "";
            var dict = EnumerateDictionary(value).Take(12).ToList();
            if (dict.Count > 0)
            {
                foreach (var (key, candidate) in dict)
                    result.CitizenDebug.Add(DescribeCitizenCandidate(result.SourceFileName, entryName, entryType, entryCount, key?.ToString() ?? "", candidate, "dictionary entry"));
                return;
            }

            var seq = EnumerateSequence(value).Take(12).ToList();
            if (seq.Count > 0)
            {
                var i = 0;
                foreach (var candidate in seq)
                    result.CitizenDebug.Add(DescribeCitizenCandidate(result.SourceFileName, entryName, entryType, entryCount, (i++).ToString(CultureInfo.InvariantCulture), candidate, "sequence item"));
                return;
            }

            result.CitizenDebug.Add(DescribeCitizenCandidate(result.SourceFileName, entryName, entryType, entryCount, "", value, "entry value"));
        }
        catch (Exception ex)
        {
            result.CitizenDebug.Add(new CitizenDebugInfo(result.SourceFileName, entryName, GetFriendlyTypeName(value), "", "", "", "error", "", "", "", "", "", ex.GetType().Name + ": " + ex.Message));
        }
    }

    private static CitizenDebugInfo DescribeCitizenCandidate(string sourceFile, string entryName, string entryType, string entryCount, string key, object? candidate, string note)
    {
        var unwrapped = UnwrapCitizenObject(candidate);
        var obj = unwrapped ?? candidate;
        return new CitizenDebugInfo(
            sourceFile,
            entryName,
            entryType,
            entryCount,
            key,
            GetFriendlyTypeName(obj),
            LooksLikeCitizen(obj).ToString(),
            (ReadMember(obj, "Name") != null).ToString(),
            ((ReadMember(obj, "Id") ?? ReadMember(obj, "CitizenId")) != null).ToString(),
            (ReadMember(obj, "JobExperience") != null).ToString(),
            (ReadMember(obj, "CitizenStats") != null).ToString(),
            SafeMemberSummary(obj),
            note);
    }

    private static string SafeMemberSummary(object? obj)
    {
        if (obj == null) return "";
        try
        {
            var type = obj.GetType();
            var names = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Select(f => f.Name + ":" + GetFriendlyTypeName(SafeGet(() => f.GetValue(obj))))
                .Concat(type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(p => p.GetIndexParameters().Length == 0)
                    .Select(p => p.Name + ":" + GetFriendlyTypeName(SafeGet(() => p.GetValue(obj)))))
                .Take(40);
            return string.Join("; ", names);
        }
        catch { return ""; }
    }

    private static object? SafeGet(Func<object?> getter)
    {
        try { return getter(); }
        catch { return null; }
    }

    private static object? UnwrapCitizenObject(object? obj)
    {
        if (obj == null) return null;
        if (LooksLikeCitizen(obj)) return obj;

        foreach (var memberName in new[] { "Citizen", "CitizenModel", "Model", "Value", "Data" })
        {
            var candidate = ReadMember(obj, memberName);
            if (candidate != null && LooksLikeCitizen(candidate)) return candidate;
        }

        return obj;
    }

    private static bool LooksLikeCitizen(object? obj)
    {
        if (obj == null) return false;
        var typeName = GetFriendlyTypeName(obj);
        if (typeName.Contains("CitizenModel", StringComparison.OrdinalIgnoreCase) ||
            typeName.Contains("WildCitizenInstanceModel", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var hasJobs = ReadMember(obj, "JobExperience") != null;
        var hasStats = ReadMember(obj, "CitizenStats") != null;
        var hasIdOrName = ReadMember(obj, "Name") != null || ReadMember(obj, "CitizenId") != null || ReadMember(obj, "Id") != null;
        var hasCitizenSpecific = ReadMember(obj, "LoyaltyLevel") != null || ReadMember(obj, "CharacterPersonality") != null || ReadMember(obj, "CharacterBackground") != null || ReadMember(obj, "CitizenAuras") != null;
        return hasIdOrName && (hasJobs || hasStats || hasCitizenSpecific);
    }

    private static (CitizenInfo Info, List<CitizenJobInfo> Jobs) ReadCitizen(string sourceFileName, string kind, string key, object citizen)
    {
        var citizenId = ReadMember(citizen, "Id")?.ToString()
                        ?? ReadMember(citizen, "CitizenId")?.ToString()
                        ?? key;
        var entityId = ReadMember(citizen, "EntityId")?.ToString() ?? "";
        var baseEntityGuid = ReadMember(citizen, "BaseEntityGuid")?.ToString() ?? "";
        var baseId = ReadMember(citizen, "CitizenBaseId")?.ToString() ?? "";
        var name = ReadStringId(ReadMember(citizen, "Name"));
        var status = ReadMember(citizen, "Status")?.ToString() ?? "";
        var currentJob = ReadMember(citizen, "CurrentJob")?.ToString() ?? "";
        var currentHunger = ToFloat(ReadMember(citizen, "CurrentHunger"));
        var loyalty = ToFloat(ReadMember(citizen, "Loyalty"));
        var loyaltyLevel = ToInt(ReadMember(citizen, "LoyaltyLevel"));
        var personality = ReadMember(citizen, "CharacterPersonality")?.ToString() ?? "";
        var background = ReadMember(citizen, "CharacterBackground")?.ToString() ?? "";
        var homeBuildingId = ReadMember(citizen, "HomeBuildingId")?.ToString() ?? "";
        var citizenSlotId = ReadMember(citizen, "CitizenSlotId")?.ToString() ?? "";
        var currentWorldId = ReadMember(citizen, "CurrentWorldId")?.ToString() ?? "";
        var spawnWorldId = ReadMember(citizen, "SpawnWorldId")?.ToString() ?? "";
        var personalQuestStatus = ReadMember(citizen, "PersonalQuestStatus")?.ToString() ?? "";

        var statsObj = ReadMember(citizen, "CitizenStats");
        var efficiency = ReadCitizenStat(statsObj, "Citizen_Efficiency", "CalculatedValue");
        var expertise = ReadCitizenStat(statsObj, "Citizen_Expertise", "CalculatedValue");
        var happiness = ReadCitizenStat(statsObj, "Citizen_Happiness", "CalculatedValue");
        var foodCost = ReadCitizenStat(statsObj, "Citizen_FoodCost", "CalculatedValue");
        var loyaltyGain = ReadCitizenStat(statsObj, "Citizen_LoyaltyGain", "CalculatedValue");
        var experienceGain = ReadCitizenStat(statsObj, "Citizen_ExperienceGain", "CalculatedValue");

        var baseEfficiency = ReadCitizenStat(statsObj, "Citizen_Efficiency", "BaseValue");
        var baseExpertise = ReadCitizenStat(statsObj, "Citizen_Expertise", "BaseValue");

        var jobMap = ReadCitizenJobMap(citizen);
        var jobs = new List<CitizenJobInfo>();
        foreach (var (jobId, progress) in jobMap.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            jobs.Add(new CitizenJobInfo(sourceFileName, kind, citizenId, name, jobId, ToInt(ReadMember(progress, "Level")), ToFloat(ReadMember(progress, "Experience"))));
        }

        var currentJobLevel = "";
        var currentJobExperience = "";
        if (!string.IsNullOrWhiteSpace(currentJob) && jobMap.TryGetValue(currentJob, out var currentProgress) && currentProgress != null)
        {
            currentJobLevel = ToInt(ReadMember(currentProgress, "Level")).ToString(CultureInfo.InvariantCulture);
            currentJobExperience = ToFloat(ReadMember(currentProgress, "Experience")).ToString(CultureInfo.InvariantCulture);
        }

        var auraIds = new List<string>();
        var traitIds = new List<string>();
        var auras = ReadMember(citizen, "Auras");
        foreach (var aura in EnumerateSequence(auras))
        {
            if (aura == null) continue;
            var dataId = ReadMember(aura, "DataId")?.ToString() ?? "";
            var type = ReadMember(aura, "Type")?.ToString() ?? "";
            if (!string.IsNullOrWhiteSpace(dataId))
            {
                auraIds.Add(dataId);
                if (type.Equals("Trait", StringComparison.OrdinalIgnoreCase)) traitIds.Add(dataId);
            }
        }

        var wildTraitList = ReadMember(citizen, "CitizenAuras");
        foreach (var trait in EnumerateSequence(wildTraitList))
        {
            var id = trait?.ToString() ?? "";
            if (!string.IsNullOrWhiteSpace(id))
            {
                traitIds.Add(id);
                auraIds.Add(id);
            }
        }

        traitIds = traitIds.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        auraIds = auraIds.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

        // Some saved CitizenStats instances only retain the aura list and do not expose a hydrated Stats dictionary
        // until the live game recalculates them. In that case we still show the actual modifiers visible on this
        // citizen, instead of leaving the UI as an em dash. No modifier means 0.
        efficiency ??= EstimateCitizenAuraStat(auraIds, "Citizen_Efficiency");
        expertise ??= EstimateCitizenAuraStat(auraIds, "Citizen_Expertise");
        happiness ??= EstimateCitizenAuraStat(auraIds, "Citizen_Happiness");
        foodCost ??= EstimateCitizenAuraStat(auraIds, "Citizen_FoodCost");
        loyaltyGain ??= EstimateCitizenAuraStat(auraIds, "Citizen_LoyaltyGain");
        experienceGain ??= EstimateCitizenAuraStat(auraIds, "Citizen_ExperienceGain");
        baseEfficiency ??= 0f;
        baseExpertise ??= 0f;

        var info = new CitizenInfo(
            sourceFileName,
            kind,
            citizenId,
            entityId,
            baseEntityGuid,
            name,
            baseId,
            status,
            currentJob,
            currentJobLevel,
            currentJobExperience,
            FormatNullableFloat(efficiency),
            FormatNullableFloat(expertise),
            FormatNullableFloat(baseEfficiency),
            FormatNullableFloat(baseExpertise),
            FormatNullableFloat(happiness),
            FormatNullableFloat(foodCost),
            FormatNullableFloat(loyaltyGain),
            FormatNullableFloat(experienceGain),
            loyalty.ToString(CultureInfo.InvariantCulture),
            loyaltyLevel.ToString(CultureInfo.InvariantCulture),
            currentHunger.ToString(CultureInfo.InvariantCulture),
            personality,
            background,
            homeBuildingId,
            citizenSlotId,
            currentWorldId,
            spawnWorldId,
            personalQuestStatus,
            jobMap.Count.ToString(CultureInfo.InvariantCulture),
            traitIds.Count.ToString(CultureInfo.InvariantCulture),
            string.Join(";", traitIds),
            string.Join(";", auraIds),
            GetFriendlyTypeName(citizen));

        return (info, jobs);
    }


    private static void AddCitizenEntityDiagnostics(GameStateInspectionResult result, List<(string Name, object? Value)> stateEntries)
    {
        try
        {
            if (result.Citizens.Count == 0) return;

            var entitiesValue = stateEntries.FirstOrDefault(e => string.Equals(e.Name, "Entities", StringComparison.OrdinalIgnoreCase)).Value;
            var controllerStatesValue = stateEntries.FirstOrDefault(e => string.Equals(e.Name, "CitizenControllerStates", StringComparison.OrdinalIgnoreCase)).Value;
            var citizenSlotsValue = stateEntries.FirstOrDefault(e => string.Equals(e.Name, "CitizenSlots", StringComparison.OrdinalIgnoreCase)).Value;

            var entityIds = result.Citizens
                .Select(c => c.EntityId)
                .Where(IsLikelyGuidText)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var citizenIds = result.Citizens
                .Select(c => c.CitizenId)
                .Where(IsLikelyGuidText)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var citizenSlotIds = result.Citizens
                .Select(c => c.CitizenSlotId)
                .Where(IsLikelyGuidText)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var entityLookup = BuildTargetObjectLookup(entitiesValue, entityIds, maxScan: 700000, idMemberCandidates: ["Id", "EntityId", "Guid", "EntityGuid"]);
            var controllerLookup = BuildTargetObjectLookup(controllerStatesValue, citizenIds.Concat(entityIds).ToHashSet(StringComparer.OrdinalIgnoreCase), maxScan: 10000, idMemberCandidates: ["CitizenId", "EntityId", "Id"]);
            var slotLookup = BuildTargetObjectLookup(citizenSlotsValue, citizenSlotIds, maxScan: 10000, idMemberCandidates: ["Id", "CitizenSlotId", "SlotId"]);

            foreach (var citizen in result.Citizens.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.CitizenId, StringComparer.OrdinalIgnoreCase))
            {
                var notes = new List<string>();
                var terms = BuildCitizenDiagnosticTerms(citizen);

                var entityFound = TryLookupTarget(entityLookup, citizen.EntityId, out var entityKey, out var entityObj);
                if (!entityFound)
                {
                    notes.Add("EntityId not found in Entities. This is a strong loading-risk if the game expects Citizen.EntityId to resolve to ServerEntityModel.");
                }

                var controllerFound = TryLookupTarget(controllerLookup, citizen.CitizenId, out var controllerKey, out var controllerObj)
                                      || TryLookupTarget(controllerLookup, citizen.EntityId, out controllerKey, out controllerObj);

                var slotFound = TryLookupTarget(slotLookup, citizen.CitizenSlotId, out var slotKey, out var slotObj);

                var entityHits = entityFound
                    ? CollectObjectTextHits(entityObj, terms, "Entities[" + entityKey + "]", maxDepth: 5, maxNodes: 600, maxHits: 60).ToList()
                    : new List<CitizenAuraRuntimeHitInfo>();
                var controllerHits = controllerFound
                    ? CollectObjectTextHits(controllerObj, terms, "CitizenControllerStates[" + controllerKey + "]", maxDepth: 5, maxNodes: 400, maxHits: 50).ToList()
                    : new List<CitizenAuraRuntimeHitInfo>();
                var slotHits = slotFound
                    ? CollectObjectTextHits(slotObj, terms, "CitizenSlots[" + slotKey + "]", maxDepth: 4, maxNodes: 200, maxHits: 30).ToList()
                    : new List<CitizenAuraRuntimeHitInfo>();

                foreach (var hit in entityHits)
                    result.CitizenAuraRuntime.Add(hit with { SourceFile = result.SourceFileName, CitizenId = citizen.CitizenId, Name = citizen.Name, EntityId = citizen.EntityId, SourceKind = "Entity", SourceKey = entityKey });
                foreach (var hit in controllerHits)
                    result.CitizenAuraRuntime.Add(hit with { SourceFile = result.SourceFileName, CitizenId = citizen.CitizenId, Name = citizen.Name, EntityId = citizen.EntityId, SourceKind = "CitizenControllerState", SourceKey = controllerKey });
                foreach (var hit in slotHits)
                    result.CitizenAuraRuntime.Add(hit with { SourceFile = result.SourceFileName, CitizenId = citizen.CitizenId, Name = citizen.Name, EntityId = citizen.EntityId, SourceKind = "CitizenSlot", SourceKey = slotKey });

                if ((SplitSemicolonIds(citizen.AuraIds).Any() || SplitSemicolonIds(citizen.TraitIds).Any()) && entityFound && entityHits.Count == 0 && controllerHits.Count == 0)
                {
                    notes.Add("Citizen has aura/trait IDs, but no matching aura/trait/buff text was found in its Entity or CitizenControllerState snapshot. This may mean aura runtime state is not mirrored there, or the editor must update another runtime structure.");
                }

                result.CitizenEntityBindings.Add(new CitizenEntityBindingInfo(
                    result.SourceFileName,
                    citizen.CitizenId,
                    citizen.Name,
                    citizen.EntityId,
                    citizen.TraitIds,
                    citizen.AuraIds,
                    entityFound,
                    entityKey,
                    GetFriendlyTypeName(entityObj),
                    entityFound ? SafeMemberSummary(entityObj) : "",
                    entityHits.Count.ToString(CultureInfo.InvariantCulture),
                    controllerFound.ToString(),
                    controllerKey,
                    GetFriendlyTypeName(controllerObj),
                    controllerFound ? SafeMemberSummary(controllerObj) : "",
                    controllerHits.Count.ToString(CultureInfo.InvariantCulture),
                    slotFound.ToString(),
                    slotKey,
                    GetFriendlyTypeName(slotObj),
                    string.Join(" | ", notes)));
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("Citizen/Entity diagnostic failed: " + ex);
        }
    }

    private static HashSet<string> BuildCitizenDiagnosticTerms(CitizenInfo citizen)
    {
        var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in new[] { citizen.CitizenId, citizen.EntityId, citizen.CitizenSlotId, citizen.CurrentWorldId, citizen.SpawnWorldId })
        {
            if (!string.IsNullOrWhiteSpace(value)) terms.Add(value);
        }
        foreach (var id in SplitSemicolonIds(citizen.AuraIds)) terms.Add(id);
        foreach (var id in SplitSemicolonIds(citizen.TraitIds)) terms.Add(id);
        foreach (var token in new[] { "Aura", "Auras", "Trait", "Traits", "Buff", "Debuff", "Stat", "Stats", "Citizen", "Loyalty", "Efficiency", "Expertise", "Happiness" })
            terms.Add(token);
        return terms;
    }

    private static List<string> SplitSemicolonIds(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new List<string>();
        return text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static Dictionary<string, (string Key, object? Value)> BuildTargetObjectLookup(object? source, HashSet<string> targets, int maxScan, string[] idMemberCandidates)
    {
        var result = new Dictionary<string, (string Key, object? Value)>(StringComparer.OrdinalIgnoreCase);
        if (source == null || targets.Count == 0) return result;

        var scanned = 0;
        foreach (var (key, value) in EnumerateDictionary(source))
        {
            if (++scanned > maxScan) break;
            var keyText = key?.ToString() ?? "";
            if (targets.Contains(keyText))
            {
                result[keyText] = (keyText, value);
                if (result.Count >= targets.Count) break;
                continue;
            }

            foreach (var member in idMemberCandidates)
            {
                var id = ReadMember(value, member)?.ToString() ?? "";
                if (targets.Contains(id))
                {
                    result[id] = (keyText, value);
                    break;
                }
            }

            if (result.Count >= targets.Count) break;
        }

        return result;
    }

    private static bool TryLookupTarget(Dictionary<string, (string Key, object? Value)> lookup, string id, out string key, out object? value)
    {
        key = "";
        value = null;
        if (string.IsNullOrWhiteSpace(id)) return false;
        if (!lookup.TryGetValue(id, out var found)) return false;
        key = found.Key;
        value = found.Value;
        return true;
    }

    private static bool IsLikelyGuidText(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) && Guid.TryParse(value, out _);
    }

    private static IEnumerable<CitizenAuraRuntimeHitInfo> CollectObjectTextHits(object? root, HashSet<string> terms, string rootPath, int maxDepth, int maxNodes, int maxHits)
    {
        if (root == null || terms.Count == 0) yield break;

        var queue = new Queue<(object Obj, string Path, int Depth)>();
        var visited = new HashSet<int>();
        queue.Enqueue((root, rootPath, 0));
        var emitted = 0;
        var scanned = 0;

        while (queue.Count > 0 && scanned < maxNodes && emitted < maxHits)
        {
            var (obj, path, depth) = queue.Dequeue();
            if (obj == null) continue;
            var type = obj.GetType();
            if (IsSimpleDiagnosticValue(obj)) continue;

            var hash = RuntimeHelpers.GetHashCode(obj);
            if (!visited.Add(hash)) continue;
            scanned++;

            foreach (var (memberName, memberType, value) in EnumerateDiagnosticMembers(obj))
            {
                if (emitted >= maxHits) yield break;
                var memberPath = path + "." + memberName;
                var valueText = ValueToDiagnosticText(value);
                var reason = GetDiagnosticHitReason(memberName, valueText, terms);
                if (!string.IsNullOrEmpty(reason))
                {
                    emitted++;
                    yield return new CitizenAuraRuntimeHitInfo("", "", "", "", "", "", memberPath, memberName, memberType, TrimForReport(valueText, 500), reason);
                }

                if (depth < maxDepth && value != null && value is not string && !IsSimpleDiagnosticValue(value))
                {
                    if (value is IEnumerable enumerable)
                    {
                        var i = 0;
                        foreach (var child in enumerable)
                        {
                            if (child == null) continue;
                            if (i >= 40) break;
                            if (!IsSimpleDiagnosticValue(child)) queue.Enqueue((child, memberPath + "[" + i.ToString(CultureInfo.InvariantCulture) + "]", depth + 1));
                            i++;
                        }
                    }
                    else
                    {
                        queue.Enqueue((value, memberPath, depth + 1));
                    }
                }
            }
        }
    }

    private static IEnumerable<(string Name, string TypeName, object? Value)> EnumerateDiagnosticMembers(object obj)
    {
        var type = obj.GetType();
        foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            object? value = null;
            try { value = field.GetValue(obj); } catch { }
            yield return (field.Name, GetFriendlyTypeName(value ?? field.FieldType), value);
        }

        foreach (var prop in type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (prop.GetIndexParameters().Length != 0) continue;
            object? value = null;
            try { value = prop.GetValue(obj); } catch { }
            yield return (prop.Name, GetFriendlyTypeName(value ?? prop.PropertyType), value);
        }
    }

    private static bool IsSimpleDiagnosticValue(object value)
    {
        var type = value.GetType();
        return value is string || type.IsPrimitive || type.IsEnum || type == typeof(decimal) || type == typeof(Guid) || type == typeof(DateTime);
    }

    private static string ValueToDiagnosticText(object? value)
    {
        if (value == null) return "";
        if (value is string s) return s;
        if (IsSimpleDiagnosticValue(value)) return value.ToString() ?? "";
        var count = TryGetCount(value);
        if (count.HasValue) return "Count=" + count.Value.ToString(CultureInfo.InvariantCulture) + " " + GetFriendlyTypeName(value);
        return SafeMemberSummary(value);
    }

    private static string GetDiagnosticHitReason(string memberName, string valueText, HashSet<string> terms)
    {
        var reasons = new List<string>();
        foreach (var term in terms)
        {
            if (string.IsNullOrWhiteSpace(term)) continue;
            if (memberName.Contains(term, StringComparison.OrdinalIgnoreCase)) reasons.Add("member:" + term);
            else if (!string.IsNullOrWhiteSpace(valueText) && valueText.Contains(term, StringComparison.OrdinalIgnoreCase)) reasons.Add("value:" + term);
            if (reasons.Count >= 6) break;
        }
        return string.Join(";", reasons.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string TrimForReport(string text, int max)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= max) return text;
        return text[..max] + "...";
    }

    private static Dictionary<string, object?> ReadCitizenJobMap(object citizen)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var jobs = ReadMember(citizen, "JobExperience");
        foreach (var (key, value) in EnumerateDictionary(jobs))
        {
            var jobId = key?.ToString() ?? "";
            if (!string.IsNullOrWhiteSpace(jobId)) result[jobId] = value;
        }
        return result;
    }

    private static float? ReadCitizenStat(object? statsObj, string statId, string valueMember)
    {
        if (statsObj == null || string.IsNullOrWhiteSpace(statId)) return null;

        // Fast path: read the public Stats dictionary directly.
        var stats = ReadMember(statsObj, "Stats");
        object? bestValue = null;
        foreach (var (key, value) in EnumerateDictionary(stats))
        {
            var keyText = key?.ToString() ?? "";
            var valueStatId = ReadMember(value, "StatId")?.ToString() ?? "";
            if (keyText.Equals(statId, StringComparison.OrdinalIgnoreCase) || valueStatId.Equals(statId, StringComparison.OrdinalIgnoreCase))
            {
                bestValue = value;
                break;
            }
        }

        // Fallback: tolerate slightly different stat dictionary keys.
        if (bestValue == null)
        {
            var targetTail = statId.Split('_', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? statId;
            foreach (var (key, value) in EnumerateDictionary(stats))
            {
                var keyText = key?.ToString() ?? "";
                var valueStatId = ReadMember(value, "StatId")?.ToString() ?? "";
                if (keyText.Contains(targetTail, StringComparison.OrdinalIgnoreCase) ||
                    valueStatId.Contains(targetTail, StringComparison.OrdinalIgnoreCase))
                {
                    bestValue = value;
                    break;
                }
            }
        }

        if (bestValue != null)
        {
            return ToFloat(ReadMember(bestValue, valueMember));
        }

        // Last fallback: use CitizenStats.Get(statId) when available. This returns the calculated value.
        if (valueMember.Equals("CalculatedValue", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var getMethod = statsObj.GetType().GetMethod("Get", BindingFlags.Instance | BindingFlags.Public);
                if (getMethod != null)
                {
                    var value = getMethod.Invoke(statsObj, new object[] { statId });
                    if (value != null) return ToFloat(value);
                }
            }
            catch
            {
                // Keep the inspector read-only and tolerant; missing stats are shown as em dash in the UI.
            }
        }

        return null;
    }

    private static float EstimateCitizenAuraStat(IEnumerable<string> auraIds, string statId)
    {
        if (string.IsNullOrWhiteSpace(statId)) return 0f;
        var total = 0f;
        foreach (var auraId in auraIds ?? Enumerable.Empty<string>())
        {
            foreach (var effect in GetCitizenAuraEffects(auraId))
            {
                if (!effect.StatId.Equals(statId, StringComparison.OrdinalIgnoreCase)) continue;
                // Additive effects are the game's direct flat modifiers. Multiplier-only effects are still shown
                // numerically as their raw multiplier so the user can see that a value exists.
                total += effect.Additive;
                total += effect.Multiplier;
                total += effect.BaseMultiplier;
                total += effect.AdditiveMultiplier;
                total += effect.BonusMultiplier;
            }
        }
        return total;
    }

    private static IEnumerable<CitizenAuraStatEffect> GetCitizenAuraEffects(string? rawAuraId)
    {
        var auraId = NormalizeCitizenAuraId(rawAuraId);
        if (string.IsNullOrWhiteSpace(auraId)) yield break;

        static CitizenAuraStatEffect E(string statId, float additive = 0f, float multiplier = 0f, float baseMultiplier = 0f, float additiveMultiplier = 0f, float bonusMultiplier = 0f)
            => new(statId, additive, multiplier, baseMultiplier, additiveMultiplier, bonusMultiplier);

        foreach (var effect in auraId switch
        {
            "citizen_aura:debuff:food" => new[] { E("Citizen_FoodCost", additive: 10f) },
            "citizen_aura:debuff:expertise" => new[] { E("Citizen_Expertise", additive: -2f) },
            "citizen_aura:debuff:efficiency" => new[] { E("Citizen_Efficiency", additive: -4f) },
            "citizen_aura:debuff:experience" => new[] { E("Citizen_ExperienceGain", additive: -0.3f) },
            "citizen_aura:debuff:loyalty" => new[] { E("Citizen_LoyaltyGain", additive: -0.5f) },
            "citizen_aura:debuff:happiness" => new[] { E("Citizen_Happiness", additive: -3f) },
            "citizen_aura:debuff:anxiety" => new[] { E("Citizen_Happiness", additive: -2f), E("Citizen_LoyaltyGain", additive: -0.2f) },
            "citizen_aura:buff:expertise" => new[] { E("Citizen_Expertise", additive: 3f) },
            "citizen_aura:buff:efficiency" => new[] { E("Citizen_Efficiency", additive: 4f) },
            "citizen_aura:buff:experience" => new[] { E("Citizen_ExperienceGain", additive: 0.3f) },
            "citizen_aura:buff:loyalty" => new[] { E("Citizen_LoyaltyGain", additive: 0.3f) },
            "citizen_aura:buff:happiness" => new[] { E("Citizen_Happiness", additive: 3f) },
            "citizen_aura:loyalty:1" => new[] { E("Citizen_Happiness", additive: 1f), E("Citizen_ExperienceGain", additive: 0.1f) },
            "citizen_aura:loyalty:2" => new[] { E("Citizen_Happiness", additive: 2f), E("Citizen_Expertise", additive: 0.5f), E("Citizen_ExperienceGain", additive: 0.15f) },
            "citizen_aura:loyalty:3" => new[] { E("Citizen_Happiness", additive: 2.5f), E("Citizen_Expertise", additive: 1f), E("Citizen_ExperienceGain", additive: 0.2f) },
            "citizen_aura:loyalty:4" => new[] { E("Citizen_Happiness", additive: 3f), E("Citizen_Expertise", additive: 1f), E("Citizen_ExperienceGain", additive: 0.3f) },
            "citizen_aura:wine:0" => new[] { E("Citizen_Happiness", additive: 2f), E("Citizen_LoyaltyGain", additive: 0.05f), E("Citizen_Efficiency", additive: -1f) },
            "citizen_aura:gift:0" => new[] { E("Citizen_Happiness", additive: 1f) },
            "citizen_aura:gift:1" => new[] { E("Citizen_Happiness", additive: 1f), E("Citizen_LoyaltyGain", additive: 0.5f) },
            "citizen_aura:gift:2" => new[] { E("Citizen_Happiness", additive: 2f) },
            "citizen_aura:gift:bad" => new[] { E("Citizen_Happiness", additive: -1f) },
            "citizen_aura:gift:food_cost_reduction" => new[] { E("Citizen_FoodCost", multiplier: -0.2f) },
            "citizen_aura:injury:broken_arm" => new[] { E("Citizen_Efficiency", multiplier: -0.5f) },
            "citizen_aura:injury:broken_leg" => new[] { E("Citizen_Efficiency", multiplier: -0.3f) },
            "citizen_aura:injury:concussion" => new[] { E("Citizen_ExperienceGain", multiplier: -0.5f), E("Citizen_Expertise", multiplier: -0.5f) },
            "citizen_aura:injury:tetanus" => new[] { E("Citizen_Happiness", multiplier: -0.25f) },
            "citizen_aura:injury:tinnitus" => new[] { E("Citizen_Happiness", multiplier: -0.5f) },
            "citizen_aura:injury:wounded_eye" => new[] { E("Citizen_Efficiency", multiplier: -0.5f), E("Citizen_Expertise", multiplier: -0.25f) },
            "citizen_aura:injury:broken_rib" => new[] { E("Citizen_Efficiency", multiplier: -0.25f), E("Citizen_ExperienceGain", multiplier: -0.25f) },
            "citizen_aura:injury:infected_wound" => new[] { E("Citizen_FoodCost", additive: 1f), E("Citizen_Happiness", multiplier: -0.25f) },
            "citizen_aura:buff:cornucopia" => new[] { E("Citizen_FoodCost", multiplier: -1f) },
            "citizen_aura:buff:venus_embrace" => new[] { E("Citizen_Efficiency", additive: 2f) },
            "citizen_aura:buff:venus_citizen_expertise" => new[] { E("Citizen_Expertise", additive: 1f) },
            "citizen_aura:buff:town_defence" => new[] { E("Citizen_Happiness", additive: 1f) },
            _ => Array.Empty<CitizenAuraStatEffect>()
        })
        {
            yield return effect;
        }
    }

    private static string NormalizeCitizenAuraId(string? rawAuraId)
    {
        var id = (rawAuraId ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(id)) return "";
        if (id.StartsWith("citizen_aura:", StringComparison.OrdinalIgnoreCase)) return id;
        if (id.StartsWith("trait:", StringComparison.OrdinalIgnoreCase)) return "citizen_aura:" + id[6..];
        if (id is "efficiency" or "expertise" or "experience" or "loyalty" or "happiness") return "citizen_aura:buff:" + id;
        if (id is "food") return "citizen_aura:debuff:food";
        return id;
    }

    private readonly record struct CitizenAuraStatEffect(string StatId, float Additive, float Multiplier, float BaseMultiplier, float AdditiveMultiplier, float BonusMultiplier);

    private static string FormatNullableFloat(float? value)
    {
        return value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "";
    }

    private static string ReadStringId(object? value)
    {
        if (value == null) return "";
        var id = ReadMember(value, "Id")?.ToString();
        if (!string.IsNullOrWhiteSpace(id)) return id!;
        var text = value.ToString() ?? "";
        return text.Contains('.') && text.Contains("StringId") ? "" : text;
    }

    private static WorldDescriptionInfo InspectWorldDescription(string sourceFile, object desc, bool wasGzip, string note)
    {
        return new WorldDescriptionInfo
        {
            SourceFile = sourceFile,
            SourceFileName = Path.GetFileName(sourceFile),
            WasGzip = wasGzip,
            Note = note,
            Name = ToDisplayString(ReadMember(desc, "Name")),
            Created = ToDisplayString(ReadMember(desc, "Created")),
            LastPlayed = ToDisplayString(ReadMember(desc, "LastPlayed")),
            Tier = ToDisplayString(ReadMember(desc, "Tier")),
            Size = ToDisplayString(ReadMember(desc, "Size")),
            TimePlayed = ToDoubleOrNull(ReadMember(desc, "TimePlayed")),
            FolderPath = ToDisplayString(ReadMember(desc, "FolderPath")),
            GameId = NormalizeGuidString(ReadMember(desc, "GameId")),
            Modded = ToBoolOrNull(ReadMember(desc, "Modded")),
            Broken = ToBoolOrNull(ReadMember(desc, "Broken")),
            LoadedFromBackup = ToBoolOrNull(ReadMember(desc, "LoadedFromBackup")),
            TypeName = desc.GetType().FullName ?? desc.GetType().Name
        };
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
        AddPlayerSkills(info, character);
        info.InventorySlots = info.Items.Count(i => i.Section == "Inventory");
        info.EquipmentSlots = info.Items.Count(i => i.Section == "Equipment");
        info.SecondaryEquipmentSlots = info.Items.Count(i => i.Section == "SecondaryEquipment");
        return info;
    }

    private static void AddPlayerSkills(PlayerSaveInfo info, object? character)
    {
        var skillsRoot = ReadMember(character, "Skills");
        var characterSkills = ReadMember(skillsRoot, "CharacterSkills");
        var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in EnumerateDictionary(characterSkills))
        {
            var id = key?.ToString() ?? "";
            if (!string.IsNullOrWhiteSpace(id)) map[id] = value;
        }

        foreach (var data in KnownSkills)
        {
            map.TryGetValue(data.Id, out var skill);
            var level = skill == null ? 0 : ToInt(ReadMember(skill, "Level"));
            var experience = skill == null ? 0f : ToFloat(ReadMember(skill, "CurrentExperience"));
            var required = ExperienceRequiredToLevelUpAtLevel(level);
            var value = data.Value * (level / 100f);
            info.Skills.Add(new PlayerSkillInfo(info.SourceFileName, info.SaveFileName, info.PlayerName, info.PlayerId, data.Id, data.Name, data.Description, level, experience, required, value));
        }
    }

    private static void AddPlayerItems(PlayerSaveInfo info, string section, object? sequence)
    {
        var arr = EnumerateSequence(sequence).ToList();
        for (var i = 0; i < arr.Count; i++)
        {
            var item = arr[i];
            if (item == null)
            {
                info.Items.Add(new PlayerItemInfo(
                    info.SourceFileName,
                    info.SaveFileName,
                    info.PlayerName,
                    info.PlayerId,
                    section,
                    i,
                    "",
                    "",
                    0,
                    0,
                    "",
                    false,
                    "Empty"));
                continue;
            }

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
                baseInfo.AuraIds,
                baseInfo.HasUsableInfo,
                baseInfo.TypeName));
        }
    }


    private static List<string> ReadItemAuraIds(object? item)
    {
        var ids = new List<string>();
        var auras = ReadMember(item, "Auras");
        foreach (var aura in EnumerateSequence(auras))
        {
            var id = ReadMember(aura, "DataId")?.ToString()
                     ?? ReadMember(aura, "AuraId")?.ToString()
                     ?? ReadMember(aura, "Id")?.ToString()
                     ?? ReadMember(ReadMember(aura, "BaseState"), "BaseAuraId")?.ToString()
                     ?? ReadMember(ReadMember(aura, "Info"), "Id")?.ToString()
                     ?? ReadMember(ReadMember(aura, "AuraInfo"), "Id")?.ToString()
                     ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(id) && !ids.Contains(id, StringComparer.OrdinalIgnoreCase))
                ids.Add(id.Trim());
        }
        return ids;
    }

    private static ItemInstanceInfo ReadItemInstance(string sourceFileName, string key, object item, string sourceKind, string ownerName)
    {
        var instanceId = ReadMember(item, "Id")?.ToString() ?? key;
        var baseDataId = ReadMember(item, "BaseDataId")?.ToString() ?? "";
        var stackCount = ToInt(ReadMember(item, "StackCount"));
        var inventoryId = ReadMember(item, "InventoryId")?.ToString();
        var auraIds = ReadItemAuraIds(item);
        var auraCount = auraIds.Count;
        var usable = ReadMember(item, "UsableInfo") != null;
        return new ItemInstanceInfo(sourceFileName, sourceKind, ownerName, instanceId, baseDataId, stackCount, inventoryId ?? "", auraCount, string.Join(";", auraIds), usable, GetFriendlyTypeName(item));
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

    private static bool TryGetPlayerSlot(object playerSave, string section, int slotIndex, out object? sectionObject, out object? currentItem, out string error)
    {
        sectionObject = ReadMember(playerSave, section);
        currentItem = null;
        error = "";
        if (sectionObject == null)
        {
            error = $"找不到区域 {section}。";
            return false;
        }

        if (slotIndex < 0)
        {
            error = $"非法槽位 {slotIndex}。";
            return false;
        }

        if (sectionObject is Array array)
        {
            if (slotIndex >= array.Length)
            {
                error = $"{section} 槽位越界：{slotIndex}/{array.Length}。";
                return false;
            }
            currentItem = array.GetValue(slotIndex);
            return true;
        }

        if (sectionObject is IList list)
        {
            if (slotIndex >= list.Count)
            {
                error = $"{section} 槽位越界：{slotIndex}/{list.Count}。";
                return false;
            }
            currentItem = list[slotIndex];
            return true;
        }

        var items = EnumerateSequence(sectionObject).ToList();
        if (slotIndex >= items.Count)
        {
            error = $"{section} 槽位越界：{slotIndex}/{items.Count}。";
            return false;
        }
        currentItem = items[slotIndex];
        return true;
    }

    private static void SetPlayerSlot(object sectionObject, int slotIndex, object? value)
    {
        if (sectionObject is Array array)
        {
            array.SetValue(value, slotIndex);
            return;
        }

        if (sectionObject is IList list)
        {
            list[slotIndex] = value;
            return;
        }

        throw new InvalidOperationException($"不支持写入槽位的区域类型：{sectionObject.GetType().FullName}");
    }


    private static List<string> NormalizeItemAuraList(string? value)
    {
        return (value ?? string.Empty)
            .Replace('\r', ';')
            .Replace('\n', ';')
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(v => v.Trim().ToLowerInvariant())
            .Where(v => v.StartsWith("item_aura:", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string NormalizeAuraListText(string? value) => string.Join(";", NormalizeItemAuraList(value));

    private static bool IsEquipmentSection(string? section)
    {
        var value = (section ?? string.Empty).Trim();
        return value.Equals("Equipment", StringComparison.OrdinalIgnoreCase) || value.Equals("SecondaryEquipment", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsEquipmentSlotItemId(string? id) => IsAuraEditableItemId(id);

    private static bool IsAuraEditableItemId(string? id)
    {
        var value = (id ?? string.Empty).Trim().ToLowerInvariant();
        if (value.StartsWith("weapon:") || value.StartsWith("armor:") || value.StartsWith("trinket:") || value.StartsWith("axe:") || value.StartsWith("pickaxe:") || value.StartsWith("back:") || value.StartsWith("torch:")) return true;
        return false;
    }

    private static bool IsKnownItemAura(string auraId)
    {
        return auraId.StartsWith("item_aura:", StringComparison.OrdinalIgnoreCase);
    }

    private static void ApplyItemAurasToItem(object item, string? auraIds)
    {
        EnsureSharedDataSetup();
        var auras = ReadMember(item, "Auras");
        try
        {
            var clear = auras?.GetType().GetMethod("Clear", BindingFlags.Public | BindingFlags.Instance);
            clear?.Invoke(auras, null);
        }
        catch { }

        var addAuraMethod = FindType("CandideServer.ServerControllers.ItemInstanceSController")?.GetMethod("AddAuraToItem", BindingFlags.Public | BindingFlags.Static);
        foreach (var auraId in NormalizeItemAuraList(auraIds))
        {
            try
            {
                if (addAuraMethod != null)
                {
                    addAuraMethod.Invoke(null, new object?[] { item, auraId, true });
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error($"添加装备 Aura 失败：{auraId}", ex);
            }
        }
        InvokeIfExists(item, "CalculateStats");
    }

    private static object CreateItemInstance(string baseDataId, int stackCount)
    {
        var itemType = FindType("Shared.Models.Items.ItemInstanceModel")
            ?? throw new InvalidOperationException("Could not find Shared.Models.Items.ItemInstanceModel.");

        object? item = null;
        var stringCtor = itemType.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, binder: null, new[] { typeof(string) }, modifiers: null);
        if (stringCtor != null)
            item = stringCtor.Invoke(new object[] { baseDataId });
        else
            item = Activator.CreateInstance(itemType);

        if (item == null)
            throw new InvalidOperationException("Could not create ItemInstanceModel.");

        // BaseDataId is readonly in the game model, so prefer the string constructor. This fallback is for compatibility.
        var currentBaseId = ReadMember(item, "BaseDataId")?.ToString();
        if (!baseDataId.Equals(currentBaseId, StringComparison.OrdinalIgnoreCase))
        {
            TrySetMember(item, "BaseDataId", baseDataId);
        }

        TrySetMember(item, "Id", Guid.NewGuid());
        TrySetMember(item, "StackCount", stackCount);
        InvokeIfExists(item, "CalculateStats");
        return item;
    }

    private static object? GetOrCreateCharacterSkill(object? character, string skillId)
    {
        if (character == null) return null;
        var skillsObj = ReadMember(character, "Skills");
        if (skillsObj == null)
        {
            var skillsType = FindType("Shared.Models.Skill.Skills");
            if (skillsType == null) return null;
            skillsObj = Activator.CreateInstance(skillsType);
            SetMember(character, "Skills", skillsObj);
        }

        var dict = ReadMember(skillsObj, "CharacterSkills") as IDictionary;
        if (dict == null) return null;

        foreach (DictionaryEntry entry in dict)
        {
            if (entry.Key?.ToString()?.Equals(skillId, StringComparison.OrdinalIgnoreCase) == true)
                return entry.Value;
        }

        var skillType = FindType("Shared.Models.Skill.CharacterSkill");
        if (skillType == null) return null;
        var ctor = skillType.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, binder: null, new[] { typeof(string) }, modifiers: null);
        var skill = ctor != null ? ctor.Invoke(new object[] { skillId }) : Activator.CreateInstance(skillType);
        if (skill == null) return null;
        dict[skillId] = skill;
        return skill;
    }


    private static object? FindCitizenObjectForUpdate(object state, CitizenUpdate update)
    {
        var stateEntries = EnumerateStateEntries(state).ToList();
        foreach (var preferredName in new[] { "Citizens", "WildCitizens" })
        {
            var entry = stateEntries.FirstOrDefault(e => string.Equals(e.Name, preferredName, StringComparison.OrdinalIgnoreCase));
            var found = FindCitizenInValue(entry.Value, update, 6000);
            if (found != null) return found;
        }

        foreach (var entry in stateEntries.Where(e => (e.Name ?? "").Contains("Citizen", StringComparison.OrdinalIgnoreCase) || GetFriendlyTypeName(e.Value).Contains("Citizen", StringComparison.OrdinalIgnoreCase)))
        {
            var found = FindCitizenInValue(entry.Value, update, 6000);
            if (found != null) return found;
        }

        foreach (var entry in stateEntries)
        {
            var found = FindCitizenInValue(entry.Value, update, 12000);
            if (found != null) return found;
        }

        return null;
    }

    private static object? FindCitizenInValue(object? value, CitizenUpdate update, int maxObjects)
    {
        if (value == null || maxObjects <= 0) return null;
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<object>();
        queue.Enqueue(value);
        var scanned = 0;

        while (queue.Count > 0 && scanned < maxObjects)
        {
            var obj = queue.Dequeue();
            if (obj == null || obj is string) continue;
            if (!visited.Add(obj)) continue;
            scanned++;

            if (LooksLikeCitizen(obj) && CitizenMatchesUpdate(obj, update)) return obj;

            foreach (var (_, child) in EnumerateDictionary(obj))
            {
                if (child != null) queue.Enqueue(child);
            }
            foreach (var child in EnumerateSequence(obj))
            {
                if (child != null) queue.Enqueue(child);
            }
            foreach (var child in EnumerateObjectChildren(obj))
            {
                if (child != null) queue.Enqueue(child);
            }
        }

        return null;
    }

    private static bool CitizenMatchesUpdate(object obj, CitizenUpdate update)
    {
        var id = ReadMember(obj, "Id")?.ToString() ?? ReadMember(obj, "CitizenId")?.ToString() ?? "";
        var entityId = ReadMember(obj, "EntityId")?.ToString() ?? "";
        if (!string.IsNullOrWhiteSpace(update.CitizenId) && id.Equals(update.CitizenId, StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.IsNullOrWhiteSpace(update.EntityId) && entityId.Equals(update.EntityId, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static object? FindCitizenJobProgress(object citizen, string jobId)
    {
        var jobs = ReadMember(citizen, "JobExperience");
        foreach (var (key, value) in EnumerateDictionary(jobs))
        {
            var keyText = key?.ToString() ?? "";
            if (keyText.Equals(jobId, StringComparison.OrdinalIgnoreCase)) return value;
        }
        return null;
    }

    private static bool SetCitizenBaseStat(object citizen, string statId, float value)
    {
        var statsObj = ReadMember(citizen, "CitizenStats");
        if (statsObj == null) return false;

        var changed = SetCitizenStatsBoxedValue(statsObj, statId, value);
        if (changed)
        {
            try { SetMember(citizen, "CitizenStats", statsObj); } catch { }
        }
        return changed;
    }

    private static bool SetCitizenStatsBoxedValue(object statsObj, string statId, float value)
    {
        var stats = ReadMember(statsObj, "Stats");
        if (stats == null)
        {
            EnsureSharedDataSetup();
            var statType = FindType("Shared.Models.Stats.Stat");
            if (statType == null) return false;
            var dictType = typeof(Dictionary<,>).MakeGenericType(typeof(string), statType);
            stats = Activator.CreateInstance(dictType);
            try { SetMember(statsObj, "Stats", stats); } catch { return false; }
        }

        object? stat = null;
        foreach (var (key, candidate) in EnumerateDictionary(stats))
        {
            if ((key?.ToString() ?? "").Equals(statId, StringComparison.OrdinalIgnoreCase) ||
                (ReadMember(candidate, "StatId")?.ToString() ?? "").Equals(statId, StringComparison.OrdinalIgnoreCase))
            {
                stat = candidate;
                break;
            }
        }

        if (stat == null)
        {
            EnsureSharedDataSetup();
            var statType = FindType("Shared.Models.Stats.Stat");
            if (statType == null) return false;
            try { stat = Activator.CreateInstance(statType, statId); }
            catch { return false; }
            if (stats is IDictionary dict) dict[statId] = stat;
        }

        var oldValue = ToFloat(ReadMember(stat, "BaseValue"));
        if (Math.Abs(oldValue - value) <= 0.0001f) return false;

        var setBase = stat.GetType().GetMethod("SetBaseValue", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, binder: null, types: new[] { typeof(float) }, modifiers: null);
        if (setBase != null) setBase.Invoke(stat, new object[] { value });
        else
        {
            TrySetMember(stat, "BaseValue", value);
            TrySetMember(stat, "CalculatedValue", value);
        }

        var baseValues = ReadMember(statsObj, "_baseValues");
        if (baseValues == null)
        {
            baseValues = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            TrySetMember(statsObj, "_baseValues", baseValues);
        }
        if (baseValues is IDictionary baseDict) baseDict[statId] = value;
        return true;
    }

    private static bool ApplyCitizenTraits(object citizen, string traitIdsText)
    {
        EnsureCitizenAuraDatabase();

        var desired = new List<string>();
        foreach (var raw in SplitIdsForSave(traitIdsText))
        {
            var id = NormalizeCitizenAuraIdForSave(raw);
            if (string.IsNullOrWhiteSpace(id)) continue;
            if (!IsKnownCitizenTraitAuraId(id))
            {
                AppLogger.Error($"跳过非 Trait 或未知村民特质：{id}。为避免坏档，特质编辑只写入 CitizenAuraInfo.Type=Trait 的 aura。");
                continue;
            }
            if (!desired.Contains(id, StringComparer.OrdinalIgnoreCase)) desired.Add(id);
        }
        desired = desired.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();

        var current = ReadCurrentCitizenTraitIds(citizen)
            .Select(NormalizeCitizenAuraIdForSave)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (current.SequenceEqual(desired, StringComparer.OrdinalIgnoreCase)) return false;

        var wildList = ReadMember(citizen, "CitizenAuras");
        if (wildList is IList wildIList)
        {
            wildIList.Clear();
            foreach (var id in desired) wildIList.Add(id);
            return true;
        }

        var auras = ReadMember(citizen, "Auras");
        if (auras is IList auraList)
        {
            // 只移除真正的 Trait。不要再按 DataId 中的 :buff:/:debuff: 粗暴删除，
            // 因为 town_defence / cornucopia / loyalty / gift 等非 Trait 也可能是 buff/default aura。
            for (var i = auraList.Count - 1; i >= 0; i--)
            {
                var aura = auraList[i];
                if (IsCitizenTraitAuraObject(aura))
                    auraList.RemoveAt(i);
            }

            foreach (var id in desired)
            {
                if (TryAddCitizenAuraWithGameController(citizen, id)) continue;

                var aura = CreateCitizenAuraModel(id);
                if (aura != null) auraList.Add(aura);
                else AppLogger.Error($"无法创建村民特质：{id}。该 ID 会被跳过。");
            }

            if (!RecalculateCitizenStatsWithGameController(citizen))
                RecalculateCitizenStatsFromAuras(citizen);
            return true;
        }

        return false;
    }

    private static IEnumerable<string> ReadCurrentCitizenTraitIds(object citizen)
    {
        var wildList = ReadMember(citizen, "CitizenAuras");
        foreach (var item in EnumerateSequence(wildList))
        {
            var id = item?.ToString() ?? "";
            if (!string.IsNullOrWhiteSpace(id)) yield return id;
        }

        var auras = ReadMember(citizen, "Auras");
        foreach (var aura in EnumerateSequence(auras))
        {
            if (aura == null) continue;
            var type = ReadMember(aura, "Type")?.ToString() ?? "";
            var id = ReadMember(aura, "DataId")?.ToString() ?? "";
            if (!string.IsNullOrWhiteSpace(id) && type.Equals("Trait", StringComparison.OrdinalIgnoreCase)) yield return id;
        }
    }

    private static bool IsCitizenTraitAuraObject(object? aura)
    {
        if (aura == null) return false;
        var type = ReadMember(aura, "Type")?.ToString() ?? "";
        if (type.Equals("Trait", StringComparison.OrdinalIgnoreCase)) return true;
        var dataId = NormalizeCitizenAuraIdForSave(ReadMember(aura, "DataId")?.ToString());
        return IsKnownCitizenTraitAuraId(dataId);
    }

    private static bool IsKnownCitizenTraitAuraId(string? rawAuraId)
    {
        var auraId = NormalizeCitizenAuraIdForSave(rawAuraId);
        if (string.IsNullOrWhiteSpace(auraId)) return false;

        var auraInfo = GetCitizenAuraInfo(auraId);
        var type = auraInfo == null ? "" : (ReadMember(auraInfo, "Type")?.ToString() ?? "");
        if (type.Equals("Trait", StringComparison.OrdinalIgnoreCase)) return true;

        // Fallback for environments where the game's CitizenAuraDatabase could not be initialized.
        return auraId is
            "citizen_aura:debuff:food" or
            "citizen_aura:debuff:expertise" or
            "citizen_aura:debuff:efficiency" or
            "citizen_aura:debuff:experience" or
            "citizen_aura:debuff:loyalty" or
            "citizen_aura:debuff:happiness" or
            "citizen_aura:debuff:anxiety" or
            "citizen_aura:buff:expertise" or
            "citizen_aura:buff:efficiency" or
            "citizen_aura:buff:experience" or
            "citizen_aura:buff:loyalty" or
            "citizen_aura:buff:happiness";
    }

    private static bool TryAddCitizenAuraWithGameController(object citizen, string auraId)
    {
        try
        {
            EnsureCitizenAuraDatabase();
            var controllerType = FindType("CandideServer.ServerControllers.CitizensSController")
                                 ?? FindTypeByName("CitizensSController");
            if (controllerType == null)
            {
                AppLogger.Warn($"未找到游戏 CitizensSController，无法走原生 AddCitizenAura：{auraId}。将回退到本地构造。");
                return false;
            }

            var methods = controllerType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .Where(m => m.Name == "AddCitizenAura")
                .ToList();
            var method = methods.FirstOrDefault(m =>
            {
                var ps = m.GetParameters();
                return ps.Length == 5
                    && ps[0].ParameterType == typeof(Guid)
                    && ps[1].ParameterType == typeof(string)
                    && ps[3].ParameterType == typeof(bool)
                    && ps[4].ParameterType == typeof(bool)
                    && ps[2].ParameterType.IsAssignableFrom(citizen.GetType());
            }) ?? methods.FirstOrDefault(m =>
            {
                var ps = m.GetParameters();
                return ps.Length == 5
                    && ps[0].ParameterType == typeof(Guid)
                    && ps[1].ParameterType == typeof(string)
                    && ps[3].ParameterType == typeof(bool)
                    && ps[4].ParameterType == typeof(bool);
            });

            if (method == null)
            {
                AppLogger.Warn($"找到 {controllerType.FullName}，但没有匹配的 AddCitizenAura(Guid,string,citizen,bool,bool)。候选数量={methods.Count}。将回退到本地构造。");
                foreach (var m in methods.Take(8))
                    AppLogger.Warn("AddCitizenAura candidate: " + m);
                return false;
            }

            var before = ReadCurrentCitizenTraitIds(citizen).Select(NormalizeCitizenAuraIdForSave).ToList();
            method.Invoke(null, new object?[] { Guid.NewGuid(), auraId, citizen, false, false });
            var after = ReadCurrentCitizenTraitIds(citizen).Select(NormalizeCitizenAuraIdForSave).ToList();
            var ok = after.Contains(auraId, StringComparer.OrdinalIgnoreCase) || after.Count > before.Count;
            AppLogger.Info($"游戏原生 AddCitizenAura 调用{(ok ? "成功" : "未产生可见变化")}：{auraId} | {controllerType.FullName}.{method.Name} | before={string.Join(";", before)} | after={string.Join(";", after)}");
            return ok;
        }
        catch (TargetInvocationException ex)
        {
            var inner = ex.InnerException;
            AppLogger.Error($"通过游戏 CitizensSController.AddCitizenAura 添加特质失败，回退到本地构造：{auraId} | {inner?.GetType().Name ?? ex.GetType().Name}: {inner?.Message ?? ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"通过游戏 CitizensSController.AddCitizenAura 添加特质失败，回退到本地构造：{auraId} | {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static bool RecalculateCitizenStatsWithGameController(object citizen)
    {
        try
        {
            var controllerType = FindType("CandideServer.ServerControllers.CitizensSController")
                                 ?? FindTypeByName("CitizensSController");
            var method = controllerType?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "UpdateStats" && m.GetParameters().Length == 2);
            if (method == null)
            {
                AppLogger.Warn("未找到游戏 CitizensSController.UpdateStats(citizen, ...)，将回退到本地重算村民属性。");
                return false;
            }
            method.Invoke(null, new object?[] { citizen, null });
            AppLogger.Info($"游戏原生 UpdateStats 调用成功：{controllerType!.FullName}.{method.Name}");
            return true;
        }
        catch (TargetInvocationException ex)
        {
            var inner = ex.InnerException;
            AppLogger.Error($"通过游戏 CitizensSController.UpdateStats 重算村民属性失败，回退到本地重算：{inner?.GetType().Name ?? ex.GetType().Name}: {inner?.Message ?? ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"通过游戏 CitizensSController.UpdateStats 重算村民属性失败，回退到本地重算：{ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static object? CreateCitizenAuraModel(string auraId)
    {
        auraId = NormalizeCitizenAuraIdForSave(auraId);
        if (string.IsNullOrWhiteSpace(auraId)) return null;

        var modelType = FindType("Shared.Models.Auras.CitizenAuraModel");
        if (modelType == null) return null;

        var auraInfo = GetCitizenAuraInfo(auraId);
        var stats = auraInfo == null ? CreateCitizenAuraStatsDictionary(auraId) : ReadMember(auraInfo, "StatsToAdd");
        var isBuff = auraInfo != null && ToBool(ReadMember(auraInfo, "IsBuff"));
        var type = auraInfo == null ? GetCitizenAuraTypeValue("Trait") : ReadMember(auraInfo, "Type");
        var duration = auraInfo == null ? -1f : ToFloat(ReadMember(auraInfo, "Duration"));
        if (Math.Abs(duration) < 0.0001f) duration = -1f;
        var instanceTypeId = auraInfo == null ? null : ReadMember(auraInfo, "InstanceTypeId")?.ToString();

        try
        {
            return Activator.CreateInstance(modelType, Guid.NewGuid(), auraId, stats, isBuff, type!, duration, instanceTypeId, 0f, false);
        }
        catch
        {
            return null;
        }
    }

    private static bool EnsureCitizenAuraDatabase()
    {
        try
        {
            var dbType = FindType("Shared.Data.CitizenAuraDatabase");
            if (dbType == null) return false;
            var dataMap = GetStaticField(dbType, "DataMap");
            if ((TryGetCount(dataMap) ?? 0) > 0) return true;

            // The citizen aura setup lives in Shared.Data.Auras.SharedDataSetup, not Shared.Data.SharedDataSetup.
            // Earlier versions tried the wrong setup type, so newly-created CitizenAuraModel objects could miss
            // the game's canonical StatsToAdd / Type / InstanceTypeId payload.
            var auraSetupType = FindType("Shared.Data.Auras.SharedDataSetup");
            var setupMethod = auraSetupType?.GetMethod("SetupCitizenAuras", BindingFlags.Public | BindingFlags.Static);
            var addDataMethod = dbType.GetMethod("AddData", BindingFlags.Public | BindingFlags.Static);
            if (setupMethod == null || addDataMethod == null) return false;

            var auraList = setupMethod.Invoke(null, null);
            if (auraList == null) return false;
            addDataMethod.Invoke(null, new[] { auraList });
            return (TryGetCount(GetStaticField(dbType, "DataMap")) ?? 0) > 0;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"初始化游戏 CitizenAuraDatabase 失败：{ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static void EnsureSharedDataSetup()
    {
        if (SharedDataSetupAttempted) return;
        SharedDataSetupAttempted = true;
        try
        {
            var setupType = FindType("Shared.Data.SharedDataSetup");
            setupType?.GetMethod("Setup", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
        }
        catch
        {
            // Databases may already be initialized; keep save logic tolerant.
        }
    }

    private static object? GetCitizenAuraInfo(string auraId)
    {
        try
        {
            EnsureCitizenAuraDatabase();
            var dbType = FindType("Shared.Data.CitizenAuraDatabase");
            if (dbType == null) return null;
            var method = dbType.GetMethod("GetAuraOrNull", BindingFlags.Public | BindingFlags.Static);
            return method?.Invoke(null, new object?[] { auraId });
        }
        catch
        {
            return null;
        }
    }

    private static object? CreateCitizenAuraStatsDictionary(string auraId)
    {
        var dataType = FindType("Shared.Models.Stats.StatModificationData");
        if (dataType == null) return null;
        var dictType = typeof(Dictionary<,>).MakeGenericType(typeof(string), dataType);
        var dict = (IDictionary?)Activator.CreateInstance(dictType);
        if (dict == null) return null;

        foreach (var effect in GetCitizenAuraEffects(auraId))
        {
            var data = Activator.CreateInstance(dataType)!;
            TrySetMember(data, "Additive", effect.Additive);
            TrySetMember(data, "Multiplier", effect.Multiplier);
            TrySetMember(data, "BaseMultiplier", effect.BaseMultiplier);
            TrySetMember(data, "AdditiveMultiplier", effect.AdditiveMultiplier);
            TrySetMember(data, "BonusMultiplier", effect.BonusMultiplier);
            dict[effect.StatId] = data;
        }
        return dict;
    }

    private static object? GetCitizenAuraTypeValue(string name)
    {
        var type = FindType("Shared.Models.Auras.CitizenAuraType");
        if (type == null) return null;
        return Enum.Parse(type, name, ignoreCase: true);
    }

    private static void RecalculateCitizenStatsFromAuras(object citizen)
    {
        var statsObj = ReadMember(citizen, "CitizenStats");
        if (statsObj == null) return;
        try { statsObj.GetType().GetMethod("ResetModifiers", BindingFlags.Instance | BindingFlags.Public)?.Invoke(statsObj, null); } catch { }
        var auras = ReadMember(citizen, "Auras");
        foreach (var aura in EnumerateSequence(auras))
        {
            if (aura == null) continue;
            var id = ReadMember(aura, "Id");
            var addAuraStats = statsObj.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(m => m.Name == "AddAuraStats" && m.GetParameters().Length == 2);
            try { addAuraStats?.Invoke(statsObj, new[] { id, aura }); } catch { }
        }
        try { SetMember(citizen, "CitizenStats", statsObj); } catch { }
    }

    private static IEnumerable<string> SplitIdsForSave(string value)
    {
        return (value ?? "").Split(new[] { ';', ',', '|', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string NormalizeCitizenAuraIdForSave(string? rawAuraId)
    {
        var id = (rawAuraId ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(id)) return "";
        if (id.StartsWith("citizen_aura:", StringComparison.OrdinalIgnoreCase)) return id;
        if (id.StartsWith("trait:", StringComparison.OrdinalIgnoreCase)) return "citizen_aura:" + id[6..];
        if (id is "efficiency" or "expertise" or "experience" or "loyalty" or "happiness") return "citizen_aura:buff:" + id;
        if (id is "food") return "citizen_aura:debuff:food";
        return id;
    }

    private static bool ToBool(object? value)
    {
        if (value == null) return false;
        try { return Convert.ToBoolean(value, CultureInfo.InvariantCulture); }
        catch { return false; }
    }

    private static bool TrySetMember(object? obj, string name, object? value)
    {
        try
        {
            SetMember(obj, name, value);
            return true;
        }
        catch
        {
            return false;
        }
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


    private static (string BackupDirectory, int BackupIndex) BackupInputDataFiles(string inputDir, string backupDir)
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
        var files = Directory.EnumerateFiles(inputDir, "*", SearchOption.AllDirectories)
            .Where(f => !IsInsideDirectory(f, backupDir))
            .Where(f =>
            {
                var name = Path.GetFileName(f);
                return name.Equals("game_state", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("game_state.", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("world_desc", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("world_desc.", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".char", StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var source in files)
        {
            var originalName = Path.GetFileNameWithoutExtension(source);
            var ext = Path.GetExtension(source);
            var backupName = $"backup{today}-{index}-{originalName}{ext}";
            var dest = Path.Combine(backupDir, backupName);
            var n = 1;
            while (File.Exists(dest))
            {
                backupName = $"backup{today}-{index}-{originalName}-{n}{ext}";
                dest = Path.Combine(backupDir, backupName);
                n++;
            }
            File.Copy(source, dest, overwrite: false);
            AppLogger.Info($"已备份存档文件：{source} -> {dest}");
        }

        if (files.Count == 0)
            AppLogger.Info("未在 input 中找到 game_state/world_desc/.char；没有创建世界备份。 ");

        return (backupDir, index);
    }

    private static bool IsInsideDirectory(string file, string dir)
    {
        var fullFile = Path.GetFullPath(file).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullDir = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullFile.StartsWith(fullDir, StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteWorldWriteNativeFailureReport(string outputDir, string gameStateFile, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(outputDir);
            var report = new
            {
                Passed = false,
                Stage = "native-server-state-serialization",
                GameState = Path.GetFileName(gameStateFile),
                ErrorType = ex.GetType().FullName,
                Message = ex.Message,
                InnerErrorType = ex.InnerException?.GetType().FullName,
                InnerMessage = ex.InnerException?.Message,
                Note = "Native writer failed before final world_write_validation_report/world_write_entry_size_compare could be generated."
            };
            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(outputDir, "world_write_native_failure_report.json"), json, Encoding.UTF8);
            File.WriteAllText(Path.Combine(outputDir, "world_write_native_failure_report.csv"),
                "Passed,Stage,GameState,ErrorType,Message" + Environment.NewLine +
                $"false,native-server-state-serialization,{CsvEscape(Path.GetFileName(gameStateFile))},{CsvEscape(ex.GetType().FullName ?? string.Empty)},{CsvEscape(ex.Message)}" + Environment.NewLine,
                Encoding.UTF8);
        }
        catch
        {
            // Do not hide the original save failure if diagnostics cannot be written.
        }
    }

    private static string? ResolveInputFile(string inputDir, string sourceFile)
    {
        if (Path.IsPathRooted(sourceFile) && File.Exists(sourceFile)) return sourceFile;

        var direct = Path.Combine(inputDir, sourceFile);
        if (File.Exists(direct)) return direct;

        var wanted = NormalizeSearchText(sourceFile);
        var wantedFileName = NormalizeSearchText(Path.GetFileName(sourceFile));
        var wantedFileNameNoExt = NormalizeSearchText(Path.GetFileNameWithoutExtension(sourceFile));

        return Directory.EnumerateFiles(inputDir, "*", SearchOption.AllDirectories)
            .FirstOrDefault(f =>
            {
                var full = NormalizeSearchText(f);
                var fileName = NormalizeSearchText(Path.GetFileName(f));
                var fileNameNoExt = NormalizeSearchText(Path.GetFileNameWithoutExtension(f));

                return full.Equals(wanted, StringComparison.OrdinalIgnoreCase)
                       || fileName.Equals(wanted, StringComparison.OrdinalIgnoreCase)
                       || fileName.Equals(wantedFileName, StringComparison.OrdinalIgnoreCase)
                       || fileNameNoExt.Equals(wanted, StringComparison.OrdinalIgnoreCase)
                       || fileNameNoExt.Equals(wantedFileNameNoExt, StringComparison.OrdinalIgnoreCase);
            });
    }


    private static byte[] SerializeGameStateToBytesUsingNativeServerState(object state, byte[] originalFileBytes, bool gzipOutput, out string note)
    {
        // v63: Do NOT call GameSaveManager.SaveGameState(false). That method touches BaseServer.Instance
        // and initializes the live server/event bus, which in turn requires Steamworks.NET. For save editing we
        // only need the game serializer context used by the custom WorldTiles/Difficulties serializers.
        //
        // A crucial detail from v62 diagnostics: some loaded saves can expose the three large array entries
        // with stale/shifted names, e.g. BiomeIds carrying a System.Single[,] value. When the serializer writes
        // those names back as-is, WorldTiles/BiomeIds/Difficulties are silently written as tiny placeholder
        // blocks or assigned to the wrong entry. Canonicalize the state-list entry names by runtime value type
        // before priming ServerGameState statics and before serializing. This keeps the writer independent from
        // BaseServer while still using the game's own ITypeSerializer implementations.
        var normalizedState = NormalizeGameStateEntriesForNativeLoad(state, out var normalizationNote);
        if (!string.IsNullOrWhiteSpace(normalizationNote))
        {
            AppLogger.Info("Native direct full-state writer entry normalization: " + normalizationNote);
        }

        PrimeNativeSerializerContextForDirectStateSerialization(normalizedState, out var contextNote);
        var arraySummary = DescribeStateWorldArrayEntries(normalizedState);
        AppLogger.Info("Native direct full-state writer: " + contextNote + "; " + arraySummary);

        var bytes = SerializeGameStateToBytesSeededWithOriginalStringMap(normalizedState, originalFileBytes, gzipOutput, out var seededNote);
        note = normalizationNote + "; " + contextNote + "; " + arraySummary + "; serialized normalized edited state list with original string-map IDs seeded; " + seededNote + "; BaseServer.SaveGameState bypassed";
        return bytes;
    }


    private static byte[] SerializeGameStateToBytesSeededWithOriginalStringMap(object normalizedState, byte[] originalFileBytes, bool gzipOutput, out string note)
    {
        note = "";
        var originalPayload = GetPossiblyGzippedPayloadBytes(originalFileBytes);
        if (!TryExtractTopLevelStateEntrySpansFromPayload(originalPayload, out var originalSpans, out var originalSpanNote) || originalSpans.Count == 0)
        {
            throw new InvalidOperationException("无法解析原始 game_state entry spans，不能 seed 原始 string map。" + originalSpanNote);
        }

        var originalHeaderLength = originalSpans[0].Offset;
        if (!TryReadStringMapHeader(originalPayload, originalHeaderLength, out var originalHeader, out var originalHeaderNote) || !originalHeader.HasDirectStringMap)
        {
            throw new InvalidOperationException("无法解析原始 game_state string map，不能 seed 原始 string map。" + originalHeaderNote);
        }

        var payload = SerializePayloadWithSeededStringMap(GetGameStateListType(), normalizedState, originalHeader.Strings, out var seededNote);
        note = seededNote;
        return WrapPayloadWithCompression(payload, gzipOutput);
    }

    private static byte[] SerializePayloadWithSeededStringMap(Type rootType, object? source, IReadOnlyList<string> seedStrings, out string note)
    {
        note = "";
        var gameSaveManagerType = FindType("CandideServer.Saving.GameSaveManager")
            ?? throw new InvalidOperationException("Could not find CandideServer.Saving.GameSaveManager. Is CandideServer.dll in lib/?");

        var reflection = GetStaticField(gameSaveManagerType, "GameStateSaveReflection")
            ?? throw new InvalidOperationException("GameSaveManager.GameStateSaveReflection is null.");
        var serializer = GetStaticField(gameSaveManagerType, "GameStateSaveSerializer")
            ?? throw new InvalidOperationException("GameSaveManager.GameStateSaveSerializer is null.");

        var stringMap = GetSerializerStringMap(serializer);
        var visited = GetSerializerVisitedCollection(serializer);
        var syncRoot = visited ?? (object)serializer;

        lock (syncRoot)
        {
            ClearCollectionLike(visited);
            stringMap.Clear();
            for (var i = 0; i < seedStrings.Count; i++)
            {
                // Keep the original compact string-map ID space exactly stable.
                // v68 skipped empty strings while seeding the serializer dictionary. Some saves contain
                // an empty string in the direct string map; skipping it creates a one-slot ID gap, so all
                // later raw-preserved StateEntry wrappers can point at the wrong string when the map is
                // written back compacted. Empty strings are valid dictionary keys and must be preserved.
                var value = seedStrings[i] ?? string.Empty;
                if (!stringMap.Contains(value))
                    stringMap.Add(value, i + 1);
            }

            if (!TrySetSerializerNextStringId(serializer, seedStrings.Count + 1, out var nextIdNote))
            {
                AppLogger.Warn("GameStateSaveSerializer next string id was not explicitly set: " + nextIdNote);
            }

            if (source == null)
            {
                using var emptyOutput = new MemoryStream();
                using var emptyWriter = new BinaryWriter(emptyOutput, Encoding.UTF8, leaveOpen: true);
                emptyWriter.Write(6448483u);
                emptyWriter.Write((byte)0);
                emptyWriter.Flush();
                note = "seeded original string map not used because source was null";
                return emptyOutput.ToArray();
            }

            using var body = new MemoryStream();
            var extendedBinaryWriterType = FindType("CandideCreator.Shared.Storage.ExtendedBinaryWriter")
                ?? throw new InvalidOperationException("ExtendedBinaryWriter type not found.");
            var bodyWriter = Activator.CreateInstance(extendedBinaryWriterType, new object?[] { body })
                ?? throw new InvalidOperationException("Failed to construct ExtendedBinaryWriter.");

            var getTypeHandlerMethod = reflection.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(m => m.Name == "GetTypeHandler" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(Type))
                ?? throw new MissingMethodException(reflection.GetType().FullName, "GetTypeHandler(Type)");
            var typeHandler = getTypeHandlerMethod.Invoke(reflection, new object?[] { rootType })
                ?? throw new InvalidOperationException("GetTypeHandler returned null for " + rootType.FullName);

            var writeTypeCodeMethod = serializer.GetType().GetMethod("WriteTypeCode", BindingFlags.Instance | BindingFlags.Public)
                ?? throw new MissingMethodException(serializer.GetType().FullName, "WriteTypeCode");
            writeTypeCodeMethod.Invoke(serializer, new object?[] { reflection, bodyWriter, typeHandler, rootType });

            var serializeMethod = typeHandler.GetType().GetMethod("Serialize", BindingFlags.Instance | BindingFlags.Public)
                ?? throw new MissingMethodException(typeHandler.GetType().FullName, "Serialize");
            serializeMethod.Invoke(typeHandler, new object?[] { reflection, serializer, bodyWriter, source });
            // Do not dispose ExtendedBinaryWriter here: in the game build it owns and closes the
            // underlying MemoryStream. v67 disposed it before reading the body, which caused
            // ObjectDisposedException: Cannot access a closed Stream. Flush if possible, then
            // read the MemoryStream while it is still open.
            FlushWriterIfPossible(bodyWriter);

            using var output = new MemoryStream();
            using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
            if (stringMap.Count > 0)
            {
                writer.Write(23225699u);
                var entries = new List<(int Id, string Value)>();
                foreach (DictionaryEntry entry in stringMap)
                {
                    if (entry.Key is string key && entry.Value is int id && id > 0)
                        entries.Add((id, key));
                }

                entries.Sort((a, b) => a.Id.CompareTo(b.Id));

                // Write the original direct string map by ID position, not by the currently-present
                // dictionary entry count. The serializer dictionary is string->id and cannot represent
                // duplicate/gap semantics perfectly; the save header, however, is a positional direct
                // string array. Starting from the original header preserves every old compact string ID.
                // New strings discovered during serialization are appended by their assigned IDs.
                var finalStrings = seedStrings.Select(x => x ?? string.Empty).ToList();
                foreach (var entry in entries)
                {
                    if (entry.Id <= 0) continue;
                    while (finalStrings.Count < entry.Id)
                        finalStrings.Add(string.Empty);
                    if (entry.Id > seedStrings.Count)
                        finalStrings[entry.Id - 1] = entry.Value ?? string.Empty;
                }

                // The game writes the compact-string map as an array of direct strings:
                // [count][element type 33][strings ordered by compact id].
                Write7BitEncodedInt(output, finalStrings.Count);
                writer.Write((byte)33);
                foreach (var value in finalStrings)
                    writer.Write(value ?? string.Empty);

                var maxId = finalStrings.Count;
                var appended = Math.Max(0, finalStrings.Count - seedStrings.Count);
                note = $"seeded original string map IDs. originalStrings={seedStrings.Count:N0}, finalStrings={finalStrings.Count:N0}, maxId={maxId:N0}, appendedOrNewStrings={appended:N0}";
            }
            else
            {
                writer.Write(6448483u);
                note = "no compact string map was produced";
            }

            writer.Flush();
            body.Position = 0;
            body.CopyTo(output);
            return output.ToArray();
        }
    }


    private static IDictionary GetSerializerStringMap(object serializer)
    {
        var serializerType = serializer.GetType();
        foreach (var field in EnumerateInstanceFields(serializerType))
        {
            object? value;
            try { value = field.GetValue(serializer); }
            catch { continue; }
            if (value is not IDictionary dict) continue;

            if (string.Equals(field.Name, "StringMap", StringComparison.OrdinalIgnoreCase))
                return dict;

            if (LooksLikeStringToIntDictionary(dict))
                return dict;
        }

        throw new InvalidOperationException("GameStateSaveSerializer string map dictionary is not accessible. Fields=" + DescribeInstanceFields(serializerType));
    }

    private static ICollection? GetSerializerVisitedCollection(object serializer)
    {
        var serializerType = serializer.GetType();
        foreach (var field in EnumerateInstanceFields(serializerType))
        {
            object? value;
            try { value = field.GetValue(serializer); }
            catch { continue; }
            if (value is not ICollection collection || value is IDictionary) continue;

            if (string.Equals(field.Name, "Visited", StringComparison.OrdinalIgnoreCase))
                return collection;
        }

        foreach (var field in EnumerateInstanceFields(serializerType))
        {
            object? value;
            try { value = field.GetValue(serializer); }
            catch { continue; }
            if (value is not ICollection collection || value is IDictionary) continue;
            var t = value.GetType();
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(HashSet<>))
                return collection;
        }

        return null;
    }


    private static void FlushWriterIfPossible(object writer)
    {
        try
        {
            var flush = writer.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => string.Equals(m.Name, "Flush", StringComparison.Ordinal) && m.GetParameters().Length == 0);
            flush?.Invoke(writer, null);
        }
        catch
        {
            // Some game writer implementations do not expose Flush. The backing MemoryStream is
            // still valid as long as we avoid disposing the wrapper before copying the bytes.
        }
    }

    private static IEnumerable<FieldInfo> EnumerateInstanceFields(Type type)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        for (var t = type; t != null; t = t.BaseType)
        {
            foreach (var field in t.GetFields(flags))
                yield return field;
        }
    }

    private static bool LooksLikeStringToIntDictionary(IDictionary dict)
    {
        if (dict.Count == 0) return true;
        var inspected = 0;
        foreach (DictionaryEntry entry in dict)
        {
            inspected++;
            if (entry.Key is not string) return false;
            if (entry.Value is not int) return false;
            if (inspected >= 8) break;
        }
        return true;
    }

    private static void ClearCollectionLike(ICollection? collection)
    {
        if (collection == null) return;
        var clear = collection.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(m => string.Equals(m.Name, "Clear", StringComparison.Ordinal) && m.GetParameters().Length == 0);
        clear?.Invoke(collection, null);
    }

    private static bool TrySetSerializerNextStringId(object serializer, int nextId, out string note)
    {
        var serializerType = serializer.GetType();
        var fields = EnumerateInstanceFields(serializerType)
            .Where(f => f.FieldType == typeof(int))
            .ToList();

        var preferred = fields.FirstOrDefault(f => string.Equals(f.Name, "_nextId", StringComparison.OrdinalIgnoreCase))
            ?? fields.FirstOrDefault(f => f.Name.Contains("next", StringComparison.OrdinalIgnoreCase) && f.Name.Contains("id", StringComparison.OrdinalIgnoreCase))
            ?? fields.FirstOrDefault(f => f.Name.Contains("next", StringComparison.OrdinalIgnoreCase));

        if (preferred == null && fields.Count == 1)
            preferred = fields[0];

        if (preferred == null)
        {
            note = "no suitable int field found. IntFields=" + string.Join(",", fields.Select(f => f.Name));
            return false;
        }

        try
        {
            preferred.SetValue(serializer, nextId);
            note = $"set {preferred.Name}={nextId:N0}";
            return true;
        }
        catch (Exception ex)
        {
            note = $"failed to set {preferred.Name}: {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private static string DescribeInstanceFields(Type type)
    {
        try
        {
            return string.Join("; ", EnumerateInstanceFields(type).Select(f => $"{f.Name}:{f.FieldType.FullName}"));
        }
        catch
        {
            return type.FullName ?? type.Name;
        }
    }

    private static void PrimeNativeSerializerContextForDirectStateSerialization(object state, out string note)
    {
        var notes = new List<string>();
        var serverGameStateType = FindType("CandideServer.ServerGameState");
        if (serverGameStateType == null)
        {
            note = "ServerGameState not found; serializer context not primed";
            return;
        }

        object? config = null;
        object? worldTiles = null;
        object? biomeIds = null;
        object? difficulties = null;

        foreach (var entry in EnumerateGameStateEntries(state))
        {
            if (string.Equals(entry.Name, "Config", StringComparison.Ordinal))
            {
                config = entry.Value;
                continue;
            }

            if (entry.Value is not Array array || array.Rank != 2) continue;
            var elementType = array.GetType().GetElementType();
            if (elementType == typeof(string)) biomeIds = entry.Value;
            else if (elementType == typeof(float) || elementType == typeof(Single)) difficulties = entry.Value;
            else if (elementType != null && string.Equals(elementType.Name, "WorldTile", StringComparison.Ordinal)) worldTiles = entry.Value;
        }

        if (config != null && TrySetStaticField(serverGameStateType, "Config", config))
        {
            notes.Add("Config.GenerateWorld=" + (ReadMember(config, "GenerateWorld")?.ToString() ?? "<unknown>"));
        }
        else
        {
            notes.Add("Config not set");
        }

        if (worldTiles != null && TrySetStaticField(serverGameStateType, "WorldTiles", worldTiles)) notes.Add("WorldTiles=" + DescribeArrayShape((Array)worldTiles));
        if (biomeIds != null && TrySetStaticField(serverGameStateType, "BiomeIds", biomeIds)) notes.Add("BiomeIds=" + DescribeArrayShape((Array)biomeIds));
        if (difficulties != null && TrySetStaticField(serverGameStateType, "Difficulties", difficulties)) notes.Add("Difficulties=" + DescribeArrayShape((Array)difficulties));

        note = "primed serializer context: " + string.Join(", ", notes);
    }

    private static string DescribeStateWorldArrayEntries(object state)
    {
        var parts = new List<string>();
        foreach (var entry in EnumerateGameStateEntries(state))
        {
            if (entry.Value is not Array array || array.Rank != 2) continue;
            var elementType = array.GetType().GetElementType();
            if (elementType == typeof(string) || elementType == typeof(float) || elementType == typeof(Single) || (elementType != null && string.Equals(elementType.Name, "WorldTile", StringComparison.Ordinal)))
            {
                parts.Add($"{entry.Name}={DescribeValueType(entry.Value)}");
            }
        }
        return parts.Count == 0 ? "state world arrays: none" : "state world arrays: " + string.Join("; ", parts);
    }

    private static object NormalizeGameStateEntriesForNativeLoad(object state, out string note)
    {
        note = "";
        if (state is not IEnumerable enumerable) return state;

        var stateType = state.GetType();
        if (!stateType.IsGenericType) return state;

        var entryType = stateType.GetGenericArguments().FirstOrDefault();
        if (entryType == null) return state;

        var normalizedList = Activator.CreateInstance(stateType);
        var addMethod = stateType.GetMethod("Add", [entryType]);
        if (normalizedList == null || addMethod == null) return state;

        var corrections = new List<string>();
        foreach (var entry in enumerable)
        {
            if (entry == null) continue;
            var t = entry.GetType();
            var oldName = t.GetField("Item1")?.GetValue(entry)?.ToString()
                          ?? t.GetProperty("Name")?.GetValue(entry)?.ToString()
                          ?? "<unknown>";
            var value = t.GetField("Item2")?.GetValue(entry)
                        ?? t.GetProperty("Value")?.GetValue(entry);
            var newName = GetCanonicalGameStateEntryName(oldName, value);
            if (!string.Equals(oldName, newName, StringComparison.Ordinal))
            {
                corrections.Add($"{oldName}->{newName} ({DescribeValueType(value)})");
            }

            var normalizedEntry = Activator.CreateInstance(entryType, newName, value);
            addMethod.Invoke(normalizedList, [normalizedEntry]);
        }

        if (corrections.Count > 0)
        {
            note = "corrected known array entry names for native LoadGameState: " + string.Join(", ", corrections);
        }
        else
        {
            note = "known array entry names already canonical";
        }
        return normalizedList;
    }

    private static string GetCanonicalGameStateEntryName(string currentName, object? value)
    {
        if (value is Array array && array.Rank == 2)
        {
            var elementType = array.GetType().GetElementType();
            if (elementType == typeof(string)) return "BiomeIds";
            if (elementType == typeof(float) || elementType == typeof(Single)) return "Difficulties";
            if (elementType != null && string.Equals(elementType.Name, "WorldTile", StringComparison.Ordinal)) return "WorldTiles";
        }
        return currentName;
    }

    private static string DescribeValueType(object? value)
    {
        if (value == null) return "null";
        if (value is Array array) return value.GetType().FullName + " shape=" + DescribeArrayShape(array);
        return value.GetType().FullName ?? value.GetType().Name;
    }

    private static void LoadGameStateIntoNativeServerStatics(object state, out string note)
    {
        note = "";
        var gameSaveManagerType = FindType("CandideServer.Saving.GameSaveManager")
            ?? throw new InvalidOperationException("Could not find CandideServer.Saving.GameSaveManager.");

        var loadGameState = gameSaveManagerType.GetMethod("LoadGameState", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(gameSaveManagerType.FullName, "LoadGameState(List<(string, object)>)");

        try
        {
            loadGameState.Invoke(null, new object?[] { state });
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            throw new InvalidOperationException(tie.InnerException.Message, tie.InnerException);
        }

        var serverGameStateType = FindType("CandideServer.ServerGameState");
        var config = serverGameStateType?.GetField("Config", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
        var generateWorld = ReadMember(config, "GenerateWorld");
        var worldTiles = serverGameStateType?.GetField("WorldTiles", BindingFlags.Static | BindingFlags.Public)?.GetValue(null) as Array;
        var biomeIds = serverGameStateType?.GetField("BiomeIds", BindingFlags.Static | BindingFlags.Public)?.GetValue(null) as Array;
        var difficulties = serverGameStateType?.GetField("Difficulties", BindingFlags.Static | BindingFlags.Public)?.GetValue(null) as Array;
        note = $"LoadGameState invoked; GenerateWorld={generateWorld}; WorldTiles={DescribeArrayShape(worldTiles)}; BiomeIds={DescribeArrayShape(biomeIds)}; Difficulties={DescribeArrayShape(difficulties)}";
        AppLogger.Info("Native server state loaded for save: " + note);
    }


    private static void ForcePopulateKnownWorldArrayStaticsFromState(object state, out string note)
    {
        note = "";
        var serverGameStateType = FindType("CandideServer.ServerGameState");
        if (serverGameStateType == null)
        {
            note = "ServerGameState not found; skipped static world array repair";
            return;
        }

        object? worldTiles = null;
        object? biomeIds = null;
        object? difficulties = null;

        foreach (var entry in EnumerateGameStateEntries(state))
        {
            var value = entry.Value;
            if (value is not Array array || array.Rank != 2) continue;
            var elementType = array.GetType().GetElementType();
            if (elementType == typeof(string)) biomeIds = value;
            else if (elementType == typeof(float) || elementType == typeof(Single)) difficulties = value;
            else if (elementType != null && string.Equals(elementType.Name, "WorldTile", StringComparison.Ordinal)) worldTiles = value;
        }

        var changes = new List<string>();
        if (worldTiles != null && TrySetStaticField(serverGameStateType, "WorldTiles", worldTiles)) changes.Add("WorldTiles=" + DescribeArrayShape((Array)worldTiles));
        if (biomeIds != null && TrySetStaticField(serverGameStateType, "BiomeIds", biomeIds)) changes.Add("BiomeIds=" + DescribeArrayShape((Array)biomeIds));
        if (difficulties != null && TrySetStaticField(serverGameStateType, "Difficulties", difficulties)) changes.Add("Difficulties=" + DescribeArrayShape((Array)difficulties));

        note = changes.Count == 0
            ? "no world array statics repaired from state entries"
            : "repaired ServerGameState world array statics from state entries: " + string.Join(", ", changes);
        AppLogger.Info("Native server state static repair: " + note);
    }

    private static bool TrySetStaticField(Type type, string fieldName, object value)
    {
        try
        {
            var field = type.GetField(fieldName, BindingFlags.Static | BindingFlags.Public);
            if (field == null) return false;
            if (!field.FieldType.IsInstanceOfType(value)) return false;
            field.SetValue(null, value);
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Failed to set ServerGameState.{fieldName}: {ex.Message}");
            return false;
        }
    }

    private static object BuildNativeGameStateFromServerStatics(out string note)
    {
        var gameSaveManagerType = FindType("CandideServer.Saving.GameSaveManager")
            ?? throw new InvalidOperationException("Could not find CandideServer.Saving.GameSaveManager.");

        var saveGameState = gameSaveManagerType.GetMethod("SaveGameState", BindingFlags.Static | BindingFlags.Public)
            ?? throw new MissingMethodException(gameSaveManagerType.FullName, "SaveGameState(bool)");

        try
        {
            var nativeState = saveGameState.Invoke(null, new object?[] { false })
                ?? throw new InvalidOperationException("GameSaveManager.SaveGameState(false) returned null.");
            var count = nativeState is IEnumerable e ? e.Cast<object>().Count() : -1;
            note = $"native SaveGameState(false) returned {count} state entries";
            AppLogger.Info("Native game state built for save: " + note);
            return nativeState;
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            throw new InvalidOperationException(tie.InnerException.Message, tie.InnerException);
        }
    }

    private static IEnumerable<(string Name, object? Value)> EnumerateGameStateEntries(object state)
    {
        if (state is not IEnumerable enumerable) yield break;
        foreach (var entry in enumerable)
        {
            if (entry == null) continue;
            var t = entry.GetType();
            var name = t.GetField("Item1")?.GetValue(entry)?.ToString()
                       ?? t.GetProperty("Name")?.GetValue(entry)?.ToString()
                       ?? "";
            var value = t.GetField("Item2")?.GetValue(entry)
                        ?? t.GetProperty("Value")?.GetValue(entry);
            yield return (name, value);
        }
    }

    private static void CopyWorldWriteReportsToOutputRoot(string tempOutputDir, string outputDir, string validationRoot, bool success)
    {
        try
        {
            Directory.CreateDirectory(outputDir);
            if (!Directory.Exists(tempOutputDir)) return;

            var copied = new List<string>();
            foreach (var file in Directory.EnumerateFiles(tempOutputDir, "world_write*.*", SearchOption.TopDirectoryOnly)
                         .Concat(Directory.EnumerateFiles(tempOutputDir, "world_desc_binding_report.*", SearchOption.TopDirectoryOnly))
                         .Concat(Directory.EnumerateFiles(tempOutputDir, "citizen_entity_binding_report.*", SearchOption.TopDirectoryOnly))
                         .Concat(Directory.EnumerateFiles(tempOutputDir, "citizen_aura_runtime_report.*", SearchOption.TopDirectoryOnly))
                         .Concat(Directory.EnumerateFiles(tempOutputDir, "citizen_trait_deep_diff.*", SearchOption.TopDirectoryOnly)))
            {
                var dest = Path.Combine(outputDir, Path.GetFileName(file));
                File.Copy(file, dest, overwrite: true);
                copied.Add(Path.GetFileName(file));
            }

            var marker = new
            {
                Passed = success,
                Stage = "preflight-report-copy",
                TempValidationRoot = validationRoot,
                TempOutput = tempOutputDir,
                CopiedReports = copied,
                Note = copied.Count == 0
                    ? "No world_write reports were produced before the failure. Check logs/latest.log for the earlier exception."
                    : "Reports were copied from the preflight temp output before cleanup."
            };
            File.WriteAllText(Path.Combine(outputDir, "world_write_preflight_report_copy.json"), JsonSerializer.Serialize(marker, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
            AppLogger.Info($"世界写入预验证报告已复制到 output：{copied.Count} 个文件。" + (copied.Count == 0 ? " 保存流程在生成报告前失败。" : ""));
        }
        catch (Exception ex)
        {
            AppLogger.Warn("复制世界写入预验证报告失败：" + ex.Message);
        }
    }

    private static string DescribeArrayShape(Array? array)
    {
        if (array == null) return "null";
        var parts = new List<string>();
        for (var i = 0; i < array.Rank; i++) parts.Add(array.GetLength(i).ToString(CultureInfo.InvariantCulture));
        return string.Join("x", parts);
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
        var ok = TryDeserializeGameStateWithFormat(bytes, out state, out _, out note);
        return ok;
    }

    private static bool TryDeserializeGameStateWithFormat(byte[] bytes, out object? state, out bool wasGzip, out string note)
    {
        state = null;
        wasGzip = false;
        note = "";
        try
        {
            using var stream = OpenBytesPossiblyGzipped(bytes, out wasGzip);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            var decompressedLength = memory.Length;
            memory.Position = 0;
            state = DeserializeWithType(memory, GetGameStateListType());
            note = wasGzip ? $"game_state gzip bytes={decompressedLength}" : $"game_state raw bytes={decompressedLength}";
            return state != null;
        }
        catch (Exception ex)
        {
            note = "game_state no: " + ex.GetType().Name + " | " + ex.Message;
            return false;
        }
    }

    private static bool TryDeserializeWorldDescriptionWithFormat(byte[] bytes, out object? worldDescription, out bool wasGzip, out string note)
    {
        worldDescription = null;
        wasGzip = false;
        note = "";
        try
        {
            var worldDescriptionType = FindType("CandideServer.GameDescription")
                ?? throw new InvalidOperationException("Could not find CandideServer.GameDescription.");
            using var stream = OpenBytesPossiblyGzipped(bytes, out wasGzip);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            var decodedLength = memory.Length;
            memory.Position = 0;
            worldDescription = DeserializeWithType(memory, worldDescriptionType);
            note = wasGzip ? $"world_desc gzip bytes={decodedLength}" : $"world_desc raw bytes={decodedLength}";
            return worldDescription != null;
        }
        catch (Exception ex)
        {
            note = "world_desc no: " + ex.GetType().Name + " | " + ex.Message;
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
        return deserializeMethod.Invoke(deserializer, new object[] { reflection, stream, type, true });
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
            throw new InvalidOperationException("No DLL files found in the dependency directory. Required: CandideServer.dll, Shared.dll, CandideCreator.Shared.dll, MonoGame.Framework.dll.");
        }

        var required = new[] { "CandideCreator.Shared", "Shared", "MonoGame.Framework", "CandideServer" };
        var blockedDllNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "steam_api", "steam_api64", "Steamworks.NET",
            "winmm", "version", "OnlineFix", "OnlineFix64", "SteamFix", "SteamFix64",
        };

        var byName = dlls
            .GroupBy(d => Path.GetFileNameWithoutExtension(d) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        DllBySimpleName.Clear();
        var blockedCount = 0;
        foreach (var kv in byName)
        {
            var simple = kv.Key;
            if (blockedDllNames.Contains(simple) || simple.Contains("steamfix", StringComparison.OrdinalIgnoreCase) || simple.Contains("onlinefix", StringComparison.OrdinalIgnoreCase))
            {
                blockedCount++;
                continue;
            }

            // Register every safe DLL for on-demand dependency resolution, but only eagerly load
            // the four game serializer assemblies. This keeps SteamFix/OnlineFix blocked while
            // allowing single-exe dist builds to resolve harmless runtime dependencies such as
            // Google.Protobuf, LiteNetLib, SharpDX, YarnSpinner, etc.
            DllBySimpleName[simple] = kv.Value;
        }

        var missing = required.Where(name => !DllBySimpleName.ContainsKey(name)).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException("Missing required game DLL(s): " + string.Join(", ", missing.Select(x => x + ".dll")) + ". Do not copy SteamFix/OnlineFix/steam_api DLLs into lib/.");
        }

        AssemblyLoadContext.Default.Resolving += (_, assemblyName) =>
        {
            var name = assemblyName.Name;
            if (name == null) return null;
            if (LoadedAssemblies.TryGetValue(name, out var loaded)) return loaded;
            if (!DllBySimpleName.TryGetValue(name, out var dllPath)) return null;

            try
            {
                var asm = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(dllPath));
                LoadedAssemblies[name] = asm;
                AppLogger.Info("Resolved dependency DLL on demand: " + Path.GetFileName(dllPath));
                return asm;
            }
            catch (Exception ex)
            {
                AppLogger.Error("Failed to resolve dependency DLL: " + dllPath, ex);
                return null;
            }
        };

        foreach (var preferred in required)
        {
            LoadAssembly(DllBySimpleName[preferred]);
        }

        AppLogger.Info("Registered game DLL whitelist: " + string.Join(", ", required.Select(x => x + ".dll")));
        AppLogger.Info("Registered safe on-demand dependency DLLs: " + DllBySimpleName.Count.ToString(CultureInfo.InvariantCulture));
        AppLogger.Info("Blocked risky DLLs in dependency directory: " + blockedCount.ToString(CultureInfo.InvariantCulture));
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

    private static IEnumerable<object?> EnumerateObjectChildren(object? obj)
    {
        if (obj == null || obj is string) yield break;
        var type = obj.GetType();
        if (type.IsPrimitive || type.IsEnum || type == typeof(decimal) || type == typeof(Guid) || type == typeof(DateTime)) yield break;
        var ns = type.Namespace ?? "";
        if (ns.StartsWith("System", StringComparison.Ordinal)) yield break;

        foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            object? value = null;
            try { value = field.GetValue(obj); } catch { }
            if (value != null) yield return value;
        }

        foreach (var prop in type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (prop.GetIndexParameters().Length != 0) continue;
            object? value = null;
            try { value = prop.GetValue(obj); } catch { }
            if (value != null) yield return value;
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
            field.SetValue(obj, ConvertForMember(value, field.FieldType));
            return;
        }
        var prop = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (prop != null && prop.CanWrite && prop.GetIndexParameters().Length == 0)
        {
            prop.SetValue(obj, ConvertForMember(value, prop.PropertyType));
            return;
        }
        throw new MissingMemberException(type.FullName, name);
    }

    private static object? ConvertForMember(object? value, Type targetType)
    {
        if (value == null) return null;
        var valueType = value.GetType();
        if (targetType.IsAssignableFrom(valueType)) return value;
        var nullable = Nullable.GetUnderlyingType(targetType);
        if (nullable != null) targetType = nullable;
        if (targetType == typeof(Guid))
        {
            if (value is Guid g) return g;
            return Guid.Parse(value.ToString() ?? "");
        }
        if (targetType.IsEnum)
        {
            if (value is string s) return Enum.Parse(targetType, s, ignoreCase: true);
            return Enum.ToObject(targetType, value);
        }
        return Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
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

    private static Type? FindTypeByName(string typeName)
    {
        foreach (var asm in LoadedAssemblies.Values.Distinct().Concat(AppDomain.CurrentDomain.GetAssemblies()).Distinct())
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).Cast<Type>().ToArray(); }
            catch { continue; }
            var exact = types.FirstOrDefault(t => string.Equals(t.Name, typeName, StringComparison.Ordinal));
            if (exact != null) return exact;
            var suffix = types.FirstOrDefault(t => (t.FullName ?? "").EndsWith("." + typeName, StringComparison.Ordinal));
            if (suffix != null) return suffix;
        }
        return null;
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

    private static float ToFloat(object? value)
    {
        if (value == null) return 0f;
        try { return Convert.ToSingle(value, CultureInfo.InvariantCulture); }
        catch { return 0f; }
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
        AppLogger.Info($"World desc files   : {result.TotalWorldDescriptions}");
        AppLogger.Info($"Player save files  : {result.TotalPlayerSaves}");

        foreach (var gs in result.GameStates)
        {
            AppLogger.Info("");
            AppLogger.Info($"GameState: {gs.SourceFileName}");
            AppLogger.Info($"- State entries : {gs.TotalStateEntries}");
            AppLogger.Info($"- ItemInstances : {gs.TotalItemInstances}");
            AppLogger.Info($"- Inventories   : {gs.TotalInventories}");
            AppLogger.Info($"- Citizens      : {gs.TotalCitizens}");
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

        WriteCsv(Path.Combine(outputDir, "world_desc.csv"), result.WorldDescriptions,
            ["SourceFile", "Name", "GameId", "Created", "LastPlayed", "Tier", "Size", "TimePlayed", "Modded", "Broken", "LoadedFromBackup", "WasGzip", "TypeName", "Note"],
            d => [d.SourceFile, d.Name, d.GameId, d.Created, d.LastPlayed, d.Tier, d.Size, d.TimePlayed?.ToString(CultureInfo.InvariantCulture) ?? "", d.Modded?.ToString() ?? "", d.Broken?.ToString() ?? "", d.LoadedFromBackup?.ToString() ?? "", d.WasGzip.ToString(), d.TypeName, d.Note]);

        WriteWorldDescBindingReport(outputDir, BuildWorldDescBindingReport(result));

        var gameStates = result.GameStates;
        WriteCsv(Path.Combine(outputDir, "state_entries.csv"), gameStates.SelectMany(g => g.StateEntries),
            ["SourceFile", "Name", "TypeName", "Count", "Description"],
            e => [e.SourceFile, e.Name, e.TypeName, e.Count?.ToString(CultureInfo.InvariantCulture) ?? "", e.Description]);

        WriteCsv(Path.Combine(outputDir, "world_item_instances.csv"), FilterWorldItems(gameStates.SelectMany(g => g.ItemInstances), options.Filter),
            ["SourceFile", "SourceKind", "OwnerName", "InstanceId", "BaseDataId", "StackCount", "InventoryId", "AuraCount", "AuraIds", "HasUsableInfo", "TypeName"],
            i => [i.SourceFile, i.SourceKind, i.OwnerName, i.InstanceId, i.BaseDataId, i.StackCount.ToString(CultureInfo.InvariantCulture), i.InventoryId, i.AuraCount.ToString(CultureInfo.InvariantCulture), i.AuraIds, i.HasUsableInfo.ToString(), i.TypeName]);

        WriteCsv(Path.Combine(outputDir, "world_item_totals.csv"), gameStates.SelectMany(g => g.ItemTotals).Where(i => options.Filter == null || i.BaseDataId.Contains(options.Filter, StringComparison.OrdinalIgnoreCase)),
            ["SourceFile", "BaseDataId", "TotalStackCount", "Stacks"],
            i => [i.SourceFile, i.BaseDataId, i.TotalStackCount.ToString(CultureInfo.InvariantCulture), i.Stacks.ToString(CultureInfo.InvariantCulture)]);

        WriteCsv(Path.Combine(outputDir, "world_inventories.csv"), gameStates.SelectMany(g => g.Inventories),
            ["SourceFile", "InventoryId", "Name", "InventoryType", "OwnerEntityId", "FilterItemFlags", "TotalSlots", "FilledSlots"],
            i => [i.SourceFile, i.InventoryId, i.Name, i.InventoryType, i.OwnerEntityId, i.FilterItemFlags, i.TotalSlots.ToString(CultureInfo.InvariantCulture), i.FilledSlots.ToString(CultureInfo.InvariantCulture)]);

        WriteCsv(Path.Combine(outputDir, "world_inventory_slots.csv"), gameStates.SelectMany(g => g.InventorySlots),
            ["SourceFile", "InventoryId", "InventoryName", "SlotIndex", "ItemInstanceId", "BaseDataId", "StackCount"],
            s => [s.SourceFile, s.InventoryId, s.InventoryName, s.SlotIndex.ToString(CultureInfo.InvariantCulture), s.ItemInstanceId, s.BaseDataId, s.StackCount.ToString(CultureInfo.InvariantCulture)]);

        WriteCsv(Path.Combine(outputDir, "citizens.csv"), gameStates.SelectMany(g => g.Citizens),
            ["SourceFile", "Kind", "CitizenId", "EntityId", "BaseEntityGuid", "Name", "CitizenBaseId", "Status", "CurrentJob", "CurrentJobLevel", "CurrentJobExperience", "Efficiency", "Expertise", "BaseEfficiency", "BaseExpertise", "Happiness", "FoodCost", "LoyaltyGain", "ExperienceGain", "Loyalty", "LoyaltyLevel", "CurrentHunger", "Personality", "Background", "HomeBuildingId", "CitizenSlotId", "CurrentWorldId", "SpawnWorldId", "PersonalQuestStatus", "JobCount", "TraitCount", "TraitIds", "AuraIds", "TypeName"],
            c => [c.SourceFile, c.Kind, c.CitizenId, c.EntityId, c.BaseEntityGuid, c.Name, c.CitizenBaseId, c.Status, c.CurrentJob, c.CurrentJobLevel, c.CurrentJobExperience, c.Efficiency, c.Expertise, c.BaseEfficiency, c.BaseExpertise, c.Happiness, c.FoodCost, c.LoyaltyGain, c.ExperienceGain, c.Loyalty, c.LoyaltyLevel, c.CurrentHunger, c.Personality, c.Background, c.HomeBuildingId, c.CitizenSlotId, c.CurrentWorldId, c.SpawnWorldId, c.PersonalQuestStatus, c.JobCount, c.TraitCount, c.TraitIds, c.AuraIds, c.TypeName]);

        WriteCsv(Path.Combine(outputDir, "citizen_jobs.csv"), gameStates.SelectMany(g => g.CitizenJobs),
            ["SourceFile", "Kind", "CitizenId", "Name", "JobId", "Level", "Experience"],
            j => [j.SourceFile, j.Kind, j.CitizenId, j.Name, j.JobId, j.Level.ToString(CultureInfo.InvariantCulture), j.Experience.ToString(CultureInfo.InvariantCulture)]);

        WriteCsv(Path.Combine(outputDir, "citizen_debug.csv"), gameStates.SelectMany(g => g.CitizenDebug),
            ["SourceFile", "EntryName", "EntryType", "EntryCount", "CandidateKey", "CandidateType", "LooksLikeCitizen", "HasName", "HasId", "HasJobExperience", "HasCitizenStats", "Members", "Note"],
            d => [d.SourceFile, d.EntryName, d.EntryType, d.EntryCount, d.CandidateKey, d.CandidateType, d.LooksLikeCitizen, d.HasName, d.HasId, d.HasJobExperience, d.HasCitizenStats, d.Members, d.Note]);

        var citizenEntityReport = BuildCitizenEntityBindingReport(result);
        WriteCitizenEntityBindingReport(outputDir, citizenEntityReport);

        WriteCsv(Path.Combine(outputDir, "citizen_aura_runtime_report.csv"), gameStates.SelectMany(g => g.CitizenAuraRuntime),
            ["SourceFile", "CitizenId", "Name", "EntityId", "SourceKind", "SourceKey", "Path", "MemberName", "MemberType", "Value", "Reason"],
            h => [h.SourceFile, h.CitizenId, h.Name, h.EntityId, h.SourceKind, h.SourceKey, h.Path, h.MemberName, h.MemberType, h.Value, h.Reason]);

        var players = FilterPlayers(result.PlayerSaves, options.PlayerFilter).ToList();
        WriteCsv(Path.Combine(outputDir, "player_saves.csv"), players,
            ["SourceFile", "SaveFileName", "PlayerName", "PlayerId", "EntityId", "WorldId", "Money", "TimePlayed", "FilledInventorySlots", "FilledEquipmentSlots", "FilledSecondaryEquipmentSlots"],
            p => [p.SourceFileName, p.SaveFileName, p.PlayerName, p.PlayerId.ToString(CultureInfo.InvariantCulture), p.EntityId, p.WorldId, p.Money.ToString(CultureInfo.InvariantCulture), p.TimePlayed.ToString(CultureInfo.InvariantCulture), p.InventorySlots.ToString(CultureInfo.InvariantCulture), p.EquipmentSlots.ToString(CultureInfo.InvariantCulture), p.SecondaryEquipmentSlots.ToString(CultureInfo.InvariantCulture)]);

        var playerItems = players.SelectMany(p => p.Items).Where(i => options.Filter == null || i.BaseDataId.Contains(options.Filter, StringComparison.OrdinalIgnoreCase)).ToList();
        WriteCsv(Path.Combine(outputDir, "player_items.csv"), playerItems,
            ["SourceFile", "SaveFileName", "PlayerName", "PlayerId", "Section", "SlotIndex", "InstanceId", "BaseDataId", "StackCount", "AuraCount", "AuraIds", "HasUsableInfo", "TypeName"],
            i => [i.SourceFile, i.SaveFileName, i.PlayerName, i.PlayerId.ToString(CultureInfo.InvariantCulture), i.Section, i.SlotIndex.ToString(CultureInfo.InvariantCulture), i.InstanceId, i.BaseDataId, i.StackCount.ToString(CultureInfo.InvariantCulture), i.AuraCount.ToString(CultureInfo.InvariantCulture), i.AuraIds, i.HasUsableInfo.ToString(), i.TypeName]);

        var playerSkills = players.SelectMany(p => p.Skills).ToList();
        WriteCsv(Path.Combine(outputDir, "player_skills.csv"), playerSkills,
            ["SourceFile", "SaveFileName", "PlayerName", "PlayerId", "SkillId", "Name", "Description", "Level", "CurrentExperience", "ExperienceRequiredToLevelUp", "CurrentValue"],
            i => [i.SourceFile, i.SaveFileName, i.PlayerName, i.PlayerId.ToString(CultureInfo.InvariantCulture), i.SkillId, i.Name, i.Description, i.Level.ToString(CultureInfo.InvariantCulture), i.CurrentExperience.ToString(CultureInfo.InvariantCulture), i.ExperienceRequiredToLevelUp.ToString(CultureInfo.InvariantCulture), i.CurrentValue.ToString(CultureInfo.InvariantCulture)]);

        var playerTotals = playerItems
            .Where(i => !string.IsNullOrWhiteSpace(i.BaseDataId))
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


    private static CitizenEntityBindingReport BuildCitizenEntityBindingReport(FullInspectionResult result)
    {
        var bindings = result.GameStates.SelectMany(g => g.CitizenEntityBindings).ToList();
        var report = new CitizenEntityBindingReport
        {
            Passed = true,
            GameStateCount = result.GameStates.Count,
            CitizenCount = result.GameStates.Sum(g => g.Citizens.Count),
            EntityBindingCount = bindings.Count,
            MissingEntityCount = bindings.Count(b => !b.EntityFound),
            CitizensWithNoRuntimeAuraHits = bindings.Count(b => (SplitSemicolonIds(b.AuraIds).Any() || SplitSemicolonIds(b.TraitIds).Any()) && b.EntityFound && b.EntityAuraHitCount == "0" && b.ControllerAuraHitCount == "0"),
            Bindings = bindings
        };

        foreach (var b in bindings.Where(b => !b.EntityFound))
        {
            report.Errors.Add($"Citizen {b.CitizenId} ({b.Name}) references EntityId {b.EntityId}, but that EntityId was not found in the Entities StateEntry.");
        }

        foreach (var b in bindings.Where(b => (SplitSemicolonIds(b.AuraIds).Any() || SplitSemicolonIds(b.TraitIds).Any()) && b.EntityFound && b.EntityAuraHitCount == "0" && b.ControllerAuraHitCount == "0").Take(25))
        {
            report.Warnings.Add($"Citizen {b.CitizenId} ({b.Name}) has aura/trait IDs but no matching aura/trait/buff text was found in its Entity or CitizenControllerState diagnostic scan.");
        }

        report.Passed = report.Errors.Count == 0;
        return report;
    }

    private static void WriteCitizenEntityBindingReport(string outputDir, CitizenEntityBindingReport report)
    {
        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(Path.Combine(outputDir, "citizen_entity_binding_report.json"), JsonSerializer.Serialize(report, jsonOptions), Encoding.UTF8);
        WriteCsv(Path.Combine(outputDir, "citizen_entity_binding_report.csv"), report.Bindings,
            ["SourceFile", "CitizenId", "Name", "EntityId", "TraitIds", "AuraIds", "EntityFound", "EntityKey", "EntityType", "EntitySummary", "EntityAuraHitCount", "ControllerFound", "ControllerKey", "ControllerType", "ControllerSummary", "ControllerAuraHitCount", "CitizenSlotFound", "CitizenSlotKey", "CitizenSlotType", "Notes"],
            b => [b.SourceFile, b.CitizenId, b.Name, b.EntityId, b.TraitIds, b.AuraIds, b.EntityFound.ToString(), b.EntityKey, b.EntityType, b.EntitySummary, b.EntityAuraHitCount, b.ControllerFound, b.ControllerKey, b.ControllerType, b.ControllerSummary, b.ControllerAuraHitCount, b.CitizenSlotFound, b.CitizenSlotKey, b.CitizenSlotType, b.Notes]);
    }

    private static WorldDescBindingReport BuildWorldDescBindingReport(FullInspectionResult result, string stage = "inspection")
    {
        var report = new WorldDescBindingReport
        {
            Stage = stage,
            GameStateCount = result.GameStates.Count,
            WorldDescCount = result.WorldDescriptions.Count,
            PlayerSaveCount = result.PlayerSaves.Count,
            Note = "world_desc is the game's GameDescription file. The game writes it next to game_state and loads it before game_state. v71 compares GameId and summary metadata so game_state-only edits do not silently drift from world_desc."
        };

        if (result.WorldDescriptions.Count == 0)
            AddBindingIssue(report, isError: false, "world_desc.missing", "", "", "WARN", "No world_desc was parsed. The game normally expects world_desc beside game_state.");
        if (result.GameStates.Count == 0)
            AddBindingIssue(report, isError: true, "game_state.missing", "", "", "ERROR", "No game_state was parsed.");

        foreach (var desc in result.WorldDescriptions)
        {
            if (desc.Broken == true)
                AddBindingIssue(report, isError: true, "world_desc.Broken", desc.SourceFileName, "Broken=true", "ERROR", "world_desc is marked broken.");
            if (desc.LoadedFromBackup == true)
                AddBindingIssue(report, isError: false, "world_desc.LoadedFromBackup", desc.SourceFileName, "LoadedFromBackup=true", "WARN", "world_desc was loaded from backup in the game model.");
        }

        foreach (var gs in result.GameStates)
        {
            foreach (var desc in result.WorldDescriptions.DefaultIfEmpty())
            {
                if (desc == null) continue;
                var left = $"{desc.SourceFileName}:{desc.GameId}";
                var right = $"{gs.SourceFileName}:{gs.GameId}";
                if (!string.IsNullOrWhiteSpace(desc.GameId) && !string.IsNullOrWhiteSpace(gs.GameId))
                {
                    if (GuidsEqual(desc.GameId, gs.GameId))
                        report.Checks.Add(new WorldDescBindingCheck("INFO", "GameId", left, right, "OK", "world_desc.GameId matches game_state.GameId."));
                    else
                        AddBindingIssue(report, isError: true, "GameId", left, right, "ERROR", "world_desc.GameId does not match game_state.GameId.");
                }
                else
                {
                    AddBindingIssue(report, isError: false, "GameId", left, right, "WARN", "Could not compare GameId because one side is blank.");
                }

                if (desc.TimePlayed.HasValue && gs.TimePlayed.HasValue)
                {
                    var delta = Math.Abs(desc.TimePlayed.Value - gs.TimePlayed.Value);
                    var status = delta > 600 ? "WARN" : "OK";
                    var note = $"TimePlayed delta={delta.ToString("0.###", CultureInfo.InvariantCulture)} seconds. This is usually not fatal, but large drift suggests world_desc was not updated by the game save path.";
                    if (status == "WARN") AddBindingIssue(report, isError: false, "TimePlayed", desc.TimePlayed.Value.ToString(CultureInfo.InvariantCulture), gs.TimePlayed.Value.ToString(CultureInfo.InvariantCulture), status, note);
                    else report.Checks.Add(new WorldDescBindingCheck("INFO", "TimePlayed", desc.TimePlayed.Value.ToString(CultureInfo.InvariantCulture), gs.TimePlayed.Value.ToString(CultureInfo.InvariantCulture), status, note));
                }
            }
        }

        foreach (var player in result.PlayerSaves)
        {
            if (string.IsNullOrWhiteSpace(player.WorldId)) continue;
            foreach (var desc in result.WorldDescriptions)
            {
                if (string.IsNullOrWhiteSpace(desc.GameId)) continue;
                if (GuidsEqual(player.WorldId, desc.GameId))
                    report.Checks.Add(new WorldDescBindingCheck("INFO", "PlayerWorldId", $"{player.SourceFileName}:{player.WorldId}", $"{desc.SourceFileName}:{desc.GameId}", "OK", "player .char WorldId matches world_desc.GameId."));
                else
                    AddBindingIssue(report, isError: false, "PlayerWorldId", $"{player.SourceFileName}:{player.WorldId}", $"{desc.SourceFileName}:{desc.GameId}", "WARN", "player .char WorldId does not match world_desc.GameId. This may be normal for test files, but can break loading if the game expects matching world/player files.");
            }
        }

        report.Passed = report.Errors.Count == 0;
        return report;
    }

    private static void AddBindingIssue(WorldDescBindingReport report, bool isError, string check, string left, string right, string status, string note)
    {
        report.Checks.Add(new WorldDescBindingCheck(isError ? "ERROR" : "WARN", check, left, right, status, note));
        if (isError) report.Errors.Add($"{check}: {note} ({left} / {right})");
        else report.Warnings.Add($"{check}: {note} ({left} / {right})");
    }

    private static void WriteWorldDescBindingReport(string outputDir, WorldDescBindingReport report)
    {
        Directory.CreateDirectory(outputDir);
        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(Path.Combine(outputDir, "world_desc_binding_report.json"), JsonSerializer.Serialize(report, jsonOptions), Encoding.UTF8);
        WriteCsv(Path.Combine(outputDir, "world_desc_binding_report.csv"), report.Checks,
            ["Level", "Check", "Left", "Right", "Status", "Note"],
            c => [c.Level, c.Check, c.Left, c.Right, c.Status, c.Note]);
    }

    private static WorldDescBindingReport? WriteWorldDescBindingReportForInput(string inputDir, string outputDir, string stage)
    {
        try
        {
            var result = InspectBindingRelevantInputFiles(inputDir);
            var report = BuildWorldDescBindingReport(result, stage);
            WriteWorldDescBindingReport(outputDir, report);
            var citizenEntityReport = BuildCitizenEntityBindingReport(result);
            WriteCitizenEntityBindingReport(outputDir, citizenEntityReport);
            WriteCsv(Path.Combine(outputDir, "citizen_aura_runtime_report.csv"), result.GameStates.SelectMany(g => g.CitizenAuraRuntime),
                ["SourceFile", "CitizenId", "Name", "EntityId", "SourceKind", "SourceKey", "Path", "MemberName", "MemberType", "Value", "Reason"],
                h => [h.SourceFile, h.CitizenId, h.Name, h.EntityId, h.SourceKind, h.SourceKey, h.Path, h.MemberName, h.MemberType, h.Value, h.Reason]);
            AppLogger.Info("world_desc/game_state 绑定校验报告：" + Path.Combine(outputDir, "world_desc_binding_report.json"));
            AppLogger.Info("Citizen/Entity aura runtime 诊断报告：" + Path.Combine(outputDir, "citizen_entity_binding_report.json"));
            return report;
        }
        catch (Exception ex)
        {
            AppLogger.Warn("world_desc/game_state 绑定校验报告生成失败：" + ex.Message);
            return null;
        }
    }

    private static FullInspectionResult InspectBindingRelevantInputFiles(string inputDir)
    {
        var result = new FullInspectionResult();
        foreach (var file in GetInputFiles(inputDir))
        {
            var name = Path.GetFileName(file);
            if (name.Equals(GameStateFileName, StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith(GameStateFileName + ".", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("world_desc", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("world_desc.", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".char", StringComparison.OrdinalIgnoreCase))
            {
                InspectFile(file, new Options { Limit = 2000 }, result);
            }
        }

        result.TotalFilesScanned = result.FilesScanned.Count;
        result.TotalGameStates = result.GameStates.Count;
        result.TotalWorldDescriptions = result.WorldDescriptions.Count;
        result.TotalPlayerSaves = result.PlayerSaves.Count;
        return result;
    }

    private static Guid? TryGetSingleWorldDescGameIdFromInput(string inputDir)
    {
        try
        {
            var descIds = new HashSet<Guid>();
            foreach (var file in GetInputFiles(inputDir))
            {
                var name = Path.GetFileName(file);
                if (!name.Equals("world_desc", StringComparison.OrdinalIgnoreCase) &&
                    !name.StartsWith("world_desc.", StringComparison.OrdinalIgnoreCase))
                    continue;

                var bytes = File.ReadAllBytes(file);
                if (!TryDeserializeWorldDescriptionWithFormat(bytes, out var desc, out _, out _) || desc == null)
                    continue;

                var gameIdText = NormalizeGuidString(ReadMember(desc, "GameId"));
                if (Guid.TryParse(gameIdText, out var gameId) && gameId != Guid.Empty)
                    descIds.Add(gameId);
            }

            if (descIds.Count == 1) return descIds.First();
            if (descIds.Count > 1)
                throw new InvalidOperationException("input 中存在多个不同的 world_desc.GameId，无法判断应绑定哪个世界。");
        }
        catch (Exception ex)
        {
            AppLogger.Warn("读取 world_desc.GameId 失败：" + ex.Message);
        }

        return null;
    }

    private static bool TrySetTopLevelGameStateGameId(object state, Guid gameId)
    {
        if (state is not IList list) return false;
        for (var i = 0; i < list.Count; i++)
        {
            var entry = list[i];
            if (entry == null) continue;
            var t = entry.GetType();
            var name = t.GetField("Item1")?.GetValue(entry)?.ToString()
                       ?? t.GetProperty("Name")?.GetValue(entry)?.ToString();
            if (!string.Equals(name, "GameId", StringComparison.OrdinalIgnoreCase)) continue;

            if (t.IsValueType && t.IsGenericType && t.GetGenericTypeDefinition() == typeof(ValueTuple<,>))
            {
                list[i] = ("GameId", (object)gameId);
                return true;
            }

            var field = t.GetField("Item2");
            if (field != null)
            {
                field.SetValue(entry, gameId);
                return true;
            }

            var prop = t.GetProperty("Value");
            if (prop?.CanWrite == true)
            {
                prop.SetValue(entry, gameId);
                return true;
            }

            return false;
        }

        return false;
    }

    private static bool GuidsEqual(string a, string b)
    {
        if (Guid.TryParse(a, out var ga) && Guid.TryParse(b, out var gb)) return ga == gb;
        return string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeGuidString(object? value)
    {
        if (value == null) return "";
        if (value is Guid g) return g.ToString("D");
        var text = value.ToString()?.Trim() ?? "";
        return Guid.TryParse(text, out var parsed) ? parsed.ToString("D") : text;
    }

    private static string ToDisplayString(object? value)
    {
        if (value == null) return "";
        if (value is DateTime dt) return dt.ToString("O", CultureInfo.InvariantCulture);
        if (value is DateTimeOffset dto) return dto.ToString("O", CultureInfo.InvariantCulture);
        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? value.ToString() ?? "";
    }

    private static double? ToDoubleOrNull(object? value)
    {
        if (value == null) return null;
        try
        {
            return value switch
            {
                double d => d,
                float f => f,
                int i => i,
                long l => l,
                uint u => u,
                ulong ul => ul,
                decimal m => (double)m,
                _ when double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => null
            };
        }
        catch { return null; }
    }

    private static bool? ToBoolOrNull(object? value)
    {
        if (value == null) return null;
        if (value is bool b) return b;
        var text = value.ToString();
        return bool.TryParse(text, out var parsed) ? parsed : null;
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

    private sealed record SkillDefinition(string Id, string Name, string Description, float Value);

    public sealed record PlayerItemStackUpdate(string SourceFile, string SaveFileName, string PlayerName, int PlayerId, string Section, int SlotIndex, string InstanceId, string OriginalBaseDataId, string NewBaseDataId, int StackCount, string AuraIds);

    public sealed record PlayerMoneyUpdate(string SourceFile, string SaveFileName, string PlayerName, int PlayerId, ulong Money);

    public sealed record PlayerSkillUpdate(string SourceFile, string SaveFileName, string PlayerName, int PlayerId, string SkillId, int Level, float CurrentExperience);

    public sealed record CitizenUpdate(string SourceFile, string Kind, string CitizenId, string EntityId, int CurrentJobLevel, float CurrentJobExperience, float Efficiency, float Expertise, float Loyalty, int LoyaltyLevel, string TraitIds);

    private sealed record EntryRawSpan(int Index, long Offset, long Length, string Note);

    public sealed record WorldStateEntrySizeCompareRow(
        int Index,
        string EntryName,
        string TypeName,
        int? Count,
        long? OriginalBytes,
        long? RoundtripBytes,
        long? DiffBytes,
        double? DiffPercent,
        bool CanDeserialize,
        bool CanRoundtrip,
        long? OriginalOffset,
        long? RoundtripOffset,
        string Status,
        string Note);

    public sealed record WorldRoundtripMetrics(long CompressedBytes, long DecompressedBytes, bool WasGzip, int StateEntries, int ItemInstances, int Inventories, int Citizens, int? WorldItems, string Note);

    public sealed record WorldRoundtripResult(bool Passed, string Message, string OriginalPath, string RoundtripPath, WorldRoundtripMetrics? Before, WorldRoundtripMetrics? After, IReadOnlyList<string> Errors);

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
        public int TotalWorldDescriptions { get; set; }
        public int TotalPlayerSaves { get; set; }
        public List<FileScanInfo> FilesScanned { get; set; } = new();
        public List<GameStateInspectionResult> GameStates { get; set; } = new();
        public List<WorldDescriptionInfo> WorldDescriptions { get; set; } = new();
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
        public int TotalCitizens { get; set; }
        public string GameId { get; set; } = "";
        public double? TimePlayed { get; set; }
        public string ConfigDescription { get; set; } = "";
        public List<StateEntryInfo> StateEntries { get; set; } = new();
        public List<CitizenInfo> Citizens { get; set; } = new();
        public List<CitizenJobInfo> CitizenJobs { get; set; } = new();
        public List<CitizenDebugInfo> CitizenDebug { get; set; } = new();
        public List<CitizenEntityBindingInfo> CitizenEntityBindings { get; set; } = new();
        public List<CitizenAuraRuntimeHitInfo> CitizenAuraRuntime { get; set; } = new();
        public List<ItemInstanceInfo> ItemInstances { get; set; } = new();
        public List<ItemTotalInfo> ItemTotals { get; set; } = new();
        public List<InventoryInfo> Inventories { get; set; } = new();
        public List<InventorySlotInfo> InventorySlots { get; set; } = new();
    }

    private sealed class WorldDescriptionInfo
    {
        public string SourceFile { get; set; } = "";
        public string SourceFileName { get; set; } = "";
        public string Name { get; set; } = "";
        public string Created { get; set; } = "";
        public string LastPlayed { get; set; } = "";
        public string Tier { get; set; } = "";
        public string Size { get; set; } = "";
        public double? TimePlayed { get; set; }
        public string FolderPath { get; set; } = "";
        public string GameId { get; set; } = "";
        public bool? Modded { get; set; }
        public bool? Broken { get; set; }
        public bool? LoadedFromBackup { get; set; }
        public bool WasGzip { get; set; }
        public string TypeName { get; set; } = "";
        public string Note { get; set; } = "";
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
        public List<PlayerSkillInfo> Skills { get; set; } = new();
    }

    private sealed record CitizenTraitDeepDiffRow(string SourceFile, string CitizenId, string EntityId, string Name, string RequestedTraitIds, string BeforeTraitIds, string AfterTraitIds, string BeforeAuraObjects, string AfterAuraObjects);
    private sealed record WorldWriteLabReportRow(string Directory, string Kind, string Note);
    private sealed record SafeWorldPairReportRow(string Level, string Check, string Status, string Note);

    private sealed class WorldDescBindingReport
    {
        public bool Passed { get; set; }
        public string Stage { get; set; } = "inspection";
        public int GameStateCount { get; set; }
        public int WorldDescCount { get; set; }
        public int PlayerSaveCount { get; set; }
        public List<WorldDescBindingCheck> Checks { get; set; } = new();
        public List<string> Errors { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
        public string Note { get; set; } = "";
    }

    private sealed record WorldDescBindingCheck(string Level, string Check, string Left, string Right, string Status, string Note);

    private sealed record FileScanInfo(string Path, string FileName, long SizeBytes, bool DetectedGameState, bool DetectedPlayerSave, string Note);
    private sealed record StateEntryInfo(string SourceFile, string Name, string TypeName, int? Count, string Description);
    private sealed record ItemInstanceInfo(string SourceFile, string SourceKind, string OwnerName, string InstanceId, string BaseDataId, int StackCount, string InventoryId, int AuraCount, string AuraIds, bool HasUsableInfo, string TypeName);
    private sealed record ItemTotalInfo(string SourceFile, string BaseDataId, int TotalStackCount, int Stacks);
    private sealed record InventoryInfo(string SourceFile, string InventoryId, string Name, string InventoryType, string OwnerEntityId, string FilterItemFlags, int TotalSlots, int FilledSlots);
    private sealed record InventorySlotInfo(string SourceFile, string InventoryId, string InventoryName, int SlotIndex, string ItemInstanceId, string BaseDataId, int StackCount);
    private sealed record PlayerItemInfo(string SourceFile, string SaveFileName, string PlayerName, int PlayerId, string Section, int SlotIndex, string InstanceId, string BaseDataId, int StackCount, int AuraCount, string AuraIds, bool HasUsableInfo, string TypeName);
    private sealed record PlayerSkillInfo(string SourceFile, string SaveFileName, string PlayerName, int PlayerId, string SkillId, string Name, string Description, int Level, float CurrentExperience, float ExperienceRequiredToLevelUp, float CurrentValue);
    private sealed record CitizenInfo(string SourceFile, string Kind, string CitizenId, string EntityId, string BaseEntityGuid, string Name, string CitizenBaseId, string Status, string CurrentJob, string CurrentJobLevel, string CurrentJobExperience, string Efficiency, string Expertise, string BaseEfficiency, string BaseExpertise, string Happiness, string FoodCost, string LoyaltyGain, string ExperienceGain, string Loyalty, string LoyaltyLevel, string CurrentHunger, string Personality, string Background, string HomeBuildingId, string CitizenSlotId, string CurrentWorldId, string SpawnWorldId, string PersonalQuestStatus, string JobCount, string TraitCount, string TraitIds, string AuraIds, string TypeName);
    private sealed record CitizenJobInfo(string SourceFile, string Kind, string CitizenId, string Name, string JobId, int Level, float Experience);
    private sealed record CitizenDebugInfo(string SourceFile, string EntryName, string EntryType, string EntryCount, string CandidateKey, string CandidateType, string LooksLikeCitizen, string HasName, string HasId, string HasJobExperience, string HasCitizenStats, string Members, string Note);
    private sealed record CitizenEntityBindingInfo(string SourceFile, string CitizenId, string Name, string EntityId, string TraitIds, string AuraIds, bool EntityFound, string EntityKey, string EntityType, string EntitySummary, string EntityAuraHitCount, string ControllerFound, string ControllerKey, string ControllerType, string ControllerSummary, string ControllerAuraHitCount, string CitizenSlotFound, string CitizenSlotKey, string CitizenSlotType, string Notes);
    private sealed record CitizenAuraRuntimeHitInfo(string SourceFile, string CitizenId, string Name, string EntityId, string SourceKind, string SourceKey, string Path, string MemberName, string MemberType, string Value, string Reason);
    private sealed class CitizenEntityBindingReport
    {
        public bool Passed { get; set; }
        public string Stage { get; set; } = "inspection";
        public int GameStateCount { get; set; }
        public int CitizenCount { get; set; }
        public int EntityBindingCount { get; set; }
        public int MissingEntityCount { get; set; }
        public int CitizensWithNoRuntimeAuraHits { get; set; }
        public List<CitizenEntityBindingInfo> Bindings { get; set; } = new();
        public List<string> Errors { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
        public string Note { get; set; } = "v72 diagnostic: validates Citizen.EntityId against Entities and records possible aura/trait/buff/debuff runtime mirrors in Entities/CitizenControllerStates/CitizenSlots. This report is diagnostic only; it does not prove the save will load in-game.";
    }
    private sealed record PlayerItemTotalInfo(string SourceFile, string SaveFileName, string PlayerName, int PlayerId, string BaseDataId, int TotalStackCount, int Stacks);
}
