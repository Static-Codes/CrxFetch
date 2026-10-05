# CrxFetch

A pure C# library mainly designed to download Chrome Web Store extensions as raw `.crx` packages. No external chrome dependency, no developer mode, and no third-party services. Every package is signature-verified and its extension id is re-derived from the embedded public key, so a substituted payload is rejected rather than returned.



## Quick start

```csharp
using CrxFetch;

// One-shot: parse an id or store url, download, verify.
var result = await CrxFetch.CrxDownloader.FetchAsync(
    "https://chromewebstore.google.com/detail/ublock-origin/cjpalhdlnbpafiamejdnhcphjbkeiagm"
);

Console.WriteLine(result.Info.ExtensionId);      // cjpalhdlnbpafiamejdnhcphjbkeiagm
Console.WriteLine(result.Info.Format);           // Crx3
Console.WriteLine(result.Info.SignatureVerified);// True
await File.WriteAllBytesAsync("ublock.crx", result.Package);
```

Via a proxy, and extracting the archive:

```csharp
var options = new CrxDownloadOptions { Proxy = "socks5://127.0.0.1:1080" };

using var downloader = new CrxDownloader(options);
var result = await downloader.DownloadAsync("aapbdbdomjkkjkaonfhkkikfgjllcleb");

CrxArchive.ExtractToDirectory(result.Package, result.Info, "./extracted");
```

Inspecting a `.crx` already on disk:

```csharp
var info = CrxFile.Inspect(await File.ReadAllBytesAsync("ublock.crx"));

Console.WriteLine(info.ExtensionId);       // id re-derived from the public key
Console.WriteLine(info.SignatureDetail);   // "developer key ok, 1/2 other proofs valid"
Console.WriteLine(info.ZipOffset);         // where the ZIP starts inside the container
```

## How retrieval works

`ChromeUpdateService` talks to Google's Update Protocol endpoint,
`https://clients2.google.com/service/update2/crx` — the same one Chromium's own component
updater uses. It tries four client identities in order and accepts either of the two shapes
the service answers in:

| shape | request | result |
| --- | --- | --- |
| redirect | `response=redirect` | package url in the `Location` header |
| update_2 | `response=update_2` | `gupdate` XML whose `updatecheck` carries a `codebase` attribute |

Two behaviours are worth knowing, both measured rather than assumed:

- **`response=redirect` is largely retired.** Across a 2053-proxy sweep, 457 reached Google and
  *every one* answered 204 No Content. The `update_2` shape still serves packages. The library
  tries both, so it does not depend on the redirect path.
- **204 is a refusal, not a malformed request.** The body is empty and no `Location` header is
  sent. The `update_2` XML says the same thing more precisely, via an `_esbAllowlist` attribute
  on the app and `status="noupdate"` on the update check:

  | Extension | `_esbAllowlist` | `updatecheck` |
  | --- | --- | --- |
  | Google Translate (`aapbdbdomjj…`, published 2010) | `true` | `codebase="https://clients2.googleusercontent.com/crx/blobs/…"` |
  | uBlock Origin (`cjpalhdlnbp…`) | `false` | no codebase |

  So the deciding factor is Google's extension allowlist, **not** how recently an extension
  shipped — an earlier guess along those lines was wrong. Extensions outside the allowlist get
  no package from any egress, so no proxy setting can produce one; they have to come from
  elsewhere. Changing egress via `--proxy` / `CrxDownloadOptions.Proxy` still matters for
  reaching the service at all, since datacentre and VPN ranges are frequently unreachable.

## CRX3 signatures

The signed payload is **not** the archive alone. Per Chromium's `crx3.proto` and
`crx_verifier.cc` it is:

```
"CRX3 SignedData\0" | uint32le(len(signed_header_data)) | signed_header_data | archive
```

Most reimplementations get this wrong, and a tool that signs or verifies only the archive will
reject every genuine package. `CrxFile` implements the real scheme and is tested against
Chromium's own `valid_publisher.crx3` test data.

Verification mirrors Chromium's trust model:

- the `crx_id` comes from the signed header, so it is covered by the signature;
- the developer-key proof is the one whose public key derives that `crx_id`, and it must verify;
- a CRX3 without a matching developer proof is rejected.

CRX2 is parsed only far enough to locate the archive. Chromium rejects CRX2 outright
(`version != 3` fails immediately), so there is no signature check to make and none is claimed.

## API

| Type | Purpose |
| --- | --- |
| `CrxDownloader` | Retrieve, download and validate. `FetchAsync` for one-shot use. |
| `CrxLink` | Recognises urls that host a package, direct or via a redirect. |
| `CrxDownloader.DownloadFromLinkAsync` | Fetches a hosted package, following redirects explicitly. |
| `CrxDownloader.FetchLinkAsync` | One-shot for a link, an id, or a store URL. |
| `CrxDownloadOptions` | Proxy, Chrome version, timeouts, signature requirement. |
| `CrxDownloadResult` | Package bytes, `CrxInfo`, package uri, and every request attempted. |
| `CrxArchive` | Extracts the ZIP embedded in a CRX. |
| `CrxFile` | Parses a CRX, verifies proofs, derives the extension id. |
| `CrxDownloader.Validate` | Validates bytes already in hand. |
| `ChromeUpdateService` | GUP resolution, without downloading. |
| `GupResult` / `GupAttempt` | Resolution outcome and per-request detail. |
| `ExtensionId` | Pulls a 32-char id out of an id or store url. |
| `ProxyEndpoint` | Parses proxy endpoints. |

