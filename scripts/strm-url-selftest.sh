#!/usr/bin/env bash
set -euo pipefail

DOTNET=${DOTNET:-$HOME/.dotnet/dotnet}
ROOT=$(cd "$(dirname "$0")/.." && pwd)
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT

cp "$ROOT/StrmAssistant/Common/StrmUrlHelper.cs" "$TMP/"
cat > "$TMP/selftest.csproj" <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework></PropertyGroup>
</Project>
EOF
cat > "$TMP/Program.cs" <<'EOF'
using System;
using StrmAssistant.Common;

const string raw = "http://172.17.0.1:9527/d/csoeobr9uu532d4sf.mp4?/熊出没 (2012) S01E48 [2160p H.265].mp4";
const string expected = "http://172.17.0.1:9527/d/csoeobr9uu532d4sf.mp4?/%E7%86%8A%E5%87%BA%E6%B2%A1%20%282012%29%20S01E48%20%5B2160p%20H.265%5D.mp4";

var actual = StrmUrlHelper.Normalize(raw);
Console.WriteLine("raw       = " + raw);
Console.WriteLine("normalized= " + actual);
if (actual != expected) { Console.Error.WriteLine("FAIL: unexpected encoding"); return 1; }
if (StrmUrlHelper.Normalize(actual) != actual) { Console.Error.WriteLine("FAIL: not idempotent"); return 2; }
if (StrmUrlHelper.Normalize("http://h/a%20b?x=1&y=2") != "http://h/a%20b?x=1&y=2") { Console.Error.WriteLine("FAIL: damaged valid URL"); return 3; }
if (StrmUrlHelper.Normalize("/tmp/a b.strm") != "/tmp/a b.strm") { Console.Error.WriteLine("FAIL: changed non-http path"); return 4; }
if (StrmUrlHelper.Normalize("http://h/a%20b?x=1&y=2#z") != "http://h/a%20b?x=1&y=2#z") { Console.Error.WriteLine("FAIL: damaged structure"); return 5; }
Console.WriteLine("PASS: 5 assertions");
return 0;
EOF

"$DOTNET" run --project "$TMP/selftest.csproj" --configuration Release
