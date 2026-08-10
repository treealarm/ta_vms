FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY ["web_vms/VmsSingleton/VmsSingleton.csproj", "web_vms/VmsSingleton/"]
COPY ["web_vms/VmsLog/VmsLog.csproj", "web_vms/VmsLog/"]
COPY ["proto", "proto"]

RUN dotnet restore "web_vms/VmsSingleton/VmsSingleton.csproj"

COPY . .
WORKDIR "/src/web_vms/VmsSingleton"
RUN dotnet publish "VmsSingleton.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "VmsSingleton.dll"]
