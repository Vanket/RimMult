# Build from the repository root:
#   docker build -f src/RimMult.Server/Dockerfile -t rimmult-server .
#   docker run -d -p 26480:26480/udp -v rimmult-data:/data rimmult-server
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/RimMult.Server/RimMult.Server.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /data
COPY --from=build /app /app
EXPOSE 26480/udp
ENTRYPOINT ["dotnet", "/app/RimMult.Server.dll", "--config", "/data/server.json"]
