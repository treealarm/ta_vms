FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY ["dotnet/VmsCfg/VmsCfg.csproj", "dotnet/VmsCfg/"]
COPY ["dotnet/web_srv.Types/web_srv.Types.csproj", "dotnet/web_srv.Types/"]
COPY ["dotnet/OnvifLib/OnvifLib.csproj", "dotnet/OnvifLib/"]
COPY ["dotnet/VmsLog/VmsLog.csproj", "dotnet/VmsLog/"]
COPY ["proto", "proto"]

RUN dotnet restore "dotnet/VmsCfg/VmsCfg.csproj"

COPY . .
WORKDIR "/src/dotnet/VmsCfg"
RUN dotnet publish "VmsCfg.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "VmsCfg.dll"]
