# CrxFetch

A pure C# library mainly designed to download Chrome Web Store extensions as raw `.crx` packages. No external chrome dependency, no developer mode, and no third-party services. Every package is signature-verified and its extension id is re-derived from the embedded public key, so a substituted payload is rejected rather than returned.



## Quick start

### Downloading without extracting the archive
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

### Downloading with a proxy and extracting the archive:

```csharp
var options = new CrxDownloadOptions { Proxy = "socks5://127.0.0.1:1080" };

using var downloader = new CrxDownloader(options);
var result = await downloader.DownloadAsync("aapbdbdomjkkjkaonfhkkikfgjllcleb");

CrxArchive.ExtractToDirectory(result.Package, result.Info, "./extracted");
```

Inspecting an existing `.crx` on your machine:

```csharp
var info = CrxFile.Inspect(await File.ReadAllBytesAsync("path/to/extension.crx"));

Console.WriteLine(info.ExtensionId);       // The Id is re-derived from the extension's public key.
Console.WriteLine(info.SignatureDetail);   // A description of the signature, i.e, "developer key ok, 1/2 other proofs valid"
Console.WriteLine(info.ZipOffset);         // The location of the starting byte for the ZIP the container
```

## Developer Notes

### General 
`ChromeUpdateService` talks to Google's Update Protocol endpoint, `https://clients2.google.com/service/update2/crx`: the same one Chromium's own component
updater uses. It tries four client identities in order and accepts either of the two shapes the service answers in:

| shape | request | result |
| --- | --- | --- |
| `redirect` (Largely deprecated) | `response=redirect` | A package Uri in the `Location` header |
| `update_2` (Still serving) | `response=update_2` | A `gupdate` XML containing `updatecheck` which carries a `codebase` attribute |

Future maintenance notes.

- **`response=redirect` is largely retired.** Across a 2053-proxy sweep, 457 reached Google and
  *every one* answered 204 No Content. The `update_2` shape still serves packages. The library
  tries both, so it does not depend on the redirect path.
- **204 is a refusal, not a malformed request.** The body is empty and no `Location` header is
  sent. The `update_2` XML says the same thing more precisely, via an `_esbAllowlist` attribute
  on the app and `status="noupdate"` on the update check:

  | Extension | `_esbAllowlist` | `updatecheck` |
  | --- | --- | --- |
  | Google Translate (`aapbdbdomjj...`, published 2010) | `true` | `codebase="https://clients2.googleusercontent.com/crx/blobs/…"` |
  | uBlock Origin (`cjpalhdlnbp...`) | `false` | no codebase url |

## CRX3 signatures

Per Chromium's [`crx3.proto`](https://chromium.googlesource.com/chromium/src/+/HEAD/components/crx_file/crx3.proto) and [`crx_verifier.cc`](https://chromium.googlesource.com/chromium/src/+/HEAD/components/crx_file/crx_verifier.cc): ```"CRX3 SignedData\0" | uint32le(len(signed_header_data)) | signed_header_data | archive```

`CrxFile` implements the real scheme and is tested against Chromium's own [`crx_file.h`](https://chromium.googlesource.com/chromium/src/+/HEAD/components/crx_file/crx_file.h) test data.

This verification process mirrors the official Chromium's trust model:

##### Chromium's CRX Trust Model
- The `crx_id` comes from the signed header, therefore, it is covered by the signature.

- The public key from the developer key proof is used to derive it's `crx_id`.

- A CRX3 that doesn't derive a matching developer proof is rejected.

- A CRX2 is only parsed enough to locate the archive.
    - Chromium rejects CRX2 outright with a check on `version != 3`, so there is no signature check performed.

## API

| Type | Purpose |
| --- | --- |
| `CrxDownloader` | Retrieves, downloads and validates. `FetchAsync` for one-shot use. |
| `CrxLink` | Recognizes urls that host a package, direct or via a redirect. |
| `CrxDownloader.DownloadFromLinkAsync` | Fetches a hosted package, following redirects explicitly. |
| `CrxDownloader.FetchLinkAsync` | One-shot for a link, an id, or a store URL. |
| `CrxDownloadOptions` | Proxy, Chrome version, timeouts, signature requirement. |
| `CrxDownloadResult` | Package bytes, `CrxInfo`, package Uri, and every request attempted. |
| `CrxArchive` | Extracts the ZIP embedded in a CRX archive. |
| `CrxFile` | Parses a .CRX file, verifies proofs, then derives it's extension id. |
| `CrxDownloader.Validate` | Validates bytes already on disk. |
| `ChromeUpdateService` | GUP resolution, without downloading. |
| `GupResult`/`GupAttempt` | Resolution outcome and per-request detail. |
| `ExtensionId` | Pulls a 32-char extension id out of an id string or chrome web-store url. |
| `ProxyEndpoint` | Parses proxy endpoints. |

