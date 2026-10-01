# ==========================================================================
# new-keys.ps1 - print a fresh S3 key pair for deploy\.env. Called by
# NewS3Keys.cmd; the Linux/macOS twin is new-s3-keys.sh.
#
# SeaweedFS reads its identities from the s3_identities config in
# docker-compose.yml, so there is nothing to provision inside the store:
# paste the two lines into deploy\.env, then (if the stack is already running)
#   docker compose up -d --force-recreate s3
# because Compose does not notice a key change on its own.
#
# HEX, deliberately: .env is interpolated by Compose and the keys are
# embedded in a JSON document, so a `$`, `#`, `"` or `\` would be expanded,
# start a comment or break the JSON. Hex is [0-9a-f] only.
# ==========================================================================
param([string]$Kind = "app")

switch ($Kind.ToLower()) {
    "app"       { $prefix = "S3_APP" }
    "root"      { $prefix = "S3_ROOT" }
    "glitchtip" { $prefix = "GLITCHTIP_S3" }
    default {
        Write-Error "Unknown kind '$Kind'. Usage: NewS3Keys.cmd [app|root|glitchtip]"
        exit 2
    }
}

function New-HexSecret {
    param([int]$Bytes = 32)
    $b = [byte[]]::new($Bytes)
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b)
    # No pipeline, matching glitchtip-secrets\new-secrets.ps1 (cmd-invoked).
    return [System.BitConverter]::ToString($b).Replace('-', '').ToLower()
}

Write-Output "${prefix}_ACCESS_KEY=$(New-HexSecret 10)"
Write-Output "${prefix}_SECRET_KEY=$(New-HexSecret 24)"
