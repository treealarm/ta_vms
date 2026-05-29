FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY ["web_vms/VmsUtils/VmsUtils.csproj", "web_vms/VmsUtils/"]
COPY ["proto", "proto"]

RUN dotnet restore "web_vms/VmsUtils/VmsUtils.csproj"

COPY . .
WORKDIR "/src/web_vms/VmsUtils"
RUN dotnet publish "VmsUtils.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "VmsUtils.dll"]
