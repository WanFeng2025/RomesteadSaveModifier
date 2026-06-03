## v84 / R1.1.68

- 修复 output_csv=false 时切换玩家 .char 读取不到 CSV 缓存，导致玩家列表被清空、装备列表为空的问题。
- 识别阶段的运行时 CSV/JSON 缓存改写到系统临时目录，而不是 output/。
- 切换玩家 .char 时改为从最近一次识别缓存异步重建视图，不再同步读取 output 根目录，减少 UI 卡顿。
- 保留 v82 ammo 可装备栏修复和 v83 output_csv 严格输出控制。

## v83 / R1.1.67

- Fixed player equipment validation: ammo IDs (`ammo:*`) are valid equipment-slot items in player `.char` files.
- Ammo in equipment slots remains stackable and is no longer forced to count 1; other equipment remains non-stackable.
- Updated the equipment-slot validation message to mention ammo.


## v81 / R1.1.65

- 新增 `settings.json`：`output_csv`，默认 `false`。
- 世界写入诊断 JSON/CSV 只在 `output_csv=true` 时输出。
- `game_state.unpatched_DO_NOT_USE` 只在 `debug-mode=true` 时输出到 `output/debug/`。
- 不再生成 `output/world_write_lab/`。
- 不再生成 `output/safe_world_pair/`。
- 正式修改后的世界文件输出到 `output/world/game_state` 和 `output/world/world_desc`。

## R1.1.63 / v79
- Added left-side .char switcher for multiple player files.
- Added citizen trait deep-diff report and world_write_lab original/saved pair outputs to stop blind trait debugging.
- Keeps v78 item database audit fixes.

Romestead Save Inspector WinUI3 v79 / R1.1.63

- Continued trait/aura work.
- Adds robust CitizensSController discovery by type name suffix.
- Logs native AddCitizenAura/UpdateStats success, method candidates, or fallback reason.
- Keeps safe_world_pair output from v75/v76.

## R1.1.60 / v76

- 继续保留村民特质编辑，不隐藏。
- 修正 trait 写入策略：只移除/重建 `CitizenAuraInfo.Type=Trait` 的 aura，不再因为 ID 中包含 `:buff:` / `:debuff:` 就删除非 Trait aura。
- 新增 `Shared.Data.Auras.SharedDataSetup.SetupCitizenAuras()` -> `CitizenAuraDatabase.AddData(...)` 初始化路径，避免使用错误的 `Shared.Data.SharedDataSetup` 导致新建 `CitizenAuraModel` 缺少游戏原生 `StatsToAdd / Type / InstanceTypeId`。
- 添加优先调用游戏 `CitizensSController.AddCitizenAura(...)` 和 `CitizensSController.UpdateStats(...)` 的路径，失败时才回退到本地构造。
- 继续输出 `safe_world_pair` 和 v72 的 Citizen/Entity 诊断报告。

## R1.1.59 / v75

- 修复 v74 编译错误：`BuildOutputSnapshot` 是 static 方法，不能直接访问实例字段 `_enableCitizenTraitEditor`。
- 现在会在进入 worker thread 前捕获 `enableCitizenTraitEditor`，并作为参数传入 snapshot 构建流程。
- 继续保留 v74 的 safe world pair 成对输出功能。

## R1.1.58 / v74

- 继续保留并默认显示村民特质 / buff / debuff 编辑功能：`enable-citizen-trait-editor=true`。
- 保留 v72 的 Citizen ↔ Entity runtime 诊断报告，不再走“彻底隐藏”方案。
- `settings.json` 里的 `enable-citizen-trait-editor` 只作为紧急开关；默认开启，后续继续分析游戏加载失败原因。
- 继续输出：`citizen_entity_binding_report.json/csv`、`citizen_aura_runtime_report.csv`、`world_desc_binding_report.*`、`world_write_validation_report.*`。
- 下轮测试如果游戏仍加载失败，请优先上传：`citizen_entity_binding_report.json`、`citizen_aura_runtime_report.csv`、`latest.log`、`world_write_validation_report.json`。

