[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageDirectory,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [Parameter(Mandatory)][ValidatePattern('^(?:[0-9a-f]{7,40}|local)$')][string]$Commit
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $PackageDirectory).Path
$app = (Resolve-Path -LiteralPath (Join-Path $root 'app')).Path
if ([IO.Path]::GetDirectoryName($app) -ne $root) { throw '应用目录必须位于安装根目录下' }
if (-not (Test-Path -LiteralPath (Join-Path $app 'HongxueCompanion.exe') -PathType Leaf)) { throw '主程序缺失' }
if (Test-Path -LiteralPath (Join-Path $root 'HongxueCompanion.exe')) { throw '请使用新的打包目录' }
& (Join-Path $PSScriptRoot 'Build-PortableLauncher.ps1') -OutputPath (Join-Path $root 'HongxueCompanion.exe') -Version $Version -Commit $Commit
& (Join-Path $PSScriptRoot 'Build-PortableLauncher.ps1') -OutputPath (Join-Path $root 'ASPAssistant.App.exe') -Version $Version -Commit $Commit -LegacyUpdater
if (Test-Path -LiteralPath (Join-Path $app 'licenses')) {
    Move-Item -LiteralPath (Join-Path $app 'licenses') -Destination (Join-Path $root 'licenses')
}
else { New-Item -ItemType Directory -Path (Join-Path $root 'licenses') | Out-Null }
foreach ($name in @('LICENSE','LICENSE.upstream','NOTICE')) {
    Move-Item -LiteralPath (Join-Path $app $name) -Destination (Join-Path $root ('licenses/'+$name))
}
@{ formatVersion=1; executable='app/HongxueCompanion.exe' } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'update-layout.json') -Encoding utf8
@"
鸿雪·卫戍助手 v$Version

双击根目录 HongxueCompanion.exe 启动。app 文件夹包含主程序、运行依赖和游戏数据，请保留完整目录。
个人配置继续保存在用户 AppData 的 HongxueCompanion 目录；实际路径可在“设置 → 关于 → 数据”查看。
ASPAssistant.App.exe 为旧版自动更新和旧快捷方式保留的兼容入口；日常推荐使用 HongxueCompanion.exe。
v1.4.0 及之后版本可直接自动更新；更早版本建议完整下载安装。
更新前请完全退出助手（包括托盘）。准备导入配置后，请完全退出并手动重开；下次启动时应用导入，并保留导入前备份。
许可与第三方归属见 licenses 文件夹。
"@ | Set-Content -LiteralPath (Join-Path $root '使用说明.txt') -Encoding utf8BOM
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../../docs/问题排查指南.md') -Destination (Join-Path $root '问题排查指南.md')
