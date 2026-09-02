# Duel Masters Backends

Authoritative, protocol-neutral server implementations for the Duel Masters TCG.
The client talks to any of these over a **JSON WebSocket contract**
(`action` / `sessionId` / `playerId` / `payload`), so the transport below can be
swapped without touching the Godot client.

All implementations share the same rules source of truth:
`src/rules/DuelMasters.Domain` (pure C#, zero dependencies).

## Stacks

| Folder          | Stack                    | Transport          | Port | Status                       |
| --------------- | ------------------------ | ------------------ | ---- | ---------------------------- |
| `dotnet/`       | .NET 8 (EF Core + Npgsql)| REST + JWT         | 8080 | Implemented (Phase 1.5)      |
| `nestjs/`       | NestJS + Socket.io       | WebSockets         | 3000 | Scaffold only                |
| `spring-boot/`  | Spring Boot STOMP        | WebSockets/STOMP   | 8080 | Scaffold only                |

The `dotnet/` server (Phase 1.5) exposes JWT-authenticated REST endpoints over
PostgreSQL for the card catalog, users, and decks. Matchmaking/real-time play
over SignalR (DuelHub) lands in Phase 4, and the JSON WebSocket contract
(`action` / `sessionId` / `playerId` / `payload`) is shared by all backends.

Endpoints:
- `POST /api/auth/register` / `POST /api/auth/login` → JWT
- `GET /api/cards` (+ `?set=` / `?civilization=` / `?cardType=` / `?powerfulOrEqual=`) and `GET /api/cards/{id}`
- `GET/POST/PUT/DELETE /api/decks/...` (deck rules enforced server-side: exactly 40 cards, max 4 copies)

The card catalog is seeded on startup from `src/resources/data/cards.json`
(Phase 1 output). Each folder contains its container recipe (`Dockerfile`).
NestJS/Spring Boot land later.

## Containerization

Every backend is containerized with a multi-stage `Dockerfile`. Build contexts:

```bash
# .NET 8 (context = repo root: the build copies the shared Domain library)
docker build -f backends/dotnet/Dockerfile .

# NestJS (self-contained; context = the nestjs folder)
docker build -f backends/nestjs/Dockerfile backends/nestjs

# Spring Boot (self-contained; context = the spring-boot folder)
docker build -f backends/spring-boot/Dockerfile backends/spring-boot
```

Run the whole Phase 1.5 stack (PostgreSQL + API):

```bash
docker compose -f backends/docker-compose.yml up --build
```

`.gdignore` in this root tells Godot to ignore the whole tree, and the Godot
csproj excludes `backends/**` from compilation.