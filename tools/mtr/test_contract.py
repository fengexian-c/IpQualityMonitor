#!/usr/bin/env python3
"""Compile and test the patched native decoder against synthetic packets, without network access.

Usage: test_contract.py /path/to/patched-and-configured-mtr
This complements, and does not replace, real final-image/network tests.
"""
from pathlib import Path
import os
import subprocess
import sys
import tempfile

def main():
    if len(sys.argv) != 2:
        raise SystemExit("usage: test_contract.py /path/to/patched-and-configured-mtr")
    root = Path(sys.argv[1]).resolve()
    if not (root / 'config.h').is_file():
        raise SystemExit('Run the upstream configure step first (config.h is required)')
    sources = ['cmdparse', 'command', 'probe', 'timeval', 'sockaddr', 'construct_unix',
               'deconstruct_unix', 'probe_unix', 'wait_unix']
    with tempfile.TemporaryDirectory() as directory:
        executable = Path(directory) / 'contract-harness'
        subprocess.run([os.environ.get('CC', 'cc'), '-std=gnu11', '-Wall', '-Wno-pointer-sign',
                        '-I' + str(root), '-include', str(root / 'config.h'),
                        str(Path(__file__).with_name('contract_harness.c')),
                        *[str(root / 'packet' / (name + '.c')) for name in sources],
                        '-o', str(executable)], check=True)
        lines = subprocess.check_output([str(executable)], text=True, timeout=10).splitlines()
    expected = {
        1: 'reply ip-4 127.0.0.1 round-trip-time 1250',
        2: 'reply ip-6 ::1 round-trip-time 1250',
        3: 'ttl-expired ip-4 127.0.0.1 round-trip-time 1250',
        4: 'ttl-expired ip-6 ::1 round-trip-time 1250',
        12: 'no-reply', 13: 'no-reply', 18: 'cancelled',
        19: 'feature-support support ok', 20: 'feature-support support ok',
        21: 'feature-support support ok', 22: 'feature-support support no',
    }
    for token, family, kind, code in [(5, 4, 3, 3), (6, 6, 1, 4), (7, 4, 11, 1),
                                    (8, 6, 3, 1), (9, 4, 12, 2), (10, 6, 2, 0), (11, 6, 4, 1)]:
        address = '127.0.0.1' if family == 4 else '::1'
        expected[token] = f'icmp-error ip-{family} {address} round-trip-time 1250 icmp-type {kind} icmp-code {code}'
    actual = {}
    for line in lines:
        token, reply = line.split(' ', 1)
        token = int(token)
        assert token not in actual or token == 18, f'Duplicate response: {line}'
        actual[token] = reply
    assert len(lines) == len(expected) + 1, lines  # repeated cancellation is idempotent
    assert actual == expected, (actual, expected)
    print('PASS native contract: IPv4/IPv6 Echo, TTL, type/code, late-packet expiry, foreign/malformed Echo rejection, cancellation, raw capability gates')


if __name__ == "__main__":
    main()
