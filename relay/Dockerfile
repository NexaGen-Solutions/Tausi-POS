# The Tausi relay as a container (NexaGen hosts it; it isn't part of the bar installers).
# Build from relay/:  docker build -t tausi-relay .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props global.json ./
COPY src/Tausi.Relay/Tausi.Relay.csproj src/Tausi.Relay/
RUN dotnet restore src/Tausi.Relay/Tausi.Relay.csproj
COPY src/Tausi.Relay/ src/Tausi.Relay/
RUN dotnet publish src/Tausi.Relay/Tausi.Relay.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "TausiRelay.dll"]
