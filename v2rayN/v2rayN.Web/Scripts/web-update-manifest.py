#!/usr/bin/env python3
"""Build or verify the v2rayN Web `web-update.json` release manifest.

The manifest is published next to the official v2rayN Release assets. Every URL points at the
official release tag in the repository that produced the assets, so the Web self-update never
depends on a fork-specific release channel.

Usage:
  web-update-manifest.py write --version 7.25.3 --repository 2dust/v2rayN --commit <sha> \
      --build-date 2026-09-28T00:00:00Z --dist dist --output dist/web-update.json
  web-update-manifest.py verify --manifest dist/web-update.json --dist dist [--repository 2dust/v2rayN]
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
from pathlib import Path

PRODUCT = "v2rayN.Web"
RID_ARCH = {"linux-x64": "64", "linux-arm64": "arm64"}
VERSION_RE = re.compile(r"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$")
REPOSITORY_RE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9-]*/[A-Za-z0-9_.-]+$")
OFFICIAL_URL_RE = re.compile(
    r"^https://github\.com/[A-Za-z0-9][A-Za-z0-9-]*/[A-Za-z0-9_.-]+/releases/download/(?P<tag>[^/]+)/(?P<asset>[^/]+)$"
)


def fail(message: str) -> None:
    print(f"web-update manifest error: {message}", file=sys.stderr)
    raise SystemExit(1)


def full_asset_name(rid: str) -> str:
    return f"v2rayN.Web-linux-{RID_ARCH[rid]}.tar.gz"


def app_asset_name(rid: str) -> str:
    return f"v2rayN.Web-app-linux-{RID_ARCH[rid]}.tar.gz"


def asset_url(repository: str, version: str, asset: str) -> str:
    return f"https://github.com/{repository}/releases/download/{version}/{asset}"


def require_release_files(dist: Path) -> None:
    for rid in RID_ARCH:
        for asset in (full_asset_name(rid), app_asset_name(rid)):
            path = dist / asset
            if not path.is_file() or path.stat().st_size == 0:
                fail(f"release asset is missing or empty: {path}")


def package_entry(dist: Path, repository: str, version: str, rid: str) -> dict:
    path = dist / app_asset_name(rid)
    data = path.read_bytes()
    return {
        "rid": rid,
        "asset": path.name,
        "url": asset_url(repository, version, path.name),
        "sha256": hashlib.sha256(data).hexdigest(),
        "size": len(data),
    }


def build_manifest(dist: Path, version: str, repository: str, commit: str, build_date: str) -> dict:
    if not VERSION_RE.match(version):
        fail(f"official release versions must be x.y.z, got: {version}")
    if not REPOSITORY_RE.match(repository):
        fail(f"invalid release repository: {repository}")
    if not commit or commit == "unknown":
        fail("the manifest requires a real commit identity")
    if not build_date or build_date == "unknown":
        fail("the manifest requires a real build date")
    require_release_files(dist)
    return {
        "product": PRODUCT,
        "version": version,
        "commit": commit,
        "buildDate": build_date,
        "packages": [package_entry(dist, repository, version, rid) for rid in RID_ARCH],
    }


def verify_manifest(manifest_path: Path, dist: Path, repository: str | None, version: str | None = None) -> None:
    try:
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        fail(f"could not read {manifest_path}: {exc}")
    if not isinstance(manifest, dict) or manifest.get("product") != PRODUCT:
        fail(f"{manifest_path} is not a {PRODUCT} manifest")
    manifest_version = manifest.get("version")
    if not isinstance(manifest_version, str) or not VERSION_RE.match(manifest_version):
        fail(f"{manifest_path} has an invalid version: {manifest_version!r}")
    if version is not None and manifest_version != version:
        fail(f"{manifest_path} version {manifest_version} does not match {version}")
    if not isinstance(manifest.get("commit"), str) or not manifest["commit"]:
        fail(f"{manifest_path} has no commit identity")
    if not isinstance(manifest.get("buildDate"), str) or not manifest["buildDate"]:
        fail(f"{manifest_path} has no build date")

    packages = manifest.get("packages")
    if not isinstance(packages, list) or len(packages) != len(RID_ARCH):
        fail(f"{manifest_path} must contain exactly {len(RID_ARCH)} packages")
    by_rid = {}
    for package in packages:
        if not isinstance(package, dict):
            fail(f"{manifest_path} contains a non-object package entry")
        rid = package.get("rid")
        if rid not in RID_ARCH:
            fail(f"{manifest_path} contains an unsupported runtime identifier: {rid!r}")
        if rid in by_rid:
            fail(f"{manifest_path} contains duplicate runtime identifiers")
        asset = package.get("asset")
        if asset != app_asset_name(rid):
            fail(f"{manifest_path} has an unexpected asset for {rid}: {asset!r}")
        path = dist / asset
        if not path.is_file() or path.stat().st_size == 0:
            fail(f"release asset is missing or empty: {path}")
        data = path.read_bytes()
        if package.get("sha256") != hashlib.sha256(data).hexdigest():
            fail(f"sha256 mismatch for {asset}")
        if package.get("size") != len(data):
            fail(f"size mismatch for {asset}")
        url = package.get("url")
        if not isinstance(url, str):
            fail(f"missing asset URL for {rid}")
        match = OFFICIAL_URL_RE.match(url)
        if not match or match.group("tag") != manifest_version or match.group("asset") != asset:
            fail(f"asset URL is not an official release URL for this tag: {url}")
        if repository is not None and not url.startswith(f"https://github.com/{repository}/"):
            fail(f"asset URL does not use repository {repository}: {url}")
        by_rid[rid] = package

    for rid in RID_ARCH:
        if rid not in by_rid:
            fail(f"{manifest_path} is missing runtime {rid}")
    require_release_files(dist)


def write_manifest(args: argparse.Namespace) -> None:
    dist = Path(args.dist)
    manifest = build_manifest(dist, args.version, args.repository, args.commit, args.build_date)
    output = Path(args.output)
    output.write_text(json.dumps(manifest, separators=(",", ":")) + "\n", encoding="utf-8")
    verify_manifest(output, dist, args.repository, args.version)
    print(f"Wrote {output} for {PRODUCT} {args.version} ({args.repository})")


def verify_command(args: argparse.Namespace) -> None:
    verify_manifest(Path(args.manifest), Path(args.dist), args.repository)
    print(f"Verified {args.manifest}")


def main(argv: list[str]) -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    subparsers = parser.add_subparsers(dest="command", required=True)

    write_parser = subparsers.add_parser("write", help="write web-update.json from the packaged assets")
    write_parser.add_argument("--version", required=True)
    write_parser.add_argument("--repository", required=True)
    write_parser.add_argument("--commit", required=True)
    write_parser.add_argument("--build-date", required=True)
    write_parser.add_argument("--dist", required=True)
    write_parser.add_argument("--output", required=True)
    write_parser.set_defaults(func=write_manifest)

    verify_parser = subparsers.add_parser("verify", help="verify web-update.json against the packaged assets")
    verify_parser.add_argument("--manifest", required=True)
    verify_parser.add_argument("--dist", required=True)
    verify_parser.add_argument("--repository")
    verify_parser.set_defaults(func=verify_command)

    args = parser.parse_args(argv)
    args.func(args)


if __name__ == "__main__":
    main(sys.argv[1:])
