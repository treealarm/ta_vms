FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY ["dotnet/VmsSquareSingleton/VmsSquareSingleton.csproj", "dotnet/VmsSquareSingleton/"]
COPY ["dotnet/VmsLog/VmsLog.csproj", "dotnet/VmsLog/"]
COPY ["dotnet/SquareIntegrationClient/SquareIntegrationClient.csproj", "dotnet/SquareIntegrationClient/"]
COPY ["proto", "proto"]

RUN dotnet restore "dotnet/VmsSquareSingleton/VmsSquareSingleton.csproj"

COPY . .
WORKDIR "/src/dotnet/VmsSquareSingleton"
RUN dotnet publish "VmsSquareSingleton.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "VmsSquareSingleton.dll"]
