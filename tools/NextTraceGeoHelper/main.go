// SPDX-License-Identifier: GPL-3.0-only
// Standalone NextTrace v3 GeoIP helper. It does not send traceroute or ping packets.
package main

import (
    "bufio"
    "context"
    "encoding/json"
    "flag"
    "fmt"
    "io"
    "log"
    "net/http"
    "net/netip"
    "os"
    "runtime"
    "strings"
    "time"

    "github.com/fatih/color"
    "github.com/nxtrace/NTrace-core/ipgeo"
    "github.com/nxtrace/NTrace-core/wshandle"
)

const upstreamCommit = "40ac803ca233b0bc3d4b3bf1cacc8f7e0d8368f2"

type request struct {
    ID string `json:"id"`
    Op string `json:"op"`
    IPs []string `json:"ips"`
    Token string `json:"token"`
}

type event map[string]any
type cachedGeo struct { value *ipgeo.IPGeoData; at time.Time }

type probe struct {
    emit func(event)
    live bool
    conn *wshandle.WsConn
    cancel context.CancelFunc
    lookup func(string, time.Duration, string, bool) (*ipgeo.IPGeoData, error)
    cache map[string]cachedGeo
    queries int
    connections int
    hits int
}

func publicIP(raw string) (string, bool) {
    ip, err := netip.ParseAddr(raw)
    if err != nil || ip.Zone() != "" { return "", false }
    ip = ip.Unmap()
    if !ip.IsGlobalUnicast() || ip.IsPrivate() || ip.IsLoopback() || ip.IsLinkLocalUnicast() { return "", false }
    for _, value := range []string{"0.0.0.0/8", "100.64.0.0/10", "192.0.0.0/24", "192.0.2.0/24", "192.88.99.0/24", "198.18.0.0/15", "198.51.100.0/24", "203.0.113.0/24", "240.0.0.0/4", "2001:db8::/32", "2001::/23", "2002::/16", "3fff::/20"} {
        if netip.MustParsePrefix(value).Contains(ip) { return "", false }
    }
    if ip.Is6() && !netip.MustParsePrefix("2000::/3").Contains(ip) { return "", false }
    return ip.String(), true
}

func (p *probe) close() {
    if p.cancel != nil { p.cancel() }
    if p.conn != nil { p.conn.Close() }
    p.conn = nil
}

func (p *probe) connect(ctx context.Context) error {
    if p.conn != nil && p.conn.IsConnected() { return nil }
    sessionCtx, cancel := context.WithCancel(ctx)
    p.cancel = cancel
    // Upstream owns PoW, TLS, websocket handshake and its receive/write loops.
    p.conn = wshandle.NewWithContextAsync(sessionCtx)
    authCtx, authCancel := context.WithTimeout(ctx, 15*time.Second)
    defer authCancel()
    if err := p.conn.WaitUntilConnected(authCtx); err != nil { p.close(); return err }
    p.connections++
    return nil
}

