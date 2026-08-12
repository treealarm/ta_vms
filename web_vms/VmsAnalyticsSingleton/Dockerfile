FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY ["web_vms/VmsAnalyticsSingleton/VmsAnalyticsSingleton.csproj", "web_vms/VmsAnalyticsSingleton/"]
COPY ["web_vms/VmsLog/VmsLog.csproj", "web_vms/VmsLog/"]
COPY ["proto", "proto"]

RUN dotnet restore "web_vms/VmsAnalyticsSingleton/VmsAnalyticsSingleton.csproj"

COPY . .
WORKDIR "/src/web_vms/VmsAnalyticsSingleton"
RUN dotnet publish "VmsAnalyticsSingleton.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "VmsAnalyticsSingleton.dll"]
