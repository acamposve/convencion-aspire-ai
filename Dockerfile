# syntax=docker/dockerfile:1
#
# One image carries both the API and the migrator: they share every dependency, and shipping one
# image means a deployment cannot run the schema of one build against the binary of another.

FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
WORKDIR /src

# Restore inputs first so a source change does not invalidate the package layer.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/ src/

RUN dotnet publish src/Convenciones.Api/Convenciones.Api.csproj \
        --configuration Release --output /app/publish /p:UseAppHost=false
RUN dotnet publish src/Convenciones.Migrator/Convenciones.Migrator.csproj \
        --configuration Release --output /app/publish /p:UseAppHost=false
RUN dotnet publish src/Convenciones.Worker/Convenciones.Worker.csproj \
        --configuration Release --output /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final

WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0 \
    DOTNET_NOLOGO=1

COPY --from=build /app/publish ./

# The migrator applies these files, so the schema always matches the binary in the same image.
COPY supabase/migrations ./supabase/migrations
COPY supabase/seed.sql ./supabase/seed.sql

# The aspnet image defines APP_UID (1654); the process never runs as root.
USER $APP_UID

EXPOSE 8080

ENTRYPOINT ["dotnet", "Convenciones.Api.dll"]
