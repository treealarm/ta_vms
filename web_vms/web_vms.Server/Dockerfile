# =========================
# Базовый runtime для ASP.NET
# =========================
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app

# =========================
# SDK с Node.js для фронта
# =========================
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build-env
RUN apt-get update && apt-get install -y curl
RUN curl -sL https://deb.nodesource.com/setup_20.x | bash
RUN apt-get install -y nodejs

ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# =========================
# Копируем проекты и proto
# =========================
COPY ["web_vms/web_vms.Server/web_vms.Server.csproj", "web_vms.Server/"]
COPY ["web_vms/web_vms.client/web_vms.client.esproj", "web_vms.client/"]
COPY ["proto", "proto"]

# =========================
# Восстанавливаем зависимости серверного проекта
# =========================
RUN dotnet restore "web_vms.Server/web_vms.Server.csproj"

# =========================
# Копируем весь исходный код и фронт
# =========================
COPY . .

# =========================
# Собираем фронт (npm install + build)
# =========================
WORKDIR "/src/web_vms/web_vms.client"
RUN npm ci
RUN npm run build

# =========================
# Собираем сервер
# =========================
WORKDIR "/src/web_vms/web_vms.Server"
RUN dotnet build "web_vms.Server.csproj" -c $BUILD_CONFIGURATION -o /app/build

# =========================
# Публикуем сервер
# =========================
RUN dotnet publish "web_vms.Server.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# =========================
# Финальный образ для запуска
# =========================
FROM base AS final
WORKDIR /app
COPY --from=build-env /app/publish .

ENTRYPOINT ["dotnet", "web_vms.Server.dll"]