### Proxies

`ProxyEndpoint` accepts `http`, `https`, `socks4`, `socks4a` and `socks5`. A bare `host:port` is read as `http`. A null proxy leaves the system proxy configuration in effect.

```csharp
ProxyEndpoint.TryParse("1.2.3.4:8080", out var proxy);
ProxyEndpoint.Parse("socks4://1.2.3.4:1080");
```

### Errors

| Exception | `CrxFailureReason` | Meaning |
| --- | --- | --- |
| `CrxTransportException` | `Transport` | Service or CDN unreachable. |
| `CrxNoPackageException` | `NoPackage` | Reached, but no package offered. Carries `Attempts` and `DeclinedWithNoContent`. |
| `CrxValidationException` | `Validation` | Not a CRX, the Id is wrong, or signature failed. |

All derive from `CrxException`.

### Hosted links

A package url can be fetched directly, skipping the update service entirely. 
Both shapes work: a url that serves the bytes, and one that answers with a redirect. 
Redirects are followed explicitly and limited to 10, therefore, the final url is known and reported.

```csharp
// Direct CDN link, or a redirect such as the update service's own response=redirect endpoint.
var result = await CrxFetch.CrxDownloader.FetchLinkAsync(
  "https://clients2.googleusercontent.com/crx/blobs/AZPVhcSooyIJq6Qj....crx"
);

// PackageUri is where the bytes finally came from, after following redirects.
Console.WriteLine(result.PackageUri);
Console.WriteLine(result.Info.SignatureVerified);
```

Extensions outside Google's allowlist never get a `codebase` from GUP. 

That said, there are two ways you can obtain a package URL, both of which will be fetched and verified:
1. The chrome store's own redirect endpoint
2. A trusted mirror or other hosted artifact

When the link embeds an id it is used to pin identity, so a link serving the wrong package is
rejected rather than written out. Pass `expectedExtensionId: null` to skip that check.

```csharp
// Reach for the network directly, no update service involved.
await CrxFetch.CrxDownloader.FetchLinkAsync(url, new CrxDownloadOptions { Proxy = proxy });

// Deciding explicitly.
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
| `inspect` | Verify a `.crx` already on disk. Exits with 0 when the signature is verified. |

`fetch` options: `-o|--out <file>`, `--proxy <host:port>`, `--chrome-version <ver>`, `--zip`,
`--no-verify`, `-v|--verbose`. `inspect` takes no options.

The fetch target is routed automatically; It is determined by  the Uri schema

| Target | Route |
| --- | --- |
| 32 Char Extension ID | UpdateService |
| A chrome webstore URL (`chromewebstore.google.com`/`chrome.google.com`) URL | UpdateService |
| Any other `http`/`http(s)` URi | Fetched directly, following any redirects. |

A raw `.crx` link from GitHub, a mirror or Google's own CDN is downloaded directly, never touching the update service. 

Note: `extension-link` is accepted as an alias for `fetch`.

The command name is optional: `crxfetch <id>` still resolves to `crxfetch fetch <id>`, and a leading token that is not a command name is treated as the target.

### Exit Codes

  - `0`: Success,
  - `64`: Usage Error,
  - `65`: Bad Payload/Rejected Request,
  - `69`: No package located or a network transport failure occurred.

## Tests

`CrxFetch.Tests` runs three layers:

- **Offline**: id parsing, CRX2/CRX3 parsing and signature verification against Chromium's own `valid_publisher.crx3` test data, validation outcomes, and proxy endpoint parsing for all five schemes.
- **Proxy plumbing**: a local CONNECT proxy proves the request is tunneled rather than silently going direct, and a dead-port endpoint per scheme proves every scheme is dialed.
- **End-to-end** (`Category=E2E`): retrieve, download and inspect against the live service, with and without a proxy, across `http`, `https`, `socks4` and `socks5`.

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
