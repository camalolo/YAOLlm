# Collectible Database Format

## Schema

```json
{
  "gameId": "string",
  "gameName": "string",
  "categories": [
    {
      "name": "string",
      "items": [
        {
          "id": 0,
          "name": "string",
          "level": "string",
          "hint": "string (optional)"
        }
      ]
    }
  ]
}
```

## Rules

- `id`: Zero-based sequential integer within a category. **Maps directly to the
  save file's collection bit/byte/array index.** This is the bridge between the
  database and the binary save.
- `categories`: Each category is independent — ids restart at 0 per category.
- `level`: Logical grouping (level name, area, chapter). Used for display and
  progress-by-level breakdowns.
- `hint`: One-sentence location description. Optional but recommended.
- `gameId`: Must match `ISavegameConnector.GameId`.

## Example

```json
{
  "gameId": "tomb-raider-underworld",
  "gameName": "Tomb Raider: Underworld",
  "categories": [
    {
      "name": "Treasures",
      "items": [
        { "id": 0, "name": "Treasure I", "level": "Mediterranean Sea", "hint": "The Path to Avalon — near the entrance" },
        { "id": 1, "name": "Treasure II", "level": "Mediterranean Sea", "hint": "Niflheim — behind the broken bridge" }
      ]
    },
    {
      "name": "Relics",
      "items": [
        { "id": 0, "name": "Thor's Gauntlet", "level": "Mediterranean Sea", "hint": "Niflheim — after the hammer puzzle" }
      ]
    }
  ]
}
```
