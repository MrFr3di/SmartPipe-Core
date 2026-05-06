#nullable enable
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using SmartPipe.Core;

namespace SmartPipe.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(iterationCount: 10, warmupCount: 1)]
public class SecretScannerReDoSBenchmarks
{
    // Malicious inputs for each pattern (same as in ReDoSTests)
    private readonly string _apiKeyMalicious = "api-key = '" + new string('x', 10000);
    private readonly string _passwordMalicious = "password = \"" + new string('p', 10000);
    private readonly string _openAiKeyMalicious = "sk-" + new string('a', 31) + "!";
    private readonly string _privateKeyMalicious = "-----BEGIN" + new string('-', 1000);
    private readonly string _jwtMalicious = "eyJ" + new string('x', 1000) + ".eyJ" + new string('y', 1000);
    private readonly string _awsAccessKeyMalicious = "AKIA" + new string('A', 15) + "!";
    private readonly string _gitHubPatMalicious = "ghp_" + new string('g', 35) + "!";
    private readonly string _googleOAuthMalicious = "ya29." + new string('z', 10000) + "!";

    [Benchmark]
    public bool APIKeyPattern_Benchmark()
    {
        return SecretScanner.HasSecrets(_apiKeyMalicious);
    }

    [Benchmark]
    public bool PasswordPattern_Benchmark()
    {
        return SecretScanner.HasSecrets(_passwordMalicious);
    }

    [Benchmark]
    public bool OpenAIKeyPattern_Benchmark()
    {
        return SecretScanner.HasSecrets(_openAiKeyMalicious);
    }

    [Benchmark]
    public bool PrivateKeyPattern_Benchmark()
    {
        return SecretScanner.HasSecrets(_privateKeyMalicious);
    }

    [Benchmark]
    public bool JWTPattern_Benchmark()
    {
        return SecretScanner.HasSecrets(_jwtMalicious);
    }

    [Benchmark]
    public bool AWSAccessKeyPattern_Benchmark()
    {
        return SecretScanner.HasSecrets(_awsAccessKeyMalicious);
    }

    [Benchmark]
    public bool GitHubPATPattern_Benchmark()
    {
        return SecretScanner.HasSecrets(_gitHubPatMalicious);
    }

    [Benchmark]
    public bool GoogleOAuthPattern_Benchmark()
    {
        return SecretScanner.HasSecrets(_googleOAuthMalicious);
    }
}
