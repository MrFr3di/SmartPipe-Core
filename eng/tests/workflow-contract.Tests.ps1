param()

$ErrorActionPreference = 'Stop'
$testScript = Join-Path $PSScriptRoot 'workflow_contract_tests.py'

python $testScript
if ($LASTEXITCODE -ne 0) {
    throw "Workflow contract tests failed with exit code $LASTEXITCODE."
}