## R1.1.56 / v72

- Added Citizen ↔ Entity aura/runtime diagnostics.
- New output files: `citizen_entity_binding_report.json`, `citizen_entity_binding_report.csv`, and `citizen_aura_runtime_report.csv`.
- These reports check whether each `Citizen.EntityId` resolves inside the `Entities` StateEntry and whether likely aura/trait/buff/debuff runtime mirrors exist in `Entities`, `CitizenControllerStates`, or `CitizenSlots`.
- Purpose: diagnose game-load failures after trait/buff/debuff edits when `game_state` structural validation and `world_desc` binding validation already pass.

## R1.1.53 / v69

- Fixes seeded compact string-map preservation for trait/buff/debuff world writes.
- Empty strings in the original direct string map are now preserved instead of skipped.
- The direct string map is written back by original compact ID position, then new strings are appended.
- This targets the v68 failure where large arrays were preserved but the validation reader lost Inventories/WorldItems/Citizens due to a shifted compact string-map ID.

# Romestead Save Inspector / Modifier

## R1.1.52 / v68
- Fixed the native seeded string-map serializer path after v67.
- The tool no longer disposes the game ExtendedBinaryWriter before copying the MemoryStream body, avoiding `Cannot access a closed Stream`.
- Keeps the staged preflight flow: failed world writes do not overwrite `input/game_state`.

# Romestead Save Inspector / 存档修改器 v42 raw-preserve diagnostics

This build keeps citizen/NPC world editing disabled and changes the safe **world write roundtrip test** into a raw-preserve validation pass for large top-level world arrays.

## New in v42

- Uses v41 diagnostics to preserve the original serialized bytes for these large world entries:
  - `WorldTiles`
  - `BiomeIds`
  - `Difficulties`
- Still never overwrites `input/game_state`.
- Writes the final raw-preserved test output:
  - `output/game_state_roundtrip_test_DO_NOT_USE`
  - `output/game_state_roundtrip_raw_preserve_test_DO_NOT_USE`
- Also writes the unpatched serializer output for comparison:
  - `output/game_state_roundtrip_unpatched_DO_NOT_USE`
- Keeps the main roundtrip reports:
  - `output/world_roundtrip_report.csv`
  - `output/world_roundtrip_report.json`
- Writes final StateEntry comparison reports:
  - `output/world_state_entry_size_compare.csv`
  - `output/world_state_entry_size_compare.json`
- Writes unpatched serializer diagnostic reports:
  - `output/world_state_entry_size_compare_unpatched.csv`
  - `output/world_state_entry_size_compare_unpatched.json`
- Keeps game DLL loading on a whitelist only:
  - `CandideServer.dll`
  - `Shared.dll`
  - `CandideCreator.Shared.dll`
  - `MonoGame.Framework.dll`
- Non-whitelisted DLLs such as `steam_api64.dll`, `Steamworks.NET.dll`, SteamFix/OnlineFix hook DLLs, `winmm.dll`, and `version.dll` are not loaded by the tool.

## Safe usage

1. Put `game_state`, `world_desc`, and `.char` files into `input/`.
2. Detect the game directory or provide only the four required game DLLs in `lib/`.
3. Click **Check files**.
4. Click **Test world write / analyze entries**.
5. Send back these files for diagnosis:
   - `output/world_state_entry_size_compare.csv`
   - `output/world_state_entry_size_compare.json`
   - `output/world_state_entry_size_compare_unpatched.csv`
   - `output/world_state_entry_size_compare_unpatched.json`
   - `output/world_roundtrip_report.csv`
   - `output/world_roundtrip_report.json`
   - the latest log under `logs/`

Do not use the generated `game_state_*_DO_NOT_USE` files as real saves yet. Player `.char` editing remains available. `game_state` / NPC saving remains disabled until raw-preserve roundtrip is proven safe on the user's machine.


## v43 安全世界写入实验

