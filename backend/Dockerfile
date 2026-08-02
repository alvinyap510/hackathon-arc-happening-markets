# Venue.Api — the RFM venue backend (C# / .NET 9). Runs against a real RPC in
# E2E (Venue__Simulate=false, NethereumChainGateway); the compose stack feeds it the
# deployed contract addresses + operator/demo-user keys via env.
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY Venue.sln .
COPY src/ ./src/
COPY tests/ ./tests/
RUN dotnet restore Venue.sln
RUN dotnet publish src/Venue.Api/Venue.Api.csproj -c Release -o /out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /out .
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
# The compose stack mounts ./e2e/.runtime at /app/shared and the deploy job writes
# backend.env there AFTER this image is built but BEFORE the container starts; source
# and EXPORT it here (set -a) so the real Nethereum config reaches the dotnet process.
ENTRYPOINT ["/bin/sh", "-c", "set -a; . /app/shared/backend.env; set +a; exec dotnet Venue.Api.dll"]
