#!/usr/bin/env python3
"""Apply auditable GeoIP-only hardening to a temporary build copy, never upstream.

Pinned original files and all module ZIPs remain unchanged in source delivery.
Run by build-linux.sh after verifying and extracting the local module archive.
"""
import hashlib
from pathlib import Path
import sys


def edit(path, digest, replacements):
    original = path.read_bytes()
    if hashlib.sha256(original).hexdigest() != digest:
        raise SystemExit(f"Pinned source changed: {path.name}")
    text = original.decode()
    for before, after, count in replacements:
        if text.count(before) != count:
            raise SystemExit(f"Patch context changed: {path.name}: {before[:50]}")
        text = text.replace(before, after)
    path.write_text(text)


def patch(root):
    upstream = root / 'upstream/NTrace-core-40ac803ca233b0bc3d4b3bf1cacc8f7e0d8368f2'
    client = root / 'patched-powclient/pow_client.go'
    edit(client, '732eb5a6a5d7a505702d5da22bd5bd41a59b9bd9ef29394be932dd66ac080e9a', [
        ('\tCode int\n\tBody string', '\tCode int\n\tBody string\n\tRetryAfter string', 1),
        ('func (e *HTTPStatusError) Error() string {', 'func (e *HTTPStatusError) HTTPStatus() int { return e.Code }\nfunc (e *HTTPStatusError) RetryAfterHeader() string { return e.RetryAfter }\nfunc (e *HTTPStatusError) Unwrap() error { if e.Code == 429 { return ErrTooManyRequests }; return nil }\n\nfunc (e *HTTPStatusError) Error() string {', 1),
        ('type GetTokenParams struct {', 'type GetTokenParams struct {\n\tContext context.Context', 1),
        ('ctx := context.Background()', 'ctx := getTokenParams.Context\n\tif ctx == nil { ctx = context.Background() }', 1),
        ('client := &http.Client{', 'client := &http.Client{\n\t\tCheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse },', 1),
        ('\t\tif resp.StatusCode == http.StatusTooManyRequests {\n\t\t\treturn ErrTooManyRequests\n\t\t}\n\t\treturn &HTTPStatusError{Code: resp.StatusCode, Body: bodySnippet(resp.Body, 2048)}', '\t\treturn &HTTPStatusError{Code: resp.StatusCode, RetryAfter: resp.Header.Get("Retry-After")}', 1),
        ('\tif out == nil {', '\tbounded, readErr := io.ReadAll(io.LimitReader(resp.Body, 49153))\n\tif readErr != nil { return readErr }\n\tif len(bounded) > 49152 { return errors.New("PoW response too large") }\n\tif out == nil {', 1),
        ('json.NewDecoder(resp.Body).Decode(out)', 'json.Unmarshal(bounded, out)', 1),
        ('factors, err := solveSemiprime(ctx, challengeResponse.Challenge.Challenge)', 'if len(challengeResponse.Challenge.Challenge) > 128 || len(challengeResponse.Challenge.RequestID) > 1024 { return "", ErrInvalidChallenge }\n\tfactors, err := solveSemiprime(ctx, challengeResponse.Challenge.Challenge)', 1),
    ])
    edit(upstream / 'pow/pow.go', '1d46bb9895bb07c4606d19ecc654374b8464165944a7691ad3921b1fc35ef417', [
        ('getTokenParams := powclient.NewGetTokenParams()', 'getTokenParams := powclient.NewGetTokenParams()\n\tgetTokenParams.Context = opCtx', 1),
        ('i < 3', 'i < 1', 1),
        ('RetToken failed after 3 attempts', 'RetToken failed after 1 attempt', 1),
        ('fmt.Errorf("RetToken timed out after 10s (host=%s)", host)', 'fmt.Errorf("RetToken timed out after 10s (host=%s): %w", host, context.DeadlineExceeded)', 1),
    ])
    edit(upstream / 'util/latency.go', 'f39083ec1d9794972552cc124d97441c19e7304e242ab6390a0e306db8f113c5', [
        ('client := &http.Client{', 'client := &http.Client{\n\t\tCheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse },', 1),
        ('bodyBytes, err := io.ReadAll(resp.Body)', 'bodyBytes, err := io.ReadAll(io.LimitReader(resp.Body, 4097))\n\tif len(bodyBytes) > 4096 { return }', 1),
    ])
    edit(upstream / 'wshandle/client.go', '9de3be6979fed8b20631c98d5ff2916859d55c547086ab7132cb21645ecbf368', [
        ('"github.com/gorilla/websocket"', '"github.com/gorilla/websocket"\n\t"github.com/tsosunchia/powclient"', 1),
        ('type WsConn struct {', 'type WsConn struct {\n\tlastError error', 1),
        ('func (c *WsConn) getConn()', 'func (c *WsConn) LastError() error { c.stateMu.RLock(); defer c.stateMu.RUnlock(); return c.lastError }\nfunc (c *WsConn) setLastError(err error) { c.stateMu.Lock(); c.lastError = err; c.stateMu.Unlock() }\n\nfunc (c *WsConn) getConn()', 1),
        ('c := &WsConn{', 'if conn != nil { conn.SetReadLimit(49152) }\n\tc := &WsConn{', 1),
        ('func (c *WsConn) replaceConn(conn *websocket.Conn) {', 'func (c *WsConn) replaceConn(conn *websocket.Conn) {\n\tif conn != nil { conn.SetReadLimit(49152) }', 1),
        ('\t\t\t\t// log.Println(err)\n\t\t\t\tc.setConnected(false)', '\t\t\t\tc.setLastError(err)\n\t\t\t\tc.setConnected(false)', 1),
        ('\t\tif c.IsConnected() {\n\t\t\treturn nil\n\t\t}', '\t\tif err := c.LastError(); err != nil { return err }\n\t\tif c.IsConnected() {\n\t\t\treturn nil\n\t\t}', 1),
        ('\t\tcase <-c.closeCh:\n\t\t\treturn errConnClosed', '\t\tcase <-c.closeCh:\n\t\t\tif err := c.LastError(); err != nil { return err }\n\t\t\treturn errConnClosed', 1),
        ('func (c *WsConn) startReconnecting() bool {', 'func (c *WsConn) startReconnecting() bool {\n\tif c.LastError() != nil { return false }', 1),
        ('\t\tif err != nil {\n\t\t\tif !c.suppressCanceledContextLog(err) {\n\t\t\t\tlog.Printf("fast ip refresh failed: %v", err)', '\t\tif err != nil {\n\t\t\tc.setLastError(err)\n\t\t\tif !c.suppressCanceledContextLog(err) {\n\t\t\t\tlog.Printf("fast ip refresh failed: %v", err)', 1),
        ('\t\t\tif err != nil {\n\t\t\t\tif util.EnvDevMode {', '\t\t\tif err != nil {\n\t\t\t\tc.setLastError(err)\n\t\t\t\tif util.EnvDevMode {', 1),
        ('ws, _, err := dialer.DialContext(ctx, u.String(), requestHeader)', 'ws, response, err := dialer.DialContext(ctx, u.String(), requestHeader)\n\tif err != nil {\n\t\tif response != nil { err = &powclient.HTTPStatusError{Code: response.StatusCode, RetryAfter: response.Header.Get("Retry-After")} ; if response.Body != nil { _ = response.Body.Close() } }\n\t\tc.setLastError(err)\n\t}', 1),
    ])


if __name__ == '__main__':
    patch(Path(sys.argv[1]).resolve())
