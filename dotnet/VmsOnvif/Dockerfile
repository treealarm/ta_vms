FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY ["dotnet/VmsOnvif/VmsOnvif.csproj", "dotnet/VmsOnvif/"]
COPY ["dotnet/web_srv.Types/web_srv.Types.csproj", "dotnet/web_srv.Types/"]
COPY ["dotnet/OnvifLib/OnvifLib.csproj", "dotnet/OnvifLib/"]
COPY ["dotnet/VmsLog/VmsLog.csproj", "dotnet/VmsLog/"]
COPY ["proto", "proto"]

RUN dotnet restore "dotnet/VmsOnvif/VmsOnvif.csproj"

COPY . .
WORKDIR "/src/dotnet/VmsOnvif"
RUN dotnet publish "VmsOnvif.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "VmsOnvif.dll"]
