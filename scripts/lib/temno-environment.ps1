# Environment identity for the publish scripts (WP-260, docs/contracts/environments.md).
# Dot-source it. It leaves the caller's strict mode and $ErrorActionPreference alone, and it is
# strict-mode clean because build-msix.ps1 runs with Set-StrictMode -Version Latest.

function Get-TemnoEnvironmentNames {
    [CmdletBinding()]
    param()

    return @('development', 'production')
}

function Get-TemnoOtherEnvironment {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidateSet('development', 'production')]
        [string]$Environment
    )

    if ($Environment.ToLowerInvariant() -eq 'development') { return 'production' }
    return 'development'
}

function Get-TemnoDesktopApp {
    # The app each environment's package installs as (WP-334, docs/contracts/environments.md § Desktop builds).
    # Development's is Temno-Dev under the development certificate's subject, the publisher its update feed
    # expects (WP-375). Production's Publisher is $null: it is the manifest's, the release publisher (WP-266).
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidateSet('development', 'production')]
        [string]$Environment
    )

    if ($Environment.ToLowerInvariant() -eq 'development') {
        return [pscustomobject]@{
            Environment = 'development'; Name = 'io.temno.windows.dev'; DisplayName = 'Temno-Dev'; Folder = 'Temno-Dev'
            Publisher = 'CN=Temno'
            # Distinct from production's, so the two packages share no identifier.
            PhoneProductId = 'B4C6E2D8-71A3-4F59-9E0B-3D2C5A8F1E64'
        }
    }

    return [pscustomobject]@{
        Environment = 'production'; Name = 'io.temno.windows'; DisplayName = 'Temno'; Folder = 'Temno'
        Publisher = $null
        PhoneProductId = $null
    }
}

function Get-TemnoDeployProfileRoot {
    [CmdletBinding()]
    param()

    return [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\.local\deploy-profiles'))
}

function ConvertFrom-TemnoJsonObject {
    [CmdletBinding()]
    param(
        [AllowNull()]
        [AllowEmptyString()]
        [string]$Text
    )

    # ConvertFrom-Json unrolls a top-level array, so an object root is checked on the text.
    if ($null -eq $Text -or -not $Text.TrimStart([char]0xFEFF, ' ', "`t", "`r", "`n").StartsWith('{')) {
        throw 'the JSON root must be an object'
    }

    $parsed = $Text | ConvertFrom-Json -ErrorAction Stop
    if ($parsed -isnot [System.Management.Automation.PSCustomObject]) {
        throw 'the JSON root must be an object'
    }

    return $parsed
}

function Get-TemnoJsonMember {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [psobject]$Object,

        [Parameter(Mandatory)]
        [string]$Name
    )

    # Property lookup on a PSCustomObject ignores case; the stamp and profile names are exact.
    return @($Object.PSObject.Properties | Where-Object { $_.Name -ceq $Name })
}

function Get-TemnoSecretPatterns {
    [CmdletBinding()]
    param()

    # A key starts a token, so "task_" or "disk_" in a note is not a Stripe key and "task-or-" is
    # not an OpenRouter key. A fal key is a UUID, a colon and 32 hex digits; a bare UUID is not one.
    # The labels are the refusal's only description of the value.
    return [ordered]@{
        'sk_' = '(?<![A-Za-z0-9])sk_'
        'rk_' = '(?<![A-Za-z0-9])rk_'
        'whsec_' = 'whsec_'
        'sb_secret_' = 'sb_secret_'
        'eyJhbGciOi' = 'eyJhbGciOi'
        're_' = '(?<![A-Za-z0-9])re_[A-Za-z0-9]{8}'
        'sk-or-' = '(?<![A-Za-z0-9])sk-or-'
        'sk-ant-' = '(?<![A-Za-z0-9])sk-ant-'
        'sk-proj-' = '(?<![A-Za-z0-9])sk-proj-'
        'fal <uuid>:' = '(?i)[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}:[0-9a-f]{32}'
    }
}

function Find-TemnoSecretShape {
    [CmdletBinding()]
    param(
        [AllowNull()]
        [AllowEmptyString()]
        [string]$Text
    )

    if ([string]::IsNullOrEmpty($Text)) { return $null }
    $patterns = Get-TemnoSecretPatterns
    foreach ($label in $patterns.Keys) {
        if ($Text -cmatch $patterns[$label]) { return $label }
    }

    return $null
}