func (p *probe) run(ctx context.Context, req request) bool {
    started := time.Now()
    if req.ID == "" || len(req.ID) > 80 { p.emit(event{"type":"error", "code":"invalid_id"}); return true }
    if req.Op == "v4" { return p.v4(ctx, req) }
    if req.Op == "shutdown" { p.emit(event{"id":req.ID, "type":"done", "status":"shutdown"}); return false }
    if req.Op == "stats" {
        var mem runtime.MemStats; runtime.ReadMemStats(&mem)
        p.emit(event{"id":req.ID,"type":"stats","queries":p.queries,"cacheHits":p.hits,"connections":p.connections,"cacheEntries":len(p.cache),"heapBytes":mem.HeapAlloc,"goroutines":runtime.NumGoroutine()})
        return true
    }
    if req.Op != "lookup" || len(req.IPs) == 0 || len(req.IPs) > 32 {
        p.emit(event{"id":req.ID,"type":"error","code":"invalid_request"}); return true
    }
    ips := make([]string, 0, len(req.IPs)); seen:=map[string]bool{}
    success, failed := 0, 0
    for _, raw := range req.IPs {
        ip, ok := publicIP(raw)
        if !ok { failed++; p.emit(event{"id":req.ID,"type":"error","code":"not_public_ip","ip":raw}); continue }
        if !seen[ip] { seen[ip]=true; ips=append(ips,ip) }
    }
    for _, ip := range ips {
        if ctx.Err() != nil { p.emit(event{"id":req.ID,"type":"done","status":"cancelled","success":success,"failed":failed}); return false }
        if saved, ok := p.cache[ip]; ok && time.Since(saved.at) < 24*time.Hour {
            p.hits++; success++
            p.emit(event{"id":req.ID,"type":"result","ip":ip,"cached":true,"geo":saved.value,"queriedAt":saved.at.UTC().Format(time.RFC3339Nano)})
            continue
        }
        if !p.live { p.emit(event{"id":req.ID,"type":"error","ip":ip,"code":"live_not_enabled"}); failed++; continue }
        if p.lookup == nil {
            p.emit(event{"id":req.ID,"type":"status","stage":"connecting","ip":ip})
            if err := p.connect(ctx); err != nil {
                p.emit(event{"id":req.ID,"type":"error","code":"connection_failed","restartRequired":true})
                p.emit(event{"id":req.ID,"type":"done","status":"failed","elapsedMs":time.Since(started).Milliseconds()})
                return false // No uncontrolled retry loop after this experiment fails.
            }
            p.lookup = ipgeo.NextTraceAPIV3GeoIP
        }
        p.emit(event{"id":req.ID,"type":"status","stage":"querying","ip":ip})
        before:=time.Now(); p.queries++
        geo, err := p.lookup(ip, 6*time.Second, "cn", false)
        // A timed-out upstream request has no request ID and may reply late. End this
        // process epoch instead of allowing that reply into a subsequent request.
        if err != nil {
            p.emit(event{"id":req.ID,"type":"error","ip":ip,"code":"lookup_failed","restartRequired":true,"elapsedMs":time.Since(before).Milliseconds()})
            p.emit(event{"id":req.ID,"type":"done","status":"failed","success":success,"failed":failed+1,"elapsedMs":time.Since(started).Milliseconds()})
            return false
        }
        if err != nil || geo == nil || (geo.Country == "" && geo.City == "" && geo.Asnumber == "") || strings.Contains(geo.Asnumber,"Error") {
            failed++
            p.emit(event{"id":req.ID,"type":"error","ip":ip,"code":"lookup_failed","elapsedMs":time.Since(before).Milliseconds()})
            continue
        }
        // The upstream v3 dispatcher correlates by response IP but leaves IPGeoData.IP empty.
        // Always expose the request address separately; do not claim it was populated upstream.
        if len(p.cache)>=1024 { p.emit(event{"id":req.ID,"type":"error","code":"session_limit","restartRequired":true}); return false }
        at:=time.Now(); p.cache[ip]=cachedGeo{geo,at}; success++
        p.emit(event{"id":req.ID,"type":"result","ip":ip,"cached":false,"geo":geo,"queriedAt":at.UTC().Format(time.RFC3339Nano),"elapsedMs":time.Since(before).Milliseconds()})
    }
    status:="completed"; if failed>0 { status="partial"; if success==0 { status="failed" } }
    p.emit(event{"id":req.ID,"type":"done","status":status,"success":success,"failed":failed,"elapsedMs":time.Since(started).Milliseconds()})
    return true
}

