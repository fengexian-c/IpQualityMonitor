package main

import (
	"context"
	"encoding/json"
	"errors"
	"github.com/nxtrace/NTrace-core/ipgeo"
	"io"
	"net/http"
	"strings"
	"testing"
	"time"
)

type testTransport func(*http.Request) (*http.Response, error)

func (f testTransport) RoundTrip(r *http.Request) (*http.Response, error) { return f(r) }
func TestV4Transport(t *testing.T) {
	old := http.DefaultTransport
	defer func() { http.DefaultTransport = old }()
	for _, code := range []int{200, 302, 401, 403, 429, 503} {
		calls := 0
		var events []event
		http.DefaultTransport = testTransport(func(r *http.Request) (*http.Response, error) {
			calls++
			if r.URL.String() != "https://api.nxtrace.org/v4/ipGeo?ip=202.97.63.30" || r.Header.Get("X-NextTrace-Token") != "fixture-token" {
				t.Fatal("incorrect token endpoint/header")
			}
			return &http.Response{StatusCode: code, Header: http.Header{"X-Nexttrace-Quota-Expires-At": []string{"2026-09-17T03:17:18Z"}, "Retry-After": []string{"60"}, "Location": []string{"https://example.invalid"}}, Body: io.NopCloser(strings.NewReader(`{"ip":"202.97.63.30","city":"圣何塞"}`)), Request: r}, nil
		})
		p := &probe{live: true, emit: func(e event) { events = append(events, e) }}
		if !p.run(context.Background(), request{ID: "1", Op: "v4", IPs: []string{"202.97.63.30"}, Token: "fixture-token"}) || len(events) != 2 || events[0]["statusCode"] != code || calls != 1 || p.connections != 0 {
			t.Fatalf("status %d: transport/redirect/PoW behavior incorrect", code)
		}
		if code != 200 && events[0]["geo"] != nil {
			t.Fatal("error body must not cross the process protocol")
		}
	}
}
func TestV4RejectsInvalidInputAndLargeResponse(t *testing.T) {
	old := http.DefaultTransport
	defer func() { http.DefaultTransport = old }()
	calls := 0
	http.DefaultTransport = testTransport(func(r *http.Request) (*http.Response, error) {
		calls++
		return &http.Response{StatusCode: 200, Header: http.Header{}, Body: io.NopCloser(strings.NewReader(strings.Repeat(" ", 50000))), Request: r}, nil
	})
	var last event
	p := &probe{live: true, emit: func(e event) { last = e }}
	p.run(context.Background(), request{ID: "1", Op: "v4", IPs: []string{"10.0.0.1"}, Token: "fixture"})
	if calls != 0 {
		t.Fatal("private IP sent")
	}
	p.run(context.Background(), request{ID: "2", Op: "v4", IPs: []string{"202.97.63.30"}, Token: "fixture\r\ninjected"})
	if calls != 0 {
		t.Fatal("invalid token sent")
	}
	p.run(context.Background(), request{ID: "3", Op: "v4", IPs: []string{"202.97.63.30"}, Token: "fixture"})
	if calls != 1 || last["code"] != "v4_invalid_response" {
		t.Fatal("oversized response accepted")
	}
}

func TestAllReservedAddressesAreOffline(t *testing.T) {
	old := http.DefaultTransport
	defer func() { http.DefaultTransport = old }()
	calls := 0
	http.DefaultTransport = testTransport(func(r *http.Request) (*http.Response, error) { calls++; panic("reserved address escaped") })
	for _, ip := range []string{"10.0.0.1", "127.0.0.1", "169.254.1.1", "100.64.0.1", "192.168.1.1", "192.0.2.1", "198.18.1.1", "198.51.100.1", "203.0.113.1", "224.0.0.1", "240.0.0.1", "::1", "fc00::1", "fe80::1", "2001:db8::1", "2002::1", "3fff::1", "::ffff:10.0.0.1", "fe80::1%eth0", "not-an-ip"} {
		p := &probe{live: true, cache: map[string]cachedGeo{}, emit: func(event) {}, connectFn: func(context.Context) error { calls++; panic("reserved address connected") }}
		p.run(context.Background(), request{ID: "v3", Op: "lookup", IPs: []string{ip}})
		p.run(context.Background(), request{ID: "v4", Op: "v4", IPs: []string{ip}, Token: "fixture"})
	}
	if calls != 0 {
		t.Fatal("private input made outbound request")
	}
}

