FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Liro.Discord.Alerts/Liro.Discord.Alerts.csproj -c Release -o /out

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /out .
RUN mkdir -p /var/lib/liro-alerts && chown $APP_UID /var/lib/liro-alerts
USER $APP_UID
ENTRYPOINT ["dotnet", "Liro.Discord.Alerts.dll"]
