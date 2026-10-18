FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY ["dotnet/VmsLogger/VmsLogger.csproj", "dotnet/VmsLogger/"]
COPY ["dotnet/VmsLog/VmsLog.csproj", "dotnet/VmsLog/"]
COPY ["proto", "proto"]

RUN dotnet restore "dotnet/VmsLogger/VmsLogger.csproj"

COPY . .
WORKDIR "/src/dotnet/VmsLogger"
RUN dotnet publish "VmsLogger.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "VmsLogger.dll"]
