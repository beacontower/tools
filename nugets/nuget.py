"""Vendor the BeaconTower NuGet packages a customer needs to build a provider.

Every BeaconTower package - BeaconTower.ProviderSdk included - resolves from a
private GitHub Packages feed that answers 401 without Glaze credentials, so a
customer cannot restore against it. The bundle carries the packages instead.

Every version comes from upstream.lock and nothing is hardcoded here. A list
in this file would drift from what the lock pins, and the customer's restore
would then fail with a 401 that reads like a credentials problem rather than a
packaging omission.

Three roots are named there: providersdk, which the customer's project compiles
against, and telemetry + healthchecks, which a provider HOST needs and which
are not ProviderSdk dependencies, so its closure does not bring them.

The closure is walked rather than listed. Each .nupkg is a zip holding a
.nuspec; its <dependency id="BeaconTower.*"> entries name the next layer - so
ProviderSdk's own five dependencies come along without being listed here.

Usage:
    nuget.py vendor <out-dir> [upstream.lock]   # download and write nuget.config
    nuget.py verify <out-dir>                   # list what is there
"""
import os
import pathlib
import re
import subprocess
import sys
import urllib.error
import urllib.request
import zipfile

FEED = "https://nuget.pkg.github.com/beacontower"

# NuGet package id <- the name it is pinned under in upstream.lock.
ROOTS = {
    "BeaconTower.ProviderSdk": "providersdk",
    "BeaconTower.Telemetry": "telemetry",
    "BeaconTower.HealthChecks": "healthchecks",
}
SDK = "BeaconTower.ProviderSdk"


def die(msg):
    print("nuget: %s" % msg, file=sys.stderr)
    sys.exit(1)


class StripAuthOnRedirect(urllib.request.HTTPRedirectHandler):
    """The feed 302s to Azure blob storage with a pre-signed URL. Carrying the
    Basic credential across that hop makes the blob host reject it with 403, so
    the header has to be dropped on redirect - which is what curl does."""

    def redirect_request(self, req, fp, code, msg, headers, newurl):
        new = super().redirect_request(req, fp, code, msg, headers, newurl)
        if new is not None:
            for h in [k for k in new.headers if k.lower() == "authorization"]:
                del new.headers[h]
        return new


def account():
    """GitHub Packages rejects a placeholder username with 403 even when the
    token is valid, so the real login is required."""
    a = os.environ.get("GITHUB_ACTOR")
    if a:
        return a
    try:
        return subprocess.run(["gh", "api", "user", "--jq", ".login"],
                              capture_output=True, text=True, timeout=30).stdout.strip() or "x"
    except Exception:
        return "x"


def token():
    """A Glaze developer's credential, used to BUILD the bundle. The customer
    who receives it needs none - that is the whole point."""
    t = os.environ.get("GITHUB_TOKEN")
    if t:
        return t
    try:
        t = subprocess.run(["gh", "auth", "token"], capture_output=True, text=True,
                           timeout=30).stdout.strip()
    except Exception:
        t = ""
    if not t:
        die("no credential for the private feed. Set GITHUB_TOKEN or run 'gh auth login'.\n"
            "       This is needed to BUILD a bundle, not to use one.")
    return t


def lock_pin(lock, name):
    """The version pinned under <name> in upstream.lock."""
    if not lock.is_file():
        die("no upstream.lock at %s - cannot tell which packages to vendor" % lock)
    for line in lock.read_text().splitlines():
        parts = line.split()
        if len(parts) >= 2 and parts[0] == name:
            return parts[1]
    die("upstream.lock has no %r pin. Add one:\n       %-15s <version>" % (name, name))


def sdk_version(lock):
    """The ProviderSdk version a customer's .csproj gets a PackageReference to."""
    return lock_pin(pathlib.Path(lock), ROOTS[SDK])


def root_versions(lock):
    """The three roots and their pinned versions.

    ProviderSdk's own dependencies are NOT here: they are declared in its
    .nuspec and the closure walk picks them up."""
    lock = pathlib.Path(lock)
    return {pkg: lock_pin(lock, key) for pkg, key in ROOTS.items()}


