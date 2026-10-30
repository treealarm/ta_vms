FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY ["dotnet/VmsMap/VmsMap.csproj", "dotnet/VmsMap/"]
COPY ["dotnet/VmsDb/VmsDb.csproj", "dotnet/VmsDb/"]
COPY ["dotnet/VmsLog/VmsLog.csproj", "dotnet/VmsLog/"]
COPY ["proto", "proto"]

RUN dotnet restore "dotnet/VmsMap/VmsMap.csproj"

COPY . .
WORKDIR "/src/dotnet/VmsMap"
RUN dotnet publish "VmsMap.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "VmsMap.dll"]
