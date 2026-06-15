# Savegame Connector — Feature Plan (Revised)

## Goal

A modular system that lets YAOLlm read the player's latest savegame and expose
collectible progress to the LLM via a **tool call** (`get_collectibles`). Each
game connector ships its own **embedded collectible database** (item names,
locations, categories). The LLM calls the tool when the user asks about progress,
gets structured found/missing data, and helps track what's left to find.

## Decisions Made

| Decision | Choice | Rationale |
|---|---|---|
| Tool call vs prompt injection | **Tool call** | Some games have hundreds of collectibles; injecting into every prompt wastes context |
| Collectible list source | **Embedded per-connector database** | Precise, no LLM hallucination, works for obscure games |
| First game | **Tomb Raider: Underworld** | User is currently playing it; 185 collectibles (179 treasures + 6 relics) |

---

## Part 1: Tool Architecture

### Problem: No tool executor abstraction

Currently, tool execution is hardcoded by string name (`if toolCall.Name == "web_search"`)
duplicated across **3 providers**: `OpenAICompatibleProvider` (line 188),
`OpenRouterProvider` (line 230), and `GeminiProvider` (line 160). Adding a new
tool requires editing all three.

### Solution: `IToolExecutor` interface + registry on providers

```csharp
public interface IToolExecutor
{
    /// Tool name as the LLM sees it, e.g. "get_collectibles"
    string Name { get; }

    /// Tool definition (schema) sent to the LLM
    ToolDefinition Definition { get; }

    /// Execute the tool call and return the result
    Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken ct = default);
}
```

`BaseLLMProvider` gains:
```csharp
protected List<IToolExecutor> CustomToolExecutors { get; } = new();
```

Each provider's dispatch loop changes from:
```csharp
if (toolCall.Name == "web_search" && _searchService != null) { ... }
else if (toolCall.Name == "web_fetch" && _webFetchService != null) { ... }
else { /* unknown */ }
```
to:
```csharp
var executor = CustomToolExecutors.FirstOrDefault(e => e.Name == toolCall.Name);
if (executor != null)
    toolResults.Add(await executor.ExecuteAsync(toolCall, cancellationToken));
else if (toolCall.Name == "web_search" && _searchService != null) { ... }  // unchanged
else if (toolCall.Name == "web_fetch" && _webFetchService != null) { ... }  // unchanged
else { /* unknown */ }
```

**Existing tools stay hardcoded** — no refactor risk. Only the new `get_collectibles`
tool goes through the executor path. This touches all 3 providers but adds only
~5 lines to each dispatch section.

### Tool registration flow

```
Program.cs
  creates SavegameManager + SavegameToolExecutor
  passes executors to PresetManager
    ↓
PresetManager.CreateProvider()
  adds executors to provider.CustomToolExecutors
    ↓
MainForm.SendMessage()
  builds tool list: ToolDefinitions.GetAllWithTts() + executor definitions
  passes to provider.StreamAsync(messages, image, tools)
```

### Tool definition

```csharp
new ToolDefinition(
    "get_collectibles",
    "Get the player's current collectible progress from their latest save file. " +
    "Returns found and missing items grouped by category (treasures, relics, etc.). " +
    "Use this when the player asks about their progress, what they're missing, " +
    "or what to look for next.",
    new
    {
        type = "object",
        properties = new
        {
            category = new
            {
                type = "string",
                description = "Optional: filter to a specific category " +
                              "(e.g. 'treasures', 'relics'). Omit for all categories."
            }
        }
    }
)
```

### Tool result format

