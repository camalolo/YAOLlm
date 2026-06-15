# TRU .TRUSave Format — Reverse Engineering Notes

## What We Know

### File Structure

```
0x0000 - 0x0027   Header metadata
  0x00: "RGMH" magic (4 bytes)
  0x04: version (uint32 = 1)
  0x08: data offset = 0x2028
  0x0C: header size = 0x28
  0x14: PNG screenshot size (~44729 bytes)
  
0x0028 - 0x2027   Game title (UTF-16LE "Tomb Raider: Underworld") + zero padding

0x2028 - 0x2050   Pre-PNG metadata block
  0x2028: "PCUD" tag (DCUP format marker)
  0x2030: "SURT" tag (TRU Save marker)
  0x2038: level/chapter index (uint32 = 1 = Mediterranean)
  0x203C: timestamp/hash (uint32)
  0x2040: timestamp/hash (uint32)
  0x2044: game data size (uint32 = 0x140000 = 1,310,720)
  0x2048: game data offset (uint32 = 0xCF09)
  0x204C: checksum (uint32)

0x2050 - 0xCF08   PNG screenshot (variable, ~44KB)
  Standard PNG with IHDR, iCCP (Photoshop profile), IDAT, IEND
  256x141 pixels, RGBA

0xCF09 - end      Game state data (~1.31 MB)
  0xCF0C: save type/version (uint32 = 1)
  0xCF14: level name length + padding
  0xCF19: level name string (null-terminated)
    e.g. "l1_norse_hallway_a", "l1_kraken_entrance"
  ... serialized instance state data follows
```

### Game Data Section

The ~1.3MB game data section contains serialized game objects (instances).
Each instance has position, rotation, state flags, and unique IDs.

Key patterns found:
- `0x11000000` (savedID=17) appears 456 times — likely instance state entries
- Each entry followed by data including a uint16 instance ID (decrementing sequence)
- Format is NOT simple tag+size blocks; structure is complex and interleaved

### Relevant Exe Symbols (RTTI)

| Class | Purpose |
|---|---|
| `NtRewardSystem` | Manages collectible rewards (treasures/relics) |
| `collectableholder` | Holds collectible state in-game |
| `NtUnlockablesSystem` | Tracks unlockable content (concept art, etc.) |
| `evControlSaveData` | Event-based save data for gear/weapon ownership |
| `evObjectPickup` | Pickup event handler |

### Debug Log Strings in Exe

```
evControlSaveData: gear %s %s owned
evControlSaveData: rbweapon %s %s owned
evObjectPickup: gear %s
evObjectPickup: weapon %s
INSTANCE_IntroduceSavedInstance %s:%s%d
PDA Map failed to save
```

## What We Don't Know

1. **Where collectible flags are stored** — The event system was removed in Underworld.
   Collectible state could be:
   - Per-instance "dead/collected" flags (like Legend/Anniversary)
   - A global bitfield in an unknown section
   - Part of the `NtRewardSystem` serialized state

2. **How to parse the instance list** — The exact serialization format is undocumented.
   The CDC engine's `SavedBasic` header format from Soul Reaver 2 may not apply.

3. **Checksum mechanism** — Bytes at 0x203C, 0x2040, 0x204C are unknown hashes.
   Modifying save data may require recalculating these.

## Save File Inventory (User's PC)

Location: `C:\Users\camal\OneDrive\Documents\Eidos\Tomb Raider - Underworld\`

| File | Size | Modified | Level Area |
|---|---|---|---|
| MEDITERRANEAN...TRUSave | 1,363,721 | Jun 14 15:20 | l1_kraken_entrance |
| Savegame 2.TRUSave | 1,363,721 | Jun 14 15:33 | l1_kraken_entrance |
| Savegame 1.TRUSave | 1,363,721 | Jun 15 14:42 | l1_norse_hallway_a |
| Autosave.TRUSave | 1,363,721 | Jun 15 14:40 | l1_norse_hallway_a |

All saves are Level 1 (Mediterranean Sea), different sub-areas.
Savegame 1 vs Autosave differ by only 4 bytes (timer/health, not collectibles).

## Why Static Analysis Hit a Wall

All 4 saves are at **different sub-areas** of the same level. When the player moves
between sub-areas, hundreds of instance states change (positions, activation flags,
physics state), producing 28,000+ byte diffs that drown out collectible flag changes.

## What's Needed: Controlled Binary Diffing

To isolate collectible flags, we need save pairs where the **only difference** is
one treasure collected:

### Step-by-step procedure

1. **Load a save at a known sub-area** (e.g., "The Path to Avalon")
2. **Play to the next checkpoint WITHOUT collecting anything** → Save as `baseline.TRUSave`
3. **Reload, then collect exactly ONE treasure** → Reach next checkpoint → Save as `plus1.TRUSave`
4. **Copy both files out** (rename to avoid overwriting)

### Analysis from controlled saves

With only ~1 collectible difference:
- Diff should show 1-10 changed bytes (flag + maybe counter + checksum)
- Pattern identification: bitfield? byte-per-item? ID list?
- Collect treasure #2 → save → diff against `plus1` to confirm pattern
- Map each flag position to specific treasure ID

### Alternative: Memory Reading

If save diffing proves impractical (checksums, compression), read collectible
state from game RAM while the game runs:
- Use the TRLAU-menu-hook ASI loader to inspect `NtRewardSystem` state
- Or use Cheat Engine to find treasure count values by scanning