- v42 已验证无修改 roundtrip 通过：`WorldTiles`、`BiomeIds`、`Difficulties` 使用原始 bytes 保留。
- v43 的村民保存不再完整重写 `game_state`。流程是：修改小型可控 StateEntry -> 序列化临时结果 -> 把大型世界数组块替换回原始 bytes -> 反序列化校验 -> 再覆盖 `input/game_state`。
- 覆盖前会输出并检查：
  - `output/world_write_validation_report.csv/json`
  - `output/world_write_entry_size_compare.csv/json`
  - `output/game_state.unpatched_DO_NOT_USE`（危险诊断文件，不要进游戏使用）
- 如果序列化头/string map 与原始文件不同，v43 会拒绝写入。这通常表示本次修改引入了新的字符串 ID，例如原存档里不存在的新 trait id。数值类修改优先测试。


## R1.1.38

- Added a same-length Citizens delta fallback for numeric-only citizen/world edits.
- When the full serializer changes the header/string map, numeric-only Citizens changes can now be patched into the original payload while preserving the original header and large world arrays.
- Trait/aura string-list changes still do not use this fallback; if they change the string map, the save is rejected until a true string-reference remapper/local Citizens patch is implemented.
- Staged preflight remains enabled, so failed world writes do not touch the real input files.


- 修复日志初始化时 latest.log/时间戳日志被占用导致的启动异常。
- 保存流程改为 staged save：先在临时 input 副本中预验证 game_state 世界写入，验证通过后才真正写入玩家 .char 和 game_state。
- 如果村民特质/字符串类修改会导致 string map/header 不安全变化，保存会在真实文件写入前失败，避免出现“玩家已保存、世界未保存”的半保存状态。
- 保留 v52 的 dist/single-exe 依赖解析修复。

## R1.1.36

- 放宽安全世界写入的 header/string map 判断：当序列化器只追加少量字符串导致 prefix 增长时，允许继续 raw-preserve 大型世界数组块，并保留写入后反序列化与 StateEntry 校验。
- 修复修改村民 trait/job 等字符串类数据时可能被过严保护误拦截的问题。

## R1.1.32

- Save button renamed to 保存玩家/世界数据 / Save player/world data.
- Test world write button is hidden by default. Set `debug-mode` to `true` in `settings.json` to show it. Missing, empty, or `false` means hidden.
- Inspection now retries once without user-name filter if the first filtered scan finds no player item rows.

## R1.1.31

- 保存完成后恢复自动刷新，但刷新改为后台扫描 + 后台 CSV 解析 + UI 分批应用，避免窗口长时间未响应。
- 保存后仍会自动重新读取 `game_state` / `.char` / `world_desc` 并刷新表格；耗时可能较长，但窗口应保持可拖动、可响应。
- 保存/识别期间会临时禁用相关按钮，防止重复任务叠加。
- 调试日志改为批量刷新并只保留最近 600 行，避免 TextBox 反复拼接超长日志造成界面卡死。


## R1.1.45

- Added software-level citizen validation for edited world data:
  - Current job level must be 1-10.
  - Loyalty level must be 0-4.
  - Trait/buff/debuff IDs are normalized and each semicolon/comma/pipe separated ID must exist in the citizen trait database.
- Disabled the dangerous `unsafe-citizen-string-world-write` bypass path after testing showed it can still drop large world arrays and create corrupted saves.
- Trait/buff/debuff changes are now software-validated against the database, but world saving is blocked until a Citizens local-structure patcher is implemented.
  - Default is `false`.
  - When `true`, trait/buff/debuff string edits that pass software validation may bypass the binary header/string-map rejection and continue using the raw-preserve world write path.
  - This is NOT safe for release builds. It is intended only for local experiments on backup saves because previous tests showed that bypassing binary safety checks can produce game-rejected worlds.
- Staged preflight remains enabled; the unsafe mode also runs in preflight before real files are written.


## R1.1.45 note

Citizen trait/buff/debuff IDs are validated in software, but they are not written to `game_state` in this build. Testing showed that bypassing header/string-map validation can cause large world arrays such as `Difficulties` to disappear from the generated file. Numeric citizen changes remain supported through the existing safe write path.


## R1.1.45 / v60 native full writer experiment

