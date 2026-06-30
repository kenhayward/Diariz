# Build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Diariz.slnx ./
COPY src/Diariz.Domain/Diariz.Domain.csproj src/Diariz.Domain/
COPY src/Diariz.Api/Diariz.Api.csproj src/Diariz.Api/
RUN dotnet restore src/Diariz.Api/Diariz.Api.csproj
COPY src/ src/
RUN dotnet publish src/Diariz.Api/Diariz.Api.csproj -c Release -o /app /p:UseAppHost=false

# Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
# postgresql-client gives pg_dump/pg_restore for the platform backup/restore (MaintenanceController). The
# client major must be >= the Postgres server major (PG16) — a newer client is fine, which the distro's
# default package satisfies.
RUN apt-get update \
    && apt-get install -y --no-install-recommends postgresql-client \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Diariz.Api.dll"]
