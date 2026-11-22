Set-StrictMode -Version Latest

function Get-TemnoRepoRoot {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$StartPath
    )

    $current = (Resolve-Path -LiteralPath $StartPath).Path
    if (Test-Path -LiteralPath $current -PathType Leaf) {
        $current = Split-Path -Parent $current
    }

    while (-not [string]::IsNullOrWhiteSpace($current)) {
        if (Test-Path -LiteralPath (Join-Path $current "TemnoWindows.sln")) {
            return $current
        }

        $parent = Split-Path -Parent $current
        if ($parent -eq $current) {
            break
        }

        $current = $parent
    }

    throw "Could not locate the Temno Windows repository from '$StartPath'."
}

function Get-TemnoEnvironmentValue {
    [CmdletBinding()]
    param(
        [AllowNull()]
        [string]$ExplicitValue,

        [Parameter(Mandatory)]
        [string]$EnvironmentVariable
    )

    if (-not [string]::IsNullOrWhiteSpace($ExplicitValue)) {
        return $ExplicitValue
    }

    # WP-193 dual-read: TEMNO_* wins; a set PALMIER_* legacy value is honoured for one release.
    $value = [Environment]::GetEnvironmentVariable($EnvironmentVariable)
    if (-not [string]::IsNullOrWhiteSpace($value)) {
        return $value
    }
    if ($EnvironmentVariable.StartsWith('TEMNO_', [StringComparison]::Ordinal)) {
        return [Environment]::GetEnvironmentVariable('PALMIER_' + $EnvironmentVariable.Substring(6))
    }
    return $value
}

function Assert-TemnoHttpsUri {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Value,

        [Parameter(Mandatory)]
        [string]$Label
    )

    $uri = $null
    if (-not [Uri]::TryCreate($Value, [UriKind]::Absolute, [ref]$uri) -or
        $uri.Scheme -ne [Uri]::UriSchemeHttps -or
        -not [string]::IsNullOrEmpty($uri.UserInfo)) {
        throw "$Label must be an absolute HTTPS URI without user credentials."
    }

    return $uri
}

function Assert-TemnoMsixVersion {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Version,

        [Parameter(Mandatory)]
        [string]$Source
    )

    if ($Version -notmatch '^\d{1,5}\.\d{1,5}\.\d{1,5}\.\d{1,5}$') {
        throw "$Source version '$Version' must contain four unsigned 16-bit components."
    }

    foreach ($component in $Version.Split('.')) {
        if ([int]$component -gt 65535) {
            throw "$Source version '$Version' contains a component larger than 65535."
        }
    }
}

function Get-TemnoXmlAttribute {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [System.Xml.XmlElement]$Element,

        [Parameter(Mandatory)]
        [string]$Name
    )

    return $Element.GetAttribute($Name)
}

