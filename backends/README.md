# Duel Masters Backends

Authoritative, protocol-neutral server implementations for the Duel Masters TCG.
The client talks to any of these over a **JSON WebSocket contract**
(`action` / `sessionId` / `playerId` / `payload`), so the transport below can be
swapped without touching the Godot client.

All implementations share the same rules source of truth:
`src/rules/DuelMasters.Domain` (pure C#, zero dependencies).

## Stacks

| Folder          | Stack             | Transport          | Port | Status          |
| --------------- | ----------------- | ------------------ | ---- | --------------- |
| `dotnet/`       | .NET 9 + SignalR  | WebSockets/SignalR | 8080 | Scaffold only   |
| `nestjs/`       | NestJS + Socket.io| WebSockets         | 3000 | Scaffold only   |
| `spring-boot/`  | Spring Boot STOMP | WebSockets/STOMP   | 8080 | Scaffold only   |

Each folder currently contains its container recipe (`Dockerfile` +
`.dockerignore`) only. The actual server projects land with the backend
implementation phase; the Dockerfiles are authored against the final layout so
building never has to change.

## Containerization

Every backend is containerized with a multi-stage `Dockerfile`. Build contexts:

```bash
# .NET 9 (context = repo root: the build copies the shared Domain library)
docker build -f backends/dotnet/Dockerfile .

# NestJS (self-contained; context = the nestjs folder)
docker build -f backends/nestjs/Dockerfile backends/nestjs

# Spring Boot (self-contained; context = the spring-boot folder)
docker build -f backends/spring-boot/Dockerfile backends/spring-boot
```

Run (example, .NET):

```bash
docker build -f backends/dotnet/Dockerfile -t duel-masters-dotnet .
docker run -p 8080:8080 duel-masters-dotnet
```

`.gdignore` in this root tells Godot to ignore the whole tree, and the Godot
csproj excludes `backends/**` from compilation.