function Get-TemnoJsonStrings {
    [CmdletBinding()]
    param(
        [AllowNull()]
        $Value,

        [string]$Path = '$'
    )

    if ($null -eq $Value) { return }
    if ($Value -is [string]) {
        [pscustomobject]@{ Path = $Path; Text = $Value }
        return
    }

    if ($Value -is [System.Management.Automation.PSCustomObject]) {
        foreach ($property in $Value.PSObject.Properties) {
            [pscustomobject]@{ Path = "$Path (member name)"; Text = $property.Name }
            Get-TemnoJsonStrings -Value $property.Value -Path "$Path.$($property.Name)"
        }
        return
    }

    if ($Value -is [System.Collections.IEnumerable]) {
        $index = 0
        foreach ($item in $Value) {
            Get-TemnoJsonStrings -Value $item -Path "$Path[$index]"
            $index++
        }
    }
}

function Read-TemnoDeployProfileFile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [ValidateSet('development', 'production')]
        [string]$Environment
    )

    $example = 'scripts/deploy-profile.example.json'
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "No $Environment deploy profile at '$Path'. Copy $example there, fill in the $Environment hosts and Supabase project, and keep it untracked."
    }

    $text = [System.IO.File]::ReadAllText($Path)

    # Checked on the raw text first so a secret in a broken file is still named. The value is
    # never echoed.
    $shape = Find-TemnoSecretShape -Text $text
    if ($null -ne $shape) {
        throw "Deploy profile '$Path' holds a secret-shaped value ($shape...). Profiles carry public host names and the project ref only; remove it and rotate the key if it was real."
    }

    try {
        $parsed = ConvertFrom-TemnoJsonObject -Text $text
    }
    catch {
        throw "Deploy profile '$Path' is not readable JSON: $($_.Exception.Message). See $example."
    }

    # JSON escapes can hide a prefix from the raw-text scan.
    foreach ($item in @(Get-TemnoJsonStrings -Value $parsed)) {
        $shape = Find-TemnoSecretShape -Text $item.Text
        if ($null -ne $shape) {
            throw "Deploy profile '$Path' holds a secret-shaped value ($shape...) at $($item.Path). Profiles carry public host names and the project ref only; remove it and rotate the key if it was real."
        }
    }

    $allowed = @('environment', 'gatewayHost', 'accountsHost', 'supabaseProjectRef', 'note')
    foreach ($property in $parsed.PSObject.Properties) {
        if ($allowed -cnotcontains $property.Name) {
            throw "Deploy profile '$Path' has an unknown member '$($property.Name)'. The members are $($allowed -join ', '); see $example."
        }
    }

    $values = @{}
    foreach ($name in $allowed) {
        $member = @(Get-TemnoJsonMember -Object $parsed -Name $name)
        if ($member.Count -eq 0 -or $null -eq $member[0].Value) {
            if ($name -ne 'note') {
                throw "Deploy profile '$Path' has no '$name'. See $example."
            }

            $values[$name] = ''
            continue
        }

        if ($member[0].Value -isnot [string]) {
            throw "Deploy profile '$Path' member '$name' must be a string."
        }

        $values[$name] = $member[0].Value
    }

    $named = $values['environment']
    if ((Get-TemnoEnvironmentNames) -cnotcontains $named) {
        throw "Deploy profile '$Path' says environment '$named'. It must be exactly development or production."
    }

    $requested = $Environment.ToLowerInvariant()
    if ($named -cne $requested) {
        throw "Deploy profile '$Path' is for $named, and this run is -Environment $requested. A profile names only its own environment."
    }

    $hostPattern = '^(?=.{1,253}$)[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)+$'
    foreach ($name in @('gatewayHost', 'accountsHost')) {
        $values[$name] = $values[$name].Trim().TrimEnd('.').ToLowerInvariant()
        if ($values[$name] -notmatch $hostPattern) {
            throw "Deploy profile '$Path' member '$name' must be a bare host name such as gateway.example.com, with no scheme, port or path."
        }
    }

    $values['supabaseProjectRef'] = $values['supabaseProjectRef'].Trim()
    if ($values['supabaseProjectRef'] -cnotmatch '^[a-z0-9]{1,40}$') {
        throw "Deploy profile '$Path' member 'supabaseProjectRef' must be the project ref alone (lowercase letters and digits), not a URL or key."
    }

    return [pscustomobject]@{
        Environment = $named
        GatewayHost = $values['gatewayHost']
        AccountsHost = $values['accountsHost']
        SupabaseProjectRef = $values['supabaseProjectRef']
        Note = $values['note']
        Path = [System.IO.Path]::GetFullPath($Path)
    }
}

