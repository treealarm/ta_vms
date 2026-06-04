FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY ["web_vms/VmsCfg/VmsCfg.csproj", "web_vms/VmsCfg/"]
COPY ["web_vms/web_vms.Types/web_vms.Types.csproj", "web_vms/web_vms.Types/"]
COPY ["web_vms/OnvifLib/OnvifLib.csproj", "web_vms/OnvifLib/"]
COPY ["web_vms/VmsLog/VmsLog.csproj", "web_vms/VmsLog/"]
COPY ["proto", "proto"]

RUN dotnet restore "web_vms/VmsCfg/VmsCfg.csproj"

COPY . .
WORKDIR "/src/web_vms/VmsCfg"
RUN dotnet publish "VmsCfg.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "VmsCfg.dll"]
