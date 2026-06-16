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
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Diariz.Api.dll"]