The executor returns a formatted string (not raw JSON — LLMs parse markdown
tables well and it's more compact):

```
## Collectible Progress — Tomb Raider: Underworld
Save: 2024-01-15 14:32 | 24% complete

### Treasures: 45/179

**Found by level:**
| Level | Found | Total |
|---|---|---|
| Mediterranean Sea | 5/26 | 26 |
| Coastal Thailand | 12/30 | 30 |
| Croft Manor | 8/13 | 13 |
| Southern Mexico | 15/50 | 50 |
| Jan Mayen Island | 5/30 | 30 |
| Arctic Sea | 0/30 | 30 |

**Missing items:**
- Mediterranean Sea: #3 (Niflheim, near the broken bridge), #8 (God of Thunder, underwater)
- Coastal Thailand: #15 (The Ancient World), #22-30
...

### Relics: 2/6

**Found:** Relic #1 (Mediterranean Sea), Relic #2 (Coastal Thailand)
**Missing:** Relic #3 (Croft Manor), Relic #4 (Southern Mexico), Relic #5 (Jan Mayen), Relic #6 (Arctic Sea)
```

---

## Part 2: Savegame Connector Framework

### Interfaces

```csharp
/// Game-specific connector. One class per game.
public interface ISavegameConnector
{
    string GameId { get; }              // "tomb-raider-underworld"
    string GameName { get; }            // "Tomb Raider: Underworld"

    /// Window title substrings for auto-detection (case-insensitive)
    IReadOnlyList<string> WindowTitlePatterns { get; }

    /// Resolve the path to the latest save file
    string? ResolveLatestSavePath();

    /// Parse the save file and return collectible state
    Task<CollectibleReport?> ReadSaveAsync(string savePath, CancellationToken ct = default);

    /// The complete collectible database for this game (all possible items)
    CollectibleDatabase GetCollectibleDatabase();
}
```

```csharp
/// Standardized collectible data — same shape for every game
public class CollectibleReport
{
    public string GameName { get; set; } = "";
    public DateTime? SaveTimestamp { get; set; }
    public int PercentComplete { get; set; }
    public List<CategoryProgress> Categories { get; set; } = new();
}

public class CategoryProgress
{
    public string Name { get; set; } = "";        // "Treasures"
    public int FoundCount { get; set; }
    public int TotalCount { get; set; }
    public List<ItemProgress> Items { get; set; } = new();
}

public class ItemProgress
{
    public string Name { get; set; } = "";
    public string Level { get; set; } = "";       // "Mediterranean Sea"
    public bool Found { get; set; }
    public string? Notes { get; set; }            // location hint, optional
}
```

```csharp
/// Embedded per-game collectible database
public class CollectibleDatabase
{
    public List<CategoryDefinition> Categories { get; set; } = new();
}

public class CategoryDefinition
{
    public string Name { get; set; } = "";        // "Treasures"
    public List<ItemDefinition> Items { get; set; } = new();
}

public class ItemDefinition
{
    public int Id { get; set; }                   // sequential or game-specific
    public string Name { get; set; } = "";
    public string Level { get; set; } = "";
    public string? LocationHint { get; set; }     // brief description of where
}
```

### SavegameManager (YAOLlm side)

```csharp
public class SavegameManager
{
    private readonly Dictionary<string, ISavegameConnector> _connectors = new();
    private string? _activeGameId;
    private string? _cachedSavePath;
    private DateTime _cachedSaveMtime;
    private CollectibleReport? _cachedReport;

    void RegisterConnector(ISavegameConnector connector);

    /// Detect game from window title. Returns game ID or null.
    string? DetectActiveGame(string windowTitle);

    /// Get current collectible report. Caches by save file mtime.
    Task<CollectibleReport?> GetReportAsync(CancellationToken ct = default);

    /// Format report for tool result
    Task<string> FormatReportAsync(string? categoryFilter, CancellationToken ct);
}
```

Caching strategy:
- On first call, resolve save path + read file + parse
- Cache the `CollectibleReport` + file mtime
- On subsequent calls, check if save file mtime changed
- If changed, re-read; otherwise return cached
- This avoids re-parsing the binary save on every tool call

---

## Part 3: Building the Collectible List

### Two-part problem

| Part | What | Difficulty |
|---|---|---|
| **A: Item database** | What items exist, their names, categories, levels | Easy — community resources |
| **B: Save mapping** | Where each item's collected/not-collected flag lives in the binary save | Hard — reverse engineering |

### Part A: Item database (reliable, straightforward)

Compile from authoritative community sources:

- **Stella's Tomb Raider Site** (tombraiders.net) — complete treasure/relic lists with locations per level
- **WikiRaider** (wikiraider.com) — item names, categories
- **Tomb Raider Chronicles** (tombraiderchronicles.com) — walkthroughs with item locations

For TRU, the complete database is:
- **179 Treasures**: 26 (Mediterranean) + 30 (Thailand) + 13 (Manor) + 50 (Mexico) + 30 (Jan Mayen) + 30 (Arctic)
- **6 Relics**: 1 per main level (Mediterranean, Thailand, Manor, Mexico, Jan Mayen, Arctic)

This gets compiled into a JSON file embedded as a resource:
```json
{
  "categories": [
    {
      "name": "Treasures",
      "items": [
        { "id": 1, "name": "Treasure #1", "level": "Mediterranean Sea", "locationHint": "The Path to Avalon, near entrance" },
        ...
      ]
    },
    {
      "name": "Relics",
      "items": [
        { "id": 1, "name": "Thor's Gauntlet", "level": "Mediterranean Sea", "locationHint": "Niflheim, after solving the hammer puzzle" },
        ...
      ]
    }
  ]
}
```

### Part B: Save mapping (reverse engineering)

#### Current state: UNDOCUMENTED

The `.TRUSave` binary format has **no public documentation** for collectible flags.
The CDC Engine's event system (used in Legend/Anniversary to track collectibles
via `SavedEvent` with `eventVariables[]`) was **removed in Underworld**.
The replacement mechanism is unknown.

#### Methodology: Binary diffing

