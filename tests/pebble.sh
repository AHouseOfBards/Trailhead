#!/bin/sh
# Runs the Pebble tests: installs Pebble and its challenge server with Go (once, into a cache outside the repo),
# starts them on free ports, runs Jellyfin.Plugin.Trailhead.PebbleTests against them, and stops them again.
# The exit status is the tests' status. If Go, Pebble or a port can't be had, it fails; it never skips.
#
#   sh tests/pebble.sh            run the tests
#
# Called by gate_extra in tests/gate.conf, so `sh tests/gate.sh` and CI both run it.

set -u
root=$(cd "$(dirname "$0")/.." && pwd)
cd "$root" || exit 2

version=v2.10.3
cache=${XDG_CACHE_HOME:-$HOME/.cache}/trailhead-pebble/$version

die() { echo "pebble: $*" >&2; exit 1; }

command -v go >/dev/null 2>&1 || die "Go is not installed. It builds Pebble (go install); get it from https://go.dev/dl/ (CI uses actions/setup-go)."
command -v curl >/dev/null 2>&1 || die "curl is not installed; it is how this script waits for the servers."
command -v dotnet >/dev/null 2>&1 || die "dotnet is not installed."

# Windows binaries want C:/... paths; elsewhere paths are left alone.
native() { if command -v cygpath >/dev/null 2>&1; then cygpath -m "$1"; else printf '%s' "$1"; fi; }

exe=
case "$(uname -s)" in MINGW*|MSYS*|CYGWIN*) exe=.exe ;; esac

mkdir -p "$cache" || die "cannot make $cache"
if [ ! -x "$cache/pebble$exe" ] || [ ! -x "$cache/pebble-challtestsrv$exe" ] || [ ! -f "$cache/certs/cert.pem" ]; then
    echo "pebble: installing Pebble $version into $cache"
    GOBIN=$(native "$cache") go install "github.com/letsencrypt/pebble/v2/cmd/pebble@$version" || die "go install pebble failed"
    GOBIN=$(native "$cache") go install "github.com/letsencrypt/pebble/v2/cmd/pebble-challtestsrv@$version" || die "go install pebble-challtestsrv failed"

    # Pebble's test certificates (its minica root, and the localhost certificate its API serves) are in its source,
    # which go install has just put in the module cache; they are not in the binary.
    src=$(go list -m -f '{{.Dir}}' "github.com/letsencrypt/pebble/v2@$version" 2>/dev/null)
    if [ -z "$src" ]; then
        src="$(go env GOMODCACHE)/github.com/letsencrypt/pebble/v2@$version"
    fi
    src=$(cygpath -u "$src" 2>/dev/null || printf '%s' "$src")
    mkdir -p "$cache/certs" || die "cannot make $cache/certs"
    cp "$src/test/certs/localhost/cert.pem" "$src/test/certs/localhost/key.pem" "$src/test/certs/pebble.minica.pem" "$cache/certs/" || die "Pebble's test certificates are not at $src/test/certs"
fi

work=$(mktemp -d 2>/dev/null) || die "mktemp failed"
pids=
cleanup() {
    for p in $pids; do kill "$p" 2>/dev/null; done
    for p in $pids; do wait "$p" 2>/dev/null; done
    rm -rf "$work"
}
trap cleanup EXIT
trap 'exit 130' INT TERM

# Free ports: let the OS hand out six, with a throwaway Go program (no portable shell way to ask).
printf '%s\n' \
    'package main' \
    'import ("fmt"; "net")' \
    'func main() {' \
    '  var ls []net.Listener' \
    '  for i := 0; i < 6; i++ {' \
    '    l, err := net.Listen("tcp", "127.0.0.1:0")' \
    '    if err != nil { panic(err) }' \
    '    ls = append(ls, l)' \
    '    fmt.Println(l.Addr().(*net.TCPAddr).Port)' \
    '  }' \
    '}' > "$work/ports.go"