function ConvertTo-TemnoAppxManifestInfo {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [System.Xml.XmlDocument]$Document,

        [Parameter(Mandatory)]
        [string]$Source
    )

    $root = $Document.DocumentElement
    if ($null -eq $root -or $root.LocalName -ne 'Package') {
        throw "$Source is not an MSIX Package manifest."
    }

    $identity = $root.SelectSingleNode("./*[local-name()='Identity']")
    if ($null -eq $identity) {
        throw "$Source does not contain Package/Identity."
    }

    $application = $root.SelectSingleNode("./*[local-name()='Applications']/*[local-name()='Application']")
    if ($null -eq $application) {
        throw "$Source does not contain Package/Applications/Application."
    }

    $properties = $root.SelectSingleNode("./*[local-name()='Properties']")
    $displayNameNode = $null
    $publisherDisplayNameNode = $null
    if ($null -ne $properties) {
        $displayNameNode = $properties.SelectSingleNode("./*[local-name()='DisplayName']")
        $publisherDisplayNameNode = $properties.SelectSingleNode("./*[local-name()='PublisherDisplayName']")
    }

    $visual = $root.SelectSingleNode(".//*[local-name()='VisualElements']")
    $splash = $root.SelectSingleNode(".//*[local-name()='SplashScreen']")

    $assetPaths = New-Object System.Collections.Generic.List[string]
    $propertiesLogo = $root.SelectSingleNode("./*[local-name()='Properties']/*[local-name()='Logo']")
    if ($null -ne $propertiesLogo -and -not [string]::IsNullOrWhiteSpace($propertiesLogo.InnerText)) {
        $assetPaths.Add($propertiesLogo.InnerText)
    }

    foreach ($visualElement in @($root.SelectNodes(".//*[local-name()='VisualElements']"))) {
        foreach ($attributeName in @('Square150x150Logo', 'Square44x44Logo')) {
            $value = Get-TemnoXmlAttribute -Element $visualElement -Name $attributeName
            if (-not [string]::IsNullOrWhiteSpace($value)) {
                $assetPaths.Add($value)
            }
        }
    }

    foreach ($defaultTile in @($root.SelectNodes(".//*[local-name()='DefaultTile']"))) {
        $value = Get-TemnoXmlAttribute -Element $defaultTile -Name 'Wide310x150Logo'
        if (-not [string]::IsNullOrWhiteSpace($value)) {
            $assetPaths.Add($value)
        }
    }

    foreach ($splashScreen in @($root.SelectNodes(".//*[local-name()='SplashScreen']"))) {
        $value = Get-TemnoXmlAttribute -Element $splashScreen -Name 'Image'
        if (-not [string]::IsNullOrWhiteSpace($value)) {
            $assetPaths.Add($value)
        }
    }

    [pscustomobject]@{
        Source = $Source
        IdentityName = Get-TemnoXmlAttribute -Element $identity -Name 'Name'
        Publisher = Get-TemnoXmlAttribute -Element $identity -Name 'Publisher'
        Version = Get-TemnoXmlAttribute -Element $identity -Name 'Version'
        DisplayName = if ($null -ne $displayNameNode) { $displayNameNode.InnerText.Trim() } else { '' }
        PublisherDisplayName = if ($null -ne $publisherDisplayNameNode) { $publisherDisplayNameNode.InnerText.Trim() } else { '' }
        VisualDisplayName = if ($null -ne $visual) { Get-TemnoXmlAttribute -Element $visual -Name 'DisplayName' } else { '' }
        Description = if ($null -ne $visual) { Get-TemnoXmlAttribute -Element $visual -Name 'Description' } else { '' }
        BackgroundColor = if ($null -ne $visual) { Get-TemnoXmlAttribute -Element $visual -Name 'BackgroundColor' } else { '' }
        SplashBackgroundColor = if ($null -ne $splash) { Get-TemnoXmlAttribute -Element $splash -Name 'BackgroundColor' } else { '' }
        ApplicationId = Get-TemnoXmlAttribute -Element $application -Name 'Id'
        Executable = Get-TemnoXmlAttribute -Element $application -Name 'Executable'
        EntryPoint = Get-TemnoXmlAttribute -Element $application -Name 'EntryPoint'
        HasRunFullTrust = $null -ne $root.SelectSingleNode(".//*[local-name()='Capability' and @Name='runFullTrust']")
        AssetPaths = $assetPaths.ToArray()
    }
}

function Get-TemnoLockedMsixIdentity {
    [CmdletBinding()]
    param()

    [pscustomobject]@{
        IdentityName = 'io.temno.windows'
        Publisher = 'CN=Temno'
        DisplayName = 'Temno'
        PublisherDisplayName = 'Temno'
        VisualDisplayName = 'Temno'
        Description = 'A native Windows video editor you can edit by hand or hand to an agent.'
        BackgroundColor = '#0A0A0A'
    }
}

function Assert-TemnoLockedMsixIdentity {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [psobject]$Manifest
    )

    $lock = Get-TemnoLockedMsixIdentity
    $checks = @(
        @{ Name = 'Identity Name'; Actual = $Manifest.IdentityName; Expected = $lock.IdentityName }
        @{ Name = 'Identity Publisher'; Actual = $Manifest.Publisher; Expected = $lock.Publisher }
        @{ Name = 'DisplayName'; Actual = $Manifest.DisplayName; Expected = $lock.DisplayName }
        @{ Name = 'PublisherDisplayName'; Actual = $Manifest.PublisherDisplayName; Expected = $lock.PublisherDisplayName }
        @{ Name = 'VisualElements DisplayName'; Actual = $Manifest.VisualDisplayName; Expected = $lock.VisualDisplayName }
        @{ Name = 'VisualElements Description'; Actual = $Manifest.Description; Expected = $lock.Description }
        @{ Name = 'VisualElements BackgroundColor'; Actual = $Manifest.BackgroundColor; Expected = $lock.BackgroundColor }
        @{ Name = 'SplashScreen BackgroundColor'; Actual = $Manifest.SplashBackgroundColor; Expected = $lock.BackgroundColor }
    )

    foreach ($check in $checks) {
        if (-not [string]::Equals([string]$check.Actual, $check.Expected, [StringComparison]::Ordinal)) {
            throw "$($Manifest.Source) locked MSIX $($check.Name) is '$($check.Actual)'; expected '$($check.Expected)'."
        }
    }
}

