#!/usr/bin/env python3
"""Apply the Linux-only IQM packet contract to the exact audited mtr source.

This is NOT a general mtr patcher. Unknown versions fail before modifying any file.
All modifications remain GPL-2.0; ship this file with the matching upstream source.
"""
from pathlib import Path
import hashlib
import json
import sys


def once(text: str, old: str, new: str) -> str:
    count = text.count(old)
    if count != 1:
        raise ValueError(f"Expected one patch anchor, found {count}: {old[:100]!r}")
    return text.replace(old, new, 1)


def replace_block(text: str, marker: str, replacement: str) -> str:
    if text.count(marker) != 1:
        raise ValueError(f"Block anchor not unique: {marker}")
    start = text.index(marker)
    opened = text.index('{', start)
    depth = 1
    cursor = opened + 1
    while depth:
        if cursor >= len(text):
            raise ValueError("Unbalanced upstream block")
        if text[cursor] == '{': depth += 1
        elif text[cursor] == '}': depth -= 1
        cursor += 1
    return text[:start] + replacement + text[cursor:]


def apply(root: Path) -> None:
    lock = json.loads((Path(__file__).with_name('UPSTREAM.json')).read_text())
    sources = {}
    for name, expected in lock['files'].items():
        data = (root / name).read_bytes()
        actual = hashlib.sha1(b'blob ' + str(len(data)).encode() + b'\0' + data).hexdigest()
        if actual != expected:
            raise ValueError(f"Upstream mismatch for {name}: {actual}; expected {expected}")
        sources[name] = data.decode('utf-8')

    header = sources['packet/probe.h']
    header = once(header, '    int timeout;', '    int timeout;\n\n    /* IQM: exact per-probe timeout, zero keeps upstream seconds. */\n    int timeout_ms;')
    sources['packet/probe.h'] = header

    command = sources['packet/command.c']
    command = once(command, '    if (!strcmp(feature, "version")) {', '''    /* IQM requires raw ICMP: datagram error queues lose some type/code information. */
    if (!strcmp(feature, "iqm-contract-v1")) return "ok";
    if (!strcmp(feature, "iqm-raw-ip-4"))
        return net_state->platform.ip4_present && net_state->platform.ip4_socket_raw ? "ok" : "no";
    if (!strcmp(feature, "iqm-raw-ip-6"))
        return net_state->platform.ip6_present && net_state->platform.ip6_socket_raw ? "ok" : "no";
    if (!strcmp(feature, "version")) {''')
    command = once(command, '    /*  Number of seconds to wait for a reply  */', '''    if (!strcmp(name, "timeout-ms")) {
        long value_ms;
        errno = 0;
        value_ms = strtol(value, &endstr, 10);
        if (errno || endstr == value || *endstr || value_ms < 1 || value_ms > 60000) return false;
        param->timeout_ms = (int)value_ms;
        return true;
    }

    /*  Number of seconds to wait for a reply  */''')
    command = once(command, '''    } else if (!strcmp(command->command_name, COMMAND_NAME_SEND_PROBE)) {
        send_probe_command(command, net_state);''', '''    } else if (!strcmp(command->command_name, "cancel-probe")) {
        struct probe_t *p;
        LIST_FOREACH(p, &net_state->outstanding_probes, probe_list_entry) {
            if (p->token == command->token) { free_probe(net_state, p); break; }
        }
        printf("%d cancelled\\n", command->token);
    } else if (!strcmp(command->command_name, COMMAND_NAME_SEND_PROBE)) {
        send_probe_command(command, net_state);''')
    sources['packet/command.c'] = command

    unix = sources['packet/probe_unix.c']
    unix = once(unix, '    probe->platform.timeout_time.tv_sec += param->timeout;', '''    if (param->timeout_ms > 0) {
        probe->platform.timeout_time.tv_sec += param->timeout_ms / 1000;
        probe->platform.timeout_time.tv_usec += (param->timeout_ms % 1000) * 1000;
        if (probe->platform.timeout_time.tv_usec >= 1000000) {
            probe->platform.timeout_time.tv_sec++;
            probe->platform.timeout_time.tv_usec -= 1000000;
        }
    } else {
        probe->platform.timeout_time.tv_sec += param->timeout;
    }''')
    sources['packet/probe_unix.c'] = unix

    # Upstream receives before checking expiry; a queued late packet must not be a success.
    unix = once(unix, '    round_trip_us =', '''    if ((probe->platform.timeout_time.tv_sec || probe->platform.timeout_time.tv_usec) &&
        compare_timeval(*timestamp, probe->platform.timeout_time) >= 0) {
        printf("%d no-reply\\n", probe->token);
        free_probe(net_state, probe);
        return;
    }
    round_trip_us =''')
    sources['packet/probe_unix.c'] = unix

    # Carry ICMP type/code through the existing internal integer result parameter.
    # Only the private component emits these tagged results; CLI mtr is not installed.
    decoder = sources['packet/deconstruct_unix.c']
    decoder = once(decoder, '''    receive_probe(net_state, probe, icmp_type,
                  remote_addr, timestamp, mpls_count, mpls);''', '''    if (probe->remote_addr.ss_family != remote_addr->ss_family) return;
    if (icmp_type == ICMP_ECHOREPLY &&
        memcmp(sockaddr_addr_offset(&probe->remote_addr),
               sockaddr_addr_offset(remote_addr), sockaddr_addr_size(remote_addr))) return;
    receive_probe(net_state, probe, icmp_type,
                  remote_addr, timestamp, mpls_count, mpls);''')
    for echo in ('ICMP_ECHOREPLY', 'ICMP6_ECHOREPLY'):
        decoder = once(decoder, f'    if (icmp->type == {echo}) {{',
                       f'    if (icmp->type == {echo} && icmp->code == 0) {{')
    for family, condition in ((4, 'ICMP_DEST_UNREACH'), (6, 'ICMP6_DEST_UNREACH')):
        marker = f'    if (icmp->type == {condition}) {{'
        replacement = f'''    if (icmp->type == {condition}) {{
        handle_inner_ip{family}_packet(net_state, remote_addr,
            0x10000 | ((int)icmp->type << 8) | icmp->code,
            inner_ip, inner_size, timestamp, mpls_count, mpls);
    }}'''
        decoder = replace_block(decoder, marker, replacement)
    ttl_anchor = "                                ICMP_TIME_EXCEEDED, inner_ip, inner_size,\n                                timestamp, mpls_count, mpls);"
    if decoder.count(ttl_anchor) != 2:
        raise ValueError("Expected the IPv4 and IPv6 TTL handler anchors")
    decoder = decoder.replace(ttl_anchor,
        "                                icmp->code == 0 ? ICMP_TIME_EXCEEDED :\n"
        "                                    (0x10000 | ((int)icmp->type << 8) | icmp->code),\n"
        "                                inner_ip, inner_size, timestamp, mpls_count, mpls);")
    # IPv4 parameter problem and IPv6 packet-too-big/parameter-problem are errors, not silence.
    marker4 = '    if (icmp->type == ICMP_DEST_UNREACH) {'
    decoder = once(decoder, marker4, '''    if (icmp->type == 12) {
        handle_inner_ip4_packet(net_state, remote_addr,
            0x10000 | ((int)icmp->type << 8) | icmp->code,
            inner_ip, inner_size, timestamp, mpls_count, mpls);
    }
''' + marker4)
    marker6 = '    if (icmp->type == ICMP6_DEST_UNREACH) {'
    decoder = once(decoder, marker6, '''    if (icmp->type == 2 || icmp->type == 4) {
        handle_inner_ip6_packet(net_state, remote_addr,
            0x10000 | ((int)icmp->type << 8) | icmp->code,
            inner_ip, inner_size, timestamp, mpls_count, mpls);
    }
''' + marker6)
    sources['packet/deconstruct_unix.c'] = decoder

    probe = sources['packet/probe.c']
    probe = once(probe, '''    if (icmp_type == ICMP_TIME_EXCEEDED) {
        result = "ttl-expired";''', '''    int iqm_error = (icmp_type & 0x10000) != 0;
    if (iqm_error) {
        result = "icmp-error";
    } else if (icmp_type == ICMP_TIME_EXCEEDED) {
        result = "ttl-expired";''')
    anchor = '''             probe->token, result, ip_argument, ip_text, round_trip_us);'''
    probe = once(probe, anchor, anchor + '''
    if (iqm_error) {
        snprintf(response, COMMAND_BUFFER_SIZE,
            "%d icmp-error %s %s round-trip-time %u icmp-type %d icmp-code %d",
            probe->token, ip_argument, ip_text, round_trip_us,
            (icmp_type >> 8) & 255, icmp_type & 255);
    }
''')
    sources['packet/probe.c'] = probe

    # Linux packet timing must use one monotonic clock domain for sends, receives and expiry.
    clock_header = '''#ifndef IQM_CLOCK_H
#define IQM_CLOCK_H
#include <sys/time.h>
#include <time.h>
#include <stdlib.h>
static inline int iqm_gettime(struct timeval *value, void *unused) {
    struct timespec now;
    (void)unused;
    if (clock_gettime(CLOCK_MONOTONIC, &now) != 0) abort();
    value->tv_sec = now.tv_sec;
    value->tv_usec = now.tv_nsec / 1000;
    return 0;
}
#endif
'''
    for file in sorted((root / 'packet').glob('*.c')):
        name = str(file.relative_to(root))
        text = sources.get(name, file.read_text())
        if 'gettimeofday(' in text:
            sources[name] = '#include "iqm_clock.h"\n' + text.replace('gettimeofday(', 'iqm_gettime(')
    sources['packet/iqm_clock.h'] = clock_header
    # Write only after every original hash and every patch anchor has passed.
    for name, text in sources.items():
        (root / name).write_text(text)
    (root / 'IQM-PATCHES.txt').write_text(
        'IQM Linux packet contract v1\nUpstream: ' + lock['commit'] + '\n'
        'Changes: millisecond expiry; acknowledged cancellation; raw-mode capability checks;\n'
        'lossless error type/code for handled ICMP errors; monotonic packet clocks;\n'
        'reject late replies, mismatched address families and foreign Echo replies.\n'
        'Experimental: final image and controlled-network validation required.\n')


if __name__ == '__main__':
    if len(sys.argv) != 2:
        raise SystemExit('usage: patch.py /path/to/exact-mtr-checkout')
    apply(Path(sys.argv[1]).resolve())
