param(
    [Parameter(Mandatory=$true)]
    [string]$targetDir
)

$hashes = [ordered]@{}

try {
    # 标准化路径格式
    $targetDir = $targetDir.Replace('\', '/').TrimEnd('/')

    # 遍历目标目录
    Get-ChildItem -Path $targetDir -File -Recurse -Exclude *.zip,*.pdb,*.ipdb | ForEach-Object {
        # 计算相对路径
        $relativePath = $_.FullName.Replace('\', '/').Replace("$targetDir/", "")
        
        # 计算哈希值
        $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
        
        # 添加到哈希表
        $hashes.Add($relativePath, $hash)
    }

    # 生成 JSON 文件
    $outputPath = Join-Path $targetDir "hashes.json"
    $hashes | ConvertTo-Json | Out-File -FilePath $outputPath -Encoding utf8
    Write-Output "✔ 哈希文件已生成: $outputPath"
}
catch {
    Write-Error "❌ 生成哈希文件时发生错误: $_"
    exit 1
}
