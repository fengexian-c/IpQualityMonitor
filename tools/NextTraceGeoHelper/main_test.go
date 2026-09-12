package main

import (
 "context"
 "io"
 "net/http"
 "strings"
 "testing"
)
type testTransport func(*http.Request)(*http.Response,error)
func(f testTransport)RoundTrip(r *http.Request)(*http.Response,error){return f(r)}
func TestV4Transport(t *testing.T){
 old:=http.DefaultTransport;defer func(){http.DefaultTransport=old}()
 for _,code:=range []int{200,302,401,403,429,503}{
  calls:=0;var events []event
  http.DefaultTransport=testTransport(func(r *http.Request)(*http.Response,error){
   calls++
   if r.URL.String()!="https://api.nxtrace.org/v4/ipGeo?ip=202.97.63.30"||r.Header.Get("X-NextTrace-Token")!="fixture-token"{t.Fatal("incorrect token endpoint/header")}
   return &http.Response{StatusCode:code,Header:http.Header{"X-Nexttrace-Quota-Expires-At":[]string{"2026-09-17T03:17:18Z"},"Retry-After":[]string{"60"},"Location":[]string{"https://example.invalid"}},Body:io.NopCloser(strings.NewReader(`{"ip":"202.97.63.30","city":"圣何塞"}`)),Request:r},nil
  })
  p:=&probe{live:true,emit:func(e event){events=append(events,e)}}
  if !p.run(context.Background(),request{ID:"1",Op:"v4",IPs:[]string{"202.97.63.30"},Token:"fixture-token"})||len(events)!=2||events[0]["statusCode"]!=code||calls!=1||p.connections!=0 {t.Fatalf("status %d: transport/redirect/PoW behavior incorrect",code)}
  if code!=200&&events[0]["geo"]!=nil {t.Fatal("error body must not cross the process protocol")}
 }
}
func TestV4RejectsInvalidInputAndLargeResponse(t *testing.T){
 old:=http.DefaultTransport;defer func(){http.DefaultTransport=old}();calls:=0
 http.DefaultTransport=testTransport(func(r *http.Request)(*http.Response,error){calls++;return &http.Response{StatusCode:200,Header:http.Header{},Body:io.NopCloser(strings.NewReader(strings.Repeat(" ",50000))),Request:r},nil})
 var last event;p:=&probe{live:true,emit:func(e event){last=e}}
 p.run(context.Background(),request{ID:"1",Op:"v4",IPs:[]string{"10.0.0.1"},Token:"fixture"});if calls!=0 {t.Fatal("private IP sent")}
 p.run(context.Background(),request{ID:"2",Op:"v4",IPs:[]string{"202.97.63.30"},Token:"fixture\r\ninjected"});if calls!=0 {t.Fatal("invalid token sent")}
 p.run(context.Background(),request{ID:"3",Op:"v4",IPs:[]string{"202.97.63.30"},Token:"fixture"});if calls!=1||last["code"]!="v4_invalid_response" {t.Fatal("oversized response accepted")}
}
