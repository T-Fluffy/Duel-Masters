# Duel Masters TCG Engine

![Godot 4](https://img.shields.io/badge/Godot-4.7-%23478cbf?logo=godotengine&logoColor=white&style=for-the-badge)
![C#](https://img.shields.io/badge/C%23-.NET%208-%23512bd4?logo=csharp&logoColor=white&style=for-the-badge)
![ASP.NET Core (SignalR)](https://img.shields.io/badge/ASP.NET%20Core-10-%235b0fb5?logo=dotnet&logoColor=white&style=for-the-badge)
![PostgreSQL](https://img.shields.io/badge/PostgreSQL-16-%234169e1?logo=postgresql&logoColor=white&style=for-the-badge)
![xUnit](https://img.shields.io/badge/xUnit-tests-%23c21325?style=for-the-badge)
![Python](https://img.shields.io/badge/Python-3-%233776ab?logo=python&logoColor=white&style=for-the-badge)
![Docker Compose](https://img.shields.io/badge/Docker-Compose-%232496ed?logo=docker&logoColor=white&style=for-the-badge)

A modern, real-time digital recreation of the classic **Duel Masters** trading card
game, built with:

- **Godot 4.x (C#)** for the client — 2D/2.5D arena with a Master-Duel-inspired
  presentation (hover feedback, tap animations, shield-break VFX).
- A **pure C# rules library** (`DuelMasters.Domain`) shared by the client, the
  tests, the AI, and (later) the authoritative backend — zero engine dependencies.
- A **protocol-neutral JSON WebSocket contract** so the backend can run on
  **.NET 10 (SignalR)** today and be swapped to **NestJS (Socket.io)** or
  **Spring Boot (STOMP)** later without touching the client.
- A **Python ingestion pipeline** that turns raw card PNG/JPEG images into a
  structured `cards.json` database (OCR + web-scrape → keyword/effect mapping for
  **DM01–09**, including evolution creatures).

## Status

| Phase | Description | Status |
| ----- | ----------- | ------ |
| 0 | Structure, tooling, git, CI-ready baseline | ✅ Done |
| 1 | Card JSON schema + ingestion pipeline + starter set | ✅ Done |
| 1.5 | .NET 10 backend (JWT auth, deck CRUD) + Postgres + Deck Builder | ✅ Done |
| 2 | Domain rules engine (turn machine, combat, shield triggers) + xUnit tests | ✅ Done |
| 3 | Godot 2.5D board UI + local hotseat sandbox | ✅ Done |
| 4 | Authoritative .NET / SignalR backend + client transport | ✅ Done |
| 5 | AI opponent | ✅ Done |
| 6 | Shaders, VFX, sound polish | ⏳ |

## Implemented features

- **Full turn machine** — mana charging (one card per turn), summoning, spell
  casting, creature attacks + blocks (sick/tapped rules), shield triggers, and
  the 0-shields ⇒ win condition.
- **Attack UX** — tap-tiles on the arena highlight attackable targets, attacker
  selection cancels by clicking the attacker again or pressing **Esc**, and a
  prompt hints when another untapped creature can still attack.
- **Shield VFX** — shield breaks animate the card flying to the owner's hand
  (face-up for player 1 / hotseat, face-down for the AI).
- **Evolution mechanic** — evolution creatures can be played onto an
  `evolutionOf`-matching base creature (GUI + AI), sit on top of the base, and
  the whole stack goes to the graveyard together. Full model/engine/AI/UI
  support with dedicated tests.
- **Skilled AI opponent** — evolutions, effect activations, direct attacks and
  blocks via trigger handling; played in hotseat or guest-vs-AI.
- **Online duels** — server-authoritative SignalR matches; the status panel now
  shows real player names and the winner. Hosts and joiners can each bring one of
  their **saved decks** from the deck builder (or fall back to a random deck),
  and the live board supports evolution, blocking and shield triggers exactly like
  the hotseat arena.

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
4. Run the project. Optionally start the backend stack (Postgres + the ASP.NET server,
   which hosts the Phase 1.5 REST API **and** the Phase 4 SignalR `DuelHub` at
   `http://127.0.0.1:8080/duel`) from the repo root:

   ```bash
   docker-compose -f backends/docker-compose.yml up --build -d
   ```

   (If Docker Desktop isn't running, start it first; the stack waits for Postgres to
   become healthy before starting the server.) Stop it later with
   `docker-compose -f backends/docker-compose.yml down`. The project's main scene is the
   **login/register screen** (`src/scenes/auth/`), which stores the signed-in session
   on the `Global` autoload and then opens the **main menu** (`src/ui/main_menu/`).
   From there you can launch the **hotseat arena** (`src/scenes/arena/`, a local
   2-player sandbox that plays straight against the shared `DuelMasters.Domain` rules
   engine), the **Deck Builder** (`src/scenes/deck_builder/`), or **Online Duel**
   (`src/scenes/network_lobby/` + `src/scenes/network_arena/`), which connects to the
   SignalR hub to host/join a server-authoritative match. If the backend is offline, use
   **Continue as Guest** to reach the menu anyway and play sans save.

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
│  │   ├─ Game/              deterministic turn/duel state machine
│  │   ├─ Ai/                AI controller + profiles (evolutions, attacks, triggers)
│  │   └─ Networking/        protocol-neutral JSON DTOs
│  ├─ gameplay/              board, card view, player, AI
│  ├─ ui/                    menus, deck builder, HUD
│  ├─ scenes/                arena, deck builder, lobby, network arena, auth
│  ├─ resources/             cards.json + card data loaders
│  ├─ debug/                 FPS/version overlay
│  └─ shaders/               VFX shaders
├─ tests/DuelMasters.Domain.Tests/   xUnit tests for the rules engine
└─ tools/
   ├─ extract/               OCR + web-scraping pipeline (per-card JSON)
   └─ map/                   keyword/effect mapping → spliced cards.json
```

## Card data pipeline

1. Extract card text from raw card images (PNG/JPEG) via OCR
   (`tools/extract/01_ocr.py`, per-card output in `tools/extract/ocr/`).
2. Web-scrape card pages to cross-check the data (`tools/extract/02_scrape.py`,
   output in `tools/extract/scrape/`), then prepare + categorize the combined
   text (`03_prepare.py`, `04_categorize.py`).
3. Build keyword/effect mappings (`tools/map/01_build_mapping.py`) which produce
   `mapping.json`, then splice it onto the card DB (`tools/map/02_splice.py`).
4. The tool produces `src/resources/data/cards.json`, consumed by the domain
   library and the Godot client.

## Networking

The client talks to the backend over a neutral JSON message schema
(`action` / `sessionId` / `playerId` / `payload`), so the transport is
interchangeable. The authoritative .NET 10 SignalR hub is Phase 4.

## Architecture docs

- `Duel_Masters_TCG_Engine_GDD.md` — full game/system design specification
- `Duel_Masters_Strategy_and_Codebase.md` — strategy/feasibility + reference code

## License

MIT — see `LICENSE`. Art assets must be rights-cleared before committing
(see `ASSETS_LICENSE.md`).