FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY ["dotnet/VmsUtils/VmsUtils.csproj", "dotnet/VmsUtils/"]
COPY ["dotnet/VmsLog/VmsLog.csproj", "dotnet/VmsLog/"]
COPY ["proto", "proto"]

RUN dotnet restore "dotnet/VmsUtils/VmsUtils.csproj"

COPY . .
WORKDIR "/src/dotnet/VmsUtils"
RUN dotnet publish "VmsUtils.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "VmsUtils.dll"]
