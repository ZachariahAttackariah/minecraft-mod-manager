# Builds ..\MinecraftModManager.exe with the C# compiler that ships with Windows (.NET Framework 4.8).
# The page (mod-manager.template.html + names.json) is embedded in the exe.
# The app window uses Microsoft's WebView2 DLLs from source\lib (from the Microsoft.Web.WebView2
# NuGet package); they are copied next to the exe. The WebView2 runtime itself is part of Windows.
$ErrorActionPreference = "Stop"
$src = $PSScriptRoot
$appDir = Split-Path $src -Parent
$lib = Join-Path $src "lib"
$exe = Join-Path $appDir "MinecraftModManager.exe"
$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { throw "C# compiler not found at $csc" }
if (Get-Process -Name MinecraftModManager -ErrorAction SilentlyContinue) {
    throw "MinecraftModManager.exe is running. Close its window, then build again."
}
$webViewDlls = "Microsoft.Web.WebView2.Core.dll", "Microsoft.Web.WebView2.WinForms.dll", "WebView2Loader.dll"
foreach ($dll in $webViewDlls) { if (-not (Test-Path (Join-Path $lib $dll))) { throw "Missing $lib\$dll" } }

$temp = Join-Path $src "build-temp"
New-Item -ItemType Directory -Force $temp | Out-Null
try {
    # names.json (optional, not in the repository) maps jar file names to display names and Modrinth links.
    $namesFile = Join-Path $src "names.json"
    $names = if (Test-Path $namesFile) { [System.IO.File]::ReadAllText($namesFile).Trim() } else { "{}" }
    $template = [System.IO.File]::ReadAllText((Join-Path $src "mod-manager.template.html"))
    if (-not $template.Contains("/*__NAMES__*/{}")) { throw "The template is missing the /*__NAMES__*/{} placeholder." }
    $page = Join-Path $temp "mod-manager.html"
    # The pixel font for the stylised text (fonts\Minecraft.otf, SIL OFL, see fonts\Minecraft-OFL.txt) goes into
    # the page itself, so it shows without an internet connection.
    $fontFile = Join-Path $src "fonts\Minecraft.otf"
    if (-not $template.Contains("__FONT_MINECRAFT__")) { throw "The template is missing the __FONT_MINECRAFT__ placeholder." }
    if (-not (Test-Path $fontFile)) { throw "Missing $fontFile" }
    $font = [Convert]::ToBase64String([System.IO.File]::ReadAllBytes($fontFile))
    [System.IO.File]::WriteAllText($page, $template.Replace("/*__NAMES__*/{}", $names).Replace("__FONT_MINECRAFT__", $font), (New-Object System.Text.UTF8Encoding($false)))

    # Syntax-check the page's script with Windows' own JavaScript engine (Chakra) so a typo can't ship.
    # That engine is older than the app's, so a few modern-but-valid spellings are rewritten in the check copy only.
    $script = [regex]::Match($template, '(?s)<script>(.*)</script>').Groups[1].Value
    $script = [regex]::Replace($script, '([{,]\s*)class:', '$1"class":').Replace('.catch(', '["catch"](').Replace('.default', '["default"]')
    $check = Join-Path $temp "syntax-check.js"
    [System.IO.File]::WriteAllText($check, "function __check__() {`n" + $script + "`n}`nWScript.Echo('syntax ok');", [System.Text.Encoding]::Unicode)
    $result = (& cscript //nologo "//E:{16d51579-a30b-4c8b-a276-0ff4dc41e755}" $check 2>&1 | Out-String).Trim()
    if ($result -ne "syntax ok") { throw "The page's script has a syntax error (line numbers count from <script>, plus one):`n$result" }
    # Two functions with the same name in the page: the later one silently replaces the earlier everywhere.
    $dupes = [regex]::Matches($script, '(?m)^  function ([A-Za-z0-9_$]+)\(') | ForEach-Object { $_.Groups[1].Value } | Group-Object | Where-Object { $_.Count -gt 1 } | ForEach-Object { $_.Name }
    if ($dupes) { throw "The page defines these functions more than once (the last one wins everywhere): $($dupes -join ', ')" }

    $icon = Join-Path $src "app.ico"
    if (-not (Test-Path $icon)) { & (Join-Path $src "make-icon.ps1") | Out-Host }

    & $csc /nologo /target:winexe /platform:x64 /optimize+ /utf8output `
        "/out:$exe" "/win32icon:$icon" "/resource:$page,MinecraftModManager.mod-manager.html" `
        /r:System.Web.Extensions.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll `
        /r:System.Windows.Forms.dll /r:System.Drawing.dll `
        "/r:$lib\Microsoft.Web.WebView2.Core.dll" "/r:$lib\Microsoft.Web.WebView2.WinForms.dll" `
        (Get-ChildItem $src -Filter *.cs | Sort-Object Name | ForEach-Object { $_.FullName })
    if ($LASTEXITCODE -ne 0) { throw "Build failed (csc exit code $LASTEXITCODE)." }
    foreach ($dll in $webViewDlls) { Copy-Item (Join-Path $lib $dll) $appDir -Force }
    "Built {0} ({1:N0} bytes) + WebView2 DLLs" -f $exe, (Get-Item $exe).Length
} finally {
    Remove-Item -Recurse -Force $temp -ErrorAction SilentlyContinue
}