function Read-TemnoAppxManifest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $resolvedPath = (Resolve-Path -LiteralPath $Path -ErrorAction Stop).Path
    $document = New-Object System.Xml.XmlDocument
    $document.PreserveWhitespace = $true
    $document.Load($resolvedPath)
    return ConvertTo-TemnoAppxManifestInfo -Document $document -Source $resolvedPath
}

function Assert-TemnoAppxManifestInfo {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [psobject]$Manifest,

        [switch]$RequireResolvedApplicationTokens
    )

    if ([string]::IsNullOrWhiteSpace($Manifest.IdentityName) -or
        $Manifest.IdentityName -notmatch '^[A-Za-z][A-Za-z0-9.-]*$') {
        throw "$($Manifest.Source) has an invalid package Identity Name '$($Manifest.IdentityName)'."
    }

    if ([string]::IsNullOrWhiteSpace($Manifest.Publisher) -or $Manifest.Publisher -notmatch '^CN=') {
        throw "$($Manifest.Source) must declare an Identity Publisher beginning with 'CN='."
    }

    Assert-TemnoMsixVersion -Version $Manifest.Version -Source $Manifest.Source

    foreach ($field in @('ApplicationId', 'Executable', 'EntryPoint')) {
        if ([string]::IsNullOrWhiteSpace([string]$Manifest.$field)) {
            throw "$($Manifest.Source) has an empty application $field."
        }
    }

    if ($RequireResolvedApplicationTokens -and
        ($Manifest.Executable.Contains('$') -or $Manifest.EntryPoint.Contains('$'))) {
        throw "$($Manifest.Source) still contains unresolved application tokens."
    }

    if (-not $Manifest.HasRunFullTrust) {
        throw "$($Manifest.Source) must retain the runFullTrust capability for the WinUI desktop app."
    }

    if ($Manifest.AssetPaths.Count -eq 0) {
        throw "$($Manifest.Source) does not declare any visual assets."
    }
}

function Test-TemnoManifestAssetOnDisk {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$ManifestDirectory,

        [Parameter(Mandatory)]
        [string]$AssetPath
    )

    $normalized = $AssetPath.Replace('/', '\')
    $candidate = Join-Path $ManifestDirectory $normalized
    if (Test-Path -LiteralPath $candidate -PathType Leaf) {
        return $true
    }

    $directory = Split-Path -Parent $candidate
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
        return $false
    }

    $fileName = Split-Path -Leaf $candidate
    $stem = [System.IO.Path]::GetFileNameWithoutExtension($fileName)
    $extension = [System.IO.Path]::GetExtension($fileName)
    return $null -ne (Get-ChildItem -LiteralPath $directory -File -Filter "$stem.*$extension" -ErrorAction SilentlyContinue |
        Select-Object -First 1)
}