def fetch(pkg, ver, out, tok, who):
    lower = pkg.lower()
    dest = out / ("%s.%s.nupkg" % (lower, ver))
    if dest.is_file():
        return dest
    url = "%s/download/%s/%s/%s.%s.nupkg" % (FEED, lower, ver, lower, ver)
    req = urllib.request.Request(url)
    import base64
    req.add_header("Authorization", "Basic " + base64.b64encode(
        ("%s:%s" % (who, tok)).encode()).decode())
    opener = urllib.request.build_opener(StripAuthOnRedirect)
    try:
        with opener.open(req, timeout=120) as r:
            dest.write_bytes(r.read())
    except urllib.error.HTTPError as e:
        die("cannot fetch %s %s from the private feed: HTTP %s" % (pkg, ver, e.code))
    except Exception as e:
        die("cannot fetch %s %s: %s" % (pkg, ver, e))
    return dest


def nuspec_deps(nupkg):
    """BeaconTower dependencies declared inside a .nupkg, as (id, version).

    Matched textually rather than parsed: a nuspec is untrusted input as far as
    this process is concerned, and an XML parser would carry entity-expansion
    risk for no benefit here."""
    with zipfile.ZipFile(nupkg) as z:
        spec = [n for n in z.namelist() if n.endswith(".nuspec")]
        if not spec:
            return []
        blob = z.read(spec[0]).decode("utf-8", "replace")
    return [(i, v.strip("[]()")) for i, v in
            re.findall(r'<dependency\s+id="(BeaconTower\.[^"]+)"\s+version="([^"]+)"', blob)]


NUGET_CONFIG = """<?xml version="1.0" encoding="utf-8"?>
<!-- Written by 'btk3s bundle'.

     BeaconTower.* resolves from BOTH the vendored folder and the private feed.
     A customer has the folder and no credential: the folder satisfies every
     package and the private source is never contacted. A Glaze developer with
     a credential falls through to the feed for anything not vendored.

     Verified behaviour: complete folder + no credential restores clean; an
     incomplete folder + no credential fails NU1301 naming the exact package.
-->
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
    <add key="btk3s-bundle" value="./packages" />
    <add key="beacontower_github" value="https://nuget.pkg.github.com/beacontower/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
    <packageSource key="btk3s-bundle">
      <package pattern="BeaconTower.*" />
    </packageSource>
    <packageSource key="beacontower_github">
      <package pattern="BeaconTower.*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
"""


def vendor(outdir, lock=None):
    outdir = pathlib.Path(outdir)
    if lock is None:
        lock = pathlib.Path(__file__).resolve().parent.parent / "upstream.lock"
    pkgdir = outdir / "packages"
    pkgdir.mkdir(parents=True, exist_ok=True)

    versions = root_versions(lock)
    tok, who = token(), account()

    seen, queue, got = {}, [], []
    queue = sorted(versions.items())

    while queue:
        pkg, ver = queue.pop(0)
        if seen.get(pkg) == ver:
            continue
        if pkg in seen and seen[pkg] != ver:
            die("version conflict for %s: %s and %s. The bundle cannot carry both."
                % (pkg, seen[pkg], ver))
        seen[pkg] = ver
        path = fetch(pkg, ver, pkgdir, tok, who)
        got.append((pkg, ver, path.stat().st_size))
        for dep, dver in nuspec_deps(path):
            if dep not in seen:
                queue.append((dep, dver))

    (outdir / "nuget.config").write_text(NUGET_CONFIG)

    total = sum(s for _, _, s in got)
    for pkg, ver, size in sorted(got):
        print("    %-32s %-10s %6.1f KB" % (pkg, ver, size / 1024.0))
    print("    %d package(s), %.0f KB. ProviderSdk %s; every version from "
          "upstream.lock" % (len(got), total / 1024.0, versions[SDK]))
    return 0


def main():
    if len(sys.argv) < 3:
        die("usage: nuget.py vendor <out-dir> [upstream.lock] | verify <out-dir>")
    if sys.argv[1] == "vendor":
        return vendor(sys.argv[2], sys.argv[3] if len(sys.argv) > 3 else None)
    if sys.argv[1] == "verify":
        d = pathlib.Path(sys.argv[2]) / "packages"
        n = sorted(d.glob("*.nupkg")) if d.is_dir() else []
        for f in n:
            print("    %s" % f.name)
        print("    %d package(s)" % len(n))
        return 0 if n else 1
    die("unknown subcommand %r" % sys.argv[1])


if __name__ == "__main__":
    sys.exit(main())
