# MinIO -> SeaweedFS, and a server move by backup/restore - Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the archived MinIO container with SeaweedFS (issue #769), and move production to a new server
by taking a platform backup on the old (MinIO) stack and restoring it into the new (SeaweedFS) stack, with no
bucket-level copying.

**Architecture:** Diariz already talks plain S3 (AWSSDK.S3 in the API, boto3 in the worker), and the platform
backup (`GET /api/maintenance/backup`) already carries the Postgres dump **plus every object in the
`recordings` bucket** in one zip, read through `IAudioStorage` - so it is store-agnostic by construction. A
backup taken from MinIO restores into SeaweedFS through the same `IAudioStorage.UploadAsync`. The code work is
therefore: swap the container (compose + Testcontainers), prove the S3 contract and the backup/restore path
against real SeaweedFS, make restore report what it restored, and write the cutover runbook. The server move
itself is operational (Part B).

**Tech Stack:** SeaweedFS `chrislusf/seaweedfs:4.48` (Apache-2.0, `weed server -s3`), .NET 10 / AWSSDK.S3 v4,
Testcontainers 4.x generic `ContainerBuilder`, boto3, Docker Compose (inline `configs:` needs Compose
v2.23.1+).

**Spec:** issue #769 (scope 1-5) and `docs/Research/minio-archival-and-seaweedfs.md` (risks in section 3).

## Global Constraints

- Every fix starts as an issue: **#769 already exists - reuse it**, PR body carries `Fixes #769` on its own line.
- One PR, one release: Build bump `0.273.0 -> 0.273.1` (check `version.json` on `main` first and bump from
  whatever it is), in `version.json` + all seven mirrors (three lock files carry it twice each), plus a
  `RECENT[0]` entry in `apps/web/src/lib/releaseNotes/current.ts`.
- TDD: no production code without a failing test first. Integration tests need Docker.
- No em/en dashes in user-facing strings (release notes, i18n). Plain `-`.
- No production data anywhere in the repo, issues or PR: report counts and sizes only.
- **Where this runs:** the plan is meant to be executed on a machine with **no existing Diariz stack**, so a
  local `docker compose up` in `deploy/` is a safe end-to-end smoke there. Before running `up` or `down`,
  check: if `docker volume ls` shows any `diariz_*` volume, or `docker compose ls` shows a `diariz` project,
  stop. That machine has a live stack (the original dev box *is* prod), so use the dev server instead.
- Never `git add -A`; stage explicit paths.
- Pin the SeaweedFS image to an exact tag (`4.48`), never `latest`, in compose **and** the test fixture.
- Keep host port `9002` for the S3 API, bound to `127.0.0.1` by default.
- Backup `CurrentFormat` stays `1`: no migration in this PR, and the archive layout does not change.

## Decisions made in this plan (flag to the user if they disagree)

1. **Service name `s3`, in-network endpoint `http://s3:8333`.** Store-neutral, so the next swap is config only.
2. **Credentials via an inline compose `configs:` block (`s3.json`)**, interpolated from `.env`. It keeps the
   0.273.0 hardening: a root identity plus a **scoped app identity** limited to `recordings` (and, in the
   observability overlay, a scoped GlitchTip identity limited to `glitchtip`). The MinIO `mc admin`
   provisioning scripts go away; a key generator script replaces them.
3. **Env vars renamed `MINIO_*` -> `S3_*`, old names kept as fallbacks**, so an existing `.env` still resolves.
4. **GlitchTip starts fresh on the new server.** Its `glitchtip` bucket and its own Postgres are not in the
   Diariz backup. They hold error telemetry only. Moving them would mean the manual bucket copy you said you
   don't want.
5. **The `apikeys` volume (Data Protection keyring + OpenIddict signing keys) is copied to the new server.** It
   is a few KB, deliberately excluded from the backup zip. Without it, every user's stored LLM API key becomes
   undecryptable and every MCP/OAuth connector must re-authorise. It is optional, but recommended.

---

## File map

| File | Change |
|---|---|
| `tests/Diariz.Api.IntegrationTests/Infrastructure/ContainersFixture.cs` | MinIO module -> generic SeaweedFS container; `Minio*` props -> `S3*` |
| `tests/Diariz.Api.IntegrationTests/Infrastructure/SeaweedFsS3Config.cs` | **new** - the fixture's `s3.json` |
| `tests/Diariz.Api.IntegrationTests/Diariz.Api.IntegrationTests.csproj` | drop `Testcontainers.Minio` |
| `tests/Diariz.Api.IntegrationTests/{AudioStorage,AudioRetention,LiveChunkFlow}IntegrationTests.cs`, `Infrastructure/DiarizWebAppFactory.cs` | rename property uses |
| `tests/Diariz.Api.IntegrationTests/BackupRestoreS3IntegrationTests.cs` | **new** - backup -> restore round trip on real SeaweedFS |
| `tests/Diariz.Api.Tests/MaintenanceControllerTests.cs` | restore reports counts |
| `src/Diariz.Api/Controllers/MaintenanceController.cs` | restore returns `objectsRestored` / `bytesRestored` |
| `apps/web/src/lib/types.ts` (`RestoreResult`, line ~776) | add the two fields |
| `src/Diariz.Api/Services/AudioStorage.cs`, `Program.cs`, `src/Diariz.Worker/storage.py` | comments only ("MinIO" -> "the S3 store") |
| `deploy/docker-compose.yml`, `deploy/docker-compose.rocm.yml`, `deploy/docker-compose.observability.yml` | `minio` -> `s3` (SeaweedFS) |
| `deploy/.env.example` | `S3_*` vars |
| `deploy/new-s3-keys.sh`, `deploy/NewS3Keys.cmd` | **new** - print hex key pairs |
| `deploy/{provision-diariz-minio.sh,ProvisionDiarizMinio.cmd,provision-glitchtip-minio.sh,ProvisionGlitchTipMinio.cmd}`, `deploy/diariz-minio/`, `deploy/glitchtip-minio/` | **delete** |
| `deploy/BringUpProd.cmd`, `deploy/BringUpWebApi.cmd`, `deploy/new-glitchtip-secrets.sh`, `deploy/NewGlitchTipSecrets.cmd` | update MinIO references |
| `.github/workflows/ci.yml`, `coverage.yml` | update any MinIO image pre-pull/cache step |
| `docs/Server_Migration_Runbook.md` | **new** - Part B, as a checked-in runbook |
| `CLAUDE.md`, `README.md`, `docs/Overall_Synopsis_of_Platform.md`, `docs/Data_Schema.md`, `docs/GlitchTip_Deployment.md`, `apps/web/src/components/AboutModal.tsx`, `docs/Runtime_Architecture.archify.json` (+ re-render) | docs |
| `version.json` + 7 mirrors, `apps/web/src/lib/releaseNotes/current.ts` | release |

