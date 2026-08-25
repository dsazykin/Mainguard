#!/bin/sh
# Expands @response-file arguments and execs the real protoc with plain argv.
#
# grpc.tools' linux-arm64 protoc segfaults parsing @argfiles (exit 139; reproduced on 2.71.0
# and 2.76.0 with a one-line rsp), and MSBuild's ProtoCompile ALWAYS passes one — so every
# gRPC codegen build inside an arm64 jail (the macos-host substrate's .mainguard/verify) died
# before compiling a single proto. Mainguard.Protos.csproj points Protobuf_ProtocFullPath at a
# generated launcher that runs this script with the real protoc path as $1, on linux-arm64 only;
# every other platform runs protoc untouched.
#
# The rsp format is one UNQUOTED argument per line (captured from a live build); paths with
# spaces do not occur under the jail's fixed layout, so no quote handling is attempted.
set -eu

real="$1"; shift

# Rebuild argv in place: originals are consumed from the front while their expansions append at
# the back; after the original count of iterations only the expanded list remains.
n=$#
i=0
while [ "$i" -lt "$n" ]; do
    a="$1"; shift
    case "$a" in
        @*)
            while IFS= read -r line || [ -n "$line" ]; do
                [ -n "$line" ] && set -- "$@" "$line"
            done < "${a#@}"
            ;;
        *) set -- "$@" "$a" ;;
    esac
    i=$((i + 1))
done

exec "$real" "$@"