// The v4 credential is accepted only over stdin and never stored or logged.
// Go's verified TLS transport also supports Windows hosts whose Schannel cannot
// negotiate the service connection. This is the normal token API, without PoW.
func (p *probe) v4(ctx context.Context, req request) bool {
    if !p.live || len(req.IPs)!=1 || len(req.Token)==0 || len(req.Token)>4096 || strings.ContainsAny(req.Token,"\r\n") {
        p.emit(event{"id":req.ID,"type":"error","code":"invalid_v4_request"});return true
    }
    ip,ok:=publicIP(req.IPs[0]);if !ok {p.emit(event{"id":req.ID,"type":"error","code":"not_public_ip"});return true}
    bounded,cancel:=context.WithTimeout(ctx,8*time.Second);defer cancel()
    request,err:=http.NewRequestWithContext(bounded,http.MethodGet,"https://api.nxtrace.org/v4/ipGeo?ip="+ip,nil)
    if err!=nil {p.emit(event{"id":req.ID,"type":"error","code":"invalid_v4_request"});return true}
    request.Header.Set("X-NextTrace-Token",req.Token);request.Header.Set("User-Agent","IpQualityMonitor/2.7")
    client:=&http.Client{Timeout:8*time.Second,CheckRedirect:func(*http.Request,[]*http.Request)error{return http.ErrUseLastResponse}}
    response,err:=client.Do(request);req.Token=""
    if err!=nil {p.emit(event{"id":req.ID,"type":"error","code":"v4_connection_failed"});return true}
    defer response.Body.Close()
    result:=event{"id":req.ID,"type":"result","ip":ip,"statusCode":response.StatusCode,"expiry":response.Header.Get("X-NextTrace-Quota-Expires-At"),"retryAfter":response.Header.Get("Retry-After")}
    if response.StatusCode>=200 && response.StatusCode<300 {
        body,err:=io.ReadAll(io.LimitReader(response.Body,49153))
        if err!=nil || len(body)>49152 || !json.Valid(body) {p.emit(event{"id":req.ID,"type":"error","code":"v4_invalid_response"});return true}
        result["geo"]=json.RawMessage(body)
    }
    p.emit(result);p.emit(event{"id":req.ID,"type":"done","status":"completed"});return true
}

func serve(ctx context.Context, input io.Reader, output io.Writer, p *probe) error {
    enc:=json.NewEncoder(output)
    p.emit=func(e event) { e["schema"]=1; _=enc.Encode(e) }
    if p.cache==nil { p.cache=map[string]cachedGeo{} }
    p.emit(event{"type":"ready","mode":"NextTrace-API-v3","upstream":upstreamCommit,"pid":os.Getpid()})
    scan:=bufio.NewScanner(input); scan.Buffer(make([]byte,4096),65536)
    for scan.Scan() {
        var req request
        if err:=json.Unmarshal(scan.Bytes(),&req);err!=nil { p.emit(event{"type":"error","code":"invalid_json"});continue }
        if !p.run(ctx,req) { break }
    }
    return scan.Err()
}

// Any upstream websocket/authentication diagnostic ends the session. The host
// applies a cooldown rather than allowing an internal reconnect loop to spin.
type cancelOnDiagnostic struct { cancel context.CancelFunc }
func (w cancelOnDiagnostic) Write(data []byte) (int,error) { w.cancel(); return len(data),nil }

func main() {
    live:=flag.Bool("live",false,"Enable a bounded real NextTrace v3 GeoIP experiment")
    lifetime:=flag.Duration("lifetime",10*time.Minute,"Hard experiment lifetime (maximum 10m)")
    flag.Parse()
    if *lifetime<=0 || *lifetime>10*time.Minute { fmt.Fprintln(os.Stderr,"invalid lifetime");os.Exit(2) }
    // Reserve stdout exclusively for JSON even when upstream prints API selection messages.
    output:=os.Stdout; os.Stdout=os.Stderr
    color.Output=os.Stderr; color.Error=os.Stderr; color.NoColor=true
    ctx,cancel:=context.WithCancel(context.Background());defer cancel()
    log.SetOutput(cancelOnDiagnostic{cancel})
    timer:=time.AfterFunc(*lifetime,func(){os.Exit(124)});defer timer.Stop()
    p:=&probe{live:*live}
    defer p.close()
    if err:=serve(ctx,os.Stdin,output,p);err!=nil { fmt.Fprintln(os.Stderr,"protocol input failed");os.Exit(2) }
}
