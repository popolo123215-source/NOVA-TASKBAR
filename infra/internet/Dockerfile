FROM mcr.microsoft.com/dotnet/sdk:10.0.403-noble AS build
WORKDIR /source
COPY src/Nova.RendezvousProtocol/Nova.RendezvousProtocol.csproj src/Nova.RendezvousProtocol/
COPY src/Nova.RendezvousService/Nova.RendezvousService.csproj src/Nova.RendezvousService/
RUN dotnet restore src/Nova.RendezvousService/Nova.RendezvousService.csproj
COPY src/Nova.RendezvousProtocol/ src/Nova.RendezvousProtocol/
COPY src/Nova.RendezvousService/ src/Nova.RendezvousService/
RUN dotnet publish src/Nova.RendezvousService/Nova.RendezvousService.csproj \
    --configuration Release --no-restore --output /out --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:10.0.14-noble
WORKDIR /app
COPY --from=build /out/ ./
RUN mkdir -p /var/lib/nova-rendezvous && chown -R $APP_UID:$APP_UID /app /var/lib/nova-rendezvous
ENV ASPNETCORE_URLS=http://0.0.0.0:8443 \
    NOVA_HTTPS_PORT=8443 \
    NOVA_STATE_PATH=/var/lib/nova-rendezvous \
    NOVA_TLS_PFX_PATH=/run/certs/rendezvous.pfx
EXPOSE 8443/tcp
USER $APP_UID
ENTRYPOINT ["dotnet", "Nova.RendezvousService.dll"]
