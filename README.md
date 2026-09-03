# Duel Masters TCG Engine

A modern, real-time digital recreation of the classic **Duel Masters** trading card
game, built with:

- **Godot 4.x (C#)** for the client — 2D/2.5D arena with a Master-Duel-inspired
  presentation (hover feedback, tap animations, shield-break VFX).
- A **pure C# rules library** (`DuelMasters.Domain`) shared by the client, the
  tests, the AI, and (later) the authoritative backend — zero engine dependencies.
- A **protocol-neutral JSON WebSocket contract** so the backend can run on
  **.NET 9 (SignalR)** today and be swapped to **NestJS (Socket.io)** or
  **Spring Boot (STOMP)** later without touching the client.
- A **Python ingestion pipeline** that turns raw card PNG/JPEG images into a
  structured `cards.json` database.

## Status

| Phase | Description | Status |
| ----- | ----------- | ------ |
| 0 | Structure, tooling, git, CI-ready baseline | ✅ Done |
| 1 | Card JSON schema + ingestion pipeline + starter set | ✅ Done |
| 1.5 | .NET 10 backend (JWT auth, deck CRUD) + Postgres + Deck Builder | ✅ Done |
| 2 | Domain rules engine (turn machine, combat, shield triggers) + xUnit tests | ✅ Done |
| 3 | Godot 2.5D board UI + local hotseat sandbox | ✅ Done |
| 4 | Authoritative .NET / SignalR backend + client transport | ⏳ |
| 5 | AI opponent | ⏳ |
| 6 | Shaders, VFX, sound polish | ⏳ |

Game rules and architecture are specified in the design documents bundled in the
repo root (`Duel_Masters_TCG_Engine_GDD.md`, `Duel_Masters_Strategy_and_Codebase.md`).


## Requirements

- **Godot 4.3 or newer — the .NET edition** (`Godot_v4.x-stable_mono_...`)
- **.NET SDK 8.0 or newer** (`net8.0` targets; the project rolls forward to run on
  newer runtimes)
- **Python 3** (only for card ingestion)
- Git + [Git LFS](https://git-lfs.com) (art assets are LFS-tracked)

## Getting started

1. Clone the repository.
2. Open `project.godot` in the **.NET edition** of Godot.
3. Build the C# solution (`.godot/mono` auto-builds in the editor, or run
   `dotnet build -c Debug`).
4. Run the project (optionally start the Phase 1.5 backend so login and deck save/load
   work: `docker compose up` under `backends/`). The project's main scene is the
   **login/register screen** (`src/scenes/auth/`), which stores the signed-in session
   on the `Global` autoload and then opens the **main menu** (`src/ui/main_menu/`).
   From there you can launch the **hotseat arena** (`src/scenes/arena/`, a local
   2-player sandbox that plays straight against the shared `DuelMasters.Domain` rules
   engine) or the **Deck Builder** (`src/scenes/deck_builder/`). If the backend is
   offline, use **Continue as Guest** to reach the menu anyway and play sans save.

   > **Window sizing in the editor:** the editor's **Game view** remembers its own window
   > geometry (embedded vs floating, and size) in the git-ignored `.godot/editor/*.cfg`,
   > separate from `project.godot`. To test the **Display Settings** (⚙ → Display Settings,
   > or the Settings button in the main menu) reliably, run the game in a **separate window**:
   > in the Game panel's run-mode menu uncheck **"Embed on next play"** (and keep **"Make
   > game workspace floating on next play"** checked), then play and resize once. The game
   > itself defaults to **windowed 1920×1080**.

## Building & testing

```bash
dotnet build -c Debug      # builds client + domain library
dotnet test                # runs the DuelMasters.Domain rules tests
```

## Project structure

Layout follows the [Project-Structure](https://github.com/FatEarthStudios) C#
template conventions (one class per file, namespace per folder, `_camelCase`
privates, `[Export]`/`[GlobalClass]` exposure).

```text
duel_masters/
├─ assets/                   art/ (cards, ui) + cards_raw/ (ingestion source)
├─ docs/                     rules reference and architecture notes
├─ src/
│  ├─ core/                  Global autoload, MainGame entry point
│  ├─ rules/                 DuelMasters.Domain — pure C# rules library
│  │   ├─ Model/             cards, players, zones, game state, enums
│  │   ├─ Engine/            deterministic turn/duel state machine
│  │   ├─ Commands/          play, summon, cast, attack, block, end turn…
│  │   ├─ Effects/           keyword + scripted effect registry
│  │   └─ Net/               protocol-neutral JSON DTOs
│  ├─ gameplay/              board, card view, player, AI
│  ├─ ui/                    menus, deck builder, HUD
│  ├─ resources/             cards.json + card data loaders
│  ├─ debug/                 FPS/version overlay
│  └─ shaders/               VFX shaders
├─ tests/DuelMasters.Domain.Tests/   xUnit tests for the rules engine
└─ tools/
   ├─ editor/                ApplyProjectSettings editor script
   └─ card_ingestion/        ingest_cards.py (PNG/JPG → cards.json)
```

## Card data pipeline

1. Drop raw card images (PNG/JPEG) into `assets/cards_raw/`.
2. Run `python tools/card_ingestion/ingest_cards.py`.
3. The tool produces `src/resources/data/cards.json`, consumed by the domain
   library and the Godot client.

## Networking

The client talks to the backend over a neutral JSON message schema
(`action` / `sessionId` / `playerId` / `payload`), so the transport is
interchangeable. The authoritative .NET 9 SignalR hub is Phase 4.

## Architecture docs

- `Duel_Masters_TCG_Engine_GDD.md` — full game/system design specification
- `Duel_Masters_Strategy_and_Codebase.md` — strategy/feasibility + reference code

## License

MIT — see `LICENSE`. Art assets must be rights-cleared before committing
(see `ASSETS_LICENSE.md`).