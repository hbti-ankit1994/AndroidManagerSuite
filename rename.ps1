$files = Get-ChildItem -Path . -Recurse -Include *.cs,*.xaml,*.csproj,*.slnx,*.md
foreach ($f in $files) {
    if ($f.FullName -match '\\SDK\\') { continue }
    $content = Get-Content $f.FullName -Raw
    if ($content -match 'ADBClient') {
        $content = $content -replace 'ADBClient', 'AndroidManagerSuite'
        Set-Content -Path $f.FullName -Value $content -NoNewline
    }
}