function Read-TemnoProductionTargets {
    [CmdletBinding()]
    param(
        [string]$IdentityPath = (Join-Path $PSScriptRoot '..\..\src\Temno.Core\Environments\EnvironmentIdentity.cs')
    )

    # Read the public source identity so this check also works before building an assembly.
    # Unknown source syntax must not silently make the production boundary disappear.
    $source = [System.IO.File]::ReadAllText($IdentityPath)
    $initializers = [regex]::Matches($source,
        'public\s+static\s+EnvironmentIdentity\s+Production\s*\{\s*get;\s*\}\s*=\s*new\s*\((?<args>[^;]*?)\)\s*;',
        [System.Text.RegularExpressions.RegexOptions]::Singleline)
    if ($initializers.Count -ne 1) {
        throw 'Cannot read the production target identity from EnvironmentIdentity.cs; refusing the deploy profile.'
    }

    $targets = @{}
    foreach ($name in @('SupabaseProjectRef', 'GatewayHost', 'AccountsHost')) {
        $values = [regex]::Matches($initializers[0].Groups['args'].Value,
            ('\b' + $name + '\s*:\s*"(?<value>[^"\\]*)"\s*(?:,|$)'))
        if ($values.Count -ne 1) {
            throw "Cannot read production $name as a single literal; refusing the deploy profile."
        }
        $targets[$name] = $values[0].Groups['value'].Value.Trim().TrimEnd('.').ToLowerInvariant()
    }

    return [pscustomobject]$targets
}

function Assert-TemnoDevelopmentProfileTargets {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [psobject]$Profile,

        [Parameter(Mandatory)]
        [psobject]$ProductionTargets
    )

    if ($Profile.Environment -cne 'development') { return }
    $productionHosts = @($ProductionTargets.GatewayHost, $ProductionTargets.AccountsHost) |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    foreach ($hostName in @($Profile.GatewayHost, $Profile.AccountsHost)) {
        if ($productionHosts -contains $hostName) {
            throw 'The development deploy profile names a production host from EnvironmentIdentity.Production; refusing the target.'
        }
    }
    if (-not [string]::IsNullOrWhiteSpace($ProductionTargets.SupabaseProjectRef) -and
        $Profile.SupabaseProjectRef -ceq $ProductionTargets.SupabaseProjectRef) {
        throw 'The development deploy profile names the production Supabase project from EnvironmentIdentity.Production; refusing the target.'
    }
}

function Get-TemnoDeployProfile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidateSet('development', 'production')]
        [string]$Environment,

        # Tests point this at a temporary directory; scripts leave it empty.
        [string]$ProfileRoot
    )

    $Environment = $Environment.ToLowerInvariant()
    if ([string]::IsNullOrWhiteSpace($ProfileRoot)) {
        $ProfileRoot = Get-TemnoDeployProfileRoot
    }

    $root = [System.IO.Path]::GetFullPath($ProfileRoot)
    $deployProfile = Read-TemnoDeployProfileFile -Path (Join-Path $root "$Environment.json") -Environment $Environment
    if ($Environment -ceq 'development') {
        Assert-TemnoDevelopmentProfileTargets -Profile $deployProfile -ProductionTargets (Read-TemnoProductionTargets)
    }

    $otherEnvironment = Get-TemnoOtherEnvironment -Environment $Environment
    $otherPath = Join-Path $root "$otherEnvironment.json"
    if (Test-Path -LiteralPath $otherPath -PathType Leaf) {
        try {
            $otherProfile = Read-TemnoDeployProfileFile -Path $otherPath -Environment $otherEnvironment
        }
        catch {
            throw "Cannot confirm the $Environment and $otherEnvironment profiles are disjoint, because the $otherEnvironment profile is refused: $($_.Exception.Message)"
        }

        $otherHosts = @($otherProfile.GatewayHost, $otherProfile.AccountsHost)
        $shared = @(@($deployProfile.GatewayHost, $deployProfile.AccountsHost) | Where-Object { $otherHosts -contains $_ } | Sort-Object -Unique)
        if ($deployProfile.SupabaseProjectRef -ceq $otherProfile.SupabaseProjectRef) {
            $shared += "Supabase project $($deployProfile.SupabaseProjectRef)"
        }

        if ($shared.Count -gt 0) {
            throw "The $Environment and $otherEnvironment profiles share $($shared -join ', '). Each environment has its own hosts and project (docs/contracts/environments.md)."
        }
    }

    return $deployProfile
}

