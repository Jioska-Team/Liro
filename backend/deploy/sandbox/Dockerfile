FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG PROJECT=Liro.Api
WORKDIR /src
COPY . .
RUN if [ "$PROJECT" = "Liro.SandboxMigrator" ]; then project="tools/$PROJECT/$PROJECT.csproj"; else project="src/$PROJECT/$PROJECT.csproj"; fi; \
    dotnet publish "$project" -c Release -o /out --self-contained false; \
    test -f "/out/$PROJECT.dll"

FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG PROJECT=Liro.Api
ENV LIRO_EXECUTABLE=$PROJECT.dll
WORKDIR /app
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /var/lib/liro-market && chown $APP_UID /var/lib/liro-market
COPY --from=build /out .
USER $APP_UID
ENTRYPOINT ["/bin/sh", "-c", "exec dotnet \"$LIRO_EXECUTABLE\" \"$@\"", "--"]