func TestProtocolSchemaCorrelationAndMalformedInput(t *testing.T) {
	var output strings.Builder
	input := "not json\n" + `{"id":"req-1","op":"lookup","ips":["127.0.0.1"]}` + "\n" + `{"id":"req-2","op":"shutdown"}` + "\n"
	if err := serve(context.Background(), strings.NewReader(input), &output, &probe{}); err != nil {
		t.Fatal(err)
	}
	lines := strings.Split(strings.TrimSpace(output.String()), "\n")
	if len(lines) != 5 {
		t.Fatalf("unexpected frames: %s", output.String())
	}
	for i, line := range lines {
		var frame map[string]any
		if err := json.Unmarshal([]byte(line), &frame); err != nil {
			t.Fatal(err)
		}
		if frame["schema"] != float64(1) {
			t.Fatal("schema missing")
		}
		if i == 2 && (frame["id"] != "req-1" || frame["ip"] != "127.0.0.1") {
			t.Fatal("correlation lost")
		}
	}
	var oversized strings.Builder
	if err := serve(context.Background(), strings.NewReader(strings.Repeat("x", 65537)+"\n"), &oversized, &probe{}); err == nil {
		t.Fatal("oversized input accepted")
	}
}

func TestV3CacheAndFailedEpoch(t *testing.T) {
	calls := 0
	var events []event
	p := &probe{live: true, cache: map[string]cachedGeo{}, emit: func(e event) { events = append(events, e) }, lookup: func(ip string, _ time.Duration, _ string, _ bool) (*ipgeo.IPGeoData, error) {
		calls++
		return &ipgeo.IPGeoData{Country: "fixture", City: "mock"}, nil
	}}
	req := request{ID: "r1", Op: "lookup", IPs: []string{"8.8.8.8"}}
	if !p.run(context.Background(), req) || !p.run(context.Background(), req) || calls != 1 || p.hits != 1 {
		t.Fatal("cache did not reuse one request")
	}
	p.lookup = func(string, time.Duration, string, bool) (*ipgeo.IPGeoData, error) { return nil, errors.New("TimeOut") }
	if p.run(context.Background(), request{ID: "r2", Op: "lookup", IPs: []string{"1.1.1.1"}}) {
		t.Fatal("timeout reused unsafe upstream epoch")
	}
	found := false
	for _, e := range events {
		if e["reason"] == "timeout" && e["id"] == "r2" && e["ip"] == "1.1.1.1" && e["restartRequired"] == true {
			found = true
		}
	}
	if !found {
		t.Fatal("structured timeout missing")
	}
}

func TestV4MalformedOrMismatchedResponse(t *testing.T) {
	old := http.DefaultTransport
	defer func() { http.DefaultTransport = old }()
	for _, body := range []string{`null`, `[]`, `{}`, `{"ip":"1.1.1.1","city":"wrong IP"}`, `{"ip":"8.8.8.8","city":123}`, `{"city":"ok"} {"city":"extra"}`} {
		http.DefaultTransport = testTransport(func(r *http.Request) (*http.Response, error) {
			return &http.Response{StatusCode: 200, Header: http.Header{}, Body: io.NopCloser(strings.NewReader(body)), Request: r}, nil
		})
		var events []event
		p := &probe{live: true, emit: func(e event) { events = append(events, e) }}
		p.run(context.Background(), request{ID: "r", Op: "v4", IPs: []string{"8.8.8.8"}, Token: "fixture"})
		if len(events) != 1 || events[0]["code"] != "v4_invalid_response" || events[0]["id"] != "r" || events[0]["ip"] != "8.8.8.8" {
			t.Fatalf("accepted malformed or mismatched geo %q: %v", body, events)
		}
	}
}

func TestRejectedHugeIPCannotAmplifyProtocol(t *testing.T) {
	var output strings.Builder
	body, _ := json.Marshal(request{ID: "huge", Op: "lookup", IPs: []string{strings.Repeat("<", 10000)}})
	if err := serve(context.Background(), strings.NewReader(string(body)+"\n"), &output, &probe{}); err != nil {
		t.Fatal(err)
	}
	for _, line := range strings.Split(strings.TrimSpace(output.String()), "\n") {
		if len(line) > 65535 {
			t.Fatal("protocol output amplification")
		}
	}
}