### Proxies

`ProxyEndpoint` accepts `http`, `https`, `socks4`, `socks4a` and `socks5`. A bare `host:port`
is read as `http`. A null proxy leaves the system proxy configuration in effect.

```csharp
ProxyEndpoint.TryParse("1.2.3.4:8080", out var proxy);
ProxyEndpoint.Parse("socks4://1.2.3.4:1080");
```

### Errors

| Exception | `CrxFailureReason` | Meaning |
| --- | --- | --- |
| `CrxTransportException` | `Transport` | Service or CDN unreachable. |
| `CrxNoPackageException` | `NoPackage` | Reached, but no package offered. Carries `Attempts` and `DeclinedWithNoContent`. |
| `CrxValidationException` | `Validation` | Not a CRX, wrong id, or signature failed. |

All derive from `CrxException`.

### Hosted links

A package url can be fetched directly, skipping the update service entirely. Both shapes work: a
url that serves the bytes, and one that answers with a redirect. Redirects are followed explicitly
and bounded to 10 hops, so the final url is known and reported.

```csharp
// Direct CDN link, or a redirect such as the update service's own response=redirect endpoint.
var result = await CrxFetch.CrxDownloader.FetchLinkAsync(
  "https://clients2.googleusercontent.com/crx/blobs/AZPVhcSooyIJq6Qj....crx"
);

// PackageUri is where the bytes finally came from, after following redirects.
Console.WriteLine(result.PackageUri);
Console.WriteLine(result.Info.SignatureVerified);
```

This is the escape hatch for extensions the update service refuses. Extensions outside Google's
allowlist never get a `codebase` from GUP, but if you can obtain a package url another way —
the store's own redirect endpoint, a mirror, an internal artifact — it will be fetched and
verified.

When the link embeds an id it is used to pin identity, so a link serving the wrong package is
rejected rather than written out. Pass `expectedExtensionId: null` to skip that check.

```csharp
// Reach for the network directly, no update service involved.
await CrxFetch.CrxDownloader.FetchLinkAsync(url, new CrxDownloadOptions { Proxy = proxy });

// Or decide explicitly.
using var downloader = new CrxDownloader();
await downloader.DownloadFromLinkAsync(new Uri(url));
```

## Command line

`CrxFetch.Cli` wraps the library in two commands:

```
crxfetch fetch <extension-id|store-url|extension-link> [options]
crxfetch inspect <file.crx>
```

| Command | Purpose |
| --- | --- |
| `fetch` | Download and verify a package. |
| `inspect` | Verify a `.crx` already on disk. Exits 0 when the signature verifies. |

`fetch` options: `-o|--out <file>`, `--proxy <host:port>`, `--chrome-version <ver>`, `--zip`,
`--no-verify`, `-v|--verbose`. `inspect` takes no options.

The fetch target is routed automatically, by asking whether the URL is on the Chrome Web Store:

| Target | Route |
| --- | --- |
| bare 32-char id | update service |
| `chromewebstore.google.com` / `chrome.google.com` URL | update service |
| any other `http(s)` URL | fetched as-is, following redirects |

So a raw `.crx` link from GitHub, a mirror or Google's own CDN resolves straight to a package
download and never touches the update service. `extension-link` is accepted as an alias for
`fetch`.

The command name is optional: `crxfetch <id>` still means `crxfetch fetch <id>`, and a leading
token that is not a command name is treated as the target.

### Exit codes

  - `0`: Success,
  - `64`: Usage,
  - `65`: Bad Payload,
  - `69`: No package located or a network transport failure occurred.

## Tests

`CrxFetch.Tests` (namespace `CrxFetch.Tests`) runs three layers:

- **Offline** — id parsing, CRX2/CRX3 parsing and signature verification against Chromium's
  own `valid_publisher.crx3` test data, validation outcomes, and proxy endpoint parsing for all
  five schemes.
- **Proxy plumbing** — a local CONNECT proxy proves the request is tunnelled rather than
  silently going direct, and a dead-port endpoint per scheme proves every scheme is dialled.
- **End-to-end** (`Category=E2E`) — retrieve, download and inspect against the live service,
  with and without a proxy, across `http`, `https`, `socks4` and `socks5`.

```bash
dotnet test  # Shorthand for `dotnet test --filter 'Category!=E2E'`

CRXFETCH_TEST_PROXIES="socks5://host:port,socks4://host:port,http://host:port" \
CRXFETCH_TEST_PROXY_FILE=/path/to/proxies.txt \
  dotnet test --filter Category=E2E                      # E2E testing across every scheme
```

`CRXFETCH_TEST_PROXIES` takes a list separated by commas, semicolons or whitespace;
`CRXFETCH_TEST_PROXY_FILE` reads one endpoint per line; `CRXFETCH_TEST_PROXY` takes a single
endpoint. E2E tests skip themselves when a scheme has no endpoint, and skip rather than fail
when a public proxy is dead, so the suite is green on an offline machine and does not blame
the library for a flaky third party.