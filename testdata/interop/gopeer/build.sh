#!/bin/sh
# Builds the go-dnp3 interoperability peers into one directory:
#   dnp3-master, dnp3-outstation   the stock binaries
#   gopeer                         this directory: a master and an outstation
#                                  built on go-dnp3's public packages that
#                                  exercise the extended services
#
# usage: build.sh /path/to/go-dnp3 /path/to/output-dir
#
# Point GO_DNP3_BIN at the output directory to run the interop tests.
set -eu

src=$(cd "$1" && pwd)
dst=$2
here=$(cd "$(dirname "$0")" && pwd)

mkdir -p "$dst"
(cd "$src" && go build -o "$dst/" ./cmd/dnp3-master ./cmd/dnp3-outstation)

# Built in a scratch copy so that pointing the module at a local checkout does
# not leave a replace directive in the committed go.mod.
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
cp "$here/main.go" "$here/go.mod" "$work/"
cp "$src/go.sum" "$work/"
(cd "$work" &&
  go mod edit -replace "github.com/dscsystems/go-dnp3=$src" &&
  GOFLAGS=-mod=mod go build -o "$dst/gopeer" .)
