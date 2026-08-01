[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$requiredCapabilities = $env:LISTENARR_REQUIRED_NATIVE_TEST_CAPABILITIES
if ([string]::IsNullOrWhiteSpace($requiredCapabilities)) {
    throw 'LISTENARR_REQUIRED_NATIVE_TEST_CAPABILITIES must declare the native capabilities required by this CI job.'
}

Write-Host "Required native test capabilities: $requiredCapabilities"
Write-Host "Runner OS: $env:RUNNER_OS"
Write-Host "Runner architecture: $env:RUNNER_ARCH"
Write-Host "Runner image: $env:ImageOS $env:ImageVersion"

$preflightFilter = 'FullyQualifiedName=Listenarr.Tests.Features.Architecture.NativeTestCapabilityContractTests.RequiredNativeTestCapabilities_AreAvailable'
& dotnet test tests/Listenarr.Tests.csproj `
    -c Release `
    --no-build `
    --filter $preflightFilter `
    --logger 'console;verbosity=normal'
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

& dotnet test listenarr.slnx `
    -c Release `
    --no-build `
    --logger 'console;verbosity=normal'
exit $LASTEXITCODE