function Get-TemnoMsixPackageDetails {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$PackagePath
    )

    $resolvedPath = (Resolve-Path -LiteralPath $PackagePath -ErrorAction Stop).Path
    if ([System.IO.Path]::GetExtension($resolvedPath) -ine '.msix') {
        throw "Package '$resolvedPath' must have a .msix extension."
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($resolvedPath)
    try {
        $manifestEntry = $archive.Entries | Where-Object { $_.FullName -ieq 'AppxManifest.xml' } | Select-Object -First 1
        if ($null -eq $manifestEntry) {
            throw "Package '$resolvedPath' does not contain AppxManifest.xml."
        }

        $reader = New-Object System.IO.StreamReader($manifestEntry.Open())
        try {
            $document = New-Object System.Xml.XmlDocument
            $document.LoadXml($reader.ReadToEnd())
        }
        finally {
            $reader.Dispose()
        }

        $manifest = ConvertTo-TemnoAppxManifestInfo -Document $document -Source "$resolvedPath!AppxManifest.xml"
        $entries = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
        return [pscustomobject]@{
            PackagePath = $resolvedPath
            Manifest = $manifest
            Entries = $entries
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Get-TemnoMsixSignatureInfo {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$PackagePath
    )

    $resolvedPath = (Resolve-Path -LiteralPath $PackagePath -ErrorAction Stop).Path
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($resolvedPath)
    try {
        $signatureEntry = $archive.Entries | Where-Object { $_.FullName -ieq 'AppxSignature.p7x' } | Select-Object -First 1
        if ($null -eq $signatureEntry) {
            throw "Package '$resolvedPath' does not contain AppxSignature.p7x."
        }

        $stream = $signatureEntry.Open()
        $memory = New-Object System.IO.MemoryStream
        try {
            $stream.CopyTo($memory)
            $signedCms = New-Object System.Security.Cryptography.Pkcs.SignedCms
            $signedCms.Decode($memory.ToArray())
        }
        finally {
            $memory.Dispose()
            $stream.Dispose()
        }

        if ($signedCms.SignerInfos.Count -ne 1 -or $null -eq $signedCms.SignerInfos[0].Certificate) {
            throw "Package '$resolvedPath' must contain exactly one readable signing certificate."
        }

        $certificate = $signedCms.SignerInfos[0].Certificate
        return [pscustomobject]@{
            Thumbprint = $certificate.Thumbprint
            Subject = $certificate.Subject
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Test-TemnoManifestAssetInPackage {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string[]]$Entries,

        [Parameter(Mandatory)]
        [string]$AssetPath
    )

    $normalized = $AssetPath.Replace('\', '/')
    if ($Entries -icontains $normalized) {
        return $true
    }

    $directory = [System.IO.Path]::GetDirectoryName($normalized).Replace('\', '/')
    $fileName = [System.IO.Path]::GetFileName($normalized)
    $stem = [System.IO.Path]::GetFileNameWithoutExtension($fileName)
    $extension = [System.IO.Path]::GetExtension($fileName)
    $prefix = if ([string]::IsNullOrWhiteSpace($directory)) { "$stem." } else { "$directory/$stem." }

    return $null -ne ($Entries | Where-Object {
        $_.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase) -and
        $_.EndsWith($extension, [System.StringComparison]::OrdinalIgnoreCase)
    } | Select-Object -First 1)
}

function Get-TemnoNuGetPackagesRoot {
    [CmdletBinding()]
    param()

    if (-not [string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES) -and (Test-Path -LiteralPath $env:NUGET_PACKAGES)) {
        return (Resolve-Path -LiteralPath $env:NUGET_PACKAGES).Path
    }

    $userProfile = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
    $candidate = Join-Path $userProfile '.nuget\packages'
    if (Test-Path -LiteralPath $candidate) {
        return (Resolve-Path -LiteralPath $candidate).Path
    }

    return $null
}

function Resolve-TemnoSignTool {
    [CmdletBinding()]
    param()

    $command = Get-Command 'signtool.exe' -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $roots = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'),
        (Join-Path $env:ProgramFiles 'Windows Kits\10\bin')
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and (Test-Path -LiteralPath $_) }

    foreach ($root in $roots) {
        $candidate = Get-ChildItem -LiteralPath $root -Recurse -File -Filter 'signtool.exe' -ErrorAction SilentlyContinue |
            Where-Object { $_.DirectoryName -match '\\x64$' } |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($null -ne $candidate) {
            return $candidate.FullName
        }
    }

    # Fall back to the SignTool shipped in the restored Microsoft.Windows.SDK.BuildTools NuGet
    # package so signing works on machines with only the .NET SDK (no standalone Windows SDK / VS).
    $packagesRoot = Get-TemnoNuGetPackagesRoot
    if (-not [string]::IsNullOrWhiteSpace($packagesRoot)) {
        $buildToolsRoot = Join-Path $packagesRoot 'microsoft.windows.sdk.buildtools'
        if (Test-Path -LiteralPath $buildToolsRoot) {
            $candidate = Get-ChildItem -LiteralPath $buildToolsRoot -Recurse -File -Filter 'signtool.exe' -ErrorAction SilentlyContinue |
                Where-Object { $_.DirectoryName -match '\\x64$' } |
                Sort-Object FullName -Descending |
                Select-Object -First 1
            if ($null -ne $candidate) {
                return $candidate.FullName
            }
        }
    }

    throw 'SignTool.exe was not found on PATH, in the Windows Kits, or in the restored Microsoft.Windows.SDK.BuildTools NuGet package. Install the Windows SDK Signing Tools component or restore the app project, then retry.'
}

function Resolve-TemnoMakeAppx {
    [CmdletBinding()]
    param()

    $roots = @(
        (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)) '.nuget\packages\microsoft.windows.sdk.buildtools'),
        (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin')
    ) | Where-Object { Test-Path -LiteralPath $_ }
    foreach ($root in $roots) {
        $found = Get-ChildItem -LiteralPath $root -Recurse -File -Filter 'makeappx.exe' -ErrorAction SilentlyContinue |
            Where-Object { $_.DirectoryName -match '\\x64$' } | Sort-Object FullName -Descending | Select-Object -First 1
        if ($null -ne $found) { return $found.FullName }
    }

    throw 'MakeAppx.exe was not found. Restore the app project once (it brings Microsoft.Windows.SDK.BuildTools) and retry.'
}

function Set-TemnoPackageIdentity {
    # Gives an unsigned MSIX another app's identity in place, as install-app.ps1 gives its registered folder one
    # (WP-334): unpack, set the identity and display names in AppxManifest.xml, pack. MakeAppx rebuilds the block
    # map and content types; a signature would no longer match, so this refuses a signed package.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$PackagePath,

        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [string]$Publisher,

        [Parameter(Mandatory)]
        [string]$DisplayName,

        [string]$PhoneProductId
    )

    $package = Get-TemnoMsixPackageDetails -PackagePath $PackagePath
    if ($package.Entries -icontains 'AppxSignature.p7x') {
        throw "Package '$($package.PackagePath)' is signed; give it its identity before signing."
    }

    $makeAppx = Resolve-TemnoMakeAppx
    $staging = Join-Path ([System.IO.Path]::GetDirectoryName($package.PackagePath)) ('identity-' + [Guid]::NewGuid().ToString('N'))
    try {
        $output = & $makeAppx unpack /p $package.PackagePath /d $staging /o 2>&1
        if ($LASTEXITCODE -ne 0) { throw "MakeAppx could not unpack '$($package.PackagePath)': $($output | Out-String)" }
        foreach ($generated in @('AppxBlockMap.xml', '[Content_Types].xml', 'AppxMetadata')) {
            $path = Join-Path $staging $generated
            if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
        }

        $manifestPath = Join-Path $staging 'AppxManifest.xml'
        $manifest = New-Object System.Xml.XmlDocument
        $manifest.PreserveWhitespace = $true
        $manifest.Load($manifestPath)
        $ns = New-Object System.Xml.XmlNamespaceManager($manifest.NameTable)
        $ns.AddNamespace('f', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
        $ns.AddNamespace('uap', 'http://schemas.microsoft.com/appx/manifest/uap/windows10')
        $ns.AddNamespace('mp', 'http://schemas.microsoft.com/appx/2014/phone/manifest')
        $identity = $manifest.SelectSingleNode('/f:Package/f:Identity', $ns)
        $identity.SetAttribute('Name', $Name)
        $identity.SetAttribute('Publisher', $Publisher)
        $manifest.SelectSingleNode('/f:Package/f:Properties/f:DisplayName', $ns).InnerText = $DisplayName
        foreach ($visual in $manifest.SelectNodes('/f:Package/f:Applications/f:Application/uap:VisualElements', $ns)) {
            $visual.SetAttribute('DisplayName', $DisplayName)
        }
        $phone = $manifest.SelectSingleNode('/f:Package/mp:PhoneIdentity', $ns)
        if ($null -ne $phone -and -not [string]::IsNullOrWhiteSpace($PhoneProductId)) {
            $phone.SetAttribute('PhoneProductId', $PhoneProductId)
        }
        $manifest.Save($manifestPath)

        $output = & $makeAppx pack /d $staging /p $package.PackagePath /o 2>&1
        if ($LASTEXITCODE -ne 0) { throw "MakeAppx could not pack '$($package.PackagePath)': $($output | Out-String)" }
    }
    finally {
        if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue }
    }

    $result = (Get-TemnoMsixPackageDetails -PackagePath $package.PackagePath).Manifest
    if ($result.IdentityName -cne $Name -or $result.Publisher -cne $Publisher -or $result.DisplayName -cne $DisplayName) {
        throw "Package '$($package.PackagePath)' did not take the identity $Name ($Publisher)."
    }

    return $result
}
