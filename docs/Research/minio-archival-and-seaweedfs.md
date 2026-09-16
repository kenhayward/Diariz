# MinIO's archival and a move to SeaweedFS - assessment

> Research only, 2026-09-16. No code changed. Written after Docker Hub stopped serving `minio/minio`
> (issue #765, fixed in 0.272.0 by pinning to quay.io). Claims about Diariz carry a file reference so they
> can be re-checked as the code moves; claims about upstream projects carry a date, because they will age.

## The short answer

**MinIO's open-source edition is finished, but Diariz is not in trouble.** Nothing is broken today and the
exposure is modest. **SeaweedFS is a sound replacement** and, because Diariz uses a very small slice of
the S3 API, the move is a bounded infrastructure change rather than a rewrite. Recommended timing: do the
cheap hardening now, and migrate deliberately within the next few months.

## 1. MinIO's development status

Confirmed against the GitHub API on 2026-09-16: `minio/minio` is **archived** (read-only), last push
2026-04-24, licence AGPL-3.0.

| Date | Event |
|---|---|
| May 2025 | Web admin console removed from the Community Edition (kept in the paid AIStor product) |
| Oct 2025 | Official community Docker images and binaries stop being published |
| 2025-12-03 | README declares maintenance mode: no features, security fixes only "as appropriate" |
| 2026-02-12 | README changes to "no longer maintained" |
| 2026-04-25 | Repository archived on GitHub |

Consequences:

- **No upstream fixes of any kind, including security.** Third parties sell extended support (e.g. TuxCare)
  and there are community forks, but neither is MinIO itself.
- **Image availability is not guaranteed.** Docker Hub began refusing anonymous pulls of `minio/minio`
  (registry returns 401), which failed all 502 integration tests at fixture start (#765). The fix pinned
  `quay.io/minio/minio:RELEASE.2025-09-07T16-13-09Z`, whose digest (`sha256:14cea493...`) is identical to
  the image production was already running - but quay.io is under no obligation to keep hosting it.

## 2. What it means for Diariz

- **Exposure is limited but grows with time.** MinIO sits behind the API on the compose network. The S3 port
  is, however, published on the host (`deploy/docker-compose.yml`, `"9002:9000"`, bound to all interfaces),
  and the root credentials default to `minioadmin` if `MINIO_ROOT_USER`/`MINIO_ROOT_PASSWORD` are unset.
  In-container services use `minio:9000`, so nothing inside the stack needs that host port.
- **Two consumers, not one.** GlitchTip also stores its data in this MinIO. As of 2026-09-16 production
  holds about 6.6 GB under `recordings` and 1.8 GB under `glitchtip` - small enough that data transfer is a
  non-issue.
- **The S3 surface Diariz depends on is small:**
  - API (`src/Diariz.Api/Services/AudioStorage.cs`, client built in `src/Diariz.Api/Program.cs`):
    `PutBucket`, `PutObject`, `GetObject` (including `ByteRange`, used for audio seeking), `DeleteObject`,
    `ListObjectsV2`, and presigned GET URLs. `ForcePathStyle`, region `us-east-1`.
  - Worker (`src/Diariz.Worker/storage.py`): boto3 `download_file` and `upload_file` (the latter switches
    to multipart automatically for large files).
  - Not used: lifecycle rules, bucket policies, versioning, event notifications, replication.

## 3. SeaweedFS as the replacement

Status on 2026-09-16: not archived, Apache-2.0, pushed to that day, releases roughly weekly (4.45, 4.46,
4.47 between 2026-08-31 and 2026-09-14).

**For:**

- Healthy, permissively licensed and mature.
- Its S3 gateway covers well beyond what Diariz uses: presigned URLs, range requests, multipart uploads,
  versioning, object lock, lifecycle, tagging, CORS, conditional requests and checksums.
- Can run as a single process (`weed server -s3`), which suits a single-host compose stack.
- Data migration is mechanical: `rclone` between two S3 endpoints.
- **An acceptance test already exists.** `tests/Diariz.Api.IntegrationTests` exercises the real S3
  contract Diariz relies on (`AudioStorageIntegrationTests`: upload/read round trip, byte ranges, presigned
  reads, presigned range requests, missing key, idempotent bucket creation). Passing that suite against
  SeaweedFS is a meaningful gate.

**Against:**

- **A different operating model.** Master, volume and filer layers exist even in single-process mode, so
  tuning, backup and troubleshooting knowledge does not transfer from MinIO.
- **Edge-case divergence from AWS.** Example: a Content-MD5 mismatch on a presigned upload returns 500
  rather than S3's 400 `BadDigest` (seaweedfs#7305); signature mismatches have also been reported with
  some SDKs (seaweedfs#6598).
- **The specific risk for Diariz is the AWS SDK for .NET v4,** which sends flexible-checksum headers by
  default - an area where several S3-compatible stores have lagged. The integration suite would surface it
  immediately.
- **Recorded MinIO-specific workarounds must be re-validated, not assumed:** the `PutObject` payload-signing
  note (`AudioStorage.cs`, and the MinIO quirk in `CLAUDE.md`) and the presigned-URL-over-plain-HTTP
  handling in `GetPresignedReadUrlAsync`.
- **Test harness work.** Testcontainers has no SeaweedFS module; `ContainersFixture` would need a generic
  container with its own readiness check.
- GlitchTip's S3 usage must be verified separately; the Diariz suite does not cover it.

## 4. Alternatives considered

| Option | Licence | For | Against |
|---|---|---|---|
| Stay on pinned MinIO | AGPL-3.0 | Zero work | Frozen forever; image hosting outside our control |
| **SeaweedFS** | Apache-2.0 | Mature, very active, broad S3 coverage | More operational concepts than needed |
| Garage | AGPL-3.0 | Very simple to run; popular for small self-hosted stacks | Smaller feature set and community; check GlitchTip needs |
| RustFS | Apache-2.0 | Designed as a MinIO drop-in | 1.0.0 released on 2026-09-16 - too new to trust with recordings |
| Community MinIO forks | AGPL-3.0 | Closest to drop-in | Longevity depends on volunteers |

## 5. Recommendation

1. **Now (cheap, independent of any migration):**
   - Copy the pinned MinIO image to a registry we control (GHCR) or keep a saved tarball, so a quay.io
     removal cannot block a deploy.
   - Stop publishing the S3 port on all interfaces (bind to `127.0.0.1`, or remove the mapping if nothing
     outside the stack uses it), and confirm production does not rely on the `minioadmin` defaults.
2. **Within a few months: migrate to SeaweedFS**, as its own change, rehearsed on the dev server:
   - Integration suite green against SeaweedFS as the gate.
   - Verify GlitchTip against it separately.
   - Copy data with `rclone`; keep the old MinIO volume untouched until SeaweedFS has run in production for
     a while.
   - Update `docs/Data_Schema.md` (storage layout), `docs/Overall_Synopsis_of_Platform.md` (external
     dependency), the MinIO notes in `CLAUDE.md`, and the backup/restore docs if storage handling changes.
3. **Choose Garage instead** if operational simplicity matters more than feature breadth.
4. **Revisit RustFS** in 6-12 months; if it has stayed healthy it becomes the lowest-friction option.

## Sources

- [minio/minio - Maintenance Mode, issue #21714](https://github.com/minio/minio/issues/21714)
- [Why not archive the Community Edition? - Discussion #21667](https://github.com/minio/minio/discussions/21667)
- [MinIO's community edition is archived. What still runs in 2026 - Storm Developments](https://stormdevelopments.ca/blog/minio-s-community-edition-is-archived-what-still-runs-in-2026/)
- [MinIO Is Done With Open Source, What Are Your Options? - It's FOSS](https://itsfoss.com/news/minio-moves-away-from-open-source/)
- [MinIO Is Dead, Long Live MinIO - Vonng](https://blog.vonng.com/en/db/minio-resurrect/)
- [MinIO Support After the Archival - TuxCare](https://tuxcare.com/blog/minio-els/)
- [MinIO's Open-Source Edition Is Archived - Cloudhim](https://www.cloudhim.com/cloud-infrastructure/minio-community-edition-archived-what-to-do)
- [MinIO Is Archived: Move to Garage - SumGuy's Ramblings](https://sumguy.com/minio-archived-garage-alternative/)
- [The End of an Era: MinIO Community Edition is Archived - The Cloud Support Engineer](https://thecloudsupportengineer.com/the-end-of-an-era-minio-community-edition-is-archived-whats-next/)
- [SeaweedFS repository](https://github.com/seaweedfs/seaweedfs) and [Amazon S3 API wiki](https://github.com/seaweedfs/seaweedfs/wiki/Amazon-S3-API)
- [seaweedfs#7305 - presigned URL MD5 mismatch returns 500](https://github.com/seaweedfs/seaweedfs/issues/7305)
- [seaweedfs#6598 - SignatureDoesNotMatch with NodeJS](https://github.com/seaweedfs/seaweedfs/issues/6598)
- [S3 API Gateway - DeepWiki](https://deepwiki.com/seaweedfs/seaweedfs/3.3-s3-api-gateway)
