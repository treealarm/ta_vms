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
COPY ["web_vms/web_vms.Server/web_vms.Server.csproj", "web_vms/web_vms.Server/"]
COPY ["web_vms/web_vms.client/web_vms.client.esproj", "web_vms/web_vms.client/"]
COPY ["web_vms/OnvifLib/OnvifLib.csproj", "web_vms/OnvifLib/"]
COPY ["web_vms/VmsCfg/VmsCfg.csproj", "web_vms/VmsCfg/"]
COPY ["web_vms/web_vms.Types/web_vms.Types.csproj", "web_vms/web_vms.Types/"]
COPY ["web_vms/VmsLog/VmsLog.csproj", "web_vms/VmsLog/"]
COPY ["web_vms/KeycloakAdmin/KeycloakAdmin.csproj", "web_vms/KeycloakAdmin/"]
COPY ["proto", "proto"]

# =========================
# Restore server dependencies (NuGet cache mount: KeycloakAdmin pulls in Keycloak.Net.Core,
# whose old runtime-graph transitive packages are prone to nuget.org timeouts — persist the
# global-packages cache across builds so a successful restore doesn't need to repeat)
# =========================
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet restore "web_vms/web_vms.Server/web_vms.Server.csproj"

# =========================
# Copy full source (server + client)
# =========================
COPY . .

# =========================
# Build frontend (npm ci + build)
# =========================
WORKDIR "/src/web_vms/web_vms.client"
RUN npm ci
RUN npm run build

# =========================
# Build server
# =========================
WORKDIR "/src/web_vms/web_vms.Server"
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet build "web_vms.Server.csproj" -c $BUILD_CONFIGURATION -o /app/build

# =========================
# Publish server
# =========================
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet publish "web_vms.Server.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# =========================
# Final runnable image
# =========================
FROM base AS final
WORKDIR /app
COPY --from=build-env /app/publish .

ENTRYPOINT ["dotnet", "web_vms.Server.dll"]
