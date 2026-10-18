# =========================
# Base ASP.NET runtime image
# =========================
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app

# =========================
# SDK image + Node.js for SPA build
# =========================
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build-env
RUN apt-get update && apt-get install -y curl
RUN curl -sL https://deb.nodesource.com/setup_20.x | bash
RUN apt-get install -y nodejs

ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# =========================
# Copy project files and proto
# =========================
COPY ["dotnet/web_srv.Server/web_srv.Server.csproj", "dotnet/web_srv.Server/"]
COPY ["dotnet/web_srv.client/web_srv.client.esproj", "dotnet/web_srv.client/"]
COPY ["dotnet/OnvifLib/OnvifLib.csproj", "dotnet/OnvifLib/"]
COPY ["dotnet/VmsCfg/VmsCfg.csproj", "dotnet/VmsCfg/"]
COPY ["dotnet/web_srv.Types/web_srv.Types.csproj", "dotnet/web_srv.Types/"]
COPY ["dotnet/VmsLog/VmsLog.csproj", "dotnet/VmsLog/"]
COPY ["dotnet/KeycloakAdmin/KeycloakAdmin.csproj", "dotnet/KeycloakAdmin/"]
COPY ["proto", "proto"]

# =========================
# Restore server dependencies (NuGet cache mount: KeycloakAdmin pulls in Keycloak.Net.Core,
# whose old runtime-graph transitive packages are prone to nuget.org timeouts — persist the
# global-packages cache across builds so a successful restore doesn't need to repeat)
# =========================
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet restore "dotnet/web_srv.Server/web_srv.Server.csproj"

# =========================
# Copy full source (server + client)
# =========================
COPY . .

# =========================
# Build frontend (npm ci + build)
# =========================
WORKDIR "/src/dotnet/web_srv.client"
RUN npm ci
RUN npm run build

# =========================
# Build server
# =========================
WORKDIR "/src/dotnet/web_srv.Server"
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet build "web_srv.Server.csproj" -c $BUILD_CONFIGURATION -o /app/build

# =========================
# Publish server
# =========================
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet publish "web_srv.Server.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# =========================
# Final runnable image
# =========================
FROM base AS final
WORKDIR /app
COPY --from=build-env /app/publish .

ENTRYPOINT ["dotnet", "web_srv.Server.dll"]
