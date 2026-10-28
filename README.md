# FactorioMCP

An MCP (Model Context Protocol) server that connects to a running Factorio game via RCON and lets an AI agent play the game realistically — walking, crafting, building — without cheating.

```
LLM (Claude/GPT/etc) ←→ [MCP Server (C#, stdio)] ←→ [RCON TCP] ←→ Factorio Game
```

## Key Principle

The AI player **does not cheat**. It walks to locations using real game physics, waits for crafting to finish, and only interacts with entities within range. No teleportation, no spawning items, no skipping time.

## Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- [Factorio](https://factorio.com/) (any edition that supports RCON)

## Setup

### 1. Launch Factorio with RCON

**Singleplayer:**
```sh
factorio.exe --rcon-port 27015 --rcon-password mypassword
```

**Dedicated server:**
```sh
factorio --start-server save.zip --rcon-port 27015 --rcon-password mypassword
```

### 2. Settings

All settings are in `appsettings.json` next to `FactorioMCP.exe`; environment variables are not read. On the first start the server writes this file with the defaults, then you fill in the RCON password. The folder is self-contained: copy it to another PC and run it there.

| Setting | Default | Description |
|---------|---------|-------------|
| `Rcon:Host` | `127.0.0.1` | RCON server host |
| `Rcon:Port` | `27015` | RCON server port |
| `Rcon:Password` | empty (required) | RCON password |
| `Agent:Name` | `agent` | Name of the agent character this instance controls (default mode) |
| `Agent:Color` | empty | Character colour: `#RRGGBB`, `r,g,b` (0-255) or `none`; empty = red for the agent `engineer`, default colour for others |
| `Agent:ControlPlayer` | empty | Name of a connected player to control instead of an agent character |
| `BuildingsFile` | `buildings.json` | File for the building memory; a relative path is inside the exe's folder |
| `RestrictedArea:CenterX`, `CenterY`, `Width`, `Height` | `0` | Rectangle on nauvis that only humans may enter. Width or Height 0 = no area, the feature is off |

```jsonc
{
  "Rcon": { "Host": "127.0.0.1", "Port": 27015, "Password": "mypassword" },
  "Agent": { "Name": "agent", "Color": "", "ControlPlayer": "" },
  "BuildingsFile": "buildings.json",
  "RestrictedArea": { "CenterX": 0, "CenterY": 0, "Width": 0, "Height": 0 }
}
```

By default the server controls an agent character: a character entity created by script near spawn on first use, with no player or client behind it. It works on a headless server, and people can join the game with their own players next to it. The character persists in the save under its `Agent:Name`. Set `Agent:ControlPlayer` to control a connected player instead. Blueprint tools work in both modes.