function Assert-TemnoMetadataReaderAvailable {
    [CmdletBinding()]
    param()

    if ($PSVersionTable.PSEdition -ne 'Core') {
        throw 'Reading the environment compiled into Temno.Core.dll needs PowerShell 7 (pwsh), which ships System.Reflection.Metadata. Windows PowerShell 5.1 cannot check an MSIX; run this script with pwsh.'
    }
}

function Read-TemnoSerializedStrings {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [byte[]]$Blob,

        [Parameter(Mandatory)]
        [int]$Count,

        [Parameter(Mandatory)]
        [string]$Source
    )

    # ECMA-335 II.23.3: prolog 0x0001, then each fixed string argument as a SerString.
    $malformed = "$Source has a malformed assembly attribute."
    if ($Blob.Length -lt 2 -or $Blob[0] -ne 1 -or $Blob[1] -ne 0) { throw $malformed }

    $offset = 2
    $values = New-Object 'System.Collections.Generic.List[object]'
    for ($index = 0; $index -lt $Count; $index++) {
        if ($offset -ge $Blob.Length) { throw $malformed }
        $first = [int]$Blob[$offset]
        if ($first -eq 0xFF) {
            $values.Add($null)
            $offset++
            continue
        }

        if (($first -band 0x80) -eq 0) {
            $length = $first
            $offset += 1
        }
        elseif (($first -band 0xC0) -eq 0x80) {
            if ($offset + 1 -ge $Blob.Length) { throw $malformed }
            $length = (($first -band 0x3F) -shl 8) -bor [int]$Blob[$offset + 1]
            $offset += 2
        }
        elseif (($first -band 0xE0) -eq 0xC0) {
            if ($offset + 3 -ge $Blob.Length) { throw $malformed }
            $length = (($first -band 0x1F) -shl 24) -bor ([int]$Blob[$offset + 1] -shl 16) -bor ([int]$Blob[$offset + 2] -shl 8) -bor [int]$Blob[$offset + 3]
            $offset += 4
        }
        else {
            throw $malformed
        }

        if ($offset + $length -gt $Blob.Length) { throw $malformed }
        $values.Add([System.Text.Encoding]::UTF8.GetString($Blob, $offset, $length))
        $offset += $length
    }

    return , $values.ToArray()
}

function Get-TemnoAttributeTypeName {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [System.Reflection.Metadata.MetadataReader]$Reader,

        [Parameter(Mandatory)]
        [System.Reflection.Metadata.EntityHandle]$Constructor
    )

    $kinds = [System.Reflection.Metadata.HandleKind]
    if ($Constructor.Kind -eq $kinds::MemberReference) {
        $member = $Reader.GetMemberReference([System.Reflection.Metadata.MemberReferenceHandle]$Constructor)
        $parent = $member.Parent
        if ($parent.Kind -eq $kinds::TypeReference) {
            $type = $Reader.GetTypeReference([System.Reflection.Metadata.TypeReferenceHandle]$parent)
            return $Reader.GetString($type.Namespace) + '.' + $Reader.GetString($type.Name)
        }

        if ($parent.Kind -eq $kinds::TypeDefinition) {
            $type = $Reader.GetTypeDefinition([System.Reflection.Metadata.TypeDefinitionHandle]$parent)
            return $Reader.GetString($type.Namespace) + '.' + $Reader.GetString($type.Name)
        }

        return ''
    }

    if ($Constructor.Kind -eq $kinds::MethodDefinition) {
        $method = $Reader.GetMethodDefinition([System.Reflection.Metadata.MethodDefinitionHandle]$Constructor)
        $type = $Reader.GetTypeDefinition($method.GetDeclaringType())
        return $Reader.GetString($type.Namespace) + '.' + $Reader.GetString($type.Name)
    }

    return ''
}

