FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY ["web_vms/VmsOnvif/VmsOnvif.csproj", "web_vms/VmsOnvif/"]
COPY ["web_vms/web_vms.Types/web_vms.Types.csproj", "web_vms/web_vms.Types/"]
COPY ["web_vms/OnvifLib/OnvifLib.csproj", "web_vms/OnvifLib/"]
COPY ["proto", "proto"]

RUN dotnet restore "web_vms/VmsOnvif/VmsOnvif.csproj"

COPY . .
WORKDIR "/src/web_vms/VmsOnvif"
RUN dotnet publish "VmsOnvif.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "VmsOnvif.dll"]
