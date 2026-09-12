#!/bin/sh
# First-boot provisioning of the mainguardd key-ring passphrase (F53 / audit-W1D).
#
# WHY THIS EXISTS
# ---------------
# The daemon's secrets (notably the audit chain's AES-GCM master key) live in a DataProtection key
# ring under $HOME/.mainguard/Keyring. On Windows that ring is wrapped with DPAPI and on macOS with a
# login-Keychain key; on Linux there is no equivalent the daemon can reach — a systemd service has no
# session D-Bus for libsecret, the kernel keyring does not survive a reboot, and systemd-creds needs
# root for the host key. So Mainguard wraps the ring with AES-256-GCM under a PBKDF2-stretched
# operator passphrase, and SOMETHING has to supply that passphrase on a machine nobody types into.
#
# That something is this script. It mints one random 256-bit passphrase per INSTALL (never per image:
# the MainguardOS tarball is byte-reproducible, so a baked-in secret would be the same on every
# machine on earth) and leaves it in a root-owned file the daemon's unprivileged user can read.
#
# WHAT THIS DOES AND DOES NOT BUY
# -------------------------------
# It does NOT defend against root, nor against the `mainguard` user itself — that user's daemon has
# to be able to decrypt the ring, so anything running as it can too. It DOES mean the key ring is no
# longer self-contained: a copy of $HOME/.mainguard (a backup, a `wsl --export`, a synced folder, a
# stolen VHDX read on another machine) carries ciphertext and no key, where before it carried the
# master key in plain XML next to the secrets it protects. That is the gap F53 is about.
#
# An operator who wants a stronger custody model replaces the file — with a systemd
# `LoadCredential=`/`LoadCredentialEncrypted=` target, a passphrase held on a TPM, or anything else
# that lands the text in a file the unit points MAINGUARD_KEYRING_PASSPHRASE_FILE at. This script
# never overwrites a passphrase that is already there.
#
# Run as root from the unit's `ExecStartPre=+…` line, before mainguardd starts.
set -eu

DIR=/etc/mainguard
FILE="$DIR/keyring.passphrase"
GROUP=mainguard

# 0750 root:mainguard — the daemon's user can traverse in, nobody else can.
mkdir -p "$DIR"
chown "root:$GROUP" "$DIR"
chmod 0750 "$DIR"

# Never regenerate: a new passphrase would make the existing key ring — and with it the audit
# master key and every secret encrypted under it — permanently unreadable.
if [ -s "$FILE" ]; then
  chown "root:$GROUP" "$FILE"
  chmod 0640 "$FILE"
  exit 0
fi

TMP="$(mktemp "$DIR/.keyring.passphrase.XXXXXX")"
chmod 0640 "$TMP"
# 32 bytes of kernel CSPRNG, base64'd. No trailing newline worries: the daemon strips those.
head -c 32 /dev/urandom | base64 | tr -d '\n' > "$TMP"
chown "root:$GROUP" "$TMP"
# Atomic: a half-written passphrase file would be a half-lost key ring.
mv "$TMP" "$FILE"

echo "mainguardd: minted a machine-local key-ring passphrase at $FILE" >&2