function Get-TemnoAssemblyMetadataValues {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [byte[]]$Bytes,

        [Parameter(Mandatory)]
        [string]$Key,

        [Parameter(Mandatory)]
        [string]$Source
    )

    Assert-TemnoMetadataReaderAvailable
    $found = New-Object 'System.Collections.Generic.List[object]'
    $stream = New-Object System.IO.MemoryStream (, $Bytes)
    try {
        $peReader = New-Object System.Reflection.PortableExecutable.PEReader ([System.IO.Stream]$stream)
        try {
            try {
                if (-not $peReader.HasMetadata) { throw "$Source is not a .NET assembly." }
                $reader = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($peReader)
                if (-not $reader.IsAssembly) { throw "$Source is not a .NET assembly." }
                foreach ($handle in $reader.GetAssemblyDefinition().GetCustomAttributes()) {
                    $attribute = $reader.GetCustomAttribute($handle)
                    if ((Get-TemnoAttributeTypeName -Reader $reader -Constructor $attribute.Constructor) -cne 'System.Reflection.AssemblyMetadataAttribute') {
                        continue
                    }

                    $arguments = Read-TemnoSerializedStrings -Blob $reader.GetBlobBytes($attribute.Value) -Count 2 -Source $Source
                    if ($arguments[0] -ceq $Key) {
                        $found.Add($arguments[1])
                    }
                }
            }
            catch [System.BadImageFormatException] {
                throw "$Source is not a readable .NET assembly: $($_.Exception.Message)"
            }
        }
        finally {
            $peReader.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }

    return , $found.ToArray()
}

function Get-TemnoStampEnvironmentByTryParse {
    [CmdletBinding()]
    param(
        [AllowNull()]
        [AllowEmptyString()]
        [string]$Text
    )

    # ArtifactStamp.TryParse step for step: System.Text.Json with default options (no comments,
    # no trailing commas), an object root, a string member named exactly "environment", and one of
    # the two names. $null when TryParse returns false. Needs pwsh: Windows PowerShell 5.1 has no
    # System.Text.Json.
    if ([string]::IsNullOrWhiteSpace($Text)) { return $null }
    try {
        $document = [System.Text.Json.JsonDocument]::Parse($Text)
    }
    catch {
        return $null
    }

    try {
        $root = $document.RootElement
        if ($root.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) { return $null }
        $value = New-Object System.Text.Json.JsonElement
        if (-not $root.TryGetProperty('environment', [ref]$value)) { return $null }
        if ($value.ValueKind -ne [System.Text.Json.JsonValueKind]::String) { return $null }
        $name = $value.GetString()
        if ((Get-TemnoEnvironmentNames) -ccontains $name) { return $name }
        return $null
    }
    finally {
        $document.Dispose()
    }
}

