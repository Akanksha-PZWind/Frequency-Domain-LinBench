# Builds dist\LinBench\LinBench.exe and dist\LinBench-<version>-win64.zip (same approach as TuneMe)
#   powershell -ExecutionPolicy Bypass -File build.ps1
# Needs Windows and uv (https://docs.astral.sh/uv/). The internet is needed once, to fetch the pinned packages into
# .venv-build; the result needs nothing installed and no internet on the PC it is copied to.
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

if (-not (Test-Path ".venv-build\Scripts\python.exe")) {
    Write-Host "Creating Python 3.12 environment (.venv-build) ..."
    uv venv .venv-build --python 3.12
}
Write-Host "Installing pinned requirements ..."
uv pip install --python .venv-build\Scripts\python.exe -r requirements.txt

$ver = (& .venv-build\Scripts\python.exe -c "import linbench; print(linbench.__version__)").Trim()
Write-Host "Building LinBench $ver"

Write-Host "Generating icon and version info ..."
& .venv-build\Scripts\python.exe tools\make_icon.py
& .venv-build\Scripts\python.exe tools\make_version_info.py $ver

Write-Host "Running PyInstaller ..."
& .venv-build\Scripts\pyinstaller.exe LinBench.spec --noconfirm --clean
if ($LASTEXITCODE -ne 0) { throw "PyInstaller failed" }

# the documents and the samples travel with the program; the complete source goes along as well, so the copy on
# another PC can be read, changed and built again
Copy-Item README.md dist\LinBench\README.md -Force
Copy-Item docs dist\LinBench\docs -Recurse -Force
Copy-Item samples dist\LinBench\samples -Recurse -Force
$src = "dist\LinBench\source"
if (Test-Path $src) { Remove-Item $src -Recurse -Force }
New-Item -ItemType Directory $src | Out-Null
foreach ($item in "linbench", "tests", "docs", "samples", "tools", "assets") {
    Copy-Item $item (Join-Path $src $item) -Recurse -Force
}
foreach ($f in "README.md", "VALIDATION.md", "CLAUDE.md", "requirements.txt", "LinBench.spec", "build.ps1", "main.py", ".gitignore") {
    if (Test-Path $f) { Copy-Item $f $src -Force }
}
Get-ChildItem $src -Recurse -Directory -Filter "__pycache__" | Remove-Item -Recurse -Force

Write-Host "Checking that the build needs nothing installed ..."
& .venv-build\Scripts\python.exe tools\audit_bundle.py dist\LinBench
if ($LASTEXITCODE -ne 0) { throw "The build is not self-contained (see the list above)" }

$zip = "dist\LinBench-$ver-win64.zip"
Write-Host "Packing $zip ..."
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path dist\LinBench -DestinationPath $zip -CompressionLevel Optimal
$size = (Get-ChildItem dist\LinBench -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
$zsize = (Get-Item $zip).Length / 1MB
Write-Host ("Done: dist\LinBench\LinBench.exe  (folder {0:N0} MB, zip {1:N0} MB)" -f $size, $zsize)