ports=$(cd "$work" && go run ports.go) || die "could not pick free ports"
set -- $ports
[ $# -eq 6 ] || die "expected 6 free ports, got: $ports"
dns=$1; api=$2; acme=$3; mgmt=$4; acme_nonce=$5; mgmt_nonce=$6

certs=$(native "$cache/certs")

# The same shape as Pebble's test/config/pebble-config.json, with an RSA issuer (the plugin's key is RSA, T34) and
# its own ports. dns-01 never uses httpPort or tlsPort.
writeconf() {
    printf '{"pebble":{"listenAddress":"127.0.0.1:%s","managementListenAddress":"127.0.0.1:%s",' "$2" "$3"
    printf '"certificate":"%s/cert.pem","privateKey":"%s/key.pem","httpPort":5002,"tlsPort":5001,' "$certs" "$certs"
    printf '"ocspResponderURL":"","externalAccountBindingRequired":false,"domainBlocklist":["blocked-domain.example"],'
    printf '"retryAfter":{"authz":3,"order":5},"keyAlgorithm":"rsa",'
    printf '"profiles":{"default":{"description":"The profile you know and love","validityPeriod":7776000},'
    printf '"shortlived":{"description":"A short-lived cert profile, without actual enforcement","validityPeriod":518400}}}}\n'
}
writeconf x "$acme" "$mgmt" > "$work/pebble.json"
writeconf x "$acme_nonce" "$mgmt_nonce" > "$work/pebble-nonce.json"

# No A or AAAA defaults: a name that resolves somewhere is what the real CA would also see, and dns-01 only reads TXT.
# The other challenge listeners are turned off so nothing else needs a port.
"$cache/pebble-challtestsrv$exe" -dnsserver "127.0.0.1:$dns" -management "127.0.0.1:$api" \
    -defaultIPv4 "" -defaultIPv6 "" -http01 "" -https01 "" -doh "" -tlsalpn01 "" > "$work/challtestsrv.log" 2>&1 &
pids="$pids $!"

# PEBBLE_VA_NOSLEEP: no random wait before validating. NONCEREJECT: Pebble refuses 5% of nonces by default, and a
# retried order would show up twice in the tests that count orders, so the normal one refuses none and the other 50%.
PEBBLE_VA_NOSLEEP=1 PEBBLE_WFE_NONCEREJECT=0 "$cache/pebble$exe" -config "$(native "$work/pebble.json")" -dnsserver "127.0.0.1:$dns" > "$work/pebble.log" 2>&1 &
pids="$pids $!"
PEBBLE_VA_NOSLEEP=1 PEBBLE_WFE_NONCEREJECT=50 "$cache/pebble$exe" -config "$(native "$work/pebble-nonce.json")" -dnsserver "127.0.0.1:$dns" > "$work/pebble-nonce.log" 2>&1 &
pids="$pids $!"

waitfor() {
    n=0
    while ! curl -sk -o /dev/null --max-time 2 "$1"; do
        n=$((n + 1))
        [ $n -le 50 ] || { echo "pebble: $1 did not answer; logs follow" >&2; tail -n 20 "$work"/*.log >&2; return 1; }
        sleep 0.2
    done
}
waitfor "http://127.0.0.1:$api/" || exit 1
waitfor "https://localhost:$acme/dir" || exit 1
waitfor "https://localhost:$acme_nonce/dir" || exit 1
waitfor "https://localhost:$mgmt/roots/0" || exit 1


# Test-only values; none is a secret.
export TRAILHEAD_PEBBLE_DIR="https://localhost:$acme/dir"
export TRAILHEAD_PEBBLE_MGMT="https://localhost:$mgmt"
export TRAILHEAD_PEBBLE_NONCE_DIR="https://localhost:$acme_nonce/dir"
export TRAILHEAD_PEBBLE_NONCE_MGMT="https://localhost:$mgmt_nonce"
export TRAILHEAD_PEBBLE_CA="$certs/pebble.minica.pem"
export TRAILHEAD_CHALLTESTSRV_API="http://127.0.0.1:$api/"
export TRAILHEAD_CHALLTESTSRV_DNS="127.0.0.1:$dns"

jf=
[ -n "${JELLYFIN_VERSION:-}" ] && jf="-p:JellyfinVersion=$JELLYFIN_VERSION"

dotnet test Jellyfin.Plugin.Trailhead.PebbleTests -c Release -nologo -v q --logger "console;verbosity=normal" $jf
status=$?
if [ $status -ne 0 ]; then
    echo "pebble: tests failed; Pebble's log tail:" >&2
    tail -n 30 "$work/pebble.log" >&2
fi
exit $status
