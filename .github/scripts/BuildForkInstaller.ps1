param(
    [Parameter(Mandatory)][string] $Repository,
    [Parameter(Mandatory)][string] $UpstreamTag,
    [Parameter(Mandatory)][string] $Version,
    [Parameter(Mandatory)][string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$output = (New-Item -ItemType Directory -Path $OutputDirectory -Force).FullName
$app = Join-Path $output 'BetterGI'

# 用上游正式版本号发布自包含应用，并写入 fork 的更新仓库。
dotnet publish (Join-Path $repoRoot 'BetterGenshinImpact/BetterGenshinImpact.csproj') -c Release -p:Platform=x64 -p:PublishProfile=FolderProfile "-p:Version=$Version" "-p:ForkUpdateRepository=$Repository" -o $app
$appVersion = (Get-Item -LiteralPath (Join-Path $app 'BetterGI.exe')).VersionInfo.ProductVersion
if ($appVersion -cne $Version) { throw "Unexpected BetterGI version: $appVersion." }
$assemblyInfo = Get-ChildItem -LiteralPath (Join-Path $repoRoot 'BetterGenshinImpact/obj/x64/Release') -Filter '*.AssemblyInfo.cs' -File -Recurse
if (-not ($assemblyInfo | Select-String -SimpleMatch "AssemblyMetadataAttribute(`"ForkUpdateRepository`", `"$Repository`")")) {
    throw 'The fork update repository was not written to the assembly.'
}

# 复用对应上游 Release 的地图编辑器和脚本列表静态资源。
$extra = Join-Path $output 'upstream-assets'
$archive = Join-Path $output "BetterGI_v$Version.7z"
gh release download $UpstreamTag -R babalae/better-genshin-impact --pattern "BetterGI_v$Version.7z" --output $archive
$webAssets = @('Assets/Map/Editor', 'Assets/Web/ScriptRepo')
$filters = @($webAssets | ForEach-Object { "BetterGI/$_/*" })
7z x $archive @filters "-o$extra" -y
foreach ($asset in $webAssets) {
    $source = Join-Path $extra "BetterGI/$asset"
    if (-not (Get-ChildItem -LiteralPath $source -File -Recurse | Select-Object -First 1)) {
        throw "Missing upstream release assets: $asset."
    }
    $destination = (New-Item -ItemType Directory -Path (Join-Path $app $asset) -Force).FullName
    Get-ChildItem -LiteralPath $source -Force | Copy-Item -Destination $destination -Recurse -Force
}

# 使用与上游 0.67.0 相同的打包器，固定下载版本和校验值。
$builder = Join-Path $output 'kachina-builder.exe'
gh release download 0.5.1 -R YuehaiTeam/kachina-installer --pattern kachina-builder.exe --output $builder
if ((Get-FileHash -LiteralPath $builder -Algorithm SHA256).Hash -ine 'addaa8e2de08926636f4eabbc9bed4c28e46e92ae55f7cc77d60a34b53bfe8cb') {
    throw 'Kachina builder hash does not match.'
}

# 保留 BetterGI 的安装标识和 User 目录保护，只使用 fork 的 GitHub 更新源。
$config = Get-Content -LiteralPath (Join-Path $repoRoot 'Build/kachina.config.json') -Raw | ConvertFrom-Json
$githubSource = $config.source | Where-Object id -eq 'github'
$githubSource.uri = "https://github.com/$Repository" + '/releases/download/${version}/BetterGI.Install.${version}.exe'
$config.source = @($githubSource)
$configPath = Join-Path $output 'kachina.config.json'
$config | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $configPath -Encoding utf8NoBOM

$updater = Join-Path $app 'BetterGI.update.exe'
$metadata = Join-Path $output 'metadata.json'
$compressed = Join-Path $output 'hashed'
$installer = Join-Path $output "BetterGI.Install.$Version.exe"
& $builder pack -c $configPath -o $updater
& $builder gen -j 6 -i $app -m $metadata -o $compressed -r $Repository -t $Version -u $updater > (Join-Path $output 'kachina-gen.log')
& $builder pack -c $configPath -m $metadata -d $compressed -o $installer

# 从成品回读版本、更新源和用户目录保护设置，避免发布无法更新的安装包。
$verifiedMetadata = Join-Path $output 'verified-metadata.json'
$verifiedConfig = Join-Path $output 'verified-config.json'
$verifiedUpdaterConfig = Join-Path $output 'verified-updater-config.json'
& $builder extract -i $installer -f $verifiedMetadata -n '\0META'
& $builder extract -i $installer -f $verifiedConfig -n '\0CONFIG'
& $builder extract -i $updater -f $verifiedUpdaterConfig -n '\0CONFIG'
$package = Get-Content -LiteralPath $verifiedMetadata -Raw | ConvertFrom-Json
$installConfig = Get-Content -LiteralPath $verifiedConfig -Raw | ConvertFrom-Json
$updateConfig = Get-Content -LiteralPath $verifiedUpdaterConfig -Raw | ConvertFrom-Json
if ($package.tag_name -cne $Version -or $package.repo_name -cne $Repository -or
    $installConfig.source.Count -ne 1 -or $installConfig.source[0].uri -cne $githubSource.uri -or
    $updateConfig.source.Count -ne 1 -or $updateConfig.source[0].uri -cne $githubSource.uri -or
    $installConfig.ignoreFolderPath -cnotcontains '${INSTALL_PATH}/User' -or
    $updateConfig.ignoreFolderPath -cnotcontains '${INSTALL_PATH}/User') {
    throw 'Installer version, update source, or User directory protection does not match.'
}
