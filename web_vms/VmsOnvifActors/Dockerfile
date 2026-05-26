FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY ["web_vms/VmsOnvifActors/VmsOnvifActors.csproj", "web_vms/VmsOnvifActors/"]
COPY ["web_vms/OnvifLib/OnvifLib.csproj", "web_vms/OnvifLib/"]
COPY ["web_vms/web_vms.Types/web_vms.Types.csproj", "web_vms/web_vms.Types/"]

RUN dotnet restore "web_vms/VmsOnvifActors/VmsOnvifActors.csproj"

COPY . .
WORKDIR "/src/web_vms/VmsOnvifActors"
RUN dotnet publish "VmsOnvifActors.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "VmsOnvifActors.dll"]
