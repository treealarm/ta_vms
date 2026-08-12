FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY ["web_vms/VmsSquareSingleton/VmsSquareSingleton.csproj", "web_vms/VmsSquareSingleton/"]
COPY ["web_vms/VmsLog/VmsLog.csproj", "web_vms/VmsLog/"]
COPY ["web_vms/SquareIntegrationClient/SquareIntegrationClient.csproj", "web_vms/SquareIntegrationClient/"]
COPY ["proto", "proto"]

RUN dotnet restore "web_vms/VmsSquareSingleton/VmsSquareSingleton.csproj"

COPY . .
WORKDIR "/src/web_vms/VmsSquareSingleton"
RUN dotnet publish "VmsSquareSingleton.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "VmsSquareSingleton.dll"]
