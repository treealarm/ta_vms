FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY ["dotnet/VmsOnvifActors/VmsOnvifActors.csproj", "dotnet/VmsOnvifActors/"]
COPY ["dotnet/web_srv.Types/web_srv.Types.csproj", "dotnet/web_srv.Types/"]
COPY ["dotnet/VmsLog/VmsLog.csproj", "dotnet/VmsLog/"]

RUN dotnet restore "dotnet/VmsOnvifActors/VmsOnvifActors.csproj"

COPY . .
WORKDIR "/src/dotnet/VmsOnvifActors"
RUN dotnet publish "VmsOnvifActors.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "VmsOnvifActors.dll"]