---

# Part A - the code change (one PR, closes #769)

### Task 1: Run the integration suite against SeaweedFS

This is the gate the research doc calls for. It goes first because it is the cheapest way to find out whether
SeaweedFS honours the S3 surface we use (AWS SDK v4 flexible checksums, SigV4 payload signing over HTTP,
presigned GET over HTTP, byte ranges, ListObjectsV2 pagination). The "failing test" here is the whole existing
suite, which fails to start on a machine without a cached MinIO image.

**Files:**
- Create: `tests/Diariz.Api.IntegrationTests/Infrastructure/SeaweedFsS3Config.cs`
- Modify: `tests/Diariz.Api.IntegrationTests/Infrastructure/ContainersFixture.cs`
- Modify: `tests/Diariz.Api.IntegrationTests/Diariz.Api.IntegrationTests.csproj` (remove the `Testcontainers.Minio` line and its comment's "MinIO")
- Modify: every `fx.Minio*` use (18 sites: `grep -rn "Minio" tests --include=*.cs`)

**Interfaces:**
- Produces: `ContainersFixture.S3Endpoint` (string, `http://host:port`), `S3AccessKey`, `S3SecretKey` (the root
  identity). Tasks 3 uses these.

- [ ] **Step 1: Confirm the container contract by hand (5 min, records two facts the code depends on)**

```bash
docker run -d --name sw-spike -p 18333:8333 chrislusf/seaweedfs:4.48 server -dir=/data -ip.bind=0.0.0.0 -s3 -s3.port=8333 -volume.max=0 -master.volumeSizeLimitMB=64
```
```bash
curl -s -o /dev/null -w "%{http_code}\n" http://localhost:18333/healthz
```
```bash
docker exec sw-spike sh -c "wget -q -O /dev/null http://127.0.0.1:8333/healthz && echo ok"
```
```bash
docker rm -f sw-spike
```
Expected: `200` and `ok` (an alpine/busybox `wget` exists in the image). **If `/healthz` is not 200**, use the
master's `http://127.0.0.1:9333/cluster/healthz` everywhere this plan says `8333/healthz` (fixture wait,
compose healthcheck), and write that down in the fixture comment.

- [ ] **Step 2: Write the fixture's identity file**

`SeaweedFsS3Config.cs`:
```csharp
namespace Diariz.Api.IntegrationTests.Infrastructure;

/// <summary>The <c>s3.json</c> the SeaweedFS test container starts with. One root identity, mirroring the
/// compose stack's root (the compose stack also has a scoped app identity; tests create a bucket per test,
/// so they need bucket-creation rights on arbitrary names and run as root).</summary>
internal static class SeaweedFsS3Config
{
    public const string AccessKey = "diariz-test-root";
    public const string SecretKey = "diariz-test-root-secret-0123456789abcdef";

    public static string Json => $$"""
        {"identities":[{"name":"root","credentials":[{"accessKey":"{{AccessKey}}","secretKey":"{{SecretKey}}"}],
          "actions":["Admin","Read","Write","List","Tagging"]}]}
        """;
}
```

- [ ] **Step 3: Swap the container in `ContainersFixture`**

Replace `using Testcontainers.Minio;` with `using DotNet.Testcontainers.Builders; using DotNet.Testcontainers.Containers; using System.Text;`, and the MinIO field and properties with:

```csharp
    // SeaweedFS replaced MinIO (issue #769): MinIO's community edition is archived and its images can no
    // longer be pulled. Pinned to the tag deploy/docker-compose.yml uses. Testcontainers has no SeaweedFS
    // module, so this is a generic container with its own readiness check.
    //
    // -volume.max=0 sizes the volume count from free disk. SeaweedFS makes each bucket a collection with its
    // own volumes, and the default cap (8) runs out after a couple of buckets - the suite creates one bucket
    // per test. -master.volumeSizeLimitMB keeps each volume small for the same reason.
    private const int S3Port = 8333;
    private readonly IContainer _s3 = new ContainerBuilder("chrislusf/seaweedfs:4.48")
        .WithResourceMapping(Encoding.UTF8.GetBytes(SeaweedFsS3Config.Json), "/etc/seaweedfs/s3.json")
        .WithCommand("server", "-dir=/data", "-ip.bind=0.0.0.0", "-s3", $"-s3.port={S3Port}",
            "-s3.config=/etc/seaweedfs/s3.json", "-volume.max=0", "-master.volumeSizeLimitMB=64")
        .WithPortBinding(S3Port, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer()
            .UntilHttpRequestIsSucceeded(r => r.ForPort(S3Port).ForPath("/healthz")))
        .Build();

    public string S3Endpoint => $"http://{_s3.Hostname}:{_s3.GetMappedPublicPort(S3Port)}";
    public string S3AccessKey => SeaweedFsS3Config.AccessKey;
    public string S3SecretKey => SeaweedFsS3Config.SecretKey;
```
Update `InitializeAsync`/`DisposeAsync` to start/dispose `_s3`, and the class summary ("Postgres, Redis and
SeaweedFS (S3)"). If the Testcontainers version in use rejects `new ContainerBuilder(image)`, use
`new ContainerBuilder().WithImage(...)` with `#pragma warning disable CS0618` and a one-line reason.

- [ ] **Step 4: Rename every use**

`fx.MinioEndpoint -> fx.S3Endpoint`, `fx.MinioAccessKey -> fx.S3AccessKey`, `fx.MinioSecretKey -> fx.S3SecretKey`
in all files from the grep. Update comments that say "against the MinIO container" to "against the S3
container".

- [ ] **Step 5: Build the whole solution (integration compile breaks hide from unit runs)**

Run: `dotnet build Diariz.slnx`
Expected: 0 errors, 0 warnings.

- [ ] **Step 6: Run the S3 contract tests, then the whole integration suite**

Run: `dotnet test tests/Diariz.Api.IntegrationTests --filter "FullyQualifiedName~AudioStorage" > %TEMP%/s3-int.txt` and read the file (never grep a failing run away).
Expected: all pass. Then run `dotnet test tests/Diariz.Api.IntegrationTests > %TEMP%/int-all.txt`.
Expected: same pass count as `main` (record both numbers in the PR).

**If something fails, it is a SeaweedFS compatibility finding, not a test to adjust.** The likely candidates
and their fixes, in order:
- `PutObject` 400/403 with checksum or `x-amz-content-sha256` complaints -> set
  `RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED` and
  `ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED` on `AmazonS3Config` in **both**
  `Program.cs` and the test helpers that mirror it. Add a test-first assertion in `AudioStorageIntegrationTests`
  that a 6 MB upload round-trips before changing `Program.cs`.
- "no writable volumes" / 500 on the Nth bucket -> raise `-volume.max`/lower `-master.volumeSizeLimitMB`.
- Anything else: stop and report to the user with the full error; do not paper over it.

- [ ] **Step 7: Commit**

```bash
git add tests/Diariz.Api.IntegrationTests
git commit -m "test: run the integration suite against SeaweedFS instead of MinIO (#769)"
```

---

### Task 2: Restore reports what it restored

On cutover night, the operator has to compare the restore against the backup. Today `Restore` returns only
`restored: true`. Two counts make that comparison one line of output.

**Files:**
- Test: `tests/Diariz.Api.Tests/MaintenanceControllerTests.cs`
- Modify: `src/Diariz.Api/Controllers/MaintenanceController.cs` (`Restore`, the `foreach (var entry ...)` loop and the `Ok(new { ... })`)
- Modify: `apps/web/src/lib/types.ts:776` (`RestoreResult`)

**Interfaces:**
- Produces: restore response JSON gains `objectsRestored: number` and `bytesRestored: number`. Part B reads them.

- [ ] **Step 1: Write the failing test** (append to `MaintenanceControllerTests`)

```csharp
    [Fact]
    public async Task Restore_ReportsHowManyObjectsAndBytesItRestored()
    {
        // The operator compares these against the archive listing at cutover. A wrong count must be visible
        // in the response, not discovered later as a missing recording.
        var archive = BuildArchive(Migration, "DUMP", new()
        {
            ["u1/a.webm"] = "12345",      // 5 bytes
            ["u1/sub/b.pdf"] = "1234567", // 7 bytes
        });

        var result = await BuildForRestore(new FakeAudioStorage(), new FakeDatabaseBackup(), archive).Restore();

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = JsonSerializer.SerializeToElement(ok.Value, JsonOpts);
        Assert.Equal(2, body.GetProperty("objectsRestored").GetInt32());
        Assert.Equal(12, body.GetProperty("bytesRestored").GetInt64());
    }
```

- [ ] **Step 2: Run it to see it fail**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~Restore_ReportsHowMany"`
Expected: FAIL - `KeyNotFoundException` for `objectsRestored`.

- [ ] **Step 3: Implement**

In `Restore`, before the `foreach (var entry in zip.Entries)` loop:
```csharp
            int objectsRestored = 0;
            long bytesRestored = 0;
```
After `await _storage.UploadAsync(key, read, ContentTypeForKey(key), ct);`:
```csharp
                    objectsRestored++;
                    bytesRestored += read.Length;
```
And add to the anonymous response object:
```csharp
                objectsRestored,
                bytesRestored,
```
In the web `RestoreResult` type add `objectsRestored: number; bytesRestored: number;`. (The admin UI does not
need to show them. This is just the type, so it stays accurate.)

- [ ] **Step 4: Run it to see it pass, then the class**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~MaintenanceController"`
Expected: all PASS. Mutation check: change `objectsRestored++` to `objectsRestored += 2`, re-run, and confirm the new test fails. Then revert by editing (not restoring a backup file, because the mtime would make MSBuild skip the rebuild).

- [ ] **Step 5: Commit**

```bash
git add tests/Diariz.Api.Tests/MaintenanceControllerTests.cs src/Diariz.Api/Controllers/MaintenanceController.cs apps/web/src/lib/types.ts
git commit -m "feat(api): restore reports objects and bytes restored (#769)"
```

---

### Task 3: Backup -> restore round trip on real SeaweedFS

This proves the actual migration path. The unit tests use `FakeAudioStorage`, and the S3 contract tests never
run the controller. This test runs `MaintenanceController.Backup` against one real bucket and `Restore` into
another, using the real S3 store and nested keys. One object is larger than the SDK's default part and buffer
sizes.

**Files:**
- Create: `tests/Diariz.Api.IntegrationTests/BackupRestoreS3IntegrationTests.cs`

**Interfaces:**
- Consumes: `fx.S3Endpoint/S3AccessKey/S3SecretKey` (Task 1); `objectsRestored`/`bytesRestored` (Task 2);
  `FakeDatabaseBackup`, `FakeSchemaVersion`, `Http.Context` from `Diariz.Api.TestSupport`.

- [ ] **Step 1: Write the test**

```csharp
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using Diariz.Api.Configuration;
using Diariz.Api.Controllers;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Diariz.Api.IntegrationTests;

/// <summary>The server move restores a backup taken from one object store into another. This runs the real
/// controller over the real S3 store in both directions, so a listing, streaming or upload incompatibility
/// shows up here and not at cutover.</summary>
[Collection(IntegrationCollection.Name)]
public class BackupRestoreS3IntegrationTests(ContainersFixture fx)
{
    private const string Migration = "20260615111923_InitialCreate";

    private AudioStorage Storage(string bucket)
    {
        var opts = new StorageOptions
        {
            Endpoint = fx.S3Endpoint, AccessKey = fx.S3AccessKey, SecretKey = fx.S3SecretKey,
            Bucket = bucket, ForcePathStyle = true,
        };
        var s3 = new AmazonS3Client(new BasicAWSCredentials(opts.AccessKey, opts.SecretKey),
            new AmazonS3Config { ServiceURL = opts.Endpoint, ForcePathStyle = true, AuthenticationRegion = "us-east-1" });
        return new AudioStorage(s3, Options.Create(opts));
    }

    private static MaintenanceController Controller(IAudioStorage storage) =>
        new(storage, new FakeDatabaseBackup { DumpBytes = "DUMP"u8.ToArray() },
            new FakeSchemaVersion(Migration), new BackupProgress())
        { ControllerContext = Http.Context(Guid.NewGuid(), [Roles.PlatformAdministrator]) };

    [Fact]
    public async Task BackupFromOneBucket_RestoresEveryObjectByteForByte_IntoAnother()
    {
        var source = Storage($"src-{Guid.NewGuid():N}");
        var target = Storage($"dst-{Guid.NewGuid():N}");
        await source.EnsureBucketAsync();
        await target.EnsureBucketAsync();

        var big = new byte[12 * 1024 * 1024];
        new Random(42).NextBytes(big);
        var objects = new Dictionary<string, byte[]>
        {
            [$"{Guid.NewGuid()}/rec.webm"] = big,
            [$"{Guid.NewGuid()}/attachments/notes.pdf"] = "PDF"u8.ToArray(),
            [$"{Guid.NewGuid()}/deep/a/b/c.wav"] = "WAV"u8.ToArray(),
        };
        foreach (var (key, bytes) in objects)
            using (var ms = new MemoryStream(bytes)) await source.UploadAsync(key, ms, "application/octet-stream");

        // Backup from the source bucket.
        var file = Assert.IsType<FileStreamResult>(await Controller(source).Backup());
        using var zipCopy = new MemoryStream();
        await using (file.FileStream) await file.FileStream.CopyToAsync(zipCopy);

        // Restore into the target bucket, through the raw-body path the admin UI and curl both use.
        var restoring = Controller(target);
        zipCopy.Position = 0;
        restoring.ControllerContext.HttpContext.Request.Body = zipCopy;
        var ok = Assert.IsType<OkObjectResult>(await restoring.Restore());

        var body = JsonSerializer.SerializeToElement(ok.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(objects.Count, body.GetProperty("objectsRestored").GetInt32());
        Assert.Equal(objects.Values.Sum(b => (long)b.Length), body.GetProperty("bytesRestored").GetInt64());

        var restoredKeys = new List<string>();
        await foreach (var k in target.ListKeysAsync()) restoredKeys.Add(k);
        Assert.Equal(objects.Keys.Order(), restoredKeys.Order());
        foreach (var (key, bytes) in objects)
        {
            await using var read = await target.OpenReadAsync(key);
            using var got = new MemoryStream();
            await read.CopyToAsync(got);
            Assert.Equal(bytes, got.ToArray());
        }
    }
}
```
If `Http.Context` does not give a settable `Request.Body`, check `MaintenanceControllerTests.BuildForRestore` and
follow how it supplies the archive.

- [ ] **Step 2: Run it**

Run: `dotnet test tests/Diariz.Api.IntegrationTests --filter "FullyQualifiedName~BackupRestoreS3" > %TEMP%/br.txt`
Expected: PASS. This is a characterisation test of a path that should already work, so prove it can fail.
Temporarily skip the `UploadAsync` call in `Restore`, confirm the test fails on `objectsRestored`/the key list,
then restore the call by editing it back.

- [ ] **Step 3: Commit**

```bash
git add tests/Diariz.Api.IntegrationTests/BackupRestoreS3IntegrationTests.cs
git commit -m "test: backup from one S3 bucket restores byte-for-byte into another on SeaweedFS (#769)"
```

---

### Task 4: Compose - replace `minio` with a SeaweedFS `s3` service

There is no unit test for compose YAML. Text-matching tests on config prove nothing. The verification is
`docker compose config` resolving cleanly here, and then the dev-server smoke in Task 6.

**Files:**
- Modify: `deploy/docker-compose.yml`, `deploy/docker-compose.rocm.yml`, `deploy/docker-compose.observability.yml`, `deploy/.env.example`
- Create: `deploy/new-s3-keys.sh`, `deploy/NewS3Keys.cmd`
- Delete: `deploy/provision-diariz-minio.sh`, `deploy/ProvisionDiarizMinio.cmd`, `deploy/provision-glitchtip-minio.sh`, `deploy/ProvisionGlitchTipMinio.cmd`, `deploy/diariz-minio/`, `deploy/glitchtip-minio/`
- Modify: `deploy/BringUpProd.cmd`, `deploy/BringUpWebApi.cmd`, `deploy/new-glitchtip-secrets.sh`, `deploy/NewGlitchTipSecrets.cmd`, `.github/workflows/ci.yml`, `.github/workflows/coverage.yml` (any `minio` reference: `grep -n -i minio <file>`)

- [ ] **Step 1: Replace the service in `docker-compose.yml`**

```yaml
  s3:
    # SeaweedFS S3 gateway (issue #769). Replaced MinIO, whose community edition is archived and whose images
    # can no longer be pulled. Pinned: bump deliberately, after the integration suite passes on the new tag
    # (tests/Diariz.Api.IntegrationTests/Infrastructure/ContainersFixture.cs pins the same tag).
    image: chrislusf/seaweedfs:4.48
    restart: unless-stopped
    # One process runs master + volume + filer + S3. -volume.max=0 sizes volumes from free disk (the default
    # cap of 8 runs out: each bucket is its own collection). Only the S3 port is used by anything.
    command: >
      server -dir=/data -ip.bind=0.0.0.0
      -s3 -s3.port=8333 -s3.config=/etc/seaweedfs/s3.json
      -volume.max=0 -master.volumeSizeLimitMB=1024
    configs:
      - source: s3_identities
        target: /etc/seaweedfs/s3.json
    ports:
      # Host-only by default (a published port bypasses the host firewall); set S3_BIND=0.0.0.0 to reach it
      # from another machine. Host 9002 kept from the MinIO days so existing tooling still works.
      - "${S3_BIND:-${MINIO_BIND:-127.0.0.1}}:9002:8333"
    volumes:
      - s3data:/data
    healthcheck:
      test: ["CMD", "wget", "-q", "-O", "/dev/null", "http://127.0.0.1:8333/healthz"]
      interval: 5s
      timeout: 3s
      retries: 20
```
Top level, next to `secrets:`:
```yaml
configs:
  # SeaweedFS identities. Inline so Compose interpolates the keys from deploy/.env; the file exists only
  # inside the s3 container. Root administers the store; the app identity is confined to `recordings`
  # (Admin:recordings is what lets the API create its own bucket on first boot). The observability overlay
  # REPLACES this whole config to add a GlitchTip identity - keep the two in step.
  s3_identities:
    content: |
      {"identities":[
        {"name":"root","credentials":[{"accessKey":"${S3_ROOT_ACCESS_KEY:-${MINIO_ROOT_USER:?required - set S3_ROOT_ACCESS_KEY in deploy/.env}}","secretKey":"${S3_ROOT_SECRET_KEY:-${MINIO_ROOT_PASSWORD:?required - set S3_ROOT_SECRET_KEY in deploy/.env}}"}],
         "actions":["Admin","Read","Write","List","Tagging"]},
        {"name":"diariz","credentials":[{"accessKey":"${S3_APP_ACCESS_KEY:-${MINIO_APP_ACCESS_KEY:?required - run deploy/new-s3-keys.sh}}","secretKey":"${S3_APP_SECRET_KEY:-${MINIO_APP_SECRET_KEY:?required - run deploy/new-s3-keys.sh}}"}],
         "actions":["Admin:recordings","Read:recordings","Write:recordings","List:recordings","Tagging:recordings"]}
      ]}
```
In `volumes:` replace `miniodata:` with `s3data:`. **Do not delete `miniodata` from the old server.** A new
volume name means the new stack can never touch MinIO's data.

- [ ] **Step 2: Point every consumer at it**

In `api`, `worker`, and `live-worker`: `depends_on: minio` -> `s3`; `Storage__Endpoint`/`S3_ENDPOINT` ->
`"http://s3:8333"`; access/secret keys -> `${S3_APP_ACCESS_KEY:-${MINIO_APP_ACCESS_KEY:?...}}` /
`${S3_APP_SECRET_KEY:-${MINIO_APP_SECRET_KEY:?...}}` (no fallback to root any more: the scoped identity is now
mandatory, and the generator makes it a one-liner). Fix the comments that say "MinIO". Apply the same edits to
`docker-compose.rocm.yml`.

- [ ] **Step 3: Observability overlay**

In `docker-compose.observability.yml`: `AWS_S3_ENDPOINT_URL: "http://s3:8333"`, keys
`${GLITCHTIP_S3_ACCESS_KEY:-${GLITCHTIP_MINIO_ACCESS_KEY:?required - run deploy/new-s3-keys.sh glitchtip}}`
(and secret), any `depends_on: minio` -> `s3`, and a top-level `configs: s3_identities:` that repeats the two
identities above **plus**:
```json
        {"name":"glitchtip","credentials":[{"accessKey":"${GLITCHTIP_S3_ACCESS_KEY:-${GLITCHTIP_MINIO_ACCESS_KEY}}","secretKey":"${GLITCHTIP_S3_SECRET_KEY:-${GLITCHTIP_MINIO_SECRET_KEY}}"}],
         "actions":["Admin:glitchtip","Read:glitchtip","Write:glitchtip","List:glitchtip","Tagging:glitchtip"]}
```
GlitchTip creates its bucket itself only if it is configured to. If it is not, the runbook (Part B) creates it
with the root key: `aws --endpoint-url http://127.0.0.1:9002 s3 mb s3://glitchtip`.

- [ ] **Step 4: Key generator, replacing the provision scripts**

`deploy/new-s3-keys.sh`:
```bash
#!/usr/bin/env bash
# Print a fresh S3 key pair for deploy/.env. SeaweedFS reads identities from the s3_identities config in
# docker-compose.yml, so there is nothing to provision inside the store - paste the lines, then
#   docker compose up -d
# Usage: ./new-s3-keys.sh            -> S3_APP_* (the API and worker)
#        ./new-s3-keys.sh root       -> S3_ROOT_*
#        ./new-s3-keys.sh glitchtip  -> GLITCHTIP_S3_*
# Hex only: a $ or # in .env would be interpolated or start a comment.
set -euo pipefail
case "${1:-app}" in
  app) P=S3_APP ;; root) P=S3_ROOT ;; glitchtip) P=GLITCHTIP_S3 ;;
  *) echo "Usage: $(basename "$0") [app|root|glitchtip]" >&2; exit 2 ;;
esac
echo "${P}_ACCESS_KEY=$(openssl rand -hex 10)"
echo "${P}_SECRET_KEY=$(openssl rand -hex 24)"
```
`deploy/NewS3Keys.cmd` must do the same on Windows. Look at how `NewGlitchTipSecrets.cmd` generates hex
(it already does this), and copy that approach. Delete the four MinIO provision scripts and their two
directories. In `.env.example`, replace the MinIO block with `S3_BIND`, `S3_ROOT_*`, `S3_APP_*` (empty, plus a
note pointing to `new-s3-keys.sh`), and `GLITCHTIP_S3_*`. Add one line saying that the old `MINIO_*` names
still work as fallbacks.

- [ ] **Step 5: Verify the compose files resolve**

```bash
cd deploy && S3_ROOT_ACCESS_KEY=a S3_ROOT_SECRET_KEY=b S3_APP_ACCESS_KEY=c S3_APP_SECRET_KEY=d docker compose --env-file .env.example config > "$TEMP/compose.txt"
```
Expected: exit 0, and `compose.txt` contains `http://s3:8333` and **no** `minio` (`grep -ci minio` -> 0). Repeat
with `-f docker-compose.yml -f docker-compose.observability.yml` and with `docker-compose.rocm.yml`. Then repeat
the first command with **only** the old `MINIO_*` names set, to prove the fallbacks resolve. (`up` comes in Task 6,
after the "no existing stack" check in Global Constraints.)

- [ ] **Step 6: Commit**

```bash
git add deploy/docker-compose.yml deploy/docker-compose.rocm.yml deploy/docker-compose.observability.yml deploy/.env.example deploy/new-s3-keys.sh deploy/NewS3Keys.cmd deploy/BringUpProd.cmd deploy/BringUpWebApi.cmd deploy/new-glitchtip-secrets.sh deploy/NewGlitchTipSecrets.cmd .github/workflows/ci.yml .github/workflows/coverage.yml
git rm -r deploy/provision-diariz-minio.sh deploy/ProvisionDiarizMinio.cmd deploy/provision-glitchtip-minio.sh deploy/ProvisionGlitchTipMinio.cmd deploy/diariz-minio deploy/glitchtip-minio
git commit -m "feat(deploy): replace MinIO with SeaweedFS as the S3 store (#769)"
```

---

### Task 5: Docs, runbook and release

**Files:** as listed in the file map ("docs" and "release" rows), plus `docs/Server_Migration_Runbook.md`.

- [ ] **Step 1: Write `docs/Server_Migration_Runbook.md`**: Part B of this plan, reworded as a standalone
  runbook (commands and checks only, no plan scaffolding). This becomes the document you work from on cutover
  night.
- [ ] **Step 2: CLAUDE.md**: the Ports bullet ("MinIO S3 API `9002->9000`" -> "S3 API (SeaweedFS) `9002->8333`";
  drop "MinIO console 9001"; `minio:9000` -> `s3:8333`), the "MinIO/S3 quirk" bullet (keep the payload-signing note,
  which is SDK behaviour, not MinIO's, and record anything Task 1 found), the integration-tests paragraph
  ("Postgres/pgvector, Redis, and SeaweedFS"), the Full stack section (`.env` var names).
- [ ] **Step 3: `docs/Overall_Synopsis_of_Platform.md`**: external dependency, deployment, the S3 identities, and
  the backup/restore section (state that the backup is store-agnostic and is the supported way to move servers;
  the keyring is not in it).
- [ ] **Step 4: `docs/Data_Schema.md`**: the bucket/key layout section, which names the store. Key layout is
  unchanged. The volume is now `s3data`. Add no migration-history row, because there is no migration.
- [ ] **Step 5: README**: architecture row and flow line ("MinIO" -> "SeaweedFS (S3)"), the quick-start list, and the
  licence note at line ~213 (SeaweedFS is Apache-2.0, so the AGPL caveat goes). Leave the Features table and
  `docs/features.md` alone unless they name MinIO (`grep -n -i minio docs/features.md`). Leave `CAPABILITIES`
  alone, because the scope is unchanged.
- [ ] **Step 6: `AboutModal.tsx`**: change `MinIO/S3` to `SeaweedFS (S3)` in the disclaimers. Run `npm test` in
  `apps/web` (the no-fancy-dashes test covers this file).
- [ ] **Step 7: `docs/GlitchTip_Deployment.md`**: the `GLITCHTIP_S3_*` keys and the generator.
- [ ] **Step 8: Architecture diagram**: a datastore changed, so this is a topology change. Edit the MinIO
  component in `docs/Runtime_Architecture.archify.json` (name, cited paths: the deleted provision scripts must
  not be cited), re-pin `meta.repository.revision` to the branch HEAD **after** the previous commits, and
  re-render with the command in CLAUDE.md (set `ARCHIFY_CHROME`). Never hand-edit the HTML.
- [ ] **Step 9: Comments in code**: `AudioStorage.cs` class summary and presign comment, the `Program.cs` "Storage
  (MinIO / S3)" header, `storage.py` docstring. Comment-only, so run `dotnet build Diariz.slnx` and
  `python -m pytest` in `src/Diariz.Worker` afterwards anyway (edits after the last test run are unverified).
- [ ] **Step 10: Release**: bump `version.json` and the seven mirrors by text replace, and add `RECENT[0]`:

```ts
  {
    version: "0.273.1",
    date: "2026-10-01",
    pr: <PR number - confirm with `gh pr list --state all --limit 1` + 1, then fix after `gh pr create` if wrong>,
    headline: "Object storage moves from MinIO to SeaweedFS",
    summary:
      "MinIO's free edition is no longer maintained and its images can no longer be downloaded, so a fresh install could not start. Audio and attachments are now stored in SeaweedFS, an actively maintained S3-compatible store. Existing servers move their data with a platform backup and restore - see docs/Server_Migration_Runbook.md. A restore now reports how many files and bytes it put back.",
    changed: [
      "The object store is SeaweedFS instead of MinIO. Storage credentials are now set with S3_* settings; the old MINIO_* names still work.",
    ],
    fixed: ["Fresh installs failed because the MinIO image could not be downloaded (#769)."],
  },
```
- [ ] **Step 11: Full verification, then commit**

Run: `dotnet build Diariz.slnx`, `dotnet test > %TEMP%/all.txt` (unit + integration), `cd apps/web && npm test && npm run build`,
`cd src/Diariz.Worker && python -m pytest`. Expected: all green, no warnings. The OpenAPI snapshot is unaffected
(admin routes are excluded). Stage the files explicitly and commit:
`docs: SeaweedFS across the docs, server migration runbook, 0.273.1 (#769)`.

---

### Task 6: Dev-server smoke, then PR

- [ ] **Step 1: Bring a stack up.** **Preferred:** use the executing machine, provided it has no existing Diariz
  stack (see Global Constraints). `cp deploy/.env.example deploy/.env`, fill in the required secrets and the
  `S3_*` keys from `new-s3-keys.sh`, then `cd deploy && docker compose up -d --build`. **Alternative:** the
  dev server (`dev.diariz.stocks-hayward.com`), after adding `S3_ROOT_*`/`S3_APP_*` to its `.env`. It starts
  with an **empty** SeaweedFS because its MinIO volume is left alone. Bring its data across with the Part B
  backup/restore, which also rehearses the restore. On a fresh local stack, rehearse the same way: take a
  backup after the Step 2 checks, then restore it with the B2.3 `curl` commands.
- [ ] **Step 2: End-to-end checks** (issue #769 scope 3): `docker compose ps` shows `s3` healthy. Upload a short
  recording, then confirm that it transcribes (the worker reads the S3 store through boto3), plays, and can
  be seeked (ranged GETs). Make a clip or screenshot (presigned GET through ffmpeg). Delete a recording and
  check that its object is gone. Run a live session if live transcription is enabled (merge jobs make the
  worker upload multipart). Take an admin backup, then restore it.
- [ ] **Step 3: Scoped key proof:**
  `AWS_ACCESS_KEY_ID=<app> AWS_SECRET_ACCESS_KEY=<app> aws --endpoint-url http://127.0.0.1:9002 s3 ls s3://recordings`
  succeeds; the same against `s3://glitchtip` (or any other bucket) is **denied**.
- [ ] **Step 4:** `gh pr create`. The body must include: `Fixes #769` on its own line, the integration pass
  count before and after, the deployment surface (**server redeploy only, no desktop release**, plus a warning
  that redeploying an existing server **starts with an empty object store**. Use the runbook, and do not
  simply `git pull && up`), and the release checklist items touched. Bind the PR with `ccd_pr` and watch CI.

> **Important for the existing prod:** after this merges, do **not** redeploy the old server from `main`. It
> would come up on an empty SeaweedFS while its DB still points at blobs in MinIO, so every recording would
> lose its audio. The old server stays on its current build until it is retired.

---

# Part B - the server move (operational, after Part A is merged)

The old server keeps running MinIO, untouched, the whole time. The new server is built from `main` (SeaweedFS)
and filled from a backup. The same backup can be restored any number of times (restore wipes and replaces), so
you rehearse first and then do the real cutover.

### B0. Sizing and prerequisites

- **Disk on the new server: at least 2.5x the backup zip** (about 8.5 GB today, from about 6.6 GB of recordings
  plus the dump). Restore writes the whole zip to the API container's `/tmp`, then fills SeaweedFS. Each object
  also spills to a temp file before upload.
- **Disk on the old server: about 1x the zip free** in Docker's data root. Backup builds the zip in the API
  container's `/tmp` before streaming it.
- New server: Docker + Compose v2.23.1+ (inline `configs:`), NVIDIA Container Toolkit, the repo at the merged
  `main`, outbound access to Hugging Face. The worker re-downloads about 4 GB of models into `workercache` on
  first job.
- The new server runs the **same or a newer** app version than the old one. Restore refuses a backup from a
  newer schema. An older one is rolled forward automatically.

### B1. Build the new server

1. `.env`: start from `deploy/.env.example`. **Carry over from the old `.env`** (copy the values server-to-server
   over SSH, never through chat or the repo): `JWT_KEY`, `CALLBACK_SECRET`, `POSTGRES_PASSWORD`, `REDIS_PASSWORD`,
   `APP_PUBLIC_URL` (same public origin, so desktop apps, Google OAuth redirect URIs, webhooks and the claude.ai MCP
   connector all keep working), `HF_TOKEN`, `Seed__*`, Google/Microsoft OAuth, SMTP, LLM endpoints. **Generate
   new**: `S3_ROOT_*`, `S3_APP_*` (`deploy/new-s3-keys.sh`, `... root`).
2. Copy the keyring volume (recommended, Decision 5). On the old server:
   ```bash
   docker run --rm -v diariz_apikeys:/keys -v "$PWD":/out alpine tar czf /out/apikeys.tgz -C /keys .
   ```
   Then copy `apikeys.tgz` across with `scp`, and on the new server **before the first `up`**:
   ```bash
   docker volume create diariz_apikeys && docker run --rm -v diariz_apikeys:/keys -v "$PWD":/in alpine tar xzf /in/apikeys.tgz -C /keys
   ```
   Treat `apikeys.tgz` as a secret, and delete both copies afterwards.
3. `cd deploy && docker compose up -d --build`. Wait for `docker compose ps` to show everything healthy. The API
   creates the empty `recordings` bucket and seeds the admin from `Seed__*`.

### B2. Rehearsal restore

1. Old server, quiet moment: Admin -> Maintenance -> **Download backup** (or `curl` it on the old box; see B3.2).
   Note its object count without unpacking it:
   ```bash
   unzip -l diariz-backup-*.zip | grep -c " objects/"
   ```
2. Copy it to the new server: `rsync -P` resumes if the link drops.
3. Restore **on the new server, directly against the API on 127.0.0.1:8080**. This bypasses nginx and any outer
   proxy and its body or timeout limits, and the browser:
   ```bash
   TOKEN=$(curl -s -X POST http://127.0.0.1:8080/api/auth/login -H 'Content-Type: application/json' \
     -d '{"email":"<seed admin email>","password":"<seed admin password>"}' | jq -r .accessToken)
   curl -sS -X POST -T diariz-backup-XXXX.zip -H "Authorization: Bearer $TOKEN" \
     -H 'Content-Type: application/zip' http://127.0.0.1:8080/api/maintenance/restore
   ```
   Use `-T`, **not** `--data-binary @file`, because `--data-binary` loads the whole file into memory. Expect
   `{"restored":true,...,"objectsRestored":N,"bytesRestored":B}`, where **N equals the `unzip -l` count**. If
   `restartRecommended` is true: `docker compose restart api worker`.
4. Verify by count: the number of recordings, users and people in the admin pages matches the old server.
   Pick a few recordings across a range of ages, and confirm that each one plays, seeks, and shows its
   transcript, summary and speakers. Re-transcribe one recording (worker <-> SeaweedFS). Upload a new one.
   Search and chat return results (embeddings are in the dump). If the keyring was copied, confirm that a
   user's own LLM key still works.
5. Write down how long the backup, the transfer and the restore took. That is the length of the cutover window.

### B3. Cutover

1. Tell users about the window (B2.5 timing). Ask them to stop recording and to close desktop apps. Desktop
   apps reconnect afterwards, because the address is the same.
2. Old server: take the **final backup**, and note its timestamp `T`.
3. New server: run the B2.3 restore again. It wipes the rehearsal data. Check that `objectsRestored` matches the
   count from `unzip -l`.
4. Repeat the B2.4 spot checks.
5. Switch traffic: point DNS or the outer reverse proxy at the new server. The outer proxy must forward `/mcp`
   with buffering off, and allow long timeouts on `/api/maintenance/`.
6. Catch stragglers. On the **old** server, count anything written after `T`:
   ```bash
   docker compose exec postgres psql -U diariz -d diariz -c "select count(*) from \"Recordings\" where \"CreatedAt\" > '<T>';"
   ```
   (Check the actual table and column names in `docs/Data_Schema.md` first.) If the count is not zero,
   download those recordings' audio from the old UI and upload them to the new one.

### B4. Afterwards

- Leave the old stack running (as you planned) until the new one has been in use for a while. Then
  `docker compose down` **without `-v`**, and keep the `miniodata` and `pgdata` volumes until you are sure.
- GlitchTip (Decision 4): bring the overlay up fresh on the new server. If its bucket isn't created
  automatically, create it with `aws --endpoint-url http://127.0.0.1:9002 s3 mb s3://glitchtip` using the
  root key. Error history starts again.
- Delete every copy of the backup zip and `apikeys.tgz` that you no longer need. They contain everyone's
  recordings.

---

## Self-review notes

- Issue #769 scope 1 is Task 4. Scope 2 is Task 1. Scope 3 is covered by Tasks 1 and 3 plus the Task 6 smoke.
  Scope 4 is Part B, which uses the backup instead of the issue's `rclone`, at your request. That the backup is
  store-agnostic is proven by Task 3. Scope 5 is Task 5.
- The user asked to restore a backup into the new container: Tasks 2 and 3 plus Part B.
- Known unverified facts, each checked before anything depends on it: the `/healthz` path (Task 1 Step 1);
  SeaweedFS and AWS SDK v4 checksum compatibility (Task 1 Step 6, with a named fix); whether a scoped
  `Admin:recordings` identity can create its own bucket on first boot (Task 6 Step 1: if the API fails at
  `EnsureBucketAsync`, create the bucket once with the root key and record that in the runbook).
