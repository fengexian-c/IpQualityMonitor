# syntax=docker/dockerfile:1
# Manifest digests verified by the amd64 CI on 2026-10-08.
# Supported deployment target: linux/amd64.
# Update deliberately and rerun build/smoke tests for security updates.
FROM ubuntu:24.04@sha256:534baea6a22c03a63003dbc8dbe78fe34bc0d7e595d9a9dc9834884ff530eb55 AS packet
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
    && python3 /patches/test_contract.py /mtr \
    && mkdir -p /artifacts \
    && cp mtr-packet /artifacts/iqm-mtr-packet \
    && cp -a /patches /mtr/iqm-patches \
    && tar --exclude=.git --exclude='*.o' --exclude=mtr-packet --exclude=mtr \
       --exclude=autom4te.cache -czf /artifacts/mtr-source.tar.gz -C /mtr .

# Go exists only in this build stage. The compiler archive is version+SHA pinned.
FROM ubuntu:24.04@sha256:534baea6a22c03a63003dbc8dbe78fe34bc0d7e595d9a9dc9834884ff530eb55 AS geo
ARG DEBIAN_FRONTEND=noninteractive
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates curl python3 \
    && rm -rf /var/lib/apt/lists/* \
    && curl --fail --location --proto '=https' --tlsv1.2 \
       https://go.dev/dl/go1.26.5.linux-amd64.tar.gz -o /tmp/go.tar.gz \
    && echo '5c2c3b16caefa1d968a94c1daca04a7ca301a496d9b086e17ad77bb81393f053  /tmp/go.tar.gz' | sha256sum -c - \
    && tar -xzf /tmp/go.tar.gz -C /usr/local && rm /tmp/go.tar.gz
ENV PATH=/usr/local/go/bin:$PATH GOTOOLCHAIN=local CGO_ENABLED=0 GOOS=linux GOARCH=amd64
WORKDIR /source
COPY tools/NextTraceGeoHelper/ tools/NextTraceGeoHelper/
COPY deploy/Dockerfile deploy/Dockerfile.dockerignore deploy/
COPY tests/server/test_geo_helper_contracts.py tests/server/
# Even dependency resolution and tests must work with no network access.
RUN --network=none sh tools/NextTraceGeoHelper/build-linux.sh /artifacts \
    && IQM_GEO_HELPER=/artifacts/iqm-nexttrace-geo python3 -m unittest discover \
       -s tests/server -p test_geo_helper_contracts.py -v

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317 AS build
WORKDIR /source
COPY NuGet.Config ./
COPY src/TcpLatencyMonitor.Core/ src/TcpLatencyMonitor.Core/
COPY src/IpQualityMonitor.Application/ src/IpQualityMonitor.Application/
COPY src/IpQualityMonitor.Linux/ src/IpQualityMonitor.Linux/
COPY src/IpQualityMonitor.Web/ src/IpQualityMonitor.Web/
RUN dotnet publish src/IpQualityMonitor.Web/IpQualityMonitor.Web.csproj \
    -c Release -o /out -p:UseAppHost=false

FROM build AS tests
# The fake helper process tests use Python only in this disposable test stage.
RUN apt-get update && apt-get install -y --no-install-recommends python3 \
    && rm -rf /var/lib/apt/lists/*
COPY tests/ tests/
RUN dotnet run --project tests/IpQualityMonitor.Server.Tests/IpQualityMonitor.Server.Tests.csproj -c Release \
    && dotnet run --project tests/TcpLatencyMonitor.Tests/TcpLatencyMonitor.Tests.csproj -c Release -- --routes \
    && dotnet run --project tests/TcpLatencyMonitor.Tests/TcpLatencyMonitor.Tests.csproj -c Release -- --route-history \
    && dotnet run --project tests/TcpLatencyMonitor.Tests/TcpLatencyMonitor.Tests.csproj -c Release -- --multi \
    && dotnet run --project tests/TcpLatencyMonitor.Tests/TcpLatencyMonitor.Tests.csproj -c Release -- --nexttrace \
    && dotnet run --project tests/TcpLatencyMonitor.Tests/TcpLatencyMonitor.Tests.csproj -c Release -- --geo \
    && dotnet run --project tests/TcpLatencyMonitor.Tests/TcpLatencyMonitor.Tests.csproj -c Release -- --locations \
    && dotnet run --project tests/TcpLatencyMonitor.Tests/TcpLatencyMonitor.Tests.csproj -c Release -- --metadata-checks

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble@sha256:222759b391a1aaf241166672c8f99b2d4ada452e7b5319f3c6e8f265a37b5ad4 AS runtime
ARG DEBIAN_FRONTEND=noninteractive
RUN apt-get update && apt-get install -y --no-install-recommends iproute2 libcap2-bin ca-certificates tzdata \
    && rm -rf /var/lib/apt/lists/* \
    && groupadd --gid 10001 iqm && useradd --uid 10001 --gid 10001 --no-create-home iqm \
    && install -d -o 10001 -g 10001 /data /app \
    && ln -s "$(command -v ip)" /usr/local/bin/iqm-ip
COPY --from=packet /artifacts/iqm-mtr-packet /usr/local/libexec/iqm-mtr-packet
COPY --from=packet /artifacts/mtr-source.tar.gz /usr/share/doc/ipqualitymonitor/mtr-source.tar.gz
COPY tools/mtr/UPSTREAM.json /usr/share/doc/ipqualitymonitor/UPSTREAM.json
COPY --from=geo /artifacts/iqm-nexttrace-geo /usr/local/libexec/iqm-nexttrace-geo
COPY --from=geo /artifacts/nexttrace-* /usr/share/doc/ipqualitymonitor/
COPY --from=build /out/ /app/
# Apply file capability to the FINAL file after COPY. Do not combine with no-new-privileges.
RUN chmod 0755 /usr/local/libexec/iqm-mtr-packet /usr/local/libexec/iqm-nexttrace-geo \
    && test -z "$(getcap /usr/local/libexec/iqm-nexttrace-geo)" \
    && setcap cap_net_raw=ep /usr/local/libexec/iqm-mtr-packet \
    && getcap /usr/local/libexec/iqm-mtr-packet | grep -q cap_net_raw
USER 10001:10001
WORKDIR /app
ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    IPQUALITY_DATA_DIR=/data \
    IPQUALITY_IP_PATH=/usr/local/bin/iqm-ip \
    IPQUALITY_MTR_PATH=/usr/local/libexec/iqm-mtr-packet \
    IPQUALITY_NEXTTRACE_PATH=/usr/local/libexec/iqm-nexttrace-geo \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=45s --retries=3 \
    CMD ["dotnet", "/app/IpQualityMonitor.Web.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "/app/IpQualityMonitor.Web.dll"]

