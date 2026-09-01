# syntax=docker/dockerfile:1

# Duel Masters .NET 9 authoritative server (SignalR).
# The build copies the shared rules library (src/rules/DuelMasters.Domain) from the
# monorepo, so the build context MUST be the repository root:
#   docker build -f backends/dotnet/Dockerfile .
#
# NOTE: requires the server project at backends/dotnet/DuelMasters.Server.csproj,
# which lands with the backend implementation phase.

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /source

# Restore layer — cached until a project file changes.
COPY src/rules/DuelMasters.Domain.csproj src/rules/
COPY backends/dotnet/DuelMasters.Server.csproj backends/dotnet/
RUN dotnet restore backends/dotnet/DuelMasters.Server.csproj

# Build and publish.
COPY src/rules/ src/rules/
COPY backends/dotnet/ backends/dotnet/
WORKDIR /source/backends/dotnet
RUN dotnet publish DuelMasters.Server.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
ENTRYPOINT ["dotnet", "DuelMasters.Server.dll"]