function Read-TemnoArtifactStampText {
    [CmdletBinding()]
    param(
        [AllowNull()]
        [AllowEmptyString()]
        [string]$Text,

        [Parameter(Mandatory)]
        [string]$Source
    )

    # $Text is the file as File.ReadAllText returns it, which is how both services read it, so the
    # file's byte-order mark is already gone. It must be the stamp exactly as the publish target
    # writes it, with only JSON whitespace around it. Every such text is one ArtifactStamp.TryParse
    # accepts, so nothing passes here that a service refuses: comments, trailing commas, single
    # quotes, bare names, other casing, a non-object root and a second byte-order mark are refused.
    # This is stricter than TryParse, which also takes a repeated member (the last wins) or other
    # spacing; no build writes those. Under pwsh the text must pass TryParse's own parse as well.
    # Windows PowerShell 5.1 has no System.Text.Json, so there the exact match decides alone, and
    # it gives the same answer.
    $unreadable = "$Source is unreadable; it must be {`"environment`":`"development`"} or {`"environment`":`"production`"}."
    $environment = $null
    if (-not [string]::IsNullOrEmpty($Text)) {
        $match = [regex]::Match($Text, '\A[ \t\r\n]*\{"environment":"(development|production)"\}[ \t\r\n]*\z')
        if ($match.Success) { $environment = $match.Groups[1].Value }
    }

    if ($null -ne $environment -and $PSVersionTable.PSEdition -eq 'Core' -and (Get-TemnoStampEnvironmentByTryParse -Text $Text) -cne $environment) {
        $environment = $null
    }

    if ($null -eq $environment) {
        return [pscustomobject]@{ Environment = $null; Missing = $false; Problem = $unreadable }
    }

    return [pscustomobject]@{ Environment = $environment; Missing = $false; Problem = $null }
}

function Read-TemnoAssemblyEnvironment {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [byte[]]$Bytes,

        [Parameter(Mandatory)]
        [string]$Source
    )

    # The function returns its array whole (the unary comma), so it is not wrapped in @() here.
    $values = Get-TemnoAssemblyMetadataValues -Bytes $Bytes -Key 'TemnoEnvironment' -Source $Source
    if ($values.Count -eq 0) {
        return [pscustomobject]@{
            Environment = $null
            Missing = $true
            Problem = "$Source carries no AssemblyMetadata(`"TemnoEnvironment`"). It was built before WP-260 or outside this repository's build; rebuild it with -Environment."
        }
    }

    $distinct = @($values | ForEach-Object { "$_" } | Sort-Object -Unique)
    if ($distinct.Count -ne 1 -or (Get-TemnoEnvironmentNames) -cnotcontains $values[0]) {
        return [pscustomobject]@{
            Environment = $null
            Missing = $false
            Problem = "$Source has an unreadable TemnoEnvironment ($($distinct -join ', ')); it must be exactly development or production."
        }
    }

    return [pscustomobject]@{ Environment = [string]$values[0]; Missing = $false; Problem = $null }
}

function Read-TemnoZipEntryBytes {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [System.IO.Compression.ZipArchiveEntry]$Entry
    )

    $memory = New-Object System.IO.MemoryStream
    $stream = $Entry.Open()
    try {
        $stream.CopyTo($memory)
    }
    finally {
        $stream.Dispose()
    }

    return , $memory.ToArray()
}

function Get-TemnoZipEntryExtractedName {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$FullName
    )

    # Where an extractor may write an entry, relative to the archive root: "\" separates like "/",
    # empty and "." segments vanish, ".." climbs, a colon starts an NTFS stream name (so the file
    # is the part before it), and Windows drops a name's trailing dots and spaces. $null when the
    # entry climbs out of the root.
    $segments = New-Object 'System.Collections.Generic.List[string]'
    foreach ($segment in $FullName.Replace('\', '/').Split('/')) {
        if ($segment -eq '..') {
            if ($segments.Count -eq 0) { return $null }
            $segments.RemoveAt($segments.Count - 1)
            continue
        }

        $trimmed = $segment.Split(':')[0].TrimEnd([char[]]@('.', ' '))
        if ($trimmed.Length -gt 0) { $segments.Add($trimmed) }
    }

    return ($segments -join '/')
}

