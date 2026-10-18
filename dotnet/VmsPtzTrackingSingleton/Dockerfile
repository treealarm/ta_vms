FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY ["dotnet/VmsPtzTrackingSingleton/VmsPtzTrackingSingleton.csproj", "dotnet/VmsPtzTrackingSingleton/"]
COPY ["dotnet/VmsLog/VmsLog.csproj", "dotnet/VmsLog/"]
COPY ["proto", "proto"]

RUN dotnet restore "dotnet/VmsPtzTrackingSingleton/VmsPtzTrackingSingleton.csproj"

COPY . .
WORKDIR "/src/dotnet/VmsPtzTrackingSingleton"
RUN dotnet publish "VmsPtzTrackingSingleton.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "VmsPtzTrackingSingleton.dll"]
