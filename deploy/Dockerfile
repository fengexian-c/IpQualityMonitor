# syntax=docker/dockerfile:1
# Initial preview. For a release, resolve these maintained tags to tested image digests.
FROM ubuntu:24.04 AS packet
ARG DEBIAN_FRONTEND=noninteractive
RUN apt-get update && apt-get install -y --no-install-recommends \
    ca-certificates git build-essential autoconf automake libtool pkg-config libcap-dev python3 \
    && rm -rf /var/lib/apt/lists/*
COPY tools/mtr/ /patches/
WORKDIR /mtr
RUN revision="$(python3 -c 'import json; print(json.load(open("/patches/UPSTREAM.json"))["commit"])')" \
    && git init . && git remote add origin https://github.com/traviscross/mtr.git \
    && git fetch --depth 1 origin "$revision" && git checkout --detach FETCH_HEAD \
    && test "$(git rev-parse HEAD)" = "$revision" \
    && python3 /patches/patch.py /mtr \
    && ./bootstrap.sh && ./configure --without-gtk --without-ncurses --without-ncursesw \
    && make -j2 mtr-packet \
    && mkdir -p /artifacts \
    && cp mtr-packet /artifacts/iqm-mtr-packet \
    && cp -a /patches /mtr/iqm-patches \
    && tar --exclude=.git --exclude='*.o' --exclude=mtr-packet --exclude=mtr \
       --exclude=autom4te.cache -czf /artifacts/mtr-source.tar.gz -C /mtr .

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /source
COPY NuGet.Config ./
COPY src/TcpLatencyMonitor.Core/ src/TcpLatencyMonitor.Core/
COPY src/IpQualityMonitor.Application/ src/IpQualityMonitor.Application/
COPY src/IpQualityMonitor.Linux/ src/IpQualityMonitor.Linux/
COPY src/IpQualityMonitor.Web/ src/IpQualityMonitor.Web/
RUN dotnet publish src/IpQualityMonitor.Web/IpQualityMonitor.Web.csproj \
    -c Release -o /out -p:UseAppHost=false

FROM build AS tests
COPY tests/ tests/
RUN dotnet run --project tests/IpQualityMonitor.Server.Tests/IpQualityMonitor.Server.Tests.csproj -c Release \
    && dotnet run --project tests/TcpLatencyMonitor.Tests/TcpLatencyMonitor.Tests.csproj -c Release -- --routes \
    && dotnet run --project tests/TcpLatencyMonitor.Tests/TcpLatencyMonitor.Tests.csproj -c Release -- --route-history

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS runtime
ARG DEBIAN_FRONTEND=noninteractive
RUN apt-get update && apt-get install -y --no-install-recommends iproute2 libcap2-bin ca-certificates tzdata \
    && rm -rf /var/lib/apt/lists/* \
    && groupadd --gid 10001 iqm && useradd --uid 10001 --gid 10001 --no-create-home iqm \
    && install -d -o 10001 -g 10001 /data /app \
    && ln -s "$(command -v ip)" /usr/local/bin/iqm-ip
COPY --from=packet /artifacts/iqm-mtr-packet /usr/local/libexec/iqm-mtr-packet
COPY --from=packet /artifacts/mtr-source.tar.gz /usr/share/doc/ipqualitymonitor/mtr-source.tar.gz
COPY tools/mtr/UPSTREAM.json /usr/share/doc/ipqualitymonitor/UPSTREAM.json
COPY --from=build /out/ /app/
# Apply file capability to the FINAL file after COPY. Do not combine with no-new-privileges.
RUN chmod 0755 /usr/local/libexec/iqm-mtr-packet \
    && setcap cap_net_raw=ep /usr/local/libexec/iqm-mtr-packet \
    && getcap /usr/local/libexec/iqm-mtr-packet | grep -q cap_net_raw
USER 10001:10001
WORKDIR /app
ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    IPQUALITY_DATA_DIR=/data \
    IPQUALITY_IP_PATH=/usr/local/bin/iqm-ip \
    IPQUALITY_MTR_PATH=/usr/local/libexec/iqm-mtr-packet \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=45s --retries=3 \
    CMD ["dotnet", "/app/IpQualityMonitor.Web.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "/app/IpQualityMonitor.Web.dll"]