function Read-TemnoArtifactStamp {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $stampName = 'temno-environment.json'
    $noStamp = 'It was built before WP-260 or without -Environment; rebuild it with -Environment.'
    if (-not (Test-Path -LiteralPath $Path)) {
        return [pscustomobject]@{ Environment = $null; Missing = $false; Problem = "Artifact '$Path' does not exist." }
    }

    $fullPath = (Resolve-Path -LiteralPath $Path).ProviderPath
    if (Test-Path -LiteralPath $fullPath -PathType Container) {
        $stampPath = Join-Path $fullPath $stampName
        if (-not (Test-Path -LiteralPath $stampPath -PathType Leaf)) {
            return [pscustomobject]@{ Environment = $null; Missing = $true; Problem = "'$fullPath' carries no $stampName. $noStamp" }
        }

        return Read-TemnoArtifactStampText -Text ([System.IO.File]::ReadAllText($stampPath)) -Source $stampPath
    }

    $extension = [System.IO.Path]::GetExtension($fullPath).ToLowerInvariant()
    if ($extension -eq '.dll') {
        return Read-TemnoAssemblyEnvironment -Bytes ([System.IO.File]::ReadAllBytes($fullPath)) -Source $fullPath
    }

    if ($extension -ne '.zip' -and $extension -ne '.msix') {
        return [pscustomobject]@{ Environment = $null; Missing = $false; Problem = "Artifact '$fullPath' is not a .zip, .msix, .dll or directory." }
    }

    if ($extension -eq '.msix') {
        # Checked before the archive is opened, so 5.1 gets the clear message.
        Assert-TemnoMetadataReaderAvailable
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    try {
        $archive = [System.IO.Compression.ZipFile]::OpenRead($fullPath)
    }
    catch {
        return [pscustomobject]@{ Environment = $null; Missing = $false; Problem = "Artifact '$fullPath' is not a readable archive: $($_.Exception.Message)" }
    }

    try {
        $wanted = if ($extension -eq '.msix') { 'Temno.Core.dll' } else { $stampName }
        # Every entry an extractor could write to the root's $wanted counts, however it is spelled,
        # so a second spelling cannot replace the one read here.
        $entries = @($archive.Entries | Where-Object { (Get-TemnoZipEntryExtractedName -FullName $_.FullName) -ieq $wanted })
        if ($entries.Count -eq 0) {
            $what = if ($extension -eq '.msix') { "no Temno.Core.dll at its root, so its environment cannot be read" } else { "no $stampName at its root. $noStamp" }
            return [pscustomobject]@{ Environment = $null; Missing = ($extension -ne '.msix'); Problem = "'$fullPath' carries $what" }
        }

        if ($entries.Count -gt 1) {
            $names = @($entries | ForEach-Object { "'$($_.FullName)'" }) -join ', '
            return [pscustomobject]@{ Environment = $null; Missing = $false; Problem = "'$fullPath' carries $wanted more than once ($names). Extraction keeps one of them, so which one the service reads is unknown." }
        }

        $entry = $entries[0]
        if ($entry.FullName -ine $wanted) {
            return [pscustomobject]@{ Environment = $null; Missing = $false; Problem = "'$fullPath' carries $wanted as '$($entry.FullName)'. It must sit at the archive root under exactly that name; extractors disagree about where that spelling lands." }
        }

        if ($extension -eq '.msix') {
            return Read-TemnoAssemblyEnvironment -Bytes (Read-TemnoZipEntryBytes -Entry $entry) -Source "$fullPath!Temno.Core.dll"
        }

        if ($entry.Length -gt 4096) {
            return [pscustomobject]@{ Environment = $null; Missing = $false; Problem = "'$fullPath!$stampName' is too large to be a stamp." }
        }

        $reader = New-Object System.IO.StreamReader ($entry.Open(), [System.Text.Encoding]::UTF8, $true)
        try {
            $text = $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }

        return Read-TemnoArtifactStampText -Text $text -Source "$fullPath!$stampName"
    }
    finally {
        $archive.Dispose()
    }
}

function Get-TemnoArtifactEnvironment {
    [CmdletBinding()]
    param(
        # A service ZIP or publish directory (its temno-environment.json), an MSIX or a
        # Temno.Core.dll (the compiled AssemblyMetadata).
        [Parameter(Mandatory)]
        [string]$Path
    )

    $stamp = Read-TemnoArtifactStamp -Path $Path
    if ($null -ne $stamp.Problem) { throw $stamp.Problem }
    return $stamp.Environment
}

function Assert-TemnoArtifactEnvironment {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [ValidateSet('development', 'production')]
        [string]$Environment,

        [string]$Label = 'Artifact',

        # The input of a repackaging script: an unstamped input predates WP-260.
        [switch]$Repackage
    )

    $Environment = $Environment.ToLowerInvariant()
    $stamp = Read-TemnoArtifactStamp -Path $Path
    if ($stamp.Missing -and $Repackage) {
        throw "$Label '$Path' is unstamped. A package from before WP-260 carries no temno-environment.json, so it cannot be repackaged for $Environment. Publish a fresh verified package with -p:TemnoEnvironment=$Environment and repackage that."
    }

    if ($null -ne $stamp.Problem) {
        throw "$Label refused: $($stamp.Problem)"
    }

    if ($stamp.Environment -cne $Environment) {
        throw "$Label '$Path' was built for $($stamp.Environment), and this run is -Environment $Environment. Use the $Environment artifact, or rebuild with -Environment $Environment."
    }

    return $stamp.Environment
}

