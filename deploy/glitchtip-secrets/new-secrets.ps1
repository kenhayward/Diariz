# ==========================================================================
# new-secrets.ps1 - generate GlitchTip's secrets and assemble its EMAIL_URL.
#
# Called by NewGlitchTipSecrets.cmd. Windows PowerShell 5.1 compatible.
#
# Nothing here is written to disk. The output is meant to be copied straight
# into deploy\.env on the server you are setting up, and the window closed.
# ==========================================================================
[CmdletBinding()]
param(
    [switch]$NoEmail
)

$ErrorActionPreference = 'Stop'

# --------------------------------------------------------------------------
# Random secrets.
#
# HEX, deliberately, for all of them.
#
# A .env value is read by docker compose, by a POSIX shell and sometimes by a
# Windows shell. In .env a `$` is interpolated by compose and a `#` starts a
# comment; in a shell, quotes, spaces, backticks and `!` all need escaping.
# base64 avoids `$` and `#` but still yields `+` `/` `=`, which are fine in
# .env but bite the moment someone pastes a value into a shell command.
#
# Hex is [0-9a-f] only: no escaping anywhere, ever. 32 bytes is 256 bits,
# which is more than Django's SECRET_KEY needs and costs nothing.
# --------------------------------------------------------------------------
function New-HexSecret {
    param([int]$Bytes = 32)
    $b = [byte[]]::new($Bytes)
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b)
    # No pipeline: this file is also invoked from cmd contexts where a `|`
    # would need escaping that then leaks into PowerShell.
    return [System.BitConverter]::ToString($b).Replace('-', '').ToLower()
}

$secretKey = New-HexSecret 32
$pgPassword = New-HexSecret 32

Write-Host ''
Write-Host '=== GlitchTip secrets ===' -ForegroundColor Cyan
Write-Host ''
Write-Host 'Paste into deploy\.env on THIS server:'
Write-Host ''
Write-Host "GLITCHTIP_SECRET_KEY=$secretKey"
Write-Host "GLITCHTIP_POSTGRES_PASSWORD=$pgPassword"

if ($NoEmail) {
    Write-Host ''
    Write-Host 'Skipping EMAIL_URL (you passed /noemail).'
    Write-Host ''
    return
}

# --------------------------------------------------------------------------
# EMAIL_URL.
#
# GlitchTip requires SMTP for invitations and password resets, and takes it
# as a single URL: smtp://user:password@host:port
#
# That is a URL, so the user and password are URL components, not free text.
# An `@` in the password ends the userinfo early; a `:` splits user from
# password; a `/`, `?` or `#` terminates it. Any of those produces a
# confusing authentication failure rather than a parse error.
#
# EscapeDataString does RFC 3986 percent-encoding, escaping everything
# outside the unreserved set (A-Z a-z 0-9 - . _ ~). That is the correct
# encoding for a URL component.
# --------------------------------------------------------------------------
Write-Host ''
Write-Host '=== EMAIL_URL ===' -ForegroundColor Cyan
Write-Host ''
Write-Host 'GlitchTip needs SMTP for invitations and password resets.'
Write-Host 'Press Enter at the host prompt to skip and fill it in by hand later.'
Write-Host ''

$smtpHost = Read-Host 'SMTP host (e.g. smtp.fastmail.com)'
if ([string]::IsNullOrWhiteSpace($smtpHost)) {
    Write-Host ''
    Write-Host 'Skipped. GLITCHTIP_EMAIL_URL must still be set before first start.'
    Write-Host ''
    return
}

$smtpPort = Read-Host 'SMTP port [587]'
if ([string]::IsNullOrWhiteSpace($smtpPort)) { $smtpPort = '587' }

$smtpUser = Read-Host 'SMTP username'
$securePass = Read-Host 'SMTP password (not echoed)' -AsSecureString
$bstr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($securePass)
try {
    $smtpPass = [System.Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
} finally {
    [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
}

$encUser = [uri]::EscapeDataString($smtpUser)
$encPass = [uri]::EscapeDataString($smtpPass)

$fromEmail = Read-Host "DEFAULT_FROM_EMAIL [$smtpUser]"
if ([string]::IsNullOrWhiteSpace($fromEmail)) { $fromEmail = $smtpUser }

Write-Host ''
Write-Host "GLITCHTIP_EMAIL_URL=smtp://${encUser}:${encPass}@${smtpHost}:${smtpPort}"
Write-Host "GLITCHTIP_FROM_EMAIL=$fromEmail"
Write-Host ''

if ($encUser -ne $smtpUser -or $encPass -ne $smtpPass) {
    Write-Host 'NOTE: your username or password contained characters that had to be' -ForegroundColor Yellow
    Write-Host '      percent-encoded to survive being inside a URL. That is correct per' -ForegroundColor Yellow
    Write-Host '      RFC 3986, but VERIFY IT WORKS: after first start, trigger a password' -ForegroundColor Yellow
    Write-Host '      reset and confirm the mail arrives. If it does not, the simplest fix' -ForegroundColor Yellow
    Write-Host '      is an SMTP app-password made only of letters and digits, which needs' -ForegroundColor Yellow
    Write-Host '      no encoding at all.' -ForegroundColor Yellow
    Write-Host ''
}
