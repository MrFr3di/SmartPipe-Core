using SmartPipe.RepositoryChecks.Infrastructure;

namespace SmartPipe.RepositoryChecks.Tests.Repository;

internal sealed class RepositoryTestDirectory : IDisposable
{
    public RepositoryTestDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SmartPipe.RepositoryChecks.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Write(string relativePath, string contents)
    {
        var path = System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    public string WriteBytes(string relativePath, byte[] contents)
    {
        var path = System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, contents);
        return path;
    }

    public bool TryCreateFileLink(string relativeLinkPath, string relativeTargetPath)
    {
        var linkPath = System.IO.Path.Combine(Path, relativeLinkPath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        var targetPath = System.IO.Path.Combine(Path, relativeTargetPath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(linkPath)!);
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
            return new FileInfo(linkPath).LinkTarget is not null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    public bool TryCreateDirectoryLink(string relativeLinkPath, string relativeTargetPath)
    {
        var linkPath = System.IO.Path.Combine(Path, relativeLinkPath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        var targetPath = System.IO.Path.Combine(Path, relativeTargetPath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(linkPath)!);
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return new DirectoryInfo(linkPath).LinkTarget is not null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    public void Dispose() => Directory.Delete(Path, recursive: true);
}

internal sealed class FakeProcessRunner : IProcessRunner
{
    private readonly Queue<object> _responses;

    public FakeProcessRunner(params object[] responses) => _responses = new Queue<object>(responses);

    public List<ProcessRequest> Requests { get; } = [];

    public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        var response = _responses.Dequeue();
        if (response is Exception exception)
        {
            throw exception;
        }

        return Task.FromResult((ProcessResult)response);
    }
}
