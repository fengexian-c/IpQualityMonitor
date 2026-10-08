#!/bin/sh
set -eu
cd "$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
if [ "$(id -u)" != 0 ]; then
    printf '%s\n' 'Run with sudo: the fixed non-root container UID needs ownership of its data directory.' >&2
    exit 1
fi
umask 077
mkdir -p data secrets
# Only the application data directory is changed; never recursively chown a NAS share.
chown 10001:10001 data
chmod 0700 data
chmod 0700 secrets
if [ ! -f secrets/admin_password.txt ]; then
    if command -v openssl >/dev/null 2>&1; then
        openssl rand -base64 36 > secrets/admin_password.txt
    elif command -v python3 >/dev/null 2>&1; then
        python3 -c 'import secrets; print(secrets.token_urlsafe(36))' > secrets/admin_password.txt
    else
        echo 'Install openssl or python3 to generate the initial password.' >&2; exit 1
    fi
fi
chown 10001:10001 secrets/admin_password.txt
chmod 0400 secrets/admin_password.txt
if [ ! -f .env ]; then cp .env.example .env; fi
printf '%s\n' 'Initialized. Edit deploy/.env to select the host binding address.' \
    'The password was NOT printed. Read it locally with: sudo cat deploy/secrets/admin_password.txt' \
    'Do not commit deploy/data, deploy/secrets, or deploy/.env.'
