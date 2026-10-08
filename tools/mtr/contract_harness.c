/* Synthetic packet tests for the exact patched upstream decoder, no sockets/capabilities. */
#include "packet/probe.h"
#include "packet/protocols.h"
#include "packet/deconstruct_unix.h"
#include "packet/command.h"
#include <assert.h>
#include <arpa/inet.h>
#include <stdio.h>
#include <string.h>
#include <unistd.h>

static struct probe_t *fixture(struct net_state_t *state, int token, int family)
{
    memset(state, 0, sizeof(*state));
    LIST_INIT(&state->outstanding_probes);
    state->platform.ip4_present = state->platform.ip6_present = true;
    state->platform.ip4_socket_raw = state->platform.ip6_socket_raw = true;
    state->platform.next_sequence = 40000;
    struct probe_t *probe = alloc_probe(state, token);
    assert(probe != NULL);
    assert(decode_address_string(family, family == 4 ? "127.0.0.1" : "::1", &probe->remote_addr) == 0);
    probe->platform.departure_time = (struct timeval){100, 0};
    probe->platform.timeout_time = (struct timeval){101, 0};
    return probe;
}

static void decode(int token, int family, int type, int code, int late, int foreign)
{
    struct net_state_t state;
    struct probe_t *probe = fixture(&state, token, family);
    struct sockaddr_storage peer = probe->remote_addr;
    if (foreign) assert(decode_address_string(family, family == 4 ? "127.0.0.2" : "::2", &peer) == 0);
    struct timeval stamp = {late ? 101 : 100, 1250};
    if (family == 4) {
        struct { struct IPHeader ip; struct ICMPHeader icmp; struct IPHeader inner; struct ICMPHeader echo; } packet = {0};
        packet.ip.version = packet.inner.version = 0x45;
        packet.ip.protocol = packet.inner.protocol = IPPROTO_ICMP;
        packet.icmp.type = type; packet.icmp.code = code;
        packet.echo.type = ICMP_ECHO;
        packet.icmp.id = packet.echo.id = htons(getpid());
        packet.icmp.sequence = packet.echo.sequence = htons(probe->sequence);
        handle_received_ip4_packet(&state, &peer, &packet,
            type == ICMP_ECHOREPLY ? sizeof(packet.ip) + sizeof(packet.icmp) : sizeof(packet), &stamp);
    } else {
        struct { struct ICMPHeader icmp; struct IP6Header inner; struct ICMPHeader echo; } packet = {0};
        packet.inner.version = 0x60; packet.inner.protocol = IPPROTO_ICMPV6;
        packet.icmp.type = type; packet.icmp.code = code;
        packet.echo.type = ICMP6_ECHO;
        packet.icmp.id = packet.echo.id = htons(getpid());
        packet.icmp.sequence = packet.echo.sequence = htons(probe->sequence);
        handle_received_ip6_packet(&state, &peer, &packet,
            type == ICMP6_ECHOREPLY ? sizeof(packet.icmp) : sizeof(packet), &stamp);
    }
    if (foreign || ((type == ICMP_ECHOREPLY || type == ICMP6_ECHOREPLY) && code)) {
        assert(state.outstanding_probe_count == 1);
        free_probe(&state, probe);
    } else assert(state.outstanding_probe_count == 0);
}

static void command(struct net_state_t *state, const char *text)
{
    struct command_buffer_t buffer = {0};
    buffer.incoming_read_position = strlen(text);
    assert(buffer.incoming_read_position < COMMAND_BUFFER_SIZE);
    memcpy(buffer.incoming_buffer, text, buffer.incoming_read_position);
    dispatch_buffer_commands(&buffer, state);
}

int main(void)
{
    decode(1, 4, ICMP_ECHOREPLY, 0, 0, 0);
    decode(2, 6, ICMP6_ECHOREPLY, 0, 0, 0);
    decode(3, 4, ICMP_TIME_EXCEEDED, 0, 0, 0);
    decode(4, 6, ICMP6_TIME_EXCEEDED, 0, 0, 0);
    decode(5, 4, ICMP_DEST_UNREACH, 3, 0, 0);
    decode(6, 6, ICMP6_DEST_UNREACH, 4, 0, 0);
    decode(7, 4, ICMP_TIME_EXCEEDED, 1, 0, 0);
    decode(8, 6, ICMP6_TIME_EXCEEDED, 1, 0, 0);
    decode(9, 4, 12, 2, 0, 0);
    decode(10, 6, 2, 0, 0, 0);
    decode(11, 6, 4, 1, 0, 0);
    decode(12, 4, ICMP_ECHOREPLY, 0, 1, 0);
    decode(13, 6, ICMP6_TIME_EXCEEDED, 0, 1, 0);
    decode(14, 4, ICMP_ECHOREPLY, 0, 0, 1);
    decode(15, 6, ICMP6_ECHOREPLY, 0, 0, 1);
    decode(16, 4, ICMP_ECHOREPLY, 1, 0, 0);
    decode(17, 6, ICMP6_ECHOREPLY, 1, 0, 0);
    struct net_state_t state;
    fixture(&state, 18, 4);
    command(&state, "18 cancel-probe\n");
    assert(state.outstanding_probe_count == 0);
    command(&state, "18 cancel-probe\n");
    command(&state, "19 check-support feature iqm-contract-v1\n20 check-support feature iqm-raw-ip-4\n21 check-support feature iqm-raw-ip-6\n");
    state.platform.ip6_socket_raw = false;
    command(&state, "22 check-support feature iqm-raw-ip-6\n");
    return 0;
}