This is the proven approach for undocumented save formats:

1. **Start a fresh game** — save at the first checkpoint (before collecting anything)
2. **Collect exactly ONE treasure** — save again
3. **Binary diff** the two `.TRUSave` files → find all changed bytes/bits
4. **Collect a second treasure** — save again → diff against step 2
5. **Identify the pattern:**
   - Individual bit flags? (1 bit per item, packed into bytes)
   - Boolean array? (1 byte per item)
   - Collected-item-ID list? (variable-length array of IDs)
   - Counter + bitmask?
6. **Map each flag position to a specific item** by collecting items in a known order
7. **Validate** — create a save with known items collected, verify parser reads them correctly

#### Practical considerations

- **Save before collecting**: Use the game's checkpoint/autosave system. Collect
  one item, reach next checkpoint, save.
- **Multiple saves**: Keep incremental save files (`save1.TRUSave`, `save2.TRUSave`)
  for diffing.
- **Expected data size**: 185 collectibles could be:
  - 24 bytes if bitfield (185 bits ÷ 8)
  - 185 bytes if byte-per-item
  - Variable if ID list
- **Header knowns**: The `SaveGameHeader` from the CDC engine has `percentComplete`
  (1 byte) and `unlockedItemsCount` (2 bytes) — these can serve as validation
  anchors during reverse engineering.

#### Fallback approaches if binary diffing is blocked

If the save format proves too complex (encryption, compression, checksumming):

1. **Memory reading**: Use the TRLAU-menu-hook ASI loader to inspect runtime
   game state. Read collectible flags from RAM while the game runs.
   - Pro: easier to find (memory editors, known offsets from modding community)
   - Con: requires game running, more fragile, needs memory pattern updates

2. **Progress percentage only**: Read just `percentComplete` from the header.
   Less granular but still useful ("you're at 24% completion").

3. **Checksum patching**: If saves are checksummed, find and recalculate the
   checksum after parsing. Required for any write operations (not needed for
   read-only collectible tracking).

---

## Part 4: Tomb Raider Underworld Connector

### Save file details

