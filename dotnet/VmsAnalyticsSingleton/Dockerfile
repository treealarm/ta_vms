FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY ["dotnet/VmsAnalyticsSingleton/VmsAnalyticsSingleton.csproj", "dotnet/VmsAnalyticsSingleton/"]
COPY ["dotnet/VmsLog/VmsLog.csproj", "dotnet/VmsLog/"]
COPY ["proto", "proto"]

RUN dotnet restore "dotnet/VmsAnalyticsSingleton/VmsAnalyticsSingleton.csproj"

COPY . .
WORKDIR "/src/dotnet/VmsAnalyticsSingleton"
RUN dotnet publish "VmsAnalyticsSingleton.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "VmsAnalyticsSingleton.dll"]