function Test-TemnoArtifactRootFile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Name
    )

    $fullPath = (Resolve-Path -LiteralPath $Path).ProviderPath
    if (Test-Path -LiteralPath $fullPath -PathType Container) {
        return Test-Path -LiteralPath (Join-Path $fullPath $Name) -PathType Leaf
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($fullPath)
    try {
        return @($archive.Entries | Where-Object { $_.FullName.Replace('\', '/') -ieq $Name }).Count -gt 0
    }
    finally {
        $archive.Dispose()
    }
}

function Get-TemnoDeployTarget {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [psobject]$DeployProfile,

        [Parameter(Mandatory)]
        [ValidateSet('Gateway', 'Billing', 'App')]
        [string]$Service
    )

    switch ($Service) {
        'Gateway' { return [pscustomobject]@{ Host = $DeployProfile.GatewayHost; Description = $DeployProfile.GatewayHost } }
        'Billing' { return [pscustomobject]@{ Host = $DeployProfile.AccountsHost; Description = $DeployProfile.AccountsHost } }
        default {
            return [pscustomobject]@{
                Host = $DeployProfile.GatewayHost
                Description = "the $($DeployProfile.Environment) backend: gateway $($DeployProfile.GatewayHost), accounts $($DeployProfile.AccountsHost), Supabase project $($DeployProfile.SupabaseProjectRef)"
            }
        }
    }
}

function Invoke-TemnoArtifactVerification {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [ValidateSet('development', 'production')]
        [string]$Environment,

        [Parameter(Mandatory)]
        [ValidateSet('Gateway', 'Billing', 'App')]
        [string]$Service,

        [string]$ProfileRoot
    )

    # Every refusal happens before the first line is written, so a wrong artifact never
    # prints a target someone could upload to.
    $Environment = $Environment.ToLowerInvariant()
    $built = Assert-TemnoArtifactEnvironment -Path $Path -Environment $Environment
    $fullPath = (Resolve-Path -LiteralPath $Path).ProviderPath
    $isDirectory = Test-Path -LiteralPath $fullPath -PathType Container
    $extension = [System.IO.Path]::GetExtension($fullPath).ToLowerInvariant()
    if ($Service -eq 'App' -and ($isDirectory -or $extension -ne '.msix')) {
        throw "Artifact '$fullPath' is not an .msix package."
    }

    if ($Service -ne 'App' -and -not $isDirectory -and $extension -ne '.zip') {
        throw "Artifact '$fullPath' is not a $Service package ZIP or publish directory."
    }

    $entryPoint = switch ($Service) { 'Gateway' { 'Temno.Gateway.dll' } 'Billing' { 'Temno.Billing.dll' } default { $null } }
    if ($null -ne $entryPoint -and -not (Test-TemnoArtifactRootFile -Path $fullPath -Name $entryPoint)) {
        throw "Artifact '$fullPath' has no $entryPoint at its root, so it is not a $Service package."
    }

    $deployProfile = Get-TemnoDeployProfile -Environment $Environment -ProfileRoot $ProfileRoot
    $target = Get-TemnoDeployTarget -DeployProfile $deployProfile -Service $Service
    $sha256 = if (Test-Path -LiteralPath $fullPath -PathType Leaf) { (Get-FileHash -Algorithm SHA256 -LiteralPath $fullPath).Hash } else { $null }

    Write-Host "Artifact       $fullPath"
    if ($null -ne $sha256) { Write-Host "SHA-256        $sha256" }
    Write-Host "Environment    $built (artifact and profile agree)"
    Write-Host "Profile        $($deployProfile.Path)"
    Write-Host "Target         $($target.Description)"
    Write-Host 'Verified only. Nothing was built or uploaded.'

    return [pscustomobject]@{
        Artifact = $fullPath
        Sha256 = $sha256
        Environment = $built
        Service = $Service
        TargetHost = $target.Host
        Target = $target.Description
        Profile = $deployProfile.Path
    }
}