| Property | Value |
|---|---|
| Location | `%USERPROFILE%\Documents\Eidos\Tomb Raider - Underworld\` |
| Extension | `.TRUSave` |
| Latest save | Named save or `autosave.TRUSave` (by mtime) |
| Format | Binary (CDC Engine proprietary) |
| Steam Cloud | No |
| Header (from TR7 decompilation) | `SaveGameHeader` struct (flags, saveVersion, percentComplete, etc.) |

### Window title detection

The game window title contains "TOMB RAIDER: UNDERWORLD" or "Tomb Raider: Underworld".
`WindowTitlePatterns = ["Tomb Raider: Underworld", "TOMB RAIDER: UNDERWORLD"]`

### Implementation plan

```
TRUnderworldConnector
├── ResolveLatestSavePath()     → scan .TRUSave files, return newest by mtime
├── ReadSaveAsync(path)         → parse binary, extract collectible flags
│   ├── ParseHeader()           → SaveGameHeader (known struct from cdcEngine)
│   ├── ParseCollectibleFlags() → THE REVERSE ENGINEERED PART
│   └── BuildReport()           → match flags to embedded database
├── GetCollectibleDatabase()    → load embedded TRU_collectibles.json
└── WindowTitlePatterns         → ["Tomb Raider: Underworld", ...]
```

---

## Part 5: Implementation Phases

### Phase 1: Tool Executor Framework (no game-specific code)
**Files:** `IToolExecutor.cs`, `SavegameToolExecutor.cs`, modifications to `BaseLLMProvider`, `OpenAICompatibleProvider`, `OpenRouterProvider`, `GeminiProvider`, `PresetManager`, `MainForm`, `Program.cs`, `ToolDefinitions.cs`

- [ ] Define `IToolExecutor` interface
- [ ] Add `CustomToolExecutors` list to `BaseLLMProvider`
- [ ] Add executor dispatch (5 lines) to each provider's tool call handler
- [ ] Add executor definitions to tool list in `MainForm.cs`
- [ ] Wire executor registration through `PresetManager`
- [ ] Test: create a mock executor that returns hardcoded data, verify LLM calls it

### Phase 2: Connector Framework (no real game connector)
**Files:** `ISavegameConnector.cs`, `CollectibleReport.cs`, `CollectibleDatabase.cs`, `SavegameManager.cs`, `Savegame/`

- [ ] Define `ISavegameConnector`, `CollectibleReport`, `CollectibleDatabase`, `ItemProgress`
- [ ] Implement `SavegameManager` (registry, window title detection, mtime caching, formatting)
- [ ] Implement `SavegameToolExecutor` (calls `SavegameManager.FormatReportAsync`)
- [ ] Create mock connector for testing
- [ ] Test end-to-end: mock connector → tool call → LLM gets formatted report

### Phase 3: TRU Collectible Database
**Files:** `Savegame/Connectors/TRU/collectibles.json` (embedded resource)

- [ ] Compile 179 treasure entries from Stella's site / WikiRaider
- [ ] Compile 6 relic entries
- [ ] Add location hints per item
- [ ] Validate counts per level (26+30+13+50+30+30 = 179)

### Phase 4: TRU Save Reverse Engineering
**This is the hard part. Requires playing the game with controlled saves.**

- [ ] Create initial save (fresh game, first checkpoint, no collectibles)
- [ ] Binary-diff workflow: collect 1 treasure → save → diff
- [ ] Identify collectible flag encoding pattern
- [ ] Map flag positions to item IDs (systematic collection order)
- [ ] Validate with a save containing known items
- [ ] Document findings (offset table) in the connector code/data

### Phase 5: TRU Connector Implementation
**Files:** `Savegame/Connectors/TRUnderworldConnector.cs`

- [ ] Implement `ResolveLatestSavePath()` (scan `.TRUSave` directory)
- [ ] Implement `ParseHeader()` using known `SaveGameHeader` struct
- [ ] Implement `ParseCollectibleFlags()` using reverse-engineered offsets
- [ ] Implement `BuildReport()` — match flags to database items
- [ ] Implement `GetCollectibleDatabase()` — load embedded JSON
- [ ] Error handling: file not found, file locked, corrupt/unknown format
- [ ] Test end-to-end: play TRU → collect items → ask YAOLLM about progress

### Phase 6: Polish
- [ ] UI status: "Reading savegame..." when tool is called
- [ ] System prompt: mention the tool is available when a supported game is detected
- [ ] Graceful degradation: no connector for current game → tool returns helpful message
- [ ] Config: `SAVEGAME_CONNECTOR_ENABLED`, per-game path overrides
- [ ] Logging: detection, reads, cache hits/misses, parse errors

---

## File Layout

```
YAOLlm/
├── IToolExecutor.cs                    # NEW — interface
├── ToolDefinitions.cs                  # MODIFIED — add get_collectibles def (optional, or from executor)
├── Savegame/                           # NEW directory
│   ├── ISavegameConnector.cs
│   ├── CollectibleReport.cs            # CollectibleReport, CategoryProgress, ItemProgress
│   ├── CollectibleDatabase.cs          # CollectibleDatabase, CategoryDefinition, ItemDefinition
│   ├── SavegameManager.cs
│   ├── SavegameToolExecutor.cs         # IToolExecutor impl
│   └── Connectors/
│       ├── MockConnector.cs            # Phase 2 testing
│       └── TRUnderworld/
│           ├── TRUnderworldConnector.cs
│           ├── collectibles.json       # embedded resource (185 items)
│           └── save_format.md          # reverse engineering notes
├── Providers/
│   ├── BaseLLMProvider.cs              # MODIFIED — add CustomToolExecutors
│   ├── OpenAICompatibleProvider.cs     # MODIFIED — add executor dispatch (~5 lines)
│   ├── OpenRouterProvider.cs           # MODIFIED — add executor dispatch (~5 lines)
│   └── GeminiProvider.cs              # MODIFIED — add executor dispatch (~5 lines)
├── PresetManager.cs                    # MODIFIED — accept + pass tool executors
├── MainForm.cs                         # MODIFIED — build tool list with executor defs
├── Program.cs                          # MODIFIED — wire SavegameManager + executor
├── ConversationManager.cs             # MODIFIED — mention tool in prompt when game detected
└── plans/
    └── savegame-connector.md           # this file
```

---

## Key Risks & Mitigations

| Risk | Impact | Mitigation |
|---|---|---|
| TRU save format too complex (compression/encryption) | Blocks Phase 4 | Fall back to memory reading or percent-complete only |
| Save file locked while game is running | Can't read save | Open with `FileShare.ReadWrite`; fall back to last readable copy |
| Tool result too large for context | LLM gets confused | Summarize by level; full detail only on `category` filter |
| Provider doesn't support tools (Ollama) | Tool unavailable | Graceful: don't register tool for Ollama; system prompt notes limitation |
| Save format changes between game versions | Parser breaks | Version check via `saveVersion` header field; log warning on unknown version |

---

## Open Questions

1. **Binary diffing logistics** — Phase 4 requires controlled save pairs. Do you
   have the ability to create saves at will (checkpoints), or do you need a
   specific setup? This affects the reverse engineering workflow.

2. **Memory reading fallback** — If save parsing fails, should we implement
   memory reading via the TRLAU-menu-hook ASI loader as a backup? This adds
   significant complexity but is more reliable for some games.

3. **TRU save file sharing** — For reverse engineering, having multiple save
   files at different collection states would accelerate the process. Can you
   provide saves from different progress points?