- Adds an experimental native ServerGameState static-context writer for citizen trait/buff/debuff changes.
- The tool invokes the game's private GameSaveManager.LoadGameState path by reflection before serialization, so WorldTiles/BiomeIds/Difficulties custom serializers can see ServerGameState.Config.GenerateWorld and the loaded world arrays.
- Trait writes no longer use raw-preserve header bypass. They must pass full decompressed-size, re-deserialization, StateEntry count, and large-array size checks before input/game_state is overwritten.
- This is still experimental. Test only on backups and send latest.log plus world_write_validation_report.json if rejected.


## R1.1.45 / v60 native writer array-name normalization

- Fixes the v58 native writer failure where `System.Single[,]` could be assigned to the native `BiomeIds` `System.String[,]` field during `GameSaveManager.LoadGameState(...)`.
- Before calling native `LoadGameState`, known 2D array entries are normalized by runtime value type: `WorldTile[,] -> WorldTiles`, `string[,] -> BiomeIds`, `float[,] -> Difficulties`.
- Adds `world_write_native_failure_report.json/csv` when native writer fails before the normal validation reports can be generated.


## R1.1.45 / v60 compile fix

- Fixed v59 build error in native failure CSV report generation.
- Replaced missing Csv(...) calls with existing CsvEscape(...).
- No gameplay logic changes from v59.


## R1.1.45 / v61 preflight report copy + native save-state rebuild

- Preflight-generated `world_write*.json/csv` reports are copied back from the temporary `_world_write_preflight_*` output directory to the normal `output/` folder before cleanup, including failure cases.
- The native trait/buff/debuff writer now repairs `ServerGameState.WorldTiles`, `ServerGameState.BiomeIds`, and `ServerGameState.Difficulties` from deserialized state entries before serialization.
- The native writer now attempts to rebuild the outgoing state list through `GameSaveManager.SaveGameState(false)` after loading `ServerGameState` statics, instead of serializing the edited list directly.
- This remains experimental; real files are still protected by decompressed-size, StateEntry, and large-array checks.


## R1.1.48 / v63
- Changed native trait/buff/debuff writer to avoid GameSaveManager.SaveGameState(false) and BaseServer initialization.
- Primes ServerGameState.Config/WorldTiles/BiomeIds/Difficulties from the loaded state, then serializes the edited state list directly with GameStateSaveSerializer.
- Keeps full validation before overwriting input/game_state.


## R1.1.48 / v64

- Trait/buff/debuff native writer now applies a hybrid raw-preserve pass for the three large world array payloads after direct GameStateSaveSerializer output.
- Validation accepts large world array preservation by raw payload identity when saved entry names are shifted/stale in the loaded state list.

## R1.1.50 / v66
- Trait/buff/debuff native writer now seeds the game serializer with the original game_state string-map IDs before serialization.
- This preserves compact string IDs used by raw-preserved world-array StateEntries and appends any new strings after the original map.
- The hybrid writer no longer remaps raw-preserved wrapper IDs when the header is append-only compatible.

## R1.1.51 / v67

- 修复 v66 原生 string map seed 阶段硬编码 `GameStateSaveSerializer.Visited` 导致失败的问题。
- 改为通过反射扫描 serializer 实例中的 string map、visited 集合和 next-id 字段，兼容字段可见性或命名差异。
- 继续保留预验证和失败报告复制，真实 `input/game_state` 仍只在完整校验通过后覆盖。


### v74 safe world pair output

保存村民世界数据后，工具会额外生成 `output/safe_world_pair/`，里面包含匹配的一组 `game_state` 和 `world_desc`。测试世界加载时请成对复制这两个文件，不要只复制根目录下的 `output/game_state`。同时生成 `safe_world_pair_report.json/csv` 记录同步的 GameId、TimePlayed、Broken 等字段。


## v83 / R1.1.67

Fixed output_csv=false strict behavior. Read-only inspection CSV/JSON files are now generated only in a temporary runtime cache and deleted after the UI snapshot is loaded. Visible debug reports remain only when output_csv=true.
