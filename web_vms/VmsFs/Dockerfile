FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY ["web_vms/VmsFs/VmsFs.csproj", "web_vms/VmsFs/"]
COPY ["web_vms/VmsLog/VmsLog.csproj", "web_vms/VmsLog/"]
COPY ["web_vms/web_vms.Types/web_vms.Types.csproj", "web_vms/web_vms.Types/"]
COPY ["proto", "proto"]

RUN dotnet restore "web_vms/VmsFs/VmsFs.csproj"

COPY . .
WORKDIR "/src/web_vms/VmsFs"
RUN dotnet publish "VmsFs.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "VmsFs.dll"]