To run several agents in one game, give each one its own settings file, each with its own `Agent:Name` and `BuildingsFile`: either copy the whole folder, or start the same exe with `--settings agent2.json` (a relative path is inside the exe's folder; a missing file is created with the defaults). Walking and mining state is kept per character, so the instances do not interfere.

**Restricted area.** With `RestrictedArea` set, agents learn about the area at session start (server instructions), in the `set_agent_name` result and from `get_restricted_areas`. Tool calls that target a position inside it (`x`/`y`, `targetX`/`targetY`, `x1..y2` areas, the entries of `targets`, `placementsJson` and `planJson`) are refused with error `restricted_area`, the parameter and the nearest allowed position; walks to targets inside it, or blocked by it, carry a `restricted_area_note`. The agent `engineer` (the MCP developers' agent) is exempt. Changes to the file take effect without a restart, except the session-start instructions.

### 3. Build

```sh
dotnet build
```

## Connecting an MCP Client

### Claude Desktop

Add to your `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "factorio": {
      "command": "dotnet",
      "args": ["run", "--project", "path/to/FactorioMCP"]
    }
  }
}
```

### VS Code (GitHub Copilot)

A pre-configured [`.vscode/mcp.json`](.vscode/mcp.json) is included in the repository. Open the workspace in VS Code and it will be detected automatically. Set the RCON password in `appsettings.json` next to the built exe.

<details>
<summary>Manual setup</summary>

Add to your `.vscode/mcp.json`:

```json
{
  "servers": {
    "factorio": {
      "type": "stdio",
      "command": "dotnet",
      "args": ["run", "--project", "${workspaceFolder}/FactorioMCP"]
    }
  }
}
```

</details>

### LM Studio (Local Models)

[LM Studio](https://lmstudio.ai/) can connect to MCP servers via its built-in MCP client. This is a good option for running a local model without cloud API costs.

1. **Install LM Studio** and download a model (recommended: `qwen3-32b` or similar with tool-calling support).

2. **Enable MCP** in LM Studio: go to **Settings → MCP** (or the MCP tab in the chat sidebar).

3. **Add the FactorioMCP server** with this configuration:

   ```json
   {
     "factorio": {
       "command": "dotnet",
       "args": ["run", "--project", "path/to/FactorioMCP"]
     }
   }
   ```

4. **Start a chat** with the model — tools will appear in the MCP tools panel. The model will call tools to interact with Factorio.

> **Note:** Local models may have weaker tool-calling compared to cloud models. If the model struggles to use tools correctly, try a larger parameter model or one specifically fine-tuned for function calling.

## Available Tools

> **Full reference**: See [TOOLS.md](TOOLS.md) for detailed parameters, return values, and AI prompt engineering tips.

### Movement

| Tool | Description |
|------|-------------|
| `WalkForDuration` | Walk in a direction for N seconds, then stop. Returns position after walking. |
| `StopWalking` | Stop the player from walking immediately. |
| `GetPlayerPosition` | Get the player's current map position. |

### Inventory & Crafting

| Tool | Description |
|------|-------------|
| `GetInventory` | List all items and counts in the player's main inventory. |
| `Craft` | Begin crafting items using a recipe. Respects real crafting time. |
| `GetCraftingQueue` | Get the current crafting queue contents. |

### Entity Placement & Mining

| Tool | Description |
|------|-------------|
| `PlaceEntity` | Place an entity from inventory at map coordinates (with direction). Automatically tracked in building memory. |
| `MineEntity` | Mine/remove an entity at map coordinates into inventory. Automatically untracked from building memory. |

### Entity Interaction

| Tool | Description |
|------|-------------|
| `InsertItems` | Insert items from player inventory into a machine (fuel, ore, etc.). |
| `RemoveItems` | Remove items from a machine's inventory into player inventory. |
| `InspectEntity` | Inspect entity status, inventories, fuel, recipe, and health. |

### World Scanning

| Tool | Description |
|------|-------------|
| `GetNearbyEntities` | List all entities near the player within a given radius. |
| `CheckDistance` | Check distance to coordinates and whether within build/reach range. |
| `ScanResources` | Scan for resource patches (ores, oil) with type summaries and center coordinates. |
| `ScanTiles` | Scan terrain tiles for type counts (grass, sand, water, etc.). |

### Wait & Polling

| Tool | Description |
|------|-------------|
| `WaitForCrafting` | Wait for the crafting queue to empty. |
| `WaitForPosition` | Wait until the player reaches target coordinates. |
| `WaitForTicks` | Wait for a specified number of game ticks to elapse. |
| `GetGameTick` | Get the current game tick. |

### Chat

| Tool | Description |
|------|-------------|
| `InitializeChatListener` | Initialize/re-initialize the chat message event listener. |
| `GetChatMessages` | Get chat messages from the in-game chat log (with polling support). |
| `SendChatMessage` | Send an `[AI]`-tagged chat message visible to all players. |

### Energy

| Tool | Description |
|------|-------------|
| `GetElectricNetwork` | Get electric network stats: production, consumption, satisfaction, accumulators. |
| `InspectEntityPower` | Inspect per-entity power status: connection, energy, buffer, drain. |

### Research

| Tool | Description |
|------|-------------|
| `GetResearchStatus` | Get current research technology and progress percentage. |
| `GetAvailableTechnologies` | List all technologies available for research (prerequisites met). |
| `StartResearch` | Start researching a technology. |

### Recipe & Technology Queries

| Tool | Description |
|------|-------------|
| `GetRecipeDetails` | Get recipe ingredients, products, crafting time, and category. |
| `GetAvailableRecipes` | List all currently unlocked recipes. |
| `GetTechnologyDetails` | Get technology prerequisites, effects, cost, and ingredients. |

### Building Memory

| Tool | Description |
|------|-------------|
| `GetAllBuildings` | Get all tracked buildings with names, positions, directions, and labels. |
| `GetBuildingsNear` | Get tracked buildings near a position within a radius. |
| `FindBuildingsByType` | Find all tracked buildings of a specific entity type. |
| `GetBuildingSummary` | Get counts of each tracked building type. |
| `UpdateBuildingLabel` | Set or remove a label on a tracked building. |
| `ClearBuildingMemory` | Clear all tracked buildings from AI memory (not from game world). |

### Blueprints & Ghosts

| Tool | Description |
|------|-------------|
| `PlaceGhostEntity` | Place a ghost (construction plan) for an entity at map coordinates. |
| `PlaceBlueprintString` | Import and build a blueprint string at a position. |
| `GetGhostEntities` | Scan for ghost entities in an area. |
| `CreateBlueprintFromArea` | Capture an area as an exportable blueprint string. |
| `RevokeGhostEntity` | Remove a ghost entity at a position. |

### Advanced

| Tool | Description |
|------|-------------|
| `ExecuteLua` | Execute arbitrary Lua code via RCON. Use for advanced operations not covered by other tools. |

## Project Structure

```
FactorioMCP/
├── Program.cs                          # Host setup, DI, MCP server wiring
├── Rcon/
│   ├── RconClient.cs                   # Source RCON protocol over TCP
│   └── RconPacket.cs                   # RCON packet types and record
├── Models/
│   └── TrackedBuilding.cs              # Building memory data model
├── Services/
│   ├── FactorioService.cs              # High-level game operations via Lua
│   ├── EnergyService.cs                # Electric network inspection via Lua
│   ├── BlueprintService.cs             # Blueprint and ghost entity operations
│   ├── BuildingMemoryService.cs        # Building tracking with JSON persistence
│   ├── GameCommandQueue.cs             # Serializes concurrent MCP tool calls
│   └── RconConnectionService.cs        # RCON connection on startup
├── Tools/
│   ├── MovementTools.cs                # Walk, stop, get position
│   ├── InventoryTools.cs               # Inventory, crafting
│   ├── EntityTools.cs                  # Place/mine entities
│   ├── InteractionTools.cs             # Insert/remove items, inspect entities
│   ├── WorldTools.cs                   # Nearby entities, distance, scan
│   ├── WaitTools.cs                    # Wait for crafting, position, ticks
│   ├── ChatTools.cs                    # Chat message listener, send/receive
│   ├── EnergyTools.cs                  # Electric network and power inspection
│   ├── ResearchTools.cs                # Research status, start research
│   ├── RecipeTools.cs                  # Recipe and technology queries
│   ├── BuildingTools.cs                # Building memory queries
│   ├── BlueprintTools.cs               # Blueprint and ghost entity tools
│   └── LuaTools.cs                     # Raw Lua execution
└── Resources/
    └── GameStateResources.cs           # MCP Resources (read-only game state)

FactorioMCP.Tests/                      # Unit tests (xUnit)
```

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for workflow guidelines, CPX complexity scale, and coding conventions.

## License

See repository for license details.