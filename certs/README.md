# Certificates (distribution signing)

This folder is for **local/team signing material** and intentionally should not contain committed secrets.

The repo `.gitignore` excludes common key formats (including `*.pfx`).

## Expected file

- `certs\PrismCapture_Distribution.pfx`

The helper script [scripts/PublishPrismCaptureMsix.ps1](../scripts/PublishPrismCaptureMsix.ps1) looks for that path by default.

## Publisher must match

MSIX signing requires that the certificate **Subject** matches the Publisher in:

- `src\ScreenRecorder.App\Package.appxmanifest` (`<Identity Publisher="..." />`)

If they don’t match, installation will fail.

## Password handling

Prefer using an environment variable rather than writing passwords to disk:

```powershell
$env:PRISMCAPTURE_PFX_PASSWORD = "<pfx-password>"
.\scripts\PublishPrismCaptureMsix.ps1 -Platform x64 -Version 1.0.0.0
```

If `PRISMCAPTURE_PFX_PASSWORD` is not set, the script will prompt.
