# syntax=docker/dockerfile:1

# Duel Masters .NET authoritative server (SignalR, .NET 10 = current LTS).
# The backend targets net10.0 and runs on .NET 10 container images. It references
# DuelMasters.Domain (net8.0) which is shared with the Godot client; a net10.0
# project may reference a net8.0 library.
# The build copies the shared rules library (src/rules/DuelMasters.Domain) from the
# monorepo, so the build context MUST be the repository root:
#   docker build -f backends/dotnet/Dockerfile .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

# Restore layer — cached until a project file changes.
COPY src/rules/DuelMasters.Domain.csproj src/rules/
COPY backends/dotnet/DuelMasters.Server.csproj backends/dotnet/
RUN dotnet restore backends/dotnet/DuelMasters.Server.csproj

# Build and publish. (No --no-restore: the isolated restore layer's NuGet cache
# may miss build-time analyzer packages, so let publish resolve its own deps.)
COPY src/rules/ src/rules/
COPY src/resources/data/ src/resources/data/
COPY backends/dotnet/ backends/dotnet/
WORKDIR /source/backends/dotnet
RUN dotnet publish DuelMasters.Server.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
ENTRYPOINT ["dotnet", "DuelMasters.Server.dll"]