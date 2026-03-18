# =========================
# Базовый runtime для ASP.NET
# =========================
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app

# =========================
# SDK с Node.js для фронта
# =========================
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS with-node
RUN apt-get update
RUN apt-get install curl
RUN curl -sL https://deb.nodesource.com/setup_20.x | bash
RUN apt-get -y install nodejs

# =========================
# Сборка проекта
# =========================
FROM with-node AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# Копируем proto из корня
COPY ["proto", "proto"]

# Копируем проекты фронта и сервера
COPY ["web_vms/web_vms.Server/web_vms.Server.csproj", "web_vms/web_vms.Server/"]
COPY ["web_vms/web_vms.client/web_vms.client.esproj", "web_vms/web_vms.client/"]

# Восстанавливаем зависимости сервера
RUN dotnet restore "web_vms/web_vms.Server/web_vms.Server.csproj"
COPY . .

COPY ["web_vms/web_vms.client/", "web_vms.client/"]
WORKDIR "/src/web_vms/web_vms.client"
RUN npm ci

# Переходим в каталог сервера и билдим
WORKDIR "/src/web_vms/web_vms.Server"
RUN dotnet build "web_vms.Server.csproj" -c $BUILD_CONFIGURATION -o /app/build

# =========================
# Публикуем сервер
# =========================
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "web_vms.Server.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# =========================
# Финальный образ для запуска
# =========================
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

ENTRYPOINT ["dotnet", "web_vms.Server.dll"]